using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Core.Input;
using YARG.Menu.MusicLibrary;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YargNetplay.Net;

namespace YargNetplay.UI
{
    /// <summary>
    /// The shared setlist, shown over the album art in the music library's song info panel while connected:
    /// every song, who added it, and its sync/transfer state. It lives inside the album art box and never grows
    /// past it, so nothing else on the page is covered. Collapses to its header (click it, or Select on a controller).
    /// </summary>
    internal sealed class SetlistOverlay : MonoBehaviour
    {
        // The cover square itself (the border drawn around the artwork), not the whole album container, which also
        // holds the Play / Add to Setlist button underneath.
        private const string CoverPath =
            "Horizontal Layout/Sidebar/Inner Container/Album Cover Container/Image/Album Cover Small Container/Album Cover Small Container/Album Cover Border";
        private const float HeaderHeight = 56f, Padding = 14f, RowSpacing = 8f;

        private static readonly AccessTools.FieldRef<Navigator, Stack<NavigationScheme>> SchemeStack =
            AccessTools.FieldRefAccess<Navigator, Stack<NavigationScheme>>("_schemeStack");
        private static readonly AccessTools.FieldRef<NavigationScheme, List<NavigationScheme.Entry>> SchemeEntries =
            AccessTools.FieldRefAccess<NavigationScheme, List<NavigationScheme.Entry>>("_entries");

        private const string AlbumContainerPath = "Horizontal Layout/Sidebar/Inner Container/Album Cover Container";

        private RectTransform _bounds, _albumContainer, _header, _panel, _content;
        private GameObject _ui;
        private ScrollRect _scroll;
        private TMP_Text _title, _toggle;
        private TMP_FontAsset _font;
        private readonly List<TMP_Text> _rows = new();
        private float _nextRefresh;
        private bool _hidFps;
        private int _lastCount = -1;

        /// <summary>True while the expanded list is on screen, so "X added a song" toasts would only repeat it.</summary>
        public static bool ListShowing { get; private set; }

        private static void ToggleCollapsed() => Plugin.SetlistCollapsed.Value = !Plugin.SetlistCollapsed.Value;

        // ------------------------------------------------------------ creation

        private static void Attach(MusicLibraryMenu library)
        {
            if (library.transform.Find(CoverPath) is not RectTransform cover)
            {
                Plugin.Log.LogWarning("Setlist overlay: album cover not found; the music library layout changed.");
                return;
            }
            var parent = cover.parent;
            if (parent.Find("Netplay Setlist") != null) return;

            // Same rect as the cover, drawn on top of it.
            var go = new GameObject("Netplay Setlist", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.transform.SetAsLastSibling();
            var rect = (RectTransform) go.transform;
            rect.anchorMin = cover.anchorMin;
            rect.anchorMax = cover.anchorMax;
            rect.pivot = cover.pivot;
            rect.anchoredPosition = cover.anchoredPosition;
            rect.sizeDelta = cover.sizeDelta;

            var font = library.transform.Find("Horizontal Layout/Sidebar").GetComponentInChildren<TMP_Text>(true)?.font;
            go.AddComponent<SetlistOverlay>().Build(rect, library.transform.Find(AlbumContainerPath) as RectTransform, font);
        }

        private void Build(RectTransform bounds, RectTransform albumContainer, TMP_FontAsset font)
        {
            _bounds = bounds;
            _albumContainer = albumContainer;
            _font = font;

            _ui = NewChild("UI", transform).gameObject;
            Stretch((RectTransform) _ui.transform);

            // Header: directly above the cover, same width, in the space between the top of the screen and the art,
            // so collapsing leaves the artwork completely uncovered.
            _header = NewChild("Header", _ui.transform);
            _header.anchorMin = new Vector2(0, 1);
            _header.anchorMax = new Vector2(1, 1);
            _header.pivot = new Vector2(0.5f, 0);
            _header.sizeDelta = new Vector2(0, HeaderHeight);
            _header.gameObject.AddComponent<Image>().color = new Color(0.07f, 0.12f, 0.22f, 0.95f);
            _header.gameObject.AddComponent<Button>().onClick.AddListener(ToggleCollapsed);

            _title = NewText("Title", _header, 26, TextAlignmentOptions.MidlineLeft);
            Stretch(_title.rectTransform, left: Padding, right: Padding + 70); // room for Show/Hide
            _title.fontStyle = FontStyles.Bold | FontStyles.UpperCase;

            _toggle = NewText("Toggle", _header, 20, TextAlignmentOptions.MidlineRight);
            Stretch(_toggle.rectTransform, left: Padding, right: Padding);
            _toggle.fontStyle = FontStyles.Bold | FontStyles.UpperCase;
            foreach (var text in new[] { _title, _toggle })
            {
                text.enableAutoSizing = true;
                text.fontSizeMin = 12;
                text.fontSizeMax = text.fontSize;
            }

            // List panel: over the cover from its top edge, height fits the list but never past the cover.
            _panel = NewChild("Panel", _ui.transform);
            _panel.anchorMin = new Vector2(0, 1);
            _panel.anchorMax = new Vector2(1, 1);
            _panel.pivot = new Vector2(0.5f, 1);
            _panel.sizeDelta = Vector2.zero;
            _panel.gameObject.AddComponent<Image>().color = new Color(0.02f, 0.04f, 0.08f, 0.9f);

            // Scrollable list (mouse wheel / drag).
            var list = NewChild("List", _panel);
            Stretch(list);
            list.gameObject.AddComponent<Image>().color = Color.clear; // catches scroll events over empty space
            list.gameObject.AddComponent<RectMask2D>();

            _content = NewChild("Content", list);
            _content.anchorMin = new Vector2(0, 1);
            _content.anchorMax = new Vector2(1, 1);
            _content.pivot = new Vector2(0.5f, 1);
            _content.sizeDelta = Vector2.zero; // new RectTransforms default to 100x100, which would widen it past the box
            var layout = _content.gameObject.AddComponent<VerticalLayoutGroup>();
            int pad = Mathf.RoundToInt(Padding);
            layout.padding = new RectOffset(pad, pad, pad - 4, pad);
            layout.spacing = RowSpacing;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            _content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _scroll = list.gameObject.AddComponent<ScrollRect>();
            _scroll.content = _content;
            _scroll.viewport = list;
            _scroll.horizontal = false;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 30f;

            Refresh();
            if (System.Environment.GetEnvironmentVariable("YARGNETPLAY_DUMP_MENUS") == "1")
                Invoke(nameof(DumpLayout), 5f);
        }

        private void DumpLayout() => HierarchyDump.DumpObject(_bounds, "setlist-dump.txt");

        // ------------------------------------------------------------ state -> UI

        private void Update()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.2f;
            Refresh();
        }

        private void Refresh()
        {
            var session = Session.Current;
            bool connected = session is { IsConnected: true };
            _ui.SetActive(connected);
            HideFpsCounter(connected);
            if (!connected)
            {
                ListShowing = false;
                _lastCount = -1;
                return;
            }

            var queue = session.Queue;
            bool collapsed = Plugin.SetlistCollapsed.Value;
            _header.sizeDelta = new Vector2(0, HeaderHeightAboveCover());
            _title.text = $"Setlist  <size=75%><color=#9fb3c8>{queue.Count} song{(queue.Count == 1 ? "" : "s")}</color></size>";
            _toggle.text = $"<color=#9fb3c8>{(collapsed ? "Show" : "Hide")}</color>";

            bool added = _lastCount >= 0 && queue.Count > _lastCount;
            _lastCount = queue.Count;

            ListShowing = !collapsed;
            _panel.gameObject.SetActive(!collapsed);
            if (collapsed) return;

            int count = Mathf.Max(queue.Count, 1);
            while (_rows.Count < count) _rows.Add(NewRow());
            for (int i = 0; i < _rows.Count; i++) _rows[i].gameObject.SetActive(i < count);

            if (queue.Count == 0)
            {
                _rows[0].text = "<color=#9fb3c8><size=85%>Hold Green on a song to add it to the setlist for both players.</size></color>";
            }
            else
            {
                for (int i = 0; i < queue.Count; i++)
                {
                    var item = queue[i];
                    var (state, color) = QueueStatus.Describe(item, session.Transfer?.GetStatus(item.Hash), session.PeerName);
                    string by = item.AddedByMe ? "you" : session.PeerName;
                    _rows[i].text =
                        $"<b>{i + 1}. <noparse>{item.Name}</noparse></b>  <color=#9fb3c8><noparse>{item.Artist}</noparse></color>\n" +
                        $"<size=78%><color=#9fb3c8>Added by <noparse>{by}</noparse></color>  <color={color}><noparse>{state}</noparse></color></size>";
                }
            }

            // Fit the content, but never past the cover.
            LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
            float wanted = LayoutUtility.GetPreferredHeight(_content);
            _panel.sizeDelta = new Vector2(0, Mathf.Min(wanted, _bounds.rect.height));

            // Show a newly added song straight away.
            if (added)
            {
                Canvas.ForceUpdateCanvases();
                _scroll.verticalNormalizedPosition = 0f;
            }
        }

        /// <summary>The space between the top of the album panel (the top of the screen) and the cover, capped at a normal header.</summary>
        private float HeaderHeightAboveCover()
        {
            if (_albumContainer == null) return HeaderHeight;
            var corners = new Vector3[4];
            _albumContainer.GetWorldCorners(corners);
            float panelTop = corners[1].y;
            _bounds.GetWorldCorners(corners);
            float coverTop = corners[1].y;
            float gap = (panelTop - coverTop) / Mathf.Max(_bounds.lossyScale.y, 0.0001f);
            return Mathf.Clamp(gap, 32f, HeaderHeight);
        }

        private void OnDisable()
        {
            ListShowing = false;
            HideFpsCounter(false);
        }

        /// <summary>
        /// YARG's FPS counter sits in the top-right corner, right over the setlist header. Hide it while the setlist
        /// is shown and put it back afterwards (only if it was on: this never changes the player's setting).
        /// </summary>
        private void HideFpsCounter(bool hide)
        {
            var stats = StatsManager.Instance;
            if (stats == null) return;

            if (hide && !_hidFps && stats.IsShowing(StatsManager.Stat.FPS))
            {
                stats.SetShowing(StatsManager.Stat.FPS, false);
                _hidFps = true;
            }
            else if (!hide && _hidFps)
            {
                stats.SetShowing(StatsManager.Stat.FPS, true);
                _hidFps = false;
            }
        }

        // ------------------------------------------------------------ widgets

        private TMP_Text NewRow()
        {
            var text = NewText("Row", _content, 22, TextAlignmentOptions.TopLeft);
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            return text;
        }

        private TMP_Text NewText(string name, Transform parent, float size, TextAlignmentOptions alignment)
        {
            var text = NewChild(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
            if (_font != null) text.font = _font;
            text.fontSize = size;
            text.alignment = alignment;
            text.richText = true;
            text.color = Color.white;
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
            return text;
        }

        private static RectTransform NewChild(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform) go.transform;
        }

        private static void Stretch(RectTransform rect, float left = 0, float right = 0, float top = 0, float bottom = 0)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        // ------------------------------------------------------------ hooks

        [HarmonyPatch(typeof(MusicLibraryMenu), "OnEnable")]
        private static class LibraryEnablePatch
        {
            private static void Postfix(MusicLibraryMenu __instance) => Attach(__instance);
        }

        /// <summary>Select toggles the overlay. Added to the library's own scheme (Select is unused there) while connected.</summary>
        [HarmonyPatch(typeof(MusicLibraryMenu), nameof(MusicLibraryMenu.SetNavigationScheme))]
        private static class LibrarySchemePatch
        {
            private static void Postfix(MusicLibraryMenu __instance)
            {
                // Show mode keeps its own scheme and SetNavigationScheme pushes nothing then.
                if (__instance.MenuState == MenuState.Show || Session.Current is not { IsConnected: true }) return;
                var stack = SchemeStack(Navigator.Instance);
                if (stack.Count > 0)
                    SchemeEntries(stack.Peek()).Add(new NavigationScheme.Entry(MenuAction.Select, "Setlist", ToggleCollapsed));
            }
        }
    }
}
