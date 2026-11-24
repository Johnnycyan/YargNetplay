using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine.SceneManagement;
using YARG;
using YARG.Core.Input;
using YARG.Core.Song;
using YargNetplay.Game;
using YargNetplay.UI;

namespace YargNetplay.Net
{
    internal enum SessionState
    {
        Idle,
        Hosting,    // listening, nobody joined yet (or everyone left)
        Connecting,
        Lobby,
        Waiting,    // we confirmed our difficulty and are waiting for the others to be ready for the same song
        Loading,    // everyone ready: gameplay scene loading / waiting at the start gate
        InSong,
    }

    internal sealed class QueueItem
    {
        public byte[] Hash;
        public string Name;
        public string Artist;
        public long LengthMilliseconds;
        public bool LocalHas;
        /// <summary>Each other player's answer, by player id. Missing = waiting for their answer.</summary>
        public readonly Dictionary<byte, bool> PeerHas = new();
        public bool AddedByMe;
        /// <summary>Name of the player who added it, when it wasn't us.</summary>
        public string AddedBy;
        /// <summary>Player id of who added it, when it wasn't us.</summary>
        public byte AddedById;
    }

    internal sealed class PeerSetup
    {
        public byte[] ProfileBytes;
        public string EnginePresetJson;
    }

    /// <summary>Another player in the session, as this game knows them.</summary>
    internal sealed class Peer
    {
        public byte Id;
        public string Name = "a player";
        public string YargVersion;

        public PartChoice Part;
        public bool InSongSelect;
        public bool LobbyReady;

        /// <summary>
        /// Their round trip to the host (0 for the host itself, whose link to us is our own <see cref="Session.RttSeconds"/>).
        /// Measured by the host and shared with everyone in <see cref="MsgType.PlayerStats"/>.
        /// </summary>
        public double RttSeconds, WorstRttSeconds;

        /// <summary>UDP between them and the host didn't get through (host's view, shared in PlayerStats).</summary>
        public bool UdpFailed;

        // Ready handshake and the current song
        public byte[] ReadyHash;
        public PeerSetup ReadySetup;
        public bool Loaded;
        public readonly RemoteInputBuffer Inputs = new();

        // Host only
        public Connection Conn;
        public IPAddress Address;
        public bool Welcomed;
        public double ConnectedAt;
        public readonly List<double> RecentRtts = new();
        public readonly Queue<double> RttHistory = new();
    }

    /// <summary>
    /// One netplay session. Sessions are a star: the host accepts up to <see cref="Protocol.MaxPlayers"/> - 1 players,
    /// each connected to the host only, and passes their messages on to each other (see <see cref="Protocol"/>). The
    /// host's clock is the reference clock.
    /// All public members are main-thread only; network threads only touch <see cref="Connection"/>, the routing table
    /// and <see cref="UdpChannel"/>.
    ///
    /// Song flow: any player opens difficulty select (single song or setlist) and the others follow
    /// (<see cref="MsgType.OpenSetlist"/>). Every load of the gameplay scene is held at
    /// <see cref="TryPassGameplayGate"/> until all players are Ready for the same song, so setlist
    /// continues and score-screen restarts work the same way as the first song.
    /// </summary>
    internal sealed class Session : IDisposable
    {
        public static Session Current { get; private set; }

        private const double PING_INTERVAL = 1.0;
        private const double START_LEAD_TIME = 0.75;
        private const int CLOCK_SAMPLES = 16;
        private const int RTT_HISTORY = 30;
        private const double RTT_WARMUP = 1.0;
        private const double AUTO_DELAY_MARGIN = 0.040;
        private const double AUTO_DELAY_MIN_MS = 50, AUTO_DELAY_MAX_MS = 1000;

        private static readonly double TicksToSeconds = 1.0 / Stopwatch.Frequency;

        private const double UDP_PROBE_INTERVAL = 0.5, UDP_KEEPALIVE_INTERVAL = 1.0;
        private const double UDP_GRACE = 5.0; // how long after connecting UDP may take to get through before the page says it didn't
        private const int UDP_REPEATED_INPUTS = 16;
        private const double UDP_REPEAT_WINDOW = 1.0; // inputs older than this (behind the watermark) aren't repeated
        private const double STREAM_MIN_INTERVAL = 0.005;

        private const byte NoId = 254;

        // Host: the listener, players who connected but haven't said Hello yet, and the routing table the read threads
        // use to pass frames on (welcomed players' connections, by id; replaced whole, never changed in place).
        private TcpListener _listener;
        private int _listenPort;
        private readonly ConcurrentQueue<TcpClient> _accepted = new();
        private readonly List<Peer> _pending = new();
        private volatile Connection[] _routes = new Connection[256];

        // Joining player: the connection to the host.
        private Connection _hostConn;
        private IPAddress _hostAddress;
        private string _rejectReason;

        /// <summary>Inputs and watermarks also go over UDP when this is set (see <see cref="UdpChannel"/>).</summary>
        private UdpChannel _udp;
        private double _nextUdpProbe;
        private bool _udpWasAlive;

        // Our input stream for the current song: numbered inputs, the latest ones (repeated in every UDP packet), and
        // the last watermark sent.
        private int _sendSeq;
        private readonly List<(int seq, GameInput input)> _recentInputs = new();
        private double _lastMarkSent = double.NegativeInfinity;
        private double _nextStreamSend;
        private bool _inputsSinceStream;

        /// <summary>The UPnP forward of the hosting port, while hosting (null otherwise).</summary>
        public PortMapping PortMapping { get; private set; }
        private SongTransfer _transfer;
        private TcpClient _pendingClient; // set by the connect thread, picked up on the main thread
        private string _pendingError;
        private double _nextPing;
        private readonly List<(double rtt, double offset)> _clockSamples = new();
        private readonly Queue<double> _rttHistory = new();
        private double _connectedAt;

        public bool IsHost { get; private set; }

        /// <summary>Our player id: <see cref="Protocol.HostId"/> on the host, given by the host when joining.</summary>
        public byte LocalId { get; private set; } = NoId;

        /// <summary>The address the player typed or picked to join (null when hosting).</summary>
        public string JoinAddress { get; private set; }

        /// <summary>Everyone else in the session, in the order they joined (the host first, for joining players).</summary>
        public List<Peer> Peers { get; } = new();

        /// <summary>Ready toggle on the Online page. The host sends everyone to song select once all are ready.</summary>
        public bool LocalLobbyReady { get; private set; }
        public SessionState State { get; private set; }
        public string Status { get; private set; } = "Not connected";
        public string LocalName { get; set; } = Environment.UserName;

        /// <summary>Our round trip to the host (0 on the host).</summary>
        public double RttSeconds { get; private set; }

        public Peer FindPeer(byte id) => Peers.FirstOrDefault(p => p.Id == id);

        public string NameOf(byte id) => id == LocalId ? LocalName : FindPeer(id)?.Name ?? "a player";

        /// <summary>The other players' names as one phrase ("Alice", "Alice and Bob", "Alice, Bob and Carol").</summary>
        public string OthersText => NameList(Peers.Select(p => p.Name));

        public static string NameList(IEnumerable<string> names)
        {
            var list = names.ToList();
            return list.Count switch
            {
                0 => "the other players",
                1 => list[0],
                _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[list.Count - 1],
            };
        }

        /// <summary>
        /// Delay picked from the network for one remote player: half the worst round trip their inputs take to get here,
        /// plus a margin for frame timing and jitter, rounded up to 10 ms. Inputs travel through the host, so that's our
        /// round trip to the host plus theirs (each the worst of the last <see cref="RTT_HISTORY"/> pings, one second
        /// apart). Pings sent in the first <see cref="RTT_WARMUP"/> seconds of a connection don't count: round trips right
        /// after connecting are often much slower.
        /// </summary>
        public double AutoDelaySeconds(Peer peer)
        {
            double mine = IsHost ? 0 : _rttHistory.Count > 0 ? _rttHistory.Max() : RttSeconds;
            double worstRtt = mine + peer.WorstRttSeconds;
            double ms = Math.Ceiling((worstRtt / 2 + AUTO_DELAY_MARGIN) * 100) * 10;
            return Math.Max(AUTO_DELAY_MIN_MS, Math.Min(AUTO_DELAY_MAX_MS, ms)) / 1000.0;
        }

        /// <summary>
        /// How far behind the shared song clock a remote player starts each song. With Auto delay on,
        /// <see cref="RemoteDriver"/> then adjusts it to the lag it measures.
        /// </summary>
        public double RemoteDelaySeconds(Peer peer) => Plugin.AutoDelay.Value ? AutoDelaySeconds(peer) : Plugin.RemoteDelayMs.Value / 1000.0;

        /// <summary>The longest starting delay of any remote player (shown on the Online page).</summary>
        public double MaxRemoteDelaySeconds => Peers.Count == 0 ? 0 : Peers.Max(RemoteDelaySeconds);

        /// <summary>The shared setlist. Mirrored into YARG's own setlist by <see cref="NativeSetlist"/>.</summary>
        public List<QueueItem> Queue { get; } = new();

        // Ready handshake
        private byte[] _localReadyHash;
        private bool _gateOpen;
        private bool _undoShowAdvanceOnCancel;

        // Current song
        /// <summary>The setups of the players in the current song, by player id (null outside a song).</summary>
        public Dictionary<byte, PeerSetup> ActiveSetups { get; private set; }
        public bool LocalLoaded { get; private set; }
        public double? StartAtHostTime { get; private set; }

        /// <summary>Host clock offset: hostTime = localTime + offset. Always 0 on the host.</summary>
        private double _clockOffset;

        public static double LocalNow => Stopwatch.GetTimestamp() * TicksToSeconds;
        public double HostNow => LocalNow + _clockOffset;

        /// <summary>In a session with at least one other player.</summary>
        public bool IsConnected => IsHost ? Peers.Count > 0 : _hostConn != null && !_hostConn.IsClosed && LocalId != NoId;

        /// <summary>Connected for a while, but no UDP packets got through on the way from this player: their inputs only travel over TCP.</summary>
        public bool UdpFailed(Peer peer)
        {
            if (_udp == null) return IsConnected;
            if (IsHost) return !_udp.Alive(peer.Id) && LocalNow - peer.ConnectedAt > UDP_GRACE;
            bool mine = !_udp.Alive(Protocol.HostId) && LocalNow - _connectedAt > UDP_GRACE;
            return mine || (peer.Id != Protocol.HostId && peer.UdpFailed);
        }

        /// <summary>Round trip between us and this player (through the host).</summary>
        public double PingTo(Peer peer) => IsHost ? peer.RttSeconds : RttSeconds + peer.RttSeconds;

        public SongTransfer Transfer => _transfer;
        public bool InSongFlow => State is SessionState.Loading or SessionState.InSong;

        /// <summary>
        /// A restart or skip is under way: the song being played is over, but its scene stays up (paused) until all
        /// players are ready for the next one.
        /// </summary>
        public bool ChangingSong { get; private set; }

        /// <summary>Set when the next song was agreed while a gameplay scene was up: its unload must not end the new song.</summary>
        private bool _replacingSong;
        public bool ReplacingSong => _replacingSong;

        // ---------------------------------------------------------------- lifecycle

        public static Session Host(int port)
        {
            Current?.Dispose();
            var s = new Session { IsHost = true, LocalId = Protocol.HostId, _listenPort = port };
            Current = s;

            try
            {
                s._listener = new TcpListener(IPAddress.Any, port);
                s._listener.Start(Protocol.MaxPlayers);
            }
            catch (Exception e)
            {
                s.Fail("Could not listen on port " + port + ": " + e.Message);
                return s;
            }

            s.State = SessionState.Hosting;
            s.Status = $"Hosting on port {port}, waiting for players...";
            s.PortMapping = PortMapping.Open(port);
            // UDP on the hosting port (the one forwarded to us); input packets from one player are passed on to the rest.
            s._udp = UdpChannel.Open(port, relay: true);
            s.InitTransfer();

            var listener = s._listener;
            new Thread(() =>
            {
                try
                {
                    while (true) s._accepted.Enqueue(listener.AcceptTcpClient());
                }
                catch (Exception e)
                {
                    if (s._listener == listener) s._pendingError = e.Message;
                }
            }) { IsBackground = true, Name = "YargNetplay.Accept" }.Start();
            return s;
        }

        public static Session Join(string host, int port, string address)
        {
            Current?.Dispose();
            var s = new Session
            {
                IsHost = false,
                State = SessionState.Connecting,
                Status = $"Connecting to {address}...",
                JoinAddress = address,
            };
            Current = s;

            new Thread(() =>
            {
                try
                {
                    var client = new TcpClient();
                    client.Connect(host, port);
                    s._pendingClient = client;
                }
                catch (Exception e) { s._pendingError = e.Message; }
            }) { IsBackground = true, Name = "YargNetplay.Connect" }.Start();
            return s;
        }

        public void Dispose()
        {
            bool wasConnected = IsConnected;
            Shutdown();
            if (Current == this) Current = null;
            if (wasConnected) Notify.Info("Left the online session");
        }

        private void Fail(string message)
        {
            Plugin.Log.LogWarning(message);
            Status = message;
            Shutdown();
            Notify.Error(message);
        }

        private void InitTransfer()
        {
            _transfer = new SongTransfer(SendTo, IsPeerConnected, NameOf);
            _transfer.SongReceived += OnSongReceived;
        }

        /// <summary>Thread-safe (the song sender's worker asks): whether a player is still in the session.</summary>
        private bool IsPeerConnected(byte id) => _present[id] && (IsHost ? _routes[id] is { IsClosed: false } : _hostConn is { IsClosed: false });

        /// <summary>Which player ids are in the session, for other threads (replaced whole, never changed in place).</summary>
        private volatile bool[] _present = new bool[256];

        private void Shutdown()
        {
            var listener = _listener;
            _listener = null;
            try { listener?.Stop(); } catch { /* ignore */ }
            while (_accepted.TryDequeue(out var client)) client.Close();

            // The port forward stays up while hosting: players' UDP packets come in through it.
            PortMapping?.Close();
            PortMapping = null;
            _udp?.Dispose();
            _udp = null;
            _transfer?.Dispose();
            _transfer = null;
            foreach (var peer in Peers.Concat(_pending)) peer.Conn?.Dispose();
            _pending.Clear();
            Peers.Clear();
            _routes = new Connection[256];
            _present = new bool[256];
            _hostConn?.Dispose();
            _hostConn = null;
            State = SessionState.Idle;
            ResetReady();
            ResetLobbyReady();
            NetplayDialogs.CloseWaiting();
            ReceivedLibrary.SetActive(false);
            PartFilter.Refresh();
        }

        // ---------------------------------------------------------------- sending

        /// <summary>To one player. Thread-safe.</summary>
        public void SendTo(byte to, MsgType type, Action<BinaryWriter> body = null)
        {
            if (IsHost) _routes[to]?.Send(type, LocalId, to, body);
            else _hostConn?.Send(type, LocalId, to, body);
        }

        /// <summary>To every other player.</summary>
        public void Broadcast(MsgType type, Action<BinaryWriter> body = null)
        {
            if (!IsHost)
            {
                _hostConn?.Send(type, LocalId, Protocol.Broadcast, body);
                return;
            }
            foreach (var conn in _routes)
                conn?.Send(type, LocalId, Protocol.Broadcast, body);
        }

        /// <summary>
        /// Host, on a player's read thread: passes the frame on to whoever it's for, and says whether the host handles
        /// it too. Frames from a player who hasn't been welcomed yet only reach the host.
        /// </summary>
        private bool Route(byte from, byte[] frame)
        {
            frame[1] = from; // nobody speaks for someone else
            var routes = _routes;
            if (routes[from] == null) return true;

            byte to = frame[2];
            if (to == Protocol.Broadcast)
            {
                for (int id = 0; id < routes.Length; id++)
                    if (id != from) routes[id]?.SendFrame(frame);
                return true;
            }
            if (to == Protocol.HostId) return true;
            if (to != from) routes[to]?.SendFrame(frame);
            return false;
        }

        /// <summary>Call after <see cref="Peers"/> changes.</summary>
        private void UpdateRoutes()
        {
            var routes = new Connection[256];
            var present = new bool[256];
            foreach (var peer in Peers)
            {
                routes[peer.Id] = peer.Conn;
                present[peer.Id] = true;
            }
            if (IsHost) _routes = routes;
            _present = present;
        }

        // ---------------------------------------------------------------- main-thread pump

        public void Update()
        {
            if (_pendingError != null)
            {
                var error = _pendingError;
                _pendingError = null;
                Fail((IsHost ? "Hosting stopped: " : "Connection failed: ") + error);
                return;
            }

            if (IsHost) UpdateHost();
            else if (!UpdateJoined()) return;

            // Keep song transfers off the wire while a song is loading or playing.
            if (_transfer != null) _transfer.Paused = InSongFlow;

            if (LocalNow >= _nextPing && (IsConnected || _pending.Count > 0))
            {
                _nextPing = LocalNow + PING_INTERVAL;
                double t = LocalNow;
                if (IsHost)
                {
                    Broadcast(MsgType.Ping, w => w.Write(t));
                    SendPlayerStats();
                }
                else
                {
                    SendTo(Protocol.HostId, MsgType.Ping, w => w.Write(t));
                }
            }

            UpdateUdp();

            CheckReady();

            // Host: everyone in the song loaded, schedule the shared start.
            if (IsHost && State == SessionState.Loading && LocalLoaded && StartAtHostTime == null
                && SongPeers().All(p => p.Loaded))
            {
                double worstRtt = SongPeers().Select(p => p.RttSeconds).DefaultIfEmpty(0).Max();
                double startAt = HostNow + START_LEAD_TIME + worstRtt;
                StartAtHostTime = startAt;
                Broadcast(MsgType.StartAt, w => w.Write(startAt));
            }
        }

        private void UpdateHost()
        {
            if (_listener == null) return;

            while (_accepted.TryDequeue(out var client)) OnAccepted(client);

            foreach (var peer in _pending.ToList())
            {
                Drain(peer.Conn);
                if (peer.Conn.IsClosed) _pending.Remove(peer);
            }

            foreach (var peer in Peers.ToList())
            {
                if (_listener == null) return; // a message ended the session
                Drain(peer.Conn);
                if (peer.Conn.IsClosed && Peers.Contains(peer))
                    RemovePeer(peer, peer.Conn.CloseReason);
            }
        }

        /// <summary>Joining player: false once the session ended.</summary>
        private bool UpdateJoined()
        {
            if (_pendingClient != null)
            {
                var client = _pendingClient;
                _pendingClient = null;
                OnConnectedToHost(client);
            }

            if (_hostConn == null) return false;
            Drain(_hostConn);
            if (_hostConn == null) return false;

            if (_rejectReason != null)
            {
                Fail("Couldn't join: " + _rejectReason);
                return false;
            }
            if (_hostConn.IsClosed)
            {
                Fail("Disconnected: " + _hostConn.CloseReason);
                return false;
            }
            return true;
        }

        private void Drain(Connection conn)
        {
            // Frames that arrived before a connection closed still count (a Reject, a last LeaveSong).
            while (conn.Inbox.TryDequeue(out var frame))
            {
                try { Handle(frame); }
                catch (Exception e) { Plugin.Log.LogError($"Bad message: {e}"); }
                if (IsHost ? _listener == null : _hostConn == null) break; // the session ended
            }
        }

        private void OnAccepted(TcpClient client)
        {
            byte id = 1;
            while (id < NoId && Peers.Concat(_pending).Any(p => p.Id == id)) id++;
            if (id >= NoId)
            {
                client.Close();
                return;
            }

            var peer = new Peer
            {
                Id = id,
                Address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address,
                ConnectedAt = LocalNow,
            };
            peer.Conn = new Connection(client, (_, frame) => Route(id, frame));
            _pending.Add(peer);
            Plugin.Log.LogInfo($"Player {id} connected from {peer.Conn.RemoteEndPoint}");
        }

        private void OnConnectedToHost(TcpClient client)
        {
            _hostAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            _hostConn = new Connection(client);
            _connectedAt = LocalNow;
            _rttHistory.Clear();
            Status = "Connected to " + _hostConn.RemoteEndPoint + ", saying hello...";

            _hostConn.Send(MsgType.Hello, NoId, Protocol.HostId, w =>
            {
                w.Write(Protocol.Version);
                w.Write(GameInfo.YargVersion);
                w.Write(Plugin.Version);
                w.Write(LocalName ?? "Player");
            });
        }

        /// <summary>Host: a player said Hello. Turn them away, or bring them in and tell everyone.</summary>
        private void OnHello(Peer peer, int protocol, string yargVersion, string modVersion, string name)
        {
            string reject =
                protocol != Protocol.Version ? $"netplay version mismatch (host {Plugin.Version}, you {modVersion})"
                : Peers.Count + 1 >= Protocol.MaxPlayers ? $"the game is full ({Protocol.MaxPlayers} players)"
                : State is not (SessionState.Hosting or SessionState.Lobby) || SceneManager.GetActiveScene().buildIndex != (int) SceneIndex.Menu
                    ? "the others are starting or playing a song. Try again when it's over."
                : null;
            if (reject != null)
            {
                Plugin.Log.LogInfo($"Turned away {name}: {reject}");
                peer.Conn.Send(MsgType.Reject, LocalId, peer.Id, w => w.Write(reject));
                peer.Conn.Close("Turned away: " + reject);
                _pending.Remove(peer);
                return;
            }

            peer.Name = name;
            peer.YargVersion = yargVersion;
            peer.Welcomed = true;
            _pending.Remove(peer);

            // Everyone else first, so frames the new player sends once welcomed always come after its PlayerJoined.
            Broadcast(MsgType.PlayerJoined, w => { w.Write(peer.Id); w.Write(name); w.Write(yargVersion); w.Write(true); });

            var conn = peer.Conn;
            conn.Send(MsgType.Welcome, LocalId, peer.Id);
            conn.Send(MsgType.PlayerJoined, LocalId, peer.Id, w => { w.Write(LocalId); w.Write(LocalName ?? "Host"); w.Write(GameInfo.YargVersion); w.Write(false); });
            foreach (var other in Peers)
                conn.Send(MsgType.PlayerJoined, LocalId, peer.Id, w => { w.Write(other.Id); w.Write(other.Name); w.Write(other.YargVersion ?? ""); w.Write(false); });

            // The shared setlist so far, in order.
            foreach (var item in Queue)
                conn.Send(MsgType.QueueAdd, LocalId, peer.Id, w => WriteQueueAdd(w, item));

            Peers.Add(peer);
            UpdateRoutes();
            AnnounceTo(peer.Id);

            if (_udp != null)
            {
                ulong token = _udp.AddPeer(peer.Id);
                int port = _udp.LocalPort;
                SendTo(peer.Id, MsgType.UdpInfo, w => { w.Write(token); w.Write(port); });
            }

            if (State == SessionState.Hosting) State = SessionState.Lobby;
            UpdateHostStatus();
            ReceivedLibrary.SetActive(true);
            PartFilter.Refresh();

            if (yargVersion == GameInfo.YargVersion) Notify.Success($"{name} joined your game");
            else Notify.Warning($"{name} joined, but runs YARG {yargVersion} (you: {GameInfo.YargVersion}). Scores may not match.");
        }

        private void UpdateHostStatus()
        {
            if (!IsHost) return;
            Status = Peers.Count == 0 ? $"Hosting on port {_listenPort}, waiting for players..." : $"Playing with {OthersText}";
        }

        /// <summary>Tells a player who just joined what they need to know about us.</summary>
        private void AnnounceTo(byte id)
        {
            var part = PartFilter.LocalPart;
            SendTo(id, MsgType.PartChoice, part.Write);
            bool inSongSelect = NativeMenus.InSongSelect;
            SendTo(id, MsgType.SongSelectState, w => w.Write(inSongSelect));
            bool ready = LocalLobbyReady;
            if (ready) SendTo(id, MsgType.LobbyReady, w => w.Write(true));
            foreach (var item in Queue.Where(q => q.LocalHas))
            {
                var hash = item.Hash;
                SendTo(id, MsgType.Availability, w => { w.WriteHash(hash); w.Write(true); });
            }
        }

        /// <summary>A player left (or their connection dropped). Everyone else carries on.</summary>
        private void RemovePeer(Peer peer, string reason)
        {
            if (!Peers.Remove(peer)) return;
            Plugin.Log.LogInfo($"{peer.Name} left: {reason}");
            UpdateRoutes();

            if (IsHost)
            {
                peer.Conn?.Dispose();
                _udp?.RemovePeer(peer.Id);
                byte id = peer.Id;
                Broadcast(MsgType.PlayerLeft, w => w.Write(id));
            }

            _transfer?.OnPeerLeft(peer.Id, peer.Name);
            foreach (var item in Queue) item.PeerHas.Remove(peer.Id);
            Notify.Warning($"{peer.Name} left the game");

            if (Peers.Count == 0)
            {
                // Nobody to wait for any more. A song already under way just carries on.
                if (State == SessionState.Waiting) CancelReady();
                ResetLobbyReady();
                if (IsHost && State == SessionState.Lobby) State = SessionState.Hosting;
            }
            UpdateHostStatus();
            PartFilter.Refresh();
        }

        // ---------------------------------------------------------------- UDP

        private void UpdateUdp()
        {
            if (_udp == null) return;

            while (_udp != null && _udp.TryReceive(out byte from, out byte kind, out var r))
            {
                try { HandleUdp(from, kind, r); }
                catch (Exception e) { Plugin.Log.LogWarning($"Bad UDP packet: {e.Message}"); }
            }

            if (_udp == null) return;

            if (!IsHost && _udp.HasAddress(Protocol.HostId))
            {
                bool alive = _udp.Alive(Protocol.HostId);
                if (alive != _udpWasAlive)
                {
                    _udpWasAlive = alive;
                    Plugin.Log.LogInfo(alive ? "UDP: packets from the host are getting through" : "UDP: no packets from the host lately; inputs use TCP only");
                }
            }

            // Probes open the way through the NATs and keep it open. During songs the input packets do that.
            if (LocalNow >= _nextUdpProbe)
            {
                bool alive = IsHost ? Peers.All(p => _udp.Alive(p.Id)) : _udp.Alive(Protocol.HostId);
                _nextUdpProbe = LocalNow + (alive ? UDP_KEEPALIVE_INTERVAL : UDP_PROBE_INTERVAL);
                _udp.SendAll(UdpChannel.KindProbe);
            }
        }

        private void HandleUdp(byte from, byte kind, BinaryReader r)
        {
            if (kind != UdpChannel.KindInputs) return; // probes only show the way is open

            byte player = r.ReadByte();
            double songKey = r.ReadDouble();
            int lastSeq = r.ReadInt32();
            double mark = r.ReadDouble();
            int count = r.ReadByte();

            // The host takes a player's own inputs only; everyone else gets them through the host.
            if (IsHost ? player != from : from != Protocol.HostId) return;
            // From an earlier song (UDP can arrive late), or the song isn't set up here yet: TCP brings it anyway.
            if (StartAtHostTime != songKey || !InSongFlow || FindPeer(player) is not Peer peer) return;

            for (int i = 0; i < count; i++)
            {
                double time = r.ReadDouble();
                int action = r.ReadInt32();
                int value = r.ReadInt32();
                peer.Inputs.Add(lastSeq - count + 1 + i, new GameInput(time, action, value));
            }
            peer.Inputs.SetWatermark(lastSeq, mark);
        }

        // ---------------------------------------------------------------- song select presence

        /// <summary>Another player who is in song select, so we can join them there without them pulling us in.</summary>
        public Peer PeerInSongSelect => Peers.FirstOrDefault(p => p.InSongSelect);

        public void SendSongSelectState(bool inSongSelect)
        {
            if (IsConnected) Broadcast(MsgType.SongSelectState, w => w.Write(inSongSelect));
        }

        // ---------------------------------------------------------------- preferred parts (library filter)

        public void SendPartChoice()
        {
            var part = PartFilter.LocalPart;
            if (IsConnected) Broadcast(MsgType.PartChoice, part.Write);
        }

        // ---------------------------------------------------------------- incoming

        private void Handle(byte[] frame)
        {
            using var r = new BinaryReader(new MemoryStream(frame));
            var type = (MsgType) r.ReadByte();
            byte from = r.ReadByte();
            byte to = r.ReadByte();

            if (IsHost)
            {
                var pending = _pending.FirstOrDefault(p => p.Id == from);
                if (pending != null)
                {
                    // Nothing counts before Hello.
                    if (type == MsgType.Hello)
                        OnHello(pending, r.ReadInt32(), r.ReadString(), r.ReadString(), r.ReadString());
                    return;
                }
            }

            var peer = FindPeer(from);

            // Session membership only comes from the host (another player's copy would be passed on with their id).
            bool fromHost = !IsHost && from == Protocol.HostId;
            if (type is MsgType.Reject or MsgType.Welcome or MsgType.PlayerJoined or MsgType.PlayerLeft or MsgType.PlayerStats
                && !fromHost)
                return;

            switch (type)
            {
                case MsgType.Reject:
                    _rejectReason = r.ReadString();
                    break;

                case MsgType.Welcome:
                    OnWelcome(to);
                    break;

                case MsgType.PlayerJoined:
                {
                    byte id = r.ReadByte();
                    string name = r.ReadString();
                    string yargVersion = r.ReadString();
                    bool isNew = r.ReadBoolean();
                    OnPlayerJoined(id, name, yargVersion, isNew);
                    break;
                }

                case MsgType.PlayerLeft:
                    if (FindPeer(r.ReadByte()) is Peer left) RemovePeer(left, "left the session");
                    break;

                case MsgType.PlayerStats:
                {
                    int count = r.ReadByte();
                    for (int i = 0; i < count; i++)
                    {
                        byte id = r.ReadByte();
                        double rtt = r.ReadSingle(), worst = r.ReadSingle();
                        bool udpFailed = r.ReadBoolean();
                        if (id == LocalId || FindPeer(id) is not Peer p) continue;
                        p.RttSeconds = rtt;
                        p.WorstRttSeconds = worst;
                        p.UdpFailed = udpFailed;
                    }
                    break;
                }

                case MsgType.Ping:
                {
                    double t = r.ReadDouble();
                    double now = LocalNow;
                    SendTo(from, MsgType.Pong, w => { w.Write(t); w.Write(now); });
                    break;
                }

                case MsgType.Pong:
                    OnPong(peer, r.ReadDouble(), r.ReadDouble());
                    break;
            }

            // Everything else comes from a player we know.
            if (peer == null) return;

            switch (type)
            {
                case MsgType.QueueAdd:
                {
                    var hash = r.ReadHash();
                    string name = r.ReadString();
                    string artist = r.ReadString();
                    long length = r.ReadInt64();
                    byte addedBy = r.ReadByte();

                    var item = FindQueueItem(hash);
                    bool isNew = item == null;
                    if (isNew)
                    {
                        item = new QueueItem
                        {
                            Hash = hash, Name = name, Artist = artist, LengthMilliseconds = length,
                            AddedByMe = addedBy == LocalId, AddedBy = NameOf(addedBy), AddedById = addedBy,
                        };
                        Queue.Add(item);
                    }
                    if (from == addedBy) item.PeerHas[from] = true;
                    item.LocalHas = SongLibrary.TryGet(hash, out var song);

                    bool has = item.LocalHas;
                    Broadcast(MsgType.Availability, w => { w.WriteHash(hash); w.Write(has); });

                    // The open setlist overlay already shows the new song, who added it and its download state.
                    // Songs that were already in the setlist before we joined aren't news.
                    bool announce = isNew && from == addedBy && !SetlistOverlay.ListShowing;
                    if (has)
                    {
                        NativeSetlist.Add(song);
                        if (announce) Notify.Info($"{peer.Name} added {name} to the setlist");
                    }
                    else
                    {
                        // Missing it: have someone who has it send it straight away.
                        RequestSong(item);
                        if (announce) Notify.Info($"{peer.Name} added {name}. You don't have it, downloading...");
                    }
                    break;
                }

                case MsgType.QueueRemove:
                {
                    var hash = r.ReadHash();
                    var item = FindQueueItem(hash);
                    if (item != null) Queue.Remove(item);
                    NativeSetlist.Remove(hash);
                    break;
                }

                case MsgType.QueueClear:
                    Queue.Clear();
                    NativeSetlist.Clear();
                    break;

                case MsgType.QueueOrder:
                {
                    int count = r.ReadInt32();
                    var order = new List<QueueItem>(count);
                    for (int i = 0; i < count; i++)
                        if (FindQueueItem(r.ReadHash()) is QueueItem listed && !order.Contains(listed)) order.Add(listed);
                    // Anything the sender didn't list (added here in the meantime) keeps its place after the listed songs.
                    order.AddRange(Queue.Where(q => !order.Contains(q)));
                    Queue.Clear();
                    Queue.AddRange(order);
                    NativeSetlist.ApplyOrder();
                    break;
                }

                case MsgType.Availability:
                {
                    var item = FindQueueItem(r.ReadHash());
                    bool has = r.ReadBoolean();
                    if (item == null) break;
                    item.PeerHas[from] = has;
                    // Someone has a song we're missing and haven't asked anyone for yet.
                    if (has && !item.LocalHas && _transfer?.GetIncoming(item.Hash) == null)
                        _transfer?.Request(item.Hash, from);
                    break;
                }

                case MsgType.SongRequest:
                    _transfer?.OnRequest(r.ReadHash(), from);
                    break;

                case MsgType.SongBegin:
                    _transfer?.OnBegin(r, from);
                    break;

                case MsgType.SongChunk:
                    _transfer?.OnChunk(r, from);
                    break;

                case MsgType.SongEnd:
                    _transfer?.OnEnd(r, from);
                    break;

                case MsgType.SongFail:
                    _transfer?.OnFail(r, from);
                    break;

                case MsgType.OpenSetlist:
                {
                    bool show = r.ReadBoolean();
                    int count = r.ReadInt32();
                    var hashes = new List<byte[]>(count);
                    for (int i = 0; i < count; i++) hashes.Add(r.ReadHash());
                    OnPeerOpenedSetlist(peer, show, hashes);
                    break;
                }

                case MsgType.Ready:
                {
                    peer.ReadyHash = r.ReadHash();
                    int len = r.ReadInt32();
                    peer.ReadySetup = new PeerSetup
                    {
                        ProfileBytes = r.ReadBytes(len),
                        EnginePresetJson = r.ReadString(),
                    };
                    break;
                }

                case MsgType.Unready:
                    peer.ReadyHash = null;
                    peer.ReadySetup = null;
                    if (State == SessionState.Waiting)
                        Notify.Warning($"{peer.Name} backed out");
                    break;

                case MsgType.LeaveSetlist:
                    OnPeerLeftSetlist(peer);
                    break;

                case MsgType.PartChoice:
                    peer.Part = PartChoice.Read(r);
                    PartFilter.Refresh();
                    break;

                case MsgType.SongSelectState:
                    peer.InSongSelect = r.ReadBoolean();
                    break;

                case MsgType.Loaded:
                    peer.Loaded = true;
                    break;

                case MsgType.StartAt:
                    if (from == Protocol.HostId) StartAtHostTime = r.ReadDouble();
                    break;

                case MsgType.Input:
                {
                    int seq = r.ReadInt32();
                    double time = r.ReadDouble();
                    int action = r.ReadInt32();
                    int value = r.ReadInt32();
                    peer.Inputs.Add(seq, new GameInput(time, action, value));
                    break;
                }

                case MsgType.Watermark:
                {
                    int lastSeq = r.ReadInt32();
                    double mark = r.ReadDouble();
                    peer.Inputs.SetWatermark(lastSeq, mark);
                    break;
                }

                case MsgType.UdpInfo:
                {
                    ulong token = r.ReadUInt64();
                    int port = r.ReadInt32();
                    if (IsHost) _udp?.SetPeerAddress(peer.Id, peer.Address, port, token);
                    else if (from == Protocol.HostId) _udp?.SetPeerAddress(Protocol.HostId, _hostAddress, port, token);
                    break;
                }

                case MsgType.Pause:
                    GameHooks.ApplyRemotePause(true);
                    break;

                case MsgType.Resume:
                    GameHooks.ApplyRemotePause(false);
                    break;

                case MsgType.LeaveSong:
                    // Leaving while a restart or skip waits for everyone: drop the wait, then leave.
                    if (ChangingSong) CancelReady(leaveSong: false);
                    Notify.Warning(GameHooks.ApplyRemoteLeave()
                        ? $"{peer.Name} left the song. Back to the library."
                        : $"{peer.Name} left the song");
                    break;

                case MsgType.ChangeSong:
                {
                    int index = r.ReadInt32();
                    var hash = r.ReadHash();
                    bool skip = r.ReadBoolean();
                    OnPeerChangedSong(peer, index, hash, skip);
                    break;
                }

                case MsgType.LobbyReady:
                    peer.LobbyReady = r.ReadBoolean();
                    if (IsHost && peer.LobbyReady) Notify.Info($"{peer.Name} is ready");
                    break;

                case MsgType.GoToSongSelect:
                    if (from != Protocol.HostId) break;
                    ResetLobbyReady();
                    NativeMenus.OpenLibrary();
                    Notify.Info($"{peer.Name} opened song select. Add songs to the setlist or pick one.");
                    break;
            }
        }

        /// <summary>Joining player: the host let us in and gave us our id.</summary>
        private void OnWelcome(byte id)
        {
            if (IsHost || LocalId != NoId) return;
            LocalId = id;
            State = SessionState.Lobby;
            ReceivedLibrary.SetActive(true);

            // The joining player takes UDP on any free port.
            _udp = UdpChannel.Open(0, relay: false);
            if (_udp != null)
            {
                ulong token = _udp.AddPeer(Protocol.HostId);
                int port = _udp.LocalPort;
                SendTo(Protocol.HostId, MsgType.UdpInfo, w => { w.Write(token); w.Write(port); });
            }
            InitTransfer();

            SendPartChoice();
            SendSongSelectState(NativeMenus.InSongSelect);
            PartFilter.ApplyToProfile(PartFilter.LocalPart);
        }

        private void OnPlayerJoined(byte id, string name, string yargVersion, bool isNew)
        {
            if (IsHost || id == LocalId) return;
            var peer = FindPeer(id);
            if (peer == null)
            {
                Peers.Add(peer = new Peer { Id = id });
                UpdateRoutes();
            }
            peer.Name = name;
            peer.YargVersion = yargVersion;

            if (id == Protocol.HostId)
            {
                SavedServers.RememberJoin(JoinAddress, name);

                // Engines must match exactly or the remote simulation drifts.
                if (yargVersion == GameInfo.YargVersion)
                {
                    Status = $"Connected to {name}";
                    Notify.Success($"Connected to {name}");
                }
                else
                {
                    Status = $"Connected to {name} - WARNING: YARG version differs ({GameInfo.YargVersion} vs {yargVersion})";
                    Notify.Warning($"Connected to {name}, but they run YARG {yargVersion} (you: {GameInfo.YargVersion}). Scores may not match.");
                }

                // Share any setlist that was built before connecting (or kept from an earlier session). The host
                // sends the setlist so far right after this, and duplicates merge.
                foreach (var item in Queue.Where(q => q.AddedByMe).ToList())
                    SendQueueAdd(item);
                NativeSetlist.ResyncIfOpen();
            }
            else if (isNew)
            {
                AnnounceTo(id);
                if (yargVersion == GameInfo.YargVersion) Notify.Info($"{name} joined the game");
                else Notify.Warning($"{name} joined, but runs YARG {yargVersion} (you: {GameInfo.YargVersion}). Scores may not match.");
            }
            PartFilter.Refresh();
        }

        private void OnPong(Peer peer, double sent, double peerTime)
        {
            double now = LocalNow;
            double rtt = now - sent;

            if (IsHost)
            {
                if (peer == null) return;
                peer.RecentRtts.Add(rtt);
                if (peer.RecentRtts.Count > CLOCK_SAMPLES) peer.RecentRtts.RemoveAt(0);
                if (sent >= peer.ConnectedAt + RTT_WARMUP)
                {
                    peer.RttHistory.Enqueue(rtt);
                    if (peer.RttHistory.Count > RTT_HISTORY) peer.RttHistory.Dequeue();
                }
                peer.RttSeconds = peer.RecentRtts.Min();
                peer.WorstRttSeconds = peer.RttHistory.Count > 0 ? peer.RttHistory.Max() : peer.RttSeconds;
                return;
            }

            if (peer?.Id != Protocol.HostId) return;
            _clockSamples.Add((rtt, peerTime - (sent + rtt / 2)));
            if (_clockSamples.Count > CLOCK_SAMPLES) _clockSamples.RemoveAt(0);
            if (sent >= _connectedAt + RTT_WARMUP)
            {
                _rttHistory.Enqueue(rtt);
                if (_rttHistory.Count > RTT_HISTORY) _rttHistory.Dequeue();
            }

            // The lowest-RTT sample has the least queuing noise.
            var best = _clockSamples.OrderBy(s => s.rtt).First();
            RttSeconds = best.rtt;
            _clockOffset = best.offset;
        }

        /// <summary>Host: everyone's connection to the host, for the player list and each player's remote delays.</summary>
        private void SendPlayerStats()
        {
            if (Peers.Count == 0) return;
            var stats = Peers.Select(p => (p.Id, (float) p.RttSeconds, (float) p.WorstRttSeconds, UdpFailed(p))).ToList();
            Broadcast(MsgType.PlayerStats, w =>
            {
                w.Write((byte) stats.Count);
                foreach (var (id, rtt, worst, udpFailed) in stats)
                {
                    w.Write(id);
                    w.Write(rtt);
                    w.Write(worst);
                    w.Write(udpFailed);
                }
            });
        }

        // ---------------------------------------------------------------- lobby

        public void SetLobbyReady(bool ready)
        {
            if (!IsConnected || LocalLobbyReady == ready) return;
            LocalLobbyReady = ready;
            Broadcast(MsgType.LobbyReady, w => w.Write(ready));
        }

        /// <summary>Host: whether every other player is ready (or already in song select).</summary>
        public bool EveryoneLobbyReady => Peers.Count > 0 && Peers.All(p => p.LobbyReady || p.InSongSelect);

        /// <summary>Host: send everyone to song select (Quickplay).</summary>
        public void SendEveryoneToSongSelect()
        {
            if (!IsConnected || !IsHost) return;
            Broadcast(MsgType.GoToSongSelect);
            ResetLobbyReady();
            NativeMenus.OpenLibrary();
        }

        private void ResetLobbyReady()
        {
            LocalLobbyReady = false;
            foreach (var peer in Peers) peer.LobbyReady = false;
        }

        // ---------------------------------------------------------------- shared setlist

        private QueueItem FindQueueItem(byte[] hash) => Queue.FirstOrDefault(q => q.Hash.AsSpan().SequenceEqual(hash));

        /// <summary>A song was added to the local setlist (natively or from the overlay).</summary>
        public void AddToQueue(SongEntry song)
        {
            var hash = song.Hash.HashBytes;
            if (FindQueueItem(hash) != null) return;

            var item = new QueueItem
            {
                Hash = hash,
                Name = song.Name.Original,
                Artist = song.Artist.Original,
                LengthMilliseconds = song.SongLengthMilliseconds,
                LocalHas = true,
                AddedByMe = true,
            };
            Queue.Add(item);
            if (IsConnected) SendQueueAdd(item);
        }

        public void RemoveFromQueue(byte[] hash)
        {
            var item = FindQueueItem(hash);
            if (item == null) return;
            Queue.Remove(item);
            if (IsConnected) Broadcast(MsgType.QueueRemove, w => w.WriteHash(hash));
        }

        /// <summary>Moves a song up (-1) or down (+1) in the shared setlist. False when it can't move that way.</summary>
        public bool MoveInQueue(int index, int delta)
        {
            int to = index + delta;
            if (index < 0 || index >= Queue.Count || to < 0 || to >= Queue.Count) return false;
            (Queue[index], Queue[to]) = (Queue[to], Queue[index]);
            SendQueueOrder();
            return true;
        }

        /// <summary>
        /// YARG's own setlist was reordered (it only holds the songs this player has): put those songs in the same
        /// order, in the places they already take in the shared setlist, so songs still downloading don't move.
        /// </summary>
        public void MatchQueueOrder(IList<byte[]> nativeOrder)
        {
            var native = nativeOrder.Select(FindQueueItem).Where(q => q != null).Distinct().ToList();
            int next = 0;
            for (int i = 0; i < Queue.Count && next < native.Count; i++)
                if (native.Contains(Queue[i])) Queue[i] = native[next++];
            SendQueueOrder();
        }

        private void SendQueueOrder()
        {
            if (!IsConnected) return;
            var hashes = Queue.Select(q => q.Hash).ToList();
            Broadcast(MsgType.QueueOrder, w =>
            {
                w.Write(hashes.Count);
                foreach (var hash in hashes) w.WriteHash(hash);
            });
        }

        public void ClearQueue()
        {
            if (Queue.Count == 0) return;
            Queue.Clear();
            if (IsConnected) Broadcast(MsgType.QueueClear);
        }

        public void RetryTransfer(QueueItem item)
        {
            if (IsConnected && !item.LocalHas) RequestSong(item);
        }

        /// <summary>Asks a player who has this song to send it: whoever added it if they're still here, else anyone who has it.</summary>
        private void RequestSong(QueueItem item)
        {
            if (_transfer == null) return;
            var source = !item.AddedByMe && FindPeer(item.AddedById) is Peer adder && item.PeerHas.TryGetValue(adder.Id, out bool adderHas) && adderHas
                ? adder
                : Peers.FirstOrDefault(p => item.PeerHas.TryGetValue(p.Id, out bool has) && has);
            // Nobody known to have it yet: their Availability will ask for it.
            if (source != null) _transfer.Request(item.Hash, source.Id);
        }

        private void OnSongReceived(byte[] hash)
        {
            var item = FindQueueItem(hash);
            if (item != null) item.LocalHas = true;
            if (IsConnected) Broadcast(MsgType.Availability, w => { w.WriteHash(hash); w.Write(true); });

            ReceivedLibrary.Refresh();
            if (SongLibrary.TryGet(hash, out var song))
            {
                if (item != null) NativeSetlist.Add(song);
                // The open setlist overlay already shows the song turning Ready.
                if (!SetlistOverlay.ListShowing) Notify.Success($"Downloaded {song.Name.Original}");
            }
        }

        private void SendQueueAdd(QueueItem item) => Broadcast(MsgType.QueueAdd, w => WriteQueueAdd(w, item));

        private void WriteQueueAdd(BinaryWriter w, QueueItem item)
        {
            w.WriteHash(item.Hash);
            w.Write(item.Name ?? "");
            w.Write(item.Artist ?? "");
            w.Write(item.LengthMilliseconds);
            w.Write(item.AddedByMe ? LocalId : item.AddedById);
        }

        // ---------------------------------------------------------------- following each other into difficulty select

        /// <summary>We opened difficulty select for these songs; bring everyone along.</summary>
        public void SendOpenSetlist(IReadOnlyList<SongEntry> songs, bool show)
        {
            if (!IsConnected || State is not (SessionState.Lobby or SessionState.Waiting)) return;

            Broadcast(MsgType.OpenSetlist, w =>
            {
                w.Write(show);
                w.Write(songs.Count);
                foreach (var s in songs) w.WriteHash(s.Hash.HashBytes);
            });
        }

        /// <summary>We backed out of difficulty select to the library; take everyone back too.</summary>
        public void SendLeaveSetlist()
        {
            if (!IsConnected || State is not (SessionState.Lobby or SessionState.Waiting)) return;
            CancelReady();
            Broadcast(MsgType.LeaveSetlist);
        }

        private void OnPeerLeftSetlist(Peer peer)
        {
            CancelReady();
            peer.ReadyHash = null;
            peer.ReadySetup = null;
            if (NativeMenus.FollowOutOfDifficultySelect())
                Notify.Info($"{peer.Name} went back to the song list");
        }

        private void OnPeerOpenedSetlist(Peer peer, bool show, List<byte[]> hashes)
        {
            var songs = new List<SongEntry>();
            foreach (var hash in hashes)
            {
                if (SongLibrary.TryGet(hash, out var song))
                {
                    songs.Add(song);
                }
                else
                {
                    // Whoever opened it has every song in it.
                    _transfer?.Request(hash, peer.Id);
                    Notify.Warning($"{peer.Name} picked a song you don't have yet. Downloading it; try again when it finishes.");
                    return;
                }
            }

            NativeMenus.FollowIntoDifficultySelect(songs, show, peer.Name);
        }

        // ---------------------------------------------------------------- ready gate

        /// <summary>
        /// Called (via a patch) whenever the game wants to load the gameplay scene.
        /// Returns true when the load may proceed; otherwise we send Ready and wait for everyone else.
        /// </summary>
        public bool TryPassGameplayGate()
        {
            if (_gateOpen)
            {
                _gateOpen = false;
                return true;
            }

            var song = GlobalVariables.State.CurrentSong;
            if (song == null) return true;

            if (State is SessionState.Loading or SessionState.InSong)
            {
                // Only a restart or skip announced to the others may leave the song for another one.
                if (!ChangingSong) return false;
                State = SessionState.Lobby;
            }

            if (!SongLauncher.TryPrepareLocalPlayer(song, out var setup, out var error))
            {
                NetplayDialogs.ShowError(error);
                return false;
            }

            if (Math.Abs(GlobalVariables.State.SongSpeed - 1f) > 0.001f)
            {
                GlobalVariables.State.SongSpeed = 1f;
                Notify.Info("Song speed is fixed at 100% online.");
            }

            // The score screen's Continue advances the show before loading; undo that if we end up cancelling,
            // or pressing Continue again would skip a song.
            _undoShowAdvanceOnCancel = Show.Playing
                && SceneManager.GetActiveScene().buildIndex == (int) SceneIndex.Score;

            _localReadyHash = song.Hash.HashBytes;
            var hash = _localReadyHash;
            State = SessionState.Waiting;
            Broadcast(MsgType.Ready, w =>
            {
                w.WriteHash(hash);
                w.Write(setup.ProfileBytes.Length);
                w.Write(setup.ProfileBytes);
                w.Write(setup.EnginePresetJson);
            });

            NetplayDialogs.ShowWaiting(OthersText, song.Name.Original, () => CancelReady());
            CheckReady();
            return false;
        }

        /// <summary>
        /// Cancels our Ready. During a restart or skip the old song's scene is still up, so cancelling also leaves it
        /// (for everyone) unless <paramref name="leaveSong"/> is false because the song is being left anyway.
        /// </summary>
        public void CancelReady(bool leaveSong = true)
        {
            if (State != SessionState.Waiting) return;
            if (ChangingSong && leaveSong && GameHooks.GameManager != null)
                GameHooks.GameManager.ForceQuitSong(); // while ChangingSong is still set, so the others are told
            ChangingSong = false;
            _localReadyHash = null;
            State = SessionState.Lobby;
            if (IsConnected) Broadcast(MsgType.Unready);
            NetplayDialogs.CloseWaiting();

            if (_undoShowAdvanceOnCancel && Show.Index > 0)
            {
                Show.Index--;
                GlobalVariables.State.CurrentSong = Show.Songs[Show.Index];
            }
            _undoShowAdvanceOnCancel = false;
        }

        private void CheckReady()
        {
            if (State != SessionState.Waiting || _localReadyHash == null || Peers.Count == 0) return;

            foreach (var peer in Peers)
            {
                if (peer.ReadyHash == null || _localReadyHash.AsSpan().SequenceEqual(peer.ReadyHash)) continue;

                string theirs = SongLibrary.TryGet(peer.ReadyHash, out var s) ? s.Name.Original : "a different song";
                NetplayDialogs.CloseWaiting();
                NetplayDialogs.ShowError($"{peer.Name} is ready for {theirs}. Pick the same song to play together.");
                CancelReady();
                peer.ReadyHash = null;
                peer.ReadySetup = null;
                return;
            }
            if (Peers.Any(p => p.ReadyHash == null)) return;

            // A setlist has really started once its first song loads (not when difficulty select opens, which
            // can be backed out of). The next setlist is built from scratch, like in single player: the menu
            // scene's own setlist comes back empty too.
            if (Show.Playing && Show.Index == 0) Queue.Clear();

            // Everyone ready for the same song: these setups drive the remote players for this song.
            _replacingSong = GameHooks.GameManager != null; // a restart or skip: the old scene unloads after this
            ChangingSong = false;
            ActiveSetups = Peers.ToDictionary(p => p.Id, p => p.ReadySetup);
            ResetReady();
            LocalLoaded = false;
            foreach (var peer in Peers)
            {
                peer.Loaded = false;
                peer.Inputs.Clear();
            }
            StartAtHostTime = null;
            ResetInputStream();
            State = SessionState.Loading;
            Status = $"Playing with {OthersText}";

            NetplayDialogs.CloseWaiting();
            _gateOpen = true;
            GlobalVariables.Instance.LoadScene(SceneIndex.Gameplay);
        }

        private void ResetReady()
        {
            _localReadyHash = null;
            foreach (var peer in Peers)
            {
                peer.ReadyHash = null;
                peer.ReadySetup = null;
            }
            _gateOpen = false;
            _undoShowAdvanceOnCancel = false;
        }

        // ---------------------------------------------------------------- in song

        /// <summary>The players in the current song who are still connected.</summary>
        public IEnumerable<Peer> SongPeers() =>
            ActiveSetups == null ? Enumerable.Empty<Peer>() : Peers.Where(p => ActiveSetups.ContainsKey(p.Id));

        /// <summary>Called by the start-gate patch once our gameplay scene has finished loading.</summary>
        public void MarkLocalLoaded()
        {
            if (LocalLoaded) return;
            LocalLoaded = true;
            if (!IsHost) SendTo(Protocol.HostId, MsgType.Loaded);
        }

        /// <summary>True once the agreed start time has passed. Gates GameManager.Update.</summary>
        public bool IsStartGateOpen()
        {
            if (StartAtHostTime is not double start || HostNow < start) return false;
            if (State == SessionState.Loading) State = SessionState.InSong;
            return true;
        }

        public void SendInput(in GameInput input)
        {
            if (!IsConnected) return;
            var i = input;
            int seq = _sendSeq++;
            _recentInputs.Add((seq, i));
            if (_recentInputs.Count > UDP_REPEATED_INPUTS) _recentInputs.RemoveAt(0);
            _inputsSinceStream = true;
            Broadcast(MsgType.Input, w => { w.Write(seq); w.Write(i.Time); w.Write(i.Action); w.Write(i.Integer); });
        }

        /// <summary>
        /// Called every frame of a song with our engine's time, after it has taken this frame's inputs. Sends the
        /// watermark over TCP, and over UDP together with the latest inputs (again), so the others get them even when a
        /// TCP packet is held up.
        /// </summary>
        public void SendInputStream(double engineTime)
        {
            if (!IsConnected || StartAtHostTime is not double songKey) return;
            if (!_inputsSinceStream && engineTime <= _lastMarkSent) return;
            if (LocalNow < _nextStreamSend) return;
            _nextStreamSend = LocalNow + STREAM_MIN_INTERVAL;
            _inputsSinceStream = false;
            _lastMarkSent = Math.Max(_lastMarkSent, engineTime);

            int lastSeq = _sendSeq - 1;
            double mark = _lastMarkSent;
            Broadcast(MsgType.Watermark, w => { w.Write(lastSeq); w.Write(mark); });

            if (_udp == null) return;
            // Only the newest inputs, and none from long ago: all repeated inputs must be numbered in a row up to lastSeq.
            int first = _recentInputs.Count;
            while (first > 0 && _recentInputs[first - 1].input.Time >= mark - UDP_REPEAT_WINDOW) first--;
            byte me = LocalId;
            _udp.SendAll(UdpChannel.KindInputs, w =>
            {
                w.Write(me);
                w.Write(songKey);
                w.Write(lastSeq);
                w.Write(mark);
                w.Write((byte) (_recentInputs.Count - first));
                for (int k = first; k < _recentInputs.Count; k++)
                {
                    var input = _recentInputs[k].input;
                    w.Write(input.Time);
                    w.Write(input.Action);
                    w.Write(input.Integer);
                }
            });
        }

        private void ResetInputStream()
        {
            _sendSeq = 0;
            _recentInputs.Clear();
            _lastMarkSent = double.NegativeInfinity;
            _nextStreamSend = 0;
            _inputsSinceStream = false;
        }

        public void SendPause(bool paused)
        {
            if (IsConnected) Broadcast(paused ? MsgType.Pause : MsgType.Resume);
        }

        public void SendLeaveSong()
        {
            if (IsConnected) Broadcast(MsgType.LeaveSong);
        }

        /// <summary>
        /// We restarted the song or skipped to <paramref name="song"/> (setlist position <paramref name="showIndex"/>)
        /// from the pause menu. YARG then reloads the scene, which waits at the ready gate for the others to follow.
        /// </summary>
        public void BeginSongChange(SongEntry song, int showIndex, bool skip)
        {
            if (!IsConnected || ChangingSong || State is not (SessionState.Loading or SessionState.InSong)) return;
            ChangingSong = true;
            var hash = song.Hash.HashBytes;
            Broadcast(MsgType.ChangeSong, w =>
            {
                w.Write(showIndex);
                w.WriteHash(hash);
                w.Write(skip);
            });
        }

        private void OnPeerChangedSong(Peer peer, int showIndex, byte[] hash, bool skip)
        {
            // Several pressed at once: each is already waiting at the gate, which settles whether it's the same song.
            if (ChangingSong || GameHooks.GameManager == null || State is not (SessionState.Loading or SessionState.InSong)) return;
            if (!SongLibrary.TryGet(hash, out var song))
            {
                Notify.Warning($"{peer.Name} skipped to a song you don't have.");
                return;
            }

            if (Show.Playing && showIndex >= 0 && showIndex < Show.Songs.Count)
                Show.Index = showIndex;
            GlobalVariables.State.CurrentSong = song;
            ChangingSong = true;

            Notify.Info(skip ? $"{peer.Name} skipped to {song.Name.Original}" : $"{peer.Name} restarted the song");
            NetplayDialogs.CloseLeaveConfirm();
            GlobalVariables.Instance.LoadScene(SceneIndex.Gameplay); // held at the ready gate like our own restart
        }

        /// <summary>
        /// Called when the gameplay scene is unloaded. Only clears the song that was played: a Ready for
        /// the next setlist song may already have arrived from the others and must survive.
        /// </summary>
        public void EndSong()
        {
            // The next song (restart or skip) was already set up before this scene unloaded.
            if (_replacingSong)
            {
                _replacingSong = false;
                return;
            }

            ActiveSetups = null;
            LocalLoaded = false;
            foreach (var peer in Peers)
            {
                peer.Loaded = false;
                peer.Inputs.Clear();
            }
            StartAtHostTime = null;
            ResetInputStream();
            if (State is SessionState.Loading or SessionState.InSong)
            {
                State = IsConnected ? SessionState.Lobby : IsHost && _listener != null ? SessionState.Hosting : SessionState.Idle;
                if (IsHost) UpdateHostStatus();
                else if (IsConnected) Status = $"Connected to {NameOf(Protocol.HostId)}";
            }
        }
    }
}
