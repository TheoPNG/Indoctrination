using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace Indoctrination.Net
{
    /// <summary>
    /// Asks, once at startup, whether this is still the current build.
    ///
    /// Deliberately not an auto-updater. A macOS app that downloads and replaces
    /// its own bundle runs straight into Gatekeeper, and doing that smoothly
    /// needs the app signed and notarised - a paid Apple account, not an
    /// afternoon. This is the ninety percent of the value for none of that: the
    /// game says there is a newer build and opens the page to get it.
    ///
    /// **It also stops a whole class of confusing bug.** This is a networked
    /// game with an authoritative host; two players on different builds disagree
    /// about the rules, and that reads as the game being broken rather than as
    /// somebody being out of date. Knowing the version is the first half of
    /// telling them.
    ///
    /// Fails silently in every direction. No network, no feed, malformed feed,
    /// running offline for a week - none of that is a reason a card game cannot
    /// be played, so nothing here can stop the game starting.
    /// </summary>
    public sealed class UpdateCheck : MonoBehaviour
    {
        /// <summary>
        /// Where the current version is published. A small JSON file:
        ///
        ///     { "version": "0.2.0", "url": "https://...", "notes": "What changed" }
        ///
        /// Any static host will do - a GitHub raw URL, a gist, a file on a web
        /// host. Point it wherever the builds actually live and the game will
        /// notice the next time anybody opens it.
        /// </summary>
        public const string FeedUrl =
            "https://raw.githubusercontent.com/TheoPNG/Indoctrination/main/Docs/latest.json";

        /// <summary>Long enough to be worth waiting for, short enough not to be felt.</summary>
        private const int TimeoutSeconds = 6;

        [Serializable]
        private class Feed
        {
            public string version;
            public string url;
            public string notes;
        }

        /// <summary>The build this is, as set in Project Settings.</summary>
        public static string CurrentVersion => Application.version;

        /// <summary>The newer version if there is one, empty otherwise.</summary>
        public static string AvailableVersion { get; private set; } = "";

        /// <summary>Where to get it.</summary>
        public static string DownloadUrl { get; private set; } = "";

        /// <summary>What the newer build says about itself.</summary>
        public static string Notes { get; private set; } = "";

        /// <summary>Raised once, if and only if there is something newer.</summary>
        public static event Action UpdateFound;

        public static UpdateCheck CreateOn(Transform parent)
        {
            var go = new GameObject("Update Check");
            go.transform.SetParent(parent, false);
            return go.AddComponent<UpdateCheck>();
        }

        private void Start()
        {
            StartCoroutine(Ask());
        }

        private IEnumerator Ask()
        {
            using var request = UnityWebRequest.Get(FeedUrl);
            request.timeout = TimeoutSeconds;

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                // Offline, or the feed is not there. Neither is a problem worth
                // showing anybody - it only means we do not know.
                yield break;
            }

            Feed feed;
            try
            {
                feed = JsonUtility.FromJson<Feed>(request.downloadHandler.text);
            }
            catch (Exception)
            {
                yield break;
            }

            if (feed == null || string.IsNullOrWhiteSpace(feed.version))
            {
                yield break;
            }

            if (!IsNewer(feed.version, CurrentVersion))
            {
                yield break;
            }

            AvailableVersion = feed.version.Trim();
            DownloadUrl = (feed.url ?? "").Trim();
            Notes = (feed.notes ?? "").Trim();

            UpdateFound?.Invoke();
        }

        /// <summary>
        /// Whether <paramref name="candidate"/> is a later version than
        /// <paramref name="current"/>.
        ///
        /// Compared part by part as numbers, so 0.10.0 is later than 0.9.0 -
        /// comparing those as text says the opposite, which is the classic way
        /// to ship an update nobody is offered.
        /// </summary>
        public static bool IsNewer(string candidate, string current)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            var left = candidate.Trim().Split('.');
            var right = (current ?? "").Trim().Split('.');

            for (var i = 0; i < Mathf.Max(left.Length, right.Length); i++)
            {
                var a = PartAt(left, i);
                var b = PartAt(right, i);

                if (a != b)
                {
                    return a > b;
                }
            }

            return false;
        }

        private static int PartAt(string[] parts, int index)
        {
            if (index >= parts.Length)
            {
                return 0;
            }

            // Trailing letters - "0.2.0b", "1.0.0-rc1" - are ignored rather than
            // being a parse failure that silently reports "no update".
            var digits = "";
            foreach (var character in parts[index])
            {
                if (!char.IsDigit(character))
                {
                    break;
                }

                digits += character;
            }

            return int.TryParse(digits, out var value) ? value : 0;
        }

        /// <summary>Opens wherever the new build is, or the project page.</summary>
        public static void OpenDownloadPage()
        {
            if (!string.IsNullOrEmpty(DownloadUrl))
            {
                Application.OpenURL(DownloadUrl);
            }
        }
    }
}
