using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// Real dice, thrown across a table with real physics - one per player.
    ///
    /// They are the supplied die model, dropped in with a shove and a spin, and
    /// they tumble, bounce off the rails and come to rest wherever they come to
    /// rest. **Nothing moves them after they land.** An earlier version slid
    /// each die to a tidy resting spot once it stopped, which is exactly why
    /// they appeared not to stay where they fell.
    ///
    /// They are filmed by their own camera and that picture is laid over the
    /// board, because the board is a ScreenSpaceOverlay canvas - Unity
    /// composites those after every camera in the game, so nothing in the scene
    /// can be drawn over one by any means. Showing 3D on this board without the
    /// intermediate picture would mean moving the whole interface onto a
    /// camera-space canvas.
    /// </summary>
    public class DieRoller : MonoBehaviour
    {
        // ------------------------------------------------------------------
        //  THE FACE MAP - this is the bit to correct once you can watch one land.
        //
        //  Which number is printed on which side of the model is not something
        //  that can be read out of the file, so this is a starting guess: it
        //  says "the side facing +Y carries a 1", and so on round the die.
        //
        //  To fix it: roll until you see a face clearly, note which number is
        //  printed there and which way that side is pointing, and correct the
        //  entry. Opposite sides of a real die add to seven, so fixing one of a
        //  pair fixes its opposite too. Nothing else in here depends on these
        //  values - they are only used to turn a landed die onto the number the
        //  game actually rolled.
        // ------------------------------------------------------------------
        private static readonly (Vector3 Side, int Number)[] FaceMap =
        {
            (Vector3.up, 1),
            (Vector3.down, 6),
            (Vector3.right, 3),
            (Vector3.left, 4),
            (Vector3.forward, 2),
            (Vector3.back, 5)
        };

        /// <summary>How far below the board the dice table sits.</summary>
        private const float StageDepth = -2000f;

        /// <summary>Half-width of the table the dice are thrown onto.</summary>
        private const float TableHalfWidth = 5f;

        /// <summary>How big a die is once the model has been normalised.</summary>
        private const float DieSize = 1.15f;

        /// <summary>Longest the dice may tumble before they are made to settle.</summary>
        private const float MaxTumbleSeconds = 3.2f;

        private RawImage _display;
        private RectTransform _dismissArea;
        private Camera _camera;
        private RenderTexture _texture;
        private Transform _stage;
        private GameObject _model;
        private PhysicsMaterial _contact;
        private Coroutine _rolling;
        private Vector2Int _builtFor;

        private sealed class Thrown
        {
            public Transform Die;
            public Rigidbody Body;
            public Text Label;
            public int Value;
        }

        private readonly List<Thrown> _dice = new();

        /// <summary>The whole table's roll, so a repeated view does not re-throw it.</summary>
        private string _showing = "";

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

            // The picture covers the whole board so the dice can roll right
            // across it. It never takes a click - the board underneath stays
            // completely live.
            _display = root.gameObject.AddComponent<RawImage>();
            _display.raycastTarget = false;

            _dismissArea = UIFactory.Group("Dice", root);
            _dismissArea.anchorMin = _dismissArea.anchorMax = new Vector2(0.5f, 0.5f);
            _dismissArea.pivot = new Vector2(0.5f, 0.5f);
            UIFactory.SetSize(_dismissArea, 200f, 200f);

            var hit = _dismissArea.gameObject.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f);

            var dismiss = _dismissArea.gameObject.AddComponent<Button>();
            dismiss.targetGraphic = hit;
            dismiss.transition = Selectable.Transition.None;
            dismiss.onClick.AddListener(Dismiss);
            _dismissArea.gameObject.SetActive(false);

            // Nothing renders outside play mode, and there is no render texture
            // at all without a graphics device - which is the case in batchmode,
            // where the tests run. The board plays normally either way; it
            // simply never shows dice. A flourish must never be the reason
            // something fails.
            if (!Application.isPlaying || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                root.gameObject.SetActive(false);
                return;
            }

            BuildTable();
            root.gameObject.SetActive(false);
        }

        /// <summary>
        /// The table, the rails that keep the dice on it, the camera and the
        /// light. Parked far below the board and isolated by distance rather
        /// than by a layer: the board's own camera sits at y=12 with the default
        /// 1000 of draw distance and simply cannot see this far.
        /// </summary>
        private void BuildTable()
        {
            _model = Resources.Load<GameObject>("Models/Die");
            if (_model == null)
            {
                Debug.LogWarning("DieRoller found no die model at Resources/Models/Die.");
                return;
            }

            var stage = new GameObject("Die Stage") { hideFlags = HideFlags.DontSave };
            stage.transform.position = new Vector3(0f, StageDepth, 0f);
            _stage = stage.transform;

            _contact = new PhysicsMaterial("Die Felt")
            {
                bounciness = 0.28f,
                dynamicFriction = 0.5f,
                staticFriction = 0.5f,
                bounceCombine = PhysicsMaterialCombine.Maximum,
                frictionCombine = PhysicsMaterialCombine.Average
            };

            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Table";
            floor.transform.SetParent(stage.transform, false);
            floor.transform.localScale = new Vector3(TableHalfWidth * 2f, 0.5f, TableHalfWidth * 2f);
            floor.transform.localPosition = new Vector3(0f, -0.25f, 0f);
            floor.GetComponent<MeshRenderer>().enabled = false;
            floor.GetComponent<BoxCollider>().material = _contact;

            // Rails, so a hard throw cannot put a die off the table and out of
            // shot. Invisible: a backstop, not scenery.
            for (var side = 0; side < 4; side++)
            {
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = $"Rail {side}";
                wall.transform.SetParent(stage.transform, false);
                wall.GetComponent<MeshRenderer>().enabled = false;
                wall.GetComponent<BoxCollider>().material = _contact;

                var along = side % 2 == 0;
                var sign = side < 2 ? 1f : -1f;
                wall.transform.localScale = along
                    ? new Vector3(TableHalfWidth * 2f, 5f, 0.5f)
                    : new Vector3(0.5f, 5f, TableHalfWidth * 2f);
                wall.transform.localPosition = along
                    ? new Vector3(0f, 2.5f, sign * TableHalfWidth)
                    : new Vector3(sign * TableHalfWidth, 2.5f, 0f);
            }

            var cameraObject = new GameObject("Die Camera") { hideFlags = HideFlags.DontSave };
            cameraObject.transform.SetParent(stage.transform, false);
            cameraObject.transform.localPosition = new Vector3(0f, 8.4f, -6.2f);
            cameraObject.transform.localRotation = Quaternion.Euler(52f, 0f, 0f);

            _camera = cameraObject.AddComponent<Camera>();
            _camera.fieldOfView = 48f;
            _camera.nearClipPlane = 0.3f;
            _camera.farClipPlane = 45f;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _camera.enabled = false;

            var lightObject = new GameObject("Die Light") { hideFlags = HideFlags.DontSave };
            lightObject.transform.SetParent(stage.transform, false);
            lightObject.transform.localRotation = Quaternion.Euler(50f, -30f, 0f);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.6f;

            EnsureTexture();
        }

        /// <summary>
        /// Keeps the picture the same shape as the board, so the dice are not
        /// stretched out of square when the window changes.
        /// </summary>
        private void EnsureTexture()
        {
            var width = Mathf.Clamp(Screen.width, 320, 1920);
            var height = Mathf.Clamp(Screen.height, 240, 1080);

            if (_texture != null && _builtFor == new Vector2Int(width, height))
            {
                return;
            }

            if (_texture != null)
            {
                _camera.targetTexture = null;
                _texture.Release();
                Destroy(_texture);
            }

            _builtFor = new Vector2Int(width, height);
            _texture = new RenderTexture(width, height, 24) { name = "Dice" };
            _camera.targetTexture = _texture;
            _display.texture = _texture;
        }

        /// <summary>One player's die, as the board wants it thrown.</summary>
        public readonly struct Roll
        {
            public Roll(string name, int value, bool isViewer)
            {
                Name = name;
                Value = value;
                IsViewer = isViewer;
            }

            public string Name { get; }
            public int Value { get; }
            public bool IsViewer { get; }
        }

        /// <summary>
        /// Throws one die per player. Throwing the same set again does nothing:
        /// the dice already on the table show the right numbers, and re-throwing
        /// would look like a roll that never happened.
        /// </summary>
        public void Show(IReadOnlyList<Roll> rolls)
        {
            if (_stage == null || rolls == null || rolls.Count == 0)
            {
                return;
            }

            var signature = string.Join(",", rolls.Select(roll => $"{roll.Name}:{roll.Value}"));
            if (signature == _showing)
            {
                return;
            }

            _showing = signature;
            gameObject.SetActive(true);
            transform.SetAsLastSibling();

            EnsureTexture();
            _camera.enabled = true;

            if (_rolling != null)
            {
                StopCoroutine(_rolling);
            }

            BuildDice(rolls);
            _rolling = StartCoroutine(Throw());
        }

        /// <summary>
        /// Makes one die and one label per roll, reusing whatever is already on
        /// the table so a four-player game does not build four fresh dice every
        /// turn.
        /// </summary>
        private void BuildDice(IReadOnlyList<Roll> rolls)
        {
            while (_dice.Count < rolls.Count)
            {
                var die = Instantiate(_model, _stage);
                die.name = $"Die {_dice.Count}";
                die.hideFlags = HideFlags.DontSave;

                // Normalised on its largest axis, so the throw reads the same
                // whatever scale the model happens to have been exported at.
                var widest = 0f;
                foreach (var filter in die.GetComponentsInChildren<MeshFilter>())
                {
                    if (filter.sharedMesh != null)
                    {
                        var size = filter.sharedMesh.bounds.size;
                        widest = Mathf.Max(widest, Mathf.Max(size.x, Mathf.Max(size.y, size.z)));
                    }
                }

                if (widest > 0f)
                {
                    die.transform.localScale = Vector3.one * (DieSize / widest);
                }

                var collider = die.AddComponent<BoxCollider>();
                collider.size = Vector3.one * (widest > 0f ? widest : 1f);
                collider.material = _contact;

                var body = die.AddComponent<Rigidbody>();
                body.mass = 1f;
                body.linearDamping = 0.05f;
                body.angularDamping = 0.1f;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                body.interpolation = RigidbodyInterpolation.Interpolate;

                // Whose die it is, written under it. With several on the table
                // the numbers mean nothing without knowing who threw which.
                var label = UIFactory.Label(
                    $"Die Owner {_dice.Count}", transform, "", 15, TextAnchor.MiddleCenter, UITheme.Bone);
                label.fontStyle = FontStyle.Bold;
                label.raycastTarget = false;
                label.rectTransform.anchorMin = label.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                UIFactory.SetSize(label.rectTransform, 220f, 26f);

                var shadow = label.gameObject.AddComponent<Outline>();
                shadow.effectColor = new Color(0f, 0f, 0f, 0.95f);
                shadow.effectDistance = new Vector2(1.5f, -1.5f);

                _dice.Add(new Thrown { Die = die.transform, Body = body, Label = label });
            }

            for (var i = 0; i < _dice.Count; i++)
            {
                var thrown = _dice[i];
                var live = i < rolls.Count;

                thrown.Die.gameObject.SetActive(live);
                thrown.Label.gameObject.SetActive(live);

                if (!live)
                {
                    continue;
                }

                thrown.Value = rolls[i].Value;

                // The rolled number is written beside the name on purpose. It is
                // what the game is using, so while the face map is still a guess
                // this is what says whether the die agrees with it.
                thrown.Label.text = $"{(rolls[i].IsViewer ? "YOU" : rolls[i].Name)}  ·  {rolls[i].Value}";
                thrown.Label.color = rolls[i].IsViewer ? UITheme.Signal : UITheme.Bone;
            }
        }

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

            if (_dismissArea != null)
            {
                _dismissArea.gameObject.SetActive(false);
            }

            gameObject.SetActive(false);
        }

        /// <summary>
        /// Clears the dice and forgets the roll, so the next one is thrown
        /// afresh. The numbers deliberately survive an ordinary dismissal - the
        /// board refreshes on every message from the server, and forgetting them
        /// there would throw the same dice again the moment they were clicked
        /// away.
        /// </summary>
        public void Rearm()
        {
            if (_showing.Length == 0 && !gameObject.activeSelf)
            {
                return;
            }

            _showing = "";
            Dismiss();
        }

        private IEnumerator Throw()
        {
            _dismissArea.gameObject.SetActive(false);

            var live = _dice.Where(die => die.Die.gameObject.activeSelf).ToList();

            // Thrown in together from one end, spread across the table so they
            // do not land in a heap.
            for (var i = 0; i < live.Count; i++)
            {
                var lane = live.Count == 1 ? 0f : Mathf.Lerp(-2.4f, 2.4f, i / (live.Count - 1f));
                var thrown = live[i];

                thrown.Body.isKinematic = false;
                thrown.Die.localPosition = new Vector3(-TableHalfWidth + 1f, 2.6f + (i * 0.4f), lane);
                thrown.Die.localRotation = Random.rotation;

                thrown.Body.linearVelocity = new Vector3(
                    Random.Range(5.5f, 7.5f), 0.5f, Random.Range(-1.2f, 1.2f) - (lane * 0.25f));
                thrown.Body.angularVelocity = new Vector3(
                    Random.Range(-14f, 14f), Random.Range(-14f, 14f), Random.Range(-14f, 14f));
            }

            // Tumble until they have all run out of energy, or until they have
            // had long enough - dice that will not settle must not hold up the
            // board. The labels follow them the whole way.
            var tumbling = 0f;
            while (tumbling < MaxTumbleSeconds)
            {
                tumbling += Time.deltaTime;
                PlaceLabels();

                if (tumbling > 0.7f && live.All(die =>
                        die.Body.linearVelocity.magnitude < 0.3f
                        && die.Body.angularVelocity.magnitude < 0.8f))
                {
                    break;
                }

                yield return null;
            }

            // The numbers were decided by the server before the throw, so each
            // die is turned onto its own - **in place**. Only the rotation is
            // touched: a die that has been moved after it stopped does not look
            // like a die that landed there.
            foreach (var thrown in live)
            {
                thrown.Body.isKinematic = true;
            }

            var from = live.Select(die => die.Die.localRotation).ToArray();
            var to = live.Select((die, i) => NearestShowing(from[i], die.Value)).ToArray();

            var settling = 0f;
            while (settling < 0.25f)
            {
                settling += Time.deltaTime;
                var t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(settling / 0.25f));

                for (var i = 0; i < live.Count; i++)
                {
                    live[i].Die.localRotation = Quaternion.Slerp(from[i], to[i], t);
                }

                PlaceLabels();
                yield return null;
            }

            for (var i = 0; i < live.Count; i++)
            {
                live[i].Die.localRotation = to[i];
            }

            PlaceLabels();
            PlaceDismissArea(live);
            _rolling = null;
        }

        /// <summary>
        /// The orientation showing <paramref name="value"/> that is closest to
        /// how the die already lies, so the last turn onto the rolled number is
        /// as small as possible. Chosen from the four ways that side can be up,
        /// which differ only in how the die is spun about the vertical.
        /// </summary>
        private static Quaternion NearestShowing(Quaternion current, int value)
        {
            var side = FaceMap.FirstOrDefault(entry => entry.Number == value).Side;
            if (side == Vector3.zero)
            {
                return current;
            }

            // Turn that side to point straight up, then try the four spins.
            var bring = Quaternion.FromToRotation(side, Vector3.up);
            var best = bring;
            var closest = -1f;

            for (var turn = 0; turn < 4; turn++)
            {
                var candidate = Quaternion.Euler(0f, turn * 90f, 0f) * bring;
                var alignment = Mathf.Abs(Quaternion.Dot(current, candidate));

                if (alignment > closest)
                {
                    closest = alignment;
                    best = candidate;
                }
            }

            return best;
        }

        /// <summary>Keeps each name sitting under the die it belongs to.</summary>
        private void PlaceLabels()
        {
            var board = (RectTransform)transform;

            foreach (var thrown in _dice)
            {
                if (!thrown.Die.gameObject.activeSelf)
                {
                    continue;
                }

                var viewport = _camera.WorldToViewportPoint(thrown.Die.position);
                thrown.Label.rectTransform.anchoredPosition = new Vector2(
                    (viewport.x - 0.5f) * board.rect.width,
                    ((viewport.y - 0.5f) * board.rect.height) - 52f);
            }
        }

        /// <summary>
        /// Puts the clickable patch over the dice. The picture covers the whole
        /// board but never takes a click, so this is the only part of the board
        /// the dice take away from the game.
        /// </summary>
        private void PlaceDismissArea(IReadOnlyList<Thrown> live)
        {
            var board = (RectTransform)transform;
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);

            foreach (var thrown in live)
            {
                var viewport = _camera.WorldToViewportPoint(thrown.Die.position);
                var point = new Vector2(
                    (viewport.x - 0.5f) * board.rect.width,
                    (viewport.y - 0.5f) * board.rect.height);

                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }

            _dismissArea.anchoredPosition = (min + max) * 0.5f;
            _dismissArea.sizeDelta = new Vector2(
                Mathf.Max(200f, max.x - min.x + 200f),
                Mathf.Max(200f, max.y - min.y + 200f));

            _dismissArea.gameObject.SetActive(true);
        }

        private void OnDestroy()
        {
            if (_texture == null)
            {
                return;
            }

            // Unhooked before release: freeing a texture a camera still points
            // at is an error in its own right, and surfaces during teardown
            // where it is hardest to place.
            if (_camera != null)
            {
                _camera.targetTexture = null;
            }

            _texture.Release();
            Destroy(_texture);
        }
    }
}
