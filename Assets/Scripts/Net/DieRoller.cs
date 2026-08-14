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
    /// They are the supplied die model, flung in hard with a random shove and a
    /// random spin, and they tumble, bounce off the rails and come to rest
    /// wherever they come to rest. **Nothing touches them after they land.**
    /// Two earlier versions did: one slid each die to a tidy resting spot, and
    /// one turned it onto the rolled number once it stopped. Both looked like
    /// exactly what they were.
    ///
    /// The number is nonetheless the one the server rolled. See `Throw` - the
    /// throw is simulated and recorded in the frame before it is shown, and the
    /// model is turned inside its own collider so the right number is on the
    /// face that is going to land upward. By the time anything is on screen the
    /// roll is already correct and needs no help.
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
        //  THE FACE MAP - which number is printed on which side of the model.
        //
        //  Measured, not guessed. The pips on this model are modelled geometry
        //  rather than a painted texture, so they can be counted straight out of
        //  the mesh; `DieFaceProbe` in the editor tools does exactly that and
        //  prints the result. Re-run it if the die model is ever replaced:
        //
        //      Unity -batchmode -nographics -projectPath . \
        //        -executeMethod Indoctrination.EditorTools.DieFaceProbe.RunBatch
        //
        //  Opposite sides add to seven, as they do on a real die, which is a
        //  free check on the measurement.
        // ------------------------------------------------------------------
        private static readonly (Vector3 Side, int Number)[] FaceMap =
        {
            (Vector3.up, 2),
            (Vector3.down, 5),
            (Vector3.right, 3),
            (Vector3.left, 4),
            (Vector3.forward, 6),
            (Vector3.back, 1)
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

        /// <summary>Longest a single simulated throw is allowed to run.</summary>
        private const float MaxTumbleSeconds = 6f;

        /// <summary>
        /// Shortest throw worth watching. A die that stops almost at once is a
        /// legal roll and a boring one, so it is simply thrown again - the
        /// simulation is free, and nobody has seen it yet.
        /// </summary>
        private const float MinTumbleSeconds = 1.1f;

        /// <summary>How many throws to try before settling for the last one.</summary>
        private const int ThrowAttempts = 8;

        /// <summary>
        /// How square-on a die has to come to rest to count as having landed on
        /// a number. `1` is dead flat; this allows a couple of degrees.
        /// </summary>
        private const float FlatEnough = 0.999f;

        private RectTransform _dismissArea;
        private Camera _camera;
        private Transform _stage;
        private GameObject _model;
        private PhysicsMaterial _contact;
        private Coroutine _rolling;

        private sealed class Thrown
        {
            /// <summary>The physical die: a plain cube collider and a body.</summary>
            public Transform Die;

            /// <summary>
            /// The model, hung inside the collider on its own pivot. Turning
            /// this by a quarter-turn moves which number is on which side
            /// without touching the physics at all, because the collider is a
            /// cube and a cube turned by a quarter-turn is the same cube. This
            /// is what lets the die land on the rolled number without ever being
            /// nudged after it lands.
            /// </summary>
            public Transform Facing;

            public Rigidbody Body;
            public Text Label;
            public int Value;

            /// <summary>Whose die it is, as it should read while it is still rolling.</summary>
            public string Owner;
        }

        /// <summary>One die's throw, step by step, as the physics played it.</summary>
        private sealed class Recording
        {
            public readonly List<Vector3> Positions = new();
            public readonly List<Quaternion> Rotations = new();
        }

        private readonly List<Thrown> _dice = new();

        /// <summary>The whole table's roll, so a repeated view does not re-throw it.</summary>
        private string _showing = "";

        /// <summary>False while dice are in the air.</summary>
        private bool _settled = true;

        /// <summary>
        /// Whether this board can show dice at all. False outside play mode and
        /// on a machine with no graphics device, where the roller sits the whole
        /// thing out.
        /// </summary>
        private bool _canShowDice;

        /// <summary>
        /// Whether the dice have finished rolling and are showing their numbers.
        ///
        /// The board waits on this before handing the high roller their
        /// resource: being told you won before the dice have stopped gives the
        /// answer away and makes the roll decorative. A board that cannot show
        /// dice reports settled always - a flourish that is not running must
        /// never be something the game waits on.
        /// </summary>
        public bool Settled => !_canShowDice || _settled;

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
            _canShowDice = _stage != null;
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
                bounciness = 0.55f,
                dynamicFriction = 0.28f,
                staticFriction = 0.28f,
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
            _settled = false;
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
                // Three layers, and the split matters:
                //
                //   Die N     the physical die - a cube collider and a body, and
                //             nothing to look at. This is all the physics knows.
                //     Facing  a pivot that turns the model inside the cube.
                //       model the die as exported, centred and scaled to fit.
                //
                // Because the collider is a cube, turning `Facing` by a quarter
                // turn changes which number is on top and changes nothing else.
                // That is how the die lands on the number the game rolled without
                // being touched after it lands.
                var body = new GameObject($"Die {_dice.Count}") { hideFlags = HideFlags.DontSave };
                body.transform.SetParent(_stage, false);

                var facing = new GameObject("Facing").transform;
                facing.SetParent(body.transform, false);

                var die = Instantiate(_model, facing);
                die.name = "Model";
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

                // Scaled to the target size and shifted so the middle of the die
                // sits on the pivot. The centring is what makes turning `Facing`
                // safe: the model spins about its own middle rather than swinging
                // out of the collider.
                var fit = widest > 0.0001f ? TargetDieSize() / widest : 1f;
                die.transform.localScale = Vector3.one * fit;
                die.transform.localPosition = found ? -measured.center * fit : Vector3.zero;

                // A plain cube, the size the model was fitted to. Deliberately
                // not measured: the target size is known exactly, and a collider
                // that never depends on a measurement cannot be thrown off by a
                // bad one.
                var collider = body.AddComponent<BoxCollider>();
                collider.size = Vector3.one * TargetDieSize();
                collider.material = _contact;

                var rigid = body.AddComponent<Rigidbody>();
                rigid.mass = 1f;
                rigid.linearDamping = 0.02f;
                rigid.angularDamping = 0.04f;
                rigid.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                rigid.interpolation = RigidbodyInterpolation.None;

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

                _dice.Add(new Thrown
                {
                    Die = body.transform,
                    Facing = facing,
                    Body = rigid,
                    Label = label
                });
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

                // Only the name while it is in the air. The number is written
                // beside it once the die has stopped - it is what the game is
                // actually using, so it is also the check that the die on the
                // table agrees with it, but printing it during the throw
                // announces the result before the die gets there.
                thrown.Owner = rolls[i].IsViewer ? "YOU" : rolls[i].Name;
                thrown.Label.text = thrown.Owner;
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

            // Clicking the dice away finishes the roll. Anything waiting on the
            // animation must not be left waiting on one that is no longer there.
            _settled = true;
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
            _settled = true;
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

        /// <summary>
        /// Throws the dice for real, and shows exactly the throw that happened.
        ///
        /// The awkward part of a die in a game like this is that the number is
        /// already decided - the server rolled it before any of this ran - while
        /// a die that is worth watching has to be genuinely thrown. Turning a
        /// landed die onto the right number is the obvious way out and it looks
        /// exactly like what it is: the die visibly flips at the last moment.
        ///
        /// So the throw happens first, out of sight, in the frame before it is
        /// shown:
        ///
        ///   1. Throw the dice with real physics, from a random spot, with a
        ///      random shove and a random spin, and record every step.
        ///   2. Throw them again if that was a dull roll or if one came to rest
        ///      leaning on something. Nobody has seen it, so it costs nothing to
        ///      be picky, and this is what guarantees a die that has landed
        ///      squarely on a face.
        ///   3. Look at what came up, and turn the *model inside the cube* by a
        ///      quarter turn so that the number the game rolled is the number on
        ///      that face. The collider is a cube, so this changes nothing
        ///      physical, and it happens before the die is ever shown.
        ///   4. Play the recording back.
        ///
        /// What you watch is a real, unrepeatable, physically simulated roll,
        /// and it lands on the right number without anything touching it.
        /// </summary>
        private IEnumerator Throw()
        {
            _dismissArea.gameObject.SetActive(false);

            var live = _dice.Where(die => die.Die.gameObject.activeSelf).ToList();
            if (live.Count == 0)
            {
                _rolling = null;
                yield break;
            }

            // One frame, so the renderers have been through a draw and their
            // bounds mean something, and then a last check on the size. The
            // measurement in BuildDice is the one that decides the size; this
            // only catches the case where it was wrong, because a die that fills
            // the screen has broken the board twice now and must not be able to
            // again. Imperceptible - the dice have not been thrown yet.
            yield return null;
            foreach (var thrown in live)
            {
                ClampToView(thrown.Facing);
            }

            var recordings = BestOfSeveralThrows(live);

            // Turn each model inside its cube so the face that landed upward is
            // the face the game rolled. Done here, before the first frame of the
            // playback, so nothing is ever corrected mid-roll.
            for (var i = 0; i < live.Count; i++)
            {
                var landed = recordings[i].Rotations[recordings[i].Rotations.Count - 1];
                live[i].Facing.localRotation = TurnOnto(landed, live[i].Value);
            }

            yield return Replay(live, recordings);

            // Now, and not before: the dice have stopped, so the numbers can be
            // read off them and are no longer a spoiler.
            foreach (var thrown in live)
            {
                thrown.Label.text = $"{thrown.Owner}  ·  {thrown.Value}";
            }

            PlaceLabels();
            PlaceDismissArea(live);
            _settled = true;
            _rolling = null;
        }

        /// <summary>
        /// Throws the dice until they throw well, and hands back the best of it.
        ///
        /// A good throw is one that ran for a while and left every die sitting
        /// squarely on a face. This is all simulated inside a single frame with
        /// nothing drawn, so a rejected throw costs a fraction of a millisecond
        /// and is never seen.
        /// </summary>
        private List<Recording> BestOfSeveralThrows(List<Thrown> live)
        {
            var previous = Physics.simulationMode;

            // The dice are the only physical things in this game, so stepping
            // the world by hand for a moment holds nothing else up. Restored in
            // the `finally` whatever happens - leaving the world on manual would
            // stop physics for good.
            Physics.simulationMode = SimulationMode.Script;

            try
            {
                List<Recording> best = null;

                for (var attempt = 0; attempt < ThrowAttempts; attempt++)
                {
                    var recordings = SimulateThrow(live);
                    best ??= recordings;

                    var seconds = recordings[0].Positions.Count * Time.fixedDeltaTime;
                    if (seconds < MinTumbleSeconds)
                    {
                        continue;
                    }

                    var landed = recordings.All(r =>
                        Squareness(r.Rotations[r.Rotations.Count - 1]) >= FlatEnough
                        && OnTheTable(r.Positions[r.Positions.Count - 1]));

                    if (landed)
                    {
                        return recordings;
                    }

                    best = recordings;
                }

                return best;
            }
            finally
            {
                Physics.simulationMode = previous;
            }
        }

        /// <summary>
        /// One throw, stepped by hand and written down. Ends when every die has
        /// run out of energy, or when the throw has gone on too long.
        /// </summary>
        private List<Recording> SimulateThrow(List<Thrown> live)
        {
            var step = Time.fixedDeltaTime;
            var recordings = live.Select(_ => new Recording()).ToList();

            for (var i = 0; i < live.Count; i++)
            {
                // Spread across the near end of the table so several dice do not
                // start inside one another, but everything else about the throw
                // is random: where along that end, how hard, which way, and how
                // fast it is already spinning.
                var lane = live.Count == 1
                    ? Random.Range(-2f, 2f)
                    : Mathf.Lerp(-2.6f, 2.6f, i / (live.Count - 1f)) + Random.Range(-0.3f, 0.3f);

                var thrown = live[i];
                thrown.Body.isKinematic = false;
                // Clear of the near rail on purpose. Starting a die overlapping
                // the rail is not a near miss - the solver pushes the overlap
                // apart in one step and fires the die off the table.
                thrown.Die.localPosition = new Vector3(
                    -TableHalfWidth + Random.Range(1.3f, 2.2f),
                    Random.Range(2.4f, 3.6f),
                    lane);
                thrown.Die.localRotation = Random.rotation;

                // Hard enough to cross the table, hit the far rail and come
                // back, with enough spin on it to keep tumbling the whole way.
                thrown.Body.linearVelocity = new Vector3(
                    Random.Range(9f, 13f),
                    Random.Range(-0.5f, 1.5f),
                    Random.Range(-4f, 4f));
                thrown.Body.angularVelocity = new Vector3(
                    Random.Range(-26f, 26f), Random.Range(-26f, 26f), Random.Range(-26f, 26f));
            }

            var steps = Mathf.CeilToInt(MaxTumbleSeconds / step);
            var stillFor = 0;

            for (var frame = 0; frame < steps; frame++)
            {
                Physics.Simulate(step);

                for (var i = 0; i < live.Count; i++)
                {
                    recordings[i].Positions.Add(live[i].Die.localPosition);
                    recordings[i].Rotations.Add(live[i].Die.localRotation);
                }

                var resting = live.All(die =>
                    die.Body.linearVelocity.magnitude < 0.06f
                    && die.Body.angularVelocity.magnitude < 0.15f);

                // Held still for a moment, not merely still for one step - a die
                // at the top of a bounce is motionless too.
                stillFor = resting ? stillFor + 1 : 0;
                if (stillFor >= 8)
                {
                    break;
                }
            }

            foreach (var thrown in live)
            {
                thrown.Body.isKinematic = true;
            }

            return recordings;
        }

        /// <summary>Plays a recorded throw out at ordinary speed.</summary>
        private IEnumerator Replay(List<Thrown> live, List<Recording> recordings)
        {
            var step = Time.fixedDeltaTime;
            var length = recordings.Max(r => r.Positions.Count);

            for (var i = 0; i < live.Count; i++)
            {
                live[i].Die.localPosition = recordings[i].Positions[0];
                live[i].Die.localRotation = recordings[i].Rotations[0];
            }

            var clock = 0f;
            while (true)
            {
                clock += Time.deltaTime;

                var at = clock / step;
                var frame = Mathf.FloorToInt(at);
                if (frame >= length - 1)
                {
                    break;
                }

                var blend = at - frame;

                for (var i = 0; i < live.Count; i++)
                {
                    var recording = recordings[i];
                    var a = Mathf.Min(frame, recording.Positions.Count - 1);
                    var b = Mathf.Min(frame + 1, recording.Positions.Count - 1);

                    live[i].Die.localPosition = Vector3.Lerp(
                        recording.Positions[a], recording.Positions[b], blend);
                    live[i].Die.localRotation = Quaternion.Slerp(
                        recording.Rotations[a], recording.Rotations[b], blend);
                }

                PlaceLabels();
                yield return null;
            }

            for (var i = 0; i < live.Count; i++)
            {
                var recording = recordings[i];
                live[i].Die.localPosition = recording.Positions[recording.Positions.Count - 1];
                live[i].Die.localRotation = recording.Rotations[recording.Rotations.Count - 1];
            }
        }

        /// <summary>
        /// Whether a die finished lying on the table, rather than perched on a
        /// rail or thrown clear of it. A die that ends up somewhere it should
        /// not be is a throw nobody needs to see.
        /// </summary>
        private static bool OnTheTable(Vector3 at)
        {
            var edge = TableHalfWidth - (TargetDieSize() * 0.5f);
            return Mathf.Abs(at.x) < edge
                   && Mathf.Abs(at.z) < edge
                   && at.y > 0f
                   && at.y < TargetDieSize() * 1.5f;
        }

        /// <summary>
        /// How squarely a die is sitting: 1 when a face is dead flat against the
        /// table, less as it tips over onto an edge or a corner.
        /// </summary>
        private static float Squareness(Quaternion rotation)
        {
            var best = 0f;
            foreach (var (side, _) in FaceMap)
            {
                best = Mathf.Max(best, Vector3.Dot(rotation * side, Vector3.up));
            }

            return best;
        }

        /// <summary>
        /// The quarter-turn to give the model so that a die lying at
        /// <paramref name="landed"/> shows <paramref name="value"/>.
        ///
        /// Built out of two axis-aligned frames rather than a shortest-arc turn,
        /// so the result is always one of the twenty-four ways a cube can sit on
        /// itself. Anything else would leave the model at an angle inside its own
        /// collider, and the die would look like it had come to rest crooked.
        /// </summary>
        public static Quaternion TurnOnto(Quaternion landed, int value)
        {
            var wanted = FaceMap.FirstOrDefault(entry => entry.Number == value).Side;
            if (wanted == Vector3.zero)
            {
                return Quaternion.identity;
            }

            var top = TopSide(landed);

            // A second axis at right angles to each, so the turn is fully
            // pinned down and lands exactly on a quarter turn.
            var acrossWanted = Perpendicular(wanted);
            var acrossTop = Perpendicular(top);

            return Quaternion.LookRotation(top, acrossTop)
                   * Quaternion.Inverse(Quaternion.LookRotation(wanted, acrossWanted));
        }

        /// <summary>
        /// The number a die is showing, given how the physical cube came to rest
        /// and how the model is turned inside it. This is the readback that says
        /// whether the whole face arrangement is right, and it is what the tests
        /// check against, since they cannot look at the screen.
        /// </summary>
        public static int NumberShowing(Quaternion landed, Quaternion facing)
        {
            var top = TopSide(landed * facing);
            return FaceMap.First(entry => entry.Side == top).Number;
        }

        /// <summary>Which side of the die this rotation puts uppermost.</summary>
        private static Vector3 TopSide(Quaternion rotation)
        {
            var top = FaceMap[0].Side;
            var highest = float.MinValue;

            foreach (var (side, _) in FaceMap)
            {
                var height = Vector3.Dot(rotation * side, Vector3.up);
                if (height > highest)
                {
                    highest = height;
                    top = side;
                }
            }

            return top;
        }

        /// <summary>Any axis at right angles to this one, chosen the same way every time.</summary>
        private static Vector3 Perpendicular(Vector3 axis)
        {
            return Mathf.Abs(axis.y) < 0.5f ? Vector3.up : Vector3.forward;
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
