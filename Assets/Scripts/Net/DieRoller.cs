using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// Throws a real die across the table when somebody rolls, lets it settle on
    /// the number the server actually rolled, and leaves it lying there until it
    /// is clicked away.
    ///
    /// Two things about this are not obvious and are load-bearing:
    ///
    /// The board is a ScreenSpaceOverlay canvas with an opaque backdrop, so a 3D
    /// object in the scene is drawn *behind* all of it and is simply invisible.
    /// The die therefore lives far below the board on its own little stage, is
    /// filmed by its own camera, and appears on the board as the picture that
    /// camera takes. Nothing about the game's own camera or canvas changes.
    ///
    /// And the tumble is animated rather than simulated. The number is decided
    /// by the server before the die is thrown; real physics would settle on
    /// whatever face it liked and then have to be snapped round to agree, which
    /// is both visible and a lie about which one is authoritative. Animating it
    /// means the die cannot land on a number the game did not roll.
    /// </summary>
    public class DieRoller : MonoBehaviour
    {
        /// <summary>How far below the board the die's stage sits.</summary>
        private const float StageDepth = -2000f;

        /// <summary>Size of the die's picture on the board.</summary>
        private const float DisplaySize = 230f;

        private RawImage _display;
        private Camera _camera;
        private RenderTexture _texture;
        private Transform _die;
        private Coroutine _rolling;

        /// <summary>What is showing now, so a repeated view does not re-throw it.</summary>
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

            // The picture the die's own camera takes. Sized modestly and off to
            // one side: it is a flourish, not a thing to play around.
            var frame = UIFactory.Group("Die View", root);
            frame.anchorMin = frame.anchorMax = new Vector2(0.80f, 0.62f);
            frame.pivot = new Vector2(0.5f, 0.5f);
            UIFactory.SetSize(frame, DisplaySize, DisplaySize);

            _display = frame.gameObject.AddComponent<RawImage>();

            // Only the die itself answers the pointer, and only while one is
            // lying there. Everywhere else on the board stays live, which is
            // what keeps this from getting in the way of actually playing.
            _display.raycastTarget = true;

            var dismiss = frame.gameObject.AddComponent<Button>();
            dismiss.targetGraphic = _display;
            dismiss.transition = Selectable.Transition.None;
            dismiss.onClick.AddListener(Dismiss);

            // No frames outside play mode, and no render texture at all without
            // a graphics device - which is exactly the case in batchmode, where
            // the tests run. The board still builds and plays perfectly in both;
            // it simply never shows a die. Everything below here is a flourish,
            // so it is never allowed to be the reason something fails.
            if (!Application.isPlaying || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                root.gameObject.SetActive(false);
                return;
            }

            BuildStage();
            root.gameObject.SetActive(false);
        }

        /// <summary>
        /// The die, its floor and its camera, all parked far below the board.
        ///
        /// Isolated by distance rather than by a layer: layers live in project
        /// settings and would have to be reserved and kept in step, whereas the
        /// board's own camera simply cannot see this far - it sits at y=12 with
        /// the default 1000 of draw distance, and this is two thousand below it.
        /// </summary>
        private void BuildStage()
        {
            var stage = new GameObject("Die Stage") { hideFlags = HideFlags.DontSave };
            stage.transform.position = new Vector3(0f, StageDepth, 0f);

            var model = Resources.Load<GameObject>("Models/Die");
            if (model == null)
            {
                Debug.LogWarning("DieRoller found no die model at Resources/Models/Die.");
                return;
            }

            var die = Instantiate(model, stage.transform);
            die.transform.localPosition = Vector3.zero;
            _die = die.transform;

            // Normalised so the throw reads the same whatever scale the model
            // was exported at.
            var bounds = MeshBounds(die);
            if (bounds > 0f)
            {
                die.transform.localScale = Vector3.one * (1.6f / bounds);
            }

            _texture = new RenderTexture(512, 512, 16) { name = "Die", antiAliasing = 4 };
            _display.texture = _texture;

            var cameraObject = new GameObject("Die Camera") { hideFlags = HideFlags.DontSave };
            cameraObject.transform.SetParent(stage.transform, false);
            cameraObject.transform.localPosition = new Vector3(0f, 2.6f, -3.4f);
            cameraObject.transform.localRotation = Quaternion.Euler(34f, 0f, 0f);

            _camera = cameraObject.AddComponent<Camera>();
            _camera.targetTexture = _texture;
            _camera.orthographic = false;
            _camera.fieldOfView = 40f;
            _camera.nearClipPlane = 0.1f;

            // Only ever films its own stage, and clears to nothing so the board
            // shows through around the die.
            _camera.farClipPlane = 20f;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _camera.enabled = false;

            // A light of its own, since nothing else down here lights anything.
            var lightObject = new GameObject("Die Light") { hideFlags = HideFlags.DontSave };
            lightObject.transform.SetParent(stage.transform, false);
            lightObject.transform.localRotation = Quaternion.Euler(48f, -28f, 0f);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.15f;
            light.color = new Color(0.98f, 0.97f, 1f);
        }

        private static float MeshBounds(GameObject model)
        {
            var largest = 0f;
            foreach (var filter in model.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh != null)
                {
                    // Largest single axis, not the diagonal: a cube normalised
                    // by its diagonal comes out noticeably smaller than asked
                    // for, and this is framed tightly enough to notice.
                    var size = filter.sharedMesh.bounds.size;
                    largest = Mathf.Max(largest, Mathf.Max(size.x, Mathf.Max(size.y, size.z)));
                }
            }

            return largest;
        }

        /// <summary>
        /// Throws the die and lands it on <paramref name="value"/>. Rolling the
        /// same number again does nothing - the die already lying there is
        /// showing the right answer, and re-throwing it would look like a second
        /// roll that never happened.
        /// </summary>
        public void Show(int value)
        {
            if (_die == null || value < 1 || value > 6 || value == _showing)
            {
                return;
            }

            _showing = value;
            gameObject.SetActive(true);
            transform.SetAsLastSibling();

            if (_camera != null)
            {
                _camera.enabled = true;
            }

            if (_rolling != null)
            {
                StopCoroutine(_rolling);
            }

            _rolling = StartCoroutine(Throw(value));
        }

        /// <summary>
        /// Clears the die away, which is what clicking it does.
        ///
        /// The rolled number is deliberately remembered. The board refreshes on
        /// every message from the server and the roll stays on the record until
        /// the turn ends, so forgetting it here would have the die thrown again
        /// the instant anything else happened - clicking it away would not stick.
        /// </summary>
        public void Dismiss()
        {
            if (_rolling != null)
            {
                StopCoroutine(_rolling);
                _rolling = null;
            }

            if (_camera != null)
            {
                _camera.enabled = false;
            }

            gameObject.SetActive(false);
        }

        /// <summary>
        /// Clears the die and forgets the roll, so the next one is thrown afresh.
        /// Called when a turn comes round to a roll that has not happened yet.
        /// </summary>
        public void Rearm()
        {
            // Called from every board refresh, so it does nothing when there is
            // nothing to undo.
            if (_showing == -1 && !gameObject.activeSelf)
            {
                return;
            }

            _showing = -1;
            Dismiss();
        }

        private IEnumerator Throw(int value)
        {
            var resting = FaceUp(value);

            // Comes in from off to one side and tumbles as it crosses, so it
            // reads as thrown rather than placed.
            var from = new Vector3(-3.4f, 1.9f, 1.1f);
            var to = Vector3.zero;
            var spin = new Vector3(Random.Range(540f, 900f), Random.Range(360f, 720f), Random.Range(540f, 900f));
            var start = Random.rotation;

            const float travel = 0.85f;
            var elapsed = 0f;

            while (elapsed < travel)
            {
                elapsed += Time.deltaTime;
                var t = Mathf.Clamp01(elapsed / travel);

                // Decelerating tumble, with a couple of bounces on the way in.
                var eased = 1f - Mathf.Pow(1f - t, 3f);
                var hop = Mathf.Abs(Mathf.Sin(t * Mathf.PI * 2.2f)) * (1f - t) * 1.3f;

                _die.localPosition = Vector3.Lerp(from, to, eased) + Vector3.up * hop;
                _die.localRotation = start * Quaternion.Euler(spin * eased);
                yield return null;
            }

            // Settles onto the face the game actually rolled. Short enough to
            // read as the die coming to rest rather than being turned.
            var settleFrom = _die.localRotation;
            const float settle = 0.28f;
            elapsed = 0f;

            while (elapsed < settle)
            {
                elapsed += Time.deltaTime;
                var t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / settle));

                _die.localRotation = Quaternion.Slerp(settleFrom, resting, t);
                _die.localPosition = Vector3.Lerp(_die.localPosition, to, t);
                yield return null;
            }

            _die.localRotation = resting;
            _die.localPosition = to;
            _rolling = null;
        }

        /// <summary>
        /// The orientation that leaves a given number face up.
        ///
        /// This is the one part that depends on how the model itself was built -
        /// which face carries which number, and which way it points at rest. It
        /// assumes the usual arrangement, with 1 up at no rotation and opposite
        /// faces adding to seven. If a roll shows the wrong number, this table
        /// is the only thing that needs changing, one line per face, and nothing
        /// else about the throw depends on it.
        /// </summary>
        private static Quaternion FaceUp(int value) => value switch
        {
            1 => Quaternion.Euler(0f, 0f, 0f),
            2 => Quaternion.Euler(-90f, 0f, 0f),
            3 => Quaternion.Euler(0f, 0f, 90f),
            4 => Quaternion.Euler(0f, 0f, -90f),
            5 => Quaternion.Euler(90f, 0f, 0f),
            _ => Quaternion.Euler(180f, 0f, 0f)
        };

        private void OnDestroy()
        {
            if (_texture == null)
            {
                return;
            }

            // Unhooked from the camera before being released. Releasing a
            // texture a camera is still pointed at is an error in its own right,
            // and it surfaces during teardown where it is hardest to place.
            if (_camera != null)
            {
                _camera.targetTexture = null;
            }

            _texture.Release();
            Destroy(_texture);
        }
    }
}
