using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LR2.Core;
using LR2.Game;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LR2.EditorTools
{
    /// <summary>
    /// B85 (user 11-18: "sometimes player get inside objects", "some objects like pipes are suuuuper thin", "fix the clipping
    /// issues"). Edit-mode audit of the four combat maps, no play mode: thin geometry (long but only a few cm thick), pack
    /// props squashed by a non-uniform fit, prop colliders sunk into walls or other props, and spawn points where the soldier
    /// capsule does not fit. Writes Logs/LR2_PropAudit/report.txt.
    /// Batch: Unity -batchmode -projectPath . -executeMethod LR2.EditorTools.LR2PropAudit.RunBatch -quit
    /// </summary>
    public static class LR2PropAudit
    {
        private const float ThinMax = 0.06f;     // thinner than this ...
        private const float ThinLength = 1.0f;   // ... while longer than this
        private const float SquashRatio = 2.2f;  // pack-prop scale max/min
        private const float ClipDepth = 0.05f;   // collider penetration worth reporting

        [MenuItem("LR2/QA/Prop Audit (all maps)", priority = 60)]
        public static void RunMenu() => Run();

        public static void RunBatch()
        {
            bool ok = false;
            try { ok = Run(); }
            catch (Exception ex) { Debug.LogError($"[LR2] Prop audit failed: {ex}"); }
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        private static bool Run()
        {
            string[] scenes =
            {
                LR2MapBuilder.TestRangeScenePath, LR2MapBuilder.ScenePath, LR2MapBuilder.FoundryScenePath, LR2MapBuilder.DeadsectorScenePath
            };
            var sb = new StringBuilder($"=== LR2 PROP AUDIT {DateTime.Now:yyyy-MM-dd HH:mm} ===\n");
            foreach (var path in scenes)
            {
                if (!File.Exists(path)) { sb.AppendLine($"-- {path}: missing"); continue; }
                EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                Physics.SyncTransforms();
                sb.AppendLine($"\n-- {Path.GetFileNameWithoutExtension(path)}");
                Thin(sb);
                Squashed(sb);
                Clipping(sb);
                Floating(sb);
                Spawns(sb);
            }
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "LR2_PropAudit"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "report.txt"), sb.ToString());
            Debug.Log($"[LR2] Prop audit written: {Path.Combine(dir, "report.txt")}");
            return true;
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 4; i++, t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>World-space extents of a mesh renderer along its own axes (mesh bounds x lossy scale).</summary>
        private static Vector3 OrientedSize(MeshFilter mf)
        {
            var b = mf.sharedMesh.bounds;
            Vector3 s = mf.transform.lossyScale;
            return new Vector3(Mathf.Abs(b.size.x * s.x), Mathf.Abs(b.size.y * s.y), Mathf.Abs(b.size.z * s.z));
        }

        private static void Thin(StringBuilder sb)
        {
            var groups = new Dictionary<string, (int count, float thin, float len, string example)>();
            foreach (var r in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!r.enabled) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                Vector3 d = OrientedSize(mf);
                float mn = Mathf.Min(d.x, Mathf.Min(d.y, d.z)), mx = Mathf.Max(d.x, Mathf.Max(d.y, d.z));
                if (mx < ThinLength || mn > ThinMax || mn < 0.002f) continue; // decals/planes have ~0 thickness on purpose
                string key = r.name + " [" + mf.sharedMesh.name + "]";
                groups.TryGetValue(key, out var g);
                groups[key] = (g.count + 1, g.count == 0 ? mn : Mathf.Min(g.thin, mn), Mathf.Max(g.len, mx), g.example ?? PathOf(r.transform));
            }
            sb.AppendLine($"THIN ({groups.Count} kinds, thinner than {ThinMax * 100f:0} cm and longer than {ThinLength:0.0} m):");
            foreach (var kv in groups.OrderBy(k => k.Value.thin).Take(40))
                sb.AppendLine($"  {kv.Key} x{kv.Value.count}: thinnest {kv.Value.thin * 100f:0.0} cm, longest {kv.Value.len:0.00} m  e.g. {kv.Value.example}");
        }

        private static void Squashed(StringBuilder sb)
        {
            int n = 0;
            sb.AppendLine($"SQUASHED (pack prefab instances scaled more than {SquashRatio:0.0}x unevenly):");
            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)) continue;
                string src = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject) ?? "";
                if (src.StartsWith("Assets/LR2/")) continue; // LR2's own prefabs (soldiers, crates) are not pack props
                Vector3 s = t.lossyScale;
                float mn = Mathf.Min(Mathf.Abs(s.x), Mathf.Min(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                float mx = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                if (mn < 1e-4f || mx / mn < SquashRatio) continue;
                if (++n <= 40) sb.AppendLine($"  {PathOf(t)} <- {Path.GetFileNameWithoutExtension(src)} scale=({s.x:0.00},{s.y:0.00},{s.z:0.00}) ratio {mx / mn:0.0}");
            }
            if (n > 40) sb.AppendLine($"  ... {n - 40} more");
        }

        private static void Clipping(StringBuilder sb)
        {
            var cols = UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Where(c => c.enabled && !c.isTrigger && (LR2Layers.EnvironmentMask & (1 << c.gameObject.layer)) != 0)
                .Where(c => !(c is TerrainCollider) && !(c is MeshCollider mc && !mc.convex))
                .ToArray();
            // floors and the map shell overlap everything by design: skip anything with a footprint over 60 m2
            var props = cols.Where(c => c.bounds.size.x * c.bounds.size.z < 60f).ToArray();
            var hits = new List<(float depth, string a, string b)>();
            for (int i = 0; i < props.Length; i++)
            {
                var a = props[i];
                var ba = a.bounds;
                for (int j = i + 1; j < props.Length; j++)
                {
                    var b = props[j];
                    if (!ba.Intersects(b.bounds)) continue;
                    if (a.transform.root == b.transform.root && (a.transform.IsChildOf(b.transform) || b.transform.IsChildOf(a.transform))) continue;
                    if (Physics.ComputePenetration(a, a.transform.position, a.transform.rotation, b, b.transform.position, b.transform.rotation,
                            out Vector3 _, out float depth) && depth > ClipDepth)
                        hits.Add((depth, PathOf(a.transform), PathOf(b.transform)));
                }
            }
            sb.AppendLine($"CLIPPING ({hits.Count} prop pairs sunk more than {ClipDepth * 100f:0} cm into each other; deepest first):");
            foreach (var h in hits.OrderByDescending(h => h.depth).Take(40))
                sb.AppendLine($"  {h.depth * 100f:0} cm  {h.a}  <->  {h.b}");
        }

        private static void Floating(StringBuilder sb)
        {
            var targets = new List<Transform>();
            var propsGo = GameObject.Find("Environment/Props");
            if (propsGo != null)
            {
                for (int i = 0; i < propsGo.transform.childCount; i++) targets.Add(propsGo.transform.GetChild(i));
            }
            var gpGo = GameObject.Find("Environment/Gameplay");
            if (gpGo != null)
            {
                foreach (var ac in gpGo.GetComponentsInChildren<AmmoCrate>(true))
                    if (ac != null) targets.Add(ac.transform);
            }
            var lightGo = GameObject.Find("Environment/Lighting");
            if (lightGo != null)
            {
                for (int i = 0; i < lightGo.transform.childCount; i++)
                {
                    var ch = lightGo.transform.GetChild(i);
                    if (ch.name.StartsWith("LampPole")) targets.Add(ch);
                }
            }
            var natureGo = GameObject.Find("Environment/Nature");
            if (natureGo != null)
            {
                foreach (var sub in new[] { "Trees", "Undergrowth" })
                {
                    var st = natureGo.transform.Find(sub);
                    if (st == null) continue;
                    for (int i = 0; i < st.childCount; i++) targets.Add(st.GetChild(i));
                }
            }
            var structGo = GameObject.Find("Environment/Structures");
            if (structGo != null)
            {
                string[] groundPropKeys = { "Desk", "Workbench", "Lockers", "Crate", "Barrel", "Chair", "Lantern", "Gascylender", "fireextinguisher", "Trashcan", "Tire", "Jersey", "Cablespool", "ConcreteBlock", "Bollard", "FallenTree", "BurntLog" };
                foreach (var t in structGo.GetComponentsInChildren<Transform>(true))
                {
                    if (t == structGo.transform || t.name.Contains("_LOD") || t.name == "Mesh" || (t.parent != null && (t.parent.name == "Mesh" || t.parent.GetComponent<LODGroup>() != null))) continue;
                    bool match = false;
                    for (int k = 0; k < groundPropKeys.Length; k++)
                        if (t.name.IndexOf(groundPropKeys[k], StringComparison.OrdinalIgnoreCase) >= 0) { match = true; break; }
                    if (!match) continue;
                    if (t.GetComponentInParent<Light>() != null) continue;
                    targets.Add(t);
                }
            }

            var seen = new HashSet<Transform>();
            var hits = new List<(float gap, string path)>();
            foreach (var ch in targets)
            {
                if (ch == null || !seen.Add(ch)) continue;
                if (ch.GetComponent<ParticleSystem>() != null || ch.GetComponent<Light>() != null) continue;
                var rs = ch.GetComponentsInChildren<Renderer>(true);
                if (rs.Length == 0) continue;
                Bounds b = default;
                bool hasB = false;
                foreach (var r in rs)
                {
                    if (r is ParticleSystemRenderer || r is LineRenderer) continue;
                    if (!hasB) { b = r.bounds; hasB = true; }
                    else b.Encapsulate(r.bounds);
                }
                if (!hasB || b.size.y < 0.02f) continue;
                var selfCols = new HashSet<Collider>(ch.GetComponentsInChildren<Collider>(true));
                bool isTree = ch.parent != null && ch.parent.name == "Trees";

                float SampleGap(float rx, float rz)
                {
                    float rayStart = Mathf.Max(b.min.y + Mathf.Min(0.45f, Mathf.Max(0.22f, b.size.y * 0.5f)), ch.position.y + (isTree ? 1.5f : 0.25f));
                    Vector3 origin = new Vector3(rx, rayStart, rz);
                    var rayHits = Physics.RaycastAll(origin, Vector3.down, 6.0f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
                    float bestFloorY = float.NegativeInfinity;
                    foreach (var h in rayHits)
                    {
                        if (selfCols.Contains(h.collider) || h.normal.y < 0.45f) continue;
                        if (h.point.y > bestFloorY) bestFloorY = h.point.y;
                    }
                    return float.IsNegativeInfinity(bestFloorY) ? 9.99f : (b.min.y - bestFloorY);
                }

                float minGap = float.MaxValue;
                if (isTree)
                {
                    // For branching trees, probe the trunk root base rather than wide overhanging leaf canopy edges.
                    Vector3 tc = ch.position;
                    float[] tGaps = { SampleGap(tc.x, tc.z), SampleGap(tc.x + 0.3f, tc.z), SampleGap(tc.x - 0.3f, tc.z), SampleGap(tc.x, tc.z + 0.3f), SampleGap(tc.x, tc.z - 0.3f) };
                    foreach (float g in tGaps) if (g < minGap) minGap = g;
                }
                else
                {
                    float insetX = Mathf.Min(0.12f, b.extents.x * 0.35f);
                    float insetZ = Mathf.Min(0.12f, b.extents.z * 0.35f);
                    float[] gaps =
                    {
                        SampleGap(b.center.x, b.center.z),
                        SampleGap(b.min.x + insetX, b.min.z + insetZ),
                        SampleGap(b.max.x - insetX, b.min.z + insetZ),
                        SampleGap(b.min.x + insetX, b.max.z - insetZ),
                        SampleGap(b.max.x - insetX, b.max.z - insetZ)
                    };
                    foreach (float g in gaps) if (g < minGap) minGap = g;
                }
                if (minGap > 0.08f) hits.Add((minGap, PathOf(ch)));
            }
            sb.AppendLine($"FLOATING ({hits.Count} movable props > 8 cm above static floor):");
            foreach (var h in hits.OrderByDescending(x => x.gap).Take(40))
                sb.AppendLine($"  +{h.gap * 100f:0.0} cm  {h.path}");
        }

        private static void Spawns(StringBuilder sb)
        {
            var sps = UnityEngine.Object.FindObjectsByType<SpawnPoint>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            int blocked = 0;
            var lines = new List<string>();
            foreach (var sp in sps)
            {
                if (SpawnPoint.IsClear(sp.transform.position)) continue;
                blocked++;
                lines.Add($"  {PathOf(sp.transform)} at {sp.transform.position}");
            }
            sb.AppendLine($"SPAWNS: {blocked}/{sps.Length} blocked for the soldier capsule");
            foreach (var l in lines) sb.AppendLine(l);
        }
    }
}
