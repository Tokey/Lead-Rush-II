using UnityEngine;

namespace LR2.Characters
{
    /// <summary>
    /// Keeps the held item exactly on the bone Kinemation animates (zero local offset) after Kinemation's LateUpdate.
    /// The gun itself is never moved after animation: Kinemation's IK has already put the hands on it, so any later
    /// move would slide the gun out of the hands. Wall collision is Kinemation's CollisionLayer, and ADS alignment is
    /// Kinemation's AdsLayer (it centres the sight within about 2 px at rest, measured by LR2AimGripAudit).
    /// </summary>
    [DefaultExecutionOrder(200)]
    public class WeaponAntiClip : MonoBehaviour
    {
        private SoldierController _soldier;

        private void Awake() => _soldier = GetComponent<SoldierController>();

        private void LateUpdate()
        {
            if (_soldier == null) return;
            var item = _soldier.ActiveItem;
            if (item == null || !item.gameObject.activeInHierarchy) return;
            item.transform.localPosition = Vector3.zero;
            item.transform.localRotation = Quaternion.identity;
        }
    }
}
