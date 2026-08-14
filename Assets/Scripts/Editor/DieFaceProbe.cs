using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Indoctrination.EditorTools
{
    /// <summary>
    /// Reads which number is printed on which side of the die model, straight
    /// out of the geometry.
    ///
    /// The pips are modelled, not painted, so they can be counted without
    /// rendering anything - which matters, because Unity draws nothing in
    /// batchmode and there is no graphics device to render into. For each of the
    /// six sides this takes the triangles that sit near that side but do not
    /// face flat along it (the pip walls), clusters them by position, and counts
    /// the clusters.
    ///
    /// This exists so `DieRoller.FaceMap` is measured rather than guessed. Run
    /// it again if the die model is ever replaced.
    /// </summary>
    public static class DieFaceProbe
    {
        public static void RunBatch()
        {
            var model = Resources.Load<GameObject>("Models/Die");
            if (model == null)
            {
                Debug.Log("DIE PROBE: no model at Resources/Models/Die");
                EditorApplication.Exit(1);
                return;
            }

            // Every triangle in the model, in the root's own space.
            var centroids = new List<Vector3>();
            var normals = new List<Vector3>();

            foreach (var filter in model.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }

                Debug.Log($"DIE PROBE: mesh {filter.name} verts={mesh.vertexCount} "
                          + $"submeshes={mesh.subMeshCount} bounds={mesh.bounds}");

                var toRoot = model.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                var verts = mesh.vertices.Select(v => toRoot.MultiplyPoint3x4(v)).ToArray();
                var tris = mesh.triangles;

                for (var i = 0; i + 2 < tris.Length; i += 3)
                {
                    var a = verts[tris[i]];
                    var b = verts[tris[i + 1]];
                    var c = verts[tris[i + 2]];

                    centroids.Add((a + b + c) / 3f);
                    normals.Add(Vector3.Cross(b - a, c - a).normalized);
                }
            }

            if (centroids.Count == 0)
            {
                Debug.Log("DIE PROBE: no triangles");
                EditorApplication.Exit(1);
                return;
            }

            var bounds = new Bounds(centroids[0], Vector3.zero);
            foreach (var point in centroids)
            {
                bounds.Encapsulate(point);
            }

            Debug.Log($"DIE PROBE: {centroids.Count} triangles, bounds {bounds}");

            var sides = new[]
            {
                ("up", Vector3.up), ("down", Vector3.down),
                ("right", Vector3.right), ("left", Vector3.left),
                ("forward", Vector3.forward), ("back", Vector3.back)
            };

            foreach (var (name, side) in sides)
            {
                Report(name, side, bounds, centroids, normals);
            }

            EditorApplication.Exit(0);
        }

        private static void Report(
            string name,
            Vector3 side,
            Bounds bounds,
            List<Vector3> centroids,
            List<Vector3> normals)
        {
            // How far out along this side the die's surface is, and how big the
            // face is across.
            var reach = Vector3.Dot(bounds.extents, new Vector3(
                Mathf.Abs(side.x), Mathf.Abs(side.y), Mathf.Abs(side.z)));
            var centre = Vector3.Dot(bounds.center, side);
            var surface = centre + reach;
            var across = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));

            // Pip walls: near this side, but not lying flat along it. The flat
            // face itself and the far side of the die are both excluded, and so
            // is the rim, where the cube's own edges curve away.
            var picked = new List<Vector3>();
            for (var i = 0; i < centroids.Count; i++)
            {
                var depth = surface - Vector3.Dot(centroids[i], side);
                if (depth < -0.02f * across || depth > 0.18f * across)
                {
                    continue;
                }

                if (Vector3.Dot(normals[i], side) > 0.9f)
                {
                    continue;
                }

                var offset = centroids[i] - (Vector3.Dot(centroids[i], side) * side);
                var fromMiddle = (offset - Vector3.ProjectOnPlane(bounds.center, side)).magnitude;
                if (fromMiddle > 0.34f * across)
                {
                    continue;
                }

                picked.Add(offset);
            }

            // Greedy clustering: anything within a pip's width of a cluster
            // joins it.
            var clusters = new List<List<Vector3>>();
            foreach (var point in picked)
            {
                var home = clusters.FirstOrDefault(
                    c => c.Any(p => Vector3.Distance(p, point) < 0.07f * across));

                if (home == null)
                {
                    clusters.Add(new List<Vector3> { point });
                }
                else
                {
                    home.Add(point);
                }
            }

            // Stray triangles are not pips.
            var real = clusters.Where(c => c.Count >= 4).ToList();

            var where = string.Join(" ", real.Select(c =>
            {
                var mid = c.Aggregate(Vector3.zero, (sum, p) => sum + p) / c.Count;
                return $"({mid.x:0.00},{mid.y:0.00},{mid.z:0.00})";
            }));

            Debug.Log($"DIE PROBE FACE {name,-8} pips={real.Count} "
                      + $"(clusters={clusters.Count}, tris={picked.Count}) {where}");
        }
    }
}
