using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using YargNetplay.UI;

namespace YargNetplay
{
    /// <summary>
    /// Asks GitHub for the newest release once at launch and shows a toast when it's newer than this build.
    /// </summary>
    internal static class UpdateCheck
    {
        public const string Repository = "Johnnycyan/YargNetplay";
        public const string ReleasesPage = "https://github.com/" + Repository + "/releases/latest";

        private static Task<(Version version, string tag, string url)> _check;
        private static bool _handled;

        /// <summary>Starts the check in the background (it never throws into the game).</summary>
        public static void Start()
        {
            if (!Plugin.CheckForUpdates.Value || _check != null) return;
            _check = Task.Run(FetchLatest);
        }

        /// <summary>Main thread, every frame: shows the result once it's in.</summary>
        public static void Poll()
        {
            if (_handled || _check is not { IsCompleted: true } check) return;
            _handled = true;
            if (check.IsFaulted)
            {
                Plugin.Log.LogInfo("Update check failed: " + check.Exception?.GetBaseException().Message);
                return;
            }

            var (latest, tag, url) = check.Result;
            if (latest == null || !TryParse(Plugin.Version, out var current) || latest <= current)
            {
                Plugin.Log.LogInfo($"Update check: {Plugin.Version} is up to date.");
                return;
            }

            Plugin.Log.LogInfo($"Update check: {tag} is available (installed {Plugin.Version}).");
            Notify.Info($"YARG Netplay {tag} is available (you have {Plugin.Version}). " +
                        "Close YARG and run the installer again to update. Click to open the download page.",
                () => Application.OpenURL(url));
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
