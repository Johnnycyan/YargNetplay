using System.Linq;
using YargNetplay.Net;

namespace YargNetplay.UI
{
    /// <summary>One shared-setlist song's sync state, worded for the UI. Shared by the setlist overlay and the debug window.</summary>
    internal static class QueueStatus
    {
        public static (string Text, string Color) Describe(QueueItem item, TransferStatus transfer, Session session)
        {
            if (transfer is { Error: not null })
                return ("Transfer failed: " + transfer.Error, "#ff8a7a");
            if (transfer is { Done: false })
                return ($"{(transfer.Outgoing ? "Sending to " + transfer.PeerName : "Downloading")} {transfer.Progress * 100:0}%", "#80c0ff");

            if (!item.LocalHas) return ("You don't have it", "#ffcc66");

            var missing = session.Peers.Where(p => item.PeerHas.TryGetValue(p.Id, out bool has) && !has).Select(p => p.Name).ToList();
            if (missing.Count > 0)
                return ($"{Session.NameList(missing)} {(missing.Count == 1 ? "doesn't" : "don't")} have it", "#ffcc66");
            if (session.Peers.Any(p => !item.PeerHas.ContainsKey(p.Id)))
                return ("Checking...", "#cccccc");
            return ("Ready", "#5ee07a");
        }
    }
}
