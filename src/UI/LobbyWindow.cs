using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YARG.Core;
using YARG.Core.Song;
using YargNetplay.Game;
using YargNetplay.Net;

namespace YargNetplay.UI
{
    /// <summary>In-game IMGUI window: connect, browse your library, build the shared queue, start songs.</summary>
    internal sealed class LobbyWindow
    {
        private const int WindowId = 0x4E50; // "NP"
        private const float RowHeight = 22f;

        public bool Visible;

        private Rect _rect = new(40, 40, 860, 600);
        private string _joinHost = "127.0.0.1";
        private string _port = Protocol.DefaultPort.ToString();
        private string _search = "";
        private string _lastSearch;
        private List<SongEntry> _filtered = new();
        private int _lastLibraryCount = -1;
        private Vector2 _libraryScroll, _queueScroll;
        private string _error;

        public void Draw()
        {
            if (!Visible) return;
            _rect = GUILayout.Window(WindowId, _rect, DrawWindow, "YARG Netplay debug overlay  (F8 to hide; use Online in the main menu)");
        }

        private void DrawWindow(int id)
        {
            var session = Session.Current;

            DrawConnection(session);
            GUILayout.Space(6);

            if (session is { IsConnected: true })
            {
                GUILayout.BeginHorizontal();
                GUILayout.BeginVertical(GUILayout.Width(_rect.width * 0.5f));
                DrawLibrary(session);
                GUILayout.EndVertical();
                GUILayout.BeginVertical();
                DrawQueue(session);
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
            }

            if (!string.IsNullOrEmpty(_error))
                GUILayout.Label("<color=#ff8080>" + _error + "</color>", RichLabel);

            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        // ------------------------------------------------------------ connection

        private void DrawConnection(Session session)
        {
            if (session == null || session.State == SessionState.Idle)
            {
                if (session != null) GUILayout.Label(session.Status);

                GUILayout.BeginHorizontal();
                GUILayout.Label("Port", GUILayout.Width(40));
                _port = GUILayout.TextField(_port, GUILayout.Width(70));
                if (GUILayout.Button("Host", GUILayout.Width(80)) && TryPort(out int hostPort))
                {
                    Plugin.Port.Value = hostPort;
                    NetplayDialogs.Host();
                }
                GUILayout.Space(20);
                GUILayout.Label("IP", GUILayout.Width(20));
                _joinHost = GUILayout.TextField(_joinHost, GUILayout.Width(180));
                if (GUILayout.Button("Join", GUILayout.Width(80)) && TryPort(out int joinPort))
                    NetplayDialogs.Join($"{_joinHost.Trim()}:{joinPort}");
                GUILayout.EndHorizontal();
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(session.Status);
            GUILayout.FlexibleSpace();
            if (session.IsConnected)
                GUILayout.Label($"Ping {session.RttSeconds * 1000:0} ms", GUILayout.Width(90));
            if (GUILayout.Button(session.IsConnected ? "Disconnect" : "Cancel", GUILayout.Width(90)))
                session.Dispose();
            GUILayout.EndHorizontal();

            if (session.IsConnected)
            {
                GUI.enabled = !session.InSongFlow;
                GUILayout.BeginHorizontal();
                GUILayout.Label($"Remote player delay: {session.RemoteDelaySeconds * 1000:0} ms", GUILayout.Width(210));
                Plugin.AutoDelay.Value = GUILayout.Toggle(Plugin.AutoDelay.Value, "Auto", GUILayout.Width(60));
                if (!Plugin.AutoDelay.Value)
                    Plugin.RemoteDelayMs.Value = (int) Math.Round(GUILayout.HorizontalSlider(Plugin.RemoteDelayMs.Value, 30f, 500f) / 10) * 10;
                GUILayout.EndHorizontal();
                GUI.enabled = true;
            }
        }

        private bool TryPort(out int port)
        {
            if (int.TryParse(_port, out port) && port is > 0 and < 65536)
            {
                _error = null;
                return true;
            }
            _error = "Invalid port";
            return false;
        }

        // ------------------------------------------------------------ library

        private void DrawLibrary(Session session)
        {
            GUILayout.Label("Your library");
            _search = GUILayout.TextField(_search);

            int libraryCount = SongLibrary.LibraryCount + ReceivedSongs.Count;
            if (_search != _lastSearch || libraryCount != _lastLibraryCount)
            {
                var all = SongLibrary.All();
                _lastSearch = _search;
                _lastLibraryCount = libraryCount;
                var terms = _search.Trim();
                _filtered = terms.Length == 0
                    ? all.ToList()
                    : all.Where(s => Contains(s.Name.Original, terms) || Contains(s.Artist.Original, terms)).ToList();
            }

            DrawVirtualList(ref _libraryScroll, _filtered.Count, 360, (rect, i) =>
            {
                var song = _filtered[i];
                string tag = SongLibrary.IsReceived(song) ? "[received]  " : "";
                if (GUI.Button(rect, $"+  {tag}{song.Name.Original}  -  {song.Artist.Original}", LeftButton))
                {
                    session.AddToQueue(song);
                    NativeSetlist.Add(song);
                }
            });
        }

        private static bool Contains(string text, string term) =>
            text != null && text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;

        // ------------------------------------------------------------ queue

        private void DrawQueue(Session session)
        {
            GUILayout.Label("Shared setlist (start it from YARG's music library)");

            _queueScroll = GUILayout.BeginScrollView(_queueScroll, GUILayout.Height(384));
            foreach (var item in session.Queue.ToList())
            {
                GUILayout.BeginHorizontal();

                var transfer = session.Transfer?.GetStatus(item.Hash);
                var (state, color) = QueueStatus.Describe(item, transfer, session.PeerName);
                GUILayout.Label($"<color={color}>{state}</color>", RichLabel, GUILayout.Width(160));
                GUILayout.Label($"{item.Name} - {item.Artist}");

                if (transfer?.Error != null && !transfer.Outgoing && GUILayout.Button("Retry", GUILayout.Width(50)))
                    session.RetryTransfer(item);
                if (GUILayout.Button("x", GUILayout.Width(24)))
                {
                    session.RemoveFromQueue(item.Hash);
                    NativeSetlist.Remove(item.Hash);
                }

                GUILayout.EndHorizontal();

                if (transfer?.Error != null)
                    GUILayout.Label("<color=#ff8080>   " + transfer.Error + "</color>", RichLabel);
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Received songs: {ReceivedSongs.Count} ({ReceivedSongs.TotalBytes / (1024f * 1024f):0.#} MB). " +
                "Not in your library; move them into a song folder to keep them.");
            if (GUILayout.Button("Open folder", GUILayout.Width(100)))
                ReceivedSongs.OpenFolder();
            GUILayout.EndHorizontal();
        }

        // ------------------------------------------------------------ helpers

        /// <summary>Draws only the visible rows, so libraries with tens of thousands of songs stay fast.</summary>
        private static void DrawVirtualList(ref Vector2 scroll, int count, float height, Action<Rect, int> drawRow)
        {
            var view = GUILayoutUtility.GetRect(0, height, GUILayout.ExpandWidth(true));
            var content = new Rect(0, 0, view.width - 20, count * RowHeight);

            scroll = GUI.BeginScrollView(view, scroll, content);
            int first = Mathf.Max(0, (int) (scroll.y / RowHeight));
            int last = Mathf.Min(count, first + (int) (height / RowHeight) + 2);
            for (int i = first; i < last; i++)
                drawRow(new Rect(0, i * RowHeight, content.width, RowHeight - 2), i);
            GUI.EndScrollView();
        }

        private static GUIStyle _richLabel, _leftButton;
        private static GUIStyle RichLabel => _richLabel ??= new GUIStyle(GUI.skin.label) { richText = true };
        private static GUIStyle LeftButton => _leftButton ??= new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
    }
}
