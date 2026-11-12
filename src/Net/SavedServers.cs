using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Newtonsoft.Json;

namespace YargNetplay.Net
{
    internal sealed class SavedServer
    {
        public string Name;
        public string Address;
    }

    /// <summary>Servers the player has joined or added, saved to BepInEx/config/yargnetplay-servers.json.</summary>
    internal static class SavedServers
    {
        private static List<SavedServer> _servers;

        private static string FilePath => Path.Combine(Paths.ConfigPath, "yargnetplay-servers.json");

        public static IReadOnlyList<SavedServer> All => _servers ??= Load();

        private static List<SavedServer> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonConvert.DeserializeObject<List<SavedServer>>(File.ReadAllText(FilePath)) ?? new();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not read saved servers: {e.Message}");
            }
            return new();
        }

        private static void Save()
        {
            try { File.WriteAllText(FilePath, JsonConvert.SerializeObject(_servers, Formatting.Indented)); }
            catch (Exception e) { Plugin.Log.LogWarning($"Could not save servers: {e.Message}"); }
        }

        private static SavedServer Find(string address) =>
            All.FirstOrDefault(s => string.Equals(s.Address, address, StringComparison.OrdinalIgnoreCase));

        public static SavedServer Add(string address, string name = null)
        {
            address = address.Trim();
            var existing = Find(address);
            if (existing != null) return existing;

            var server = new SavedServer { Address = address, Name = string.IsNullOrWhiteSpace(name) ? address : name.Trim() };
            _servers.Add(server);
            Save();
            return server;
        }

        /// <summary>After a successful join: save the address, named after the host unless the player already named it.</summary>
        public static void RememberJoin(string address, string hostName)
        {
            if (string.IsNullOrWhiteSpace(address)) return;
            var server = Find(address) ?? Add(address);
            if (server.Name == server.Address && !string.IsNullOrWhiteSpace(hostName))
            {
                server.Name = hostName;
                Save();
            }
        }

        public static void Rename(SavedServer server, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            server.Name = name.Trim();
            Save();
        }

        public static void Remove(SavedServer server)
        {
            if (_servers.Remove(server)) Save();
        }

        /// <summary>"host", "host:port" or "[ipv6]:port".</summary>
        public static bool TryParse(string text, int defaultPort, out string host, out int port)
        {
            text = (text ?? "").Trim();
            host = text;
            port = defaultPort;
            if (text.Length == 0) return false;

            if (text.StartsWith("["))
            {
                int end = text.IndexOf(']');
                if (end < 0) return false;
                host = text.Substring(1, end - 1);
                return end + 1 >= text.Length || (text[end + 1] == ':' && int.TryParse(text.Substring(end + 2), out port));
            }

            int colon = text.LastIndexOf(':');
            if (colon > 0 && text.IndexOf(':') == colon)
            {
                host = text.Substring(0, colon);
                return int.TryParse(text.Substring(colon + 1), out port) && port is > 0 and < 65536;
            }
            return true;
        }
    }
}
