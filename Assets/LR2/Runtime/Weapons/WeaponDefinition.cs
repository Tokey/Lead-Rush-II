using System;
using LR2.Combat;
using UnityEngine;

namespace LR2.Weapons
{
    public enum WeaponClass { AssaultRifle, SMG, Pistol, Sniper, Shotgun, Melee }

    [Serializable]
    public struct DamageStep
    {
        [Tooltip("Damage applies up to this distance (metres).")]
        public float maxDistance;
        public float damage;
    }

    /// <summary>
    /// All gameplay numbers of a gun in one Inspector-friendly asset (damage model, handling, spread, recoil feel,
    /// ammo, audio/FX references). Shared by the player and bots so both shoot by the same rules.
    /// </summary>
    [CreateAssetMenu(menuName = "LR2/Weapons/Weapon Definition", fileName = "WeaponDefinition")]
    public class WeaponDefinition : ScriptableObject
    {
        [Header("Identity")]
        public string displayName = "AK-12";
        public string shortName = "AK-12";
        public Sprite icon;
        public WeaponClass weaponClass = WeaponClass.AssaultRifle;

        [Header("Fire")]
        [Tooltip("Rounds per minute.")]
        public float rpm = 700f;
        public bool allowAuto = true;
        public bool allowBurst;
        [Min(1)] public int burstCount = 3;
        [Min(1)] public int pellets = 1;

        [Header("Ammo")]
        [Min(1)] public int magazineSize = 30;
        [Min(0)] public int startingReserve = 120;
        [Min(0)] public int maxReserve = 210;

        [Header("Damage (CoD-style tiers)")]
        public DamageStep[] damageSteps =
        {
            new DamageStep { maxDistance = 22f, damage = 32f },
            new DamageStep { maxDistance = 45f, damage = 26f },
            new DamageStep { maxDistance = 999f, damage = 22f },
        };
        [Range(1f, 3f)] public float headMultiplier = 1.5f;
        [Range(0.5f, 2f)] public float neckMultiplier = 1.2f;
        [Range(0.5f, 2f)] public float upperTorsoMultiplier = 1.1f;
        [Range(0.5f, 2f)] public float lowerTorsoMultiplier = 1f;
        [Range(0.3f, 1.5f)] public float limbMultiplier = 0.9f;
        [Tooltip("Max distance the round travels.")]
        public float maxRange = 250f;
        [Tooltip("Distance bots prefer to fight at with this weapon.")]
        public float effectiveRange = 30f;

        [Header("Penetration")]
        [Tooltip("Max thickness of penetrable material (wood/thin metal/glass) the round can pass, in metres.")]
        public float penetrationDepth = 0.25f;
        [Range(0f, 1f)] public float penetrationDamageScale = 0.65f;

        [Header("Handling")]
        [Tooltip("Time from sprint to being able to fire (s).")]
        public float sprintToFireTime = 0.17f;
        [Tooltip("Aim-down-sight in/out transition time (s); drives 3D weapon ADS pose, camera FOV zoom, and spread blend.")]
        public float adsTime = 0.15f;
        [Tooltip("Field of view multiplier while aiming (0.8 = 1.25x zoom).")]
        [Range(0.3f, 1f)] public float adsFovMultiplier = 0.8f;
        public float equipTime = 0.55f;
        [Tooltip("Partial (tactical) reload time if the clip has no animation.")]
        public float reloadTime = 2.2f;
        [Tooltip("Empty reload time if the clip has no animation.")]
        public float emptyReloadTime = 2.7f;

        [Header("Spread (degrees, cone half-angle)")]
        public float hipSpread = 2.4f;
        public float adsSpread = 0.08f;
        public float moveSpreadAdd = 1.6f;
        public float airSpreadAdd = 5f;
        [Range(0.3f, 1f)] public float crouchSpreadMultiplier = 0.75f;
        [Tooltip("Spread added per shot (bloom) while hip-firing.")]
        public float bloomPerShot = 0.4f;
        public float maxBloom = 2.8f;
        [Tooltip("Bloom recovered per second.")]
        public float bloomRecovery = 7f;

        [Header("Camera feel")]
        [Tooltip("Extra camera kick per shot (pitch up, yaw random) on top of the Kinemation recoil pattern.")]
        public Vector2 cameraKick = new Vector2(0.18f, 0.12f);
        [Range(0f, 1f)] public float adsKickMultiplier = 0.5f;

        [Header("Recoil pattern (Kinemation camera climb)")]
        [Tooltip("Scales the vertical climb of the Kinemation recoil pattern per shot (the demo AK pattern climbs 3-4 deg/shot, far too much).")]
        [Range(0f, 2f)] public float recoilPatternScale = 1f;
        [Tooltip("Scales the horizontal drift of the Kinemation recoil pattern per shot.")]
        [Range(0f, 2f)] public float recoilPatternHorizontalScale = 1f;
        [Tooltip("While aiming down sights, the recoil pattern AND the extra camera kick are multiplied by this.")]
        [Range(0.2f, 1f)] public float adsRecoilMultiplier = 0.55f;

        [Header("Noise / AI")]
        public float audibleRadius = 70f;
        public bool suppressed;

        [Header("Presentation")]
        public WeaponAudioProfile audio;
        [Tooltip("Spawn a visible tracer every N rounds (0 = never).")]
        public int tracerEvery = 3;
        public GameObject shellPrefab;
        public GameObject muzzleFlashPrefab;

        [Header("First-person grip offsets (B49)")]
        [Tooltip("Right (trigger) hand offset in gun frame (x right, y up, z forward).")]
        public Vector3 rightGripOffset = new Vector3(0.006f, -0.004f, -0.075f);
        [Tooltip("Left (support) hand offset on handguard in gun frame.")]
        public Vector3 leftGripOffset = new Vector3(0f, -0.036f, 0f);

        public float SecondsBetweenShots => 60f / Mathf.Max(1f, rpm);

        public float DamageAtDistance(float meters)
        {
            if (damageSteps == null || damageSteps.Length == 0) return 25f;
            foreach (var step in damageSteps)
                if (meters <= step.maxDistance) return step.damage;
            return damageSteps[damageSteps.Length - 1].damage;
        }

        public float ZoneMultiplier(HitZone zone)
        {
            switch (zone)
            {
                case HitZone.Head: return headMultiplier;
                case HitZone.Neck: return neckMultiplier;
                case HitZone.UpperTorso: return upperTorsoMultiplier;
                case HitZone.LowerTorso: return lowerTorsoMultiplier;
                case HitZone.Arm:
                case HitZone.Leg: return limbMultiplier;
                default: return 1f;
            }
        }
    }
}
