using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using YARG.Core.Song;
using YargNetplay.Game;

namespace YargNetplay.Net
{
    internal sealed class TransferStatus
    {
        public bool Outgoing;
        public string PeerName; // who it comes from or goes to
        public volatile float Progress; // 0..1
        public volatile string Error;
        public volatile bool Done;
    }

    /// <summary>
    /// Sends songs other players are missing and receives songs we are missing, over the session connections.
    /// Loose folders are packed to .sng first; .sng files are sent as-is. Received files go to
    /// <see cref="ReceivedSongs.Folder"/>, never into the player's song library.
    /// Outgoing data is produced on a worker thread and paused while a song is being played,
    /// so it never competes with gameplay inputs on the same TCP stream.
    /// Several songs can come in at once (from different players); each is kept apart by its hash.
    /// </summary>
    internal sealed class SongTransfer : IDisposable
    {
        private const int CHUNK_SIZE = 256 * 1024;

        public delegate void Sender(byte to, MsgType type, Action<BinaryWriter> writeBody);

        private readonly Sender _send;
        private readonly Func<byte, bool> _isConnected;
        private readonly Func<byte, string> _nameOf;
        private readonly BlockingCollection<(byte[] Hash, SongEntry Song, byte To)> _outgoing = new();
        private readonly Thread _worker;
        private volatile bool _disposed;

        private sealed class Incoming
        {
            public byte From;
            public FileStream Stream;
            public string Path, Name, Artist, Ext;
            public long Length;
        }

        private readonly Dictionary<string, Incoming> _incoming = new();

        /// <summary>Incoming songs keyed by hex hash, outgoing ones by hex hash and receiver. Read by the UI.</summary>
        public ConcurrentDictionary<string, TransferStatus> Status { get; } = new();

        /// <summary>Set by the session while a song is loading or playing.</summary>
        public volatile bool Paused;

        /// <summary>Raised on the main thread when a received song is stored and playable.</summary>
        public event Action<byte[]> SongReceived;

        public SongTransfer(Sender send, Func<byte, bool> isConnected, Func<byte, string> nameOf)
        {
            _send = send;
            _isConnected = isConnected;
            _nameOf = nameOf;
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "YargNetplay.SongSend" };
            _worker.Start();
        }

        public static string Key(byte[] hash) => BitConverter.ToString(hash).Replace("-", "");

        private static string OutgoingKey(byte[] hash, byte to) => Key(hash) + ">" + to;

        /// <summary>This song's transfer to us, or else one of ours to another player (an unfinished one first).</summary>
        public TransferStatus GetStatus(byte[] hash)
        {
            string key = Key(hash);
            if (Status.TryGetValue(key, out var incoming)) return incoming;
            TransferStatus any = null;
            foreach (var pair in Status)
            {
                if (!pair.Key.StartsWith(key + ">")) continue;
                if (!pair.Value.Done && pair.Value.Error == null) return pair.Value;
                any ??= pair.Value;
            }
            return any;
        }

        /// <summary>Whether this song is being received, or was received, or failed (null: never asked for).</summary>
        public TransferStatus GetIncoming(byte[] hash) => Status.TryGetValue(Key(hash), out var s) ? s : null;

        /// <summary>True while any song is being requested, sent or received.</summary>
        public bool Active => Status.Values.Any(s => !s.Done && s.Error == null);

        // ------------------------------------------------------------ requesting (receiver side, main thread)

        public void Request(byte[] hash, byte from)
        {
            var existing = GetIncoming(hash);
            if (existing != null && existing.Error == null) return; // in flight or done

            Status[Key(hash)] = new TransferStatus { Outgoing = false, PeerName = _nameOf(from) };
            _send(from, MsgType.SongRequest, w => w.WriteHash(hash));
        }

        // ------------------------------------------------------------ sending (main thread enqueues, worker sends)

        public void OnRequest(byte[] hash, byte from)
        {
            if (!SongLibrary.TryGet(hash, out var song))
            {
                SendFail(from, hash, "Song is no longer in the sender's library");
                return;
            }
            if (song.SubType is not (EntryType.Ini or EntryType.Sng))
            {
                SendFail(from, hash, $"{song.SubType} songs can't be sent yet");
                return;
            }

            Status[OutgoingKey(hash, from)] = new TransferStatus { Outgoing = true, PeerName = _nameOf(from) };
            _outgoing.Add((hash, song, from));
        }

        private void WorkerLoop()
        {
            try
            {
                foreach (var job in _outgoing.GetConsumingEnumerable())
                {
                    if (_disposed) return;
                    SendSong(job.Hash, job.Song, job.To);
                }
            }
            catch (ObjectDisposedException) { /* shutting down */ }
        }

        private void SendSong(byte[] hash, SongEntry song, byte to)
        {
            var status = Status[OutgoingKey(hash, to)];
            string tempFile = null;
            try
            {
                if (!_isConnected(to)) throw new IOException($"{status.PeerName} left");

                string path, ext;
                if (song.SubType == EntryType.Sng)
                {
                    path = song.ActualLocation;
                    ext = Path.GetExtension(path);
                }
                else
                {
                    tempFile = Path.Combine(Path.GetTempPath(), $"yargnetplay-{Guid.NewGuid():N}.sng");
                    SngPackager.Pack(song.ActualLocation, tempFile);
                    path = tempFile;
                    ext = ".sng";
                }

                using var input = File.OpenRead(path);
                long length = input.Length;
                string name = song.Name.Original, artist = song.Artist.Original;

                _send(to, MsgType.SongBegin, w =>
                {
                    w.WriteHash(hash);
                    w.Write(length);
                    w.Write(ext);
                    w.Write(name ?? "");
                    w.Write(artist ?? "");
                });

                var buffer = new byte[CHUNK_SIZE];
                long offset = 0;
                while (offset < length)
                {
                    while (Paused && !_disposed && _isConnected(to)) Thread.Sleep(100);
                    if (_disposed) return;
                    if (!_isConnected(to)) throw new IOException($"{status.PeerName} left");

                    int read = input.Read(buffer, 0, buffer.Length);
                    if (read <= 0) throw new EndOfStreamException();

                    long chunkOffset = offset;
                    _send(to, MsgType.SongChunk, w =>
                    {
                        w.WriteHash(hash);
                        w.Write(chunkOffset);
                        w.Write(read);
                        w.Write(buffer, 0, read);
                    });
                    offset += read;
                    status.Progress = (float) offset / length;
                }

                _send(to, MsgType.SongEnd, w => w.WriteHash(hash));
                status.Done = true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Sending {song.Name.Original} to {status.PeerName} failed: {e}");
                status.Error = e.Message;
                SendFail(to, hash, "Sender error: " + e.Message);
            }
            finally
            {
                if (tempFile != null) try { File.Delete(tempFile); } catch { /* temp dir */ }
            }
        }

        private void SendFail(byte to, byte[] hash, string reason) =>
            _send(to, MsgType.SongFail, w => { w.WriteHash(hash); w.Write(reason); });

        // ------------------------------------------------------------ receiving (main thread)

        public void OnBegin(BinaryReader r, byte from)
        {
            var hash = r.ReadHash();
            long length = r.ReadInt64();
            string ext = r.ReadString();
            string name = r.ReadString();
            string artist = r.ReadString();

            string key = Key(hash);
            CloseIncoming(key, deletePartial: true);
            if (!ReceivedSongs.IsSongFile("x" + ext))
            {
                Fail(hash, $"Unexpected file type {ext}");
                return;
            }

            var incoming = new Incoming
            {
                From = from,
                Length = length,
                Ext = ext,
                Name = name,
                Artist = artist,
                Path = System.IO.Path.Combine(ReceivedSongs.PartialFolder, key + ".part"),
            };
            incoming.Stream = new FileStream(incoming.Path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            _incoming[key] = incoming;

            Status[key] = new TransferStatus { Outgoing = false, PeerName = _nameOf(from) };
        }

        public void OnChunk(BinaryReader r, byte from)
        {
            var hash = r.ReadHash();
            long offset = r.ReadInt64();
            int count = r.ReadInt32();
            var data = r.ReadBytes(count);

            string key = Key(hash);
            if (!_incoming.TryGetValue(key, out var incoming) || incoming.From != from || offset != incoming.Stream.Position)
            {
                Fail(hash, "Transfer out of order");
                CloseIncoming(key, deletePartial: true);
                return;
            }

            incoming.Stream.Write(data, 0, data.Length);
            var status = GetIncoming(hash);
            if (status != null) status.Progress = incoming.Length > 0 ? (float) incoming.Stream.Position / incoming.Length : 1f;
        }

        public void OnEnd(BinaryReader r, byte from)
        {
            var hash = r.ReadHash();
            string key = Key(hash);
            if (!_incoming.TryGetValue(key, out var incoming) || incoming.From != from) return;

            bool complete = incoming.Stream.Position == incoming.Length;
            CloseIncoming(key, deletePartial: false);

            if (!complete)
            {
                Fail(hash, "Transfer incomplete");
                TryDelete(incoming.Path);
                return;
            }

            string final = ReceivedSongs.FinalPathFor(incoming.Name, incoming.Artist, hash, incoming.Ext);
            try
            {
                if (File.Exists(final)) File.Delete(final);
                File.Move(incoming.Path, final);
            }
            catch (Exception e)
            {
                Fail(hash, "Could not store song: " + e.Message);
                TryDelete(incoming.Path);
                return;
            }

            if (!ReceivedSongs.TryRegister(final, hash, out _, out var error))
            {
                Fail(hash, "Received song is unusable: " + error);
                TryDelete(final);
                return;
            }

            ReceivedSongs.RefreshSize();
            var status = GetIncoming(hash);
            if (status != null) { status.Progress = 1f; status.Done = true; }
            SongReceived?.Invoke(hash);
        }

        public void OnFail(BinaryReader r, byte from)
        {
            var hash = r.ReadHash();
            string reason = r.ReadString();
            // Only the player we asked can fail our download (not an old request to someone else).
            var status = GetIncoming(hash);
            if (status == null || status.Done) return;
            string key = Key(hash);
            if (_incoming.TryGetValue(key, out var incoming) && incoming.From != from) return;
            Fail(hash, reason);
            CloseIncoming(key, deletePartial: true);
        }

        /// <summary>A player left: songs coming from them won't arrive. Sends to them stop by themselves.</summary>
        public void OnPeerLeft(byte id, string name)
        {
            foreach (var pair in _incoming.Where(p => p.Value.From == id).ToList())
            {
                CloseIncoming(pair.Key, deletePartial: true);
                if (Status.TryGetValue(pair.Key, out var status) && !status.Done) status.Error = $"{name} left";
            }
            // Requested from them but not started yet.
            foreach (var status in Status.Values)
                if (!status.Outgoing && !status.Done && status.Error == null && status.PeerName == name && status.Progress == 0)
                    status.Error = $"{name} left";
        }

        private void Fail(byte[] hash, string reason)
        {
            Plugin.Log.LogWarning($"Song transfer {Key(hash)} failed: {reason}");
            var status = Status.GetOrAdd(Key(hash), _ => new TransferStatus());
            status.Error = reason;
        }

        private void CloseIncoming(string key, bool deletePartial)
        {
            if (!_incoming.TryGetValue(key, out var incoming)) return;
            _incoming.Remove(key);
            incoming.Stream.Dispose();
            if (deletePartial) TryDelete(incoming.Path);
        }

        private static void TryDelete(string path)
        {
            try { if (path != null && File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }

        public void Dispose()
        {
            _disposed = true;
            _outgoing.CompleteAdding();
            foreach (var key in _incoming.Keys.ToList()) CloseIncoming(key, deletePartial: true);
        }
    }
}
