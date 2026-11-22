using System;
using LR2.Core;
using UnityEngine;

namespace LR2.Combat
{
    /// <summary>
    /// Call-of-Duty style health: 100 HP with fast full regeneration after a short delay without damage, plus an
    /// optional armour layer (soldiers: 50) that soaks damage first, never regenerates, cracks audibly when it breaks
    /// and is refilled at ammo stations (B74). Brief spawn protection ends as soon as the owner fires.
    /// </summary>
    [DisallowMultipleComponent]
    public class Health : MonoBehaviour, IDamageable
    {
        [Header("Health")]
        [SerializeField, Min(1f)] private float maxHealth = 100f;

        [Header("Armour (B74: soaks damage first, no regen, refilled at ammo stations)")]
        [Tooltip("0 = no armour (drones, props). Soldiers use 50: 100 HP + 50 armour = 150 effective (the user's 1.5x health request, 11-18 04:30, without a 150 readout).")]
        [SerializeField, Min(0f)] private float maxArmor = 0f;
        [Tooltip("Fire, falls and suicide skip the armour and hit health directly.")]
        [SerializeField] private bool environmentBypassesArmor = true;

        [Header("Regeneration")]
        [SerializeField] private bool regenerate = true;
        [Tooltip("Seconds without taking damage before health starts regenerating.")]
        [SerializeField, Min(0f)] private float regenDelay = 4f;
        [Tooltip("Health regenerated per second once regeneration starts.")]
        [SerializeField, Min(0f)] private float regenPerSecond = 55f;

        [Header("Protection")]
        [SerializeField, Min(0f)] private float spawnProtection = 1.5f;
        [SerializeField] private bool allowFriendlyFire;
        [Tooltip("If true the attacker can hurt itself (explosions).")]
        [SerializeField] private bool allowSelfDamage = true;

        public event Action<DamageInfo> Damaged;
        public event Action<DamageInfo> Died;
        public event Action Revived;
        public event Action RegenStarted;
        public event Action HealthReplenished;
        /// <summary>Raised once when a hit takes the armour from above zero to zero.</summary>
        public event Action<DamageInfo> ArmorBroken;
        /// <summary>Raised when armour is refilled (argument: amount added).</summary>
        public event Action<float> ArmorRestored;

        public float Current { get; private set; }
        public float Max => maxHealth;
        public float Normalized => maxHealth > 0f ? Current / maxHealth : 0f;
        public float Armor { get; private set; }
        public bool HasArmor => maxArmor > 0f;
        public float ArmorNormalized => maxArmor > 0f ? Armor / maxArmor : 0f;

        public float MaxArmor
        {
            get => maxArmor;
            set { maxArmor = Mathf.Max(0f, value); Armor = Mathf.Min(Armor, maxArmor); }
        }
        public bool IsAlive { get; private set; } = true;
        public bool IsInvulnerable => Time.time < _invulnerableUntil;
        public float LastDamageTime { get; private set; } = -999f;
        public DamageInfo LastDamage { get; private set; }
        public Combatant Owner { get; private set; }
        public bool CanBeDamaged => IsAlive && !IsInvulnerable;

        private float _invulnerableUntil;
        private bool _regenActive;
        private bool _needsReplenishSfx;

        public float MaxHealth
        {
            get => maxHealth;
            set { maxHealth = Mathf.Max(1f, value); Current = Mathf.Min(Current, maxHealth); }
        }

        public bool Regenerate { get => regenerate; set => regenerate = value; }

        private void Awake()
        {
            Owner = GetComponent<Combatant>();
            Current = maxHealth;
            Armor = maxArmor;
        }

        private void Update()
        {
            if (!IsAlive || !regenerate || Current >= maxHealth) return;
            if (Time.time - LastDamageTime < regenDelay) return;
            if (!_regenActive)
            {
                _regenActive = true;
                RegenStarted?.Invoke();
            }
            Current = Mathf.Min(maxHealth, Current + regenPerSecond * Time.deltaTime);
            if (Current >= maxHealth - 0.01f)
            {
                Current = maxHealth;
                _regenActive = false;
                if (_needsReplenishSfx)
                {
                    _needsReplenishSfx = false;
                    HealthReplenished?.Invoke();
                }
            }
        }

        public float ApplyDamage(ref DamageInfo info)
        {
            if (!IsAlive || info.amount <= 0f) return 0f;
            bool environmental = info.type == DamageType.Suicide || info.type == DamageType.Environment || info.type == DamageType.Fall;
            if (IsInvulnerable && !environmental) return 0f;

            if (info.attacker != null && Owner != null)
            {
                if (info.attacker == Owner)
                {
                    if (!allowSelfDamage) return 0f;
                }
                else if (!allowFriendlyFire && !TeamUtil.AreHostile(info.attacker, Owner))
                {
                    return 0f;
                }
            }

            info.victim = Owner;

            // Armour soaks first; overflow from the breaking hit carries into health (Warzone-style plates).
            float incoming = info.amount;
            float toArmor = 0f;
            bool armorBroke = false;
            if (Armor > 0f && !(environmental && environmentBypassesArmor))
            {
                toArmor = Mathf.Min(Armor, incoming);
                Armor -= toArmor;
                incoming -= toArmor;
                if (Armor <= 0.001f) { Armor = 0f; armorBroke = true; }
            }

            float applied = Mathf.Min(Current, incoming);
            Current -= incoming;
            LastDamageTime = Time.time;
            LastDamage = info;

            bool killed = Current <= 0.001f;
            if (killed)
            {
                _regenActive = false;
                _needsReplenishSfx = false;
            }
            else if (applied > 0f && Current < maxHealth - 1f)
            {
                _needsReplenishSfx = true;
                _regenActive = false;
            }
            float total = applied + toArmor;
            if (info.attacker != null && info.attacker != Owner)
                info.attacker.stats.damageDealt += total;

            Damaged?.Invoke(info);
            if (armorBroke) ArmorBroken?.Invoke(info);
            GameEvents.RaiseHit(new HitEvent
            {
                attacker = info.attacker,
                victim = Owner,
                damage = total,
                armorDamage = toArmor,
                armorBroken = armorBroke,
                zone = info.zone,
                type = info.type,
                killed = killed,
                point = info.point,
                direction = info.direction
            });

            if (killed) Die(info);
            return total;
        }

        public void Die(DamageInfo info)
        {
            if (!IsAlive) return;
            Current = 0f;
            IsAlive = false;
            _regenActive = false;
            _needsReplenishSfx = false;
            info.victim = Owner;
            if (Owner != null) Owner.LastDeathTime = Time.time;

            Died?.Invoke(info);
            GameEvents.RaiseKilled(new KillEvent
            {
                killer = info.attacker,
                victim = Owner,
                weaponName = info.weaponName,
                type = info.type,
                headshot = info.IsHeadshot,
                wallbang = info.penetrated,
                distance = info.distance,
                victimPosition = transform.position,
                direction = info.direction
            });
        }

        /// <summary>Kills the owner without an attacker (falling out of the map, admin, etc.).</summary>
        public void Kill(DamageType type = DamageType.Suicide)
        {
            Die(new DamageInfo { amount = Current, type = type, point = transform.position, direction = Vector3.down, weaponName = type.ToString() });
        }

        public void Heal(float amount)
        {
            if (!IsAlive) return;
            float before = Current;
            Current = Mathf.Min(maxHealth, Current + Mathf.Max(0f, amount));
            if (before < maxHealth - 0.5f && Current >= maxHealth - 0.01f)
            {
                _regenActive = false;
                _needsReplenishSfx = false;
                HealthReplenished?.Invoke();
            }
        }

        /// <summary>Refills armour (ammo stations). Returns the amount actually added (0 when full, dead or unarmoured).</summary>
        public float AddArmor(float amount)
        {
            if (!IsAlive || maxArmor <= 0f || amount <= 0f) return 0f;
            float before = Armor;
            Armor = Mathf.Min(maxArmor, Armor + amount);
            float added = Armor - before;
            if (added > 0.001f) ArmorRestored?.Invoke(added);
            return added;
        }

        public void Revive()
        {
            Current = maxHealth;
            Armor = maxArmor;
            IsAlive = true;
            _regenActive = false;
            _needsReplenishSfx = false;
            LastDamageTime = -999f;
            _invulnerableUntil = Time.time + spawnProtection;
            Revived?.Invoke();
        }

        public void SetInvulnerable(float seconds) => _invulnerableUntil = Time.time + Mathf.Max(0f, seconds);

        /// <summary>Firing ends spawn protection immediately (prevents spawn-protected trading).</summary>
        public void CancelSpawnProtection() => _invulnerableUntil = 0f;
    }
}
