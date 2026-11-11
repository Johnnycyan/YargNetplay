using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace YargNetplay.Net
{
    /// <summary>
    /// One framed TCP connection to the peer. Reads on a background thread into <see cref="Inbox"/>;
    /// the main thread drains the inbox. Sends are thread-safe.
    /// TCP (with Nagle off) is used for everything: inputs are absorbed by the remote-player delay
    /// buffer, and the same stream will carry song file transfers later.
    /// </summary>
    internal sealed class Connection : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly BinaryWriter _writer;
        private readonly object _sendLock = new();
        private readonly Thread _readThread;
        private volatile bool _closed;

        public ConcurrentQueue<byte[]> Inbox { get; } = new();
        public bool IsClosed => _closed;
        public string CloseReason { get; private set; }
        public string RemoteEndPoint { get; }

        public Connection(TcpClient client)
        {
            _client = client;
            _client.NoDelay = true;
            _stream = client.GetStream();
            _writer = new BinaryWriter(_stream);
            RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "?";

            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "YargNetplay.Read" };
            _readThread.Start();
        }

        public void Send(MsgType type, Action<BinaryWriter> writeBody = null)
        {
            if (_closed) return;

            byte[] payload;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((byte) type);
                writeBody?.Invoke(bw);
                bw.Flush();
                payload = ms.ToArray();
            }

            try
            {
                lock (_sendLock)
                {
                    _writer.Write(payload.Length);
                    _writer.Write(payload);
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
                    if (length <= 0 || length > Protocol.MaxFrameBytes)
                        throw new InvalidDataException($"Bad frame length {length}");

                    Inbox.Enqueue(reader.ReadBytes(length));
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
