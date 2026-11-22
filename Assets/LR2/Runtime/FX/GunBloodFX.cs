using System.Collections.Generic;
using LR2.Characters;
using LR2.Combat;
using LR2.Core;
using UnityEngine;
using UnityEngine.UI;

namespace LR2.FX
{
    /// <summary>
    /// Wet crimson 3D blood droplets welded tightly onto the held weapon's receiver/barrel (parented to ActiveItem,
    /// strictly forward of camera near plane) plus subtle peripheral lens specks on taking damage.
    /// </summary>
    public class GunBloodFX : MonoBehaviour
    {
        public static GunBloodFX Instance { get; private set; }

        private const int MaxScreenDroplets = 12;
        private const int MaxGunDroplets = 24;

        private struct ScreenDroplet
        {
            public RectTransform rt;
            public RawImage img;
            public float age;
            public float lifetime;
            public float baseWidth;
            public float baseHeight;
            public float dripSpeed;
            public float maxAlpha;
            public bool active;
        }

        private struct GunDroplet
        {
            public GameObject go;
            public Transform host;
            public Vector3 baseLocalPos;
            public Vector3 localSlipDir;
            public float baseScale;
            public float slideSpeed;
            public float maxSlide;
            public float age;
            public float lifetime;
        }

        private Combatant _player;
        private SoldierController _soldier;
        private Object _lastItem;
        private float _nextLowHealthDrip;
        private bool _wasDead;

        private Canvas _screenCanvas;
        private readonly ScreenDroplet[] _screenPool = new ScreenDroplet[MaxScreenDroplets];
        private readonly List<GunDroplet> _gunDrops = new List<GunDroplet>(MaxGunDroplets);

        private Texture2D[] _screenTexs;
        private Texture2D _gunSplatTex;
        private Material _gunBeadMat;
        private Material _gunSplatMat;
        private Mesh _quadMesh;
        private Mesh _sphereMesh;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBoot()
        {
            if (Combatant.FindLocalPlayer() != null) EnsureInstance();
        }

        public static GunBloodFX EnsureInstance()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("[LR2 GunBloodFX]");
            Instance = go.AddComponent<GunBloodFX>();
            return Instance;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            BuildTexturesAndMaterials();
            BuildScreenCanvas();
            GameEvents.Hit += OnHit;
            GameEvents.Killed += OnKilled;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            GameEvents.Hit -= OnHit;
            GameEvents.Killed -= OnKilled;

            ClearGunDroplets();
            if (_screenCanvas != null) Destroy(_screenCanvas.gameObject);
            if (_gunBeadMat != null) Destroy(_gunBeadMat);
            if (_gunSplatMat != null) Destroy(_gunSplatMat);
            if (_gunSplatTex != null) Destroy(_gunSplatTex);
            if (_screenTexs != null)
            {
                foreach (var t in _screenTexs)
                    if (t != null) Destroy(t);
            }
        }

        private void Update()
        {
            BindPlayer();
            float dt = Time.unscaledDeltaTime;

            if (_player != null)
            {
                bool dead = !_player.IsAlive;
                if (dead && !_wasDead)
                {
                    ClearGunDroplets();
                }
                else if (!dead && _wasDead)
                {
                    ClearGunDroplets();
                    ClearScreenDroplets();
                }
                _wasDead = dead;

                var curItem = _soldier != null ? _soldier.ActiveItem : null;
                if (curItem != _lastItem)
                {
                    ClearGunDroplets();
                    _lastItem = curItem;
                }

                // Very subtle edge drip when critically wounded
                if (!dead && _player.Health != null && _player.Health.Normalized < 0.35f && Time.unscaledTime >= _nextLowHealthDrip)
                {
                    _nextLowHealthDrip = Time.unscaledTime + Random.Range(2.2f, 3.6f);
                    float edgeX = Random.value < 0.5f ? -1f : 1f;
                    SplashScreenDroplets(1, 0.25f, edgeX, Random.Range(-0.8f, 0.8f));
                }
            }

            UpdateScreenDroplets(dt);
            UpdateGunDroplets(Time.deltaTime);
        }

        private void BindPlayer()
        {
            if (_player == null) _player = Combatant.FindLocalPlayer();
            if (_soldier == null && _player != null) _soldier = _player.GetComponent<SoldierController>();
        }

        private void OnHit(HitEvent e)
        {
            if (_player == null) BindPlayer();
            if (_player == null || e.damage <= 0f) return;

            if (e.victim == _player)
            {
                float sev = Mathf.Clamp01(e.damage / 35f);
                ComputeHitScreenBias(e.point, e.direction, out float biasX, out float biasY);

                // Subtle peripheral lens droplets (1-3)
                int screenCount = e.damage >= 22f ? 3 : e.damage >= 12f ? 2 : 1;
                SplashScreenDroplets(screenCount, sev, biasX, biasY);

                int gunCount = Mathf.RoundToInt(Mathf.Lerp(4f, 9f, sev));
                SplashGunDroplets(gunCount, sev);
            }
            else if (e.attacker == _player && e.victim != _player)
            {
                // Point-blank CQB splatter on gun metal only (never floating in front of lens)
                float dist = Vector3.Distance(_player.Position, e.point);
                if (dist < 3.4f)
                {
                    float closeFactor = Mathf.Clamp01(1f - dist / 3.4f);
                    SplashGunDroplets(Mathf.RoundToInt(Mathf.Lerp(2f, 5f, closeFactor)), closeFactor * 0.75f);
                }
            }
        }

        private void OnKilled(KillEvent e)
        {
            if (_player == null) return;
            if (e.victim == _player)
            {
                SplashScreenDroplets(2, 0.7f, 0f, 0f);
                return;
            }
            if (e.killer == _player && e.victim != null)
            {
                float dist = Vector3.Distance(_player.Position, e.victim.Position);
                if (dist < 3.6f) SplashGunDroplets(e.headshot ? 4 : 2, 0.8f);
            }
        }

        private void ComputeHitScreenBias(Vector3 hitPoint, Vector3 dir, out float biasX, out float biasY)
        {
            biasX = 0f;
            biasY = 0f;
            if (_soldier == null || _soldier.ViewCamera == null) return;
            Transform camT = _soldier.ViewCamera.transform;
            Vector3 toSource = -dir;
            if (toSource.sqrMagnitude < 0.01f && _player != null)
                toSource = hitPoint - _player.ChestPosition;
            if (toSource.sqrMagnitude < 0.001f) return;
            Vector3 local = camT.InverseTransformDirection(toSource.normalized);
            biasX = Mathf.Clamp(local.x, -1f, 1f);
            biasY = Mathf.Clamp(local.y, -1f, 1f);
        }

        // ------------------------------------------------------------------ subtle peripheral screen blood specks
        private void BuildScreenCanvas()
        {
            var go = new GameObject("[LR2 ScreenBloodCanvas]", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            _screenCanvas = go.AddComponent<Canvas>();
            _screenCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _screenCanvas.sortingOrder = 6;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            for (int i = 0; i < MaxScreenDroplets; i++)
            {
                var dGo = new GameObject($"BloodDrop_{i}", typeof(RectTransform));
                dGo.transform.SetParent(go.transform, false);
                var rt = (RectTransform)dGo.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.72f);

                var img = dGo.AddComponent<RawImage>();
                img.texture = _screenTexs[i % _screenTexs.Length];
                img.raycastTarget = false;
                img.color = Color.clear;
                dGo.SetActive(false);

                _screenPool[i] = new ScreenDroplet { rt = rt, img = img, active = false };
            }
        }

        private void SplashScreenDroplets(int count, float severity, float biasX, float biasY)
        {
            for (int n = 0; n < count; n++)
            {
                int slot = PickScreenSlot();
                ref var d = ref _screenPool[slot];

                float angle = Random.Range(0f, Mathf.PI * 2f);
                if (Mathf.Abs(biasX) + Mathf.Abs(biasY) > 0.25f && Random.value < 0.75f)
                {
                    float baseAng = Mathf.Atan2(biasY, biasX);
                    angle = baseAng + Random.Range(-0.85f, 0.85f);
                }

                // Keep lens specks strictly near screen perimeter so center sightline stays unobstructed
                float radiusX = Random.Range(560f, 910f);
                float radiusY = Random.Range(340f, 505f);
                float x = Mathf.Clamp(Mathf.Cos(angle) * radiusX, -920f, 920f);
                float y = Mathf.Clamp(Mathf.Sin(angle) * radiusY, -505f, 505f);

                if (Mathf.Abs(x) < 460f && Mathf.Abs(y) < 280f)
                {
                    if (Mathf.Abs(x) > Mathf.Abs(y)) x = (x >= 0f ? 1f : -1f) * Random.Range(480f, 720f);
                    else y = (y >= 0f ? 1f : -1f) * Random.Range(300f, 440f);
                }

                float size = Random.Range(12f, 26f) * Mathf.Lerp(0.8f, 1.15f, severity);
                float aspect = Random.Range(0.75f, 1.25f);
                d.baseWidth = size * aspect;
                d.baseHeight = size / aspect;
                d.rt.anchoredPosition = new Vector2(x, y);
                d.rt.sizeDelta = new Vector2(d.baseWidth, d.baseHeight);
                d.rt.localEulerAngles = new Vector3(0f, 0f, Random.Range(-16f, 16f));
                d.img.texture = _screenTexs[Random.Range(0, _screenTexs.Length)];

                d.age = 0f;
                d.lifetime = Random.Range(3.4f, 6.0f);
                d.dripSpeed = Random.Range(3f, 9f);
                d.maxAlpha = Random.Range(0.14f, 0.28f) * Mathf.Lerp(0.7f, 1f, severity);
                d.active = true;
                d.rt.gameObject.SetActive(true);
            }
        }

        private int PickScreenSlot()
        {
            int oldest = 0;
            float oldestRatio = -1f;
            for (int i = 0; i < _screenPool.Length; i++)
            {
                if (!_screenPool[i].active) return i;
                float ratio = _screenPool[i].age / Mathf.Max(0.1f, _screenPool[i].lifetime);
                if (ratio > oldestRatio)
                {
                    oldestRatio = ratio;
                    oldest = i;
                }
            }
            return oldest;
        }

        private void UpdateScreenDroplets(float dt)
        {
            float healMult = (_player != null && _player.Health != null && _player.Health.Normalized > 0.88f) ? 1.6f : 1f;
            for (int i = 0; i < _screenPool.Length; i++)
            {
                ref var d = ref _screenPool[i];
                if (!d.active) continue;
                d.age += dt * healMult;
                if (d.age >= d.lifetime)
                {
                    d.active = false;
                    d.rt.gameObject.SetActive(false);
                    continue;
                }

                float t = d.age / d.lifetime;
                float pop = Mathf.Clamp01(d.age / 0.08f);
                float popScale = Mathf.Lerp(1.2f, 1f, pop * pop);
                float fade = t < 0.45f ? 1f : Mathf.Clamp01((1f - t) / 0.55f);

                float drip = Mathf.Max(0f, d.age - 0.4f);
                float stretchY = 1f + Mathf.Clamp01(drip * 0.1f) * 0.35f;
                d.rt.anchoredPosition -= new Vector2(0f, d.dripSpeed * dt * Mathf.Clamp01(drip * 0.8f));
                d.rt.sizeDelta = new Vector2(d.baseWidth * popScale, d.baseHeight * popScale * stretchY);

                Color c = Color.Lerp(new Color(0.82f, 0.07f, 0.07f, 1f), new Color(0.36f, 0.02f, 0.03f, 1f), t);
                c.a = d.maxAlpha * pop * fade;
                d.img.color = c;
            }
        }

        private void ClearScreenDroplets()
        {
            for (int i = 0; i < _screenPool.Length; i++)
            {
                _screenPool[i].active = false;
                if (_screenPool[i].rt != null) _screenPool[i].rt.gameObject.SetActive(false);
            }
        }

        public int ActiveGunDropletCount => _gunDrops.Count;
        public float LastMeasuredTopY { get; private set; } = -1f;

        public float[] GetActiveGunDropletLocalY()
        {
            var ys = new float[_gunDrops.Count];
            for (int i = 0; i < _gunDrops.Count; i++)
                ys[i] = _gunDrops[i].go != null ? _gunDrops[i].go.transform.localPosition.y : 0f;
            return ys;
        }

        // ------------------------------------------------------------------ 3D gun blood drops welded onto weapon mesh
        private void MeasureWeaponTopEnvelope(Transform weaponRoot, float zMin, float zMax, float fallbackY, bool isCompactPistol, out float topY, out float halfX)
        {
            topY = fallbackY + (isCompactPistol ? 0.018f : 0.032f);
            halfX = isCompactPistol ? 0.012f : 0.019f;

            float measuredTop = float.MinValue;
            float measuredMaxX = 0f;
            bool any = false;

            foreach (var r in weaponRoot.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                string rn = r.gameObject.name;
                if (rn.Contains("[LR2_Blood") || rn.Contains("Laser") || rn.Contains("Muzzle") ||
                    rn.Contains("Crosshair") || rn.Contains("Particle") || rn.Contains("Flame") || rn.Contains("Smoke"))
                    continue;
                if (r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer) continue;

                Mesh mesh = null;
                if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null) mesh = smr.sharedMesh;
                else if (r.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null) mesh = mf.sharedMesh;
                if (mesh == null) continue;

                if (mesh.isReadable)
                {
                    var verts = mesh.vertices;
                    Matrix4x4 toRoot = weaponRoot.worldToLocalMatrix * r.transform.localToWorldMatrix;
                    int step = Mathf.Max(1, verts.Length / 350);
                    for (int vi = 0; vi < verts.Length; vi += step)
                    {
                        Vector3 p = toRoot.MultiplyPoint3x4(verts[vi]);
                        if (p.z < zMin || p.z > zMax) continue;
                        if (Mathf.Abs(p.x) > 0.038f || p.y > 0.095f || p.y < -0.05f) continue;
                        if (p.y > measuredTop) measuredTop = p.y;
                        measuredMaxX = Mathf.Max(measuredMaxX, Mathf.Abs(p.x));
                        any = true;
                    }
                }
                else
                {
                    Bounds lb = r.localBounds;
                    if (lb.size.sqrMagnitude < 1e-6f) continue;
                    Matrix4x4 toRoot = weaponRoot.worldToLocalMatrix * r.transform.localToWorldMatrix;
                    Vector3 min = lb.min, max = lb.max;
                    for (int c = 0; c < 8; c++)
                    {
                        Vector3 pt = toRoot.MultiplyPoint3x4(new Vector3(
                            (c & 1) == 0 ? min.x : max.x,
                            (c & 2) == 0 ? min.y : max.y,
                            (c & 4) == 0 ? min.z : max.z));
                        if (Mathf.Abs(pt.x) <= 0.05f && pt.y <= 0.085f && pt.y > measuredTop)
                        {
                            measuredTop = pt.y;
                            any = true;
                        }
                    }
                }
            }

            if (any)
            {
                topY = Mathf.Clamp(measuredTop, fallbackY + 0.006f, fallbackY + (isCompactPistol ? 0.036f : 0.062f));
                if (measuredMaxX > 0.008f) halfX = Mathf.Clamp(measuredMaxX * 0.85f, 0.011f, 0.023f);
            }
        }

        private void SplashGunDroplets(int count, float severity)
        {
            if (_soldier == null || _soldier.IsDead || _soldier.ActiveItem == null) return;
            Transform weaponRoot = _soldier.ActiveItem.transform;
            if (weaponRoot == null || !weaponRoot.gameObject.activeInHierarchy) return;

            Camera viewCam = _soldier.ViewCamera;
            Transform camT = viewCam != null ? viewCam.transform : _soldier.transform;

            // Locate structural landmarks in weaponRoot's 1:1 meter local space (never scaled 100x FBX SkinnedMesh containers)
            Transform muzzleT = FindDeepChild(weaponRoot, "Muzzle");
            Transform ejectT = FindDeepChild(weaponRoot, "EjectionPort");
            Transform aimT = FindDeepChild(weaponRoot, "AimPoint") ?? FindDeepChild(weaponRoot, "DefaultAimPoint");

            Vector3 ejectLocal = ejectT != null ? weaponRoot.InverseTransformPoint(ejectT.position) : new Vector3(0.01f, 0.0f, 0.06f);
            Vector3 muzzleLocal = muzzleT != null ? weaponRoot.InverseTransformPoint(muzzleT.position) : ejectLocal + Vector3.forward * 0.40f;
            Vector3 aimLocal = aimT != null ? weaponRoot.InverseTransformPoint(aimT.position) : new Vector3(0f, 0.055f, -0.25f);

            // B136n: Use horizontal bore span and the real upper-receiver / top-rail envelope so droplets always land ON TOP
            // of the weapon (visible in 1P), never slung underneath the barrel where Muzzle.y dips negative.
            Vector3 flatDelta = new Vector3(muzzleLocal.x - ejectLocal.x, 0f, muzzleLocal.z - ejectLocal.z);
            float boreLen = Mathf.Max(0.14f, flatDelta.magnitude);
            Vector3 fwd = flatDelta.sqrMagnitude > 1e-4f ? flatDelta / flatDelta.magnitude : Vector3.forward;
            bool isCompactPistol = boreLen < 0.28f;

            float baseY = Mathf.Max(ejectLocal.y, muzzleLocal.y, 0.0f);
            Vector3 spineStart = new Vector3(0f, baseY, ejectLocal.z - (isCompactPistol ? 0.03f : 0.05f));
            Vector3 spineEnd = spineStart + fwd * Mathf.Clamp(boreLen * 0.68f, 0.08f, 0.27f);
            MeasureWeaponTopEnvelope(weaponRoot, Mathf.Min(spineStart.z, spineEnd.z), Mathf.Max(spineStart.z, spineEnd.z), baseY, isCompactPistol,
                out float topY, out float hullRadiusX);
            LastMeasuredTopY = topY;

            Vector3 lossy = weaponRoot.lossyScale;
            float invScale = 1f / Mathf.Max(0.25f, (Mathf.Abs(lossy.x) + Mathf.Abs(lossy.y) + Mathf.Abs(lossy.z)) / 3f);
            int targetLayer = weaponRoot.gameObject.layer;

            // Pick a visible renderer layer on weapon if different
            foreach (var r in weaponRoot.GetComponentsInChildren<Renderer>(false))
            {
                if (r != null && r.enabled && r.gameObject.activeInHierarchy && !r.gameObject.name.Contains("[LR2_Blood"))
                {
                    targetLayer = r.gameObject.layer;
                    break;
                }
            }

            for (int n = 0; n < count; n++)
            {
                while (_gunDrops.Count >= MaxGunDroplets)
                {
                    if (_gunDrops[0].go != null) Destroy(_gunDrops[0].go);
                    _gunDrops.RemoveAt(0);
                }

                Vector3 localPos = Vector3.zero;
                Vector3 localNormal = Vector3.up;
                bool valid = false;

                for (int attempt = 0; attempt < 12; attempt++)
                {
                    float tAlong = Random.Range(0.10f, 0.96f);
                    Vector3 spine = Vector3.Lerp(spineStart, spineEnd, tAlong);

                    // 85% directly along the top rail / upper receiver / slide top, 15% on the upper-left bevel (0 under barrel)
                    bool placeOnTop = Random.value < 0.85f;
                    if (placeOnTop)
                    {
                        float rx = Random.Range(-0.68f, 0.68f) * hullRadiusX;
                        localNormal = new Vector3(rx / Mathf.Max(0.005f, hullRadiusX) * 0.25f, 1f, 0f).normalized;
                        localPos = new Vector3(spine.x + rx, topY + 0.0012f, spine.z);
                    }
                    else
                    {
                        float rx = -hullRadiusX * Random.Range(0.75f, 0.96f);
                        float yBevel = topY - Random.Range(0.002f, 0.008f);
                        localNormal = new Vector3(-0.62f, 0.78f, 0f).normalized;
                        localPos = new Vector3(spine.x + rx, yBevel, spine.z);
                    }

                    // Keep clear of AimPoint ocular sight axis during ADS
                    if (Vector3.Distance(localPos, aimLocal) < 0.075f) continue;

                    // Verify live camera clearance: strictly ahead of lens (>= 22 cm forward) so drops never float across screen
                    Vector3 worldPos = weaponRoot.TransformPoint(localPos);
                    Vector3 camLocal = camT.InverseTransformPoint(worldPos);
                    if (camLocal.z >= 0.22f && Mathf.Sqrt(camLocal.x * camLocal.x + camLocal.y * camLocal.y + camLocal.z * camLocal.z) >= 0.24f)
                    {
                        valid = true;
                        break;
                    }
                }

                if (!valid) continue;

                bool bead = Random.value < 0.58f;
                var go = new GameObject(bead ? "[LR2_BloodBead]" : "[LR2_BloodSplat]");
                go.layer = targetLayer;
                go.transform.SetParent(weaponRoot, false);
                go.transform.localPosition = localPos + localNormal * 0.0008f;

                Vector3 lookDir = bead ? localNormal : -localNormal;
                Vector3 upHint = Mathf.Abs(Vector3.Dot(lookDir, Vector3.up)) > 0.95f ? fwd : Vector3.up;
                go.transform.localRotation = Quaternion.LookRotation(lookDir, upHint) * Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));

                var mfDrop = go.AddComponent<MeshFilter>();
                mfDrop.sharedMesh = bead ? _sphereMesh : _quadMesh;
                var mrDrop = go.AddComponent<MeshRenderer>();
                mrDrop.sharedMaterial = bead ? _gunBeadMat : _gunSplatMat;
                mrDrop.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mrDrop.receiveShadows = true;

                float worldRadius = bead
                    ? Random.Range(0.0032f, 0.0062f) * Mathf.Lerp(0.85f, 1.25f, severity)
                    : Random.Range(0.0085f, 0.0175f) * Mathf.Lerp(0.85f, 1.25f, severity);

                float localBase = worldRadius * invScale;
                go.transform.localScale = bead
                    ? new Vector3(localBase, localBase * Random.Range(0.85f, 1.2f), localBase * 0.34f)
                    : new Vector3(localBase, localBase * Random.Range(0.85f, 1.2f), localBase);

                Vector3 hostGravity = weaponRoot.InverseTransformDirection(Vector3.down).normalized;
                Vector3 slip = Vector3.ProjectOnPlane(hostGravity, localNormal);
                if (slip.sqrMagnitude > 0.01f) slip.Normalize();
                else slip = Vector3.zero;

                _gunDrops.Add(new GunDroplet
                {
                    go = go,
                    host = weaponRoot,
                    baseLocalPos = go.transform.localPosition,
                    localSlipDir = slip,
                    baseScale = localBase,
                    slideSpeed = bead ? Random.Range(0.00035f, 0.0009f) * invScale : 0f,
                    maxSlide = Random.Range(0.002f, 0.0045f) * invScale,
                    age = 0f,
                    lifetime = Random.Range(14f, 24f),
                });
            }
        }

        private static Transform FindDeepChild(Transform root, string exactName)
        {
            if (root == null) return null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t != null && t.name == exactName) return t;
            return null;
        }

        private void UpdateGunDroplets(float dt)
        {
            for (int i = _gunDrops.Count - 1; i >= 0; i--)
            {
                var d = _gunDrops[i];
                if (d.go == null || d.host == null)
                {
                    _gunDrops.RemoveAt(i);
                    continue;
                }

                d.age += dt;
                if (d.age >= d.lifetime)
                {
                    Destroy(d.go);
                    _gunDrops.RemoveAt(i);
                    continue;
                }

                if (d.slideSpeed > 0f && d.localSlipDir.sqrMagnitude > 0.01f)
                {
                    float dist = Mathf.Min(d.maxSlide, d.age * d.slideSpeed);
                    d.go.transform.localPosition = d.baseLocalPos + d.localSlipDir * dist;
                }

                float shrinkStart = d.lifetime * 0.65f;
                if (d.age > shrinkStart)
                {
                    float remain = Mathf.Clamp01(1f - (d.age - shrinkStart) / (d.lifetime - shrinkStart));
                    bool bead = d.go.name.Contains("Bead");
                    float s = d.baseScale * remain;
                    d.go.transform.localScale = bead ? new Vector3(s, s, s * 0.34f) : new Vector3(s, s, s);
                }

                _gunDrops[i] = d;
            }
        }

        private void ClearGunDroplets()
        {
            foreach (var d in _gunDrops)
                if (d.go != null) Destroy(d.go);
            _gunDrops.Clear();
        }

        // ------------------------------------------------------------------ procedural textures & meshes
        private void BuildTexturesAndMaterials()
        {
            _screenTexs = new[]
            {
                CreateDropletTexture(128, 11),
                CreateDropletTexture(128, 37),
                CreateDropletTexture(128, 73),
            };
            _gunSplatTex = CreateDropletTexture(128, 109);

            var tmpQ = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _quadMesh = tmpQ.GetComponent<MeshFilter>().sharedMesh;
            Destroy(tmpQ);

            var tmpS = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sphereMesh = tmpS.GetComponent<MeshFilter>().sharedMesh;
            Destroy(tmpS);

            Shader lit = Shader.Find("HDRP/Lit") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

            _gunBeadMat = new Material(lit);
            _gunBeadMat.hideFlags = HideFlags.HideAndDontSave;
            if (_gunBeadMat.HasProperty("_BaseColor")) _gunBeadMat.SetColor("_BaseColor", new Color(0.58f, 0.018f, 0.018f, 1f));
            if (_gunBeadMat.HasProperty("_Color")) _gunBeadMat.SetColor("_Color", new Color(0.58f, 0.018f, 0.018f, 1f));
            if (_gunBeadMat.HasProperty("_Smoothness")) _gunBeadMat.SetFloat("_Smoothness", 0.94f);
            if (_gunBeadMat.HasProperty("_Metallic")) _gunBeadMat.SetFloat("_Metallic", 0.04f);
            if (_gunBeadMat.HasProperty("_EmissiveColor")) _gunBeadMat.SetColor("_EmissiveColor", Color.black);

            _gunSplatMat = new Material(lit);
            _gunSplatMat.hideFlags = HideFlags.HideAndDontSave;
            if (_gunSplatMat.HasProperty("_BaseColor")) _gunSplatMat.SetColor("_BaseColor", new Color(0.55f, 0.018f, 0.018f, 1f));
            if (_gunSplatMat.HasProperty("_Color")) _gunSplatMat.SetColor("_Color", new Color(0.55f, 0.018f, 0.018f, 1f));
            if (_gunSplatMat.HasProperty("_BaseColorMap")) _gunSplatMat.SetTexture("_BaseColorMap", _gunSplatTex);
            if (_gunSplatMat.HasProperty("_MainTex")) _gunSplatMat.SetTexture("_MainTex", _gunSplatTex);
            if (_gunSplatMat.HasProperty("_Smoothness")) _gunSplatMat.SetFloat("_Smoothness", 0.90f);
            if (_gunSplatMat.HasProperty("_Metallic")) _gunSplatMat.SetFloat("_Metallic", 0.02f);
            if (_gunSplatMat.HasProperty("_EmissiveColor")) _gunSplatMat.SetColor("_EmissiveColor", Color.black);
            if (_gunSplatMat.HasProperty("_AlphaCutoffEnable")) _gunSplatMat.SetFloat("_AlphaCutoffEnable", 1f);
            if (_gunSplatMat.HasProperty("_AlphaCutoff")) _gunSplatMat.SetFloat("_AlphaCutoff", 0.30f);
            if (_gunSplatMat.HasProperty("_DoubleSidedEnable")) _gunSplatMat.SetFloat("_DoubleSidedEnable", 1f);
            _gunSplatMat.EnableKeyword("_ALPHATEST_ON");
            _gunSplatMat.EnableKeyword("_DOUBLESIDED_ON");
        }

        private static float SmoothStep01(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / Mathf.Max(0.0001f, edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        private static Texture2D CreateDropletTexture(int size, int seed)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.wrapMode = TextureWrapMode.Clamp;

            var rng = new System.Random(seed);
            int lobeCount = 9;
            var lobes = new Vector3[lobeCount];
            for (int i = 0; i < lobeCount; i++)
            {
                float a = (float)i / lobeCount * Mathf.PI * 2f + (float)rng.NextDouble() * 0.55f;
                float d = (0.14f + (float)rng.NextDouble() * 0.28f);
                lobes[i] = new Vector3(Mathf.Cos(a) * d, Mathf.Sin(a) * d, 0.10f + (float)rng.NextDouble() * 0.16f);
            }

            var pixels = new Color32[size * size];
            for (int py = 0; py < size; py++)
            {
                float v = (py + 0.5f) / size * 2f - 1f;
                for (int px = 0; px < size; px++)
                {
                    float u = (px + 0.5f) / size * 2f - 1f;

                    float field = Mathf.Clamp01(1f - Mathf.Sqrt(u * u + v * v) / 0.44f);
                    for (int i = 0; i < lobeCount; i++)
                    {
                        float dx = u - lobes[i].x;
                        float dy = v - lobes[i].y;
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        field = Mathf.Max(field, Mathf.Clamp01(1f - r / lobes[i].z));
                    }

                    float alpha = SmoothStep01(0.16f, 0.36f, field);
                    float rim = Mathf.Clamp01(1f - Mathf.Abs(field - 0.28f) * 4f) * alpha;
                    float specDx = u + 0.13f;
                    float specDy = v - 0.14f;
                    float spec = Mathf.Clamp01(1f - Mathf.Sqrt(specDx * specDx + specDy * specDy) / 0.16f);
                    spec = Mathf.Pow(spec, 3f) * alpha;

                    float rCol = Mathf.Clamp01(0.62f + spec * 0.25f + rim * 0.14f);
                    float gCol = Mathf.Clamp01(0.025f + spec * 0.35f);
                    float bCol = Mathf.Clamp01(0.03f + spec * 0.35f);

                    pixels[py * size + px] = new Color(rCol, gCol, bCol, alpha);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }
    }
}
