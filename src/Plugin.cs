using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace YargNetplay
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.johnnycyan.yargnetplay";
        public const string Name = "YARG Netplay";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;

            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);

            // BepInEx's own manager object can be destroyed by some games on scene load,
            // so the mod lives on its own persistent object.
            var host = new GameObject("YargNetplay");
            DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<NetplayManager>();

            Log.LogInfo($"{Name} {Version} loaded. Press F8 to open the netplay window.");
        }
    }
}
