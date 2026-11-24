using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace YargNetplay.Net
{
    /// <summary>
    /// One framed TCP connection (a player's to the host, or the host's to one player). Reads on a background thread
    /// into <see cref="Inbox"/>; the main thread drains the inbox. Sends are thread-safe.
    /// TCP (with Nagle off) is used for everything: inputs are absorbed by the remote-player delay
    /// buffer, and the same stream carries song file transfers.
    /// </summary>
    internal sealed class Connection : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly BinaryWriter _writer;
        private readonly object _sendLock = new();
        private readonly Thread _readThread;
        private readonly Func<Connection, byte[], bool> _onFrame;
        private volatile bool _closed;

        public ConcurrentQueue<byte[]> Inbox { get; } = new();
        public bool IsClosed => _closed;
        public string CloseReason { get; private set; }
        public string RemoteEndPoint { get; }

        /// <param name="onFrame">
        /// Optional. Runs on the read thread for every frame (the host passes frames on to other players there, so
        /// relaying never waits for a game frame); the frame only goes to <see cref="Inbox"/> when it returns true.
        /// </param>
        public Connection(TcpClient client, Func<Connection, byte[], bool> onFrame = null)
        {
            _client = client;
            _client.NoDelay = true;
            _stream = client.GetStream();
            _writer = new BinaryWriter(_stream);
            _onFrame = onFrame;
            RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "?";

            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "YargNetplay.Read" };
            _readThread.Start();
        }

        public void Send(MsgType type, byte from, byte to, Action<BinaryWriter> writeBody = null)
        {
            if (_closed) return;

            byte[] frame;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((byte) type);
                bw.Write(from);
                bw.Write(to);
                writeBody?.Invoke(bw);
                bw.Flush();
                frame = ms.ToArray();
            }
            SendFrame(frame);
        }

        /// <summary>Sends a whole frame (header included) as it is, for example one passed on from another player.</summary>
        public void SendFrame(byte[] frame)
        {
            if (_closed) return;
            try
            {
                lock (_sendLock)
                {
                    _writer.Write(frame.Length);
                    _writer.Write(frame);
                    _writer.Flush();
                }
            }
            catch (Exception e)
            {
                Close("Send failed: " + e.Message);
            }
        }

        private void ReadLoop()
        {
            try
            {
                using var reader = new BinaryReader(_stream);
                while (!_closed)
                {
                    int length = reader.ReadInt32();
                    if (length < Protocol.HeaderBytes || length > Protocol.MaxFrameBytes)
                        throw new InvalidDataException($"Bad frame length {length}");

                    var frame = reader.ReadBytes(length);
                    if (frame.Length != length) throw new EndOfStreamException();
                    if (_onFrame == null || _onFrame(this, frame)) Inbox.Enqueue(frame);
                }
            }
            catch (Exception e)
            {
                Close(e is EndOfStreamException or IOException ? "Peer disconnected" : e.Message);
            }
        }

        public void Close(string reason)
        {
            if (_closed) return;
            CloseReason = reason;
            _closed = true;
            try { _client.Close(); } catch { /* already closed */ }
        }

        public void Dispose() => Close("Closed locally");
    }
}
