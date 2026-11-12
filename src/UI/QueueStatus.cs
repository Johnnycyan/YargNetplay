using YargNetplay.Net;

namespace YargNetplay.UI
{
    /// <summary>One shared-setlist song's sync state, worded for the UI. Shared by the setlist overlay and the debug window.</summary>
    internal static class QueueStatus
    {
        public static (string Text, string Color) Describe(QueueItem item, TransferStatus transfer, string peerName)
        {
            if (transfer is { Error: not null })
                return ("Transfer failed: " + transfer.Error, "#ff8a7a");
            if (transfer is { Done: false })
                return ($"{(transfer.Outgoing ? "Sending to " + peerName : "Downloading")} {transfer.Progress * 100:0}%", "#80c0ff");

            return (item.LocalHas, item.PeerHas) switch
            {
                (true, true) => ("Ready", "#5ee07a"),
                (true, null) => ("Checking...", "#cccccc"),
                (true, false) => ($"{peerName} doesn't have it", "#ffcc66"),
                _ => ("You don't have it", "#ffcc66"),
            };
        }
    }
}
