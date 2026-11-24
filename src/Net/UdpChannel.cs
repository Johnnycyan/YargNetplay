using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;

namespace YargNetplay.Net
{
    /// <summary>
    /// A UDP side channel, used during songs for inputs. Over TCP, one lost packet holds back every input sent after
    /// it until it's resent (often 200 ms or more on a bad connection). UDP packets each carry the latest inputs again,
    /// so a lost one costs nothing. TCP still carries every input too, so a connection where UDP doesn't get through
    /// (or drops a long burst) still works, just with TCP's delays.
    ///
    /// Like TCP, UDP runs between each player and the host. A player and the host tell each other over TCP which port
    /// they listen on and a random token (<see cref="MsgType.UdpInfo"/>); the host makes a different token for each
    /// player, so a packet's token says who sent it. Packets must carry the receiver's token. The host listens on the
    /// hosting port; players' packets reach it through the host's port forward (or the UPnP one), and the host replies to
    /// the address they came from, which gets through the player's NAT. Both sides send probes until packets arrive, so
    /// it usually works without any forward too (UDP hole punching).
    ///
    /// The host passes every input packet on to the other players straight from the receive thread, so relaying doesn't
    /// wait for a game frame.
    ///
    /// Packet: [uint magic][ulong receiver's token][byte kind][payload]. Receiving runs on a background thread; the
    /// rest may be called from the main thread.
    /// </summary>
    internal sealed class UdpChannel : IDisposable
    {
        public const byte KindProbe = 0;

        /// <summary>byte player, double songKey, int lastSeq, double watermark, byte count, count x (double time, int action, int value).</summary>
        public const byte KindInputs = 1;

        private const uint Magic = 0x55504E59; // "YNPU"
        private const int HeaderBytes = 4 + 8 + 1;
        private const double AliveSeconds = 3;
        // Windows reports an ICMP "port unreachable" as an error on the next Receive unless this is turned off.
        private const int SioUdpConnReset = -1744830452;

        private sealed class Peer
        {
            public byte Id;
            public ulong LocalToken;  // what this peer puts in packets to us
            public ulong RemoteToken; // what we put in packets to them
            public IPEndPoint EndPoint;
            public double LastReceived = double.NegativeInfinity;
        }

        private readonly UdpClient _udp;
        private readonly Thread _thread;
        private readonly bool _relay;
        private readonly object _lock = new();
        private readonly Dictionary<ulong, Peer> _byToken = new();
        private readonly Dictionary<byte, Peer> _byId = new();
        private readonly ConcurrentQueue<(byte peer, byte[] data)> _inbox = new();
        private volatile bool _closed;
        private bool _sendFailed;

        public int LocalPort { get; }

        private UdpChannel(UdpClient udp, bool relay)
        {
            _udp = udp;
            _relay = relay;
            LocalPort = ((IPEndPoint) udp.Client.LocalEndPoint).Port;

            try { udp.Client.IOControl(SioUdpConnReset, new byte[] { 0 }, null); }
            catch { /* not Windows */ }

            _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "YargNetplay.Udp" };
            _thread.Start();
        }

        /// <summary>
        /// Opens a UDP socket on <paramref name="port"/> (0 = any free port). With <paramref name="relay"/> (the host),
        /// input packets from one peer are passed on to all others. Null if it can't; inputs then use TCP only.
        /// </summary>
        public static UdpChannel Open(int port, bool relay)
        {
            try
            {
                return new UdpChannel(new UdpClient(new IPEndPoint(IPAddress.Any, port)), relay);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Couldn't open UDP port {port}, inputs will use TCP only: {e.Message}");
                return null;
            }
        }

        /// <summary>Starts accepting packets from player <paramref name="id"/>. Returns the token they must put in them (sent to them over TCP).</summary>
        public ulong AddPeer(byte id)
        {
            var bytes = new byte[8];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var peer = new Peer { Id = id, LocalToken = BitConverter.ToUInt64(bytes, 0) };
            lock (_lock)
            {
                RemovePeerLocked(id);
                _byId[id] = peer;
                _byToken[peer.LocalToken] = peer;
            }
            return peer.LocalToken;
        }

        public void RemovePeer(byte id)
        {
            lock (_lock) RemovePeerLocked(id);
        }

        private void RemovePeerLocked(byte id)
        {
            if (!_byId.TryGetValue(id, out var old)) return;
            _byId.Remove(id);
            _byToken.Remove(old.LocalToken);
        }

        /// <summary>Where to send to a peer until its own packets show its real address (its NAT may change the port).</summary>
        public void SetPeerAddress(byte id, IPAddress address, int port, ulong token)
        {
            lock (_lock)
            {
                if (!_byId.TryGetValue(id, out var peer)) return;
                peer.RemoteToken = token;
                peer.EndPoint ??= new IPEndPoint(address, port);
            }
        }

        /// <summary>Whether packets from this peer arrived in the last few seconds.</summary>
        public bool Alive(byte id)
        {
            lock (_lock) return _byId.TryGetValue(id, out var peer) && Session.LocalNow - peer.LastReceived < AliveSeconds;
        }

        public bool HasAddress(byte id)
        {
            lock (_lock) return _byId.TryGetValue(id, out var peer) && peer.EndPoint != null;
        }

        public void Send(byte id, byte kind, Action<BinaryWriter> writeBody = null)
        {
            if (_closed) return;
            IPEndPoint endPoint;
            ulong token;
            lock (_lock)
            {
                if (!_byId.TryGetValue(id, out var peer) || peer.EndPoint == null) return;
                endPoint = peer.EndPoint;
                token = peer.RemoteToken;
            }

            byte[] packet;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Magic);
                w.Write(token);
                w.Write(kind);
                writeBody?.Invoke(w);
                w.Flush();
                packet = ms.ToArray();
            }
            SendPacket(packet, endPoint);
        }

        /// <summary>Sends to every peer that has an address.</summary>
        public void SendAll(byte kind, Action<BinaryWriter> writeBody = null)
        {
            List<byte> ids;
            lock (_lock) ids = new List<byte>(_byId.Keys);
            foreach (var id in ids) Send(id, kind, writeBody);
        }

        private void SendPacket(byte[] packet, IPEndPoint endPoint)
        {
            try { _udp.Send(packet, packet.Length, endPoint); }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                if (!_sendFailed) Plugin.Log.LogWarning($"UDP send to {endPoint} failed: {e.Message}");
                _sendFailed = true;
            }
        }

        /// <summary>The next valid packet: the peer it came from, its kind, and a reader positioned at its payload.</summary>
        public bool TryReceive(out byte peer, out byte kind, out BinaryReader payload)
        {
            if (_inbox.TryDequeue(out var packet))
            {
                peer = packet.peer;
                kind = packet.data[12];
                payload = new BinaryReader(new MemoryStream(packet.data, HeaderBytes, packet.data.Length - HeaderBytes));
                return true;
            }

            peer = kind = 0;
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
                    OnPacket(data, from);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException)
                {
                    if (_closed) break;
                    Thread.Sleep(10); // don't spin if the socket keeps failing
                }
            }
        }

        private void OnPacket(byte[] data, IPEndPoint from)
        {
            if (data.Length < HeaderBytes || BitConverter.ToUInt32(data, 0) != Magic) return;

            List<(byte[] packet, IPEndPoint to)> relay = null;
            byte id;
            lock (_lock)
            {
                if (!_byToken.TryGetValue(BitConverter.ToUInt64(data, 4), out var peer)) return;
                id = peer.Id;

                // Answer wherever the peer's packets really come from.
                if (!from.Equals(peer.EndPoint))
                {
                    Plugin.Log.LogInfo($"UDP: player {id} is at {from}");
                    peer.EndPoint = from;
                }
                peer.LastReceived = Session.LocalNow;

                // Pass a player's own inputs on to everyone else, each with their token.
                if (_relay && data[12] == KindInputs && data.Length > HeaderBytes && data[HeaderBytes] == id)
                {
                    foreach (var other in _byId.Values)
                    {
                        if (other == peer || other.EndPoint == null) continue;
                        var copy = (byte[]) data.Clone();
                        BitConverter.GetBytes(other.RemoteToken).CopyTo(copy, 4);
                        (relay ??= new()).Add((copy, other.EndPoint));
                    }
                }
            }

            if (relay != null)
                foreach (var (packet, to) in relay) SendPacket(packet, to);
            _inbox.Enqueue((id, data));
        }

        public void Dispose()
        {
            _closed = true;
            try { _udp.Close(); } catch { /* ignore */ }
        }
    }
}
