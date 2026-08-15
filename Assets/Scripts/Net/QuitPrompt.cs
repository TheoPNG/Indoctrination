using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// Closing the game, with the consequences said out loud first.
    ///
    /// Quitting mid-game is a resignation - there is no rejoining a game in
    /// progress, so walking away and losing are the same thing whether or not
    /// the player meant them to be. That is worth a sentence before it happens,
    /// and the sentence has to be the true one for the situation: leaving the
    /// title screen costs nothing, leaving a game costs the game, and leaving a
    /// game you are hosting costs everybody else's game too.
    ///
    /// This is the one popup in the board that does block what is behind it.
    /// The others deliberately do not, because a player answering a prompt still
    /// needs to read their own hand - but there is nothing to read here, and a
    /// confirmation you can click past is not a confirmation.
    /// </summary>
    public class QuitPrompt : MonoBehaviour
    {
        private RectTransform _panel;
        private Text _warningText;
        private Text _confirmLabel;
        private bool _quitting;

        public static QuitPrompt CreateOn(Transform canvas)
        {
            var go = new GameObject("Quit Prompt", typeof(RectTransform));
            go.transform.SetParent(canvas, false);

            var prompt = go.AddComponent<QuitPrompt>();
            prompt.Build();
            return prompt;
        }

        private void Build()
        {
            var root = (RectTransform)transform;
            UIFactory.Stretch(root);

            // Dark enough to say "answer this first", not so dark that the board
            // disappears behind it.
            var scrim = UIFactory.Panel("Quit Scrim", root, new Color(
                UITheme.Void.r, UITheme.Void.g, UITheme.Void.b, 0.72f));
            UIFactory.Stretch(scrim);

            _panel = UIFactory.Panel("Quit Box", root, UITheme.SurfaceRaised);
            UITheme.Frame(_panel.GetComponent<Image>(), 1f, UITheme.Blood);
            _panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 0.5f);
            _panel.pivot = new Vector2(0.5f, 0.5f);
            UIFactory.SetSize(_panel, 560f, 250f);

            var layout = UIFactory.VerticalLayout(
                _panel, 14, new RectOffset(28, 28, 24, 24), controlHeight: true);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childForceExpandWidth = true;

            var title = UIFactory.Label(
                "Quit Title", _panel, "LEAVE THE GAME", 22, TextAnchor.MiddleCenter, UITheme.Signal);
            title.fontStyle = FontStyle.Bold;
            Row(title.rectTransform, 30f);

            _warningText = UIFactory.Label(
                "Quit Warning", _panel, "", 15, TextAnchor.UpperCenter, UITheme.Bone);
            var warningRow = _warningText.gameObject.AddComponent<LayoutElement>();
            warningRow.flexibleHeight = 1f;
            warningRow.flexibleWidth = 1f;

            var buttons = UIFactory.Group("Quit Buttons", _panel);
            Row((RectTransform)buttons.transform, 38f);
            var buttonLayout = UIFactory.HorizontalLayout(buttons, 12, new RectOffset(0, 0, 0, 0));
            buttonLayout.childAlignment = TextAnchor.MiddleCenter;

            UIFactory.ButtonWithLabel(
                "Quit Cancel", buttons, "Keep playing", Close, UITheme.ButtonQuiet, 190f, 38f);

            var confirm = UIFactory.ButtonWithLabel(
                "Quit Confirm", buttons, "Quit", Confirm, UITheme.Blood, 190f, 38f);
            _confirmLabel = confirm.GetComponentInChildren<Text>();

            gameObject.SetActive(false);
        }

        private static void Row(RectTransform rect, float height)
        {
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.minHeight = element.preferredHeight = height;
            element.flexibleWidth = 1f;
        }

        /// <summary>
        /// Opens the prompt, having first worked out what quitting would
        /// actually cost from here.
        /// </summary>
        public void Open()
        {
            if (_quitting)
            {
                return;
            }

            var view = NetworkGameManager.Instance?.View;
            var you = view?.Viewer;
            var playing = you is { isAlive: true } && view is { isGameOver: false };

            var network = NetworkManager.Singleton;
            var hosting = network != null && network.IsListening && network.IsServer;
            var guests = hosting ? Mathf.Max(0, network.ConnectedClientsIds.Count - 1) : 0;

            var warning = playing
                ? "Quitting resigns your game.\n\nYou are out for good the moment you go - "
                  + "there is no rejoining a game in progress, and the table plays on without you."
                : "This closes Indoctrination.";

            if (hosting && guests > 0)
            {
                warning += guests == 1
                    ? "\n\nYou are hosting. The other player's game ends here too."
                    : $"\n\nYou are hosting. All {guests} other players lose this game as well.";
            }

            _warningText.text = warning;
            _confirmLabel.text = playing ? "Resign and quit" : "Quit";

            gameObject.SetActive(true);
            transform.SetAsLastSibling();
        }

        public void Close()
        {
            if (!_quitting)
            {
                gameObject.SetActive(false);
            }
        }

        private void Confirm()
        {
            if (_quitting)
            {
                return;
            }

            _quitting = true;
            _confirmLabel.text = "Leaving...";
            StartCoroutine(Leave());
        }

        /// <summary>
        /// Resigns, gives the resignation a moment to reach the server, and only
        /// then tears the connection down.
        ///
        /// The order matters. Shutting down first would drop the connection with
        /// the resignation still in hand, and the table would see a player who
        /// vanished rather than one who conceded - which is a different thing to
        /// everyone still sitting there.
        /// </summary>
        private IEnumerator Leave()
        {
            var manager = NetworkGameManager.Instance;
            var view = manager?.View;
            var you = view?.Viewer;

            if (manager != null && you is { isAlive: true } && view is { isGameOver: false })
            {
                manager.RequestResignRpc();

                var waited = 0f;
                while (waited < 0.4f)
                {
                    waited += Time.unscaledDeltaTime;
                    yield return null;
                }
            }

            // Off the noticeboard first, then off the network. A hosted game
            // left listed advertises a table nobody can sit at.
            OnlineSession.Instance?.CloseAsync();

            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.Shutdown();
                yield return null;
            }

            Application.Quit();

#if UNITY_EDITOR
            // Application.Quit does nothing in the editor, and a quit button
            // that does nothing when you are testing reads as a broken button.
            UnityEditor.EditorApplication.isPlaying = false;
#endif
        }
    }
}
