// B136: Kinemation demo MeleeWeapon (2025) with LR2 damage. Serialized names match the demo component so the builder's
// field copy carries the knife's animations (attack, equip, unequip) over.
using KINEMATION.FPSAnimationFramework.Runtime.Playables;
using LR2.Audio;
using LR2.Characters;
using LR2.Combat;
using LR2.Core;
using LR2.FX;
using UnityEngine;

namespace LR2.Weapons
{
    public class LR2Melee : LR2Item
    {
        [SerializeField] private FPSAnimationAsset meleeAttackAnimation;
        [SerializeField, Min(0f)] private float meleeAttackDelay = 0f;
        [SerializeField] protected FPSAnimationAsset equipClip;
        [SerializeField] protected FPSAnimationAsset unEquipClip;

        [Header("LR2 melee")]
        [Tooltip("150 = one stab kills through armour (100 HP + 50 armour), like a CoD knife.")]
        [SerializeField] private float damage = 150f;
        [SerializeField] private float range = 1.9f;
        [SerializeField] private float radius = 0.32f;
        [Tooltip("Seconds from the button press to the blade's contact frame (at attackRate 1).")]
        [SerializeField] private float hitDelay = 0.2f;
        [SerializeField] private float cooldown = 0.6f;
        [Tooltip("Attack play rate (LR2 keeps Kinemation's actions short).")]
        [SerializeField, Range(0.5f, 2f)] private float attackRate = 1.3f;

        public override bool SupportsAds => false;
        public override bool IsBusy => Time.time < _busyUntil;
        public override string DisplayName => "Knife";

        private IPlayablesController _playables;
        private FPSAnimationAsset _attackRuntime;
        private float _nextAttack, _hitAt = -1f, _busyUntil;

        public override void OnEquip(SoldierController owner)
        {
            if (owner == null) return;
            base.OnEquip(owner);
            EnsureEntityAimPoint();
            _playables = owner.Playables;
            var controllerAnimator = owner.Animator;
            if (_playables != null && overrideController != null && controllerAnimator != null &&
                overrideController != controllerAnimator.runtimeAnimatorController)
                _playables.UpdateAnimatorController(overrideController);
            if (owner.FPSAnimator != null) owner.FPSAnimator.LinkAnimatorProfile(gameObject);
            _hitAt = -1f;
            if (equipClip != null && _playables != null) _playables.PlayAnimation(equipClip);
            else if (owner.FPSAnimator != null) owner.FPSAnimator.LinkAnimatorLayer(owner.OwnMotion(equipMotion));
            _nextAttack = Time.time + Mathf.Max(meleeAttackDelay, 0.25f);
        }

        public override void OnUnEquip()
        {
            _hitAt = -1f;
            if (unEquipClip != null && _playables != null) _playables.PlayAnimation(unEquipClip);
            else if (Owner != null && Owner.FPSAnimator != null) Owner.FPSAnimator.LinkAnimatorLayer(Owner.OwnMotion(unEquipMotion));
        }

        public override bool OnFirePressed()
        {
            if (Owner == null || Time.time < _nextAttack || !FPSAnimationAsset.IsValid(meleeAttackAnimation)) return false;
            if (_attackRuntime == null)
            {
                _attackRuntime = Instantiate(meleeAttackAnimation);
                _attackRuntime.hideFlags = HideFlags.HideAndDontSave;
                var bt = _attackRuntime.blendTime;
                bt.rateScale = attackRate;
                _attackRuntime.blendTime = bt;
            }
            if (_playables != null) _playables.PlayAnimation(_attackRuntime, 0f);
            float rate = Mathf.Max(0.1f, attackRate);
            _hitAt = Time.time + hitDelay / rate;
            _nextAttack = Time.time + Mathf.Max(cooldown / rate, meleeAttackDelay);
            _busyUntil = _nextAttack;
            var s = LR2CommonSounds.Instance;
            if (s != null) LR2Audio.PlayAt(s.weaponSwap, transform.position, 0.8f, 0f, false, 1.35f); // blade swish
            return true;
        }

        public override void Tick(float deltaTime)
        {
            if (_hitAt > 0f && Time.time >= _hitAt)
            {
                _hitAt = -1f;
                Strike();
            }
        }

        public override void CancelActions() { _hitAt = -1f; }

        private void Strike()
        {
            var owner = Owner;
            if (owner == null || owner.IsDead) return;
            Vector3 origin = owner.AimOrigin, dir = owner.AimDirection;
            int mask = LR2Layers.ToMask(LR2Layers.Hitbox) | LR2Layers.EnvironmentMask;
            var hits = Physics.SphereCastAll(origin, radius, dir, range, mask, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                var hb = hit.collider.GetComponent<Hitbox>();
                if (hb != null)
                {
                    if (hb.Combatant == owner.Combatant) continue; // own body
                    Vector3 point = hit.point == Vector3.zero ? hit.collider.bounds.center : hit.point;
                    var info = new DamageInfo
                    {
                        amount = damage,
                        type = DamageType.Melee,
                        attacker = owner.Combatant,
                        source = gameObject,
                        weaponName = "Knife",
                        point = point,
                        normal = -dir,
                        direction = dir,
                        distance = hit.distance,
                        force = 40f
                    };
                    hb.ApplyDamage(ref info);
                    if (!LR2.World.KillcamDirector.IsPlaying) ImpactSystem.SpawnBlood(point, -dir, dir, false);
                    GameEvents.RaiseNoise(owner.Combatant, point, 8f, NoiseType.Impact);
                    return;
                }
                // a wall or prop first: the blade stops there
                ImpactSystem.SpawnBulletImpact(hit.point, hit.normal, dir, SurfaceDetector.Detect(hit.collider), false);
                GameEvents.RaiseNoise(owner.Combatant, hit.point, 6f, NoiseType.Impact);
                return;
            }
        }

        private void OnDestroy()
        {
            if (_attackRuntime != null) Destroy(_attackRuntime);
        }
    }
}
