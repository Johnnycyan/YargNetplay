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
        public const string Version = "0.2.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<int> Port;
        internal static ConfigEntry<string> LastJoinAddress;
        internal static ConfigEntry<int> RemoteDelayMs;

        private void Awake()
        {
            Log = Logger;

            Port = Config.Bind("Network", "Port", Protocol.DefaultPort, "TCP port used when hosting.");
            LastJoinAddress = Config.Bind("Network", "LastJoinAddress", "", "Address last used to join (prefilled in the Join dialog).");
            RemoteDelayMs = Config.Bind("Gameplay", "RemoteDelayMs", 200,
                "How far behind the song the other player's highway runs, in ms. Must be above your ping.");

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
