using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;

namespace YargNetplay.Net
{
    /// <summary>
    /// A UDP side channel to the peer, used during songs for inputs. Over TCP, one lost packet holds back every input
    /// sent after it until it's resent (often 200 ms or more on a bad connection). UDP packets each carry the latest
    /// inputs again, so a lost one costs nothing. TCP still carries every input too, so a connection where UDP doesn't
    /// get through (or drops a long burst) still works, just with TCP's delays.
    ///
    /// Each side tells the other over TCP which port it listens on and a random token (<see cref="MsgType.UdpInfo"/>).
    /// Packets must carry the receiver's token. The host listens on the hosting port; the joining player's packets reach
    /// it through the host's port forward (or the UPnP one), and the host replies to the address they came from, which
    /// gets through the joiner's NAT. Both sides send probes until packets arrive, so it usually works without any
    /// forward too (UDP hole punching).
    ///
    /// Packet: [uint magic][ulong receiver's token][byte kind][payload]. Receiving runs on a background thread; everything
    /// else is main-thread only.
    /// </summary>
    internal sealed class UdpChannel : IDisposable
    {
        public const byte KindProbe = 0;

        /// <summary>double songKey, int lastSeq, double watermark, byte count, count x (double time, int action, int value).</summary>
        public const byte KindInputs = 1;

        private const uint Magic = 0x55504E59; // "YNPU"
        private const int HeaderBytes = 4 + 8 + 1;
        private const double AliveSeconds = 3;
        // Windows reports an ICMP "port unreachable" as an error on the next Receive unless this is turned off.
        private const int SioUdpConnReset = -1744830452;

        private readonly UdpClient _udp;
        private readonly Thread _thread;
        private readonly ConcurrentQueue<(byte[] data, IPEndPoint from)> _inbox = new();
        private volatile bool _closed;

        private IPEndPoint _peer;
        private ulong _peerToken;
        private double _lastReceived = double.NegativeInfinity;
        private bool _sendFailed;

        /// <summary>Packets sent to us must carry this. Sent to the peer over TCP.</summary>
        public ulong LocalToken { get; }
        public int LocalPort { get; }

        /// <summary>Whether packets from the peer arrived in the last few seconds.</summary>
        public bool Alive => Session.LocalNow - _lastReceived < AliveSeconds;

        public bool HasPeer => _peer != null;

        private UdpChannel(UdpClient udp)
        {
            _udp = udp;
            LocalPort = ((IPEndPoint) udp.Client.LocalEndPoint).Port;
            var bytes = new byte[8];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            LocalToken = BitConverter.ToUInt64(bytes, 0);

            try { udp.Client.IOControl(SioUdpConnReset, new byte[] { 0 }, null); }
            catch { /* not Windows */ }

            _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "YargNetplay.Udp" };
            _thread.Start();
        }

        /// <summary>Opens a UDP socket on <paramref name="port"/> (0 = any free port). Null if it can't; inputs then use TCP only.</summary>
        public static UdpChannel Open(int port)
        {
            try
            {
                return new UdpChannel(new UdpClient(new IPEndPoint(IPAddress.Any, port)));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't open UDP port {port}, inputs will use TCP only: {e.Message}");
                return null;
            }
        }

        /// <summary>Where to send until the peer's own packets show its real address (its NAT may change the port).</summary>
        public void SetPeer(IPAddress address, int port, ulong token)
        {
            _peerToken = token;
            _peer ??= new IPEndPoint(address, port);
        }

        public void Send(byte kind, Action<BinaryWriter> writeBody = null)
        {
            if (_closed || _peer == null) return;

            byte[] packet;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Magic);
                w.Write(_peerToken);
                w.Write(kind);
                writeBody?.Invoke(w);
                w.Flush();
                packet = ms.ToArray();
            }

            try { _udp.Send(packet, packet.Length, _peer); }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                if (!_sendFailed) Plugin.Log.LogWarning($"UDP send to {_peer} failed: {e.Message}");
                _sendFailed = true;
            }
        }

        /// <summary>The next valid packet from the peer: its kind, and a reader positioned at its payload.</summary>
        public bool TryReceive(out byte kind, out BinaryReader payload)
        {
            while (_inbox.TryDequeue(out var packet))
            {
                var data = packet.data;
                if (data.Length < HeaderBytes || BitConverter.ToUInt32(data, 0) != Magic
                    || BitConverter.ToUInt64(data, 4) != LocalToken)
                    continue;

                // Answer wherever the peer's packets really come from.
                if (!packet.from.Equals(_peer))
                {
                    Plugin.Log.LogInfo($"UDP: peer is at {packet.from}");
                    _peer = packet.from;
                }
                _lastReceived = Session.LocalNow;
                kind = data[12];
                payload = new BinaryReader(new MemoryStream(data, HeaderBytes, data.Length - HeaderBytes));
                return true;
            }

            kind = 0;
            payload = null;
            return false;
        }

        private void ReceiveLoop()
        {
            while (!_closed)
            {
                try
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    var data = _udp.Receive(ref from);
                    _inbox.Enqueue((data, from));
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException)
                {
                    if (_closed) break;
                    Thread.Sleep(10); // don't spin if the socket keeps failing
                }
            }
        }

        public void Dispose()
        {
            _closed = true;
            try { _udp.Close(); } catch { /* ignore */ }
        }
    }
}
