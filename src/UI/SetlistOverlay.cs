using System.Collections.Generic;
using System.Linq;
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
        private const float HeaderHeight = 56f, Padding = 14f, RowSpacing = 4f, CornerRadius = 14f, ButtonSize = 34f;

        private static readonly AccessTools.FieldRef<Navigator, Stack<NavigationScheme>> SchemeStack =
            AccessTools.FieldRefAccess<Navigator, Stack<NavigationScheme>>("_schemeStack");
        private static readonly AccessTools.FieldRef<NavigationScheme, List<NavigationScheme.Entry>> SchemeEntries =
            AccessTools.FieldRefAccess<NavigationScheme, List<NavigationScheme.Entry>>("_entries");

        private const string AlbumContainerPath = "Horizontal Layout/Sidebar/Inner Container/Album Cover Container";

        private RectTransform _bounds, _cover, _albumContainer, _header, _panel, _content;
        private GameObject _ui;
        private ScrollRect _scroll;
        private TMP_Text _title, _toggle, _edit;
        private TMP_FontAsset _font;
        private readonly List<Row> _rows = new();
        private int _selected;
        private int _scrolledTo = -1; // the selected song last scrolled into view; -1 = none yet
        private bool _grabbed;
        private NavigationScheme _editScheme;
        private static SetlistOverlay _instance;
        private float _nextRefresh;
        private bool _hidFps;
        private int _lastCount = -1;

        /// <summary>True while the expanded list is on screen, so "X added a song" toasts would only repeat it.</summary>
        public static bool ListShowing { get; private set; }

        /// <summary>True while the setlist is being edited (its own controls are on top of the library's).</summary>
        public static bool Editing => _instance != null && _instance._editScheme != null;

        private static void ToggleCollapsed()
        {
            _instance?.StopEditing();
            Plugin.SetlistCollapsed.Value = !Plugin.SetlistCollapsed.Value;
        }

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

            // Drawn on top of the cover; FitToSidePanel places it (the cover's height, the side panel's width).
            var go = new GameObject("Netplay Setlist", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.transform.SetAsLastSibling();
            var rect = (RectTransform) go.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);

            var font = library.transform.Find("Horizontal Layout/Sidebar").GetComponentInChildren<TMP_Text>(true)?.font;
            go.AddComponent<SetlistOverlay>().Build(rect, cover, library.transform.Find(AlbumContainerPath) as RectTransform, font);
        }

        private void Build(RectTransform bounds, RectTransform cover, RectTransform albumContainer, TMP_FontAsset font)
        {
            _bounds = bounds;
            _cover = cover;
            _albumContainer = albumContainer;
            FitToSidePanel();
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
            Stretch(_title.rectTransform, left: Padding, right: Padding + 140); // room for Edit and Show/Hide
            _title.fontStyle = FontStyles.Bold | FontStyles.UpperCase;

            // Edit and Show/Hide: their own click targets on top of the header's (which collapses).
            _toggle = NewHeaderButton("Toggle", Padding, ToggleCollapsed);
            _edit = NewHeaderButton("Edit", Padding + 70, () => { if (Editing) StopEditing(); else StartEditing(); });
            foreach (var text in new[] { _title, _toggle, _edit })
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
            // Square top (it hangs from the square header), rounded bottom. Semi-transparent over the artwork.
            var panelImage = _panel.gameObject.AddComponent<Image>();
            panelImage.sprite = BottomRoundedSprite();
            panelImage.type = Image.Type.Sliced;
            panelImage.color = new Color(0.02f, 0.04f, 0.08f, 0.9f);

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
            layout.padding = new RectOffset(6, 6, 6, Mathf.RoundToInt(CornerRadius)); // the bottom clears the rounding
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
                StopEditing();
                ListShowing = false;
                _lastCount = -1;
                return;
            }

            FitToSidePanel();
            var queue = session.Queue;
            if (queue.Count == 0) StopEditing();
            bool collapsed = Plugin.SetlistCollapsed.Value;
            _header.sizeDelta = new Vector2(0, HeaderHeightAboveCover());
            string total = queue.Count > 0 ? $" ({FormatLength(queue.Sum(q => q.LengthMilliseconds))})" : "";
            _title.text = $"Setlist  <size=75%><color=#9fb3c8>{queue.Count} song{(queue.Count == 1 ? "" : "s")}{total}</color></size>";
            _toggle.text = $"<color=#9fb3c8>{(collapsed ? "Show" : "Hide")}</color>";
            _edit.text = Editing ? "<color=#ffd34d>Done</color>" : "<color=#9fb3c8>Edit</color>";
            _edit.transform.parent.gameObject.SetActive(queue.Count > 0);
            _selected = Mathf.Clamp(_selected, 0, Mathf.Max(queue.Count - 1, 0));

            bool added = _lastCount >= 0 && queue.Count > _lastCount;
            _lastCount = queue.Count;

            ListShowing = !collapsed;
            _panel.gameObject.SetActive(!collapsed);
            if (collapsed) return;

            int count = Mathf.Max(queue.Count, 1);
            while (_rows.Count < count) _rows.Add(NewRow());
            for (int i = 0; i < _rows.Count; i++) _rows[i].Root.gameObject.SetActive(i < count);

            if (queue.Count == 0)
            {
                _rows[0].Show(false, false, false);
                _rows[0].Text.text = "<color=#9fb3c8><size=85%>Hold Green on a song to add it to the setlist for both players.</size></color>";
            }
            else
            {
                for (int i = 0; i < queue.Count; i++)
                {
                    var item = queue[i];
                    var (state, color) = QueueStatus.Describe(item, session.Transfer?.GetStatus(item.Hash), session.PeerName);
                    string by = item.AddedByMe ? "you" : session.PeerName;
                    _rows[i].Show(Editing, Editing && i == _selected, Editing && i == _selected && _grabbed);
                    _rows[i].Text.text =
                        $"<b>{i + 1}. <noparse>{item.Name}</noparse></b>\n" +
                        $"<size=88%><color=#c3d0de><noparse>{item.Artist}</noparse></color></size>\n" +
                        $"<size=76%><color=#9fb3c8>Added by <noparse>{by}</noparse></color>  <color={color}><noparse>{state}</noparse></color></size>";
                }
            }

            // Fit the content, but never past the cover.
            LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
            float wanted = LayoutUtility.GetPreferredHeight(_content);
            _panel.sizeDelta = new Vector2(0, Mathf.Min(wanted, _bounds.rect.height));

            // Show a newly added song straight away; while editing, bring the selected song into view when the
            // selection changes (only then, so the player can still scroll the list freely).
            if (added && !Editing)
            {
                Canvas.ForceUpdateCanvases();
                _scroll.verticalNormalizedPosition = 0f;
            }
            else if (Editing && queue.Count > 0 && _selected != _scrolledTo)
            {
                _scrolledTo = _selected;
                ScrollIntoView(_rows[_selected].Root);
            }
        }

        private void ScrollIntoView(RectTransform row)
        {
            Canvas.ForceUpdateCanvases();
            var viewport = _scroll.viewport;
            var corners = new Vector3[4];
            row.GetWorldCorners(corners);
            float top = viewport.InverseTransformPoint(corners[1]).y, bottom = viewport.InverseTransformPoint(corners[0]).y;
            float viewTop = viewport.rect.yMax, viewBottom = viewport.rect.yMin;

            // The content moves up (positive y) to reveal rows further down.
            var pos = _content.anchoredPosition;
            if (top > viewTop) pos.y -= top - viewTop;
            else if (bottom < viewBottom) pos.y += viewBottom - bottom;
            else return;
            float max = Mathf.Max(0, _content.rect.height - viewport.rect.height);
            _content.anchoredPosition = new Vector2(pos.x, Mathf.Clamp(pos.y, 0, max));
        }

        // ------------------------------------------------------------ editing

        /// <summary>
        /// Edit mode puts the setlist's own controls on top of the library's: Up/Down pick a song, Green picks it up
        /// so Up/Down move it, Yellow removes it, Red or Select finish. The mouse buttons on each row do the same.
        /// </summary>
        private void StartEditing()
        {
            if (Editing || Session.Current is not { IsConnected: true, Queue: { Count: > 0 } }) return;
            Plugin.SetlistCollapsed.Value = false;
            _grabbed = false;
            _scrolledTo = -1;
            _editScheme = new NavigationScheme(new()
            {
                new NavigationScheme.Entry(MenuAction.Up, "Menu.Common.Up", () => Step(-1)),
                new NavigationScheme.Entry(MenuAction.Down, "Menu.Common.Down", () => Step(+1)),
                new NavigationScheme.Entry(MenuAction.Green, "Move", () => { _grabbed = !_grabbed; Refresh(); }),
                new NavigationScheme.Entry(MenuAction.Yellow, "Remove", () => RemoveAt(_selected)),
                new NavigationScheme.Entry(MenuAction.Red, "Done", StopEditing),
                new NavigationScheme.Entry(MenuAction.Select, "Done", StopEditing, hide: true),
            }, false);
            Navigator.Instance.PushScheme(_editScheme);
            Refresh();
        }

        public void StopEditing()
        {
            if (_editScheme == null) return;
            var scheme = _editScheme;
            _editScheme = null;
            _grabbed = false;
            RemoveScheme(scheme);
            if (_ui != null && _ui.activeInHierarchy) Refresh();
        }

        /// <summary>Pops our scheme even if something (a dialog) was pushed above it since.</summary>
        private static void RemoveScheme(NavigationScheme scheme)
        {
            var navigator = Navigator.Instance;
            if (navigator == null) return;
            var stack = SchemeStack(navigator);
            if (!stack.Contains(scheme)) return;

            var above = new Stack<NavigationScheme>();
            while (stack.Peek() != scheme) above.Push(stack.Pop());
            navigator.PopScheme();
            while (above.Count > 0) navigator.PushScheme(above.Pop());
        }

        private void Step(int delta)
        {
            int count = Session.Current?.Queue.Count ?? 0;
            if (count == 0) return;
            if (_grabbed)
            {
                if (Game.NativeSetlist.MoveShared(_selected, delta)) _selected += delta;
            }
            else
            {
                _selected = Mathf.Clamp(_selected + delta, 0, count - 1);
            }
            Refresh();
        }

        private void Move(int index, int delta)
        {
            if (Game.NativeSetlist.MoveShared(index, delta)) _selected = index + delta;
            Refresh();
        }

        private void RemoveAt(int index)
        {
            var queue = Session.Current?.Queue;
            if (queue == null || index < 0 || index >= queue.Count) return;
            Game.NativeSetlist.RemoveShared(queue[index].Hash);
            _grabbed = false;
            Refresh();
        }

        /// <summary>m:ss, or h:mm:ss from an hour up.</summary>
        private static string FormatLength(long milliseconds)
        {
            var time = System.TimeSpan.FromMilliseconds(milliseconds);
            return time.TotalHours >= 1 ? $"{(int) time.TotalHours}:{time:mm\\:ss}" : $"{time.Minutes}:{time:ss}";
        }

        /// <summary>The cover's height and position, the side panel's full width.</summary>
        private void FitToSidePanel()
        {
            if (_cover == null || _albumContainer == null) return;
            var side = WorldRect(_albumContainer);
            var cover = WorldRect(_cover);
            _bounds.position = new Vector3(side.center.x, cover.center.y, _bounds.position.z);
            float scale = Mathf.Max(_bounds.lossyScale.x, 0.0001f);
            _bounds.sizeDelta = new Vector2(side.width / scale, cover.height / scale);
        }

        private static Rect WorldRect(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y);
        }

        private static Sprite _bottomRounded;

        /// <summary>A 9-sliced white sprite with rounded bottom corners and square top corners.</summary>
        private static Sprite BottomRoundedSprite()
        {
            if (_bottomRounded != null) return _bottomRounded;
            int r = Mathf.RoundToInt(CornerRadius), size = r * 2 + 2;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // Distance into each bottom corner's circle (y = 0 is the bottom row), antialiased over one pixel.
                float alpha = 1f;
                if (y < r && (x < r || x >= size - r))
                {
                    float cx = x < r ? r : size - r, cy = r;
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                    alpha = Mathf.Clamp01(r - d + 0.5f);
                }
                pixels[y * size + x] = new Color32(255, 255, 255, (byte) (alpha * 255));
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            // 100 pixels per unit matches the canvas reference, so the radius is CornerRadius canvas units.
            _bottomRounded = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(r, r, r, r));
            return _bottomRounded;
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

        private void OnEnable() => _instance = this;

        private void OnDisable()
        {
            StopEditing();
            if (_instance == this) _instance = null;
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

        /// <summary>One setlist entry: its text, plus Up / Down / Remove buttons shown while editing.</summary>
        private sealed class Row
        {
            public RectTransform Root;
            public Image Background;
            public TMP_Text Text;
            public GameObject Actions;

            public void Show(bool editing, bool selected, bool grabbed)
            {
                Actions.SetActive(editing);
                Background.color = grabbed ? new Color(0.55f, 0.42f, 0.05f, 0.75f)
                    : selected ? new Color(0.16f, 0.32f, 0.6f, 0.75f)
                    : Color.clear;
            }
        }

        private Row NewRow()
        {
            int index = _rows.Count;
            var root = NewChild("Row", _content);
            var background = root.gameObject.AddComponent<Image>();
            background.color = Color.clear;
            root.gameObject.AddComponent<Button>().onClick.AddListener(() =>
            {
                if (!Editing) return;
                _selected = index;
                Refresh();
            });
            var layout = root.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 6, 6);
            layout.spacing = 8;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;

            // The text takes whatever width the buttons leave and wraps (title and artist each as much as needed):
            // no preferred width of its own, so a long title can't squeeze the buttons.
            var text = NewText("Text", root, 22, TextAlignmentOptions.TopLeft);
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            var textElement = text.gameObject.AddComponent<LayoutElement>();
            textElement.minWidth = 0;
            textElement.preferredWidth = 0;
            textElement.flexibleWidth = 1;

            const float spacing = 4;
            var actions = NewChild("Actions", root);
            var actionsElement = actions.gameObject.AddComponent<LayoutElement>();
            actionsElement.minWidth = actionsElement.preferredWidth = ButtonSize * 3 + spacing * 2;
            actionsElement.minHeight = actionsElement.preferredHeight = ButtonSize;
            actionsElement.flexibleWidth = actionsElement.flexibleHeight = 0;
            var actionsLayout = actions.gameObject.AddComponent<HorizontalLayoutGroup>();
            actionsLayout.spacing = spacing;
            actionsLayout.childControlWidth = actionsLayout.childControlHeight = true;
            actionsLayout.childForceExpandWidth = actionsLayout.childForceExpandHeight = false;
            NewRowButton(actions, "\u25B2", () => Move(index, -1));
            NewRowButton(actions, "\u25BC", () => Move(index, +1));
            NewRowButton(actions, "X", () => RemoveAt(index));

            return new Row { Root = root, Background = background, Text = text, Actions = actions.gameObject };
        }

        private void NewRowButton(Transform parent, string glyph, System.Action onClick)
        {
            var rect = NewChild("Button", parent);
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.minWidth = element.preferredWidth = element.minHeight = element.preferredHeight = ButtonSize;
            element.flexibleWidth = element.flexibleHeight = 0;
            rect.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.12f);
            rect.gameObject.AddComponent<Button>().onClick.AddListener(() => onClick());
            var label = NewText("Glyph", rect, 18, TextAlignmentOptions.Center);
            Stretch(label.rectTransform);
            label.text = glyph;
        }

        /// <summary>A text button at the right of the header, <paramref name="right"/> from its right edge.</summary>
        private TMP_Text NewHeaderButton(string name, float right, System.Action onClick)
        {
            var rect = NewChild(name, _header);
            rect.anchorMin = new Vector2(1, 0);
            rect.anchorMax = new Vector2(1, 1);
            rect.pivot = new Vector2(1, 0.5f);
            rect.sizeDelta = new Vector2(64, 0);
            rect.anchoredPosition = new Vector2(-right, 0);
            rect.gameObject.AddComponent<Image>().color = Color.clear; // click target
            rect.gameObject.AddComponent<Button>().onClick.AddListener(() => onClick());

            var text = NewText("Label", rect, 20, TextAlignmentOptions.MidlineRight);
            Stretch(text.rectTransform);
            text.fontStyle = FontStyles.Bold | FontStyles.UpperCase;
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
                    SchemeEntries(stack.Peek()).Add(new NavigationScheme.Entry(MenuAction.Select, "Setlist (Hold: Edit)",
                        ToggleCollapsed, onHoldHandler: () => _instance?.StartEditing(), holdSeconds: 0.5f));
            }
        }

        /// <summary>
        /// The library rebuilds its controls by popping the top scheme, which would pop ours while editing.
        /// Treat editing like an open popup: the rebuild waits until editing ends.
        /// </summary>
        [HarmonyPatch(typeof(MusicLibraryMenu), "IsNavigationSchemeBlocked")]
        private static class SchemeBlockedPatch
        {
            private static void Postfix(ref bool __result) => __result |= Editing;
        }

        /// <summary>The library pops its scheme when it closes; take ours off first.</summary>
        [HarmonyPatch(typeof(MusicLibraryMenu), "OnDisable")]
        private static class LibraryDisablePatch
        {
            private static void Prefix() => _instance?.StopEditing();
        }
    }
}
