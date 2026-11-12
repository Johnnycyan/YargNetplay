using System;
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
        Hosting,    // listening, no peer yet
        Connecting,
        Lobby,
        Waiting,    // we confirmed our difficulty and are waiting for the peer to be ready for the same song
        Loading,    // both ready: gameplay scene loading / waiting at the start gate
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
    ///
    /// Song flow: either player opens difficulty select (single song or setlist) and the other follows
    /// (<see cref="MsgType.OpenSetlist"/>). Every load of the gameplay scene is held at
    /// <see cref="TryPassGameplayGate"/> until both players are Ready for the same song, so setlist
    /// continues and score-screen restarts work the same way as the first song.
    /// </summary>
    internal sealed class Session : IDisposable
    {
        public static Session Current { get; private set; }

        private const double PING_INTERVAL = 1.0;
        private const double START_LEAD_TIME = 0.75;
        private const int CLOCK_SAMPLES = 16;
        private const int RTT_HISTORY = 30;
        private const double AUTO_DELAY_MARGIN = 0.040;
        private const double AUTO_DELAY_MIN_MS = 50, AUTO_DELAY_MAX_MS = 1000;

        private static readonly double TicksToSeconds = 1.0 / Stopwatch.Frequency;

        private TcpListener _listener;
        private Connection _conn;
        private SongTransfer _transfer;
        private TcpClient _pendingClient; // set by accept/connect threads, picked up on the main thread
        private string _pendingError;
        private double _nextPing;
        private readonly List<(double rtt, double offset)> _clockSamples = new();
        private readonly Queue<double> _rttHistory = new();

        public bool IsHost { get; private set; }

        /// <summary>The address the player typed or picked to join (null when hosting).</summary>
        public string JoinAddress { get; private set; }

        /// <summary>Ready toggles on the Online page. The host sends everyone to song select once the peer is ready.</summary>
        public bool LocalLobbyReady { get; private set; }
        public bool PeerLobbyReady { get; private set; }
        public SessionState State { get; private set; }
        public string Status { get; private set; } = "Not connected";
        public string LocalName { get; set; } = Environment.UserName;
        public string PeerName { get; private set; } = "your partner";
        public double RttSeconds { get; private set; }

        /// <summary>
        /// Delay picked from the network: half the worst round trip of the last <see cref="RTT_HISTORY"/> pings
        /// (one second apart), plus a margin for frame timing and jitter, rounded up to 10 ms.
        /// </summary>
        public double AutoDelaySeconds
        {
            get
            {
                double worstRtt = _rttHistory.Count > 0 ? _rttHistory.Max() : RttSeconds;
                double ms = Math.Ceiling((worstRtt / 2 + AUTO_DELAY_MARGIN) * 100) * 10;
                return Math.Max(AUTO_DELAY_MIN_MS, Math.Min(AUTO_DELAY_MAX_MS, ms)) / 1000.0;
            }
        }

        /// <summary>How far behind the shared song clock the remote player is simulated. Fixed for each song when it loads.</summary>
        public double RemoteDelaySeconds => Plugin.AutoDelay.Value ? AutoDelaySeconds : Plugin.RemoteDelayMs.Value / 1000.0;

        /// <summary>The shared setlist. Mirrored into YARG's own setlist by <see cref="NativeSetlist"/>.</summary>
        public List<QueueItem> Queue { get; } = new();

        // Ready handshake
        private byte[] _localReadyHash;
        private byte[] _peerReadyHash;
        private PeerSetup _peerReadySetup;
        private bool _gateOpen;
        private bool _undoShowAdvanceOnCancel;

        // Current song
        public PeerSetup ActivePeerSetup { get; private set; }
        public bool LocalLoaded { get; private set; }
        public bool PeerLoaded { get; private set; }
        public double? StartAtHostTime { get; private set; }
        public RemoteInputBuffer RemoteInputs { get; } = new();

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

        private void Shutdown()
        {
            try { _listener?.Stop(); } catch { /* ignore */ }
            _listener = null;
            _transfer?.Dispose();
            _transfer = null;
            _conn?.Dispose();
            _conn = null;
            State = SessionState.Idle;
            ResetReady();
            ResetLobbyReady();
            NetplayDialogs.CloseWaiting();
            ReceivedLibrary.SetActive(false);
            PeerPart = default;
            PeerInSongSelect = false;
            PartFilter.Refresh();
        }

        // ---------------------------------------------------------------- main-thread pump

        public void Update()
        {
            if (_pendingError != null)
            {
                var error = _pendingError;
                _pendingError = null;
                Fail("Connection failed: " + error);
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

            if (_conn == null) return;
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

            CheckReady();

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
            ReceivedLibrary.SetActive(true);

            _conn.Send(MsgType.Hello, w =>
            {
                w.Write(Protocol.Version);
                w.Write(GameInfo.YargVersion);
                w.Write(Plugin.Version);
                w.Write(LocalName ?? "Player");
            });
            SendPartChoice();
            SendSongSelectState(NativeMenus.InSongSelect);
            PartFilter.ApplyToProfile(PartFilter.LocalPart);
        }

        // ---------------------------------------------------------------- song select presence

        /// <summary>Whether the peer is in song select, so we can join them there without them pulling us in.</summary>
        public bool PeerInSongSelect { get; private set; }

        public void SendSongSelectState(bool inSongSelect)
        {
            if (IsConnected) _conn.Send(MsgType.SongSelectState, w => w.Write(inSongSelect));
        }

        // ---------------------------------------------------------------- preferred parts (library filter)

        /// <summary>The peer's preferred part (Any until they tell us). Ours is <see cref="PartFilter.LocalPart"/>.</summary>
        public PartChoice PeerPart { get; private set; }

        public void SendPartChoice()
        {
            var part = PartFilter.LocalPart;
            if (IsConnected) _conn.Send(MsgType.PartChoice, part.Write);
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
                        _conn.Close($"Netplay version mismatch (you {Protocol.Version}, {PeerName} {protocol}, mod {modVersion})");
                        return;
                    }

                    if (!IsHost) SavedServers.RememberJoin(JoinAddress, PeerName);

                    // Engines must match exactly or the remote simulation drifts.
                    if (yargVersion == GameInfo.YargVersion)
                    {
                        Status = $"Connected to {PeerName}";
                        Notify.Success(IsHost ? $"{PeerName} joined your game" : $"Connected to {PeerName}");
                    }
                    else
                    {
                        Status = $"Connected to {PeerName} - WARNING: YARG version differs ({GameInfo.YargVersion} vs {yargVersion})";
                        Notify.Warning($"Connected to {PeerName}, but they run YARG {yargVersion} (you: {GameInfo.YargVersion}). Scores may not match.");
                    }

                    // Re-announce our setlist so a reconnect shows both sides' songs, and share any
                    // setlist that was built before connecting.
                    foreach (var item in Queue.Where(q => q.AddedByMe))
                        SendQueueAdd(item);
                    NativeSetlist.ResyncIfOpen();
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
                    _rttHistory.Enqueue(rtt);
                    if (_rttHistory.Count > RTT_HISTORY) _rttHistory.Dequeue();

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
                    item.LocalHas = SongLibrary.TryGet(hash, out var song);

                    bool has = item.LocalHas;
                    _conn.Send(MsgType.Availability, w => { w.WriteHash(hash); w.Write(has); });

                    // The open setlist overlay already shows the new song, who added it and its download state.
                    bool announce = !SetlistOverlay.ListShowing;
                    if (has)
                    {
                        NativeSetlist.Add(song);
                        if (announce) Notify.Info($"{PeerName} added {name} to the setlist");
                    }
                    else
                    {
                        // Missing it: have the peer send it straight away.
                        _transfer.Request(hash);
                        if (announce) Notify.Info($"{PeerName} added {name}. You don't have it, downloading...");
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

                case MsgType.Availability:
                {
                    var item = FindQueueItem(r.ReadHash());
                    bool has = r.ReadBoolean();
                    if (item != null) item.PeerHas = has;
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

                case MsgType.OpenSetlist:
                {
                    bool show = r.ReadBoolean();
                    int count = r.ReadInt32();
                    var hashes = new List<byte[]>(count);
                    for (int i = 0; i < count; i++) hashes.Add(r.ReadHash());
                    OnPeerOpenedSetlist(show, hashes);
                    break;
                }

                case MsgType.Ready:
                {
                    _peerReadyHash = r.ReadHash();
                    int len = r.ReadInt32();
                    _peerReadySetup = new PeerSetup
                    {
                        ProfileBytes = r.ReadBytes(len),
                        EnginePresetJson = r.ReadString(),
                    };
                    break;
                }

                case MsgType.Unready:
                    _peerReadyHash = null;
                    _peerReadySetup = null;
                    if (State == SessionState.Waiting)
                        Notify.Warning($"{PeerName} backed out");
                    break;

                case MsgType.LeaveSetlist:
                    OnPeerLeftSetlist();
                    break;

                case MsgType.PartChoice:
                    PeerPart = PartChoice.Read(r);
                    PartFilter.Refresh();
                    break;

                case MsgType.SongSelectState:
                    PeerInSongSelect = r.ReadBoolean();
                    break;

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
                    Notify.Warning(GameHooks.ApplyRemoteLeave()
                        ? $"{PeerName} left the song. Back to the library."
                        : $"{PeerName} left the song");
                    break;

                case MsgType.LobbyReady:
                    PeerLobbyReady = r.ReadBoolean();
                    if (IsHost && PeerLobbyReady) Notify.Info($"{PeerName} is ready");
                    break;

                case MsgType.GoToSongSelect:
                    ResetLobbyReady();
                    NativeMenus.OpenLibrary();
                    Notify.Info($"{PeerName} opened song select. Add songs to the setlist or pick one.");
                    break;
            }
        }

        // ---------------------------------------------------------------- lobby

        public void SetLobbyReady(bool ready)
        {
            if (!IsConnected || LocalLobbyReady == ready) return;
            LocalLobbyReady = ready;
            _conn.Send(MsgType.LobbyReady, w => w.Write(ready));
        }

        /// <summary>Host: send both players to song select (Quickplay).</summary>
        public void SendEveryoneToSongSelect()
        {
            if (!IsConnected || !IsHost) return;
            _conn.Send(MsgType.GoToSongSelect);
            ResetLobbyReady();
            NativeMenus.OpenLibrary();
        }

        private void ResetLobbyReady()
        {
            LocalLobbyReady = false;
            PeerLobbyReady = false;
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
            if (IsConnected) _conn.Send(MsgType.QueueRemove, w => w.WriteHash(hash));
        }

        public void ClearQueue()
        {
            if (Queue.Count == 0) return;
            Queue.Clear();
            if (IsConnected) _conn.Send(MsgType.QueueClear);
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

            ReceivedLibrary.Refresh();
            if (SongLibrary.TryGet(hash, out var song))
            {
                if (item != null) NativeSetlist.Add(song);
                // The open setlist overlay already shows the song turning Ready.
                if (!SetlistOverlay.ListShowing) Notify.Success($"Downloaded {song.Name.Original}");
            }
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

        // ---------------------------------------------------------------- following each other into difficulty select

        /// <summary>We opened difficulty select for these songs; bring the peer along.</summary>
        public void SendOpenSetlist(IReadOnlyList<SongEntry> songs, bool show)
        {
            if (!IsConnected || State is not (SessionState.Lobby or SessionState.Waiting)) return;

            _conn.Send(MsgType.OpenSetlist, w =>
            {
                w.Write(show);
                w.Write(songs.Count);
                foreach (var s in songs) w.WriteHash(s.Hash.HashBytes);
            });
        }

        /// <summary>We backed out of difficulty select to the library; take the peer back too.</summary>
        public void SendLeaveSetlist()
        {
            if (!IsConnected || State is not (SessionState.Lobby or SessionState.Waiting)) return;
            CancelReady();
            _conn.Send(MsgType.LeaveSetlist);
        }

        private void OnPeerLeftSetlist()
        {
            CancelReady();
            _peerReadyHash = null;
            _peerReadySetup = null;
            if (NativeMenus.FollowOutOfDifficultySelect())
                Notify.Info($"{PeerName} went back to the song list");
        }

        private void OnPeerOpenedSetlist(bool show, List<byte[]> hashes)
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
                    _transfer.Request(hash);
                    Notify.Warning($"{PeerName} picked a song you don't have yet. Downloading it; try again when it finishes.");
                    return;
                }
            }

            NativeMenus.FollowIntoDifficultySelect(songs, show);
        }

        // ---------------------------------------------------------------- ready gate

        /// <summary>
        /// Called (via a patch) whenever the game wants to load the gameplay scene.
        /// Returns true when the load may proceed; otherwise we send Ready and wait for the peer.
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
                Notify.Warning("Restarting or skipping mid-song isn't available online yet.");
                return false;
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

            // The score screen's Continue advances ShowIndex before loading; undo that if we end up cancelling,
            // or pressing Continue again would skip a song.
            _undoShowAdvanceOnCancel = GlobalVariables.State.PlayingAShow
                && SceneManager.GetActiveScene().buildIndex == (int) SceneIndex.Score;

            _localReadyHash = song.Hash.HashBytes;
            State = SessionState.Waiting;
            _conn.Send(MsgType.Ready, w =>
            {
                w.WriteHash(_localReadyHash);
                w.Write(setup.ProfileBytes.Length);
                w.Write(setup.ProfileBytes);
                w.Write(setup.EnginePresetJson);
            });

            NetplayDialogs.ShowWaiting(PeerName, song.Name.Original, CancelReady);
            CheckReady();
            return false;
        }

        public void CancelReady()
        {
            if (State != SessionState.Waiting) return;
            _localReadyHash = null;
            State = SessionState.Lobby;
            _conn?.Send(MsgType.Unready);
            NetplayDialogs.CloseWaiting();

            if (_undoShowAdvanceOnCancel && GlobalVariables.State.ShowIndex > 0)
            {
                GlobalVariables.State.ShowIndex--;
                GlobalVariables.State.CurrentSong = GlobalVariables.State.ShowSongs[GlobalVariables.State.ShowIndex];
            }
            _undoShowAdvanceOnCancel = false;
        }

        private void CheckReady()
        {
            if (State != SessionState.Waiting || _localReadyHash == null || _peerReadyHash == null) return;

            if (!_localReadyHash.AsSpan().SequenceEqual(_peerReadyHash))
            {
                string theirs = SongLibrary.TryGet(_peerReadyHash, out var s) ? s.Name.Original : "a different song";
                NetplayDialogs.CloseWaiting();
                NetplayDialogs.ShowError($"{PeerName} is ready for {theirs}. Pick the same song to play together.");
                CancelReady();
                _peerReadyHash = null;
                _peerReadySetup = null;
                return;
            }

            // A setlist has really started once its first song loads (not when difficulty select opens, which
            // can be backed out of). The next setlist is built from scratch, like in single player: the menu
            // scene's own setlist comes back empty too.
            if (GlobalVariables.State.PlayingAShow && GlobalVariables.State.ShowIndex == 0) Queue.Clear();

            // Both ready for the same song: this setup drives the remote player for this song.
            ActivePeerSetup = _peerReadySetup;
            ResetReady();
            LocalLoaded = false;
            PeerLoaded = false;
            StartAtHostTime = null;
            RemoteInputs.Clear();
            State = SessionState.Loading;
            Status = $"Playing with {PeerName}";

            NetplayDialogs.CloseWaiting();
            _gateOpen = true;
            GlobalVariables.Instance.LoadScene(SceneIndex.Gameplay);
        }

        private void ResetReady()
        {
            _localReadyHash = null;
            _peerReadyHash = null;
            _peerReadySetup = null;
            _gateOpen = false;
            _undoShowAdvanceOnCancel = false;
        }

        // ---------------------------------------------------------------- in song

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
            if (State == SessionState.Loading) State = SessionState.InSong;
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

        /// <summary>
        /// Called when the gameplay scene is unloaded. Only clears the song that was played: a Ready for
        /// the next setlist song may already have arrived from the peer and must survive.
        /// </summary>
        public void EndSong()
        {
            ActivePeerSetup = null;
            LocalLoaded = false;
            PeerLoaded = false;
            StartAtHostTime = null;
            RemoteInputs.Clear();
            if (State is SessionState.Loading or SessionState.InSong)
            {
                State = IsConnected ? SessionState.Lobby : SessionState.Idle;
                if (IsConnected) Status = $"Connected to {PeerName}";
            }
        }
    }
}
