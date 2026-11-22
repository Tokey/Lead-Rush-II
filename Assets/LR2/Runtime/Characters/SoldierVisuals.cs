using LR2.Combat;
using UnityEngine;

namespace LR2.Characters
{
    /// <summary>Friendly chevron above teammates (TDM readability). Hidden for the local player and enemies.</summary>
    public class SoldierVisuals : MonoBehaviour
    {
        [SerializeField] private Material friendlyMarkerMaterial;
        [SerializeField] private float markerHeight = 2.15f;
        [SerializeField] private float markerSize = 0.22f;
        [SerializeField] private float maxMarkerDistance = 60f;

        private Combatant _combatant;
        private Transform _marker;
        private Renderer _markerRenderer;

        [Header("Gear / uniforms")]
        [Tooltip("Uniform for the local player's side (also what you see on your own arms).")]
        [SerializeField] private Material friendlyUniform;
        [Tooltip("Uniform for the opposing side.")]
        [SerializeField] private Material enemyUniform;
        [Tooltip("Full face mask for allied forces (balaclava + skull look).")]
        [SerializeField] private Material faceMask;
        [Tooltip("Distinct face mask for hostile forces (dark red Marauder visor/balaclava).")]
        [SerializeField] private Material enemyFaceMask;
        [Tooltip("B136 (Kinemation robot): a body material whose name contains the key becomes the friendly or enemy tint. " +
                 "When set, these replace the SWAT uniform / mask swap above.")]
        [SerializeField] private TeamMaterialSwap[] teamSwaps = System.Array.Empty<TeamMaterialSwap>();
        private bool _uniformApplied;

        [System.Serializable]
        public struct TeamMaterialSwap
        {
            public string key;
            public Material friendly;
            public Material enemy;
        }

        public void SetTeamSwaps(TeamMaterialSwap[] swaps) => teamSwaps = swaps ?? System.Array.Empty<TeamMaterialSwap>();

        public Material FriendlyMarkerMaterial { get => friendlyMarkerMaterial; set => friendlyMarkerMaterial = value; }
        public void SetGear(Material friendly, Material enemy, Material mask, Material enemyMask = null)
        {
            friendlyUniform = friendly;
            enemyUniform = enemy;
            faceMask = mask;
            if (enemyMask != null) enemyFaceMask = enemyMask;
        }

        private void Awake() => _combatant = GetComponent<Combatant>();

        private void Start()
        {
            // Apply final faction uniform immediately on spawn frame so no default model texture flashes on frame 0
            if (!_uniformApplied) ApplyUniform(Combatant.FindLocalPlayer()); // team colours need no local player (B116)
        }

        /// <summary>Re-evaluates which uniform this soldier wears (call after team changes).</summary>
        public void RefreshUniform() => _uniformApplied = false;

        /// <summary>Applies baked uniform materials immediately (used both in prefab build and pre-reveal warmup).</summary>
        public void ApplyMaterialsNow(bool friendly)
        {
            if (teamSwaps != null && teamSwaps.Length > 0) { ApplySwaps(friendly, null); return; }
            ApplySet(friendly ? friendlyUniform : enemyUniform, friendly ? faceMask : (enemyFaceMask != null ? enemyFaceMask : faceMask));
        }

        /// <summary>B136: robot body/arms/legs to the team tint (weapons keep their own materials).</summary>
        private void ApplySwaps(bool friendly, TeamId? tint)
        {
            foreach (var r in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r.GetComponentInParent<LR2.Weapons.LR2Item>(true) != null) continue;
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    string n = mats[i].name.ToLowerInvariant();
                    foreach (var s in teamSwaps)
                    {
                        if (string.IsNullOrEmpty(s.key) || !n.Contains(s.key)) continue;
                        var m = friendly ? s.friendly : s.enemy;
                        if (m == null) break;
                        mats[i] = tint.HasValue ? Tint(m, tint.Value) : m;
                        changed = true;
                        break;
                    }
                }
                if (changed) r.sharedMaterials = mats;
            }
            _uniformApplied = true;
        }

        private void ApplySet(Material uniform, Material mask)
        {
            foreach (var r in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    string n = mats[i].name.ToLowerInvariant();
                    if ((n.Contains("head") || n.Contains("mask")) && mask != null) { mats[i] = mask; changed = true; }
                    else if ((n.Contains("body") || n.Contains("uniform")) && uniform != null) { mats[i] = uniform; changed = true; }
                }
                if (changed) r.sharedMaterials = mats;
            }
            _uniformApplied = true;
        }

        public static void ApplyAllNow()
        {
            var lp = Combatant.FindLocalPlayer();
            foreach (var sv in FindObjectsByType<SoldierVisuals>())
            {
                if (sv != null) sv.ApplyUniform(lp);
            }
        }

        // B116: uniforms follow the one colour rule: Alpha (always your team) navy, Bravo crimson, Charlie and Delta tinted
        // green and gold; in free-for-all you wear navy and everyone else crimson. AI vs AI has no local player, so the
        // choice no longer depends on one.
        private void ApplyUniform(Combatant local)
        {
            var team = TeamUtil.ColorTeam(local, _combatant);
            if (teamSwaps != null && teamSwaps.Length > 0)
            {
                bool tinted = team == TeamId.Charlie || team == TeamId.Delta;
                ApplySwaps(team == TeamId.Charlie || (!tinted && (team == TeamId.Alpha || (_combatant != null && _combatant.IsLocalPlayer))),
                    tinted ? team : (TeamId?)null);
                return;
            }
            if (team == TeamId.Charlie || team == TeamId.Delta)
            {
                var mask = team == TeamId.Charlie ? faceMask : (enemyFaceMask != null ? enemyFaceMask : faceMask);
                ApplySet(Tint(team == TeamId.Charlie ? friendlyUniform : enemyUniform, team), mask);
                return;
            }
            ApplyMaterialsNow(team == TeamId.Alpha || (_combatant != null && _combatant.IsLocalPlayer));
        }

        private static readonly System.Collections.Generic.Dictionary<(Material, TeamId), Material> Tinted =
            new System.Collections.Generic.Dictionary<(Material, TeamId), Material>();

        private static Material Tint(Material src, TeamId team)
        {
            if (src == null) return null;
            if (Tinted.TryGetValue((src, team), out var m) && m != null) return m;
            m = new Material(src) { name = src.name + "_" + team, hideFlags = HideFlags.DontSave };
            Color tint = team == TeamId.Charlie ? new Color(0.55f, 1f, 0.5f, 1f) : new Color(1f, 0.86f, 0.36f, 1f);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", m.GetColor("_BaseColor") * tint);
            else if (m.HasProperty("_Color")) m.SetColor("_Color", m.GetColor("_Color") * tint);
            Tinted[(src, team)] = m;
            return m;
        }

        private void LateUpdate()
        {
            if (!_uniformApplied) ApplyUniform(Combatant.FindLocalPlayer());
            var local = Combatant.FindLocalPlayer();
            bool show = local != null && _combatant != null && local != _combatant && _combatant.IsAlive
                        && !TeamUtil.AreHostile(local, _combatant) && friendlyMarkerMaterial != null;
            if (show && Vector3.Distance(local.EyePosition, transform.position) > maxMarkerDistance) show = false;

            if (show && _marker == null) CreateMarker();
            if (_markerRenderer != null) _markerRenderer.enabled = show;
            if (!show || _marker == null) return;

            _marker.position = transform.position + Vector3.up * markerHeight;
            var cam = Camera.main;
            if (cam != null)
            {
                _marker.rotation = Quaternion.LookRotation(_marker.position - cam.transform.position);
                float d = Vector3.Distance(cam.transform.position, _marker.position);
                _marker.localScale = Vector3.one * markerSize * Mathf.Clamp(d / 12f, 0.7f, 3f);
            }
        }

        private void CreateMarker()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "FriendlyMarker";
            Destroy(go.GetComponent<Collider>());
            go.layer = Core.LR2Layers.IgnoreRaycast;
            _marker = go.transform;
            _marker.SetParent(transform, true);
            _markerRenderer = go.GetComponent<Renderer>();
            _markerRenderer.sharedMaterial = friendlyMarkerMaterial;
            _markerRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _markerRenderer.receiveShadows = false;
        }
    }
}
