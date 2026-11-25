using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace YargMidiFix
{
    /// <summary>
    /// Moves Minis' MIDI hotplug check off the main thread.
    /// Minis (YARG's MIDI input library) asks RtMidi for the MIDI port count on every input update so it can notice
    /// devices being plugged in. On Windows 11 that call (midiInGetNumDevs) walks the device tree and registry each
    /// time, and profiling showed it taking about a third of the main thread's frame time.
    /// A background thread now polls the count once a second with its own RtMidi handle, and Minis' update only runs
    /// when that count differs from the one Minis last saw. Its port rebuild still happens on the main thread, since
    /// it adds and removes Input System devices. MIDI notes are read on Minis' own per-port threads and aren't affected.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.johnnycyan.yargmidifix";
        public const string Name = "YARG MIDI Fix";
        public const string Version = BuildInfo.Version;

        private const int PollIntervalMs = 1000;

        private static ManualLogSource _log;

        // Minis.MidiBackend and its last seen port count. Looked up by name: the plugin doesn't reference Minis.
        private static Type _backendType;
        private static AccessTools.FieldRef<object, uint> _lastPortCount;

        // -1 until the first poll finishes, or after a failed one: Minis' own check runs in the meantime.
        private static volatile int _portCount = -1;
        private static Thread _thread;

        private void Awake()
        {
            _log = Logger;
            _backendType = AccessTools.TypeByName("Minis.MidiBackend");
            var onUpdate = _backendType == null ? null : AccessTools.Method(_backendType, "OnUpdate");
            if (onUpdate == null || AccessTools.Field(_backendType, "_lastPortCount") == null)
            {
                _log.LogWarning("Minis.MidiBackend isn't what this plugin expects (YARG changed it?), so it does nothing.");
                return;
            }
            _lastPortCount = AccessTools.FieldRefAccess<uint>(_backendType, "_lastPortCount");
            new Harmony(Guid).Patch(onUpdate, prefix: new HarmonyMethod(typeof(Plugin), nameof(OnUpdatePrefix)));
        }

        private static bool OnUpdatePrefix(object __instance)
        {
            EnsureStarted();
            int seen = _portCount;
            return seen < 0 || (uint) seen != _lastPortCount(__instance);
        }

        private static void EnsureStarted()
        {
            if (_thread != null) return;
            _thread = new Thread(Poll) { IsBackground = true, Name = "YARG MIDI port watch" };
            _thread.Start();
        }

        private static void Poll()
        {
            IntPtr handle;
            try
            {
                handle = rtmidi_in_create_default();
            }
            catch (Exception e)
            {
                _log.LogWarning($"MIDI port watch couldn't start, Minis keeps checking every frame: {e.Message}");
                return;
            }
            if (handle == IntPtr.Zero) return;

            // The handle lives as long as the process: this is a background thread that only ends when the game quits.
            while (true)
            {
                // A throw here would end the thread with the last count still set, and Minis would never see a
                // device plugged in again. Fall back to Minis' own check instead.
                try
                {
                    uint count = rtmidi_get_port_count(handle);
                    _portCount = IsOk(handle) ? (int) count : -1;
                }
                catch (Exception e)
                {
                    _portCount = -1;
                    _log.LogWarning($"MIDI port watch stopped, Minis checks every frame again: {e.Message}");
                    return;
                }
                Thread.Sleep(PollIntervalMs);
            }
        }

        // RtMidi's C API returns a struct { void* ptr; void* data; bool ok; const char* msg; }.
        private static bool IsOk(IntPtr handle) => Marshal.ReadByte(handle, 2 * IntPtr.Size) != 0;

        // Same library and name Minis binds; Mono maps it to libRtMidi on Linux and macOS.
        [DllImport("RtMidi.dll")] private static extern IntPtr rtmidi_in_create_default();
        [DllImport("RtMidi.dll")] private static extern uint rtmidi_get_port_count(IntPtr device);
    }
}
