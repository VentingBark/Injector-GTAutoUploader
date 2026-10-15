GT AUTOUPLOADER  (PC / Steam, BepInEx 5 x64)

REQUIRES: .NET SDK (or Visual Studio), BepInEx 5 x64 + Utilla in your Gorilla Tag folder (run the game once after installing BepInEx).

SETUP
1. Unzip anywhere (e.g. C:\Dev\GTAutoUploader).
2. Double-click build.bat  (or: build.bat "D:\Games\Gorilla Tag" if not in the default Steam path).
   -> AutoUploader.dll goes to  Gorilla Tag\BepInEx\plugins
   -> MyMod.dll goes to         Gorilla Tag\BepInEx\autouploader
3. Launch the game, join a MODDED (Utilla) lobby. MyMod activates.

DAILY USE
- Edit MyMod\Plugin.cs, then run in the MyMod folder:  dotnet watch build /p:GTDir="<game path>"
  Every save rebuilds, copies to BepInEx\autouploader, and the game hot-loads it in ~1s.
- Any finished BepInEx plugin DLL dropped into BepInEx\autouploader loads the same way.
- To use your own project: add this before </Project> in its .csproj:
    <Import Project="C:\Dev\GTAutoUploader\AutoUploader\AutoUpload.targets" />

BEHAVIOR
- Mods are enabled only in modded lobbies. Injecting while in a vanilla lobby disconnects you.
- Config: BepInEx\config\com.yourname.autouploader.cfg (ExtraFolders, ActiveOutsideRooms, KickFromVanillaLobby, LoadOnStartup).
- Errors: BepInEx\LogOutput.log

NOTES
- AutoUploader itself isn't hot-reloaded; restart the game after rebuilding it.
- Use OnEnable/OnDisable/OnDestroy to clean up patches, or reloads stack.
- Don't also put your mod in BepInEx\plugins or \scripts (double load).
