using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace YargNetplay.Net
{
    /// <summary>
    /// Asks the router to forward the hosting port to this PC with UPnP (Internet Gateway Device), so the host doesn't
    /// have to set up port forwarding by hand. Opened while waiting for a player and removed once they're connected
    /// (the established connection doesn't need it) or hosting stops. When the router has UPnP turned off, or there's
    /// none, the port just stays closed and the Online page says so.
    /// </summary>
    internal sealed class PortMapping
    {
        public enum MappingState { Opening, Open, Failed }

        private const string Description = "YARG Netplay";
        // A limited lease, renewed while hosting, so a mapping left behind by a crash expires on its own.
        private const int LeaseSeconds = 3600;
        private const int DiscoveryMs = 3000;
        private const int HttpTimeoutMs = 5000;
        private const int ErrorOnlyPermanentLeases = 725;
        private const int ErrorConflict = 718;

        private static readonly IPEndPoint SsdpEndPoint = new(IPAddress.Parse("239.255.255.250"), 1900);
        private static readonly string[] SearchTargets =
        {
            "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
            "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
        };
        private static readonly string[] ServiceTypes =
        {
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANIPConnection:2",
            "urn:schemas-upnp-org:service:WANPPPConnection:1",
        };

        // Mapping threads still running, so quitting can give their removal a moment to reach the router.
        private static readonly List<Thread> Threads = new();

        public int Port { get; }
        public MappingState State { get; private set; } = MappingState.Opening;

        /// <summary>Why the port couldn't be opened (set when <see cref="State"/> is Failed).</summary>
        public string Error { get; private set; }

        private readonly object _lock = new();
        private bool _closed;

        private PortMapping(int port) => Port = port;

        /// <summary>Starts opening <paramref name="port"/> (TCP) on the router in the background.</summary>
        public static PortMapping Open(int port)
        {
            var mapping = new PortMapping(port);
            var thread = new Thread(mapping.Run) { IsBackground = true, Name = "YargNetplay.PortMapping" };
            lock (Threads) Threads.Add(thread);
            thread.Start();
            return mapping;
        }

        /// <summary>Removes the mapping (in the background). Safe to call more than once, or while it's still opening.</summary>
        public void Close()
        {
            lock (_lock)
            {
                _closed = true;
                Monitor.PulseAll(_lock);
            }
        }

        /// <summary>On quit: waits (briefly) for closed mappings to be removed from the router.</summary>
        public static void WaitForRemoval(int timeoutMs)
        {
            Thread[] threads;
            lock (Threads) threads = Threads.ToArray();
            var clock = Stopwatch.StartNew();
            foreach (var thread in threads)
                thread.Join(Math.Max(0, timeoutMs - (int) clock.ElapsedMilliseconds));
        }

        private void Run()
        {
            try { OpenUntilClosed(); }
            finally { lock (Threads) Threads.Remove(Thread.CurrentThread); }
        }

        private void OpenUntilClosed()
        {
            Gateway gateway;
            try
            {
                gateway = FindGateway();
                int lease = AddMapping(gateway);
                State = MappingState.Open;
                Plugin.Log.LogInfo($"UPnP: opened port {Port} (TCP) to {gateway.LocalIp} on {gateway.ControlUrl}" +
                                   (lease == 0 ? " (permanent lease)" : ""));

                while (true)
                {
                    lock (_lock)
                    {
                        if (!_closed) Monitor.Wait(_lock, lease == 0 ? Timeout.Infinite : lease * 1000 / 2);
                        if (_closed) break;
                    }
                    try { lease = AddMapping(gateway); }
                    catch (Exception e) { Plugin.Log.LogWarning($"UPnP: couldn't renew port {Port}: {e.Message}"); }
                }
            }
            catch (Exception e)
            {
                Error = e.Message;
                State = MappingState.Failed;
                Plugin.Log.LogWarning($"UPnP: couldn't open port {Port}: {e.Message}");
                return;
            }

            try
            {
                Soap(gateway, "DeletePortMapping",
                    $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{Port}</NewExternalPort><NewProtocol>TCP</NewProtocol>");
                Plugin.Log.LogInfo($"UPnP: closed port {Port}.");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"UPnP: couldn't close port {Port}: {e.Message}"); }
        }

        /// <summary>Adds (or refreshes) the mapping. Returns the lease it got: 0 when the router only allows permanent ones.</summary>
        private int AddMapping(Gateway gateway)
        {
            string Args(int lease) =>
                $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{Port}</NewExternalPort><NewProtocol>TCP</NewProtocol>" +
                $"<NewInternalPort>{Port}</NewInternalPort><NewInternalClient>{gateway.LocalIp}</NewInternalClient>" +
                $"<NewEnabled>1</NewEnabled><NewPortMappingDescription>{Description}</NewPortMappingDescription>" +
                $"<NewLeaseDuration>{lease}</NewLeaseDuration>";
            try
            {
                Soap(gateway, "AddPortMapping", Args(LeaseSeconds));
                return LeaseSeconds;
            }
            catch (UpnpException e) when (e.Code == ErrorOnlyPermanentLeases)
            {
                Soap(gateway, "AddPortMapping", Args(0));
                return 0;
            }
            catch (UpnpException e) when (e.Code == ErrorConflict)
            {
                throw new IOException($"the router already forwards port {Port} to another device");
            }
        }

        // ------------------------------------------------------------ discovery

        private sealed class Gateway
        {
            public string ControlUrl, ServiceType;
            public IPAddress LocalIp; // this PC's address on the router's network: where the port is forwarded to
        }

        /// <summary>Finds the router with SSDP (a multicast search on the local network), then its port mapping service.</summary>
        private static Gateway FindGateway()
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            // Multicast goes out of one adapter only, which can be a virtual one (Hyper-V, WSL, VPN), so search from each.
            foreach (var local in LocalAddresses())
            {
                try
                {
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                        BitConverter.ToInt32(local.GetAddressBytes(), 0));
                    foreach (string target in SearchTargets)
                    {
                        byte[] search = Encoding.ASCII.GetBytes(
                            "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n" +
                            $"ST: {target}\r\n\r\n");
                        // Twice, as UDP can drop one.
                        udp.Send(search, search.Length, SsdpEndPoint);
                        udp.Send(search, search.Length, SsdpEndPoint);
                    }
                }
                catch (SocketException e) { Plugin.Log.LogInfo($"UPnP: couldn't search from {local}: {e.Message}"); }
            }

            var tried = new HashSet<string>();
            string lastError = null;
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < DiscoveryMs)
            {
                udp.Client.ReceiveTimeout = Math.Max(1, DiscoveryMs - (int) clock.ElapsedMilliseconds);
                byte[] reply;
                try
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    reply = udp.Receive(ref from);
                }
                catch (SocketException) { break; } // timed out

                var location = Regex.Match(Encoding.ASCII.GetString(reply), @"^LOCATION:\s*(\S+)",
                    RegexOptions.IgnoreCase | RegexOptions.Multiline).Groups[1].Value;
                if (location.Length == 0 || !tried.Add(location)) continue;

                try
                {
                    var gateway = ReadDescription(location);
                    if (gateway != null) return gateway;
                }
                catch (Exception e) { lastError = e.Message; }
            }

            throw new IOException(lastError != null
                ? $"the router's UPnP didn't respond properly ({lastError})"
                : "no router with UPnP was found (it may be turned off in the router's settings)");
        }

        /// <summary>This PC's IPv4 addresses on adapters that are up.</summary>
        private static IEnumerable<IPAddress> LocalAddresses() =>
            NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

        /// <summary>Reads a device description and returns its port mapping service, or null when it has none.</summary>
        private static Gateway ReadDescription(string location)
        {
            var xml = new XmlDocument { XmlResolver = null };
            xml.LoadXml(HttpGet(location));

            // Matched by local name, as some routers get the namespaces wrong.
            string baseUrl = xml.SelectSingleNode("//*[local-name()='URLBase']")?.InnerText.Trim();
            var root = new Uri(string.IsNullOrEmpty(baseUrl) ? location : baseUrl);
            foreach (string type in ServiceTypes)
            {
                var control = xml.SelectSingleNode(
                    $"//*[local-name()='service'][*[local-name()='serviceType']='{type}']/*[local-name()='controlURL']");
                if (control == null) continue;

                var controlUrl = new Uri(root, control.InnerText.Trim());
                return new Gateway
                {
                    ControlUrl = controlUrl.ToString(),
                    ServiceType = type,
                    LocalIp = LocalAddressFor(controlUrl.Host),
                };
            }
            return null;
        }

        /// <summary>The address this PC uses to reach <paramref name="host"/> (a UDP "connect" sends nothing).</summary>
        private static IPAddress LocalAddressFor(string host)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(host, 1900);
            return ((IPEndPoint) socket.LocalEndPoint).Address;
        }

        // ------------------------------------------------------------ HTTP / SOAP

        private static string HttpGet(string url)
        {
            var request = (HttpWebRequest) WebRequest.Create(url);
            request.Proxy = null; // the router is on the local network
            request.Timeout = request.ReadWriteTimeout = HttpTimeoutMs;
            using var response = request.GetResponse();
            using var reader = new StreamReader(response.GetResponseStream());
            return reader.ReadToEnd();
        }

        private static void Soap(Gateway gateway, string action, string args)
        {
            byte[] body = Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                $"<s:Body><u:{action} xmlns:u=\"{gateway.ServiceType}\">{args}</u:{action}></s:Body></s:Envelope>");

            var request = (HttpWebRequest) WebRequest.Create(gateway.ControlUrl);
            request.Proxy = null;
            request.Method = "POST";
            request.ContentType = "text/xml; charset=\"utf-8\"";
            request.Headers.Add("SOAPAction", $"\"{gateway.ServiceType}#{action}\"");
            request.Timeout = request.ReadWriteTimeout = HttpTimeoutMs;
            request.ContentLength = body.Length;
            using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);

            try
            {
                using var response = request.GetResponse();
            }
            catch (WebException e) when (e.Response is HttpWebResponse response)
            {
                // UPnP errors come back as HTTP 500 with a code and description in the SOAP fault.
                string text;
                using (response)
                using (var reader = new StreamReader(response.GetResponseStream())) text = reader.ReadToEnd();
                var code = Regex.Match(text, @"<errorCode>\s*(\d+)\s*</errorCode>");
                var description = Regex.Match(text, @"<errorDescription>\s*([^<]*?)\s*</errorDescription>");
                if (!code.Success) throw;
                throw new UpnpException(int.Parse(code.Groups[1].Value),
                    description.Success ? description.Groups[1].Value : e.Message);
            }
        }

        private sealed class UpnpException : IOException
        {
            public int Code { get; }

            public UpnpException(int code, string description)
                : base($"the router refused ({code} {description})") => Code = code;
        }
    }
}
