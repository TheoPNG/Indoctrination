using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// Throws a die across the table when you roll, and leaves it lying there
    /// until it is clicked away.
    ///
    /// The die is part of the interface rather than an object in the scene, and
    /// that is not a shortcut - it is the only way it can be seen at all. The
    /// board is a ScreenSpaceOverlay canvas, which is composited after every
    /// camera in the game, so anything in the 3D scene is drawn underneath the
    /// whole board no matter what camera, depth or layer it is given. A 3D die
    /// was invisible for exactly that reason. Drawn as part of the board, it
    /// cannot be hidden by it.
    ///
    /// It also tumbles rather than simulating: the number is decided by the
    /// server before the throw, so the die is shown landing on the number the
    /// game rolled instead of being left to disagree with it.
    /// </summary>
    public class DieRoller : MonoBehaviour
    {
        /// <summary>How big the die is on the board.</summary>
        private const float DieSize = 104f;

        /// <summary>How long the die takes to cross the table.</summary>
        private const float TravelSeconds = 1.15f;

        private RectTransform _die;
        private Image _face;
        private Coroutine _rolling;

        /// <summary>The roll on the table, so a repeated view does not re-throw it.</summary>
        private int _showing = -1;

        public static DieRoller CreateOn(Transform canvas)
        {
            var go = new GameObject("Die Roller", typeof(RectTransform));
            go.transform.SetParent(canvas, false);

            var roller = go.AddComponent<DieRoller>();
            roller.Build((RectTransform)go.transform);
            return roller;
        }

        private void Build(RectTransform root)
        {
            UIFactory.Stretch(root);

            // No graphic on the root, so everywhere the die is not stays live
            // and the board underneath can be played normally.
            _die = UIFactory.Group("Die", root);
            _die.anchorMin = _die.anchorMax = new Vector2(0.5f, 0.5f);
            _die.pivot = new Vector2(0.5f, 0.5f);
            UIFactory.SetSize(_die, DieSize, DieSize);

            _face = _die.gameObject.AddComponent<Image>();
            _face.sprite = BoardArt.DieFace(1);
            _face.preserveAspect = true;

            // Only the die itself takes a click, and only while one is lying
            // there - this is the whole of its interference with the board.
            var dismiss = _die.gameObject.AddComponent<Button>();
            dismiss.targetGraphic = _face;
            dismiss.transition = Selectable.Transition.None;
            dismiss.onClick.AddListener(Dismiss);

            root.gameObject.SetActive(false);
        }

        /// <summary>
        /// Throws the die and lands it on <paramref name="value"/>. Throwing the
        /// same roll again does nothing: the die already on the table is showing
        /// the right number, and re-throwing it would look like a second roll
        /// that never happened.
        /// </summary>
        public void Show(int value)
        {
            if (value < 1 || value > 6 || value == _showing)
            {
                return;
            }

            _showing = value;
            gameObject.SetActive(true);

            // Drawn last so it lands on top of the board rather than behind a
            // compound. Anything added to the canvas after this - a preview, a
            // ritual - reclaims the top for itself.
            transform.SetAsLastSibling();

            if (_rolling != null)
            {
                StopCoroutine(_rolling);
            }

            if (!Application.isPlaying)
            {
                // Nothing animates outside play mode; the die simply rests on
                // its number so the board can still be built and inspected.
                _face.sprite = BoardArt.DieFace(value);
                _die.anchoredPosition = Vector2.zero;
                _die.localRotation = Quaternion.identity;
                return;
            }

            _rolling = StartCoroutine(Throw(value));
        }

        /// <summary>Clears the die away, which is what clicking it does.</summary>
        public void Dismiss()
        {
            if (_rolling != null)
            {
                StopCoroutine(_rolling);
                _rolling = null;
            }

            gameObject.SetActive(false);
        }

        /// <summary>
        /// Clears the die and forgets the roll, so the next one is thrown
        /// afresh. The rolled number deliberately survives an ordinary dismissal
        /// - the board refreshes on every message from the server, and forgetting
        /// it there would throw the same die again a moment after it was
        /// clicked away.
        /// </summary>
        public void Rearm()
        {
            if (_showing == -1 && !gameObject.activeSelf)
            {
                return;
            }

            _showing = -1;
            Dismiss();
        }

        private IEnumerator Throw(int value)
        {
            var canvas = (RectTransform)transform;
            var width = Mathf.Max(600f, canvas.rect.width);
            var height = Mathf.Max(400f, canvas.rect.height);

            // Comes in off the left of the table and crosses it, ending right of
            // centre where nothing else lives.
            var from = new Vector2(-width * 0.46f, height * 0.10f);
            var to = new Vector2(width * 0.22f, -height * 0.06f);

            var spin = Random.Range(760f, 1180f) * (Random.value < 0.5f ? -1f : 1f);
            var elapsed = 0f;

            while (elapsed < TravelSeconds)
            {
                elapsed += Time.deltaTime;
                var t = Mathf.Clamp01(elapsed / TravelSeconds);

                // Slows as it crosses, the way a thrown die loses its energy.
                var eased = 1f - Mathf.Pow(1f - t, 2.6f);

                // Bounces along the way, each one lower than the last.
                var bounce = Mathf.Abs(Mathf.Sin(t * Mathf.PI * 3.1f)) * (1f - t) * (height * 0.13f);

                _die.anchoredPosition = Vector2.Lerp(from, to, eased) + (Vector2.up * bounce);
                _die.localRotation = Quaternion.Euler(0f, 0f, spin * eased);

                // The face flickers while it is turning over, and settles onto
                // the rolled number as it comes to rest.
                _face.sprite = t < 0.82f
                    ? BoardArt.DieFace(Random.Range(1, 7))
                    : BoardArt.DieFace(value);

                yield return null;
            }

            // Comes to rest square on, showing what the game actually rolled.
            var restFrom = _die.localRotation;
            var restAngle = Quaternion.Euler(0f, 0f, Random.Range(-9f, 9f));
            var settle = 0f;

            while (settle < 0.22f)
            {
                settle += Time.deltaTime;
                _die.localRotation = Quaternion.Slerp(
                    restFrom, restAngle, Mathf.SmoothStep(0f, 1f, settle / 0.22f));
                yield return null;
            }

            _face.sprite = BoardArt.DieFace(value);
            _die.anchoredPosition = to;
            _die.localRotation = restAngle;
            _rolling = null;
        }
    }
}
