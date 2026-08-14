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
    /// They are ordinary objects in the scene, in front of the board, seen by
    /// the game's own camera. That is possible because the board is drawn
    /// through that camera rather than as an overlay - an overlay canvas is
    /// composited after every camera, so nothing in the scene can appear over
    /// one. Filming them into a texture and laying that over the board was
    /// tried twice and showed nothing; this needs no intermediate picture at
    /// all, so there is nothing left to go wrong between the dice and the
    /// screen.
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

        /// <summary>
        /// How far in front of the board the dice roll. Nearer to the camera
        /// than the board's own plane, which is what puts them in front of it.
        /// </summary>
        private const float StageHeight = 0f;

        /// <summary>Half-width of the table the dice are thrown onto.</summary>
        private const float TableHalfWidth = 5f;

        /// <summary>
        /// How much of the visible height a die takes up. Expressed as a
        /// fraction of what the camera can see rather than as a fixed number of
        /// units, so it is the same size on the board whatever the camera is set
        /// to - and so a mis-measured model cannot produce a die the size of the
        /// screen.
        /// </summary>
        private const float DieShareOfView = 0.07f;

        /// <summary>
        /// The size a die should end up, in world units, from what the camera can
        /// actually see. Falls back to something sane if there is no camera.
        /// </summary>
        private static float TargetDieSize()
        {
            var camera = Camera.main;
            var visibleHeight = camera != null && camera.orthographic
                ? camera.orthographicSize * 2f
                : 12f;

            return visibleHeight * DieShareOfView;
        }

        /// <summary>Longest the dice may tumble before they are made to settle.</summary>
        private const float MaxTumbleSeconds = 3.2f;

        private RectTransform _dismissArea;
        private Camera _camera;
        private Transform _stage;
        private GameObject _model;
        private PhysicsMaterial _contact;
        private Coroutine _rolling;

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
        /// The table the dice land on, the rails that keep them there, and a
        /// light of their own. It sits in front of the board's own plane, in
        /// full view of the game camera, which is what makes the dice visible
        /// without any compositing.
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
            stage.transform.position = new Vector3(0f, StageHeight, 0f);
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

            // The game's own camera sees them, so there is no second camera and
            // no texture in between - which is exactly what kept failing.
            _camera = Camera.main;

            var lightObject = new GameObject("Die Light") { hideFlags = HideFlags.DontSave };
            lightObject.transform.SetParent(stage.transform, false);
            lightObject.transform.localRotation = Quaternion.Euler(50f, -30f, 0f);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.6f;

            // The dice are scene objects, not children of the board, so hiding
            // the board's own object would leave them lying there. The stage is
            // switched on and off with them.
            stage.SetActive(false);
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
            _stage.gameObject.SetActive(true);
            transform.SetAsLastSibling();

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

                StripSceneFurniture(die);

                // Normalised on its largest axis, so the throw reads the same
                // whatever scale the model was exported at.
                die.transform.localScale = Vector3.one;
                die.transform.localRotation = Quaternion.identity;
                die.transform.localPosition = Vector3.zero;

                // Measured from the meshes and the scale their own transforms
                // apply to them, rather than from Renderer.bounds. Renderer
                // bounds are only meaningful once the object has been through a
                // frame, and these dice are measured the moment they are made -
                // reading them a frame early gives the raw mesh size, which in
                // this model is 100x smaller than what it renders at and so asks
                // for a die 100x too large. That is how it ended up enormous.
                var measured = new Bounds();
                var found = false;
                foreach (var filter in die.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null)
                    {
                        continue;
                    }

                    // Each mesh's corners, brought into the die root's own space.
                    // The root is unscaled and unrotated at this point, so this is
                    // the space the collider is expressed in too.
                    var local = filter.sharedMesh.bounds;
                    for (var corner = 0; corner < 8; corner++)
                    {
                        var point = local.center + Vector3.Scale(
                            local.extents,
                            new Vector3(
                                (corner & 1) == 0 ? -1f : 1f,
                                (corner & 2) == 0 ? -1f : 1f,
                                (corner & 4) == 0 ? -1f : 1f));

                        var inRoot = die.transform.InverseTransformPoint(
                            filter.transform.TransformPoint(point));

                        if (found)
                        {
                            measured.Encapsulate(inRoot);
                        }
                        else
                        {
                            measured = new Bounds(inRoot, Vector3.zero);
                            found = true;
                        }
                    }
                }

                var widest = found
                    ? Mathf.Max(measured.size.x, Mathf.Max(measured.size.y, measured.size.z))
                    : 0f;

                if (widest > 0.0001f)
                {
                    die.transform.localScale = Vector3.one * (TargetDieSize() / widest);
                }
                else
                {
                    // Nothing measurable. Better a die of the wrong size than one
                    // that swallows the screen.
                    die.transform.localScale = Vector3.one;
                }

                // The collider is sized and placed from the same measurement, in
                // the root's own space, so it wraps the die wherever the model
                // happens to sit relative to its own origin. It scales with the
                // root, so it is expressed unscaled here.
                var collider = die.AddComponent<BoxCollider>();
                collider.size = found ? measured.size : Vector3.one;
                collider.center = found ? measured.center : Vector3.zero;
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

            if (_dismissArea != null)
            {
                _dismissArea.gameObject.SetActive(false);
            }

            if (_stage != null)
            {
                _stage.gameObject.SetActive(false);
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

        /// <summary>
        /// Throws away anything in the model that is scenery rather than a die.
        ///
        /// A modelling file is a whole little scene, not just a shape: this one
        /// ships with a camera and a light next to the cube, and Unity imports
        /// them unless told not to. An imported camera is catastrophic here -
        /// it is a child of the die, so it tumbles with the physics and renders
        /// the world, skybox and all, from inside a spinning die, on top of the
        /// board. That is exactly what "the sky spinning around behind the die"
        /// was.
        ///
        /// The importer is now set to leave both behind (see `Die.fbx.meta`),
        /// which is the real fix. This stays as well because the symptom is so
        /// destructive and so hard to read back to its cause: a re-import, a
        /// swapped model or a fresh export could quietly turn it back on.
        /// </summary>
        private static void StripSceneFurniture(GameObject die)
        {
            foreach (var camera in die.GetComponentsInChildren<Camera>(true))
            {
                Destroy(camera);
            }

            foreach (var listener in die.GetComponentsInChildren<AudioListener>(true))
            {
                Destroy(listener);
            }

            foreach (var light in die.GetComponentsInChildren<Light>(true))
            {
                Destroy(light);
            }
        }

        /// <summary>
        /// The hard ceiling on a die's size, read from what is actually on
        /// screen. Only shrinks - a die that came out too small is a blemish, a
        /// die that came out too big hides the whole game behind it.
        /// </summary>
        private static void ClampToView(Transform die)
        {
            var renderers = die.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                return;
            }

            var drawn = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
            {
                drawn.Encapsulate(renderers[i].bounds);
            }

            var widest = Mathf.Max(drawn.size.x, Mathf.Max(drawn.size.y, drawn.size.z));
            var ceiling = TargetDieSize() * 1.5f;
            if (widest <= ceiling || widest <= 0.0001f)
            {
                return;
            }

            Debug.LogWarning(
                $"Die measured {widest:0.###} units, over the {ceiling:0.###} ceiling. Shrinking it.");
            die.localScale *= ceiling / widest;
        }

        private IEnumerator Throw()
        {
            _dismissArea.gameObject.SetActive(false);

            var live = _dice.Where(die => die.Die.gameObject.activeSelf).ToList();

            // One frame, so the renderers have been through a draw and their
            // bounds mean something, and then a last check on the size. The
            // measurement above is the one that decides the size; this only
            // catches the case where it was wrong, because a die that fills the
            // screen has broken the board twice now and must not be able to
            // again. Imperceptible - the dice have not been thrown yet.
            yield return null;
            foreach (var thrown in live)
            {
                ClampToView(thrown.Die);
            }

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

    }
}
