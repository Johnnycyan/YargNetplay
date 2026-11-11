using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using YARG;
using YARG.Core.Input;
using YARG.Core.Song;
using YargNetplay.Game;

namespace YargNetplay.Net
{
    internal enum SessionState
    {
        Idle,
        Hosting,    // listening, no peer yet
        Connecting,
        Lobby,
        Loading,    // song chosen, waiting for setups / scene load / start gate
        InSong,
    }

    internal sealed class QueueItem
    {
        public byte[] Hash;
        public string Name;
        public string Artist;
        public bool LocalHas;
        public bool? PeerHas; // null = waiting for the peer's answer
        public bool AddedByMe;
    }

    internal sealed class PeerSetup
    {
        public byte[] ProfileBytes;
        public string EnginePresetJson;
    }

    /// <summary>
    /// One netplay session with exactly one peer. The host's clock is the reference clock.
    /// All public members are main-thread only; network threads only touch <see cref="Connection"/>.
    /// </summary>
    internal sealed class Session : IDisposable
    {
        public static Session Current { get; private set; }

        private const double PING_INTERVAL = 1.0;
        private const double START_LEAD_TIME = 0.75;
        private const int CLOCK_SAMPLES = 16;

        private static readonly double TicksToSeconds = 1.0 / Stopwatch.Frequency;

        private TcpListener _listener;
        private Connection _conn;
        private SongTransfer _transfer;
        private TcpClient _pendingClient; // set by accept/connect threads, picked up on the main thread
        private string _pendingError;
        private double _nextPing;
        private readonly List<(double rtt, double offset)> _clockSamples = new();

        public bool IsHost { get; private set; }
        public SessionState State { get; private set; }
        public string Status { get; private set; } = "Not connected";
        public string LocalName { get; set; } = Environment.UserName;
        public string PeerName { get; private set; }
        public double RttSeconds { get; private set; }

        /// <summary>How far behind the shared song clock the remote player is simulated.</summary>
        public double RemoteDelaySeconds { get; set; } = 0.2;

        public List<QueueItem> Queue { get; } = new();

        // Per-song state
        public PeerSetup PeerSetup { get; private set; }
        public SongEntry PendingSong { get; private set; }
        public bool LocalLoaded { get; private set; }
        public bool PeerLoaded { get; private set; }
        public double? StartAtHostTime { get; private set; }
        public RemoteInputBuffer RemoteInputs { get; } = new();
        private bool _sceneLoadIssued;

        /// <summary>Host clock offset: hostTime = localTime + offset. Always 0 on the host.</summary>
        private double _clockOffset;

        public static double LocalNow => Stopwatch.GetTimestamp() * TicksToSeconds;
        public double HostNow => LocalNow + _clockOffset;

        public bool IsConnected => _conn != null && !_conn.IsClosed;
        public SongTransfer Transfer => _transfer;
        public bool InSongFlow => State is SessionState.Loading or SessionState.InSong;

        // ---------------------------------------------------------------- lifecycle

        public static Session Host(int port)
        {
            Current?.Dispose();
            var s = new Session { IsHost = true };
            Current = s;

            try
            {
                s._listener = new TcpListener(IPAddress.Any, port);
                s._listener.Start(1);
            }
            catch (Exception e)
            {
                s.Fail("Could not listen on port " + port + ": " + e.Message);
                return s;
            }

            s.State = SessionState.Hosting;
            s.Status = $"Hosting on port {port}, waiting for a player...";
            new Thread(() =>
            {
                try { s._pendingClient = s._listener.AcceptTcpClient(); }
                catch (Exception e) { if (s.State == SessionState.Hosting) s._pendingError = e.Message; }
            }) { IsBackground = true, Name = "YargNetplay.Accept" }.Start();
            return s;
        }

        public static Session Join(string host, int port)
        {
            Current?.Dispose();
            var s = new Session { IsHost = false, State = SessionState.Connecting, Status = $"Connecting to {host}:{port}..." };
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
            try { _listener?.Stop(); } catch { /* ignore */ }
            _listener = null;
            _transfer?.Dispose();
            _transfer = null;
            _conn?.Dispose();
            _conn = null;
            State = SessionState.Idle;
            if (Current == this) Current = null;
        }

        private void Fail(string message)
        {
            Plugin.Log.LogWarning(message);
            Status = message;
            try { _listener?.Stop(); } catch { /* ignore */ }
            _listener = null;
            _transfer?.Dispose();
            _transfer = null;
            _conn?.Dispose();
            _conn = null;
            State = SessionState.Idle;
        }

        // ---------------------------------------------------------------- main-thread pump

        public void Update()
        {
            if (_pendingError != null)
            {
                Fail("Connection failed: " + _pendingError);
                _pendingError = null;
                return;
            }

            if (_pendingClient != null)
            {
                var client = _pendingClient;
                _pendingClient = null;
                try { _listener?.Stop(); } catch { /* one peer only */ }
                _listener = null;
                OnConnected(client);
            }

            if (_conn == null) return;

            // Keep song transfers off the wire while a song is loading or playing.
            if (_transfer != null) _transfer.Paused = InSongFlow;

            while (_conn != null && _conn.Inbox.TryDequeue(out var frame))
            {
                try { Handle(frame); }
                catch (Exception e) { Plugin.Log.LogError($"Bad message: {e}"); }
            }

            if (_conn.IsClosed)
            {
                Fail("Disconnected: " + _conn.CloseReason);
                return;
            }

            if (LocalNow >= _nextPing)
            {
                _nextPing = LocalNow + PING_INTERVAL;
                double t = LocalNow;
                _conn.Send(MsgType.Ping, w => w.Write(t));
            }

            // Both setups exchanged: load the gameplay scene.
            if (State == SessionState.Loading && !_sceneLoadIssued && PeerSetup != null && PendingSong != null)
            {
                _sceneLoadIssued = true;
                SongLauncher.LoadGameplay(PendingSong);
            }

            // Host: both sides loaded, schedule the shared start.
            if (IsHost && State == SessionState.Loading && LocalLoaded && PeerLoaded && StartAtHostTime == null)
            {
                double startAt = HostNow + START_LEAD_TIME + RttSeconds;
                StartAtHostTime = startAt;
                _conn.Send(MsgType.StartAt, w => w.Write(startAt));
            }
        }

        private void OnConnected(TcpClient client)
        {
            _conn = new Connection(client);
            _transfer = new SongTransfer(_conn);
            _transfer.SongReceived += OnSongReceived;
            State = SessionState.Lobby;
            Status = "Connected to " + _conn.RemoteEndPoint + ", saying hello...";

            _conn.Send(MsgType.Hello, w =>
            {
                w.Write(Protocol.Version);
                w.Write(GameInfo.YargVersion);
                w.Write(Plugin.Version);
                w.Write(LocalName ?? "Player");
            });
        }

        // ---------------------------------------------------------------- incoming

        private void Handle(byte[] frame)
        {
            using var r = new BinaryReader(new MemoryStream(frame));
            var type = (MsgType) r.ReadByte();

            switch (type)
            {
                case MsgType.Hello:
                {
                    int protocol = r.ReadInt32();
                    string yargVersion = r.ReadString();
                    string modVersion = r.ReadString();
                    PeerName = r.ReadString();

                    if (protocol != Protocol.Version)
                    {
                        _conn.Close($"Protocol mismatch (you {Protocol.Version}, peer {protocol}, mod {modVersion})");
                        return;
                    }

                    // Engines must match exactly or the remote simulation drifts.
                    Status = yargVersion == GameInfo.YargVersion
                        ? $"Connected to {PeerName}"
                        : $"Connected to {PeerName} - WARNING: YARG version differs ({GameInfo.YargVersion} vs {yargVersion})";

                    // Re-announce our queue so a reconnect shows both sides' songs.
                    foreach (var item in Queue.Where(q => q.AddedByMe))
                        SendQueueAdd(item);
                    break;
                }

                case MsgType.Ping:
                {
                    double t = r.ReadDouble();
                    double now = LocalNow;
                    _conn.Send(MsgType.Pong, w => { w.Write(t); w.Write(now); });
                    break;
                }

                case MsgType.Pong:
                {
                    double sent = r.ReadDouble();
                    double peerTime = r.ReadDouble();
                    double now = LocalNow;
                    double rtt = now - sent;

                    _clockSamples.Add((rtt, peerTime - (sent + rtt / 2)));
                    if (_clockSamples.Count > CLOCK_SAMPLES) _clockSamples.RemoveAt(0);

                    // The lowest-RTT sample has the least queuing noise.
                    var best = _clockSamples.OrderBy(s => s.rtt).First();
                    RttSeconds = best.rtt;
                    if (!IsHost) _clockOffset = best.offset;
                    break;
                }

                case MsgType.QueueAdd:
                {
                    var hash = r.ReadHash();
                    string name = r.ReadString();
                    string artist = r.ReadString();

                    var item = FindQueueItem(hash);
                    if (item == null)
                    {
                        item = new QueueItem { Hash = hash, Name = name, Artist = artist };
                        Queue.Add(item);
                    }
                    item.PeerHas = true;
                    item.LocalHas = SongLibrary.TryGet(hash, out _);

                    bool has = item.LocalHas;
                    _conn.Send(MsgType.Availability, w => { w.WriteHash(hash); w.Write(has); });

                    // Missing it: have the peer send it straight away.
                    if (!has) _transfer.Request(hash);
                    break;
                }

                case MsgType.SongRequest:
                    _transfer.OnRequest(r.ReadHash());
                    break;

                case MsgType.SongBegin:
                    _transfer.OnBegin(r);
                    break;

                case MsgType.SongChunk:
                    _transfer.OnChunk(r);
                    break;

                case MsgType.SongEnd:
                    _transfer.OnEnd(r);
                    break;

                case MsgType.SongFail:
                    _transfer.OnFail(r);
                    break;

                case MsgType.QueueRemove:
                {
                    var item = FindQueueItem(r.ReadHash());
                    if (item != null) Queue.Remove(item);
                    break;
                }

                case MsgType.Availability:
                {
                    var item = FindQueueItem(r.ReadHash());
                    bool has = r.ReadBoolean();
                    if (item != null) item.PeerHas = has;
                    break;
                }

                case MsgType.PlayerSetup:
                {
                    int len = r.ReadInt32();
                    PeerSetup = new PeerSetup
                    {
                        ProfileBytes = r.ReadBytes(len),
                        EnginePresetJson = r.ReadString(),
                    };
                    break;
                }

                case MsgType.LoadSong:
                {
                    var hash = r.ReadHash();
                    if (!SongLibrary.TryGet(hash, out var song))
                    {
                        Status = "Host started a song you don't have yet (still transferring?)";
                        _conn.Send(MsgType.LeaveSong);
                        return;
                    }
                    BeginSong(song);
                    break;
                }

                case MsgType.Loaded:
                    PeerLoaded = true;
                    break;

                case MsgType.StartAt:
                    StartAtHostTime = r.ReadDouble();
                    break;

                case MsgType.Input:
                {
                    double time = r.ReadDouble();
                    int action = r.ReadInt32();
                    int value = r.ReadInt32();
                    RemoteInputs.Enqueue(new GameInput(time, action, value));
                    break;
                }

                case MsgType.Pause:
                    GameHooks.ApplyRemotePause(true);
                    break;

                case MsgType.Resume:
                    GameHooks.ApplyRemotePause(false);
                    break;

                case MsgType.LeaveSong:
                    Status = $"{PeerName} left the song";
                    if (State == SessionState.Loading)
                        EndSong();
                    break;
            }
        }

        // ---------------------------------------------------------------- queue

        private QueueItem FindQueueItem(byte[] hash) => Queue.FirstOrDefault(q => q.Hash.AsSpan().SequenceEqual(hash));

        public void AddToQueue(SongEntry song)
        {
            var hash = song.Hash.HashBytes;
            if (FindQueueItem(hash) != null) return;

            var item = new QueueItem
            {
                Hash = hash,
                Name = song.Name.Original,
                Artist = song.Artist.Original,
                LocalHas = true,
                AddedByMe = true,
            };
            Queue.Add(item);
            if (IsConnected) SendQueueAdd(item);
        }

        public void RemoveFromQueue(QueueItem item)
        {
            Queue.Remove(item);
            if (IsConnected) _conn.Send(MsgType.QueueRemove, w => w.WriteHash(item.Hash));
        }

        public void RetryTransfer(QueueItem item)
        {
            if (IsConnected && !item.LocalHas && item.PeerHas == true) _transfer.Request(item.Hash);
        }

        private void OnSongReceived(byte[] hash)
        {
            var item = FindQueueItem(hash);
            if (item != null) item.LocalHas = true;
            _conn?.Send(MsgType.Availability, w => { w.WriteHash(hash); w.Write(true); });
        }

        private void SendQueueAdd(QueueItem item)
        {
            _conn.Send(MsgType.QueueAdd, w =>
            {
                w.WriteHash(item.Hash);
                w.Write(item.Name ?? "");
                w.Write(item.Artist ?? "");
            });
        }

        // ---------------------------------------------------------------- song flow

        /// <summary>Host only: start a queued song that both players have.</summary>
        public bool TryStartSong(QueueItem item, out string error)
        {
            error = null;
            if (!IsHost) { error = "Only the host starts songs"; return false; }
            if (State != SessionState.Lobby) { error = "Not in the lobby"; return false; }
            if (item.PeerHas != true) { error = "Peer doesn't have this song yet (wait for the transfer to finish)"; return false; }
            if (!SongLibrary.TryGet(item.Hash, out var song)) { error = "Song not in your library"; return false; }

            // Order matters: the client must receive our setup before LoadSong.
            if (!BeginSong(song)) { error = Status; return false; }
            _conn.Send(MsgType.LoadSong, w => w.WriteHash(item.Hash));
            return true;
        }

        private bool BeginSong(SongEntry song)
        {
            if (!SongLauncher.TryPrepareLocalPlayer(song, out var setup, out var error))
            {
                Status = error;
                if (!IsHost) _conn.Send(MsgType.LeaveSong); // tell the waiting host
                return false;
            }

            LocalLoaded = false;
            PeerLoaded = false;
            StartAtHostTime = null;
            _sceneLoadIssued = false;
            RemoteInputs.Clear();

            PendingSong = song;
            State = SessionState.Loading;
            Status = $"Loading {song.Name.Original}...";

            _conn.Send(MsgType.PlayerSetup, w =>
            {
                w.Write(setup.ProfileBytes.Length);
                w.Write(setup.ProfileBytes);
                w.Write(setup.EnginePresetJson);
            });
            return true;
        }

        /// <summary>Called by the start-gate patch once our gameplay scene has finished loading.</summary>
        public void MarkLocalLoaded()
        {
            if (LocalLoaded) return;
            LocalLoaded = true;
            _conn?.Send(MsgType.Loaded);
        }

        /// <summary>True once the agreed start time has passed. Gates GameManager.Update.</summary>
        public bool IsStartGateOpen()
        {
            if (StartAtHostTime is not double start || HostNow < start) return false;
            if (State == SessionState.Loading)
            {
                State = SessionState.InSong;
                Status = $"Playing with {PeerName}";
            }
            return true;
        }

        public void SendInput(in GameInput input)
        {
            if (!IsConnected) return;
            var i = input;
            _conn.Send(MsgType.Input, w => { w.Write(i.Time); w.Write(i.Action); w.Write(i.Integer); });
        }

        public void SendPause(bool paused) => _conn?.Send(paused ? MsgType.Pause : MsgType.Resume);

        public void SendLeaveSong() => _conn?.Send(MsgType.LeaveSong);

        /// <summary>Called when the gameplay scene is left, for any reason.</summary>
        public void EndSong()
        {
            PeerSetup = null;
            PendingSong = null;
            LocalLoaded = false;
            PeerLoaded = false;
            StartAtHostTime = null;
            _sceneLoadIssued = false;
            RemoteInputs.Clear();
            if (State is SessionState.Loading or SessionState.InSong)
            {
                State = IsConnected ? SessionState.Lobby : SessionState.Idle;
                if (IsConnected) Status = $"Connected to {PeerName}";
            }
        }
    }
}
