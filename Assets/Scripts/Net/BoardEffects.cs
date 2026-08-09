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

            target = Mathf.Clamp01(target);

            // A bar already at, or already heading to, this value is left alone.
            // Restarting the tween on every refresh made the bars visibly stutter
            // whenever anything at all redrew the board.
            if (_fillTargets.TryGetValue(bar, out var existing)
                && Mathf.Approximately(existing, target))
            {
                return;
            }

            _fillTargets[bar] = target;

            StopFor(bar);
            _running[bar] = StartCoroutine(FillRoutine(bar, target, duration));
        }

        private readonly Dictionary<Image, float> _fillTargets = new();

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
        public void FlyResource(
            Vector3 fromWorld, Vector3 toWorld, ResourceColor color,
            float delay = 0f, RectTransform landsIn = null)
        {
            FlyPip(fromWorld, toWorld, BoardArt.ColorOf(color), delay, landsIn: landsIn);
        }

        /// <summary>
        /// Throws a coloured mote across the board. Damage travelling from the
        /// card that dealt it into the bar it empties makes the number changing
        /// something you can follow, rather than something you notice afterwards.
        /// </summary>
        public void FlyPip(
            Vector3 fromWorld, Vector3 toWorld, Color color,
            float delay = 0f, float size = 30f, RectTransform landsIn = null)
        {
            if (_flightLayer == null || !Application.isPlaying)
            {
                return;
            }

            StartCoroutine(FlyRoutine(fromWorld, toWorld, color, delay, size, landsIn));
        }

        private IEnumerator FlyRoutine(
            Vector3 fromWorld, Vector3 toWorld, Color color, float delay, float size,
            RectTransform landsIn = null)
        {
            if (delay > 0f)
            {
                yield return new WaitForSeconds(delay);
            }

            if (_flightLayer == null)
            {
                yield break;
            }

            var pipObject = new GameObject("Pip In Flight", typeof(RectTransform), typeof(Image));
            var pip = (RectTransform)pipObject.transform;
            pip.SetParent(_flightLayer, worldPositionStays: false);
            pip.sizeDelta = new Vector2(size, size);
            pip.position = fromWorld;

            var image = pipObject.GetComponent<Image>();
            image.sprite = BoardArt.Disc;
            image.color = color;
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

            // The thing it landed in takes the hit, so the count changing has a
            // visible cause rather than simply being a different number.
            Pop(landsIn);
        }

        // ------------------------------------------------------------- Pulsing

        private readonly Dictionary<Graphic, Coroutine> _pulsing = new();

        /// <summary>
        /// Breathes a control in and out while it is the only move a player has
        /// left, and stops cleanly when it is not. Idempotent, so calling it every
        /// refresh neither stacks pulses nor restarts the one already running.
        /// </summary>
        public void SetPulsing(Graphic graphic, bool pulsing)
        {
            if (graphic == null)
            {
                return;
            }

            if (!pulsing)
            {
                if (_pulsing.TryGetValue(graphic, out var running))
                {
                    if (running != null)
                    {
                        StopCoroutine(running);
                    }

                    // Put the colour back, or the control keeps whatever shade the
                    // pulse happened to be passing through when it stopped.
                    if (_pulseBaseColors.TryGetValue(graphic, out var restore))
                    {
                        graphic.color = restore;
                        _pulseBaseColors.Remove(graphic);
                    }

                    _pulsing.Remove(graphic);
                }

                return;
            }

            if (_pulsing.ContainsKey(graphic))
            {
                return;
            }

            _pulseBaseColors[graphic] = graphic.color;
            _pulsing[graphic] = StartCoroutine(PulseRoutine(graphic));
        }

        private readonly Dictionary<Graphic, Color> _pulseBaseColors = new();

        private IEnumerator PulseRoutine(Graphic graphic)
        {
            var baseColor = graphic.color;

            while (graphic != null)
            {
                var wave = (Mathf.Sin(Time.time * 4f) + 1f) * 0.5f;
                graphic.color = Color.Lerp(baseColor, Color.Lerp(baseColor, Color.white, 0.45f), wave);
                yield return null;
            }
        }

        // ------------------------------------------------------------ Entrances

        /// <summary>
        /// Fades something in where it already sits. Deliberately alpha only:
        /// sliding or scaling an entrance would move the thing while it arrives,
        /// and everything on this board is positioned by a layout that has
        /// already decided where it goes.
        /// </summary>
        public void FadeIn(GameObject target, float duration = 0.22f, float delay = 0f)
        {
            if (target == null || !Application.isPlaying)
            {
                return;
            }

            // Deliberately not "?? AddComponent": ?? uses reference equality and
            // so treats a destroyed component as present, handing back something
            // that throws the moment it is touched. Unity's own == null is the
            // only check that understands destroyed objects.
            var group = target.GetComponent<CanvasGroup>();
            if (group == null)
            {
                group = target.AddComponent<CanvasGroup>();
            }

            group.alpha = 0f;
            StartCoroutine(FadeRoutine(group, duration, delay));
        }

        private IEnumerator FadeRoutine(CanvasGroup group, float duration, float delay)
        {
            var waited = 0f;
            while (waited < delay)
            {
                if (group == null)
                {
                    yield break;
                }

                waited += Time.deltaTime;
                yield return null;
            }

            var elapsed = 0f;
            while (elapsed < duration)
            {
                if (group == null)
                {
                    yield break;
                }

                elapsed += Time.deltaTime;
                group.alpha = Smooth(elapsed / duration);
                yield return null;
            }

            if (group != null)
            {
                group.alpha = 1f;
            }
        }

        /// <summary>
        /// Knocks something briefly larger and lets it settle - a resource pip
        /// taking a hit as one lands in it, so the count changing has a cause you
        /// can see rather than just being a different number.
        /// </summary>
        public void Pop(RectTransform target, float strength = 1.35f, float duration = 0.28f)
        {
            if (target == null || !Application.isPlaying)
            {
                return;
            }

            StopFor(target);
            _running[target] = StartCoroutine(PopRoutine(target, strength, duration));
        }

        private IEnumerator PopRoutine(RectTransform target, float strength, float duration)
        {
            // Read from the transform rather than remembered, so overlapping pops
            // cannot compound into a permanently enlarged widget.
            var baseScale = Vector3.one;
            var elapsed = 0f;

            while (elapsed < duration && target != null)
            {
                elapsed += Time.deltaTime;
                var t = Mathf.Clamp01(elapsed / duration);

                // Straight up, then an eased fall back, which reads as impact.
                var swell = t < 0.3f
                    ? Mathf.SmoothStep(0f, 1f, t / 0.3f)
                    : 1f - Mathf.SmoothStep(0f, 1f, (t - 0.3f) / 0.7f);

                target.localScale = baseScale * (1f + ((strength - 1f) * swell));
                yield return null;
            }

            if (target != null)
            {
                target.localScale = baseScale;
            }

            _running.Remove(target);
        }

        /// <summary>
        /// Lifts a card slightly under the pointer, so the board answers back
        /// when it is pointed at rather than only when it is clicked.
        /// </summary>
        public void Hover(RectTransform target, bool hovering, float lift = 1.06f)
        {
            if (target == null || !Application.isPlaying)
            {
                return;
            }

            StopFor(target);
            _running[target] = StartCoroutine(HoverRoutine(target, hovering, lift));
        }

        private IEnumerator HoverRoutine(RectTransform target, bool hovering, float lift)
        {
            // The board scales cards to fit, so "back to normal" is whatever scale
            // the layout gave this card, not one.
            var from = target.localScale;
            var baseScale = _restingScales.TryGetValue(target, out var known) ? known : from;

            if (!_restingScales.ContainsKey(target))
            {
                _restingScales[target] = from;
                baseScale = from;
            }

            var to = hovering ? baseScale * lift : baseScale;
            const float duration = 0.12f;
            var elapsed = 0f;

            while (elapsed < duration && target != null)
            {
                elapsed += Time.deltaTime;
                target.localScale = Vector3.Lerp(from, to, Smooth(elapsed / duration));
                yield return null;
            }

            if (target != null)
            {
                target.localScale = to;
            }

            _running.Remove(target);
        }

        private readonly Dictionary<RectTransform, Vector3> _restingScales = new();

        /// <summary>
        /// Drops every animation in flight. The driver outlives any one board -
        /// it survives scene changes - so a board being torn down has to say so,
        /// or its coroutines carry on reaching for widgets that no longer exist.
        /// </summary>
        public void CancelAll()
        {
            StopAllCoroutines();
            _running.Clear();
            _fillTargets.Clear();
            _pulsing.Clear();
            _pulseBaseColors.Clear();
            _restingScales.Clear();
            _shake = null;
        }

        /// <summary>Forgets a card's resting scale, for when the board is rebuilt.</summary>
        public void ForgetRestingScale(RectTransform target)
        {
            if (target != null)
            {
                _restingScales.Remove(target);
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
