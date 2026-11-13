using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using YARG;
using YargNetplay.UI;

namespace YargNetplay
{
    /// <summary>
    /// Asks GitHub for the newest release once the main menu is up and shows a dialog when it's newer than this build. The dialog
    /// can install it: the release DLL for this build's YARG channel replaces the plugin, and takes effect on restart.
    /// </summary>
    internal static class UpdateCheck
    {
        public const string Repository = "Johnnycyan/YargNetplay";
        public const string ReleasesPage = "https://github.com/" + Repository + "/releases/latest";

        private static Task<(Version version, string tag, string url)> _check;
        private static bool _handled;

        private static string PluginPath => typeof(Plugin).Assembly.Location;

        // The check starts once the main menu has been up this long, so it doesn't land while the game is still
        // loading (the first seconds on the menu are busy with device detection and toasts).
        private const float MenuSettleSeconds = 5f;
        private static float _menuSince = -1;

        /// <summary>At launch: removes what an earlier update left behind.</summary>
        public static void Start() => RemoveUpdateLeftovers();

        /// <summary>
        /// Main thread, every frame: starts the check once the main menu has settled (in the background; it never
        /// throws into the game), and shows the result once it's in.
        /// </summary>
        public static void Poll()
        {
            if (_handled) return;
            bool onMenu = SceneManager.GetActiveScene().buildIndex == (int) SceneIndex.Menu;
            if (_check == null)
            {
                if (!Plugin.CheckForUpdates.Value) { _handled = true; return; }
                if (!onMenu) { _menuSince = -1; return; }
                if (_menuSince < 0) _menuSince = Time.unscaledTime;
                if (Time.unscaledTime - _menuSince >= MenuSettleSeconds) _check = Task.Run(FetchLatest);
                return;
            }

            if (_check is not { IsCompleted: true } check) return;
            if (check.IsFaulted)
            {
                _handled = true;
                Plugin.Log.LogInfo("Update check failed: " + check.Exception?.GetBaseException().Message);
                return;
            }

            var (latest, tag, url) = check.Result;
            if (latest == null || !TryParse(Plugin.Version, out var current) || latest <= current)
            {
                _handled = true;
                Plugin.Log.LogInfo($"Update check: {Plugin.Version} is up to date.");
                return;
            }

            // Wait for the menu (not the loading screen or a song) and for any other dialog to close.
            if (!onMenu || !NetplayDialogs.CanShowNotice) return;
            _handled = true;
            Plugin.Log.LogInfo($"Update check: {tag} is available (installed {Plugin.Version}).");
            NetplayDialogs.ShowUpdateAvailable(tag, Plugin.Version, () => Install(latest, tag, url));
        }

        private static async void Install(Version version, string tag, string releaseUrl)
        {
            NetplayDialogs.ShowUpdateProgress(tag);
            try
            {
                await Task.Run(() => Download(version, tag));
                Plugin.Log.LogInfo($"Update: installed {tag} ({BuildInfo.Channel}); it takes effect when YARG restarts.");
                NetplayDialogs.ShowUpdateInstalled(tag);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Update to {tag} failed: {e}");
                NetplayDialogs.ShowUpdateFailed(e.Message, releaseUrl);
            }
        }

        /// <summary>
        /// Downloads the release DLL next to the plugin, checks it, and swaps it in. Windows won't overwrite a loaded
        /// DLL but does allow renaming it, so the running one moves aside to ".old" (removed on the next launch).
        /// </summary>
        private static void Download(Version version, string tag)
        {
            string plugin = PluginPath, next = plugin + ".new", old = plugin + ".old";
            string url = $"https://github.com/{Repository}/releases/download/{tag}/YargNetplay-{BuildInfo.Channel}.dll";

            var request = (HttpWebRequest) WebRequest.Create(url);
            request.UserAgent = "YargNetplay/" + Plugin.Version;
            request.Timeout = request.ReadWriteTimeout = 30000;
            using (var response = request.GetResponse())
            using (var source = response.GetResponseStream())
            using (var file = File.Create(next))
                source.CopyTo(file);

            // A truncated or wrong download must never replace a working plugin.
            AssemblyName name;
            try { name = AssemblyName.GetAssemblyName(next); }
            catch (Exception e) { File.Delete(next); throw new IOException("the download isn't a valid plugin", e); }
            if (name.Name != "YargNetplay" || name.Version.Major != version.Major || name.Version.Minor != version.Minor
                || name.Version.Build != Math.Max(0, version.Build))
            {
                File.Delete(next);
                throw new IOException($"the download is {name.Name} {name.Version}, not YARG Netplay {tag}");
            }

            // Left from an earlier update in this same run (still loaded? then YARG must restart first).
            try { if (File.Exists(old)) File.Delete(old); }
            catch (Exception e) { File.Delete(next); throw new IOException("an earlier update is waiting for YARG to restart", e); }

            File.Move(plugin, old);
            try { File.Move(next, plugin); }
            catch
            {
                File.Move(old, plugin); // put the running version back
                throw;
            }
        }

        /// <summary>Removes what the last update left: the replaced plugin, or a download that was never swapped in.</summary>
        private static void RemoveUpdateLeftovers()
        {
            foreach (string leftover in new[] { PluginPath + ".old", PluginPath + ".new" })
            {
                try { if (File.Exists(leftover)) File.Delete(leftover); }
                catch (Exception e) { Plugin.Log.LogInfo($"Couldn't remove {leftover}: {e.Message}"); }
            }
        }

        private static (Version, string, string) FetchLatest()
        {
            var request = (HttpWebRequest) WebRequest.Create($"https://api.github.com/repos/{Repository}/releases/latest");
            request.UserAgent = "YargNetplay/" + Plugin.Version; // GitHub's API refuses requests without one
            request.Accept = "application/vnd.github+json";
            request.Timeout = request.ReadWriteTimeout = 10000;
            using var response = request.GetResponse();
            using var reader = new StreamReader(response.GetResponseStream());
            var release = JObject.Parse(reader.ReadToEnd());

            string tag = (string) release["tag_name"] ?? "";
            string url = (string) release["html_url"] ?? ReleasesPage;
            return TryParse(tag, out var version) ? (version, tag, url) : (null, tag, url);
        }

        /// <summary>"v1.2.3" or "1.2.3" (anything after a '-' or '+' is ignored).</summary>
        private static bool TryParse(string text, out Version version)
        {
            text = text.Trim().TrimStart('v', 'V');
            int cut = text.IndexOfAny(new[] { '-', '+' });
            if (cut >= 0) text = text.Substring(0, cut);
            return Version.TryParse(text, out version);
        }
    }
}
