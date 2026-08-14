using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// A real die, thrown across the table with real physics, in 3D.
    ///
    /// It is a genuine cube with six numbered faces, tumbling under gravity on a
    /// floor, lit and seen in perspective. It is filmed by its own camera and
    /// that picture is laid over the whole board - not because the die is fake,
    /// but because the board is a ScreenSpaceOverlay canvas, which Unity
    /// composites after every camera in the game. Nothing in the scene can be
    /// drawn over such a canvas by any means, so a die that has to be seen has
    /// to arrive as part of the picture. The alternative is moving the whole
    /// interface to a camera-space canvas, which is a far larger change.
    ///
    /// The physics is real, but the *result* is not left to it: the server has
    /// already decided the number. Once the die stops moving it is turned the
    /// short way onto the rolled face. In practice that is a small last tip,
    /// because a settled die is already lying on a face.
    /// </summary>
    public class DieRoller : MonoBehaviour
    {
        /// <summary>How far below the board the die's table sits.</summary>
        private const float StageDepth = -2000f;

        /// <summary>Half-width of the little table the die is thrown onto.</summary>
        private const float TableHalfWidth = 5.2f;

        /// <summary>Longest the die may tumble before it is made to settle.</summary>
        private const float MaxTumbleSeconds = 2.6f;

        private RawImage _display;
        private Button _dismiss;
        private RectTransform _dismissArea;
        private Camera _camera;
        private RenderTexture _texture;
        private Rigidbody _body;
        private Transform _die;
        private Coroutine _rolling;
        private Vector2Int _builtFor;

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

            // The die's picture covers the whole board, so it can roll right
            // across the table rather than being penned into a corner. It never
            // takes a click: the board underneath stays completely live.
            _display = root.gameObject.AddComponent<RawImage>();
            _display.raycastTarget = false;
            _display.color = Color.white;

            // Only where the die actually comes to rest is clickable, and only
            // once it is lying there. Moved into place when the die settles.
            _dismissArea = UIFactory.Group("Die", root);
            _dismissArea.anchorMin = _dismissArea.anchorMax = new Vector2(0.5f, 0.5f);
            _dismissArea.pivot = new Vector2(0.5f, 0.5f);
            UIFactory.SetSize(_dismissArea, 150f, 150f);

            var hit = _dismissArea.gameObject.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f);
            hit.raycastTarget = true;

            _dismiss = _dismissArea.gameObject.AddComponent<Button>();
            _dismiss.targetGraphic = hit;
            _dismiss.transition = Selectable.Transition.None;
            _dismiss.onClick.AddListener(Dismiss);
            _dismissArea.gameObject.SetActive(false);

            // Nothing renders outside play mode, and there is no render texture
            // at all without a graphics device - which is the case in batchmode,
            // where the tests run. The board plays normally either way; it
            // simply never shows a die. A flourish must never be the reason
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
        /// The die, the table it lands on, the walls that keep it there, and the
        /// camera that films the lot.
        ///
        /// Parked far below the board and isolated by distance rather than by a
        /// layer: layers live in project settings and would have to be reserved
        /// and kept in step, whereas the board's own camera sits at y=12 with
        /// the default 1000 of draw distance and simply cannot see this far.
        /// </summary>
        private void BuildTable()
        {
            var stage = new GameObject("Die Stage") { hideFlags = HideFlags.DontSave };
            stage.transform.position = new Vector3(0f, StageDepth, 0f);

            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Die Table";
            floor.transform.SetParent(stage.transform, false);
            floor.transform.localScale = new Vector3(TableHalfWidth * 2f, 0.4f, TableHalfWidth * 2f);
            floor.transform.localPosition = new Vector3(0f, -0.2f, 0f);
            floor.GetComponent<MeshRenderer>().enabled = false;

            // Walls, so a hard throw cannot send the die off the table and out
            // of shot. Invisible - they are a backstop, not scenery.
            for (var side = 0; side < 4; side++)
            {
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = $"Rail {side}";
                wall.transform.SetParent(stage.transform, false);
                wall.GetComponent<MeshRenderer>().enabled = false;

                var along = side % 2 == 0;
                var sign = side < 2 ? 1f : -1f;
                wall.transform.localScale = along
                    ? new Vector3(TableHalfWidth * 2f, 4f, 0.4f)
                    : new Vector3(0.4f, 4f, TableHalfWidth * 2f);
                wall.transform.localPosition = along
                    ? new Vector3(0f, 2f, sign * TableHalfWidth)
                    : new Vector3(sign * TableHalfWidth, 2f, 0f);
            }

            var die = new GameObject("Die") { hideFlags = HideFlags.DontSave };
            die.transform.SetParent(stage.transform, false);
            _die = die.transform;

            die.AddComponent<MeshFilter>().sharedMesh = BuildDieMesh();

            var renderer = die.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = BuildDieMaterial();
            renderer.shadowCastingMode = ShadowCastingMode.Off;

            die.AddComponent<BoxCollider>();

            _body = die.AddComponent<Rigidbody>();
            _body.mass = 1f;
            _body.linearDamping = 0.12f;
            _body.angularDamping = 0.16f;
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _body.interpolation = RigidbodyInterpolation.Interpolate;

            var cameraObject = new GameObject("Die Camera") { hideFlags = HideFlags.DontSave };
            cameraObject.transform.SetParent(stage.transform, false);
            cameraObject.transform.localPosition = new Vector3(0f, 7.6f, -5.6f);
            cameraObject.transform.localRotation = Quaternion.Euler(52f, 0f, 0f);

            _camera = cameraObject.AddComponent<Camera>();
            _camera.orthographic = false;
            _camera.fieldOfView = 46f;
            _camera.nearClipPlane = 0.3f;
            _camera.farClipPlane = 40f;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _camera.enabled = false;

            var lightObject = new GameObject("Die Light") { hideFlags = HideFlags.DontSave };
            lightObject.transform.SetParent(stage.transform, false);
            lightObject.transform.localRotation = Quaternion.Euler(52f, -34f, 0f);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.5f;
            light.color = new Color(1f, 0.99f, 0.96f);

            EnsureTexture();
        }

        /// <summary>
        /// A cube whose six faces carry the six numbers, built here rather than
        /// imported so that which face holds which number is known exactly -
        /// which is what lets the die be turned onto the rolled number with no
        /// guesswork. Opposite faces add to seven, as on a real die.
        /// </summary>
        private static Mesh BuildDieMesh()
        {
            // Face order: +Y 1, -Y 6, +X 3, -X 4, +Z 2, -Z 5.
            var normals = new[] { Vector3.up, Vector3.down, Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
            var values = new[] { 1, 6, 3, 4, 2, 5 };

            var vertices = new Vector3[24];
            var uvs = new Vector2[24];
            var faceNormals = new Vector3[24];
            var triangles = new int[36];

            for (var face = 0; face < 6; face++)
            {
                var normal = normals[face];

                // Two axes across the face, perpendicular to its normal.
                var right = Vector3.Cross(normal, Mathf.Abs(normal.y) > 0.5f ? Vector3.forward : Vector3.up).normalized;
                var up = Vector3.Cross(right, normal).normalized;

                var centre = normal * 0.5f;
                var v = face * 4;

                vertices[v + 0] = centre - (right * 0.5f) - (up * 0.5f);
                vertices[v + 1] = centre - (right * 0.5f) + (up * 0.5f);
                vertices[v + 2] = centre + (right * 0.5f) + (up * 0.5f);
                vertices[v + 3] = centre + (right * 0.5f) - (up * 0.5f);

                var cell = BoardArt.DieAtlasCell(values[face]);
                uvs[v + 0] = new Vector2(cell.xMin, cell.yMin);
                uvs[v + 1] = new Vector2(cell.xMin, cell.yMax);
                uvs[v + 2] = new Vector2(cell.xMax, cell.yMax);
                uvs[v + 3] = new Vector2(cell.xMax, cell.yMin);

                for (var i = 0; i < 4; i++)
                {
                    faceNormals[v + i] = normal;
                }

                var t = face * 6;
                triangles[t + 0] = v + 0;
                triangles[t + 1] = v + 1;
                triangles[t + 2] = v + 2;
                triangles[t + 3] = v + 0;
                triangles[t + 4] = v + 2;
                triangles[t + 5] = v + 3;
            }

            var mesh = new Mesh { name = "Die" };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetNormals(faceNormals);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Material BuildDieMaterial()
        {
            // Whichever lit shader this project's pipeline provides. Falling
            // back keeps this working if the pipeline is ever changed.
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Standard")
                         ?? Shader.Find("Sprites/Default");

            var material = new Material(shader) { name = "Die" };
            material.mainTexture = BoardArt.DieAtlas;

            if (material.HasProperty("_BaseMap"))
            {
                material.SetTexture("_BaseMap", BoardArt.DieAtlas);
            }

            if (material.HasProperty("_Smoothness"))
            {
                material.SetFloat("_Smoothness", 0.18f);
            }

            return material;
        }

        /// <summary>
        /// Keeps the picture the same shape as the board. A texture built for a
        /// different window shape would stretch the die out of square.
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
            _texture = new RenderTexture(width, height, 24) { name = "Die" };
            _camera.targetTexture = _texture;
            _display.texture = _texture;
        }

        /// <summary>
        /// Throws the die and lands it on <paramref name="value"/>. Throwing the
        /// same roll again does nothing: the die already on the table shows the
        /// right number, and re-throwing would look like a roll that never
        /// happened.
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

            EnsureTexture();
            _camera.enabled = true;

            if (_rolling != null)
            {
                StopCoroutine(_rolling);
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
        /// Clears the die and forgets the roll, so the next one is thrown
        /// afresh. The number deliberately survives an ordinary dismissal - the
        /// board refreshes on every message from the server, and forgetting it
        /// there would throw the same die again the moment it was clicked away.
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
            _dismissArea.gameObject.SetActive(false);

            // Thrown in from one side of the table, across it.
            _body.isKinematic = false;
            _die.localPosition = new Vector3(-TableHalfWidth + 0.9f, 2.4f, Random.Range(-1.4f, 1.4f));
            _die.localRotation = Random.rotation;

            _body.linearVelocity = new Vector3(Random.Range(6.5f, 8.5f), 1.2f, Random.Range(-1.6f, 1.6f));
            _body.angularVelocity = new Vector3(
                Random.Range(-16f, 16f), Random.Range(-16f, 16f), Random.Range(-16f, 16f));

            // Tumbles until it runs out of energy, or until it has had long
            // enough - a die that will not settle must not hold up the board.
            var tumbling = 0f;
            while (tumbling < MaxTumbleSeconds)
            {
                tumbling += Time.deltaTime;

                if (tumbling > 0.55f
                    && _body.linearVelocity.magnitude < 0.35f
                    && _body.angularVelocity.magnitude < 0.9f)
                {
                    break;
                }

                yield return null;
            }

            // The number was decided before the throw, so the die is turned the
            // short way onto it. A die that has settled is already lying on a
            // face, so this is a small last tip rather than a visible cheat.
            _body.isKinematic = true;

            var from = _die.localRotation;
            var to = NearestUpright(from, value);
            var resting = new Vector3(_die.localPosition.x, 0.5f, _die.localPosition.z);
            var settling = 0f;

            while (settling < 0.3f)
            {
                settling += Time.deltaTime;
                var t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(settling / 0.3f));

                _die.localRotation = Quaternion.Slerp(from, to, t);
                _die.localPosition = Vector3.Lerp(_die.localPosition, resting, t);
                yield return null;
            }

            _die.localRotation = to;
            _die.localPosition = resting;

            PlaceDismissArea();
            _rolling = null;
        }

        /// <summary>
        /// The orientation showing <paramref name="value"/> upward that is
        /// closest to how the die already lies, so settling turns it as little
        /// as possible. Chosen from the four ways that face can be up, which
        /// differ only in how the die is spun about the vertical.
        /// </summary>
        private static Quaternion NearestUpright(Quaternion current, int value)
        {
            var bring = FaceUp(value);
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

        /// <summary>
        /// The rotation that puts a given number face up.
        ///
        /// Exact rather than guessed, because the mesh is built here: +Y carries
        /// 1, -Y 6, +X 3, -X 4, +Z 2 and -Z 5, so each of these simply brings
        /// that face's axis round to vertical.
        /// </summary>
        private static Quaternion FaceUp(int value) => value switch
        {
            1 => Quaternion.identity,
            6 => Quaternion.Euler(180f, 0f, 0f),
            3 => Quaternion.Euler(0f, 0f, 90f),
            4 => Quaternion.Euler(0f, 0f, -90f),
            2 => Quaternion.Euler(-90f, 0f, 0f),
            _ => Quaternion.Euler(90f, 0f, 0f)
        };

        /// <summary>
        /// Puts the clickable patch over wherever the die came to rest. The
        /// picture covers the whole board but never takes a click, so this is
        /// the only part of the board the die takes away from the game.
        /// </summary>
        private void PlaceDismissArea()
        {
            var onScreen = _camera.WorldToViewportPoint(_die.position);
            var board = (RectTransform)transform;

            _dismissArea.anchoredPosition = new Vector2(
                (onScreen.x - 0.5f) * board.rect.width,
                (onScreen.y - 0.5f) * board.rect.height);

            _dismissArea.gameObject.SetActive(true);
        }

        private void OnDestroy()
        {
            if (_texture == null)
            {
                return;
            }

            // Unhooked before release: freeing a texture a camera still points
            // at is an error in its own right, and it surfaces during teardown
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
