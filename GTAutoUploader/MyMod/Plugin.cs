using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace MyMod
{
    // AutoUploader keeps this component ENABLED only inside Utilla modded lobbies.
    // Set up in OnEnable, tear down in OnDisable/OnDestroy.
    [BepInPlugin("com.yourname.mymod", "MyMod", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        Harmony harmony;

        void OnEnable()
        {
            harmony = new Harmony("com.yourname.mymod");
            harmony.PatchAll();
            Logger.LogInfo("MyMod enabled (modded lobby).");
        }

        void OnDisable()
        {
            harmony?.UnpatchSelf();
            harmony = null;
            Logger.LogInfo("MyMod disabled.");
        }

        void Update()
        {
            // your per-frame mod logic here
        }
    }
}
