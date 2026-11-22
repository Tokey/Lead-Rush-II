using LR2.Audio;
using LR2.Characters;
using UnityEngine;

namespace LR2.Weapons
{
    /// <summary>
    /// B136: Kinemation's demo torch as an LR2 slot (key 4). The demo shipped only the mesh and a raised "Standing Torch Idle"
    /// clip; its animator profile is missing from the package, and that clip has no weapon-bone keys (sampled as a pose it left
    /// the torch at the soldier's feet). So the torch uses the knife's own profile and is gripped like the knife, upright,
    /// with a flickering warm light and a small flame. Fire toggles the flame.
    /// </summary>
    public class LR2Torch : LR2Item
    {
        [SerializeField] private Light torchLight;
        [SerializeField] private ParticleSystem flame;
        [SerializeField] private Renderer emissiveHead;
        [SerializeField] private float lumens = 1600f;
        [SerializeField, Range(0f, 0.5f)] private float flicker = 0.22f;
        [SerializeField] private float flickerSpeed = 9f;

        public override bool SupportsAds => false;
        public override string DisplayName => "Torch";
        public bool IsLit => _lit;

        private bool _lit = true;
        private float _seed;
        private float _nextToggle;

        private void Awake() => _seed = Random.value * 100f;

        public override void OnEquip(SoldierController owner)
        {
            if (owner == null) return;
            base.OnEquip(owner);
            EnsureEntityAimPoint();
            var playables = owner.Playables;
            var controllerAnimator = owner.Animator;
            if (playables != null && overrideController != null && controllerAnimator != null &&
                overrideController != controllerAnimator.runtimeAnimatorController)
                playables.UpdateAnimatorController(overrideController);
            if (owner.FPSAnimator != null)
            {
                owner.FPSAnimator.LinkAnimatorProfile(gameObject);
                owner.FPSAnimator.LinkAnimatorLayer(owner.OwnMotion(equipMotion));
            }
            ApplyLit();
        }

        public override void OnUnEquip()
        {
            if (Owner != null && Owner.FPSAnimator != null) Owner.FPSAnimator.LinkAnimatorLayer(Owner.OwnMotion(unEquipMotion));
        }

        public override bool OnFirePressed()
        {
            if (Time.time < _nextToggle) return false;
            _nextToggle = Time.time + 0.35f;
            _lit = !_lit;
            ApplyLit();
            var s = LR2CommonSounds.Instance;
            if (s != null) LR2Audio.PlayAt(s.weaponSwap, transform.position, 0.5f, 0f, false, _lit ? 0.7f : 0.55f);
            return true;
        }

        public override void RefillAmmo()
        {
            _lit = true; // a new life starts with a lit torch
            ApplyLit();
        }

        private void ApplyLit()
        {
            if (torchLight != null) torchLight.enabled = _lit;
            if (flame != null)
            {
                if (_lit && !flame.isPlaying) flame.Play(true);
                else if (!_lit) flame.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
            if (emissiveHead != null) emissiveHead.enabled = true;
        }

        private void Update()
        {
            if (!_lit || torchLight == null) return;
            float n = Mathf.PerlinNoise(_seed, Time.time * flickerSpeed) * 2f - 1f;
            torchLight.intensity = lumens * (1f + n * flicker);
        }
    }
}
