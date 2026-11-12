using System;
using System.Collections.Concurrent;
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
        public volatile float Progress; // 0..1
        public volatile string Error;
        public volatile bool Done;
    }

    /// <summary>
    /// Sends songs the peer is missing and receives songs we are missing, over the session connection.
    /// Loose folders are packed to .sng first; .sng files are sent as-is. Received files go to
    /// <see cref="ReceivedSongs.Folder"/>, never into the player's song library.
    /// Outgoing data is produced on a worker thread and paused while a song is being played,
    /// so it never competes with gameplay inputs on the same TCP stream.
    /// </summary>
    internal sealed class SongTransfer : IDisposable
    {
        private const int CHUNK_SIZE = 256 * 1024;

        private readonly Connection _conn;
        private readonly BlockingCollection<(byte[] Hash, SongEntry Song)> _outgoing = new();
        private readonly Thread _worker;
        private volatile bool _disposed;

        private FileStream _incoming;
        private byte[] _incomingHash;
        private string _incomingPath, _incomingName, _incomingArtist, _incomingExt;
        private long _incomingLength;

        /// <summary>Keyed by hex hash. Read by the UI.</summary>
        public ConcurrentDictionary<string, TransferStatus> Status { get; } = new();

        /// <summary>Set by the session while a song is loading or playing.</summary>
        public volatile bool Paused;

        /// <summary>Raised on the main thread when a received song is stored and playable.</summary>
        public event Action<byte[]> SongReceived;

        public SongTransfer(Connection conn)
        {
            _conn = conn;
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "YargNetplay.SongSend" };
            _worker.Start();
        }

        public static string Key(byte[] hash) => BitConverter.ToString(hash).Replace("-", "");

        public TransferStatus GetStatus(byte[] hash) => Status.TryGetValue(Key(hash), out var s) ? s : null;

        /// <summary>True while any song is being requested, sent or received.</summary>
        public bool Active => Status.Values.Any(s => !s.Done && s.Error == null);

        // ------------------------------------------------------------ requesting (receiver side, main thread)

        public void Request(byte[] hash)
        {
            var existing = GetStatus(hash);
            if (existing != null && existing.Error == null) return; // in flight or done

            Status[Key(hash)] = new TransferStatus { Outgoing = false };
            _conn.Send(MsgType.SongRequest, w => w.WriteHash(hash));
        }

        // ------------------------------------------------------------ sending (main thread enqueues, worker sends)

        public void OnRequest(byte[] hash)
        {
            if (!SongLibrary.TryGet(hash, out var song))
            {
                SendFail(hash, "Song is no longer in the sender's library");
                return;
            }
            if (song.SubType is not (EntryType.Ini or EntryType.Sng))
            {
                SendFail(hash, $"{song.SubType} songs can't be sent yet");
                return;
            }

            Status[Key(hash)] = new TransferStatus { Outgoing = true };
            _outgoing.Add((hash, song));
        }

        private void WorkerLoop()
        {
            try
            {
                foreach (var job in _outgoing.GetConsumingEnumerable())
                {
                    if (_disposed) return;
                    SendSong(job.Hash, job.Song);
                }
            }
            catch (ObjectDisposedException) { /* shutting down */ }
        }

        private void SendSong(byte[] hash, SongEntry song)
        {
            var status = Status[Key(hash)];
            string tempFile = null;
            try
            {
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

                _conn.Send(MsgType.SongBegin, w =>
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
                    while (Paused && !_disposed && !_conn.IsClosed) Thread.Sleep(100);
                    if (_disposed || _conn.IsClosed) return;

                    int read = input.Read(buffer, 0, buffer.Length);
                    if (read <= 0) throw new EndOfStreamException();

                    long chunkOffset = offset;
                    _conn.Send(MsgType.SongChunk, w =>
                    {
                        w.WriteHash(hash);
                        w.Write(chunkOffset);
                        w.Write(read);
                        w.Write(buffer, 0, read);
                    });
                    offset += read;
                    status.Progress = (float) offset / length;
                }

                _conn.Send(MsgType.SongEnd, w => w.WriteHash(hash));
                status.Done = true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Sending {song.Name.Original} failed: {e}");
                status.Error = e.Message;
                SendFail(hash, "Sender error: " + e.Message);
            }
            finally
            {
                if (tempFile != null) try { File.Delete(tempFile); } catch { /* temp dir */ }
            }
        }

        private void SendFail(byte[] hash, string reason) =>
            _conn.Send(MsgType.SongFail, w => { w.WriteHash(hash); w.Write(reason); });

        // ------------------------------------------------------------ receiving (main thread)

        public void OnBegin(BinaryReader r)
        {
            var hash = r.ReadHash();
            long length = r.ReadInt64();
            string ext = r.ReadString();
            string name = r.ReadString();
            string artist = r.ReadString();

            CloseIncoming(deletePartial: true);
            if (!ReceivedSongs.IsSongFile("x" + ext))
            {
                Fail(hash, $"Unexpected file type {ext}");
                return;
            }

            _incomingHash = hash;
            _incomingLength = length;
            _incomingExt = ext;
            _incomingName = name;
            _incomingArtist = artist;
            _incomingPath = Path.Combine(ReceivedSongs.PartialFolder, Key(hash) + ".part");
            _incoming = new FileStream(_incomingPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);

            Status[Key(hash)] = new TransferStatus { Outgoing = false };
        }

        public void OnChunk(BinaryReader r)
        {
            var hash = r.ReadHash();
            long offset = r.ReadInt64();
            int count = r.ReadInt32();
            var data = r.ReadBytes(count);

            if (_incoming == null || !hash.AsSpan().SequenceEqual(_incomingHash) || offset != _incoming.Position)
            {
                Fail(hash, "Transfer out of order");
                CloseIncoming(deletePartial: true);
                return;
            }

            _incoming.Write(data, 0, data.Length);
            var status = GetStatus(hash);
            if (status != null) status.Progress = _incomingLength > 0 ? (float) _incoming.Position / _incomingLength : 1f;
        }

        public void OnEnd(BinaryReader r)
        {
            var hash = r.ReadHash();
            if (_incoming == null || !hash.AsSpan().SequenceEqual(_incomingHash)) return;

            bool complete = _incoming.Position == _incomingLength;
            string partial = _incomingPath;
            CloseIncoming(deletePartial: false);

            if (!complete)
            {
                Fail(hash, "Transfer incomplete");
                TryDelete(partial);
                return;
            }

            string final = ReceivedSongs.FinalPathFor(_incomingName, _incomingArtist, hash, _incomingExt);
            try
            {
                if (File.Exists(final)) File.Delete(final);
                File.Move(partial, final);
            }
            catch (Exception e)
            {
                Fail(hash, "Could not store song: " + e.Message);
                TryDelete(partial);
                return;
            }

            if (!ReceivedSongs.TryRegister(final, hash, out _, out var error))
            {
                Fail(hash, "Received song is unusable: " + error);
                TryDelete(final);
                return;
            }

            ReceivedSongs.RefreshSize();
            var status = GetStatus(hash);
            if (status != null) { status.Progress = 1f; status.Done = true; }
            SongReceived?.Invoke(hash);
        }

        public void OnFail(BinaryReader r)
        {
            var hash = r.ReadHash();
            Fail(hash, r.ReadString());
            if (_incomingHash != null && hash.AsSpan().SequenceEqual(_incomingHash))
                CloseIncoming(deletePartial: true);
        }

        private void Fail(byte[] hash, string reason)
        {
            Plugin.Log.LogWarning($"Song transfer {Key(hash)} failed: {reason}");
            var status = Status.GetOrAdd(Key(hash), _ => new TransferStatus());
            status.Error = reason;
        }

        private void CloseIncoming(bool deletePartial)
        {
            if (_incoming == null) return;
            _incoming.Dispose();
            _incoming = null;
            if (deletePartial) TryDelete(_incomingPath);
            _incomingHash = null;
        }

        private static void TryDelete(string path)
        {
            try { if (path != null && File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }

        public void Dispose()
        {
            _disposed = true;
            _outgoing.CompleteAdding();
            CloseIncoming(deletePartial: true);
        }
    }
}
