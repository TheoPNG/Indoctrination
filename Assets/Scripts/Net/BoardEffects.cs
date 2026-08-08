using System.Collections;
using System.Collections.Generic;
using Indoctrination.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// The board's motion: bars that slide to their new value instead of
    /// snapping, cards that swell and glow as they activate, resources that fly
    /// from the button that granted them to the player who now holds them, and a
    /// shake when something lands hard.
    ///
    /// All of it is presentation only. Nothing here can change the game - it
    /// reacts to state that has already been decided by the server, so a dropped
    /// frame or a skipped animation never costs a player anything.
    /// </summary>
    public class BoardEffects : MonoBehaviour
    {
        private static BoardEffects _instance;

        /// <summary>The shared driver, created on demand.</summary>
        public static BoardEffects Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("Board Effects") { hideFlags = HideFlags.DontSave };

                    // Only meaningful in play mode, and an outright error outside
                    // it - the smoke test builds the whole interface in the Editor.
                    if (Application.isPlaying)
                    {
                        DontDestroyOnLoad(go);
                    }

                    _instance = go.AddComponent<BoardEffects>();
                }

                return _instance;
            }
        }

        private RectTransform _flightLayer;
        private Coroutine _shake;

        /// <summary>
        /// Where flying pips are drawn. Kept at the very top of the canvas so a
        /// pip in transit is never hidden behind the panels it passes over.
        /// </summary>
        public void SetFlightLayer(RectTransform layer) => _flightLayer = layer;

        // ------------------------------------------------------------ Bar slides

        /// <summary>
        /// Eases a fill bar toward a value. Repeated calls retarget the same bar
        /// rather than stacking, so a burst of damage animates as one movement.
        /// </summary>
        public void FillTo(Image bar, float target, float duration = 0.35f)
        {
            if (bar == null)
            {
                return;
            }

            StopFor(bar);
            _running[bar] = StartCoroutine(FillRoutine(bar, Mathf.Clamp01(target), duration));
        }

        private readonly Dictionary<Object, Coroutine> _running = new();

        private void StopFor(Object key)
        {
            if (_running.TryGetValue(key, out var existing) && existing != null)
            {
                StopCoroutine(existing);
            }

            _running.Remove(key);
        }

        private IEnumerator FillRoutine(Image bar, float target, float duration)
        {
            var from = bar.fillAmount;
            var elapsed = 0f;

            while (elapsed < duration && bar != null)
            {
                elapsed += Time.deltaTime;
                bar.fillAmount = Mathf.Lerp(from, target, Smooth(elapsed / duration));
                yield return null;
            }

            if (bar != null)
            {
                bar.fillAmount = target;
            }

            _running.Remove(bar);
        }

        // ---------------------------------------------------------- Card pulses

        /// <summary>
        /// Swells a card and lights it up, for a unit that has just activated.
        /// The glow is a child behind the card's own contents, so it reads as
        /// light spilling out rather than a border drawn on top.
        /// </summary>
        public void PulseCard(RectTransform card, Color tint, float scale = 1.12f, float duration = 0.55f)
        {
            if (card == null)
            {
                return;
            }

            StopFor(card);
            _running[card] = StartCoroutine(PulseRoutine(card, tint, scale, duration));
        }

        private IEnumerator PulseRoutine(RectTransform card, Color tint, float scale, float duration)
        {
            var glowObject = new GameObject("Activation Glow", typeof(RectTransform), typeof(Image));
            var glow = (RectTransform)glowObject.transform;
            glow.SetParent(card, false);
            glow.SetAsFirstSibling();
            glow.anchorMin = Vector2.zero;
            glow.anchorMax = Vector2.one;
            glow.offsetMin = new Vector2(-26f, -26f);
            glow.offsetMax = new Vector2(26f, 26f);

            var image = glowObject.GetComponent<Image>();
            image.sprite = BoardArt.Glow;
            image.raycastTarget = false;

            var baseScale = card.localScale;
            var elapsed = 0f;

            while (elapsed < duration && card != null)
            {
                elapsed += Time.deltaTime;
                var t = Mathf.Clamp01(elapsed / duration);

                // Out and back, so the card ends exactly where it started even if
                // the animation is interrupted by the next board rebuild.
                var swell = Mathf.Sin(t * Mathf.PI);

                card.localScale = baseScale * (1f + ((scale - 1f) * swell));
                image.color = new Color(tint.r, tint.g, tint.b, 0.75f * swell);
                yield return null;
            }

            if (card != null)
            {
                card.localScale = baseScale;
            }

            if (glowObject != null)
            {
                Destroy(glowObject);
            }

            _running.Remove(card);
        }

        // --------------------------------------------------------- Flying pips

        /// <summary>
        /// Sends a resource pip from one point on screen to another - the button
        /// that granted it to the player who now holds it - so a collected
        /// resource is something you watch arrive rather than a number that
        /// changes when you look away.
        /// </summary>
        public void FlyResource(Vector3 fromWorld, Vector3 toWorld, ResourceColor color, float delay = 0f)
        {
            if (_flightLayer == null)
            {
                return;
            }

            StartCoroutine(FlyRoutine(fromWorld, toWorld, color, delay));
        }

        private IEnumerator FlyRoutine(Vector3 fromWorld, Vector3 toWorld, ResourceColor color, float delay)
        {
            if (delay > 0f)
            {
                yield return new WaitForSeconds(delay);
            }

            if (_flightLayer == null)
            {
                yield break;
            }

            var pipObject = new GameObject($"{color} In Flight", typeof(RectTransform), typeof(Image));
            var pip = (RectTransform)pipObject.transform;
            pip.SetParent(_flightLayer, worldPositionStays: false);
            pip.sizeDelta = new Vector2(30f, 30f);
            pip.position = fromWorld;

            var image = pipObject.GetComponent<Image>();
            image.sprite = BoardArt.Disc;
            image.color = BoardArt.ColorOf(color);
            image.raycastTarget = false;

            const float duration = 0.5f;
            var elapsed = 0f;

            // Arcs upward on the way across, which reads as thrown rather than dragged.
            var lift = Vector3.up * (Vector3.Distance(fromWorld, toWorld) * 0.18f);

            while (elapsed < duration && pipObject != null)
            {
                elapsed += Time.deltaTime;
                var t = Mathf.Clamp01(elapsed / duration);

                var straight = Vector3.Lerp(fromWorld, toWorld, Smooth(t));
                pip.position = straight + (lift * Mathf.Sin(t * Mathf.PI));
                pip.localScale = Vector3.one * (1f - (0.35f * t));
                image.color = new Color(image.color.r, image.color.g, image.color.b, 1f - (t * t));

                yield return null;
            }

            if (pipObject != null)
            {
                Destroy(pipObject);
            }
        }

        // -------------------------------------------------------------- Shake

        /// <summary>
        /// Knocks the board about briefly. Deliberately small: it is punctuation
        /// for a unit firing, not an earthquake, and it has to settle before the
        /// player's next click.
        /// </summary>
        public void Shake(RectTransform target, float strength = 9f, float duration = 0.3f)
        {
            if (target == null)
            {
                return;
            }

            if (_shake != null)
            {
                StopCoroutine(_shake);
                target.anchoredPosition = Vector2.zero;
            }

            _shake = StartCoroutine(ShakeRoutine(target, strength, duration));
        }

        private IEnumerator ShakeRoutine(RectTransform target, float strength, float duration)
        {
            var elapsed = 0f;

            while (elapsed < duration && target != null)
            {
                elapsed += Time.deltaTime;

                // Falls off over the shake, so it lands hard and settles.
                var remaining = 1f - Mathf.Clamp01(elapsed / duration);
                var offset = Random.insideUnitCircle * (strength * remaining * remaining);
                target.anchoredPosition = offset;

                yield return null;
            }

            if (target != null)
            {
                target.anchoredPosition = Vector2.zero;
            }

            _shake = null;
        }

        /// <summary>Ease in and out, so nothing starts or stops abruptly.</summary>
        private static float Smooth(float t) => Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
    }
}
