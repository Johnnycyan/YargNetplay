using System;
using System.IO;

namespace YargNetplay.Net
{
    /// <summary>
    /// Wire protocol. Every frame is [int32 length][byte type][payload], little-endian (BinaryWriter).
    /// Bump <see cref="Version"/> whenever any payload layout changes.
    /// </summary>
    internal static class Protocol
    {
        public const int Version = 7;
        public const int DefaultPort = 47820;
        public const int MaxFrameBytes = 16 * 1024 * 1024;
    }

    internal enum MsgType : byte
    {
        /// <summary>int protocolVersion, string yargVersion, string modVersion, string playerName</summary>
        Hello = 1,

        /// <summary>double senderLocalTime</summary>
        Ping,

        /// <summary>double echoedTime, double responderLocalTime</summary>
        Pong,

        /// <summary>byte[20] hash, string name, string artist</summary>
        QueueAdd,

        /// <summary>byte[20] hash</summary>
        QueueRemove,

        /// <summary>byte[20] hash, bool hasSong</summary>
        Availability,

        /// <summary>
        /// byte[20] hash, int profileLength, byte[] profile (YargProfile.Serialize), string enginePresetJson.
        /// Sender confirmed its difficulty and is waiting to load this song; both sides load once both are ready for the same hash.
        /// </summary>
        Ready,

        /// <summary>Sender cancelled its Ready.</summary>
        Unready,

        /// <summary>bool playingAShow, int count, count x byte[20] hash. Sender opened difficulty select for these songs; follow along.</summary>
        OpenSetlist,

        /// <summary>Sender cleared the shared setlist.</summary>
        QueueClear,

        /// <summary>bool ready. Sender's Ready toggle on the Online page.</summary>
        LobbyReady,

        /// <summary>Host to client: everyone go to song select (Quickplay) now.</summary>
        GoToSongSelect,

        /// <summary>Sender finished loading the gameplay scene and is waiting at the start gate.</summary>
        Loaded,

        /// <summary>double hostTime. Host to client: open the start gate at this host-clock time.</summary>
        StartAt,

        /// <summary>double songTime, int action, int value. One gameplay input from the sender's player.</summary>
        Input,

        Pause,
        Resume,

        /// <summary>Sender quit the current song.</summary>
        LeaveSong,

        /// <summary>byte[20] hash. Receiver asks the peer to send a song it is missing.</summary>
        SongRequest,

        /// <summary>byte[20] hash, long totalLength, string extension (".sng"/".yargsong"), string name, string artist</summary>
        SongBegin,

        /// <summary>byte[20] hash, long offset, int count, byte[count] data</summary>
        SongChunk,

        /// <summary>byte[20] hash</summary>
        SongEnd,

        /// <summary>byte[20] hash, string reason</summary>
        SongFail,

        /// <summary>Sender backed out of difficulty select to the music library; the receiver follows.</summary>
        LeaveSetlist,

        /// <summary>bool hasInstrument, int instrument, int difficulty. Sender's preferred part (song filter).</summary>
        PartChoice,

        /// <summary>bool. Whether the sender is in song select (music library or difficulty select).</summary>
        SongSelectState,
    }

    internal static class BinaryExtensions
    {
        public const int HashLength = 20;

        public static void WriteHash(this BinaryWriter writer, byte[] hash)
        {
            if (hash.Length != HashLength)
                throw new ArgumentException($"Hash must be {HashLength} bytes", nameof(hash));
            writer.Write(hash);
        }

        public static byte[] ReadHash(this BinaryReader reader) => reader.ReadBytes(HashLength);
    }
}
