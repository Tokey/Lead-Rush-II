// Based on KINEMATION FPS Animation Framework demo FPSItem (2025); field names kept identical so demo prefab data
// carries over when the LR2 builder swaps components.
using KINEMATION.FPSAnimationFramework.Runtime.Core;
using KINEMATION.FPSAnimationFramework.Runtime.Layers.IkMotionLayer;
using LR2.Characters;
using UnityEngine;

namespace LR2.Weapons
{
    public abstract class LR2Item : MonoBehaviour
    {
        [SerializeField] protected RuntimeAnimatorController overrideController;
        [SerializeField] protected IkMotionLayerSettings equipMotion;
        [SerializeField] protected IkMotionLayerSettings unEquipMotion;

        [Header("LR2 Item")]
        [SerializeField] protected string displayNameOverride;

        public SoldierController Owner { get; protected set; }
        public virtual string DisplayName => string.IsNullOrEmpty(displayNameOverride) ? name.Replace("(Clone)", "").Trim() : displayNameOverride;

        /// <summary>True while the item blocks other actions (reloading, equipping...).</summary>
        public virtual bool IsBusy => false;

        public virtual void OnEquip(SoldierController owner) { Owner = owner; }
        public virtual void OnUnEquip() { }
        public virtual bool OnAimPressed() { return false; }
        public virtual bool OnAimReleased() { return false; }
        public virtual bool OnFirePressed() { return false; }
        public virtual bool OnFireReleased() { return false; }
        public virtual bool OnReload() { return false; }
        public virtual void OnCycleScope() { }
        public virtual void OnChangeFireMode() { }
        /// <summary>B136: Kinemation attachment editing. 1 = barrel, 2 = grip, anything else = the active sight group.</summary>
        public virtual void OnAttachmentChanged(int attachmentTypeIndex) { }
        /// <summary>B136: plays this item's grenade-throw animation; false when the item has none (knife, torch).</summary>
        public virtual bool OnGrenadeThrow() { return false; }
        /// <summary>B136: seconds the throw animation keeps the hands busy (0 when the item cannot throw).</summary>
        public virtual float GrenadeThrowDuration => 0f;
        /// <summary>B140: exact seconds from throw start until the hand releases the grenade.</summary>
        public virtual float GrenadeReleaseDelay(float releaseFraction) => GrenadeThrowDuration * Mathf.Clamp01(releaseFraction);
        /// <summary>B136: false for items without a sight picture (knife, torch): the aim button does nothing.</summary>
        public virtual bool SupportsAds => true;
        /// <summary>B136: items that can enter Kinemation's attachment editing (guns with attachment groups).</summary>
        public virtual bool HasEditableAttachments => false;
        public virtual void Tick(float deltaTime) { }
        public virtual void CancelActions() { }
        public virtual void RefillAmmo() { }
        public virtual int AddReserveAmmo(int rounds) { return 0; }
        public virtual void UpdateFieldOfView(bool aiming) { }

        /// <summary>
        /// B136: Kinemation's ADS job reads the entity's aim point the moment a profile is linked (LR2's FPSBoneController patch
        /// hands the new entity to the running layers). An item without one (the demo knife) threw inside LinkAnimatorProfile,
        /// the link never finished and every later frame threw before the pose and IK layers ran: open hand, floating knife.
        /// </summary>
        protected void EnsureEntityAimPoint()
        {
            var entity = GetComponent<FPSAnimatorEntity>();
            if (entity == null || entity.defaultAimPoint != null) return;
            var t = transform.Find("AimPoint");
            if (t == null)
            {
                t = new GameObject("AimPoint").transform;
                t.SetParent(transform, false);
                var s = transform.lossyScale; // the demo knife root is scaled x100 (centimetre mesh)
                t.localPosition = new Vector3(0f, 0.05f / Mathf.Max(1e-4f, s.y), -0.1f / Mathf.Max(1e-4f, s.z));
            }
            entity.defaultAimPoint = t;
        }
    }
}
