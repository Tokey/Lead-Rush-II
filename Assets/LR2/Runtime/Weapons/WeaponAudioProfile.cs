using LR2.Audio;
using UnityEngine;

namespace LR2.Weapons
{
    /// <summary>
    /// Layered weapon audio, the way modern shooters do it: the shooter hears a punchy close shot + mechanical
    /// layer + environmental tail; everyone else hears distance-appropriate versions (close/med/far/distant).
    /// </summary>
    [CreateAssetMenu(menuName = "LR2/Weapons/Weapon Audio Profile", fileName = "WeaponAudio")]
    public class WeaponAudioProfile : ScriptableObject
    {
        [Header("Shooter (local player)")]
        public SoundSet firePlayer = new SoundSet(AudioCategory.Weapons, 1f, 40f, 0.15f);
        public SoundSet fireFirstShot = new SoundSet(AudioCategory.Weapons, 1f, 40f, 0.15f);
        public SoundSet fireMech = new SoundSet(AudioCategory.Weapons, 0.75f, 10f, 0.15f);
        public SoundSet fireMechAds = new SoundSet(AudioCategory.Weapons, 0.75f, 10f, 0.15f);
        public SoundSet fireTail = new SoundSet(AudioCategory.Weapons, 0.7f, 120f, 0.35f);
        [Tooltip("Tail used when the shooter is under a roof (warehouse, interiors).")]
        public SoundSet fireTailInterior = new SoundSet(AudioCategory.Weapons, 0.75f, 80f, 0.35f);

        [Header("Suppressed Modular Layer (B136m)")]
        public SoundSet firePlayerSuppressed = new SoundSet(AudioCategory.Weapons, 0.92f, 25f, 0.15f);
        public SoundSet fireNpcSuppressed = new SoundSet(AudioCategory.Weapons, 0.82f, 35f, 1f);
        public SoundSet fireTailSuppressed = new SoundSet(AudioCategory.Weapons, 0.42f, 45f, 0.35f);
        public SoundSet fireTailInteriorSuppressed = new SoundSet(AudioCategory.Weapons, 0.48f, 35f, 0.35f);

        public SoundSet TailFor(bool indoors) => indoors && fireTailInterior.IsValid ? fireTailInterior : fireTail;
        public SoundSet SuppressedTailFor(bool indoors) => indoors && fireTailInteriorSuppressed.IsValid ? fireTailInteriorSuppressed : fireTailSuppressed;

        [Header("Others (3D, by distance)")]
        public SoundSet fireNpcClose = new SoundSet(AudioCategory.Weapons, 1f, 45f, 1f);
        public SoundSet fireNpcMed = new SoundSet(AudioCategory.Weapons, 1f, 90f, 1f);
        public SoundSet fireNpcFar = new SoundSet(AudioCategory.Weapons, 1f, 160f, 1f);
        public SoundSet fireNpcDistant = new SoundSet(AudioCategory.Weapons, 0.9f, 300f, 1f);
        [Tooltip("Distance thresholds (m) for close / med / far versions.")]
        public Vector3 npcDistanceBands = new Vector3(15f, 35f, 70f);

        [Header("Handling")]
        public SoundSet dryFire = new SoundSet(AudioCategory.Weapons, 0.8f, 12f, 1f);
        public SoundSet magOut = new SoundSet(AudioCategory.Weapons, 0.85f, 15f, 1f);
        public SoundSet magIn = new SoundSet(AudioCategory.Weapons, 0.9f, 15f, 1f);
        public SoundSet boltCharge = new SoundSet(AudioCategory.Weapons, 0.9f, 15f, 1f);
        public SoundSet equip = new SoundSet(AudioCategory.Weapons, 0.7f, 12f, 1f);
        public SoundSet fireModeSwitch = new SoundSet(AudioCategory.Weapons, 0.6f, 8f, 1f);
        public SoundSet shellCasings = new SoundSet(AudioCategory.Sfx, 0.35f, 10f, 1f);

        [Header("Reload timing (normalised 0..1 of the reload)")]
        [Range(0f, 1f)] public float magOutAt = 0.13f;
        [Range(0f, 1f)] public float magInAt = 0.43f;
        [Range(0f, 1f)] public float boltAt = 0.63f;
        [Tooltip("Normalized end point for empty reloads so we blend back cleanly right after bolt rack before tail gun rotation (B136k).")]
        [Range(0.55f, 1f)] public float emptyReloadEndAt = 0.82f;

        public SoundSet NpcShotForDistance(float distance)
        {
            if (distance <= npcDistanceBands.x && fireNpcClose.IsValid) return fireNpcClose;
            if (distance <= npcDistanceBands.y && fireNpcMed.IsValid) return fireNpcMed;
            if (distance <= npcDistanceBands.z && fireNpcFar.IsValid) return fireNpcFar;
            if (fireNpcDistant.IsValid) return fireNpcDistant;
            return fireNpcClose.IsValid ? fireNpcClose : firePlayer;
        }
    }
}
