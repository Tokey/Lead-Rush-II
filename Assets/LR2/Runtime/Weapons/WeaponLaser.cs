using LR2.Combat;
using LR2.Core;
using UnityEngine;

namespace LR2.Weapons
{
    /// <summary>Low-intensity weapon laser shared by player and bot firearms.</summary>
    [DisallowMultipleComponent]
    public sealed class WeaponLaser : MonoBehaviour
    {
        private const float MaxDistance = 120f;
        // Wider than a physically thin beam so the faction color remains readable in a busy HDRP scene.
        private const float BeamWidth = 0.0055f;
        private static readonly RaycastHit[] Hits = new RaycastHit[16];

        private LR2Firearm _firearm;
        private Transform _externalMuzzle;
        private Transform _ignoreRoot;
        private bool _externalIsPlayer;
        private LineRenderer _beam;
        private Transform _dot;
        private Renderer _dotRenderer;
        private Material _beamMaterial;
        private Material _dotMaterial;
        private bool _appliedColorValid;
        private bool _appliedIsPlayer;

        // B113 (user 11-19 "add GI to the laser, laser should light up area too when hitting a surface like real laser"): a small
        // shadowless point light just off the hit point tints the surface around the dot, so the dot lights its surroundings
        // in dark rooms and at night and fades out in daylight like a real laser. HDRP's SSGI, where a profile enables it,
        // bounces it further.
        private Light _spill;
        private const float SpillLumens = 60f;
        private const float SpillRange = 1.35f;

        private static readonly Color PlayerBeamColor = new Color(0.05f, 1f, 0.22f, 0.96f);
        private static readonly Color PlayerDotColor = new Color(0.08f, 1f, 0.28f, 0.95f);
        private static readonly Color EnemyBeamColor = new Color(1f, 0.08f, 0.025f, 0.94f);
        private static readonly Color EnemyDotColor = new Color(1f, 0.04f, 0.01f, 0.92f);

        private void Awake()
        {
            _firearm = GetComponent<LR2Firearm>();
            var beamObject = new GameObject("LaserBeam");
            beamObject.transform.SetParent(transform, false);
            _beam = beamObject.AddComponent<LineRenderer>();
            _beam.positionCount = 2;
            // B106 (user 11-19 "improve the look of the laser"): soft additive core instead of a solid opaque line; bright at the
            // muzzle, fading with distance like a real visible laser in haze
            _beam.startWidth = BeamWidth;
            _beam.endWidth = BeamWidth * 0.6f;
            _beam.textureMode = LineTextureMode.Stretch;
            var fade = new Gradient();
            fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                         new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.55f, 0.35f), new GradientAlphaKey(0.18f, 1f) });
            _beam.colorGradient = fade;
            _beam.numCapVertices = 2;
            _beam.numCornerVertices = 2;
            _beam.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _beam.receiveShadows = false;
            _beamMaterial = CreateGlowMaterial(EnemyBeamColor, BeamTexture(), 1.6f) ?? CreateMaterial(EnemyBeamColor);
            _beam.sharedMaterial = _beamMaterial;

            // B106: a camera-facing glow (hot core + soft halo) instead of an opaque 3 cm ball
            var dotObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
            dotObject.name = "LaserDot";
            dotObject.transform.SetParent(transform, false);
            dotObject.transform.localScale = Vector3.one * 0.07f;
            var collider = dotObject.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
            _dot = dotObject.transform;
            _dotRenderer = dotObject.GetComponent<Renderer>();
            _dotMaterial = CreateGlowMaterial(EnemyDotColor, DotTexture(), 2.6f) ?? CreateMaterial(EnemyDotColor);
            _dotRenderer.sharedMaterial = _dotMaterial;
            SetVisible(false);
        }

        private void OnDestroy()
        {
            if (_beamMaterial != null) Destroy(_beamMaterial);
            if (_dotMaterial != null) Destroy(_dotMaterial);
        }

        public void SetExternalMuzzle(Transform muzzle, Transform ignoreRoot = null, bool isPlayerTeam = false)
        {
            _externalMuzzle = muzzle;
            _ignoreRoot = ignoreRoot;
            _externalIsPlayer = isPlayerTeam;
            _appliedColorValid = false;
        }

        private bool IsPlayerTeamLaser()
        {
            if (_externalMuzzle != null) return _externalIsPlayer;
            if (_firearm == null || _firearm.Owner == null || _firearm.Owner.Combatant == null) return false;
            var c = _firearm.Owner.Combatant;
            if (c.IsLocalPlayer) return true;
            var local = Combatant.FindLocalPlayer();
            return local != null ? !c.IsHostileTo(local) : !c.IsBot;
        }

        private TeamId _appliedTeam = (TeamId)(-1);

        /// <summary>B116: the beam has its owner's team colour (blue is always your team; FFA: you blue, the rest red).</summary>
        private TeamId LaserTeam()
        {
            if (_externalMuzzle != null) return _externalIsPlayer ? TeamId.Alpha : TeamId.Bravo;
            var c = _firearm != null && _firearm.Owner != null ? _firearm.Owner.Combatant : null;
            if (c == null) return TeamId.Bravo;
            return TeamUtil.ColorTeam(Combatant.FindLocalPlayer(), c);
        }

        private void SyncLaserColor()
        {
            var team = LaserTeam();
            if (_appliedColorValid && _appliedTeam == team) return;
            _appliedColorValid = true;
            _appliedTeam = team;
            _appliedIsPlayer = team == TeamId.Alpha;
            Color beam = TeamUtil.LaserColor(team);
            ApplyColor(_beamMaterial, beam);
            ApplyColor(_dotMaterial, new Color(Mathf.Min(1f, beam.r * 1.05f + 0.03f), Mathf.Min(1f, beam.g * 1.05f + 0.03f), Mathf.Min(1f, beam.b * 1.05f + 0.03f), 0.94f));
            EnsureSpill();
            if (_spill != null) _spill.color = new Color(beam.r, beam.g, beam.b, 1f);
        }

        private void EnsureSpill()
        {
            if (_spill != null) return;
            var go = new GameObject("LaserSpill");
            go.transform.SetParent(transform, false);
            _spill = go.AddComponent<Light>();
            _spill.type = LightType.Point;
            _spill.range = SpillRange;
            _spill.shadows = LightShadows.None;
            _spill.lightUnit = UnityEngine.Rendering.LightUnit.Lumen;
            _spill.intensity = SpillLumens;
            if (!go.TryGetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>(out _))
                go.AddComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>();
            _spill.enabled = false;
        }

        private static void ApplyColor(Material material, Color color)
        {
            if (material == null) return;
            if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", color);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", color * 5f);
        }

        private void LateUpdate()
        {
            bool active = _externalMuzzle != null ? (_externalMuzzle.gameObject.activeInHierarchy && LR2Settings.LaserEnabled)
                : (_firearm != null && LR2Settings.LaserEnabled && _firearm.gameObject.activeInHierarchy);
            if (!active)
            {
                SetVisible(false);
                return;
            }
            SyncLaserColor();

            // B136: the beam leaves the laser device's emitter (LR2Firearm.LaserOrigin), not the muzzle
            Transform muzzle = _externalMuzzle != null ? _externalMuzzle : _firearm.LaserOrigin;
            if (muzzle == null)
            {
                SetVisible(false);
                return;
            }

            bool fromDevice = muzzle.name == "LaserEmitter";
            Vector3 start = muzzle.position + muzzle.forward * (fromDevice ? 0.003f : 0.04f);
            Vector3 end = start + muzzle.forward * MaxDistance;
            bool hitFound = false;
            int hitCount = Physics.RaycastNonAlloc(start, muzzle.forward, Hits, MaxDistance, LR2Layers.BulletMask,
                QueryTriggerInteraction.Ignore);
            float nearest = MaxDistance;
            for (int i = 0; i < hitCount; i++)
            {
                var hit = Hits[i];
                if (hit.collider == null || IsOwnedCollider(hit.collider.transform)) continue;
                if (hit.distance < nearest)
                {
                    nearest = hit.distance;
                    end = hit.point;
                    hitFound = true;
                }
            }

            _beam.SetPosition(0, start);
            _beam.SetPosition(1, end);
            if (hitFound)
            {
                _dot.position = end - muzzle.forward * 0.006f;
                if (_spill != null) _spill.transform.position = end - muzzle.forward * 0.07f;
                var viewer = Camera.main;
                _dot.rotation = viewer != null ? Quaternion.LookRotation(viewer.transform.forward, viewer.transform.up)
                                               : Quaternion.LookRotation(-muzzle.forward, Vector3.up);
            }
            SetVisible(true, hitFound);
        }

        private bool IsOwnedCollider(Transform colliderTransform)
        {
            if (_ignoreRoot != null && colliderTransform.IsChildOf(_ignoreRoot)) return true;
            if (_firearm == null || _firearm.Owner == null) return colliderTransform.IsChildOf(transform.root);
            return colliderTransform.IsChildOf(_firearm.Owner.transform) || colliderTransform.IsChildOf(transform.root);
        }

        private void SetVisible(bool visible, bool showDot = false)
        {
            if (_beam != null) _beam.enabled = visible;
            if (_dotRenderer != null) _dotRenderer.enabled = visible && showDot;
            if (_spill != null) _spill.enabled = visible && showDot;
        }

        private static Texture2D _beamTex, _dotTex;

        private static Texture2D BeamTexture()
        {
            if (_beamTex != null) return _beamTex;
            const int w = 4, h = 32;
            _beamTex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                float v = (y + 0.5f) / h * 2f - 1f;
                float a = Mathf.Exp(-v * v * 9f) * 0.75f + Mathf.Exp(-v * v * 60f) * 0.25f; // soft halo + thin hot core
                for (int x = 0; x < w; x++) px[y * w + x] = new Color(1f, 1f, 1f, a);
            }
            _beamTex.SetPixels(px);
            _beamTex.Apply(false, true);
            return _beamTex;
        }

        private static Texture2D DotTexture()
        {
            if (_dotTex != null) return _dotTex;
            const int n = 64;
            _dotTex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                    float r2 = u * u + v * v;
                    float a = Mathf.Clamp01(Mathf.Exp(-r2 * 60f) + Mathf.Exp(-r2 * 8f) * 0.45f + Mathf.Exp(-r2 * 2.5f) * 0.12f);
                    float white = Mathf.Exp(-r2 * 90f);                        // white-hot centre, coloured halo
                    px[y * n + x] = new Color(1f, 1f, 1f, a) * new Color(1f, 1f, 1f, 1f) + new Color(white, white, white, 0f) * 0f;
                }
            _dotTex.SetPixels(px);
            _dotTex.Apply(false, true);
            return _dotTex;
        }

        /// <summary>B106: additive glow on the keyword-free LR2/ParticleUnlit shader (safe in player builds); null if it is missing.</summary>
        private static Material CreateGlowMaterial(Color color, Texture2D tex, float intensity)
        {
            var shader = Shader.Find("LR2/ParticleUnlit");
            if (shader == null) return null;
            var m = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            m.SetTexture("_MainTex", tex);
            m.SetColor("_Color", color);
            m.SetFloat("_Intensity", intensity);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            m.renderQueue = 3100;
            return m;
        }

        private static Material CreateMaterial(Color color)
        {
            Shader shader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Color");
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", color);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", color * 4f);
            if (material.HasProperty("_EmissiveIntensity")) material.SetFloat("_EmissiveIntensity", 4f);
            return material;
        }
    }

    [DefaultExecutionOrder(-500)]
    internal sealed class WeaponLaserBootstrap : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (FindAnyObjectByType<WeaponLaserBootstrap>() != null) return;
            var go = new GameObject("[LR2 Weapon Lasers]");
            DontDestroyOnLoad(go);
            go.AddComponent<WeaponLaserBootstrap>();
        }

        private void OnEnable()
        {
            GameEvents.CombatantSpawned += AttachToCombatant;
            AttachToAll();
        }

        private void OnDisable()
        {
            GameEvents.CombatantSpawned -= AttachToCombatant;
        }

        private static void AttachToAll()
        {
            var firearms = FindObjectsByType<LR2Firearm>(FindObjectsInactive.Include);
            for (int i = 0; i < firearms.Length; i++) Attach(firearms[i]);
        }

        private static void AttachToCombatant(Combatant combatant)
        {
            if (combatant == null) return;
            var firearms = combatant.GetComponentsInChildren<LR2Firearm>(true);
            for (int i = 0; i < firearms.Length; i++) Attach(firearms[i]);
        }

        private static void Attach(LR2Firearm firearm)
        {
            if (firearm != null && firearm.GetComponent<WeaponLaser>() == null)
                firearm.gameObject.AddComponent<WeaponLaser>();
        }
    }
}
