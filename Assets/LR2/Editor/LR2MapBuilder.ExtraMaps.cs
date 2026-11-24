using System.Collections.Generic;
using System.IO;
using LR2.AI;
using LR2.Audio;
using LR2.Combat;
using LR2.Core;
using LR2.FX;
using LR2.Game;
using LR2.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using static LR2.EditorTools.LR2EditorUtil;

namespace LR2.EditorTools
{
    public static partial class LR2MapBuilder
    {
        // =================================================================================================
        // Map 2: Deadsector — Dark, war-torn abandoned city block (no foliage)
        // =================================================================================================
        public static void BuildDeadsector()
        {
            var root = BeginExtraMap("Deadsector", "Assets/LR2/Data/Map/DeadsectorConfig.asset", 4021, out var scene);
            var env = root.transform.Find("Environment");
            Vector2 pMin = new Vector2(-28f, -28f), pMax = new Vector2(28f, 28f);

            // Ground: damaged asphalt intersection + 4 distinct corner sidewalks (no overlapping slabs)
            var g = Child(_structures, "Ground");
            Box("AsphaltBase", g, new Vector3(0f, -0.25f, 0f), new Vector3(130f, 0.5f, 130f), _m.asphalt, LR2Layers.Default, SurfaceType.Concrete);
            void Sidewalk(string n, float cx, float cz, float sx, float sz) =>
                Box(n, g, new Vector3(cx, 0.06f, cz), new Vector3(sx, 0.12f, sz), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
            Sidewalk("SidewalkNW", -17.5f, 17.5f, 21f, 21f);
            Sidewalk("SidewalkNE", 17.5f, 17.5f, 21f, 21f);
            Sidewalk("SidewalkSW", -17.5f, -17.5f, 21f, 21f);
            Sidewalk("SidewalkSE", 17.5f, -17.5f, 21f, 21f);
            // Road markings
            for (int i = -24; i <= 24; i += 6)
            {
                if (Mathf.Abs(i) < 7) continue;
                var sx = Box("LaneX", g, new Vector3(i, 0.008f, 0f), new Vector3(2.4f, 0.008f, 0.14f), _m.curb);
                Object.DestroyImmediate(sx.GetComponent<Collider>());
                var sz = Box("LaneZ", g, new Vector3(0f, 0.008f, i), new Vector3(0.14f, 0.008f, 2.4f), _m.curb);
                Object.DestroyImmediate(sz.GetComponent<Collider>());
            }

            // 4 Enterable war-torn corner buildings (wide doorways, damaged parapets, destructible windows & furniture)
            void CornerRuins(string name, float x0, float x1, float z0, float z1, bool brokenRoof, bool glassOnSouthWall)
            {
                var b = Child(_structures, name);
                float cx = (x0 + x1) * 0.5f, cz = (z0 + z1) * 0.5f;
                float w = x1 - x0, d = z1 - z0, hgt = 4.0f, th = 0.28f;
                Box("Floor", b, new Vector3(cx, 0.08f, cz), new Vector3(w, 0.12f, d), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
                // Side facing X=0 and Z=0 gets big doorways & broken windows
                float innerDoor = 2.4f;
                Vector4 dSpec(float off) => new Vector4(off, innerDoor, 0f, 2.5f);
                Vector4 wSpec(float off) => new Vector4(off, 1.8f, 0.95f, 2.4f);
                Wall("WallN", b, new Vector3(x0, 0.04f, z1), new Vector3(x1, 0.04f, z1), hgt, th, _m.wallPlaster, !glassOnSouthWall ? wSpec(w * 0.3f) : (d < 14f ? dSpec(w * 0.5f) : wSpec(w * 0.3f)), dSpec(w * 0.72f));
                Wall("WallS", b, new Vector3(x0, 0.04f, z0), new Vector3(x1, 0.04f, z0), hgt, th, _m.wallPlaster, dSpec(w * 0.35f), wSpec(w * 0.75f));
                Wall("WallW", b, new Vector3(x0, 0.04f, z0), new Vector3(x0, 0.04f, z1), hgt, th, _m.wallPlaster, dSpec(d * 0.55f));
                Wall("WallE", b, new Vector3(x1, 0.04f, z0), new Vector3(x1, 0.04f, z1), hgt, th, _m.wallPlaster, dSpec(d * 0.45f), wSpec(d * 0.8f));
                // Breakable glass fitted precisely inside the street-facing window frame (never blocks doorways)
                float gx = x0 + (glassOnSouthWall ? w * 0.75f : w * 0.3f);
                float gz = glassOnSouthWall ? z0 : z1;
                Box("Glass_" + name, b, new Vector3(gx, 1.71f, gz), new Vector3(1.74f, 1.45f, 0.04f), _m.glass, LR2Layers.Default, SurfaceType.Glass);
                if (brokenRoof)
                {
                    var rw = Box("RoofWest", b, new Vector3(x0 + w * 0.26f, hgt + 0.14f, cz), new Vector3(w * 0.52f, 0.22f, d + 0.4f), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
                    var re = Box("RoofEast", b, new Vector3(x1 - w * 0.16f, hgt + 0.14f, cz), new Vector3(w * 0.32f, 0.22f, d + 0.4f), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
                    ApplyMeshKeepBoxCollider(rw, GetWarpedWallMesh(name.GetHashCode() + 1, false, 0.1f, 0.025f));
                    ApplyMeshKeepBoxCollider(re, GetWarpedWallMesh(name.GetHashCode() + 2, false, 0.1f, 0.025f));
                    // Bent steel rebar protruding along the blasted roof breach
                    AddWarTornWallDetails(b, new Vector3(x0 + w * 0.52f, hgt + 0.15f, cz), d * 0.8f, hgt + 0.3f, 90f, _m.concrete, name.GetHashCode() + 7, true, false);
                }
                else
                {
                    Box("Roof", b, new Vector3(cx, hgt + 0.14f, cz), new Vector3(w + 0.4f, 0.2f, d + 0.4f), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
                }
                // Uneven jagged rooftop parapets + exterior corner rubble (gives ruined city silhouette without altering nav)
                int rs = Mathf.Abs(name.GetHashCode());
                void Parapet(string pn, Vector3 pc, float plen, float yaw)
                {
                    var pgo = Box(pn, b, pc, new Vector3(plen, 0.95f, 0.32f), _m.wallPlaster, LR2Layers.Default, SurfaceType.Concrete, yaw);
                    ApplyMeshKeepBoxCollider(pgo, GetWarpedWallMesh(rs + pn.GetHashCode(), true, 0.36f, 0.045f));
                    AddWarTornWallDetails(b, pc, plen * 0.9f, pc.y + 0.4f, yaw, _m.concrete, rs + pn.GetHashCode(), true, false);
                }
                Parapet("ParapetN", new Vector3(cx, hgt + 0.62f, z1 - 0.1f), w * 0.96f, 0f);
                Parapet("ParapetS", new Vector3(cx, hgt + 0.62f, z0 + 0.1f), w * 0.96f, 0f);
                Parapet("ParapetW", new Vector3(x0 + 0.1f, hgt + 0.58f, cz), d * 0.92f, 90f);
                Parapet("ParapetE", new Vector3(x1 - 0.1f, hgt + 0.58f, cz), d * 0.92f, 90f);
                // Interior destructible cover props + warm interior emergency bulb (B75 pack meshes)
                FittedPackProp("Desk", "SM_Table_01", b, new Vector3(cx - 1.5f, 0.54f, cz + 1.2f), new Vector3(2.0f, 0.8f, 0.85f), 0f, SurfaceType.Wood);
                PropOrBox("SM_Crate_01", "Crate", b, new Vector3(cx + 1.8f, 0.14f, cz - 1.4f), 18f, 0.95f, new Vector3(0.95f, 0.9f, 0.95f), _m.wood, SurfaceType.Wood);
                Prop("SM_Chair_01", b, new Vector3(cx - 1.5f, 0.14f, cz + 0.25f), 175f, 1f, true, SurfaceType.Wood);
                FittedPackProp("Lockers", "SM_Crate_02", b, new Vector3(cx - 2.2f, 1.14f, cz - 2.0f), new Vector3(0.6f, 2.0f, 1.6f), 90f, SurfaceType.Wood);
                AddLight(_lighting, "Emergency_" + name, new Vector3(cx, 3.4f, cz), Quaternion.identity, LightType.Point, 3600f, 4000f, 16f, 0f, false);
                AddLight(_lighting, "OpeningBounce_" + name, new Vector3(cx - Mathf.Sign(cx) * 3.6f, 2.8f, cz - Mathf.Sign(cz) * 5.2f), Quaternion.identity, LightType.Point, 5000f, 3600f, 17f, 0f, false);
            }
            CornerRuins("TransitDepot_NW", -24f, -11f, 11f, 23f, true, true);
            CornerRuins("Pharmacy_NE", 11f, 24f, 11f, 23f, false, true);
            CornerRuins("Apartment_SW", -24f, -11f, -23f, -11f, false, false);
            CornerRuins("Garage_SE", 11f, 24f, -23f, -11f, true, false);

            // Central checkpoint ruined bus/container + crossroad cover
            Container(_structures, "CheckpointContA", new Vector3(-2.5f, 0f, 2.5f), 22f, 2);
            Container(_structures, "CheckpointContB", new Vector3(3.2f, 0f, -3.5f), -68f, 0);
            // B46: street crossing Jersey barriers + cones + cable spools (~10m out along main street)
            PropOrBox("SM_Jersey", "StreetCoverN", _structures, new Vector3(-1.2f, 0f, 9.5f), 8f, 1f, new Vector3(2.4f, 1.04f, 0.75f), _m.concrete, SurfaceType.Concrete);
            PropOrBox("SM_Jersey", "StreetCoverS", _structures, new Vector3(1.2f, 0f, -9.5f), -8f, 1f, new Vector3(2.4f, 1.04f, 0.75f), _m.concrete, SurfaceType.Concrete);
            Prop("SM_Conebarrier", _props, new Vector3(-3.0f, 0f, 9.6f), 12f, 1f, true, SurfaceType.Plastic);
            Prop("SM_Conebarrier", _props, new Vector3(3.0f, 0f, -9.6f), -12f, 1f, true, SurfaceType.Plastic);

            // Perimeter walls (broken masonry silhouettes with protruding rebar & base rubble) + Clip + War-torn city skyline
            var bnd = Child(_structures, "Boundary");
            void DeadBoundary(string n, Vector3 center, float len, float yaw, int seed)
            {
                var bw = Box(n, bnd, center, new Vector3(len, 4.9f, 0.55f), _m.wallPlaster, LR2Layers.Default, SurfaceType.Concrete, yaw);
                ApplyMeshKeepBoxCollider(bw, GetWarpedWallMesh(seed, true, 0.24f, 0.055f));
                AddWarTornWallDetails(bnd, center, len, 4.5f, yaw, _m.wallPlaster, seed, true, true);
            }
            DeadBoundary("WallN", new Vector3(0f, 2.4f, 28.5f), 57.5f, 0f, 101);
            DeadBoundary("WallS", new Vector3(0f, 2.4f, -28.5f), 57.5f, 0f, 102);
            DeadBoundary("WallW", new Vector3(-28.5f, 2.4f, 0f), 56.5f, 90f, 103);
            DeadBoundary("WallE", new Vector3(28.5f, 2.4f, 0f), 56.5f, 90f, 104);
            AddPlayerClipBox(pMin, pMax, 10f);

            // Shattered, stepped urban skyline ruins (warped/broken concrete towers + tilted collapsed upper slabs)
            int ti = 0;
            foreach (var (tn, tp, ts) in new[]
            {
                ("SkyTower_W1", new Vector3(-40f, 6.5f, -16f), new Vector3(10f, 13f, 14f)),
                ("SkyTower_W2", new Vector3(-41f, 8.0f, 16f),  new Vector3(11f, 16f, 14f)),
                ("SkyTower_E1", new Vector3(40f, 7.0f, -16f),  new Vector3(10f, 14f, 14f)),
                ("SkyTower_E2", new Vector3(41f, 6.0f, 16f),   new Vector3(11f, 12f, 14f)),
                ("SkyTower_N1", new Vector3(-16f, 7.5f, 40f),  new Vector3(14f, 15f, 10f)),
                ("SkyTower_N2", new Vector3(16f, 6.5f, 41f),   new Vector3(14f, 13f, 10f)),
                ("SkyTower_S1", new Vector3(-16f, 6.0f, -40f), new Vector3(14f, 12f, 10f)),
                ("SkyTower_S2", new Vector3(16f, 8.0f, -41f),  new Vector3(14f, 16f, 10f)),
            })
            {
                ti++;
                float yawT = (ts.z > ts.x ? 90f : 0f) + R(-3.5f, 3.5f);
                var st = Box(tn, _background, tp, new Vector3(Mathf.Max(ts.x, ts.z), ts.y, Mathf.Min(ts.x, ts.z)), _m.concrete, LR2Layers.Default, SurfaceType.Concrete, yawT);
                st.transform.localRotation = Quaternion.Euler(R(-1.8f, 1.8f), yawT, R(-2.2f, 2.2f));
                ApplyMeshKeepBoxCollider(st, GetWarpedWallMesh(200 + ti, true, 0.28f, 0.06f));
                Object.DestroyImmediate(st.GetComponent<Collider>());
                var wing = Box(tn + "_Pent", _background, tp + new Vector3(R(-1.5f, 1.5f), ts.y * 0.28f, R(-1.5f, 1.5f)),
                    new Vector3(Mathf.Max(ts.x, ts.z) * 0.52f, ts.y * 0.28f, Mathf.Min(ts.x, ts.z) * 0.56f), _m.wallPlaster, LR2Layers.Default, SurfaceType.Concrete, yawT);
                wing.transform.localRotation = Quaternion.Euler(R(-3f, 3f), yawT + R(-8f, 8f), R(-3f, 3f));
                ApplyMeshKeepBoxCollider(wing, GetWarpedWallMesh(250 + ti, true, 0.34f, 0.07f));
                Object.DestroyImmediate(wing.GetComponent<Collider>());
            }
            Prop("SM_Crane_01", _background, new Vector3(-35f, 0f, 36f), 35f, 1f, false);
            Prop("SM_Crane_02", _background, new Vector3(36f, 0f, -35f), -45f, 1f, false);
            Prop("SM_MainBuilding4", _background, new Vector3(-44f, 0f, 0f), 90f, 1f, false);
            Prop("SM_MainBuilding6", _background, new Vector3(44f, 0f, 0f), -90f, 1f, false);
            Prop("SM_MainBuilding8", _background, new Vector3(0f, 0f, 44f), 180f, 1f, false);

            // Grungy war-torn street cover & props (every prop placed deliberately in clear road/plaza spaces)
            foreach (var (pos, yaw) in new[]
            {
                (new Vector3(-7.2f, 0f, 1.5f), 85f), (new Vector3(7.4f, 0f, -1.2f), 95f),
                (new Vector3(1.5f, 0f, 8.2f), 10f), (new Vector3(-1.5f, 0f, -8.5f), -8f),
                (new Vector3(-8.5f, 0.12f, 17f), 90f), (new Vector3(8.5f, 0.12f, -17f), 90f),
                (new Vector3(17f, 0.12f, 8.5f), 0f), (new Vector3(-17f, 0.12f, -8.5f), 0f),
            }) Prop("SM_Jersey", _props, pos, yaw);

            foreach (var (pos, yaw) in new[]
            {
                (new Vector3(-5.8f, 0f, 6.4f), 25f), (new Vector3(6.2f, 0f, 7.1f), -40f),
                (new Vector3(-6.4f, 0f, -6.2f), 115f), (new Vector3(6.0f, 0f, -6.8f), 15f),
                (new Vector3(-20f, 0f, 2f), 45f), (new Vector3(20f, 0f, -2f), 130f),
            })
            {
                Prop("SM_Barrel_01", _props, pos, yaw);
                Prop("SM_Barrel_02", _props, pos + new Vector3(0.85f, 0f, 0.25f), yaw + 35f);
            }

            foreach (var (pos, yaw) in new[]
            {
                (new Vector3(-14f, 0f, 4f), 10f), (new Vector3(14f, 0f, -4f), 80f),
                (new Vector3(3f, 0f, 16f), 0f), (new Vector3(-3f, 0f, -16f), 45f),
                (new Vector3(-16.2f, 0.14f, 19.0f), 15f), (new Vector3(16f, 0.14f, -16f), -20f),
            })
            {
                Prop("SM_Crate_01", _props, pos, yaw);
                Prop("SM_Woodpallet_1", _props, pos + new Vector3(1.5f, 0f, 0f), yaw + 25f);
            }

            Prop("SM_Gascylender", _props, new Vector3(-9.5f, 0.12f, 11.5f), 0f);
            Prop("SM_Gascylender", _props, new Vector3(9.5f, 0.12f, -11.5f), 0f);
            Prop("SM_Cablespool", _props, new Vector3(-2f, 0f, 21f), 20f);
            Prop("SM_Cablespool", _props, new Vector3(2f, 0f, -21f), -15f);
            Prop("SM_Tire", _props, new Vector3(-10f, 0f, -3f), 0f);
            Prop("SM_Tire", _props, new Vector3(10f, 0f, 3f), 40f);
            Prop("SM_Firehydrant", _props, new Vector3(8.5f, 0.12f, 8.5f), 0f);
            Prop("SM_Firehydrant", _props, new Vector3(-8.5f, 0.12f, -8.5f), 180f);
            Prop("SM_Trashcan", _props, new Vector3(-10f, 0.12f, 9f), 0f);
            Prop("SM_Trashcan_1", _props, new Vector3(10f, 0.12f, -9f), 90f);

            // Rubble mounds (non-foliage debris)
            foreach (var (name, pos, yaw) in new[]
            {
                ("SM_Rubble_01_01", new Vector3(-4.5f, 0f, 14f), 20f),
                ("SM_Rubble_02_01", new Vector3(4.5f, 0f, -14f), -50f),
                ("SM_Rubble_02_02", new Vector3(-19f, 0f, -4.5f), 75f),
                ("SM_Rubble_02_03", new Vector3(19f, 0f, 4.5f), -30f),
            })
            {
                var rp = LoadRuins(name);
                if (rp != null)
                {
                    var rgo = (GameObject)PrefabUtility.InstantiatePrefab(rp, _props);
                    rgo.transform.position = pos;
                    rgo.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                    rgo.AddComponent<SurfaceIdentifier>().surface = SurfaceType.Concrete;
                    if (rgo.GetComponentInChildren<Collider>() == null) rgo.AddComponent<BoxCollider>();
                }
            }

            // 4 Burning Barrel Fires (ambient glow, flickering light, safe particle systems)
            foreach (var fp in new[] { new Vector3(-8.5f, 0f, 4.5f), new Vector3(8.5f, 0f, -4.5f), new Vector3(4.5f, 0f, 19f), new Vector3(-4.5f, 0f, -19f) })
                SpawnBurningBarrel(_lighting, _props, fp);

            // Central intersection CTF cover ring + strategic FlagAnchors + ash/ember atmosphere
            Prop("SM_Jersey", _props, new Vector3(-4.5f, 0.04f, 0f), 90f);
            Prop("SM_Jersey", _props, new Vector3(4.5f, 0.04f, 0f), 90f);
            AddFlagAnchor(_gameplay, "Central Intersection", new Vector3(0f, 0.04f, 0f));
            AddFlagAnchor(_gameplay, "Northwest Plaza", new Vector3(-15.5f, 0.14f, 15.5f));
            AddFlagAnchor(_gameplay, "Northeast Court", new Vector3(15.5f, 0.14f, 15.5f));
            AddFlagAnchor(_gameplay, "Southwest Alley", new Vector3(-15.5f, 0.14f, -15.5f));
            AddFlagAnchor(_gameplay, "Southeast Crossing", new Vector3(15.5f, 0.14f, -15.5f));
            AddAmbientParticles(_lighting, "DeadsectorEmbers", false, new Vector3(0f, 2.2f, 0f), new Vector3(40f, 4.5f, 40f),
                new Color(1f, 0.56f, 0.18f, 0.85f), new Color(1f, 0.30f, 0.08f, 0.45f), 65, 11f, 2.4f, 4.4f, 0.028f, 0.06f, new Vector3(0.45f, 0.72f, 0.2f), -0.04f);
            AddAmbientParticles(_lighting, "DeadsectorAsh", true, new Vector3(0f, 3.0f, 0f), new Vector3(42f, 5.5f, 42f),
                new Color(0.55f, 0.55f, 0.58f, 0.18f), new Color(0.35f, 0.35f, 0.38f, 0.10f), 55, 9f, 3.8f, 6.2f, 0.35f, 0.85f, new Vector3(0.6f, -0.15f, -0.35f), 0.01f);

            // Spawns, TacticalPoints & AmmoCrates (B46: protected behind SW/NE corner buildings with doorway + alley exits, zero spawn-to-spawn LOS)
            AddSpawn(new Vector3(-25.8f, 0.15f, -15.5f), 85f, TeamId.Alpha, true, false);
            AddSpawn(new Vector3(-15.5f, 0.15f, -25.8f), 10f, TeamId.Alpha, true, false);
            AddSpawn(new Vector3(-20.0f, 0.15f, -17.5f), 45f, TeamId.Alpha, true, false);
            AddSpawn(new Vector3(-25.5f, 0.15f, -11.8f), 90f, TeamId.Alpha, true, false);
            AddSpawn(new Vector3(25.8f, 0.15f, 15.5f), -95f, TeamId.Bravo, true, false);
            AddSpawn(new Vector3(15.5f, 0.15f, 25.8f), 170f, TeamId.Bravo, true, false);
            AddSpawn(new Vector3(20.0f, 0.15f, 17.5f), -135f, TeamId.Bravo, true, false);
            AddSpawn(new Vector3(25.5f, 0.15f, 11.8f), -90f, TeamId.Bravo, true, false);
            AddSpawn(new Vector3(0f, 0.05f, 24f), 180f, TeamId.None, false, false);
            AddSpawn(new Vector3(0f, 0.05f, -24f), 0f, TeamId.None, false, false);
            AddSpawn(new Vector3(-24f, 0.05f, 0f), 90f, TeamId.None, false, false);
            AddSpawn(new Vector3(24f, 0.05f, 0f), -90f, TeamId.None, false, false);

            AddTac(new Vector3(-17.5f, 0.18f, 17f), TacticalPointType.Patrol, 0.7f, false);
            AddTac(new Vector3(17.5f, 0.18f, 17f), TacticalPointType.Patrol, 0.7f, false);
            AddTac(new Vector3(-17.5f, 0.18f, -17f), TacticalPointType.Patrol, 0.7f, false);
            AddTac(new Vector3(17.5f, 0.18f, -17f), TacticalPointType.Patrol, 0.7f, false);
            AddTac(new Vector3(0f, 0.05f, 0f), TacticalPointType.Choke, 0.85f, false);
            AddTac(new Vector3(-8f, 0.05f, 0f), TacticalPointType.Choke, 0.65f, false);
            AddTac(new Vector3(8f, 0.05f, 0f), TacticalPointType.Choke, 0.65f, false);

            AddAmmoCrate(new Vector3(-5.5f, 0.01f, -2.5f));
            AddAmmoCrate(new Vector3(5.5f, 0.01f, 2.5f));

            // Moody overcast dusk HDRP lighting + streetlamps + alleyway/visibility fill + weather rain
            foreach (var (sx, sz) in new[] { (-8f, 8f), (8f, 8f), (-8f, -8f), (8f, -8f) })
                AddLight(_lighting, $"StreetLamp_{(sx < 0 ? "W" : "E")}_{(sz < 0 ? "S" : "N")}",
                    new Vector3(sx, 4.5f, sz), Quaternion.identity, LightType.Point, 4800f, 5600f, 22f, 0f, false);
            foreach (float oz in new[] { -14f, 14f })
                AddLight(_lighting, $"Overhead_{(oz < 0 ? "S" : "N")}",
                    new Vector3(0f, 5.5f, oz), Quaternion.identity, LightType.Point, 4700f, 5200f, 22f, 0f, false);
            // Cross-intersection rim fills separate character silhouettes from sandstone walls at Central Intersection (#0) without extra shadow casters.
            AddLight(_lighting, "CentralCrossRimW", new Vector3(-4.2f, 3.6f, 0f), Quaternion.identity, LightType.Point, 3400f, 3800f, 13f, 0f, false);
            AddLight(_lighting, "CentralCrossRimE", new Vector3(4.2f, 3.6f, 0f), Quaternion.identity, LightType.Point, 6200f, 2800f, 13f, 0f, false);
            // B92: shadowless localized practical accent lights
            AddLight(_lighting, "Accent_PharmacyDoorNE", new Vector3(12.2f, 3.4f, 11.2f), Quaternion.identity, LightType.Point, 4200f, 3200f, 15f, 0f, false);
            AddLight(_lighting, "Accent_ApartmentAlleySW", new Vector3(-25.2f, 3.6f, -15.5f), Quaternion.identity, LightType.Point, 5600f, 3000f, 15f, 0f, false);
            BuildCustomLighting("LR2_Deadsector_Profile", sunElev: 25f, sunAz: 225f, sunLux: 20000f, sunTemp: 4300f,
                fogMfp: 130f, fogColor: new Color(0.46f, 0.50f, 0.54f), exposureComp: 0.28f, contrast: 14f, saturation: -14f,
                rainDefaultPreset: 2, evMin: 2.6f, evMax: 12.2f,
                hdriAssetPath: "Assets/LeartesStudios/Art/Textures/T_HDRI_01.HDR", skyExposure: 11.6f,
                shadowsTint: new Vector4(0.94f, 0.98f, 1.04f, 0.015f), highlightsTint: new Vector4(1.05f, 0.99f, 0.92f, 0.02f));
            PlaceEnvironmentRealism(new Color(0.58f, 0.62f, 0.65f, 1f), new Color(0.56f, 0.44f, 0.34f, 1f), 0.16f, 20f);
            PlaceMapDecals(pMin, pMax, 140);
            PlaceTargetedWearDecals();
            AddMatchSystems(40f);
            MarkStatic(env);

            EliminateSceneClipping("LR2_Deadsector", _structures, _props, null, _spawns, _gameplay);
            SetupDestructibleProps(root);

            Physics.SyncTransforms();
            BakeNavMesh(root, "Assets/LR2/Scenes/LR2_Deadsector_NavMesh.asset", false);
            GenerateCoverPoints(pMin, pMax, new[] { 0.4f });
            BuildMinimap("Assets/LR2/UI/Minimap/Deadsector_Minimap.png", pMin, pMax);

            EditorSceneManager.SaveScene(scene, DeadsectorScenePath);
            SetBuildScenes();
            Debug.Log("[LR2] Map built: " + DeadsectorScenePath);
        }

        // =================================================================================================
        // Map 3: Foundry — Multi-level industrial substation & sunken pipe trench (no foliage)
        // =================================================================================================
        public static void BuildFoundry()
        {
            var root = BeginExtraMap("Foundry", "Assets/LR2/Data/Map/FoundryConfig.asset", 7042, out var scene);
            var env = root.transform.Find("Environment");
            Vector2 pMin = new Vector2(-28f, -28f), pMax = new Vector2(28f, 28f);

            // Ground slabs surrounding Central Sunken Pipe Trench (x [-5.5, 5.5], z [-14, 14], floor at y = -1.5)
            var g = Child(_structures, "Ground");
            Box("FloorN", g, new Vector3(0f, -0.25f, 22f), new Vector3(60f, 0.5f, 16f), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
            Box("FloorS", g, new Vector3(0f, -0.25f, -22f), new Vector3(60f, 0.5f, 16f), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
            Box("FloorW", g, new Vector3(-18f, -0.25f, 0f), new Vector3(25f, 0.5f, 28f), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
            Box("FloorE", g, new Vector3(18f, -0.25f, 0f), new Vector3(25f, 0.5f, 28f), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
            Box("TrenchFloor", g, new Vector3(0f, -1.75f, 0f), new Vector3(12f, 0.5f, 30f), _m.asphalt, LR2Layers.Default, SurfaceType.Concrete);

            // Retaining walls around trench (tucked flush against slab edges, non-overlapping corners, no top Z-fighting)
            void Retain(string n, float cx, float cz, float sx, float sz) =>
                Box(n, g, new Vector3(cx, -0.752f, cz), new Vector3(sx, 1.496f, sz), _m.wallPlaster, LR2Layers.Default, SurfaceType.Concrete);
            Retain("RetW_N", -5.325f, 7.9f, 0.35f, 12.1f);
            Retain("RetW_S", -5.325f, -7.9f, 0.35f, 12.1f);
            Retain("RetE_N", 5.325f, 7.9f, 0.35f, 12.1f);
            Retain("RetE_S", 5.325f, -7.9f, 0.35f, 12.1f);
            Retain("RetN_L", -3.72f, 13.825f, 2.84f, 0.35f);
            Retain("RetN_R", 3.72f, 13.825f, 2.84f, 0.35f);
            Retain("RetS_L", -3.72f, -13.825f, 2.84f, 0.35f);
            Retain("RetS_R", 3.72f, -13.825f, 2.84f, 0.35f);

            // 4 Graded walk-down ramps into the trench (meeting slab edges cleanly)
            Stairs("RampTrenchN", _structures, new Vector3(0f, -1.5f, 9.5f), new Vector3(0f, 0f, 14.0f), 4.6f, _m.steelGrate, false);
            Stairs("RampTrenchS", _structures, new Vector3(0f, -1.5f, -9.5f), new Vector3(0f, 0f, -14.0f), 4.6f, _m.steelGrate, false);
            Stairs("RampTrenchW", _structures, new Vector3(-2.5f, -1.5f, 0f), new Vector3(-5.5f, 0f, 0f), 3.6f, _m.steelGrate, false);
            Stairs("RampTrenchE", _structures, new Vector3(2.5f, -1.5f, 0f), new Vector3(5.5f, 0f, 0f), 3.6f, _m.steelGrate, false);

            // Overhead pipe bridges spanning trench with grounded steel support stanchions on both sides
            foreach (float bz in new[] { 7.5f, -7.5f })
            {
                string tag = bz > 0f ? "N" : "S";
                Box("PipeBridge" + tag, _structures, new Vector3(0f, 3.1f, bz), new Vector3(13f, 0.45f, 1.4f), _m.rust, LR2Layers.Default, SurfaceType.Metal);
                Box("PipeBridgeSupportW_" + tag, _structures, new Vector3(-6.2f, 1.44f, bz), new Vector3(0.42f, 2.88f, 0.55f), _m.darkMetal, LR2Layers.Default, SurfaceType.Metal);
                Box("PipeBridgeSupportE_" + tag, _structures, new Vector3(6.2f, 1.44f, bz), new Vector3(0.42f, 2.88f, 0.55f), _m.darkMetal, LR2Layers.Default, SurfaceType.Metal);
            }

            // North Pump Hall & South Turbine Shed (grounded cleanly on y = 0 slabs, wide bay doors, interior machinery cover)
            void IndustrialHall(string name, float zCenter)
            {
                var h = Child(_structures, name);
                float z0 = zCenter - 4.5f, z1 = zCenter + 4.5f;
                float x0 = -13f, x1 = 13f, hgt = 6.2f, t = 0.3f;
                WallWithFloor("WallN", h, new Vector3(x0, 0f, z1), new Vector3(x1, 0f, z1), hgt, t, _m.corrugated, 0f, new Vector4(6f, 3.2f, 0f, 3.4f), new Vector4(20f, 3.2f, 0f, 3.4f));
                WallWithFloor("WallS", h, new Vector3(x0, 0f, z0), new Vector3(x1, 0f, z0), hgt, t, _m.corrugated, 0f, new Vector4(6f, 3.2f, 0f, 3.4f), new Vector4(20f, 3.2f, 0f, 3.4f));
                WallWithFloor("WallW", h, new Vector3(x0, 0f, z0), new Vector3(x0, 0f, z1), hgt, t, _m.corrugated, 0f, new Vector4(4.5f, 2.8f, 0f, 3.2f));
                WallWithFloor("WallE", h, new Vector3(x1, 0f, z0), new Vector3(x1, 0f, z1), hgt, t, _m.corrugated, 0f, new Vector4(4.5f, 2.8f, 0f, 3.2f));
                Box("Roof", h, new Vector3(0f, hgt + 0.1f, zCenter), new Vector3(26.6f, 0.2f, 9.6f), _m.roof, LR2Layers.Default, SurfaceType.Metal);
                // Interior generators + destructible consoles (B75 pack meshes)
                FittedPackProp("GeneratorW", "SM_PipeMadule3", h, new Vector3(-6.5f, 1.0f, zCenter), new Vector3(3.4f, 2.0f, 1.8f), 0f, SurfaceType.Metal);
                FittedPackProp("GeneratorE", "SM_PipeMadule3", h, new Vector3(6.5f, 1.0f, zCenter), new Vector3(3.4f, 2.0f, 1.8f), 180f, SurfaceType.Metal);
                FittedPackProp("ControlConsole", "SM_ConcreteColum2", h, new Vector3(0f, 0.6f, zCenter), new Vector3(2.4f, 1.2f, 0.9f), 0f, SurfaceType.Metal);
                AddLight(_lighting, "HallLamp_" + name, new Vector3(0f, 5.2f, zCenter), Quaternion.identity, LightType.Point, 4200f, 4800f, 20f, 0f, false);
                AddLight(_lighting, "HallBayW_" + name, new Vector3(-6.5f, 3.6f, zCenter), Quaternion.identity, LightType.Point, 5000f, 2800f, 16f, 0f, false);
                AddLight(_lighting, "HallBayE_" + name, new Vector3(6.5f, 3.6f, zCenter), Quaternion.identity, LightType.Point, 5000f, 2800f, 16f, 0f, false);
            }
            IndustrialHall("NorthPumpHall", 20.5f);
            IndustrialHall("SouthTurbineShed", -20.5f);

            // East Elevated Catwalk Network (y = 3.0) overlooking central trench
            var cat = Child(_structures, "EastCatwalk");
            Box("CatDeck", cat, new Vector3(17.5f, 2.95f, 0f), new Vector3(3.6f, 0.14f, 20f), _m.steelGrate, LR2Layers.Default, SurfaceType.Metal);
            Box("CatDeckOut", cat, new Vector3(21.5f, 2.95f, 0f), new Vector3(4.4f, 0.14f, 8f), _m.steelGrate, LR2Layers.Default, SurfaceType.Metal);
            for (int i = -1; i <= 1; i++) Box("CatPillar", cat, new Vector3(17.5f, 1.45f, i * 8f), new Vector3(0.28f, 2.9f, 0.28f), _m.darkMetal, LR2Layers.Default, SurfaceType.Metal);
            Stairs("CatStairsN", cat, new Vector3(17.5f, 0f, 14.5f), new Vector3(17.5f, 3.02f, 10.0f), 2.2f, _m.steelGrate);
            Stairs("CatStairsS", cat, new Vector3(17.5f, 0f, -14.5f), new Vector3(17.5f, 3.02f, -10.0f), 2.2f, _m.steelGrate);
            Railing(cat, new Vector3(15.75f, 3.02f, -10f), new Vector3(15.75f, 3.02f, -2.5f));
            Railing(cat, new Vector3(15.75f, 3.02f, 2.5f), new Vector3(15.75f, 3.02f, 10f));
            PropOrBox("SM_Crate_01", "CatCoverCrate", cat, new Vector3(16.6f, 3.02f, 0f), 0f, 1.05f, new Vector3(0.9f, 0.9f, 1.8f), _m.wood, SurfaceType.Wood);
            Prop("SM_BigPipe", cat, new Vector3(19.5f, 0f, 7f), 90f, 1f, false);
            Prop("SM_PipeMadule1", _structures, new Vector3(0f, 3.35f, 7.5f), 0f, 1f, false);
            Prop("SM_PipeMadule2", _structures, new Vector3(0f, 3.35f, -7.5f), 180f, 1f, false);
            Prop("SM_fireextinguisher", _props, new Vector3(15.2f, 0f, 12.8f), 0f, 1f, true, SurfaceType.Plastic);
            Prop("SM_Conebarrier", _props, new Vector3(14.5f, 0f, -12.5f), 25f, 1f, true, SurfaceType.Plastic);
            // B46: upper catwalk sightline occluder + safe stair landing pockets
            Box("CatwalkOccluderS", cat, new Vector3(15.75f, 3.85f, -5.8f), new Vector3(0.18f, 1.5f, 3.8f), _m.corrugated, LR2Layers.Default, SurfaceType.Metal);
            FittedPackProp("StairPocketN", "SM_Jersey", cat, new Vector3(15.8f, 0.55f, 15.2f), new Vector3(1.8f, 1.1f, 0.6f), 0f, SurfaceType.Concrete);
            FittedPackProp("StairPocketS", "SM_Jersey", cat, new Vector3(15.8f, 0.55f, -15.2f), new Vector3(1.8f, 1.1f, 0.6f), 0f, SurfaceType.Concrete);

            // West Container Maze
            var wYard = Child(_structures, "WestContainers");
            Container(wYard, "ContW1", new Vector3(-16f, 0f, 6f), 0f, 1);
            Container(wYard, "ContW2", new Vector3(-16f, 0f, -6f), 12f, 3);
            Container(wYard, "ContW3", new Vector3(-23f, 0f, 0f), 90f, 0);
            Container(wYard, "ContW2_Top", new Vector3(-16f, 2.59f, 6f), 0f, 2);

            // Boundary (warped/scarred industrial perimeter + steel buttresses) + Skyline Silos/Cranes
            var bnd = Child(_structures, "Boundary");
            void FoundryBoundary(string n, Vector3 center, float len, float yaw, int seed)
            {
                var bw = Box(n, bnd, center, new Vector3(len, 5.2f, 0.55f), _m.corrugated, LR2Layers.Default, SurfaceType.Metal, yaw);
                ApplyMeshKeepBoxCollider(bw, GetWarpedWallMesh(seed, true, 0.14f, 0.042f));
                AddWarTornWallDetails(bnd, center, len, 4.9f, yaw, _m.concrete, seed, false, true);
            }
            FoundryBoundary("WallN", new Vector3(0f, 2.55f, 28.5f), 57.5f, 0f, 301);
            FoundryBoundary("WallS", new Vector3(0f, 2.55f, -28.5f), 57.5f, 0f, 302);
            FoundryBoundary("WallW", new Vector3(-28.5f, 2.55f, 0f), 56.5f, 90f, 303);
            FoundryBoundary("WallE", new Vector3(28.5f, 2.55f, 0f), 56.5f, 90f, 304);
            AddPlayerClipBox(pMin, pMax, 11f);

            Prop("SM_Silo", _background, new Vector3(-37f, 0f, 15f), 0f, 1.2f, false);
            Prop("SM_Slio1", _background, new Vector3(-37f, 0f, -15f), 45f, 1.2f, false);
            Prop("SM_Slio2", _background, new Vector3(37f, 0f, 14f), 20f, 1.2f, false);
            Prop("SM_CylenderBuilding", _background, new Vector3(37f, 0f, -14f), 0f, 1.1f, false);
            Prop("SM_Crane_01", _background, new Vector3(-20f, 0f, 36f), 90f, 1f, false);
            Prop("SM_Crane_02", _background, new Vector3(20f, 0f, -36f), -90f, 1f, false);

            // Destructible industrial cover & barrels (placed in trench, courtyard, catwalks)
            Prop("SM_Jersey", _props, new Vector3(-8.5f, 0f, 4f), 90f);
            Prop("SM_Jersey", _props, new Vector3(-8.5f, 0f, -4f), 90f);
            Prop("SM_Jersey", _props, new Vector3(8.5f, 0f, 4f), 90f);
            Prop("SM_Jersey", _props, new Vector3(8.5f, 0f, -4f), 90f);
            Prop("SM_Jersey", _props, new Vector3(0f, -1.5f, 4.5f), 0f);
            Prop("SM_Jersey", _props, new Vector3(0f, -1.5f, -4.5f), 0f);
            Prop("SM_Barrel_01", _props, new Vector3(-2.5f, -1.5f, 2.5f), 0f);
            Prop("SM_Barrel_01", _props, new Vector3(2.5f, -1.5f, -2.5f), 45f);
            Prop("SM_Gascylender", _props, new Vector3(11.5f, 0f, 9f), 0f);
            Prop("SM_Gascylender", _props, new Vector3(-11.5f, 0f, -9f), 0f);
            Prop("SM_Crate_01", _props, new Vector3(-12f, 0f, 0f), 15f);
            Prop("SM_Woodpallet_1", _props, new Vector3(12f, 0f, 0f), -15f);
            Prop("SM_Cablespool", _props, new Vector3(21.5f, 3.02f, 0f), 0f);
            Prop("SM_Barrel_02", _props, new Vector3(-20f, 0f, 12f), 20f);
            Prop("SM_Barrel_04", _props, new Vector3(20f, 0f, -12f), -30f);

            // Spawns, Tactical points & Ammo crates (B46: inside solid central shelter of South/North halls, 0/16 spawn-to-spawn LOS)
            AddSpawn(new Vector3(-3.8f, 0.05f, -20.5f), 0f, TeamId.Alpha, true, false);
            AddSpawn(new Vector3(-1.3f, 0.05f, -22.5f), 0f, TeamId.Alpha, true, false);
            AddSpawn(new Vector3(1.3f, 0.05f, -22.5f), 0f, TeamId.Alpha, true, false);
            AddSpawn(new Vector3(3.8f, 0.05f, -20.5f), 0f, TeamId.Alpha, true, false);
            AddSpawn(new Vector3(-3.8f, 0.05f, 20.5f), 180f, TeamId.Bravo, true, false);
            AddSpawn(new Vector3(-1.3f, 0.05f, 22.5f), 180f, TeamId.Bravo, true, false);
            AddSpawn(new Vector3(1.3f, 0.05f, 22.5f), 180f, TeamId.Bravo, true, false);
            AddSpawn(new Vector3(3.8f, 0.05f, 20.5f), 180f, TeamId.Bravo, true, false);
            AddSpawn(new Vector3(0f, 0.05f, 18f), 180f, TeamId.None, false, false);
            AddSpawn(new Vector3(0f, 0.05f, -18f), 0f, TeamId.None, false, false);
            AddSpawn(new Vector3(19f, 3.08f, 0f), -90f, TeamId.None, false, false);
            AddSpawn(new Vector3(-16f, 0.05f, 0f), 90f, TeamId.None, false, false);

            AddTac(new Vector3(17.5f, 3.08f, 0f), TacticalPointType.Overlook, 0.9f, false);
            AddTac(new Vector3(0f, -1.45f, 0f), TacticalPointType.Choke, 0.8f, false);
            AddTac(new Vector3(0f, 0.05f, 18f), TacticalPointType.Patrol, 0.7f, false);
            AddTac(new Vector3(0f, 0.05f, -18f), TacticalPointType.Patrol, 0.7f, false);
            AddTac(new Vector3(-16f, 0.05f, 0f), TacticalPointType.Patrol, 0.65f, false);

            AddAmmoCrate(new Vector3(-10.5f, 0.01f, -5.5f));
            AddAmmoCrate(new Vector3(10.5f, 0.01f, 5.5f));

            AddFlagAnchor(_gameplay, "Sunken Pipe Trench", new Vector3(0f, -1.48f, 0f));
            AddFlagAnchor(_gameplay, "North Refinery Bay", new Vector3(0f, 0.04f, 17.5f));
            AddFlagAnchor(_gameplay, "South Refinery Bay", new Vector3(0f, 0.04f, -17.5f));
            AddFlagAnchor(_gameplay, "West Turbine Aisle", new Vector3(-16.0f, 0.04f, 0f));
            AddFlagAnchor(_gameplay, "East Catwalk Overlook", new Vector3(18.0f, 3.04f, 0f));
            AddAmbientParticles(_lighting, "FoundryTrenchSteam", true, new Vector3(0f, 0.6f, 0f), new Vector3(6f, 1.5f, 16f),
                new Color(0.75f, 0.78f, 0.80f, 0.22f), new Color(0.58f, 0.62f, 0.66f, 0.10f), 45, 8f, 2.5f, 4.5f, 0.75f, 1.55f, new Vector3(0.15f, 0.95f, 0.1f), -0.05f);
            AddAmbientParticles(_lighting, "FoundrySparks", false, new Vector3(0f, 2.5f, 0f), new Vector3(36f, 5f, 36f),
                new Color(1f, 0.68f, 0.24f, 0.85f), new Color(1f, 0.42f, 0.12f, 0.45f), 55, 10f, 1.8f, 3.6f, 0.022f, 0.052f, new Vector3(0.35f, 0.4f, -0.35f), 0.08f);

            foreach (var (fx, fz) in new[] { (-18f, 18f), (18f, 18f), (-18f, -18f), (18f, -18f) })
                AddLight(_lighting, $"YardFlood_{(fx < 0 ? "W" : "E")}_{(fz < 0 ? "S" : "N")}",
                    new Vector3(fx, 5.5f, fz), Quaternion.identity, LightType.Point, 4600f, 5000f, 21f, 0f, false);
            foreach (var fbp in new[] { new Vector3(-8.5f, 0f, 11f), new Vector3(8.5f, 0f, -11f), new Vector3(-13.5f, 0f, -5.5f), new Vector3(11.5f, 0f, 6.5f) })
                SpawnBurningBarrel(_lighting, _props, fbp);
            AddLight(_lighting, "TrenchFillN", new Vector3(0f, 1.8f, 6.5f), Quaternion.identity, LightType.Point, 5200f, 2600f, 14f, 0f, false);
            AddLight(_lighting, "TrenchFillS", new Vector3(0f, 1.8f, -6.5f), Quaternion.identity, LightType.Point, 5200f, 2600f, 14f, 0f, false);
            AddLight(_lighting, "ContAlleyFillW", new Vector3(-19.5f, 3.8f, 0f), Quaternion.identity, LightType.Point, 5200f, 2600f, 14f, 0f, false);
            // B92: shadowless localized practical accent lights
            AddLight(_lighting, "Accent_CatwalkMid", new Vector3(18.5f, 5.2f, 0f), Quaternion.identity, LightType.Point, 4600f, 3200f, 16f, 0f, false);
            AddLight(_lighting, "Accent_WestYardBounce", new Vector3(-14.5f, 3.6f, 9.5f), Quaternion.identity, LightType.Point, 5400f, 3000f, 15f, 0f, false);
            BuildCustomLighting("LR2_Foundry_Profile", sunElev: 34f, sunAz: 245f, sunLux: 24000f, sunTemp: 4300f,
                fogMfp: 140f, fogColor: new Color(0.46f, 0.49f, 0.50f), exposureComp: 0.25f, contrast: 16f, saturation: -6f,
                rainDefaultPreset: 0, evMin: 2.6f, evMax: 12.2f,
                hdriAssetPath: "Assets/LeartesStudios/AbandonedFactory/HDRP/Art/Textures/T_HdriSky.hdr", skyExposure: 11.6f,
                shadowsTint: new Vector4(0.95f, 0.99f, 1.03f, 0.015f), highlightsTint: new Vector4(1.05f, 1.00f, 0.93f, 0.02f));
            PlaceEnvironmentRealism(new Color(0.64f, 0.68f, 0.70f, 1f), new Color(0.65f, 0.52f, 0.35f, 1f), 0.14f, 18f);
            PlaceMapDecals(pMin, pMax, 130);
            PlaceTargetedWearDecals();
            AddMatchSystems(38f);
            MarkStatic(env);

            EliminateSceneClipping("LR2_Foundry", _structures, _props, null, _spawns, _gameplay);
            SetupDestructibleProps(root);

            Physics.SyncTransforms();
            BakeNavMesh(root, "Assets/LR2/Scenes/LR2_Foundry_NavMesh.asset", false);
            GenerateCoverPoints(pMin, pMax, new[] { -1.2f, 0.35f, 3.3f });
            BuildMinimap("Assets/LR2/UI/Minimap/Foundry_Minimap.png", pMin, pMax);

            EditorSceneManager.SaveScene(scene, FoundryScenePath);
            SetBuildScenes();
            Debug.Log("[LR2] Map built: " + FoundryScenePath);
        }

        // =================================================================================================
        // Map 4: Test Range — Super-simple, crisp, visually sharp tactical & LLM bot testing arena
        // =================================================================================================
        public static void BuildTestRange()
        {
            var root = BeginExtraMap("TestRange", "Assets/LR2/Data/Map/TestRangeConfig.asset", 1001, out var scene);
            var env = root.transform.Find("Environment");
            Vector2 pMin = new Vector2(-30f, -30f), pMax = new Vector2(30f, 30f);

            // Clean flat court + center lane asphalt + safety range stripes (zero Z-fighting)
            var g = Child(_structures, "Ground");
            Box("CourtWest", g, new Vector3(-20f, -0.25f, 0f), new Vector3(20f, 0.5f, 64f), _m.wallPlaster, LR2Layers.Default, SurfaceType.Concrete);
            Box("CourtCenter", g, new Vector3(0f, -0.25f, 0f), new Vector3(20f, 0.5f, 64f), _m.wallPlaster, LR2Layers.Default, SurfaceType.Concrete);
            Box("CourtEast", g, new Vector3(20f, -0.25f, 0f), new Vector3(20f, 0.5f, 64f), _m.wallPlaster, LR2Layers.Default, SurfaceType.Concrete);

            // Clean yardage markings across the range floor (-20, -10, 0, +10, +20 m)
            for (int z = -20; z <= 20; z += 10)
            {
                var line = Box($"RangeLine_{z}", g, new Vector3(0f, 0.008f, z), new Vector3(54f, 0.008f, z == 0 ? 0.22f : 0.12f), _m.concrete);
                Object.DestroyImmediate(line.GetComponent<Collider>());
            }

            // Perimeter walls (flush corners, no Z-fighting) + corner architectural towers for clean skyline framing
            var bnd = Child(_structures, "Boundary");
            Box("WallN", bnd, new Vector3(0f, 2.25f, 30.25f), new Vector3(61f, 4.5f, 0.5f), _m.wallPlaster, LR2Layers.Default, SurfaceType.Concrete);
            Box("WallS", bnd, new Vector3(0f, 2.25f, -30.25f), new Vector3(61f, 4.5f, 0.5f), _m.wallPlaster, LR2Layers.Default, SurfaceType.Concrete);
            Box("WallW", bnd, new Vector3(-30.25f, 2.25f, 0f), new Vector3(0.5f, 4.5f, 60f), _m.wallPlaster, LR2Layers.Default, SurfaceType.Concrete);
            Box("WallE", bnd, new Vector3(30.25f, 2.25f, 0f), new Vector3(0.5f, 4.5f, 60f), _m.wallPlaster, LR2Layers.Default, SurfaceType.Concrete);
            foreach (var (tx, tz, pn, yaw) in new[] {
                (-33f, 33f, "SM_Silo", 0f), (33f, 33f, "SM_Slio1", 90f),
                (-33f, -33f, "SM_CylenderBuilding", 45f), (33f, -33f, "SM_MainBuilding2", -90f)
            })
            {
                if (Prop(pn, _background, new Vector3(tx, 0f, tz), yaw, 1.15f, false) == null)
                    Box("CornerTower", _background, new Vector3(tx, 5f, tz), new Vector3(5f, 10f, 5f), _m.corrugated, LR2Layers.Default, SurfaceType.Metal);
            }
            Prop("SM_Crane_01", _background, new Vector3(-24f, 0f, 38f), 85f, 1f, false);
            Prop("SM_Crane_02", _background, new Vector3(24f, 0f, -38f), -95f, 1f, false);
            AddPlayerClipBox(pMin, pMax, 10f);

            // ---- LANE 1 (Center): Symmetrical low Jersey/spool crouch cover + tall concrete pillars
            var center = Child(_structures, "CenterLane");
            int tb = 0;
            foreach (var (x, z, w, h, d) in new[]
            {
                (0f, 0f, 3.2f, 1.08f, 0.85f),
                (-5.5f, 6.5f, 2.4f, 1.08f, 0.85f), (5.5f, -6.5f, 2.4f, 1.08f, 0.85f),
                (-5.5f, -6.5f, 2.4f, 1.08f, 0.85f), (5.5f, 6.5f, 2.4f, 1.08f, 0.85f),
                (-4.5f, 15.5f, 2.8f, 2.3f, 0.95f), (4.5f, -15.5f, 2.8f, 2.3f, 0.95f),
                (4.5f, 15.5f, 2.8f, 2.3f, 0.95f), (-4.5f, -15.5f, 2.8f, 2.3f, 0.95f),
                // B46: symmetric cover gates at +/-8.2m + spawn exit flank shields (+/-18.5m) keeping x=0 clean
                (-2.8f, 8.2f, 2.2f, 1.08f, 0.75f), (2.8f, -8.2f, 2.2f, 1.08f, 0.75f),
                (-2.4f, -18.5f, 2.4f, 1.95f, 0.75f), (2.4f, 18.5f, 2.4f, 1.95f, 0.75f),
                (-14.5f, -19.8f, 3.2f, 2.15f, 0.85f), (-14.5f, 19.8f, 3.2f, 2.15f, 0.85f),
                (14.5f, -19.8f, 3.2f, 2.15f, 0.85f), (14.5f, 19.8f, 3.2f, 2.15f, 0.85f),
            })
            {
                tb++;
                bool tall = h > 1.5f;
                string pref = tall ? "SM_ConcreteColumn" : (tb % 3 == 0 ? "SM_Cablespool" : "SM_Jersey");
                float yawCov = tall ? 0f : (Mathf.Abs(x) < 3.5f ? 0f : (x < 0f ? 22f : -22f));
                if (Prop(pref, _props, new Vector3(x, 0f, z), yawCov, tall ? 0.85f : 1f, true, SurfaceType.Concrete) == null)
                {
                    var blk = Box("TacticalBlock", center, new Vector3(x, h * 0.5f, z), new Vector3(w, h, d), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
                    ApplyMeshKeepBoxCollider(blk, GetWarpedWallMesh(400 + tb, false, 0.08f, 0.024f));
                }
            }

            // ---- LANE 2 (West): 1.6m elevated overwatch platform + ramps + pack crate mantle step
            var west = Child(_structures, "WestElevationLane");
            Box("OverwatchDeck", west, new Vector3(-18f, 1.52f, 0f), new Vector3(6f, 0.16f, 12f), _m.steelGrate, LR2Layers.Default, SurfaceType.Metal);
            Box("DeckSupport", west, new Vector3(-18f, 0.72f, 0f), new Vector3(5.4f, 1.44f, 11.4f), _m.wallPlaster, LR2Layers.Default, SurfaceType.Concrete);
            Stairs("WestRampN", west, new Vector3(-18f, 0f, 10.5f), new Vector3(-18f, 1.6f, 6f), 2.8f, _m.steelGrate, false);
            Stairs("WestRampS", west, new Vector3(-18f, 0f, -10.5f), new Vector3(-18f, 1.6f, -6f), 2.8f, _m.steelGrate, false);
            Box("DeckParapet", west, new Vector3(-15.2f, 2.05f, 0f), new Vector3(0.45f, 0.9f, 5.2f), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
            FittedPackProp("MantleTestBox", "SM_Crate_01", west, new Vector3(-12.5f, 0.425f, 0f), new Vector3(1.6f, 0.85f, 2.4f), 0f, SurfaceType.Wood);
            Prop("SM_Woodpallet_2", _props, new Vector3(-12.5f, 0f, 1.9f), 15f, 1f, true, SurfaceType.Wood);
            Prop("SM_BigPipe", _props, new Vector3(-26.5f, 0f, -8f), 90f, 1f, false);
            Prop("SM_BigPipe", _props, new Vector3(-26.5f, 0f, 8f), 90f, 1f, false);
            Prop("SM_Conebarrier", _props, new Vector3(-14.2f, 0f, 10.5f), 0f, 1f, true, SurfaceType.Plastic);
            Prop("SM_Conebarrier", _props, new Vector3(-14.2f, 0f, -10.5f), 0f, 1f, true, SurfaceType.Plastic);
            Prop("SM_fireextinguisher", _props, new Vector3(-15.2f, 1.6f, -2.5f), 0f, 1f, false);
            Prop("SM_Tire", _props, new Vector3(23f, 0f, -8f), 20f, 1f, true, SurfaceType.Plastic);
            Prop("SM_Tire", _props, new Vector3(23f, 0f, 8f), -25f, 1f, true, SurfaceType.Plastic);
            foreach (float fz in new[] { -22f, -10f, 10f, 22f })
            {
                Prop("SM_Grass1", _props, new Vector3(-28.4f, 0f, fz), R(0f, 360f), 1.2f, false);
                Prop("SM_Grass2", _props, new Vector3(28.4f, 0f, fz + 1.5f), R(0f, 360f), 1.2f, false);
            }

            // ---- LANE 3 (East): Destructible cover, breakable glass & explosive barrel test lane
            void FramedTestGlass(string gName, string frameTag, float gz)
            {
                Box(gName, _structures, new Vector3(16f, 1.15f, gz), new Vector3(3.2f, 2.2f, 0.06f), _m.glass, LR2Layers.Default, SurfaceType.Glass);
                Box("PaneFrame" + frameTag + "_Sill", _structures, new Vector3(16f, 0.05f, gz), new Vector3(3.36f, 0.1f, 0.14f), _m.darkMetal, LR2Layers.Default, SurfaceType.Metal);
                Box("PaneFrame" + frameTag + "_PostL", _structures, new Vector3(14.34f, 1.15f, gz), new Vector3(0.12f, 2.3f, 0.14f), _m.darkMetal, LR2Layers.Default, SurfaceType.Metal);
                Box("PaneFrame" + frameTag + "_PostR", _structures, new Vector3(17.66f, 1.15f, gz), new Vector3(0.12f, 2.3f, 0.14f), _m.darkMetal, LR2Layers.Default, SurfaceType.Metal);
            }
            FramedTestGlass("TestGlassPanelA", "A", 4f);
            FramedTestGlass("TestGlassPanelB", "B", -4f);
            Prop("SM_Jersey", _props, new Vector3(15.5f, 0f, 11f), 0f);
            Prop("SM_Jersey", _props, new Vector3(15.5f, 0f, -11f), 0f);
            Prop("SM_Crate_01", _props, new Vector3(18.5f, 0f, 7.5f), 0f);
            Prop("SM_Crate_02", _props, new Vector3(18.5f, 0f, -7.5f), 0f);
            Prop("SM_Barrel_01", _props, new Vector3(14f, 0f, 0f), 0f);
            Prop("SM_Gascylender", _props, new Vector3(18f, 0f, 0f), 0f);
            Prop("SM_Barrel_02", _props, new Vector3(22f, 0f, 0f), 0f);

            // B46: ballistic range spawn blast-shields at +/-21.5m blocking all spawn-to-spawn rays while keeping 4.2m walkways open
            var rangeBaffles = Child(_structures, "SpawnBaffles");
            foreach (float sbz in new[] { -21.5f, 21.5f })
            {
                string side = sbz < 0f ? "S" : "N";
                Box($"SpawnBaffle_{side}_Center", rangeBaffles, new Vector3(0f, 1.2f, sbz), new Vector3(9.4f, 2.4f, 0.5f), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
                Box($"SpawnBaffle_{side}_West", rangeBaffles, new Vector3(-14.2f, 1.2f, sbz), new Vector3(10.6f, 2.4f, 0.5f), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
                Box($"SpawnBaffle_{side}_East", rangeBaffles, new Vector3(14.2f, 1.2f, sbz), new Vector3(10.6f, 2.4f, 0.5f), _m.concrete, LR2Layers.Default, SurfaceType.Concrete);
            }

            // Deterministic Spawns, TacticalPoints & AmmoCrates
            AddSpawn(new Vector3(0f, 0.05f, -25.5f), 0f, TeamId.Alpha, true, false);
            AddSpawn(new Vector3(-16f, 0.05f, -24.5f), 0f, TeamId.Alpha, true, false);
            AddSpawn(new Vector3(16f, 0.05f, -24.5f), 0f, TeamId.Alpha, true, false);
            AddSpawn(new Vector3(0f, 0.05f, 25.5f), 180f, TeamId.Bravo, true, false);
            AddSpawn(new Vector3(-16f, 0.05f, 24.5f), 180f, TeamId.Bravo, true, false);
            AddSpawn(new Vector3(16f, 0.05f, 24.5f), 180f, TeamId.Bravo, true, false);
            AddSpawn(new Vector3(-24f, 0.05f, 0f), 90f, TeamId.None, false, false);
            AddSpawn(new Vector3(24f, 0.05f, 0f), -90f, TeamId.None, false, false);

            AddTac(new Vector3(-18f, 1.65f, 0f), TacticalPointType.Overlook, 0.9f, false);
            AddTac(new Vector3(0f, 0.05f, 0f), TacticalPointType.Choke, 0.8f, false);
            AddTac(new Vector3(17f, 0.05f, 0f), TacticalPointType.Patrol, 0.7f, false);
            AddTac(new Vector3(0f, 0.05f, 14f), TacticalPointType.Patrol, 0.6f, false);
            AddTac(new Vector3(0f, 0.05f, -14f), TacticalPointType.Patrol, 0.6f, false);

            AddAmmoCrate(new Vector3(-8.5f, 0.01f, 0f));
            AddAmmoCrate(new Vector3(8.5f, 0.01f, 0f));

            AddFlagAnchor(_gameplay, "Range Centre Line", new Vector3(0f, 0.04f, 0f));
            AddFlagAnchor(_gameplay, "West Overwatch Deck", new Vector3(-18f, 1.62f, 0f));
            AddFlagAnchor(_gameplay, "East Shatter Yard", new Vector3(17f, 0.04f, 0f));
            AddFlagAnchor(_gameplay, "North Proving Court", new Vector3(0f, 0.04f, 14.5f));
            AddFlagAnchor(_gameplay, "South Proving Court", new Vector3(0f, 0.04f, -14.5f));
            AddAmbientParticles(_lighting, "TestRangeDustMotes", true, new Vector3(0f, 2.4f, 0f), new Vector3(44f, 4.2f, 44f),
                new Color(0.92f, 0.90f, 0.84f, 0.14f), new Color(0.82f, 0.80f, 0.75f, 0.06f), 50, 8f, 4.0f, 7.0f, 0.25f, 0.55f, new Vector3(0.35f, 0.05f, 0.15f), 0f);

            // Crisp balanced 6200K daylight + shadow-side sky bounce fills (keeps shaded cover clear against dark concrete)
            foreach (var bp in new[] { new Vector3(-12f, 3.8f, 0f), new Vector3(12f, 3.8f, 0f), new Vector3(0f, 4.0f, 12f), new Vector3(0f, 4.0f, -12f) })
                AddLight(_lighting, $"ShadowBounce_{(int)bp.x}_{(int)bp.z}", bp, Quaternion.identity, LightType.Point, 5800f, 3200f, 22f, 0f, false);
            foreach (var tbp in new[] { new Vector3(-10.5f, 0f, 10.5f), new Vector3(10.5f, 0f, -10.5f) })
                SpawnBurningBarrel(_lighting, _props, tbp);
            BuildCustomLighting("LR2_TestRange_Profile", sunElev: 46f, sunAz: 215f, sunLux: 28000f, sunTemp: 6200f,
                fogMfp: 400f, fogColor: new Color(0.75f, 0.80f, 0.85f), exposureComp: 0.28f, contrast: 12f, saturation: -2f,
                rainDefaultPreset: -1, evMin: 2.8f, evMax: 12.2f,
                hdriAssetPath: "Assets/LeartesStudios/AbandonedFactory/HDRP/Art/Textures/T_HdriSky.hdr", skyExposure: 11.8f,
                shadowsTint: new Vector4(0.98f, 0.99f, 1.01f, 0.015f), highlightsTint: new Vector4(1.02f, 1.01f, 0.98f, 0.02f));
            PlaceEnvironmentRealism(new Color(0.74f, 0.78f, 0.80f, 1f), new Color(0.55f, 0.52f, 0.45f, 1f), 0.12f, 24f);
            PlaceMapDecals(pMin, pMax, 130);
            PlaceTargetedWearDecals();
            AddMatchSystems(30f);
            MarkStatic(env);

            EliminateSceneClipping("LR2_TestRange", _structures, _props, null, _spawns, _gameplay);
            SetupDestructibleProps(root);

            Physics.SyncTransforms();
            BakeNavMesh(root, "Assets/LR2/Scenes/LR2_TestRange_NavMesh.asset", false);
            GenerateCoverPoints(pMin, pMax, new[] { 0.4f, 1.85f });
            BuildMinimap("Assets/LR2/UI/Minimap/TestRange_Minimap.png", pMin, pMax);

            EditorSceneManager.SaveScene(scene, TestRangeScenePath);
            SetBuildScenes();
            Debug.Log("[LR2] Map built: " + TestRangeScenePath);
        }

        // =================================================================================================
        // Shared extra-map infrastructure helpers
        // =================================================================================================
        private static GameObject BeginExtraMap(string mapName, string configPath, int seed, out UnityEngine.SceneManagement.Scene scene)
        {
            _terrain = null;
            _cfg = LoadOrCreate<MapBuildConfig>(configPath);
            _cfg.seed = seed;
            _cfg.forest = false;
            EditorUtility.SetDirty(_cfg);
            _rnd = new System.Random(seed);
            ConfigureNavAgent();
            EnsureFolder("Assets/LR2/Scenes");
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var root = new GameObject(mapName);
            root.AddComponent<MapRoot>().config = _cfg;
            var env = Child(root.transform, "Environment");
            _structures = Child(env, "Structures");
            _props = Child(env, "Props");
            _background = Child(env, "Background");
            _decals = Child(env, "Decals");
            _lighting = Child(root.transform, "Lighting");
            _gameplay = Child(root.transform, "Gameplay");
            _spawns = Child(_gameplay, "Spawns");
            _cover = Child(_gameplay, "Cover");
            _tactical = Child(_gameplay, "Tactical");
            _systems = Child(root.transform, "Systems");

            _m = MakeMaterials();
            return root;
        }

        private static void PlaceEnvironmentRealism(Color mistColor, Color dustColor, float mistOpacity, float radius, Vector3? localPos = null, string name = "EnvironmentRealism")
        {
            if (_lighting == null) return;
            var go = new GameObject(name);
            go.transform.SetParent(_lighting, false);
            if (localPos.HasValue) go.transform.localPosition = localPos.Value;
            var afx = go.AddComponent<AtmosphereFX>();
            var so = new SerializedObject(afx);
            so.FindProperty("mistColor").colorValue = mistColor;
            so.FindProperty("dustColor").colorValue = dustColor;
            so.FindProperty("mistOpacity").floatValue = mistOpacity;
            so.FindProperty("radius").floatValue = radius;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SpawnBurningBarrel(Transform lightsParent, Transform propsParent, Vector3 pos)
        {
            var barrel = Prop("SM_Barrel_04", propsParent, pos, R(0f, 360f));
            var scorch = FindLeartes("M_RustDecal_3") ?? FindLeartes("M_CrackDecal_3");
            if (scorch != null && _decals != null)
                CreateDecal(scorch, new Vector3(pos.x, pos.y + 0.02f, pos.z), Vector3.up, 1.9f, true);
            var fireHolder = new GameObject("BurningBarrelFX");
            fireHolder.transform.SetParent(barrel != null ? barrel.transform : lightsParent, false);
            fireHolder.transform.position = (barrel != null ? barrel.transform.position : pos) + Vector3.up * 0.85f;
            var sc = fireHolder.AddComponent<SphereCollider>();
            sc.isTrigger = true;
            sc.radius = 1.65f;
            var fh = fireHolder.AddComponent<FireHazard>();
            var so = new SerializedObject(fh);
            so.FindProperty("damagePerSecond").floatValue = 14f;
            so.FindProperty("damageRadius").floatValue = 1.65f;
            so.FindProperty("lightIntensity").floatValue = 6500f;
            so.FindProperty("lightRange").floatValue = 17f;
            so.ApplyModifiedPropertiesWithoutUndo();

            // FireHazard owns the flickering runtime Fire Light; avoid stacking a duplicate static PointLight on the same barrel.
            var hazeGo = new GameObject("FireHeatHaze");
            hazeGo.transform.SetParent(fireHolder.transform, false);
            hazeGo.transform.localPosition = Vector3.up * 0.55f;
            hazeGo.AddComponent<HeatHazeEmitter>().Configure(new Vector2(0.48f, 0.96f), 0.68f);
        }

        private static void SanitizeParticleSystems(GameObject go)
        {
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                var vel = ps.velocityOverLifetime;
                if (!vel.enabled) continue;
                var m = vel.y.mode;
                if (vel.x.mode != m || vel.z.mode != m)
                {
                    if (m == ParticleSystemCurveMode.TwoConstants)
                    {
                        vel.x = new ParticleSystem.MinMaxCurve(vel.x.constantMin, vel.x.constantMax);
                        vel.z = new ParticleSystem.MinMaxCurve(vel.z.constantMin, vel.z.constantMax);
                    }
                    else
                    {
                        vel.x = new ParticleSystem.MinMaxCurve(vel.x.constant);
                        vel.y = new ParticleSystem.MinMaxCurve(vel.y.constant);
                        vel.z = new ParticleSystem.MinMaxCurve(vel.z.constant);
                    }
                }
            }
        }

        private static void BuildCustomLighting(string profileName, float sunElev, float sunAz, float sunLux, float sunTemp,
            float fogMfp, Color fogColor, float exposureComp, float contrast, float saturation, int rainDefaultPreset,
            float evMin = 6.5f, float evMax = 12.8f,
            string hdriAssetPath = "Assets/LeartesStudios/Art/Textures/T_HDRI_01.HDR", float skyExposure = 12.2f,
            Vector4? shadowsTint = null, Vector4? highlightsTint = null)
        {
            var sunGo = new GameObject("Sun");
            sunGo.transform.SetParent(_lighting, false);
            sunGo.transform.rotation = Quaternion.Euler(sunElev, sunAz, 0f);
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.lightUnit = LightUnit.Lux;
            sun.intensity = sunLux;
            sun.useColorTemperature = true;
            sun.colorTemperature = sunTemp;
            sun.color = Color.white;
            sun.shadows = LightShadows.Soft;
            var hdSun = sunGo.AddComponent<HDAdditionalLightData>();
            hdSun.angularDiameter = 1.6f;
            hdSun.SetShadowResolutionOverride(false);
            RenderSettings.sun = sun;

            var profile = NewProfile($"{LightDir}/{profileName}.asset");
            var ve = profile.Add<VisualEnvironment>(true);
            ve.skyAmbientMode.Override(SkyAmbientMode.Dynamic);
            ApplyHdriSky(ve, profile, hdriAssetPath, skyExposure, rotationDeg: sunAz * 0.4f);
            // Global indirect/sky visibility floor so dark cover & interiors never swallow enemy silhouettes
            var ilcGlobal = profile.Add<IndirectLightingController>(true);
            ilcGlobal.indirectDiffuseLightingMultiplier.Override(2.15f);
            ilcGlobal.reflectionLightingMultiplier.Override(1.50f);
            var fog = profile.Add<Fog>(true);
            fog.enabled.Override(rainDefaultPreset >= 0);
            fog.meanFreePath.Override(Mathf.Max(fogMfp, 72f));
            fog.baseHeight.Override(0f);
            fog.maximumHeight.Override(9.5f);
            fog.enableVolumetricFog.Override(rainDefaultPreset >= 0);
            fog.albedo.Override(fogColor);
            fog.anisotropy.Override(0.28f);
            fog.depthExtent.Override(90f);
            var exposure = profile.Add<Exposure>(true);
            exposure.mode.Override(ExposureMode.AutomaticHistogram);
            exposure.compensation.Override(Mathf.Clamp(exposureComp, -0.15f, 0.30f));
            exposure.limitMin.Override(Mathf.Min(evMin, 2.0f));
            exposure.limitMax.Override(Mathf.Max(evMax, 14.5f));
            exposure.adaptationMode.Override(AdaptationMode.Progressive);
            exposure.adaptationSpeedDarkToLight.Override(2.8f);
            exposure.adaptationSpeedLightToDark.Override(1.8f);
            var tone = profile.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.ACES);
            var bloom = profile.Add<Bloom>(true);
            bloom.intensity.Override(0.06f);
            bloom.threshold.Override(2.8f);
            bloom.scatter.Override(0.35f);
            bloom.dirtIntensity.Override(0f);
            bloom.dirtTexture.Override(null);
            var color = profile.Add<ColorAdjustments>(true);
            color.contrast.Override(contrast);
            color.saturation.Override(saturation);
            var smh = profile.Add<ShadowsMidtonesHighlights>(true);
            smh.shadows.Override(shadowsTint ?? new Vector4(0.97f, 0.99f, 1.02f, 0.015f));
            if (highlightsTint.HasValue) smh.highlights.Override(highlightsTint.Value);
            var vig = profile.Add<Vignette>(true);
            vig.intensity.Override(rainDefaultPreset >= 0 ? 0.25f : 0.14f);
            var ao = profile.Add<ScreenSpaceAmbientOcclusion>(true);
            ao.intensity.Override(0.78f);
            ao.radius.Override(1.45f);
            ao.directLightingStrength.Override(0.24f);
            profile.Add<ScreenSpaceReflection>(true).enabled.Override(true);
            profile.Add<GlobalIllumination>(true).enable.Override(false);
            var contact = profile.Add<ContactShadows>(true);
            contact.enable.Override(true);
            contact.length.Override(0.34f);
            contact.opacity.Override(0.90f);
            profile.Add<MicroShadowing>(true).enable.Override(true);
            var shadows = profile.Add<HDShadowSettings>(true);
            shadows.maxShadowDistance.Override(95f);
            shadows.cascadeShadowSplitCount.Override(4);
            DisableMotionBlur(profile);
            ApplyCinematicPost(profile, rainDefaultPreset >= 0 ? 1.15f : 1f); // B101
            SaveProfile(profile);

            var vol = new GameObject("GlobalVolume");
            vol.transform.SetParent(_lighting, false);
            var v = vol.AddComponent<Volume>();
            v.isGlobal = true;
            v.priority = 0f;
            v.sharedProfile = profile;
            AddArenaReflectionProbe(_lighting, new Vector3(0f, 3.5f, 0f), new Vector3(85f, 30f, 85f));

            if (rainDefaultPreset >= 0)
            {
                var wsGo = new GameObject("Weather");
                wsGo.transform.SetParent(_systems, false);
                var ws = wsGo.AddComponent<WeatherSystem>();
                ws.sun = sun;
                ws.globalVolume = v;
                ws.startPreset = rainDefaultPreset;
                ws.randomStart = false;
                ws.dynamicCycle = true;
            }
        }

        private static Texture2D EnsureHighCloudMapTexture()
        {
            const string path = LightDir + "/T_LR2_HighClouds.png";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;

            const int w = 512, h = 256;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
            var px = new Color32[w * h];
            float TileFbm(float u, float v, float scale, int octaves, float seed)
            {
                float sum = 0f, amp = 0.55f, freq = scale;
                for (int o = 0; o < octaves; o++)
                {
                    float a = u * Mathf.PI * 2f;
                    float nx = Mathf.Cos(a) * (freq * 0.28f) + seed + o * 13.7f;
                    float ny = Mathf.Sin(a) * (freq * 0.28f) + v * freq * 1.4f + seed + o * 7.3f;
                    sum += Mathf.PerlinNoise(nx, ny) * amp;
                    freq *= 2.05f;
                    amp *= 0.5f;
                }
                return sum;
            }

            for (int y = 0; y < h; y++)
            {
                float v = y / (float)(h - 1);
                float latFade = Mathf.Sin(v * Mathf.PI); // taper cleanly at poles
                for (int x = 0; x < w; x++)
                {
                    float u = x / (float)w;
                    float cumulusPatch = Mathf.Clamp01((TileFbm(u, v, 5f, 4, 3.1f) - 0.44f) * 2.4f) * latFade;
                    float cirrusWisps = Mathf.Clamp01((TileFbm(u * 1.5f, v * 2.2f, 9f, 4, 19.4f) - 0.46f) * 2.6f) * latFade;
                    px[y * w + x] = new Color32(
                        (byte)Mathf.RoundToInt(cumulusPatch * 255f),
                        (byte)Mathf.RoundToInt(cirrusWisps * 255f),
                        (byte)Mathf.RoundToInt(cirrusWisps * 255f),
                        (byte)Mathf.RoundToInt(Mathf.Max(cumulusPatch, cirrusWisps) * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(true, false);
            Directory.CreateDirectory(LightDir);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp != null)
            {
                imp.sRGBTexture = false;
                imp.wrapMode = TextureWrapMode.Repeat;
                imp.filterMode = FilterMode.Bilinear;
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static void ApplyHdriSky(VisualEnvironment ve, VolumeProfile profile, string preferredPath, float exposureEv, float rotationDeg)
        {
            // B138: Multi-layer sky system — physical atmospheric scattering + high-altitude double CloudLayer
            // + dual-deck VolumetricClouds reacting dynamically to weather & wind (zero HDRI zenith pinch).
            ve.skyType.Override((int)SkyType.PhysicallyBased);
            ve.skyAmbientMode.Override(SkyAmbientMode.Dynamic);
            ve.cloudType.Override((int)CloudType.CloudLayer);
            ve.windOrientation.Override(232f);
            ve.windSpeed.Override(46f);

            var phys = profile.Add<PhysicallyBasedSky>(true);
            phys.type.Override(PhysicallyBasedSkyModel.EarthSimple);
            phys.skyIntensityMode.Override(SkyIntensityMode.Exposure);
            // PhysicallyBasedSky is calibrated to the physical sun; combat maps keep 0.0 EV (Main Menu -0.6 EV for moody lighting).
            float pbrEv = exposureEv <= 10.8f ? -0.6f : 0.0f;
            phys.exposure.Override(pbrEv);
            phys.multiplier.Override(1.0f);
            phys.aerosolDensity.Override(0.14f);
            phys.horizonTint.Override(new Color(0.88f, 0.84f, 0.78f));
            phys.zenithTint.Override(new Color(0.42f, 0.62f, 0.96f));

            var cloudTex = EnsureHighCloudMapTexture();
            var cloudLayer = profile.Add<CloudLayer>(true);
            cloudLayer.opacity.Override(0.42f);
            cloudLayer.upperHemisphereOnly.Override(true);
            cloudLayer.layers.Override(CloudMapMode.Double);
            cloudLayer.resolution.Override(CloudResolution.CloudResolution1024);
            if (cloudTex != null) cloudLayer.layerA.cloudMap.Override(cloudTex);
            cloudLayer.layerA.opacityR.Override(0.90f);
            cloudLayer.layerA.opacityG.Override(0.0f);
            cloudLayer.layerA.opacityB.Override(0.0f);
            cloudLayer.layerA.opacityA.Override(0.0f);
            cloudLayer.layerA.altitude.Override(4800f);
            cloudLayer.layerA.rotation.Override(rotationDeg * 0.5f);
            cloudLayer.layerA.tint.Override(new Color(0.95f, 0.96f, 1.0f, 1f));
            cloudLayer.layerA.exposure.Override(-0.3f);
            cloudLayer.layerA.distortionMode.Override(CloudDistortionMode.Procedural);
            cloudLayer.layerA.scrollOrientation.Override(new WindParameter.WindParamaterValue { mode = WindParameter.WindOverrideMode.Custom, customValue = 220f, additiveValue = 0f, multiplyValue = 1f });
            cloudLayer.layerA.scrollSpeed.Override(new WindParameter.WindParamaterValue { mode = WindParameter.WindOverrideMode.Custom, customValue = 18f, additiveValue = 0f, multiplyValue = 1f });
            cloudLayer.layerA.lighting.Override(true);
            cloudLayer.layerA.thickness.Override(0.45f);
            if (cloudTex != null) cloudLayer.layerB.cloudMap.Override(cloudTex);
            cloudLayer.layerB.opacityR.Override(0.0f);
            cloudLayer.layerB.opacityG.Override(0.85f);
            cloudLayer.layerB.opacityB.Override(0.0f);
            cloudLayer.layerB.opacityA.Override(0.0f);
            cloudLayer.layerB.altitude.Override(7800f);
            cloudLayer.layerB.rotation.Override(rotationDeg * 0.5f + 95f);
            cloudLayer.layerB.tint.Override(new Color(0.88f, 0.92f, 1.0f, 1f));
            cloudLayer.layerB.exposure.Override(-0.5f);
            cloudLayer.layerB.distortionMode.Override(CloudDistortionMode.Procedural);
            cloudLayer.layerB.scrollOrientation.Override(new WindParameter.WindParamaterValue { mode = WindParameter.WindOverrideMode.Custom, customValue = 250f, additiveValue = 0f, multiplyValue = 1f });
            cloudLayer.layerB.scrollSpeed.Override(new WindParameter.WindParamaterValue { mode = WindParameter.WindOverrideMode.Custom, customValue = 10f, additiveValue = 0f, multiplyValue = 1f });
            cloudLayer.layerB.lighting.Override(true);
            cloudLayer.layerB.thickness.Override(0.32f);

            var clouds = profile.Add<VolumetricClouds>(true);
            clouds.enable.Override(true);
            clouds.cloudControl.Override(VolumetricClouds.CloudControl.Simple);
            clouds.cloudSimpleMode.Override(VolumetricClouds.CloudSimpleMode.Quality);
            clouds.bottomAltitude.Override(1100f);
            clouds.altitudeRange.Override(2200f);
            clouds.densityCurve.Override(new AnimationCurve(
                new Keyframe(0f, 0f),
                new Keyframe(0.14f, 1f),
                new Keyframe(0.55f, 0.72f),
                new Keyframe(1f, 0.08f)));
            clouds.numPrimarySteps.Override(64);
            clouds.numLightSteps.Override(6);
            clouds.densityMultiplier.Override(0.42f);
            // Higher shapeFactor (0.855) carves clear blue sky gaps between sculpted billowing cumulus decks.
            clouds.shapeFactor.Override(0.855f);
            clouds.shapeScale.Override(4.6f);
            clouds.erosionFactor.Override(0.88f);
            clouds.erosionScale.Override(75f);
            clouds.microErosionFactor.Override(0.65f);
            clouds.microErosionScale.Override(280f);
            clouds.multiScattering.Override(0.55f);
            clouds.powderEffectIntensity.Override(0.72f);
            clouds.ambientLightProbeDimmer.Override(0.85f);
            clouds.sunLightDimmer.Override(1.0f);
            clouds.scatteringTint.Override(Color.black);
            clouds.globalWindSpeed.Override(new WindParameter.WindParamaterValue { mode = WindParameter.WindOverrideMode.Custom, customValue = 54f, additiveValue = 0f, multiplyValue = 1f });
            clouds.orientation.Override(new WindParameter.WindParamaterValue { mode = WindParameter.WindOverrideMode.Custom, customValue = Mathf.Repeat(232f + rotationDeg * 0.2f, 360f), additiveValue = 0f, multiplyValue = 1f });
            clouds.shadows.Override(true);
            clouds.shadowResolution.Override(VolumetricClouds.CloudShadowResolution.Low128);
            clouds.shadowOpacity.Override(0.40f);
        }

        private static void AddArenaReflectionProbe(Transform parent, Vector3 pos, Vector3 size)
        {
            var go = new GameObject("ArenaReflectionProbe");
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            var rp = go.AddComponent<ReflectionProbe>();
            rp.mode = ReflectionProbeMode.Realtime;
            rp.refreshMode = ReflectionProbeRefreshMode.OnAwake;
            rp.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
            rp.size = size;
            rp.boxProjection = true;
            rp.resolution = 256;
            rp.importance = 1;
            go.AddComponent<HDAdditionalReflectionData>();
        }

        private static void AddFlagAnchor(Transform parent, string sectorName, Vector3 pos, float outerRadius = 11.5f, float coreRadius = 2.6f)
        {
            var go = new GameObject("FlagAnchor_" + sectorName.Replace(" ", ""));
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            var fa = go.AddComponent<FlagAnchor>();
            fa.sectorName = sectorName;
            fa.captureRadius = outerRadius;
            fa.coreRadius = coreRadius;
        }

        private static void AddAmbientParticles(Transform parent, string name, bool smokeMaterial, Vector3 center, Vector3 boxSize,
            Color colA, Color colB, int maxParticles, float rateOverTime, float lifeMin, float lifeMax,
            float sizeMin, float sizeMax, Vector3 baseVel, float gravity)
        {
            string matPath = smokeMaterial ? "Assets/LR2/Materials/FX/M_FX_Smoke.mat" : "Assets/LR2/Materials/FX/M_FX_Spark.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null) return;

            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = center;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.duration = 6f;
            main.loop = true;
            main.prewarm = true;
            main.playOnAwake = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = maxParticles;
            main.startLifetime = new ParticleSystem.MinMaxCurve(lifeMin, lifeMax);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0f, 0.08f);
            main.startSize = new ParticleSystem.MinMaxCurve(sizeMin, sizeMax);
            main.startColor = new ParticleSystem.MinMaxGradient(colA, colB);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.gravityModifier = gravity;

            var em = ps.emission;
            em.rateOverTime = rateOverTime;

            var sh = ps.shape;
            sh.enabled = true;
            sh.shapeType = ParticleSystemShapeType.Box;
            sh.scale = boxSize;

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(baseVel.x * 0.6f, baseVel.x * 1.4f + 0.15f);
            vel.y = new ParticleSystem.MinMaxCurve(baseVel.y - 0.1f, baseVel.y + 0.2f);
            vel.z = new ParticleSystem.MinMaxCurve(baseVel.z * 0.6f, baseVel.z * 1.4f + 0.15f);

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.85f, 0.2f), new GradientAlphaKey(0.85f, 0.78f), new GradientAlphaKey(0f, 1f) });
            col.color = grad;

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            ps.Play(true);
        }

        private static void PlaceMapDecals(Vector2 playMin, Vector2 playMax, int tries)
        {
            string[] names =
            {
                "M_CrackDecal", "M_CrackDecal_2", "M_CrackDecal_3", "M_CrackDecal_4",
                "M_LeakDecal", "M_LeakDecal_2", "M_LeakDecal_3", "M_LeakDecal_4",
                "M_RustDecal", "M_RustDecal_2", "M_RustDecal_3", "M_RustDecal_4",
                "M_GraffittiDecal", "M_GraffittiDecal_2", "M_Puddle", "M_Decal_Puddle_A_MIC"
            };
            PlaceProjectedDecals(playMin, playMax, tries, names);
        }
    }
}
