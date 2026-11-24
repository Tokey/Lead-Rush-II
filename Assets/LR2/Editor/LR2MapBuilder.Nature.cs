using System.Collections.Generic;
using System.IO;
using LR2.Audio;
using LR2.Combat;
using LR2.Core;
using LR2.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using static LR2.EditorTools.LR2EditorUtil;

namespace LR2.EditorTools
{
    /// <summary>
    /// "Reclaimed" pass: turns the yard into a place people left a century ago. Forest around and inside the map,
    /// undergrowth, ivy and moss on the buildings, roots, fallen trees as cover, a collapsed warehouse roof with a tree
    /// growing through it, spider webs, dark interiors (AO decal volumes kill sky light indoors, so only sun shafts and
    /// muzzle flashes light them) and forest ambience. Every knob lives on the MapBuildConfig asset.
    /// </summary>
    public static partial class LR2MapBuilder
    {
        private const string Ruins = "Assets/ScansFactory/MedievalRuins";
        private static Transform _nature;
        private static readonly List<Vector3> _keepClear = new List<Vector3>();

        // Interior volumes of the three buildings (x0, z0, x1, z1, height) - must match the building builders.
        private static readonly Vector4[] Interiors =
        {
            new Vector4(-4.5f, -1.5f, 4.5f, 5.5f),      // control house (h 3.4)
            new Vector4(-26f, -8f, -16f, 4f),           // workshop (h 7)
            new Vector4(-14f, -22f, 14f, -12f),         // warehouse (h 7.5)
        };
        private static readonly float[] InteriorHeights = { 3.4f, 7f, 7.5f };

        /// <summary>One-time switch of an existing config asset to the dark, overgrown look.</summary>
        private static void ApplyReclaimedPreset(MapBuildConfig c)
        {
            if (c.presetVersion >= 4) return;
            if (c.presetVersion < 3)
            {
                c.sunElevation = 14f;
                c.sunAzimuth = 250f;
                c.sunLux = 14000f;
                c.sunTemperature = 3900f;
                c.fogMeanFreePath = 42f;
                c.fogBaseHeight = 1f;
                c.fogMaxHeight = 28f;
                c.fogAlbedo = new Color(0.6f, 0.68f, 0.66f);
                c.fogAnisotropy = 0.78f;
                c.fogDepthExtent = 70f;
                c.contrast = 20f;
                c.saturation = -14f;
                c.temperature = 2f;
                c.bloom = 0.06f;
                c.practicalLights = false;
                c.forest = true;
            }
            c.presetVersion = 5;
            c.lensDistortion = 0f;
            c.motionBlur = 0.1f;
            c.chromaticAberration = 0.04f;
            c.filmGrain = 0.18f;
            c.vignette = 0.22f;
            c.bloom = 0.06f;
            c.contrast = 20f;
            c.exposureCompensation = -0.1f;
            c.exposureLimitMin = 4.6f;
            c.exposureLimitMax = 13.6f;
            c.indoorDarkness = 0.28f;
            EditorUtility.SetDirty(c);
        }

        private static GameObject LoadRuins(string name)
        {
            foreach (var g in AssetDatabase.FindAssets(name + " t:Prefab", new[] { Ruins }))
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (Path.GetFileNameWithoutExtension(p) == name) return AssetDatabase.LoadAssetAtPath<GameObject>(p);
            }
            return null;
        }

        private static GameObject LoadLeartes(string name)
        {
            foreach (var g in AssetDatabase.FindAssets(name + " t:Prefab", new[] { "Assets/LeartesStudios" }))
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (Path.GetFileNameWithoutExtension(p) == name) return AssetDatabase.LoadAssetAtPath<GameObject>(p);
            }
            return null;
        }

        private enum ColliderMode { None, Keep, Trunk }

        private static GameObject Plant(GameObject prefab, Transform parent, Vector3 pos, Quaternion rot, float scale, ColliderMode mode, float trunkRadius = 0.3f)
        {
            if (prefab == null) return null;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            go.transform.SetPositionAndRotation(pos, rot);
            go.transform.localScale = Vector3.one * scale;
            if (mode != ColliderMode.Keep)
                foreach (var col in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(col);
            if (mode == ColliderMode.Trunk)
            {
                var cap = go.AddComponent<CapsuleCollider>();
                cap.radius = trunkRadius;
                cap.height = 9f;
                cap.center = new Vector3(0f, 4.5f, 0f);
                go.layer = LR2Layers.Default;
                go.AddComponent<SurfaceIdentifier>().surface = SurfaceType.Wood;
            }
            else if (mode == ColliderMode.Keep)
            {
                go.AddComponent<SurfaceIdentifier>().surface = SurfaceType.Wood;
            }
            if (mode == ColliderMode.None && parent != null && parent.name == "Undergrowth" && InPlay(pos, 3f))
            {
                // Keep all in-play ground foliage ankle-high (<= 0.30 m) so leaves never block crouching/ADS sightlines
                float maxY = pos.y;
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                    maxY = Mathf.Max(maxY, r.bounds.max.y);
                float h = maxY - pos.y;
                if (h > 0.30f)
                    go.transform.localScale *= (0.30f / h);
            }
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                if (mode == ColliderMode.None && r.bounds.size.y < 1.2f) r.shadowCastingMode = ShadowCastingMode.Off; // small plants: no shadow cost
            return go;
        }

        /// <summary>Ground hit under a point (terrain, slabs, floors). Ignores trees and movable props.</summary>
        private static bool Ground(Vector2 xz, out RaycastHit hit, float fromY = 30f)
        {
            hit = default;
            var hits = Physics.RaycastAll(new Vector3(xz.x, fromY, xz.y), Vector3.down, fromY + 25f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
            float bestY = float.NegativeInfinity;
            bool found = false;
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                if (h.collider == null || h.collider is CapsuleCollider) continue;
                if (_props != null && h.collider.transform.IsChildOf(_props)) continue;
                if (h.point.y > bestY)
                {
                    bestY = h.point.y;
                    hit = h;
                    found = true;
                }
            }
            return found && hit.point.y > -2.5f;
        }

        private static bool Indoors(Vector3 p) =>
            Physics.Raycast(p + Vector3.up * 0.4f, Vector3.up, 14f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);

        private static bool NearKeepClear(Vector3 p, float r)
        {
            foreach (var k in _keepClear)
                if ((new Vector2(k.x - p.x, k.z - p.z)).sqrMagnitude < r * r) return true;
            return false;
        }

        private static bool InPlay(Vector3 p, float margin = 0f) =>
            p.x > PlayMin.x - margin && p.x < PlayMax.x + margin && p.z > PlayMin.y - margin && p.z < PlayMax.y + margin;

        private static Quaternion Yaw() => Quaternion.Euler(0f, R(0f, 360f), 0f);

        private static void BuildNature()
        {
            if (!_cfg.forest) return;
            Physics.SyncTransforms();
            _nature = Child(_structures.parent, "Nature");
            _keepClear.Clear();
            foreach (Transform s in _spawns) _keepClear.Add(s.position);
            if (_tactical != null)
            {
                foreach (Transform t in _tactical)
                    if (t.name.StartsWith("Choke") || t.name.StartsWith("Obj")) _keepClear.Add(t.position);
            }
            // Explicit Saltworks doorways, stairs & loading dock ramps so greenery never blocks building entrances or stairs.
            foreach (var dp in new[]
            {
                new Vector3(-4.5f, 0f, 2f), new Vector3(4.5f, 0f, 2f),
                new Vector3(5.6f, 0f, -2.45f), new Vector3(-2.75f, 0f, -2.45f),
                new Vector3(-16f, 0f, -2f), new Vector3(-19f, 0f, 4f), new Vector3(-23f, 0f, -8f),
                new Vector3(-7f, 0f, -12f), new Vector3(5f, 0f, -12f),
                new Vector3(-7f, 0f, -14.6f), new Vector3(5f, 0f, -14.6f),
                new Vector3(-14f, 0f, -17f), new Vector3(14f, 0f, -17f),
                new Vector3(-13.8f, 0f, -10.8f), new Vector3(13.8f, 0f, -10.8f),
                new Vector3(-0.25f, 0f, -12f),
                new Vector3(24.6f, 0f, -14.2f), new Vector3(-17.2f, 0f, 17f)
            }) _keepClear.Add(dp);

            CollapseWarehouseRoof();
            Physics.SyncTransforms();

            string[] bigTrees = { "SM_Tree_01_01", "SM_Tree_01_01_1", "SM_Tree_01_01_2", "SM_Tree_01_03", "SM_Tree_02_01", "SM_Tree_03_01", "SM_Tree_03_01_1", "SM_Tree_03_01_2" };
            string[] smallTrees = { "SM_TreeSamll_01_01", "SM_TreeSmall_01_02", "SM_TreeSmall_01_03", "SM_TreeSmall_01_04" };
            string[] deadTrees = { "SM_TreeDead_01_01", "SM_TreeDead_01_02", "SM_TreeDead_01_03", "SM_BrokenTree_01_01", "SM_BrokenTree_02_01" };
            string[] fallen = { "SM_FallenTree_01_01", "SM_FallenTree_02_01", "SM_FallenTree_03_01", "SM_BurntLog_01_01", "SM_BurntLog_02_01", "SM_BurntLog_02_02" };
            string[] stumps = { "SM_TreeStump_01_01", "SM_TreeStump_02_01", "SM_TreeStump_03_01", "SM_TreeStump_04_01", "SM_TreeStump_05_01", "SM_TreeStump_06_01", "SM_TreeStump_07_01", "SM_TreeStump_08_01", "SM_TreeStump_10_01", "SM_TreeStump_11_01" };
            string[] debris = { "SM_Debris_01_01", "SM_Debris_03_01", "SM_Debris_04_01", "SM_Debris_05_01", "SM_Debris_06_01", "SM_Debris_07_01", "SM_Debris_08_01", "SM_Debris_09_01" };
            string[] ground = { "SM_Fern_01_02", "SM_Nettle_01_01", "SM_Nettle_01_02", "SM_Nettle_01_03", "SM_Burdock_01_01", "SM_Burdock_01_02", "SM_Grass_01_02", "SM_DryGrass_01_01", "SM_DryPlant_01_02" };
            string[] bushes = { "SM_Bush_01_01", "SM_Bush_01_02", "SM_Bush_01_03", "SM_BushGroup_01_01", "SM_BushGroup_01_02", "SM_BushGroup_01_03" };
            var P = new Dictionary<string, GameObject>();
            GameObject Get(string n)
            {
                if (!P.TryGetValue(n, out var go)) P[n] = go = LoadRuins(n) ?? LoadLeartes(n);
                return go;
            }
            GameObject Pick(string[] set) => Get(set[_rnd.Next(set.Length)]);

            var trees = Child(_nature, "Trees");
            var under = Child(_nature, "Undergrowth");
            var overgrowth = Child(_nature, "Overgrowth");
            var fx = Child(_nature, "FX");
            var placedTrees = new List<Vector3>();
            bool FarFromTrees(Vector3 p, float d)
            {
                foreach (var t in placedTrees) if ((t - p).sqrMagnitude < d * d) return false;
                return true;
            }

            // ---- forest wall around the yard (replaces the industrial skyline)
            int ring = 0;
            for (int i = 0; i < 2400 && ring < _cfg.forestTrees; i++)
            {
                var xz = new Vector2(R(-76f, 76f), R(-68f, 25f));
                if (xz.x > PlayMin.x - 1f && xz.x < PlayMax.x + 1f && xz.y > PlayMin.y - 1f && xz.y < PlayMax.y + 1f) continue;
                if (!Ground(xz, out var hit) || !(hit.collider is TerrainCollider) || hit.normal.y < 0.78f) continue;
                if (!FarFromTrees(hit.point, 4.2f)) continue;
                bool dead = _rnd.NextDouble() < 0.12;
                var prefab = dead ? Pick(deadTrees) : (_rnd.NextDouble() < 0.2 ? Pick(smallTrees) : Pick(bigTrees));
                if (Plant(prefab, trees, hit.point - Vector3.up * 0.28f, Yaw(), R(0.9f, 1.35f), ColliderMode.Trunk, 0.35f) == null) continue;
                placedTrees.Add(hit.point);
                ring++;
            }

            // ---- trees that took over the yard itself (outdoors, clear of spawns, doors, stairs, and structures)
            int inside = 0;
            for (int i = 0; i < 1500 && inside < _cfg.yardTrees; i++)
            {
                var xz = new Vector2(R(PlayMin.x + 2f, PlayMax.x - 2f), R(PlayMin.y + 2f, PlayMax.y - 2f));
                if (!Ground(xz, out var hit) || hit.normal.y < 0.85f || hit.point.y > 1.6f) continue;
                if (Indoors(hit.point) || NearKeepClear(hit.point, 5.5f) || !FarFromTrees(hit.point, 8.0f)) continue;
                if (Physics.CheckSphere(hit.point + Vector3.up * 3.0f, 5.2f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) continue;
                if (Plant(Pick(bigTrees), trees, hit.point - Vector3.up * 0.1f, Yaw(), R(0.78f, 0.95f), ColliderMode.Trunk, 0.3f) == null) continue;
                placedTrees.Add(hit.point);
                inside++;
            }
            // tree growing up through the collapsed warehouse roof
            if (Ground(new Vector2(-3f, -17.5f), out var whHit, 6.5f))
            {
                Plant(Get("SM_Tree_02_01") ?? Pick(bigTrees), trees, whHit.point, Yaw(), 1.05f, ColliderMode.Trunk, 0.3f);
                placedTrees.Add(whHit.point);
            }

            // ---- fallen trees and logs: organic cover in the open (wide clearance from buildings/containers/doors)
            int logs = 0;
            for (int i = 0; i < 600 && logs < _cfg.fallenTrees; i++)
            {
                var xz = new Vector2(R(PlayMin.x + 4.5f, PlayMax.x - 4.5f), R(PlayMin.y + 4.5f, PlayMax.y - 4.5f));
                if (!Ground(xz, out var hit) || !(hit.collider is TerrainCollider) || hit.normal.y < 0.9f || Indoors(hit.point) || NearKeepClear(hit.point, 6.0f)) continue;
                if (Physics.CheckSphere(hit.point + Vector3.up * 3.8f, 4.0f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) continue;
                var rot = Quaternion.FromToRotation(Vector3.up, hit.normal) * Yaw();
                var logGo = Plant(Pick(fallen), _structures, hit.point - Vector3.up * 0.06f, rot, R(0.9f, 1.05f), ColliderMode.Keep);
                if (logGo == null) continue;
                // Sink lowest endpoint flush onto terrain so sloped logs never float at one tip
                Physics.SyncTransforms();
                Bounds lb = default;
                bool hasLb = false;
                foreach (var r in logGo.GetComponentsInChildren<Renderer>(true))
                {
                    if (!hasLb) { lb = r.bounds; hasLb = true; }
                    else lb.Encapsulate(r.bounds);
                }
                if (hasLb)
                {
                    float minFloorY = GroundY(lb.center.x, lb.center.z);
                    minFloorY = Mathf.Min(minFloorY, GroundY(lb.min.x + 0.25f, lb.min.z + 0.25f));
                    minFloorY = Mathf.Min(minFloorY, GroundY(lb.max.x - 0.25f, lb.max.z - 0.25f));
                    minFloorY = Mathf.Min(minFloorY, GroundY(lb.min.x + 0.25f, lb.max.z - 0.25f));
                    minFloorY = Mathf.Min(minFloorY, GroundY(lb.max.x - 0.25f, lb.min.z + 0.25f));
                    logGo.transform.position += Vector3.up * (minFloorY - lb.min.y - 0.06f);
                }
                logs++;
            }

            // ---- stumps + branch debris outside buildings without clipping into walls
            for (int i = 0; i < 70; i++)
            {
                var xz = new Vector2(R(PlayMin.x - 10f, PlayMax.x + 10f), R(PlayMin.y - 10f, PlayMax.y + 10f));
                if (!Ground(xz, out var hit) || Indoors(hit.point) || NearKeepClear(hit.point, 3.4f)) continue;
                if (Physics.CheckSphere(hit.point + Vector3.up * 0.65f, 0.65f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)) continue;
                bool stump = i % 3 == 0;
                Plant(stump ? Pick(stumps) : Pick(debris), under, hit.point - Vector3.up * 0.05f, Quaternion.FromToRotation(Vector3.up, hit.normal) * Yaw(), R(0.8f, 1.1f), stump ? ColliderMode.Keep : ColliderMode.None);
            }

            // ---- undergrowth (noise-driven patches on terrain only inside play, clear of doors/stairs/concrete)
            int plants = 0;
            for (int i = 0; i < _cfg.undergrowth * 3 && plants < _cfg.undergrowth; i++)
            {
                var xz = new Vector2(R(PlayMin.x - 16f, PlayMax.x + 16f), R(PlayMin.y - 14f, PlayMax.y + 10f));
                float n = Mathf.PerlinNoise(xz.x * 0.11f + 31f, xz.y * 0.11f + 7f);
                if (n < 0.38f) continue;
                if (!Ground(xz, out var hit) || hit.normal.y < 0.7f) continue;
                bool onTerrain = hit.collider is TerrainCollider;
                bool inPlay = InPlay(hit.point, 3f);
                if (!onTerrain && (inPlay || _rnd.NextDouble() < 0.75)) continue;
                if (Indoors(hit.point) || NearKeepClear(hit.point, inPlay ? 3.5f : 2.8f)) continue;
                bool bush = n > 0.72f && _rnd.NextDouble() < 0.16 && !inPlay;
                Plant(bush ? Pick(bushes) : Pick(ground), under, hit.point - Vector3.up * 0.03f, Quaternion.FromToRotation(Vector3.up, hit.normal) * Yaw(),
                      bush ? R(0.8f, 1.15f) : R(0.7f, 1.15f), ColliderMode.None);
                plants++;
            }

            // ---- buildings reclaimed: ivy on tall solid walls, low grass at wall bases, roof foliage/decals (no opaque SM_Moss discs)
            var ivy = new[] { LoadLeartes("SM_Ivy"), LoadLeartes("SM_Ivy1"), LoadLeartes("SM_Ivy2") };
            var grassL = new[] { LoadLeartes("SM_Grass2Foliage"), LoadLeartes("SM_Grass3Foliage"), LoadLeartes("SM_Grass4Foliage") };
            var roots = new[] { Get("SM_Roots_01_01"), Get("SM_Roots_01_02"), Get("SM_Roots_01_03"), Get("SM_Roots_01_04") };
            var leafDecal = FindLeartes("M_LeafDecal");
            int ivyCount = 0, mossCount = 0, baseGrass = 0;
            foreach (var r in _structures.GetComponentsInChildren<MeshRenderer>())
            {
                if (r.transform.IsChildOf(_nature)) continue;
                var b = r.bounds;
                bool wallLike = b.size.y > 2.2f && (b.size.x < 0.6f || b.size.z < 0.6f) && Mathf.Max(b.size.x, b.size.z) > 2f;
                bool topLike = b.size.y < 0.6f && b.size.x > 4f && b.size.z > 4f && b.max.y > 6.5f;
                if (wallLike)
                {
                    bool alongX = b.size.x > b.size.z;
                    float len = alongX ? b.size.x : b.size.z;
                    int count = Mathf.Clamp(Mathf.RoundToInt(len / 4.5f), 1, 6);
                    for (int k = 0; k < count; k++)
                    {
                        float u = R(0.12f, 0.88f);
                        float side = _rnd.NextDouble() < 0.5 ? -1f : 1f;
                        Vector3 n = alongX ? new Vector3(0f, 0f, side) : new Vector3(side, 0f, 0f);
                        Vector3 p = alongX ? new Vector3(Mathf.Lerp(b.min.x, b.max.x, u), 0f, b.center.z) : new Vector3(b.center.x, 0f, Mathf.Lerp(b.min.z, b.max.z, u));
                        // ivy only on tall solid wall spans (verified 1.8m below so it never crosses windows/doors or sinks underground)
                        Vector3 top = p + Vector3.up * (b.max.y - R(0.2f, 0.55f)) + n * 1.5f;
                        if (b.size.y >= 4.2f && !Indoors(top - n * 1.5f + n * 0.6f) &&
                            Physics.Raycast(top, -n, out var wh, 2.5f, LR2Layers.EnvironmentMask) &&
                            Physics.Raycast(top - Vector3.up * 1.8f, -n, 2.5f, LR2Layers.EnvironmentMask) &&
                            !NearKeepClear(wh.point, 2.8f) && ivy[k % 3] != null)
                        {
                            var g = Plant(ivy[_rnd.Next(3)], overgrowth, wh.point + wh.normal * 0.04f, Quaternion.LookRotation(-wh.normal, Vector3.up), R(0.55f, 0.85f), ColliderMode.None);
                            if (g != null) AlignThinAxis(g, wh.normal);
                            ivyCount++;
                        }
                        // low grass + compact roots along outside wall bases only
                        Vector3 basePt = p + n * R(0.45f, 0.85f);
                        if (!Indoors(basePt) && Ground(new Vector2(basePt.x, basePt.z), out var gh, b.min.y + 1.5f) &&
                            gh.collider is TerrainCollider && !Indoors(gh.point) && !NearKeepClear(gh.point, 3.4f) &&
                            !Physics.CheckSphere(gh.point + Vector3.up * 0.45f, 0.4f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                        {
                            var gp = grassL[_rnd.Next(3)] ?? Pick(ground);
                            Plant(gp, under, gh.point, Yaw(), R(0.65f, 0.95f), ColliderMode.None);
                            baseGrass++;
                            if (_rnd.NextDouble() < 0.2 && roots[0] != null)
                                Plant(roots[_rnd.Next(roots.Length)], overgrowth, gh.point, Quaternion.LookRotation(n) * Quaternion.Euler(0f, R(-30f, 30f), 0f), R(0.4f, 0.62f), ColliderMode.None);
                        }
                    }
                }
                else if (topLike)
                {
                    int count = Mathf.Clamp(Mathf.RoundToInt(b.size.x * b.size.z / 24f), 1, 8);
                    for (int k = 0; k < count; k++)
                    {
                        var p = new Vector3(R(b.min.x + 0.8f, b.max.x - 0.8f), b.max.y + 3f, R(b.min.z + 0.8f, b.max.z - 0.8f));
                        if (!Physics.Raycast(p, Vector3.down, out var th, 4f, LR2Layers.EnvironmentMask) || th.normal.y < 0.8f) continue;
                        if (NearKeepClear(th.point, 2.0f)) continue;
                        if (leafDecal != null && k % 2 == 0)
                            CreateDecal(leafDecal, th.point, Vector3.up, R(1.6f, 2.8f), true);
                        else
                            Plant(Pick(ground), overgrowth, th.point, Yaw(), R(0.45f, 0.7f), ColliderMode.None);
                        mossCount++;
                    }
                    if (_rnd.NextDouble() < 0.45)
                    {
                        var p = new Vector3(R(b.min.x + 1.5f, b.max.x - 1.5f), b.max.y + 3f, R(b.min.z + 1.5f, b.max.z - 1.5f));
                        if (Physics.Raycast(p, Vector3.down, out var th, 4f, LR2Layers.EnvironmentMask) && th.normal.y > 0.8f)
                            Plant(Pick(bushes), overgrowth, th.point, Yaw(), R(0.45f, 0.65f), ColliderMode.None);
                    }
                }
            }

            // ---- spider webs in interior corners + leaves blown inside
            var webs = new[] { Get("SM_SpiderWeb_01_04_Wall"), Get("SM_SpiderWeb_01_04_Wall_1"), Get("SM_SpiderWeb_01_04_Wall_2"), Get("SM_SpiderWeb_01_01_Pillar") };
            for (int i = 0; i < Interiors.Length; i++)
            {
                var a = Interiors[i];
                float h = InteriorHeights[i];
                Vector3[] corners = { new Vector3(a.x + 0.3f, h - 0.4f, a.y + 0.3f), new Vector3(a.z - 0.3f, h - 0.4f, a.y + 0.3f), new Vector3(a.x + 0.3f, h - 0.4f, a.w - 0.3f), new Vector3(a.z - 0.3f, h - 0.4f, a.w - 0.3f) };
                foreach (var c in corners)
                {
                    var w = webs[_rnd.Next(webs.Length)];
                    if (w == null) continue;
                    Vector3 toCenter = new Vector3((a.x + a.z) * 0.5f - c.x, 0f, (a.y + a.w) * 0.5f - c.z).normalized;
                    Plant(w, overgrowth, c, Quaternion.LookRotation(toCenter), R(0.6f, 1f), ColliderMode.None);
                }
            }
            if (leafDecal != null)
            {
                var leafPts = new List<Vector3>();
                for (int i = 0; i < 90; i++)
                {
                    var xz = new Vector2(R(PlayMin.x, PlayMax.x), R(PlayMin.y, PlayMax.y));
                    if (!Ground(xz, out var hit, 2.5f) || hit.normal.y < 0.8f) continue;
                    if (hit.collider != null && hit.collider.GetComponentInParent<DestructibleProp>() != null) continue;
                    bool dup = false;
                    for (int j = 0; j < leafPts.Count; j++)
                        if ((leafPts[j] - hit.point).sqrMagnitude < 1.8f * 1.8f) { dup = true; break; }
                    if (dup) continue;
                    CreateDecal(leafDecal, hit.point, hit.normal, R(1.5f, 3.2f), true);
                    leafPts.Add(hit.point);
                }
            }

            DarkInteriors();

            // ---- forest FX: perimeter low fog banks outside play, falling leaves, crows overhead
            void FX(string n, Vector3 p, float s = 1f) { var pf = Get(n); if (pf != null) Plant(pf, fx, p, Yaw(), s, ColliderMode.None); }
            for (int i = 0; i < 6; i++)
            {
                Vector3 fp = Vector3.zero;
                for (int t = 0; t < 10; t++)
                {
                    fp = new Vector3(R(-52f, 52f), 0f, R(-52f, 20f));
                    if (!InPlay(fp, 6f)) break;
                }
                if (!InPlay(fp, 6f)) FX("SM_FogBig", G(fp, 0.3f));
            }
            for (int i = 0; i < 5; i++) FX("SM_Leafs", G(new Vector3(R(-24f, 24f), 0f, R(-20f, 20f)), 6f));
            FX("SM_Birds", new Vector3(0f, 26f, -4f));

            ForestAmbience();
            BuildWeather(under, overgrowth);
            Debug.Log($"[LR2] Nature: {ring} forest trees, {inside} yard trees, {logs} logs, {plants} plants, {ivyCount} ivy, {mossCount} moss, {baseGrass} wall-base grass.");
        }

        /// <summary>Rotates a flat foliage patch so its thinnest axis points along the surface normal.</summary>
        private static void AlignThinAxis(GameObject go, Vector3 normal)
        {
            var mf = go.GetComponentInChildren<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return;
            var size = mf.sharedMesh.bounds.size;
            Vector3 thin = size.x <= size.y && size.x <= size.z ? Vector3.right : size.y <= size.z ? Vector3.up : Vector3.forward;
            Vector3 thinWorld = mf.transform.TransformDirection(thin);
            go.transform.rotation = Quaternion.FromToRotation(thinWorld, normal) * go.transform.rotation;
        }

        /// <summary>Removes part of the warehouse roof (decades of rot): sun shafts pour into the dark hall.</summary>
        private static void CollapseWarehouseRoof()
        {
            foreach (var t in _structures.GetComponentsInChildren<Transform>())
            {
                if (t.name != "Roof" || Mathf.Abs(t.position.z + 17f) > 0.5f || t.lossyScale.x < 20f) continue;
                Vector3 c = t.position, s = t.lossyScale;
                var parent = t.parent;
                var mat = t.GetComponent<Renderer>().sharedMaterial;
                Object.DestroyImmediate(t.gameObject);
                // hole: x [-5.6, -0.6], z [-20.4, -14.6]
                float x0 = c.x - s.x * 0.5f, x1 = c.x + s.x * 0.5f, z0 = c.z - s.z * 0.5f, z1 = c.z + s.z * 0.5f;
                void Seg(string n, float ax, float az, float bx, float bz) =>
                    Box(n, parent, new Vector3((ax + bx) * 0.5f, c.y, (az + bz) * 0.5f), new Vector3(bx - ax, s.y, bz - az), mat, LR2Layers.Default, SurfaceType.Metal);
                Seg("Roof", x0, z0, -5.6f, z1);
                Seg("Roof", -0.6f, z0, x1, z1);
                Seg("Roof", -5.6f, z0, -0.6f, -20.4f);
                Seg("Roof", -5.6f, -14.6f, -0.6f, z1);
                // a sagging broken panel hanging into the hole
                var panel = Box("RoofPanelCollapsed", parent, new Vector3(-1.4f, c.y - 1.3f, -17.5f), new Vector3(2.2f, 0.06f, 4.6f), mat, LR2Layers.Default, SurfaceType.Metal);
                panel.transform.rotation = Quaternion.Euler(0f, 0f, 52f);
                break;
            }
        }

        /// <summary>
        /// Uses local HDRP Volume IndirectLightingController (without DecalProjectors) so interiors feel sheltered
        /// without causing AO-decal flicker or pitch-black rooms on moving soldiers and weapons.
        /// </summary>
        private static void DarkInteriors()
        {
            if (_cfg.indoorDarkness <= 0.01f) return;
            var parent = Child(_lighting, "InteriorDarkness");
            var indoorProfile = NewProfile(LightDir + "/LR2_Indoor_Profile.asset");
            var ilc = indoorProfile.Add<IndirectLightingController>(true);
            ilc.indirectDiffuseLightingMultiplier.Override(Mathf.Max(0.88f, 1f - _cfg.indoorDarkness * 0.4f));
            ilc.reflectionLightingMultiplier.Override(Mathf.Max(0.85f, 1f - _cfg.indoorDarkness * 0.4f));
            SaveProfile(indoorProfile);
            for (int i = 0; i < Interiors.Length; i++)
            {
                var a = Interiors[i];
                float h = InteriorHeights[i];
                var vgo = new GameObject("IndoorVolume_" + i);
                vgo.transform.SetParent(parent, false);
                vgo.transform.position = new Vector3((a.x + a.z) * 0.5f, (h - 0.4f) * 0.5f, (a.y + a.w) * 0.5f);
                var box = vgo.AddComponent<BoxCollider>();
                box.isTrigger = true;
                box.size = new Vector3(a.z - a.x - 0.6f, h, a.w - a.y - 0.6f);
                var vol = vgo.AddComponent<Volume>();
                vol.isGlobal = false;
                vol.blendDistance = 1.2f;
                vol.priority = 5f;
                vol.sharedProfile = indoorProfile;
            }
        }

        private static void ForestAmbience()
        {
            AudioClip Clip(string n)
            {
                foreach (var g in AssetDatabase.FindAssets(n + " t:AudioClip", new[] { Ruins + "/Common/Sounds" }))
                    return AssetDatabase.LoadAssetAtPath<AudioClip>(AssetDatabase.GUIDToAssetPath(g));
                return null;
            }
            var env = Object.FindAnyObjectByType<LR2AudioEnvironment>();
            var day = Clip("S_Ambient_01_03_SwampForestLoop");
            var wind = Clip("S_WindBlowLoopStereo_01_01");
            if (env != null && day != null)
            {
                var day2 = Clip("S_Ambient_01_01_SwampForestDayLoop");
                env.ambienceLoops = day2 != null ? new[] { day, day2 } : new[] { day }; // no wind loop
                env.ambienceVolume = 0.42f;
                EditorUtility.SetDirty(env);
            }
            var shots = new GameObject("ForestOneShots").AddComponent<AmbientOneShots>();
            shots.transform.SetParent(_systems, false);
            var list = new List<AudioClip>();
            foreach (var n in new[] { "S_CrowTalk_01_01", "S_CrowTalk_01_02", "S_CrowTalk_01_03", "S_CrowTalk_01_04", "S_BirdsCrowd_01_01", "S_DistantStorm_01_01", "S_Crickets_01_01", "S_Crickets_01_02", "S_Crickets_01_03", "S_Crickets_01_04", "S_Crickets_01_05", "S_Crickets_01_06", "S_Crickets_01_07", "S_Crickets_01_08", "S_BirdsCrowd_01_01", "S_CrowTalk_01_02" })
            {
                var c = Clip(n);
                if (c != null) list.Add(c);
            }
            shots.outdoorClips = list.ToArray();
            shots.outdoorInterval = new Vector2(2.5f, 7f);
            shots.outdoorVolume = 0.45f;
            var drops = new List<AudioClip>();
            for (int i = 1; i <= 15; i++)
            {
                var c = Clip($"S_WaterDrop_01_{i:00}");
                if (c != null) drops.Add(c);
            }
            shots.dripClips = drops.ToArray();
            shots.dripPoints = new[] { new Vector3(0f, 3f, 2f), new Vector3(-21f, 6f, -2f), new Vector3(-8f, 7f, -17f), new Vector3(7f, 7f, -19f) };
        }

        /// <summary>Dynamic weather (rain, storms, fog, lightning) + wind that moves the plants.</summary>
        private static void BuildWeather(Transform under, Transform overgrowth)
        {
            // swaying plants can't be static-batched
            foreach (var root in new[] { under, overgrowth })
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    GameObjectUtility.SetStaticEditorFlags(t.gameObject, 0);

            var go = new GameObject("Weather");
            go.transform.SetParent(_systems, false);
            var ws = go.AddComponent<WeatherSystem>();
            // B93 (user 11-19: "the rain is not visible at all"): Saltworks used to open in clear dusk; it opens in drizzle now (like
            // Deadsector) and the dynamic cycle moves on from there
            ws.startPreset = 2;
            ws.randomStart = false;
            foreach (var l in _lighting.GetComponentsInChildren<Light>())
                if (l.type == LightType.Directional && l.name == "Sun") ws.sun = l;
            var gv = _lighting.Find("GlobalVolume");
            if (gv != null) ws.globalVolume = gv.GetComponent<Volume>();

            var lgo = new GameObject("LightningLight");
            lgo.transform.SetParent(go.transform, false);
            lgo.transform.rotation = Quaternion.Euler(55f, 120f, 0f);
            var ll = lgo.AddComponent<Light>();
            ll.type = LightType.Directional;
            ll.lightUnit = LightUnit.Lux;
            ll.intensity = 0f;
            ll.useColorTemperature = true;
            ll.colorTemperature = 9000f;
            ll.shadows = LightShadows.None;
            lgo.AddComponent<HDAdditionalLightData>();
            ws.lightning = ll;

            var wz = new GameObject("Wind").AddComponent<WindZone>();
            wz.transform.SetParent(go.transform, false);
            wz.transform.rotation = Quaternion.Euler(0f, 70f, 0f);
            wz.mode = WindZoneMode.Directional;
            ws.windZone = wz;

            // rain: stretched streaks in a box that follows the camera, killed by roofs/ground
            var rgo = new GameObject("Rain");
            rgo.transform.SetParent(go.transform, false);
            var ps = rgo.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.startLifetime = 1.4f;
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.012f, 0.022f);
            main.startColor = new Color(0.75f, 0.8f, 0.85f, 0.35f);
            main.maxParticles = 2400;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.playOnAwake = false;
            var em = ps.emission;
            em.rateOverTime = 0f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(34f, 0.5f, 34f);
            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(0.4f, 0.6f);
            vel.y = new ParticleSystem.MinMaxCurve(-13f, -11f);
            vel.z = new ParticleSystem.MinMaxCurve(-0.1f, 0.1f);
            var col = ps.collision;
            col.enabled = true;
            col.type = ParticleSystemCollisionType.World;
            col.mode = ParticleSystemCollisionMode.Collision3D;
            col.quality = ParticleSystemCollisionQuality.Low;
            col.collidesWith = LR2Layers.EnvironmentMask;
            col.lifetimeLoss = 1f;
            var psr = rgo.GetComponent<ParticleSystemRenderer>();
            psr.renderMode = ParticleSystemRenderMode.Stretch;
            psr.velocityScale = 0.045f;
            psr.lengthScale = 1f;
            psr.shadowCastingMode = ShadowCastingMode.Off;
            Texture2D streak = null;
            foreach (var g in AssetDatabase.FindAssets("T_Tracer t:Texture2D", new[] { "Assets/LR2" }))
                streak = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(g));
            psr.sharedMaterial = SaveMaterial(NewParticle(streak, new Color(0.7f, 0.76f, 0.82f, 0.5f), 0.35f, false), MatDir + "/M_LR2_Rain.mat");
            ws.rain = ps;

            var thunder = new List<AudioClip>();
            foreach (var n in new[] { "S_DistantStorm_01_01", "S_DistantStorm_01_02", "S_DistantStorm_01_03", "S_DistantStorm_01_04" })
                foreach (var g in AssetDatabase.FindAssets(n + " t:AudioClip", new[] { Ruins }))
                    thunder.Add(AssetDatabase.LoadAssetAtPath<AudioClip>(AssetDatabase.GUIDToAssetPath(g)));
            ws.thunderClips = thunder.ToArray();

            var sway = go.AddComponent<FoliageSway>();
            sway.roots = new[] { under };
        }
    }
}
