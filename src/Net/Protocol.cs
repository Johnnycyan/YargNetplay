using System;
using System.IO;

namespace YargNetplay.Net
{
    /// <summary>
    /// Wire protocol. Every frame is [int32 length][byte type][byte from][byte to][payload], little-endian (BinaryWriter).
    /// Bump <see cref="Version"/> whenever any payload layout changes.
    ///
    /// Sessions are a star: every player is connected to the host only, and the host passes frames on. "from" is the
    /// player who wrote the frame (the host overwrites it with the id of the connection a frame came in on, so nobody can
    /// speak for someone else) and "to" is one player's id or <see cref="Broadcast"/> for everyone else.
    /// </summary>
    internal static class Protocol
    {
        public const int Version = 11;
        public const int DefaultPort = 47820;
        public const int MaxFrameBytes = 16 * 1024 * 1024;

        /// <summary>Frame header after the length: type, from, to.</summary>
        public const int HeaderBytes = 3;

        /// <summary>The host's player id.</summary>
        public const byte HostId = 0;

        /// <summary>"to" for frames meant for every other player.</summary>
        public const byte Broadcast = 255;

        /// <summary>Players in one session, host included. Every player sees every highway, so more gets cramped.</summary>
        public const int MaxPlayers = 8;
    }

    internal enum MsgType : byte
    {
        /// <summary>int protocolVersion, string yargVersion, string modVersion, string playerName. Joining player to host.</summary>
        Hello = 1,

        /// <summary>double senderLocalTime. Host to everyone and each player to the host (clocks sync to the host).</summary>
        Ping,

        /// <summary>double echoedTime, double responderLocalTime</summary>
        Pong,

        /// <summary>byte[20] hash, string name, string artist, long lengthMilliseconds, byte addedBy</summary>
        QueueAdd,

        /// <summary>byte[20] hash</summary>
        QueueRemove,

        /// <summary>byte[20] hash, bool hasSong. Sent to everyone, so each player knows who has which song.</summary>
        Availability,

        /// <summary>
        /// byte[20] hash, int profileLength, byte[] profile (YargProfile.Serialize), string enginePresetJson.
        /// Sender confirmed its difficulty and is waiting to load this song; everyone loads once all players are ready for the same hash.
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

        /// <summary>Host to everyone: go to song select (Quickplay) now.</summary>
        GoToSongSelect,

        /// <summary>Sender finished loading the gameplay scene and is waiting at the start gate. Sent to the host.</summary>
        Loaded,

        /// <summary>double hostTime. Host to everyone: open the start gate at this host-clock time.</summary>
        StartAt,

        /// <summary>
        /// int seq, double songTime, int action, int value. One gameplay input from the sender's player. Numbered from 0
        /// each song, so the copy that also travels over UDP (<see cref="UdpChannel"/>) is only used once.
        /// </summary>
        Input,

        Pause,
        Resume,

        /// <summary>Sender quit the current song.</summary>
        LeaveSong,

        /// <summary>byte[20] hash. Receiver asks a player who has the song to send it.</summary>
        SongRequest,

        /// <summary>byte[20] hash, long totalLength, string extension (".sng"/".yargsong"), string name, string artist</summary>
        SongBegin,

        /// <summary>byte[20] hash, long offset, int count, byte[count] data</summary>
        SongChunk,

        /// <summary>byte[20] hash</summary>
        SongEnd,

        /// <summary>byte[20] hash, string reason</summary>
        SongFail,

        /// <summary>Sender backed out of difficulty select to the music library; the receivers follow.</summary>
        LeaveSetlist,

        /// <summary>bool hasInstrument, int instrument, int difficulty. Sender's preferred part (song filter).</summary>
        PartChoice,

        /// <summary>bool. Whether the sender is in song select (music library or difficulty select).</summary>
        SongSelectState,

        /// <summary>int count, count x byte[20] hash. The sender reordered the shared setlist; this is the whole new order.</summary>
        QueueOrder,

        /// <summary>
        /// int showIndex, byte[20] hash, bool skip. Sender restarted the song or skipped to the next setlist song from
        /// the pause menu; the receivers do the same. Everyone then meets at the ready gate for that song.
        /// </summary>
        ChangeSong,

        /// <summary>
        /// int lastSeq, double engineTime. The sender's engine has reached engineTime, so after input lastSeq it can't
        /// send an input earlier than that. The remote player never runs past it, so it never misses a late input.
        /// </summary>
        Watermark,

        /// <summary>
        /// ulong token, int udpPort. Between a player and the host only: the sender listens for UDP on that port and
        /// accepts packets that carry the token (see <see cref="UdpChannel"/>). Not sent when the sender couldn't open a UDP socket.
        /// </summary>
        UdpInfo,

        /// <summary>
        /// Host to a joining player, answering Hello: the player's id is the frame's "to". Followed by a
        /// <see cref="PlayerJoined"/> for each player already in the session and the shared setlist.
        /// </summary>
        Welcome,

        /// <summary>
        /// byte id, string name, string yargVersion, bool isNew. A player is in the session (sent by the host). isNew is
        /// false for the players a joining player is told about on joining.
        /// </summary>
        PlayerJoined,

        /// <summary>byte id. A player left the session (sent by the host).</summary>
        PlayerLeft,

        /// <summary>
        /// byte count, count x (byte id, float rttSeconds, float worstRttSeconds, bool udpWorking). Host to everyone,
        /// once a second: each player's connection to the host.
        /// </summary>
        PlayerStats,

        /// <summary>string reason. Host to a joining player it turns away; the connection closes after it.</summary>
        Reject,
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
