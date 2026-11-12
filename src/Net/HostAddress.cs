using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace YargNetplay.Net
{
    /// <summary>The addresses a joiner can reach this host on.</summary>
    internal static class HostAddress
    {
        /// <summary>This network's public IPv4, as seen from the internet (a plain-text lookup service).</summary>
        public static Task<string> GetPublicIpAsync() => Task.Run(() =>
        {
            var request = (HttpWebRequest) WebRequest.Create("https://api.ipify.org");
            request.Timeout = request.ReadWriteTimeout = 5000;
            using var response = request.GetResponse();
            using var reader = new StreamReader(response.GetResponseStream());
            string text = reader.ReadToEnd().Trim();
            if (!IPAddress.TryParse(text, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                throw new IOException($"Unexpected reply \"{text}\"");
            return text;
        });

        /// <summary>
        /// The IPv4 address of the adapter that reaches the internet (it has a default gateway), so VPN, virtual and
        /// unplugged adapters are skipped. Null when there's none.
        /// </summary>
        public static string GetLanIp() =>
            NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .Select(n => n.GetIPProperties())
                .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                    && !g.Address.Equals(IPAddress.Any)))
                .SelectMany(p => p.UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                ?.ToString();
    }
}
