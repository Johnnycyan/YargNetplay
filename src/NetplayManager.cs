using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using YARG;
using YARG.Menu.Navigation;
using YargNetplay.Game;
using YargNetplay.Net;
using YargNetplay.UI;

namespace YargNetplay
{
    /// <summary>Persistent driver: pumps the session, toggles the in-game window, tracks scene changes.</summary>
    internal sealed class NetplayManager : MonoBehaviour
    {
        private readonly LobbyWindow _window = new();
        private bool _menuInputsDisabledByUs;

        private void Awake()
        {
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            ReceivedSongs.Initialize();
        }

        private void OnDestroy()
        {
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            Session.Current?.Dispose();
        }

        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current.f8Key.wasPressedThisFrame)
                _window.Visible = !_window.Visible;

            Session.Current?.Update();
            SyncMenuInputLock();
        }

        private void OnGUI() => _window.Draw();

        private static void OnSceneUnloaded(Scene scene)
        {
            if (scene.buildIndex != (int) SceneIndex.Gameplay) return;
            GameHooks.Clear();
            Session.Current?.EndSong();
        }

        /// <summary>
        /// YARG's menu navigation reads the keyboard too, so typing in the window would move the menus.
        /// Lock menu inputs while the window is open outside gameplay.
        /// </summary>
        private void SyncMenuInputLock()
        {
            var nav = Navigator.Instance;
            if (nav == null) return;

            bool want = _window.Visible && SceneManager.GetActiveScene().buildIndex == (int) SceneIndex.Menu;
            if (want == _menuInputsDisabledByUs) return;

            nav.DisableMenuInputs = want;
            _menuInputsDisabledByUs = want;
        }
    }
}
