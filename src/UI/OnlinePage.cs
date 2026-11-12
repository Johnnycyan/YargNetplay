using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Core.Input;
using YARG.Helpers;
using YARG.Localization;
using YARG.Menu;
using YARG.Menu.Data;
using YARG.Menu.Navigation;
using YARG.Menu.ProfileList;
using YARG.Menu.Persistent;
using YargNetplay.Game;
using YargNetplay.Net;
using Object = UnityEngine.Object;

namespace YargNetplay.UI
{
    /// <summary>
    /// The full-screen Online page. Built at runtime by cloning YARG's Profiles page (header, list,
    /// sidebar, buttons) and re-purposing its parts, then registered with YARG's MenuManager so it opens,
    /// hides the main menu and goes Back like any other YARG page.
    /// </summary>
    internal sealed class OnlinePage : MonoBehaviour
    {
        public const MenuManager.Menu MenuId = (MenuManager.Menu) 4242;

        private enum View { Idle, Hosting, Connecting, Connected }

        private static readonly AccessTools.FieldRef<MenuManager, Dictionary<MenuManager.Menu, MenuObject>> Menus =
            AccessTools.FieldRefAccess<MenuManager, Dictionary<MenuManager.Menu, MenuObject>>("_menus");
        private static readonly AccessTools.FieldRef<NavigatableBehaviour, GameObject> SelectedVisual =
            AccessTools.FieldRefAccess<NavigatableBehaviour, GameObject>("_selectedVisual");
        private static readonly AccessTools.FieldRef<NavigatableBehaviour, bool> SelectOnClick =
            AccessTools.FieldRefAccess<NavigatableBehaviour, bool>("_selectOnClick");
        private static readonly AccessTools.FieldRef<NavigatableButton, Button.ButtonClickedEvent> NavOnClick =
            AccessTools.FieldRefAccess<NavigatableButton, Button.ButtonClickedEvent>("_onClick");

        private GameObject _rowPrefab, _headerPrefab;
        private Transform _list;
        private NavigationGroup _listGroup;
        private TMP_Text _sidebarTitle;
        private GameObject _editButton;
        private TMP_InputField _delayField;
        private ColoredButtonRef _big, _left, _right;
        private TMP_Text _folderLabel;
        private TMP_InputField _folderField;
        private ColoredButtonRef _changeFolder, _openFolder;

        private readonly List<(OnlineRow Row, TMP_Text Text, Func<string> Label)> _rows = new();
        private string _signature;
        private float _nextRefresh;
        private SavedServer _selectedServer;
        private Action _primary;

        // ------------------------------------------------------------ creation

        /// <summary>Called when the main menu starts (each time the menu scene loads).</summary>
        public static void Create(MenuManager manager)
        {
            var menus = Menus(manager);
            if (menus.ContainsKey(MenuId) || !menus.TryGetValue(MenuManager.Menu.ProfileList, out var template)) return;

            // The template is inactive, so the clone's components don't wake up until we're done with them.
            var go = Instantiate(template.gameObject, template.transform.parent);
            go.SetActive(false);
            go.name = "OnlineMenu";

            var profileList = go.GetComponent<ProfileListMenu>();
            var rowPrefab = (GameObject) AccessTools.Field(typeof(ProfileListMenu), "_profileViewPrefab").GetValue(profileList);
            var headerPrefab = (GameObject) AccessTools.Field(typeof(ProfileListMenu), "_profileListHeaderPrefab").GetValue(profileList);

            // Drop the Profiles page's own logic; keep its visuals.
            DestroyImmediate(profileList);
            foreach (var sidebar in go.GetComponentsInChildren<ProfileSidebar>(true))
                DestroyImmediate(sidebar);
            var highway = go.transform.Find("HighwayConfigurationMenu");
            if (highway != null) DestroyImmediate(highway.gameObject);
            StripBehaviour(go);

            AccessTools.Field(typeof(MenuObject), "<Menu>k__BackingField").SetValue(go.GetComponent<MenuObject>(), MenuId);
            menus[MenuId] = go.GetComponent<MenuObject>();

            go.AddComponent<OnlinePage>().Build(rowPrefab, headerPrefab);
        }

        /// <summary>Removes serialized click handlers and localization so cloned YARG widgets only do what we wire up.</summary>
        private static void StripBehaviour(GameObject root)
        {
            foreach (var localize in root.GetComponentsInChildren<LocalizeText>(true)) DestroyImmediate(localize);
            foreach (var button in root.GetComponentsInChildren<Button>(true)) button.onClick = new Button.ButtonClickedEvent();
            foreach (var nav in root.GetComponentsInChildren<NavigatableButton>(true)) NavOnClick(nav) = new Button.ButtonClickedEvent();
            foreach (var toggle in root.GetComponentsInChildren<Toggle>(true)) toggle.onValueChanged = new Toggle.ToggleEvent();
            foreach (var field in root.GetComponentsInChildren<TMP_InputField>(true))
            {
                field.onValueChanged = new TMP_InputField.OnChangeEvent();
                field.onEndEdit = new TMP_InputField.SubmitEvent();
                field.onSubmit = new TMP_InputField.SubmitEvent();
                field.onSelect = new TMP_InputField.SelectionEvent();
                field.onDeselect = new TMP_InputField.SelectionEvent();
            }
        }

        private void Build(GameObject rowPrefab, GameObject headerPrefab)
        {
            _rowPrefab = rowPrefab;
            _headerPrefab = headerPrefab;

            var header = transform.Find("GenericHeader");
            header.Find("Single Header Text").GetComponent<TMP_Text>().text = "Online";
            header.Find("Button").GetComponent<Button>().onClick.AddListener(Back);
            foreach (var name in new[] { "Add Profile", "Add Bot" })
            {
                var extra = header.Find(name);
                if (extra != null) extra.gameObject.SetActive(false);
            }

            _list = transform.Find("Background/Profiles/Viewport/Content");
            _listGroup = transform.Find("Background/Profiles").GetComponent<NavigationGroup>();

            var sidebar = transform.Find("Sidebar/Contents");
            _sidebarTitle = sidebar.Find("Header/Profile Name Container/Profile Name").GetComponent<TMP_Text>();
            _editButton = sidebar.Find("Header/Profile Name Container/Edit").gameObject;
            _editButton.GetComponentInChildren<Button>(true).onClick.AddListener(RenameSelectedServer);
            var picture = sidebar.Find("Header/Profile Picture");
            if (picture != null) picture.gameObject.SetActive(false);

            var options = sidebar.Find("ScrollView/Viewport/Content");
            foreach (Transform option in options) option.gameObject.SetActive(option.name == "Input Calibration");

            var delayRow = options.Find("Input Calibration");
            var buttons = sidebar.Find("Buttons");
            BuildFolderRows(options, delayRow, buttons);

            delayRow.Find("Option Name").GetComponent<TMP_Text>().text = "REMOTE PLAYER DELAY";
            _delayField = delayRow.Find("InputField/Field").GetComponent<TMP_InputField>();
            _delayField.contentType = TMP_InputField.ContentType.IntegerNumber;
            if (_delayField.placeholder is TMP_Text placeholder) placeholder.text = "Delay (ms)...";
            _delayField.onEndEdit.AddListener(OnDelayEdited);

            _big = new ColoredButtonRef(buttons.Find("Edit Profile"));
            _left = new ColoredButtonRef(buttons.Find("Add New Device"));
            _right = new ColoredButtonRef(buttons.Find("Remove Device"));
        }

        /// <summary>
        /// The received-songs folder: a copy of the delay row showing the path (editable), and a row with two of
        /// the sidebar's half-width buttons (Change Folder opens YARG's own folder picker, Open Folder opens Explorer).
        /// </summary>
        private void BuildFolderRows(Transform options, Transform template, Transform buttons)
        {
            var folderRow = Instantiate(template.gameObject, options).transform;
            folderRow.name = "Received Songs Folder";
            folderRow.SetSiblingIndex(template.GetSiblingIndex() + 1);
            _folderLabel = folderRow.Find("Option Name").GetComponent<TMP_Text>();
            _folderLabel.richText = true;
            _folderField = folderRow.Find("InputField/Field").GetComponent<TMP_InputField>();
            _folderField.contentType = TMP_InputField.ContentType.Standard;
            _folderField.lineType = TMP_InputField.LineType.SingleLine;
            if (_folderField.placeholder is TMP_Text placeholder) placeholder.text = "Default folder";
            _folderField.onEndEdit.AddListener(text =>
            {
                if (text != ReceivedSongs.Folder) ChangeFolder(text);
            });

            var row = new GameObject("Received Songs Buttons", typeof(RectTransform));
            row.transform.SetParent(options, false);
            row.transform.SetSiblingIndex(folderRow.GetSiblingIndex() + 1);
            ((RectTransform) row.transform).sizeDelta = ((RectTransform) template).sizeDelta;
            var layout = row.AddComponent<LayoutElement>();
            layout.minHeight = layout.preferredHeight = ((RectTransform) template).sizeDelta.y;
            var group = row.AddComponent<HorizontalLayoutGroup>();
            group.spacing = 10;
            group.childControlWidth = group.childControlHeight = true;
            group.childForceExpandWidth = group.childForceExpandHeight = true;

            _changeFolder = new ColoredButtonRef(Instantiate(buttons.Find("Add New Device"), row.transform));
            _openFolder = new ColoredButtonRef(Instantiate(buttons.Find("Remove Device"), row.transform));
        }

        // ------------------------------------------------------------ lifecycle

        private void OnEnable()
        {
            Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                NavigationScheme.Entry.NavigateUp,
                NavigationScheme.Entry.NavigateDown,
                new NavigationScheme.Entry(MenuAction.Green, "Menu.Common.Confirm", () => _primary?.Invoke()),
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", Back),
            }, true));

            _delayField.text = Plugin.RemoteDelayMs.Value.ToString();
            Refresh(force: true);
        }

        private void OnDisable()
        {
            Navigator.Instance?.PopScheme();
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.25f;
            Refresh(force: false);
        }

        private static void Back() => MenuManager.Instance.PopMenu();

        // ------------------------------------------------------------ state -> UI

        private static View CurrentView(Session s) => s switch
        {
            { IsConnected: true } => View.Connected,
            { State: SessionState.Hosting } => View.Hosting,
            { State: SessionState.Connecting } => View.Connecting,
            _ => View.Idle,
        };

        private void Refresh(bool force)
        {
            var session = Session.Current;
            var view = CurrentView(session);

            string signature = $"{view}|{session?.PeerName}|{string.Join(";", SavedServers.All.Select(s => s.Name + "@" + s.Address))}";
            if (force || signature != _signature)
            {
                _signature = signature;
                RebuildList(view, session);
            }

            foreach (var (_, text, label) in _rows) text.text = label();
            UpdateSidebar(view, session);
        }

        private void RebuildList(View view, Session session)
        {
            var previous = _selectedServer;
            _listGroup.ClearNavigatables();
            foreach (Transform child in _list) Destroy(child.gameObject);
            _rows.Clear();
            _selectedServer = null;

            if (view == View.Idle)
            {
                AddHeader("Saved servers");
                if (SavedServers.All.Count == 0)
                    AddHeader("<size=70%>Servers you join are saved here. Use Join Address or Add Server.</size>");

                foreach (var server in SavedServers.All)
                {
                    var s = server;
                    var row = AddRow(
                        () => $"{s.Name}   <size=70%><color=#9fb3c8>{s.Address}</color></size>",
                        onSelected: () => _selectedServer = s,
                        onConfirm: () => NetplayDialogs.Join(s.Address),
                        buttonLabel: "Join", onButton: () => NetplayDialogs.Join(s.Address),
                        onDelete: () => { SavedServers.Remove(s); Refresh(force: true); });
                    if (s == previous) _listGroup.SelectAt(_listGroup.Count - 1);
                }
                if (_selectedServer == null && _listGroup.Count > 0) _listGroup.SelectFirst();
                return;
            }

            AddHeader("Players");
            if (session.IsHost)
            {
                AddRow(() => $"{session.LocalName}   <size=70%><color=#ffd34d>HOST</color></size>");
                if (view == View.Connected)
                    AddRow(() => $"{session.PeerName}   {PingText(session)}   {ReadyText(session.PeerLobbyReady)}");
                else
                    AddHeader($"<size=70%>Waiting for a player. They join with your IP address and port {Plugin.Port.Value}.</size>");
            }
            else if (view == View.Connected)
            {
                AddRow(() => $"{session.PeerName}   <size=70%><color=#ffd34d>HOST</color></size>   {PingText(session)}");
                AddRow(() => $"{session.LocalName}   <size=70%><color=#9fb3c8>YOU</color></size>   {ReadyText(session.LocalLobbyReady)}");
            }
            else
            {
                AddHeader($"<size=70%>Connecting to {session.JoinAddress}...</size>");
            }
        }

        private static string PingText(Session s) => $"<size=70%><color=#9fb3c8>{s.RttSeconds * 1000:0} ms</color></size>";

        private static string ReadyText(bool ready) => ready
            ? "<size=70%><color=#5ee07a>READY</color></size>"
            : "<size=70%><color=#ff8a7a>NOT READY</color></size>";

        private void UpdateSidebar(View view, Session session)
        {
            var colors = MenuData.Colors;
            // Same look as the Profiles page's secondary (device) buttons.
            Color secondary = colors.DarkButton, cancel = colors.CancelButton;

            _editButton.SetActive(view == View.Idle && _selectedServer != null);
            _delayField.interactable = session is not { InSongFlow: true };
            UpdateFolderRows(session, secondary);

            switch (view)
            {
                case View.Idle:
                    _sidebarTitle.text = _selectedServer?.Name ?? "Not connected";
                    _big.Set("Host Game", NetplayDialogs.Host, color: colors.ConfirmButton);
                    _left.Set("Join Address", NetplayDialogs.PromptJoinAddress, color: secondary);
                    _right.Set("Add Server", PromptAddServer, color: secondary);
                    _primary = _selectedServer != null ? () => NetplayDialogs.Join(_selectedServer.Address) : null;
                    break;

                case View.Hosting:
                    _sidebarTitle.text = $"Hosting on port {Plugin.Port.Value}";
                    _big.Set("Stop Hosting", () => session.Dispose(), color: cancel);
                    _left.Hide();
                    _right.Hide();
                    _primary = null;
                    break;

                case View.Connecting:
                    _sidebarTitle.text = "Connecting...";
                    _big.Set("Cancel", () => session.Dispose(), color: cancel);
                    _left.Hide();
                    _right.Hide();
                    _primary = null;
                    break;

                case View.Connected when session.IsHost:
                    _sidebarTitle.text = $"Playing with {session.PeerName}";
                    _big.Set("Go to Song Select", session.SendEveryoneToSongSelect, enabled: session.PeerLobbyReady, color: colors.ConfirmButton);
                    _left.Hide();
                    _right.Set("Disconnect", () => session.Dispose(), color: cancel);
                    _primary = session.PeerLobbyReady ? session.SendEveryoneToSongSelect : null;
                    break;

                case View.Connected:
                    _sidebarTitle.text = $"Connected to {session.PeerName}";
                    _big.Set(session.LocalLobbyReady ? "Not Ready" : "Ready", ToggleReady, color: session.LocalLobbyReady ? cancel : colors.ConfirmButton);
                    _left.Hide();
                    _right.Set("Disconnect", () => session.Dispose(), color: cancel);
                    _primary = ToggleReady;
                    break;
            }
        }

        private void UpdateFolderRows(Session session, Color color)
        {
            bool canChange = CanChangeFolder(session, out _);
            _changeFolder.Set("Change Folder", BrowseFolder, enabled: canChange, color: color);
            _openFolder.Set("Open Folder", ReceivedSongs.OpenFolder, color: color);

            _folderField.interactable = canChange;
            if (!_folderField.isFocused && _folderField.text != ReceivedSongs.Folder) _folderField.text = ReceivedSongs.Folder;

            string detail = ReceivedSongs.Busy
                ? "Moving songs..."
                : $"{ReceivedSongs.Count} song{(ReceivedSongs.Count == 1 ? "" : "s")}, {ReceivedSongs.TotalBytes / (1024f * 1024f):0.#} MB";
            string label = $"RECEIVED SONGS\n<size=70%><color=#9fb3c8>{detail}</color></size>";
            if (_folderLabel.text != label) _folderLabel.text = label;
        }

        private static bool CanChangeFolder(Session session, out string reason)
        {
            reason = ReceivedSongs.Busy ? "Received songs are still being indexed or moved. Try again in a moment."
                : session is { InSongFlow: true } ? "The folder can't be changed during a song."
                : session?.Transfer is { Active: true } ? "Wait for the current song transfer to finish."
                : null;
            return reason == null;
        }

        private void BrowseFolder() => FileExplorerHelper.OpenChooseFolder(ReceivedSongs.Folder, ChangeFolder);

        private void ChangeFolder(string folder)
        {
            string error = null;
            bool ok = CanChangeFolder(Session.Current, out error) && ReceivedSongs.TryChangeFolder(folder,
                (summary, allMoved) => { if (allMoved) Notify.Success(summary); else Notify.Warning(summary); },
                out error);
            if (!ok) NetplayDialogs.ShowError(error);
            _folderField.text = ReceivedSongs.Folder;
        }

        private static void ToggleReady()
        {
            var session = Session.Current;
            session?.SetLobbyReady(!session.LocalLobbyReady);
        }

        // ------------------------------------------------------------ actions

        private void OnDelayEdited(string text)
        {
            if (!int.TryParse(text, out int ms)) ms = Plugin.RemoteDelayMs.Value;
            ms = Mathf.Clamp(ms, 30, 1000);
            Plugin.RemoteDelayMs.Value = ms;
            _delayField.text = ms.ToString();
            if (Session.Current is { InSongFlow: false } s) s.RemoteDelaySeconds = ms / 1000.0;
        }

        private void PromptAddServer() =>
            NetplayDialogs.ShowTextInput("Add server: IP or IP:port", "", address =>
            {
                if (!SavedServers.TryParse(address, Plugin.Port.Value, out _, out _))
                {
                    NetplayDialogs.ShowError($"\"{address}\" isn't a valid address.");
                    return;
                }
                _selectedServer = SavedServers.Add(address);
                Refresh(force: true);
            });

        private void RenameSelectedServer()
        {
            var server = _selectedServer;
            if (server == null) return;
            NetplayDialogs.ShowTextInput("Server name", server.Name, name =>
            {
                SavedServers.Rename(server, name);
                Refresh(force: true);
            });
        }

        // ------------------------------------------------------------ list widgets (YARG's profile row + list header)

        private void AddHeader(string text)
        {
            var go = Instantiate(_headerPrefab, _list);
            foreach (var localize in go.GetComponentsInChildren<LocalizeText>(true)) DestroyImmediate(localize);
            var label = go.GetComponentInChildren<TMP_Text>();
            label.richText = true;
            label.text = text;
        }

        private OnlineRow AddRow(Func<string> label, Action onSelected = null, Action onConfirm = null,
            string buttonLabel = null, Action onButton = null, Action onDelete = null)
        {
            var go = Instantiate(_rowPrefab, _list);

            // Swap the profile row's logic for ours, keeping its selection visual.
            var profileView = go.GetComponent<ProfileView>();
            var visual = SelectedVisual(profileView);
            DestroyImmediate(profileView);
            go.SetActive(false);
            var row = go.AddComponent<OnlineRow>();
            SelectedVisual(row) = visual;
            SelectOnClick(row) = true;
            row.WhenSelected = onSelected;
            row.WhenConfirmed = onConfirm;
            StripBehaviour(go);

            var text = go.transform.Find("Profile Name").GetComponent<TMP_Text>();
            text.richText = true;

            var connectGroup = go.transform.Find("Connect Group").gameObject;
            connectGroup.SetActive(onButton != null || onDelete != null);
            var connect = connectGroup.transform.Find("Connect");
            connect.gameObject.SetActive(onButton != null);
            if (onButton != null)
            {
                connect.GetComponentInChildren<TMP_Text>().text = buttonLabel;
                connect.GetComponentInChildren<Button>().onClick.AddListener(() => onButton());
            }
            var delete = connectGroup.transform.Find("Delete");
            delete.gameObject.SetActive(onDelete != null);
            if (onDelete != null) delete.GetComponentInChildren<Button>().onClick.AddListener(() => onDelete());
            go.transform.Find("Disconnect Group").gameObject.SetActive(false);

            go.SetActive(true);
            _listGroup.AddNavigatable(row);
            _rows.Add((row, text, label));
            text.text = label();
            return row;
        }

        /// <summary>One of the sidebar's ColoredButtons, re-labelled and re-wired as the page state changes.</summary>
        private sealed class ColoredButtonRef
        {
            private readonly GameObject _root;
            private readonly YARG.Menu.ColoredButton _button;
            private Action _action;

            public ColoredButtonRef(Transform root)
            {
                _root = root.gameObject;
                _button = root.GetComponent<YARG.Menu.ColoredButton>();
                _button.OnClick.AddListener(() => _action?.Invoke());
            }

            public void Set(string label, Action action, bool enabled = true, Color? color = null)
            {
                _root.SetActive(true);
                _button.Text.text = label;
                _action = enabled ? action : null;
                if (!enabled)
                {
                    _button.DisableButton();
                    return;
                }
                _button.EnableButton();
                if (color is Color c) _button.SetBackgroundAndTextColor(c);
            }

            public void Hide() => _root.SetActive(false);
        }
    }
}
