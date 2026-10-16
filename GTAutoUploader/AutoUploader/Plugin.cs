using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace AutoUploader
{
    /// Watches BepInEx\autouploader (+ optional extra folders). Any plugin DLL that appears/changes
    /// (Visual Studio, Rider, dotnet build...) is loaded into the running game, replacing the old copy.
    [BepInPlugin("com.yourname.autouploader", "AutoUploader", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        // never treat dependency DLLs as mods
        static readonly string[] Ignore =
            { "0Harmony", "BepInEx", "Mono.", "System.", "UnityEngine", "Assembly-CSharp", "Photon", "Newtonsoft", "AutoUploader" };

        class Loaded { public GameObject Go; public List<string> Guids = new List<string>(); }

        ConfigEntry<bool> loadOnStartup, activeOutsideRooms;
        ConfigEntry<string> extraFolders;

        readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        readonly ConcurrentDictionary<string, DateTime> pending = new ConcurrentDictionary<string, DateTime>(); // path -> last event (UTC)
        readonly Dictionary<string, Loaded> loaded = new Dictionary<string, Loaded>();
        readonly HashSet<string> busy = new HashSet<string>();
        bool ready, wasInRoom;
        float nextTick;

        void Awake()
        {
            loadOnStartup      = Config.Bind("General", "LoadOnStartup", true, "Load DLLs already in the folder when the game starts");
            extraFolders       = Config.Bind("General", "ExtraFolders", "", "Semicolon-separated extra folders to watch, e.g. your project's bin\\Debug\\net472");
            activeOutsideRooms = Config.Bind("Lobby", "ActiveOutsideRooms", true, "Keep uploaded mods enabled while not in any room");

            var dirs = new List<string> { Path.Combine(Paths.BepInExRootPath, "autouploader") };
            dirs.AddRange(extraFolders.Value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
            foreach (string d in dirs) Watch(d);
        }

        IEnumerator Start()
        {
            while (GorillaTagger.Instance == null || NetworkSystem.Instance == null) yield return null;
            ready = true;
            wasInRoom = NetworkSystem.Instance.InRoom;
        }

        // ---------- file watching (watcher threads only enqueue; Unity API stays on main thread) ----------
        void Watch(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var w = new FileSystemWatcher(dir, "*.dll")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                w.Created += (s, e) => Queue(e.FullPath);
                w.Changed += (s, e) => Queue(e.FullPath);
                w.Renamed += (s, e) => Queue(e.FullPath);
                watchers.Add(w);

                if (loadOnStartup.Value)
                    foreach (string f in Directory.GetFiles(dir, "*.dll")) Queue(f);

                Logger.LogInfo("Watching " + dir);
            }
            catch (Exception e) { Logger.LogError("Watch failed for " + dir + ": " + e.Message); }
        }

        static bool IsIgnored(string path)
        {
            string n = Path.GetFileNameWithoutExtension(path);
            return Ignore.Any(i => n.StartsWith(i, StringComparison.OrdinalIgnoreCase));
        }

        void Queue(string path) { if (!IsIgnored(path)) pending[path] = DateTime.UtcNow; }

        void Drop(string path) { DateTime t; pending.TryRemove(path, out t); }

        static byte[] TryRead(string path)
        {
            try { byte[] b = File.ReadAllBytes(path); return b.Length > 0 ? b : null; } // reading bytes = no file lock, VS can rebuild
            catch (IOException) { return null; }                                         // still being written
            catch (UnauthorizedAccessException) { return null; }
        }

        void Update()
        {
            if (!ready || Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + 0.25f;

            foreach (var kv in pending.ToArray())
            {
                double age = (DateTime.UtcNow - kv.Value).TotalMilliseconds;
                if (age < 700 || busy.Contains(kv.Key)) continue;      // debounce: wait for build to finish writing

                byte[] bytes = TryRead(kv.Key);
                if (bytes == null) { if (age > 30000) Drop(kv.Key); continue; }

                Drop(kv.Key);
                busy.Add(kv.Key);
                StartCoroutine(Inject(kv.Key, bytes));
            }

            bool inRoom = NetworkSystem.Instance.InRoom;               // join/leave edge -> re-validate
            if (inRoom != wasInRoom) { wasInRoom = inRoom; Guard(); }
        }

        // ---------- inject / unload ----------
        IEnumerator Inject(string path, byte[] bytes)
        {
            string name = Path.GetFileName(path);
            Unload(path);
            yield return null; yield return null;                       // let old OnDestroy finish before new Awake

            Assembly asm = null;
            try { asm = Assembly.Load(bytes); }
            catch (Exception e) { Logger.LogError(name + " failed to load: " + e.Message); }

            if (asm != null)
            {
                var types = GetTypes(asm).Where(t => typeof(BaseUnityPlugin).IsAssignableFrom(t) && !t.IsAbstract).ToList();
                if (types.Count == 0) Logger.LogWarning(name + ": no BaseUnityPlugin found, skipped");
                else
                {
                    var go = new GameObject("AutoUploader:" + name);
                    go.hideFlags = HideFlags.HideAndDontSave;
                    go.SetActive(false);                                // Awake is deferred until Apply() enables it
                    var entry = new Loaded { Go = go };

                    foreach (Type t in types)
                    {
                        try
                        {
                            BepInPlugin meta = MetadataHelper.GetMetadata(t);
                            if (meta == null) continue;
                            PluginInfo info = MakeInfo(meta, t, path);   // BaseUnityPlugin ctor looks this up (Info.Metadata...)
                            Chainloader.PluginInfos[meta.GUID] = info;
                            entry.Guids.Add(meta.GUID);
                            Component c = go.AddComponent(t);
                            SetProp(info, "Instance", c);
                        }
                        catch (Exception e) { Logger.LogError(name + " / " + t.Name + ": " + e); }
                    }

                    loaded[path] = entry;
                    Logger.LogInfo("Injected " + name + " (" + entry.Guids.Count + " plugin(s))");
                    Guard();                                            // lobby check on EVERY injection
                }
            }
            busy.Remove(path);
        }

        void Unload(string path)
        {
            Loaded l;
            if (!loaded.TryGetValue(path, out l)) return;
            loaded.Remove(path);
            foreach (string g in l.Guids)
            {
                try { new Harmony(g).UnpatchSelf(); } catch { }          // fallback if mod's Harmony id == its GUID
                Chainloader.PluginInfos.Remove(g);
            }
            if (l.Go) Destroy(l.Go);                                    // runs the mod's OnDestroy
        }

        // ---------- lobby guard ----------
        static bool InModdedLobby()
        {
            var ns = NetworkSystem.Instance;
            return ns.InRoom && (ns.GameModeString ?? "").ToUpperInvariant().Contains("MODDED"); // Utilla modded gamemodes
        }

        void Guard()
        {
            var ns = NetworkSystem.Instance;
            if (ns.InRoom)
            {
                Logger.LogWarning("Not a modded lobby - leaving.");
                ns.ReturnToSinglePlayer();                              // older builds: PhotonNetwork.Disconnect();
            }
            Apply();
        }

        void Apply()
        {
            bool on = InModdedLobby() || (!NetworkSystem.Instance.InRoom && activeOutsideRooms.Value);
            foreach (Loaded l in loaded.Values)
                if (l.Go && l.Go.activeSelf != on) l.Go.SetActive(on);  // mods get OnEnable/OnDisable
        }

        // ---------- helpers ----------
        static IEnumerable<Type> GetTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        }

        static PluginInfo MakeInfo(BepInPlugin meta, Type t, string path)
        {
            var info = new PluginInfo();
            SetProp(info, "Metadata", meta);
            SetProp(info, "Location", path);
            SetProp(info, "TypeName", t.FullName);
            SetProp(info, "Dependencies", new BepInDependency[0]);
            SetProp(info, "Incompatibilities", new BepInIncompatibility[0]);
            SetProp(info, "Processes", new BepInProcess[0]);
            return info;
        }

        static void SetProp(object o, string prop, object val)
        {
            try { o.GetType().GetProperty(prop, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(o, val, null); }
            catch { }
        }

        void OnDestroy()
        {
            foreach (var w in watchers) w.Dispose();
            foreach (string p in loaded.Keys.ToList()) Unload(p);
        }
    }
}
//hi