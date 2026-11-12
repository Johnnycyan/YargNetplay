using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using YargNetplay.Net;

namespace YargNetplay
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.johnnycyan.yargnetplay";
        public const string Name = "YARG Netplay";
        public const string Version = BuildInfo.Version; // from <Version> in the csproj (CI sets it from the tag)

        internal static ManualLogSource Log;

        internal static ConfigEntry<int> Port;
        internal static ConfigEntry<string> LastJoinAddress;
        internal static ConfigEntry<int> RemoteDelayMs;
        internal static ConfigEntry<bool> AutoDelay;
        internal static ConfigEntry<string> ReceivedSongsFolder;
        internal static ConfigEntry<bool> SetlistCollapsed;
        internal static ConfigEntry<string> PreferredInstrument;
        internal static ConfigEntry<string> PreferredDifficulty;
        internal static ConfigEntry<bool> CheckForUpdates;

        private void Awake()
        {
            Log = Logger;

            Port = Config.Bind("Network", "Port", Protocol.DefaultPort, "TCP port used when hosting.");
            LastJoinAddress = Config.Bind("Network", "LastJoinAddress", "", "Address last used to join (prefilled in the Join dialog).");
            RemoteDelayMs = Config.Bind("Gameplay", "RemoteDelayMs", 200,
                "How far behind the song the other player's highway runs, in ms, when AutoDelay is off. Must be above your ping.");
            AutoDelay = Config.Bind("Gameplay", "AutoDelay", true,
                "Pick the remote player delay from the measured ping at the start of each song.");
            ReceivedSongsFolder = Config.Bind("Songs", "ReceivedSongsFolder", "",
                "Folder for songs received from other players. Empty uses NetplaySongs in YARG's data folder. " +
                "Change it from the Online page so existing songs are moved along.");
            SetlistCollapsed = Config.Bind("UI", "SetlistCollapsed", false,
                "Whether the shared setlist overlay in the music library is collapsed to its header.");
            PreferredInstrument = Config.Bind("Gameplay", "PreferredInstrument", "Any",
                "Instrument you plan to play online (YARG Instrument name, or Any). The music library only shows " +
                "songs that have charts for both players' parts.");
            PreferredDifficulty = Config.Bind("Gameplay", "PreferredDifficulty", "Expert",
                "Difficulty for PreferredInstrument (Easy, Medium, Hard or Expert).");
            CheckForUpdates = Config.Bind("Updates", "CheckForUpdates", true,
                "Check GitHub for a newer YARG Netplay release at launch and show a notification if there is one.");

            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);

            // BepInEx's own manager object can be destroyed by some games on scene load,
            // so the mod lives on its own persistent object.
            var host = new GameObject("YargNetplay");
            DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<NetplayManager>();

            Log.LogInfo($"{Name} {Version} loaded. Use Online in the main menu (F8 opens the debug overlay).");
        }
    }
}
