// Based on KINEMATION FPS Animation Framework demo Weapon (2025). Serialized field names of the demo are kept so the
// LR2 builder can swap the component on demo prefabs without losing animation/recoil setup. LR2 adds the actual
// gunplay: ammo, hitscan ballistics, spread/bloom, layered audio, muzzle flash, casings, tracers and AI noise.
using System;
using System.Collections.Generic;
using System.Reflection;
using Demo.Scripts.Runtime.AttachmentSystem;
using KINEMATION.FPSAnimationFramework.Runtime.Camera;
using KINEMATION.FPSAnimationFramework.Runtime.Core;
using KINEMATION.FPSAnimationFramework.Runtime.Layers.AdsLayer;
using KINEMATION.FPSAnimationFramework.Runtime.Playables;
using KINEMATION.FPSAnimationFramework.Runtime.Recoil;
using KINEMATION.ProceduralRecoilAnimationSystem.Runtime;
using KINEMATION.Shared.KAnimationCore.Runtime.Core;
using KINEMATION.Shared.KAnimationCore.Runtime.Input;
using LR2.Audio;
using LR2.Characters;
using LR2.Core;
using LR2.FX;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace LR2.Weapons
{
    public class LR2Firearm : LR2Item
    {
        [Header("General")]
        [SerializeField] [Range(0f, 120f)] private float fieldOfView = 90f;
        [SerializeField] private FPSAnimationAsset reloadClip;
        [SerializeField] private LR2.Characters.ReloadMotionBridge.Rest reloadRestPose;
        [Tooltip("Reload animation play rate (1 = authored speed). Below 1 = slower, more deliberate reloads.")]
        [SerializeField, Range(0.5f, 1.5f)] private float reloadPlayRate = 0.85f;
        [Tooltip("Multiplies reloadPlayRate. 1.5 = the user's 'reload 1.5x faster' (2026-11-17).")]
        [SerializeField, Range(0.5f, 3f)] private float reloadSpeedMultiplier = 1.5f;
        [Tooltip("Tactical reload: with a round still chambered, the empty-reload clip is cut shortly after the new magazine seats (no charging handle).")]
        [SerializeField] private bool tacticalReload = true;
        [Tooltip("How long after the mag-in cue (fraction of the full clip) a tactical reload ends and blends back to ready.")]
        [SerializeField, Range(0f, 0.3f)] private float tacticalEndAfterMagIn = 0.025f;
        [Tooltip("A tactical reload keeps the chambered round, so the gun holds magazine + 1 (30+1).")]
        [SerializeField] private bool chamberedRoundBonus = true;
        private FPSAnimationAsset _reloadRuntime, _tacticalRuntime;
        // Cut tactical reload shortly after mag-in so the empty-reload twist toward bolt catch/charging handle never plays.
        private const float TacticalSpeedUp = 1.22f, TacticalBlendOut = 0.16f, EmptyBlendOut = 0.18f;
        private float _activeReloadRate = 1f;
        [SerializeField] private FPSCameraAnimation cameraReloadAnimation;
        [SerializeField] private FPSAnimationAsset grenadeClip;
        [SerializeField] private FPSCameraAnimation cameraGrenadeAnimation;
        [Tooltip("B136: grenade-throw play rate (LR2 keeps Kinemation's actions short, like the reload).")]
        [SerializeField, Range(0.5f, 2f)] private float grenadeThrowRate = 1.25f;
        private FPSAnimationAsset _grenadeRuntime;

        [Header("Recoil")]
        [SerializeField] private FPSAnimationAsset fireClip;
        [SerializeField] private RecoilAnimData recoilData;
        [SerializeField] private RecoilPatternSettings recoilPatternSettings;
        [SerializeField] private FPSCameraShake cameraShake;
        [Min(0f)] [SerializeField] private float fireRate = 600f;
        [SerializeField] private bool supportsAuto = true;
        [SerializeField] private bool supportsBurst;
        [SerializeField] private int burstLength = 3;

        [Header("Attachments")]
        [SerializeField] private AttachmentGroup<BaseAttachment> barrelAttachments = new AttachmentGroup<BaseAttachment>();
        [SerializeField] private AttachmentGroup<BaseAttachment> gripAttachments = new AttachmentGroup<BaseAttachment>();
        [SerializeField] private List<AttachmentGroup<ScopeAttachment>> scopeGroups = new List<AttachmentGroup<ScopeAttachment>>();
        [Tooltip("B136: Kinemation's own attachment behaviour (the active attachment of each group shows and links its grip/sight " +
                 "layers, cycling works). Off = the old humanoid-driver setup, which hid every attachment and stripped its layers.")]
        [SerializeField] private bool kinemationAttachments;
        [Tooltip("B125 wall-tuck gate: at full ADS only 12 % of Kinemation's collision tuck is left. Kinemation itself tucks fully.")]
        [SerializeField] private bool gateWallTuckInAds = true;

        [Header("LR2 Unified Scopes & Iron Sights")]
        [SerializeField] private SkinnedMeshRenderer primaryBodyRenderer;
        [SerializeField] private Mesh ironSightsMesh;
        [SerializeField] private Mesh scopedNoIronsMesh;
        [SerializeField] private Material[] ironSightsMaterials;
        [SerializeField] private Material[] scopedNoIronsMaterials;
        [SerializeField] private Transform ironAimPoint;
        [SerializeField] private List<GameObject> ironSightOnlyObjects = new List<GameObject>();

        [Header("LR2 Gunplay")]
        [SerializeField] private WeaponDefinition definition;
        [SerializeField] private Transform muzzle;
        [SerializeField] private MuzzleFlash muzzleFlash;
        [SerializeField] private ShellEjector shellEjector;
        [Tooltip("Bots see tracers more often so the player can read where fire comes from.")]
        [SerializeField] private int botTracerEvery = 1;
        [Tooltip("B136 (user 11-22: a laser device on every gun without one): where the beam leaves the gun. Null = the muzzle.")]
        [SerializeField] private Transform laserEmitter;

        public event Action Fired;
        public event Action AmmoChanged;
        public event Action ReloadStarted;
        public event Action ReloadFinished;
        public event Action DryFired;

        public WeaponDefinition Definition => definition;
        /// <summary>Authoritative ADS in/out duration (s) shared by 3D weapon ADS layer, camera FOV zoom, and spread blend.</summary>
        public float EffectiveAdsTime => definition != null ? Mathf.Max(0.05f, definition.adsTime) : 0.21f;
        public int AmmoInMagazine { get; private set; }
        public int ReserveAmmo { get; private set; }
        public int MagazineSize => definition != null ? definition.magazineSize : 30;
        public bool IsReloading { get; private set; }
        /// <summary>Kinemation reload animation (null = timer-only reload). Read by RetargetProxy's reload bridge.</summary>
        public FPSAnimationAsset ReloadClip => reloadClip;
        /// <summary>Baked t=0 helper-bone rest pose from reloadClip curves (avoids identity fallback in SampleAnimation).</summary>
        public LR2.Characters.ReloadMotionBridge.Rest ReloadRestPose => reloadRestPose;
        public bool IsEmpty => AmmoInMagazine <= 0;
        public FireMode FireMode => _fireMode;
        public Transform Muzzle => muzzle != null ? muzzle : transform;
        /// <summary>B136: the laser device's emitter when one is fitted and showing, else the muzzle.</summary>
        public Transform LaserOrigin => laserEmitter != null && laserEmitter.gameObject.activeInHierarchy ? laserEmitter : Muzzle;
        public bool HasLaserDevice => laserEmitter != null;
        public float ReloadProgress => IsReloading ? Mathf.Clamp01((Time.time - _reloadStart) / Mathf.Max(0.01f, _reloadDuration)) : 0f;
        /// <summary>0..1 over the part of the clip this reload actually plays (tactical reloads end early), for HUD bars.</summary>
        public float ReloadDisplayProgress => IsReloading ? Mathf.Clamp01(ReloadProgress / Mathf.Max(0.05f, _reloadEndAt)) : 0f;
        public override bool IsBusy => IsReloading || Time.time < _equipReadyAt;
        public override string DisplayName => definition != null ? definition.displayName : base.DisplayName;
        /// <summary>B136: the definition says so, or a suppressor is the active barrel attachment.</summary>
        public bool IsSuppressed => (definition != null && definition.suppressed) || _suppressorFitted;
        public override bool HasEditableAttachments => kinemationAttachments &&
            (scopeGroups.Count > 0 || barrelAttachments.attachments.Count > 1 || gripAttachments.attachments.Count > 1);
        public override float GrenadeThrowDuration => FPSAnimationAsset.IsValid(grenadeClip) && grenadeClip.clip != null
            ? grenadeClip.clip.length * 0.72f / Mathf.Max(0.1f, grenadeThrowRate) : 0f;
        public override float GrenadeReleaseDelay(float releaseFraction) => FPSAnimationAsset.IsValid(grenadeClip) && grenadeClip.clip != null
            ? grenadeClip.clip.length * Mathf.Clamp(releaseFraction, 0.1f, 0.7f) / Mathf.Max(0.1f, grenadeThrowRate)
            : base.GrenadeReleaseDelay(releaseFraction);
        /// <summary>The sight the gun is aimed through (null = iron sights / default aim point).</summary>
        public ScopeAttachment ActiveScope => !_usingIronSights && scopeGroups.Count > 0 ? scopeGroups[_scopeIndex].GetActiveAttachment() : null;
        public bool UsesKinemationAttachments => kinemationAttachments;
        public Transform ActiveAimPointTransform => _entity != null ? _entity.defaultAimPoint : null;
        public bool IsSightLockEligible => Owner != null && Owner.IsLocalPlayer && !Owner.IsDead && !IsReloading;
        public RecoilAnimation LiveRecoilAnimation => _recoilAnimation;
        public bool HasUnifiedScopes => scopeGroups != null && scopeGroups.Count > 0 && scopeGroups[0].attachments != null && scopeGroups[0].attachments.Count >= 2 && (ironAimPoint != null || scopedNoIronsMesh != null);
        public int CurrentScopeChoice => _currentScopeChoice;
        public bool IsUsingIronSights => _usingIronSights;
        public Mesh IronSightsMesh => ironSightsMesh;
        public Mesh ScopedNoIronsMesh => scopedNoIronsMesh;
        public Transform IronAimPoint => ironAimPoint;
        private bool _suppressorFitted;

        public bool CanFire => Owner != null && !IsReloading && AmmoInMagazine > 0 && Time.time >= _equipReadyAt && Owner.CanShootNow;

        public float Rpm => definition != null ? definition.rpm : Mathf.Max(60f, fireRate);

        /// <summary>Current cone half-angle in degrees (drives bullet spread and the dynamic crosshair).</summary>
        public float CurrentSpread
        {
            get
            {
                if (definition == null || Owner == null) return 1f;
                var move = Owner.Movement;
                float hip = definition.hipSpread + _bloom;
                if (move != null)
                {
                    hip += definition.moveSpreadAdd * Mathf.Clamp01(move.Speed / 5f);
                    if (!move.IsGrounded) hip += definition.airSpreadAdd;
                    if (move.Stance == SoldierStance.Crouching) hip *= definition.crouchSpreadMultiplier;
                }
                float ads = definition.adsSpread + _bloom * 0.12f + (move != null ? definition.moveSpreadAdd * 0.1f * Mathf.Clamp01(move.Speed / 5f) : 0f);
                return Mathf.Lerp(hip, ads, Owner.AimBlend);
            }
        }

        // Kinemation references
        private Animator _controllerAnimator;
        private UserInputController _userInput;
        private IPlayablesController _playables;
        private FPSCameraController _cameraController;
        private FPSAnimator _fpsAnimator;
        private FPSBoneController _boneController;
        private FPSAnimatorEntity _entity;
        private RecoilAnimation _recoilAnimation;
        private RecoilPattern _recoilPattern;
        private Animator _weaponAnimator;
        private int _scopeIndex;
        private int _configuredScopeChoice = -1;
        private int _currentScopeChoice = -1;
        private bool _usingIronSights;
        private Transform _baseDefaultAimPoint;
        private bool _baseAimPointCached;
        private static readonly FieldInfo ScopeActiveIndexField = typeof(AttachmentGroup<ScopeAttachment>).GetField(
            "_activeIndex", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        // Gunplay state
        private FireMode _fireMode = FireMode.Auto;
        private bool _triggerHeld;
        private bool _shotThisPress;
        private int _burstRemaining;
        private float _nextShotTime;
        private float _lastShotTime = -10f;
        private float _bloom;
        private bool _recoilActive;
        private int _shotCounter;
        private float _equipReadyAt;
        private bool _ammoInitialized;
        private bool _dryFiredThisPress;
        private bool _indoors;
        private float _barrelHeat;
        private float _lastHeatShotTime = -10f;
        private int _consecutiveHeatShots;
        private bool _heatFxUnlocked;
        private ParticleSystem _barrelSmoke;
        private ParticleSystem.EmissionModule _barrelSmokeEmission;
        private Material _barrelSmokeMaterial;
        private Renderer _barrelGlowRenderer;
        private Material _barrelGlowMat;
        private HeatHazeEmitter _muzzleHeatHaze;
        private bool _heatFxBuilt;
        public float BarrelHeat => _barrelHeat;
        private static readonly FieldInfo CameraFovSpeedField = typeof(FPSCameraController).GetField(
            "fovSpeed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo CameraFovEaseModeField = typeof(FPSCameraController).GetField(
            "fovEaseMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly EaseMode AdsFovEaseMode = new EaseMode(EEaseFunc.Sine);
        private FPSAnimatorProfile _runtimeProfile;
        private AdsLayerSettings _runtimeAdsSettings;

        // Reload
        private float _reloadStart;
        private float _reloadDuration;
        private bool _reloadEmpty;
        private Transform _tacticalBolt;
        private Vector3 _tacticalBoltClosedPos;
        private float _reloadEndAt = 1f; // fraction of the full clip at which this reload completes (tactical < 1)
        private float ReloadRate => Mathf.Max(0.1f, reloadPlayRate * reloadSpeedMultiplier);
        /// <summary>True while a tactical reload (round chambered, no charging handle) is playing.</summary>
        public bool IsTacticalReload => IsReloading && !_reloadEmpty && _reloadEndAt < 1f;
        /// <summary>Rounds the gun holds after a reload from this state: magazine + 1 when a round stays chambered.</summary>
        private int ReloadCapacity => MagazineSize + (chamberedRoundBonus && AmmoInMagazine > 0 ? 1 : 0);
        private bool _magOutPlayed, _magInPlayed, _boltPlayed;

        public void Configure(WeaponDefinition def, Transform muzzlePoint, MuzzleFlash flash, ShellEjector ejector)
        {
            definition = def;
            muzzle = muzzlePoint;
            muzzleFlash = flash;
            shellEjector = ejector;
        }

        private void Awake()
        {
            EnsureAmmo();
            // B125: a held gun must not be a physics or ray obstacle. The Mk18 import carried four MeshColliders (scope Reticle
            // and Plane) on the Default layer, which is in EnvironmentMask: the owner's and nearby bots' Kinemation wall-tuck
            // rays, sight lines and bullets all hit guns. Dropped guns get their own BoxCollider when they fall.
            foreach (var col in GetComponentsInChildren<Collider>(true))
                if (col != null && !col.isTrigger) Destroy(col);
            // B136: picture-in-picture scope cameras (ACOG, 1P29, long-range scope) start off; see UpdateScopeCameras
            var cams = new List<Camera>();
            foreach (var cam in GetComponentsInChildren<Camera>(true))
                if (cam != null && cam.targetTexture != null) { cam.enabled = false; cams.Add(cam); }
            _scopeCameras = cams.ToArray();
        }

        private Camera[] _scopeCameras = Array.Empty<Camera>();

        /// <summary>
        /// B136: a scope camera renders only for the local player and only for the sight being looked through (a dozen bots each
        /// rendering an HDRP camera per scope would cost more than the whole map). It runs while the gun is out, not only while
        /// aiming, so its auto exposure has settled by the time the player aims (the first pose check saw a dark lens).
        /// </summary>
        private void UpdateScopeCameras()
        {
            if (_scopeCameras.Length == 0) return;
            var scope = ActiveScope;
            bool look = Owner != null && Owner.IsLocalPlayer && !Owner.IsDead && scope != null;
            foreach (var cam in _scopeCameras)
            {
                if (cam == null) continue;
                bool on = look && cam.gameObject.activeInHierarchy && cam.transform.IsChildOf(scope.transform);
                if (cam.enabled != on) cam.enabled = on;
            }
        }

        private void EnsureAmmo()
        {
            if (_ammoInitialized) return;
            _ammoInitialized = true;
            AmmoInMagazine = MagazineSize;
            ReserveAmmo = definition != null ? definition.startingReserve : 90;
            _fireMode = (definition != null ? definition.allowAuto : supportsAuto) ? FireMode.Auto : FireMode.Semi;
        }

        private Animator ResolveWeaponAnimator()
        {
            var all = GetComponentsInChildren<Animator>(true);
            Animator chosen = null;
            foreach (var cand in all)
            {
                if (cand == null) continue;
                if (cand.transform.Find("AK12Rig") != null || cand.transform.Find("FAL") != null ||
                    cand.transform.Find("Mk18") != null || cand.transform.Find("Mk23Mod0Rig") != null)
                {
                    chosen = cand;
                    break;
                }
            }
            if (chosen == null && all.Length > 0) chosen = all[all.Length - 1];
            foreach (var extra in all)
            {
                if (chosen != null && extra != null && extra != chosen && extra.gameObject == gameObject)
                    extra.enabled = false;
            }
            if (chosen != null)
            {
                chosen.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                chosen.Rebind();
                chosen.Play("Empty", 0, 0f);
                chosen.Update(0f);
            }
            return chosen;
        }

        // ------------------------------------------------------------------ equip / FOV
        public override void OnEquip(SoldierController owner)
        {
            if (owner == null) return;
            base.OnEquip(owner);
            EnsureAmmo();

            _fpsAnimator = owner.FPSAnimator;
            _boneController = owner.GetComponent<FPSBoneController>();
            _entity = GetComponent<FPSAnimatorEntity>();
            if (_entity != null && !_baseAimPointCached)
            {
                _baseDefaultAimPoint = _entity.defaultAimPoint;
                _baseAimPointCached = true;
            }
            _weaponAnimator = ResolveWeaponAnimator();
            _controllerAnimator = owner.Animator;
            _userInput = owner.UserInput;
            _playables = owner.Playables;
            _cameraController = owner.CameraController;
            _recoilAnimation = owner.RecoilAnimation;
            _recoilPattern = owner.RecoilPattern;

            if (_playables != null && overrideController != null && _controllerAnimator != null &&
                overrideController != _controllerAnimator.runtimeAnimatorController)
                _playables.UpdateAnimatorController(overrideController);

            InitializeAttachments();
            EnsureRuntimeAdsProfile();
            if (_fpsAnimator != null)
            {
                _fpsAnimator.LinkAnimatorProfile(gameObject);
                barrelAttachments.Initialize(_fpsAnimator);
                gripAttachments.Initialize(_fpsAnimator);
            }

            if (_recoilAnimation != null) _recoilAnimation.Init(recoilData, Rpm, _fireMode);
            if (_recoilPattern != null) _recoilPattern.Init(RuntimePattern());
            // B125: the owner's own copy of the motion (its curves are evaluated on animation worker threads)
            if (_fpsAnimator != null) _fpsAnimator.LinkAnimatorLayer(Owner != null ? Owner.OwnMotion(equipMotion) : equipMotion);

            _equipReadyAt = Time.time + (definition != null ? definition.equipTime : 0.5f);
            _triggerHeld = false;
            OnAimReleased();
            if (definition != null && definition.audio != null) LR2Audio.PlayAt(definition.audio.equip, transform.position);
        }

        public override void OnUnEquip()
        {
            CancelActions();
            ClearBarrelHeat();
            if (_fpsAnimator != null) _fpsAnimator.LinkAnimatorLayer(Owner != null ? Owner.OwnMotion(unEquipMotion) : unEquipMotion);
        }

        public void SetConfiguredScopeChoice(int choice)
        {
            _configuredScopeChoice = Mathf.Clamp(choice, 0, LR2.Game.LR2Session.ScopeNames.Length - 1);
            if (_entity != null) ApplyScopeChoice(_configuredScopeChoice);
        }

        public void ApplyScopeChoice(int choice)
        {
            if (scopeGroups == null || (scopeGroups.Count == 0 && ironAimPoint == null)) return;
            choice = Mathf.Clamp(choice, 0, LR2.Game.LR2Session.ScopeNames.Length - 1);
            _configuredScopeChoice = _currentScopeChoice = choice;
            bool iron = choice == 0;
            _usingIronSights = iron;

            if (primaryBodyRenderer != null)
            {
                Mesh targetMesh = iron ? ironSightsMesh : scopedNoIronsMesh;
                if (targetMesh != null && primaryBodyRenderer.sharedMesh != targetMesh)
                    primaryBodyRenderer.sharedMesh = targetMesh;
                Material[] targetMats = iron ? ironSightsMaterials : scopedNoIronsMaterials;
                if (targetMats != null && targetMats.Length > 0 && targetMesh != null && targetMats.Length == targetMesh.subMeshCount)
                    primaryBodyRenderer.sharedMaterials = targetMats;
            }
            if (ironSightOnlyObjects != null)
            {
                for (int i = 0; i < ironSightOnlyObjects.Count; i++)
                    if (ironSightOnlyObjects[i] != null) ironSightOnlyObjects[i].SetActive(iron);
            }

            Transform chosenAim = iron ? (ironAimPoint != null ? ironAimPoint : _baseDefaultAimPoint) : _baseDefaultAimPoint;
            ScopeAttachment activeScope = null;

            if (scopeGroups.Count > 0)
            {
                _scopeIndex = 0;
                int optIdx = Mathf.Max(0, choice - 1);
                var primaryGroup = scopeGroups[0];
                int groupOpt = primaryGroup.attachments != null && primaryGroup.attachments.Count > 0
                    ? Mathf.Clamp(optIdx, 0, primaryGroup.attachments.Count - 1)
                    : 0;
                if (ScopeActiveIndexField != null) ScopeActiveIndexField.SetValue(primaryGroup, groupOpt);

                for (int g = 0; g < scopeGroups.Count; g++)
                {
                    var grp = scopeGroups[g];
                    if (grp?.attachments == null) continue;
                    for (int a = 0; a < grp.attachments.Count; a++)
                    {
                        var att = grp.attachments[a];
                        if (att == null) continue;
                        bool enable = !iron && g == 0 && a == groupOpt;
                        att.gameObject.SetActive(enable);
                        if (enable) activeScope = att;
                    }
                }
            }

            if (!iron && activeScope != null)
            {
                if (activeScope.transform.parent != null && !activeScope.transform.parent.gameObject.activeSelf)
                    activeScope.transform.parent.gameObject.SetActive(true);
                if (activeScope.aimPoint != null) chosenAim = activeScope.aimPoint;
                if (_fpsAnimator != null && activeScope.attachmentLayerSettings != null)
                    foreach (var s in activeScope.attachmentLayerSettings)
                        if (s != null) _fpsAnimator.LinkAnimatorLayer(s);
            }

            if (_entity != null && chosenAim != null)
                _entity.defaultAimPoint = chosenAim;
            if (_boneController != null && _entity != null)
                _boneController.UpdateEntity(_entity);

            UpdateScopeCameras();
            UpdateFieldOfView(Owner != null && Owner.IsAiming);
        }

        private void InitializeAttachments()
        {
            if (kinemationAttachments)
            {
                if (_configuredScopeChoice >= 0 && (scopeGroups.Count > 0 || ironAimPoint != null))
                {
                    ShowOnlyActive(barrelAttachments);
                    ShowOnlyActive(gripAttachments);
                    ApplyScopeChoice(_configuredScopeChoice);
                    RefreshSuppressor();
                    return;
                }
                // Default behaviour when no explicit scope override was configured: sync iron-sight mesh & active scope.
                ShowOnlyActive(barrelAttachments);
                ShowOnlyActive(gripAttachments);
                foreach (var group in scopeGroups) ShowOnlyActive(group);
                if (_fpsAnimator != null)
                    foreach (var group in scopeGroups) group.Initialize(_fpsAnimator);
                _scopeIndex = 0;
                if (_entity != null && _baseAimPointCached) _entity.defaultAimPoint = _baseDefaultAimPoint;
                UpdateAimPoint();
                SyncIronSightMeshState();
                RefreshSuppressor();
                return;
            }

            DisableAttachments();

            // B49/B55: Strip any Generic-rig AttachHandLayerSettings from Kinemation attachments (e.g. Mk18 GripEmpty/GripMount)
            // so Humanoid left-hand IK stays locked to the handguard instead of snapping to the waist.
            foreach (var att in GetComponentsInChildren<BaseAttachment>(true))
            {
                if (att != null && att.attachmentLayerSettings != null && att.attachmentLayerSettings.Count > 0)
                    att.attachmentLayerSettings.Clear();
            }

            if (_fpsAnimator != null)
                foreach (var group in scopeGroups) group.Initialize(_fpsAnimator);
            _scopeIndex = 0;
            if (scopeGroups.Count == 0) return;
            UpdateAimPoint();
            SyncIronSightMeshState();
        }

        private void SyncIronSightMeshState()
        {
            var sc = ActiveScope;
            bool iron = sc == null;
            _usingIronSights = iron && ironAimPoint != null;
            if (primaryBodyRenderer != null)
            {
                Mesh target = iron ? ironSightsMesh : scopedNoIronsMesh;
                if (target != null && primaryBodyRenderer.sharedMesh != target)
                    primaryBodyRenderer.sharedMesh = target;
                Material[] targetMats = iron ? ironSightsMaterials : scopedNoIronsMaterials;
                if (targetMats != null && targetMats.Length > 0 && target != null && targetMats.Length == target.subMeshCount)
                    primaryBodyRenderer.sharedMaterials = targetMats;
            }
            if (ironSightOnlyObjects != null)
                for (int i = 0; i < ironSightOnlyObjects.Count; i++)
                    if (ironSightOnlyObjects[i] != null) ironSightOnlyObjects[i].SetActive(iron);
            if (_usingIronSights && _entity != null && ironAimPoint != null)
                _entity.defaultAimPoint = ironAimPoint;
        }

        /// <summary>
        /// Disables every scope, grip and barrel attachment on this firearm.
        /// LR2 currently treats the equipped weapon as a clean weapon rig unless a live attachment system enables a part.
        /// </summary>
        public void DisableAttachments()
        {
            foreach (var att in GetComponentsInChildren<BaseAttachment>(true))
            {
                if (att == null) continue;
                if (att.attachmentLayerSettings != null)
                    att.attachmentLayerSettings.Clear();
                att.gameObject.SetActive(false);
            }
            _scopeIndex = 0;
            if (_entity != null && _baseAimPointCached)
                _entity.defaultAimPoint = _baseDefaultAimPoint;
        }

        private void UpdateAimPoint()
        {
            if (_usingIronSights && _entity != null && ironAimPoint != null)
            {
                _entity.defaultAimPoint = ironAimPoint;
                return;
            }
            if (scopeGroups.Count == 0 || _entity == null) return;
            var scope = scopeGroups[_scopeIndex].GetActiveAttachment();
            if (scope != null && scope.aimPoint != null) _entity.defaultAimPoint = scope.aimPoint;
        }

        private static void ShowOnlyActive<T>(AttachmentGroup<T> group) where T : BaseAttachment
        {
            if (group == null || group.attachments == null) return;
            var active = group.GetActiveAttachment();
            foreach (var att in group.attachments)
                if (att != null) att.gameObject.SetActive(att == active);
        }

        // B136: muzzle point follows a fitted suppressor (tracers and the flash start at its tip)
        private Vector3 _muzzleBaseLocal;
        private bool _muzzleBaseCached;

        private void RefreshSuppressor()
        {
            var att = barrelAttachments != null ? barrelAttachments.GetActiveAttachment() : null;
            string n = att != null ? att.name.ToLowerInvariant() : string.Empty;
            // Kinemation's own names: AKHexagonSuppressor, Supperssor_Mk18 (sic); the Empty* entries are the "nothing fitted" slot
            _suppressorFitted = att != null && att.gameObject.activeInHierarchy && !n.StartsWith("empty") &&
                                (n.Contains("suppress") || n.Contains("supperss") || n.Contains("silenc"));
            if (muzzle == null) return;
            if (!_muzzleBaseCached)
            {
                _muzzleBaseLocal = muzzle.localPosition;
                _muzzleBaseCached = true;
            }
            muzzle.localPosition = _muzzleBaseLocal;
            if (!_suppressorFitted) return;
            // the suppressor's furthest point along the barrel, measured from the bare muzzle
            float reach = 0f;
            Vector3 origin = muzzle.position, axis = muzzle.forward;
            foreach (var r in att.GetComponentsInChildren<Renderer>())
            {
                var b = r.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    reach = Mathf.Max(reach, Vector3.Dot(corner - origin, axis));
                }
            }
            if (reach > 0.005f && reach < 0.6f) muzzle.position = origin + axis * reach;
        }

        /// <summary>B136: Kinemation attachment editing (1 barrel, 2 grip, else the active sight group).</summary>
        public override void OnAttachmentChanged(int attachmentTypeIndex)
        {
            if (!kinemationAttachments || _fpsAnimator == null) return;
            if (attachmentTypeIndex == 1) { barrelAttachments.CycleAttachments(_fpsAnimator); RefreshSuppressor(); }
            else if (attachmentTypeIndex == 2) gripAttachments.CycleAttachments(_fpsAnimator);
            else
            {
                if (HasUnifiedScopes)
                {
                    int next = (_currentScopeChoice + 1) % LR2.Game.LR2Session.ScopeNames.Length;
                    ApplyScopeChoice(next);
                }
                else
                {
                    if (scopeGroups.Count == 0) return;
                    scopeGroups[_scopeIndex].CycleAttachments(_fpsAnimator);
                    UpdateAimPoint();
                    UpdateFieldOfView(Owner != null && Owner.IsAiming);
                }
            }
            if (definition != null && definition.audio != null) LR2Audio.PlayAt(definition.audio.fireModeSwitch, transform.position, 0.7f);
        }

        /// <summary>B136: Kinemation's grenade throw (the rifles carry C_GrenadeThrow). The soldier spawns the grenade.</summary>
        public override bool OnGrenadeThrow()
        {
            if (!FPSAnimationAsset.IsValid(grenadeClip) || _playables == null || IsReloading) return false;
            OnFireReleased();
            if (_grenadeRuntime == null)
            {
                _grenadeRuntime = Instantiate(grenadeClip);
                _grenadeRuntime.hideFlags = HideFlags.HideAndDontSave;
                var bt = _grenadeRuntime.blendTime;
                bt.rateScale = grenadeThrowRate;
                _grenadeRuntime.blendTime = bt;
            }
            _playables.PlayAnimation(_grenadeRuntime, 0f);
            if (_cameraController != null && cameraGrenadeAnimation != null) _cameraController.PlayCameraAnimation(cameraGrenadeAnimation);
            return true;
        }

        /// <summary>Kinemation per-shot camera shake asset (the killcam replays it on the recorded shots).</summary>
        public FPSCameraShake CameraShakeAsset => cameraShake;

        /// <summary>
        /// Clones the weapon's animatorProfile and AdsLayerSettings at runtime (HideAndDontSave) so each weapon
        /// drives Kinemation's 3D ADS-in and ADS-out blend at 1 / EffectiveAdsTime without mutating shared disk assets.
        /// </summary>
        private void EnsureRuntimeAdsProfile()
        {
            if (_entity == null || _entity.animatorProfile == null) return;
            float targetSpeed = 1f / EffectiveAdsTime;
            if (_runtimeProfile == null)
            {
                _runtimeProfile = Instantiate(_entity.animatorProfile);
                _runtimeProfile.name = $"{_entity.animatorProfile.name}_{name}_Runtime";
                _runtimeProfile.hideFlags = HideFlags.HideAndDontSave;
                if (_runtimeProfile.settings != null)
                {
                    for (int i = 0; i < _runtimeProfile.settings.Count; i++)
                    {
                        if (_runtimeAdsSettings == null && _runtimeProfile.settings[i] is AdsLayerSettings origAds)
                        {
                            _runtimeAdsSettings = Instantiate(origAds);
                            _runtimeAdsSettings.name = $"{origAds.name}_{name}_Runtime";
                            _runtimeAdsSettings.hideFlags = HideFlags.HideAndDontSave;
                            // Eliminate static hip-snapshot _additivePose blend bias and bind directly to Camera.
                            _runtimeAdsSettings.positionBlend = default;
                            _runtimeAdsSettings.rotationBlend = default;
                            _runtimeAdsSettings.cameraBlend = 0f;
                            _runtimeAdsSettings.aimTargetBone = new KINEMATION.Shared.KAnimationCore.Runtime.Rig.KRigElement(-1, "Camera");
                            _runtimeProfile.settings[i] = _runtimeAdsSettings;
                        }
                        else if (gateWallTuckInAds && _runtimeProfile.settings[i] is KINEMATION.FPSAnimationFramework.Runtime.Layers.CollisionLayer.CollisionLayerSettings origCol)
                        {
                            // Gate wall collision out completely at full ADS so obstacles/cover parapets never deflect sights off target.
                            var col = Instantiate(origCol);
                            col.name = $"{origCol.name}_{name}_Runtime";
                            col.hideFlags = HideFlags.HideAndDontSave;
                            col.layerMask = LR2Layers.EnvironmentMask;
                            var blends = new List<CurveBlend>();
                            if (col.curveBlending != null)
                                foreach (var b in col.curveBlending)
                                    if (b.name != FPSANames.AimingWeight) blends.Add(b);
                            blends.Add(new CurveBlend { name = FPSANames.AimingWeight, mode = ECurveBlendMode.Mask, clampMin = 0f, source = ECurveSource.Input });
                            col.curveBlending = blends;
                            _runtimeProfile.settings[i] = col;
                        }
                    }
                }
                _entity.animatorProfile = _runtimeProfile;
            }
            if (_runtimeAdsSettings != null) _runtimeAdsSettings.aimingSpeed = targetSpeed;
        }

        public override void UpdateFieldOfView(bool aiming)
        {
            // Settings and fieldOfView are HORIZONTAL degrees (CoD/BF convention); Unity cameras take the vertical angle.
            float hipFovH = Owner != null && Owner.IsLocalPlayer ? LR2Settings.FieldOfView : fieldOfView;
            var viewCam = Owner != null ? Owner.ViewCamera : null;
            float hipFovV = LR2Settings.VerticalFov(hipFovH, viewCam != null ? viewCam.aspect : 16f / 9f);
            float adsFovV = hipFovV * (definition != null ? definition.adsFovMultiplier : 0.8f);
            float sens = 1f;
            var scope = ActiveScope;
            if (scope != null)
            {
                adsFovV *= scope.aimFovZoom > 0.01f ? Mathf.Min(1f, scope.aimFovZoom) : 1f;
                if (aiming) sens = scope.sensitivityMultiplier;
            }
            float fov = aiming ? adsFovV : hipFovV;
            if (_userInput != null) _userInput.SetValue("SensitivityMultiplier", sens);
            if (_cameraController != null)
            {
                // FPSCameraController advances its Sine FOV curve by deltaTime * fovSpeed.
                // Match both zoom-in and zoom-out (including mid-ADS reversals) to the weapon's 3D ADS rate and ease mode.
                float adsDuration = EffectiveAdsTime;
                float fullDelta = Mathf.Abs(hipFovV - adsFovV);
                float remFrac = 1f;
                if (viewCam != null && fullDelta > 0.5f)
                    remFrac = Mathf.Clamp(Mathf.Abs(fov - viewCam.fieldOfView) / fullDelta, 0.1f, 1f);
                if (CameraFovSpeedField != null) CameraFovSpeedField.SetValue(_cameraController, 1f / (adsDuration * remFrac));
                if (CameraFovEaseModeField != null) CameraFovEaseModeField.SetValue(_cameraController, AdsFovEaseMode);
                _cameraController.UpdateTargetFOV(fov);
            }
        }

        public override bool OnAimPressed()
        {
            if (_userInput != null) _userInput.SetValue(FPSANames.IsAiming, true);
            UpdateFieldOfView(true);
            if (_recoilAnimation != null) _recoilAnimation.isAiming = true;
            return true;
        }

        public override bool OnAimReleased()
        {
            if (_userInput != null) _userInput.SetValue(FPSANames.IsAiming, false);
            UpdateFieldOfView(false);
            if (_recoilAnimation != null) _recoilAnimation.isAiming = false;
            return true;
        }

        public override void OnCycleScope()
        {
            if (_usingIronSights || scopeGroups.Count <= 1) return;
            _scopeIndex = (_scopeIndex + 1) % scopeGroups.Count;
            UpdateAimPoint();
            UpdateFieldOfView(Owner != null && Owner.IsAiming);
        }

        public override void OnChangeFireMode()
        {
            bool canAuto = definition != null ? definition.allowAuto : supportsAuto;
            bool canBurst = definition != null ? definition.allowBurst : supportsBurst;
            if (_fireMode == FireMode.Semi && canBurst) _fireMode = FireMode.Burst;
            else if (_fireMode != FireMode.Auto && canAuto) _fireMode = FireMode.Auto;
            else _fireMode = FireMode.Semi;
            if (_recoilAnimation != null) _recoilAnimation.fireMode = _fireMode;
            if (definition != null && definition.audio != null) LR2Audio.PlayAt(definition.audio.fireModeSwitch, transform.position);
        }

        // ------------------------------------------------------------------ trigger
        public override bool OnFirePressed()
        {
            _triggerHeld = true;
            _shotThisPress = false;
            _dryFiredThisPress = false;
            _burstRemaining = definition != null ? definition.burstCount : Mathf.Max(1, burstLength);
            return true;
        }

        public override bool OnFireReleased()
        {
            _triggerHeld = false;
            StopRecoil();
            return true;
        }

        private RecoilPatternSettings _patternRuntime;
        private Vector2 _patternBaseV, _patternBaseH;
        // B47 (specs/B47.md fairness rules): a learnable spray. Kinemation drew every shot at random inside the pattern ranges
        // and the view kick picked a random side, so no two sprays matched. Shot i of a spray now climbs the range midpoint
        // (easing to 78 % from shot 5 on) and weaves sideways along one fixed S curve, with at most 10 % seeded drift from shot
        // 4 on; the climb holds 0.10 s after the last shot before recovering. Average climb per gun is unchanged.
        private static readonly float[] SprayWeave =
            { 0f, 0.15f, 0.25f, 0.1f, -0.2f, -0.45f, -0.5f, -0.3f, 0.1f, 0.45f, 0.6f, 0.4f, 0f, -0.4f, -0.55f, -0.35f, 0.05f, 0.4f, 0.55f, 0.3f };
        private const float SprayResetSeconds = 0.3f, RecoveryHoldSeconds = 0.10f;
        private int _sprayIndex;
        private float _lastShotAt = -10f, _patternBaseDamping, _sprayWeaveNow;

        private float SpraySeed
        {
            get
            {
                string id = definition != null ? definition.displayName : name;
                int h = 17;
                foreach (char ch in id ?? "") h = h * 31 + ch;
                return (h & 0x3FF) * 0.137f;
            }
        }

        /// <summary>Per-gun copy of the Kinemation recoil pattern, so per-weapon and ADS scaling never touch the shared demo asset.</summary>
        private RecoilPatternSettings RuntimePattern()
        {
            if (recoilPatternSettings == null) return null;
            if (_patternRuntime == null)
            {
                _patternRuntime = Instantiate(recoilPatternSettings);
                _patternRuntime.name = recoilPatternSettings.name + " (LR2 runtime)";
                _patternBaseV = recoilPatternSettings.verticalRecoil;
                _patternBaseH = recoilPatternSettings.horizontalRecoil;
                _patternBaseDamping = recoilPatternSettings.damping;
            }
            ScalePatternForShot(false);
            return _patternRuntime;
        }

        /// <summary>Per-shot climb = demo pattern x weapon scale x (ADS multiplier while aiming). Read by RecoilPattern.OnFireStart.</summary>
        private void ScalePatternForShot(bool aiming, bool shot = false, float extraScale = 1f)
        {
            if (_patternRuntime == null) return;
            float v = (definition != null ? definition.recoilPatternScale : 1f) * extraScale;
            float h = (definition != null ? definition.recoilPatternHorizontalScale : 1f) * extraScale;
            float ads = aiming && definition != null ? definition.adsRecoilMultiplier : 1f;
            if (!shot)
            {
                _patternRuntime.verticalRecoil = _patternBaseV * (v * ads);
                _patternRuntime.horizontalRecoil = _patternBaseH * (h * ads);
                return;
            }
            if (Time.time - _lastShotAt > SprayResetSeconds) _sprayIndex = 0;
            int i = _sprayIndex++;
            _lastShotAt = Time.time;
            float ramp = i < 4 ? 1f : Mathf.Lerp(1f, 0.78f, Mathf.Clamp01((i - 4) / 11f));
            float vv = (_patternBaseV.x + _patternBaseV.y) * 0.5f * ramp * v * ads;
            float hMid = (_patternBaseH.x + _patternBaseH.y) * 0.5f;
            float hHalf = Mathf.Abs(_patternBaseH.y - _patternBaseH.x) * 0.5f;
            float jitter = i >= 3 ? (Mathf.PerlinNoise(SpraySeed, i * 0.37f) - 0.5f) * 0.2f : 0f; // at most +-10 % of the range
            _sprayWeaveNow = SprayWeave[i % SprayWeave.Length] + jitter;
            float hv = (hMid + hHalf * _sprayWeaveNow) * h * ads;
            _patternRuntime.verticalRecoil = new Vector2(vv, vv);   // equal ends: Kinemation's Random.Range returns that value
            _patternRuntime.horizontalRecoil = new Vector2(hv, hv);
        }

        /// <summary>Index of the next shot in the current spray (QA).</summary>
        public int SprayIndex => _sprayIndex;

        /// <summary>Current per-shot vertical climb range in degrees (QA).</summary>
        public Vector2 PatternVerticalPerShot => _patternRuntime != null ? _patternRuntime.verticalRecoil : Vector2.zero;

        private void StopRecoil()
        {
            if (!_recoilActive) return;
            _recoilActive = false;
            if (_recoilAnimation != null) _recoilAnimation.Stop();
            if (_recoilPattern != null) _recoilPattern.OnFireEnd();
        }

        public override void Tick(float deltaTime)
        {
            UpdateScopeCameras();
            if (definition != null) _bloom = Mathf.Max(0f, _bloom - definition.bloomRecovery * deltaTime);
            // B47: hold the climb for 0.10 s after the last shot, then recover at the pattern's own rate (damping 5: 80 % in ~0.32 s)
            if (_patternRuntime != null) _patternRuntime.damping = Time.time - _lastShotAt < RecoveryHoldSeconds ? 0f : _patternBaseDamping;
            if (IsReloading) UpdateReload();

            bool wantsShot = _triggerHeld && (_fireMode == FireMode.Auto || (_fireMode == FireMode.Semi && !_shotThisPress) ||
                                              (_fireMode == FireMode.Burst && _burstRemaining > 0));
            if (wantsShot && Time.time >= _nextShotTime)
            {
                if (CanFire)
                {
                    Shoot();
                    _shotThisPress = true;
                    if (_fireMode == FireMode.Burst) _burstRemaining--;
                    _nextShotTime = Time.time + (definition != null ? definition.SecondsBetweenShots : 0.1f);
                }
                else if (AmmoInMagazine <= 0 && !IsReloading && !_dryFiredThisPress && Owner != null && Owner.CanShootNow)
                {
                    _dryFiredThisPress = true;
                    if (definition != null && definition.audio != null) LR2Audio.PlayAt(definition.audio.dryFire, transform.position);
                    DryFired?.Invoke();
                    StopRecoil();
                    if (ReserveAmmo > 0) Owner.RequestReload(); // CoD auto-reload on empty
                }
            }

            // Semi/burst: release recoil after one cycle like the Kinemation demo.
            if (_recoilActive && (!_triggerHeld || _fireMode != FireMode.Auto) && Time.time >= _nextShotTime) StopRecoil();

            float shotInterval = definition != null ? definition.SecondsBetweenShots : 0.1f;
            if (_consecutiveHeatShots > 0 && Time.time - _lastHeatShotTime > Mathf.Max(0.18f, shotInterval * 2.1f))
                _consecutiveHeatShots = 0;

            if (_barrelHeat > 0f && Time.time - _lastShotTime > 0.30f)
            {
                _barrelHeat = Mathf.MoveTowards(_barrelHeat, 0f, deltaTime * 0.13f);
            }
            if (_barrelHeat <= 0.001f)
            {
                _barrelHeat = 0f;
                _heatFxUnlocked = false;
                _consecutiveHeatShots = 0;
            }
            UpdateBarrelHeatFX();
        }

        private void Shoot()
        {
            EnsureHeatFX();
            float shotInterval = definition != null ? definition.SecondsBetweenShots : 0.1f;
            if (Time.time - _lastHeatShotTime > Mathf.Max(0.18f, shotInterval * 2.1f))
                _consecutiveHeatShots = 0;
            _lastHeatShotTime = Time.time;
            _consecutiveHeatShots++;
            if (_consecutiveHeatShots >= 20) _heatFxUnlocked = true;
            _barrelHeat = Mathf.Clamp01(_barrelHeat + 0.042f);
            if (_heatFxUnlocked && _barrelSmoke != null &&
                (_consecutiveHeatShots == 20 || _consecutiveHeatShots % 3 == 0))
                _barrelSmoke.Emit(1);
            UpdateBarrelHeatFX();
            AmmoInMagazine--;
            _shotCounter++;
            var owner = Owner;
            var combatant = owner.Combatant;
            Vector3 origin = owner.AimOrigin;
            Vector3 aimDir = owner.AimDirection;
            float spread = CurrentSpread;
            int pellets = definition != null ? Mathf.Max(1, definition.pellets) : 1;
            int tracerEvery = combatant != null && combatant.IsLocalPlayer
                ? (definition != null ? definition.tracerEvery : 1)
                : botTracerEvery;
            bool tracer = tracerEvery > 0 && _shotCounter % tracerEvery == 0;

            Vector3 firstEnd = Vector3.zero;
            bool haveEnd = false;
            for (int i = 0; i < pellets; i++)
            {
                Vector3 dir = LR2Math.RandomInCone(aimDir, spread);
                var shot = Ballistics.Fire(combatant, definition, origin, dir, Muzzle.position, tracer && i == 0, gameObject);
                if (i == 0) { firstEnd = shot.endPoint; haveEnd = true; }
            }

            if (definition != null) _bloom = Mathf.Min(definition.maxBloom, _bloom + definition.bloomPerShot);

            // Visuals / Kinemation
            if (muzzleFlash != null && !_suppressorFitted) muzzleFlash.Play(); // B136: a fitted suppressor hides the flash
            if (shellEjector != null && definition != null && definition.audio != null)
                shellEjector.Eject(owner.Movement != null ? owner.Movement.Velocity : Vector3.zero, definition.audio.shellCasings);
            if (_weaponAnimator != null)
            {
                _weaponAnimator.speed = 1f;
                _weaponAnimator.Play("Fire", 0, 0f);
            }
            bool isBot = combatant == null || !combatant.IsLocalPlayer;
            if (_cameraController != null) _cameraController.PlayCameraShake(cameraShake);
            if (fireClip != null && _playables != null) _playables.PlayAnimation(fireClip);
            if (_recoilAnimation != null && recoilData != null)
            {
                if (isBot) _recoilAnimation.isAiming = true;
                _recoilAnimation.Play();
            }
            if (_recoilPattern != null)
            {
                ScalePatternForShot(owner.IsAiming, true, isBot ? 0.16f : 1f);
                _recoilPattern.OnFireStart();
            }
            else
            {
                // guns without a Kinemation pattern (MK23) still follow the spray weave for the view kick below
                if (Time.time - _lastShotAt > SprayResetSeconds) _sprayIndex = 0;
                _sprayWeaveNow = SprayWeave[_sprayIndex++ % SprayWeave.Length];
                _lastShotAt = Time.time;
            }
            _recoilActive = true;

            if (definition != null)
            {
                Vector2 kick = definition.cameraKick * (owner.IsAiming ? definition.adsKickMultiplier : 1f) * (isBot ? 0.20f : 1f);
                owner.AddViewKick(new Vector2(kick.y * Mathf.Clamp(_sprayWeaveNow * 2f, -1f, 1f), -kick.x)); // B47: side follows the weave
            }

            PlayShotAudio();
            _lastShotTime = Time.time;

            // Gameplay events
            bool suppressed = IsSuppressed;
            if (combatant != null)
            {
                combatant.stats.shotsFired++;
                if (!suppressed) combatant.LastLoudShotTime = Time.time;
            }
            if (owner.Health != null) owner.Health.CancelSpawnProtection();
            float radius = (definition != null ? definition.audibleRadius : 60f) * (_suppressorFitted ? 0.35f : 1f);
            GameEvents.RaiseShotFired(new ShotEvent
            {
                shooter = combatant,
                origin = origin,
                direction = aimDir,
                endPoint = firstEnd,
                hasEndPoint = haveEnd,
                audibleRadius = radius,
                suppressed = suppressed,
                aiming = owner.IsAiming,
                indoors = _indoors,
                weaponName = definition != null ? definition.shortName : DisplayName,
                audioProfile = definition != null ? definition.audio : null
            });
            GameEvents.RaiseNoise(combatant, Muzzle.position, radius, NoiseType.Gunshot);
            Fired?.Invoke();
            AmmoChanged?.Invoke();
        }

        private void PlayShotAudio()
        {
            if (definition == null || definition.audio == null) return;
            var a = definition.audio;
            Vector3 pos = Muzzle.position;
            var combatant = Owner != null ? Owner.Combatant : null;
            if (_shotCounter % 4 == 1 || Time.time - _lastShotTime > 0.4f) _indoors = LR2Audio.IsIndoors(pos);
            float overheat = Mathf.Clamp01((_barrelHeat - 0.30f) / 0.70f);
            bool firstPerson = combatant != null && combatant.IsLocalPlayer;
            bool aiming = Owner != null && Owner.IsAiming;
            bool firstShot = Time.time - _lastShotTime > 0.35f;
            PlayWeaponShotAudio(a, pos, firstPerson, _suppressorFitted, aiming, _indoors, firstShot, overheat, _shotCounter, _triggerHeld);
            if (!_suppressorFitted && (_shotCounter % 2 == 0 || !_triggerHeld)) PlayEchoes(pos, a.TailFor(_indoors));
        }

        /// <summary>
        /// B143: Shared weapon audio synthesis/routing so live 1P, 3P enemies/allies, and Killcam killer POV replay
        /// preserve the exact same weapon core character, suppressor state, mech layer, and indoor/outdoor tails.
        /// </summary>
        public static void PlayWeaponShotAudio(
            WeaponAudioProfile a, Vector3 pos, bool firstPerson, bool suppressed, bool aiming,
            bool indoors, bool firstShot, float overheat, int shotCounter, bool triggerHeld)
        {
            if (a == null) return;
            float sup = suppressed ? 0.42f : 1f;
            float heatPitch = overheat > 0f ? Mathf.Lerp(1f, 1.16f, overheat) * (1f + UnityEngine.Random.Range(-0.03f, 0.07f) * overheat) : 1f;
            float supPitch = suppressed ? 1.04f : 1f;
            // B146: suppressed tactical rifles keep a real ballistic bullet-fire body layered beneath the baffle thump
            bool hasDistinctBulletFire = a.firePlayer != null && a.firePlayer.IsValid
                && (a.firePlayerSuppressed == null || !a.firePlayerSuppressed.IsValid
                    || a.firePlayer.clips[0] != a.firePlayerSuppressed.clips[0]);

            if (firstPerson)
            {
                if (suppressed)
                {
                    SoundSet shotSet = a.firePlayerSuppressed.IsValid ? a.firePlayerSuppressed : a.firePlayer;
                    float shotVol = (a.firePlayerSuppressed.IsValid ? 1.0f : 0.48f) + overheat * 0.14f;
                    LR2Audio.PlayAt(shotSet, pos, shotVol, 0f, false, heatPitch * (a.firePlayerSuppressed.IsValid ? 1f : supPitch));
                    if (hasDistinctBulletFire)
                        LR2Audio.PlayAt(a.firePlayer, pos, 0.60f + overheat * 0.12f, 0f, false, heatPitch * 1.04f);
                    LR2Audio.PlayAt(aiming && a.fireMechAds.IsValid ? a.fireMechAds : a.fireMech, pos, 0.95f + overheat * 0.35f, 0f, false, heatPitch);
                    SoundSet supTail = a.SuppressedTailFor(indoors);
                    if (supTail.IsValid && (shotCounter % 2 == 0 || !triggerHeld))
                        LR2Audio.PlayAt(supTail, pos, indoors ? 0.72f : 0.60f, 0f, false, 1f + overheat * 0.06f);
                    if (hasDistinctBulletFire && (shotCounter % 2 == 0 || !triggerHeld))
                        LR2Audio.PlayAt(a.TailFor(indoors), pos, indoors ? 0.34f : 0.26f, 0f, false, 1.08f + overheat * 0.06f);
                    LR2Audio.Duck(indoors ? 0.42f : 0.30f);
                }
                else
                {
                    bool useFirst = firstShot && a.fireFirstShot.IsValid;
                    LR2Audio.PlayAt(useFirst ? a.fireFirstShot : a.firePlayer, pos, 1f + overheat * 0.18f, 0f, false, heatPitch);
                    LR2Audio.PlayAt(aiming && a.fireMechAds.IsValid ? a.fireMechAds : a.fireMech, pos, 1f + overheat * 0.45f, 0f, false, heatPitch * (1f + overheat * 0.12f));
                    if (overheat > 0.25f && a.fireMech.IsValid)
                        LR2Audio.PlayAt(a.fireMech, pos, 0.44f * overheat, 0.012f, true, 1.48f + overheat * 0.18f);
                    if (shotCounter % 2 == 0 || !triggerHeld) LR2Audio.PlayAt(a.TailFor(indoors), pos, 1f, 0f, false, 1f + overheat * 0.08f);
                    LR2Audio.Duck(indoors ? 0.75f : 0.55f);
                }
                return;
            }

            Vector3 listener = LR2Audio.ListenerPosition;
            float dist = Vector3.Distance(listener, pos);
            float travelDelay = dist / 343f;
            if (suppressed)
            {
                SoundSet coreSet = (dist < 24f && a.firePlayerSuppressed.IsValid)
                    ? a.firePlayerSuppressed
                    : (a.fireNpcSuppressed.IsValid ? a.fireNpcSuppressed : a.firePlayerSuppressed);
                if (coreSet != null && coreSet.IsValid)
                {
                    LR2Audio.PlayClip(coreSet.Pick(), pos, coreSet.volume * (0.96f + overheat * 0.12f),
                        coreSet.PickPitch() * heatPitch, 4f, 64f, 1f, coreSet.category, coreSet.priority, travelDelay);
                }
                if (hasDistinctBulletFire && dist < 56f)
                {
                    LR2Audio.PlayClip(a.firePlayer.Pick(), pos, a.firePlayer.volume * (0.50f + overheat * 0.10f),
                        a.firePlayer.PickPitch() * heatPitch * 1.04f, 4f, 64f, 1f, a.firePlayer.category, a.firePlayer.priority, travelDelay);
                }
                if (dist < 12f && a.fireMech.IsValid)
                    LR2Audio.PlayClip(a.fireMech.Pick(), pos, a.fireMech.volume * 0.62f, a.fireMech.PickPitch(), 2f, 16f, 1f, a.fireMech.category, a.fireMech.priority, travelDelay);
                SoundSet supTail = a.SuppressedTailFor(indoors);
                if (supTail.IsValid && dist < 30f && (shotCounter % 2 == 0 || !triggerHeld))
                    LR2Audio.PlayClip(supTail.Pick(), pos, supTail.volume * 0.58f, supTail.PickPitch(), 4f, 55f, 1f, supTail.category, supTail.priority, travelDelay);
                if (hasDistinctBulletFire && dist < 28f && (shotCounter % 2 == 0 || !triggerHeld))
                {
                    SoundSet bTail = a.TailFor(indoors);
                    if (bTail != null && bTail.IsValid)
                        LR2Audio.PlayClip(bTail.Pick(), pos, bTail.volume * 0.24f, bTail.PickPitch() * 1.08f, 4f, 60f, 1f, bTail.category, bTail.priority, travelDelay);
                }
                if (dist < 16f) LR2Audio.Duck(0.20f);
            }
            else
            {
                // Use the exact same gun's core shot out to 28m in true 3D space so bots & players never sound like different guns.
                SoundSet shotSet = (dist < 28f && a.firePlayer.IsValid)
                    ? a.firePlayer
                    : a.NpcShotForDistance(dist);
                if (shotSet != null && shotSet.IsValid)
                {
                    float maxDist = dist < 28f ? 120f : Mathf.Max(90f, shotSet.maxDistance);
                    LR2Audio.PlayClip(shotSet.Pick(), pos, shotSet.volume * (1f + overheat * 0.14f) * sup,
                        shotSet.PickPitch() * heatPitch, Mathf.Max(3.5f, shotSet.minDistance), maxDist, 1f, shotSet.category, shotSet.priority, travelDelay);
                }
                if (dist < 12f && a.fireMech.IsValid)
                    LR2Audio.PlayClip(a.fireMech.Pick(), pos, a.fireMech.volume * 0.65f, a.fireMech.PickPitch(), 2.5f, 16f, 1f, a.fireMech.category, a.fireMech.priority, travelDelay);
                SoundSet tail = a.TailFor(indoors);
                if (tail.IsValid && dist < 38f && (shotCounter % 2 == 0 || !triggerHeld))
                    LR2Audio.PlayClip(tail.Pick(), pos, tail.volume * 0.68f, tail.PickPitch(), 5f, 110f, 1f, tail.category, tail.priority, travelDelay);
                if (dist < 20f) LR2Audio.Duck(0.25f * sup);
            }
        }

        private void EnsureHeatFX()
        {
            if (_heatFxBuilt) return;
            _heatFxBuilt = true;
            Transform m = Muzzle;

            // Sparse, soft masked wisps replace the opaque quads that read as blocks.
            var smokeGo = new GameObject("BarrelHeatSmoke");
            smokeGo.transform.SetParent(m, false);
            _barrelSmoke = smokeGo.AddComponent<ParticleSystem>();
            var main = _barrelSmoke.main;
            main.playOnAwake = false;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.22f, 0.40f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.045f, 0.12f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.022f, 0.045f);
            main.startColor = new Color(0.76f, 0.78f, 0.82f, 0.20f);
            main.gravityModifier = -0.025f;
            main.maxParticles = 8;
            var shape = _barrelSmoke.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 5f;
            shape.radius = 0.004f;
            var sol = _barrelSmoke.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.35f, 1f, 1.6f));
            var colorLife = _barrelSmoke.colorOverLifetime;
            colorLife.enabled = true;
            var alpha = new Gradient();
            alpha.SetKeys(
                new[] { new GradientColorKey(new Color(0.82f, 0.84f, 0.88f), 0f), new GradientColorKey(new Color(0.75f, 0.77f, 0.81f), 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.26f, 0.18f), new GradientAlphaKey(0f, 1f) });
            colorLife.color = new ParticleSystem.MinMaxGradient(alpha);
            var noise = _barrelSmoke.noise;
            noise.enabled = true;
            noise.strength = 0.035f;
            noise.frequency = 0.8f;
            noise.scrollSpeed = 0.15f;
            var pr = smokeGo.GetComponent<ParticleSystemRenderer>();
            var fxLib = LR2.FX.LR2FxLibrary.Instance;
            if (pr != null && fxLib != null && fxLib.barrelSmoke != null)
            {
                // D60: library material (built into the player): the runtime HDRP/Unlit smoke was an opaque box in builds
                pr.sharedMaterial = fxLib.barrelSmoke;
                LR2.FX.LR2FxLibrary.ApplyFlipbook(_barrelSmoke, fxLib.barrelSmokeTiles);
                pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                pr.receiveShadows = false;
                pr.renderMode = ParticleSystemRenderMode.Billboard;
                pr.alignment = ParticleSystemRenderSpace.View;
            }
            else if (pr != null)
            {
                Shader sh = Shader.Find("HDRP/Unlit");
                if (sh != null)
                {
                    var smat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave, name = "LR2 Soft Thermal Smoke" };
                    if (smat.HasProperty("_UnlitColorMap")) smat.SetTexture("_UnlitColorMap", CreateSoftSmokeTexture());
                    if (smat.HasProperty("_UnlitColor")) smat.SetColor("_UnlitColor", Color.white);
                    if (smat.HasProperty("_SurfaceType")) smat.SetFloat("_SurfaceType", 1f);
                    if (smat.HasProperty("_BlendMode")) smat.SetFloat("_BlendMode", 0f);
                    HDMaterial.ValidateMaterial(smat);
                    _barrelSmokeMaterial = smat;
                    pr.sharedMaterial = smat;
                }
                pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                pr.receiveShadows = false;
                pr.renderMode = ParticleSystemRenderMode.Billboard;
                pr.alignment = ParticleSystemRenderSpace.View;
            }
            _barrelSmokeEmission = _barrelSmoke.emission;
            _barrelSmokeEmission.rateOverTime = 0f;
            _barrelSmoke.Play();

            // Diffuse red billboard at the muzzle; no sleeve mesh covers the gun.
            var glowGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            glowGo.name = "MuzzleHeatGlow";
            var col = glowGo.GetComponent<Collider>();
            if (col != null) Destroy(col);
            glowGo.transform.SetParent(m, false);
            glowGo.transform.localPosition = Vector3.back * 0.018f;
            glowGo.transform.localScale = new Vector3(0.075f, 0.075f, 1f);
            _barrelGlowRenderer = glowGo.GetComponent<Renderer>();
            if (_barrelGlowRenderer != null && fxLib != null && fxLib.muzzleHeat != null)
            {
                // D60: per-gun copy of the library material (its colour follows the barrel heat)
                _barrelGlowMat = new Material(fxLib.muzzleHeat) { hideFlags = HideFlags.HideAndDontSave, name = "LR2 Diffuse Muzzle Heat" };
                _barrelGlowRenderer.sharedMaterial = _barrelGlowMat;
                _barrelGlowRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _barrelGlowRenderer.receiveShadows = false;
                _barrelGlowRenderer.enabled = false;
            }
            else if (_barrelGlowRenderer != null)
            {
                Shader sh = Shader.Find("HDRP/Unlit");
                if (sh != null)
                {
                    _barrelGlowMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave, name = "LR2 Diffuse Muzzle Heat" };
                    if (_barrelGlowMat.HasProperty("_UnlitColorMap")) _barrelGlowMat.SetTexture("_UnlitColorMap", CreateGlowTexture());
                    if (_barrelGlowMat.HasProperty("_SurfaceType")) _barrelGlowMat.SetFloat("_SurfaceType", 1f);
                    if (_barrelGlowMat.HasProperty("_BlendMode")) _barrelGlowMat.SetFloat("_BlendMode", 0f);
                    HDMaterial.ValidateMaterial(_barrelGlowMat);
                    _barrelGlowRenderer.sharedMaterial = _barrelGlowMat;
                }
                _barrelGlowRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _barrelGlowRenderer.receiveShadows = false;
                _barrelGlowRenderer.enabled = false;
            }

            var hazeGo = new GameObject("Muzzle Heat Haze");
            hazeGo.transform.SetParent(m, false);
            hazeGo.transform.localPosition = new Vector3(0f, 0.015f, 0.005f);
            _muzzleHeatHaze = hazeGo.AddComponent<HeatHazeEmitter>();
            _muzzleHeatHaze.Configure(new Vector2(0.045f, 0.13f), 0f);
        }

        private void UpdateBarrelHeatFX()
        {
            if (!_heatFxBuilt) return;
            float glowT = _heatFxUnlocked ? Mathf.Clamp01(_barrelHeat) : 0f;
            bool glowActive = glowT > 0.025f;
            if (_barrelGlowRenderer != null)
            {
                _barrelGlowRenderer.enabled = glowActive;
                Camera view = Camera.main;
                if (glowActive && view != null)
                    _barrelGlowRenderer.transform.rotation = Quaternion.LookRotation(
                        view.transform.position - _barrelGlowRenderer.transform.position, view.transform.up);
                if (glowActive && _barrelGlowMat != null)
                {
                    float intensity = Mathf.Lerp(0.04f, 0.22f, glowT);
                    LR2.FX.LR2FxLibrary.SetTint(_barrelGlowMat, new Color(1f, 0.12f, 0.015f, intensity));
                    if (_barrelGlowMat.HasProperty("_EmissiveColor"))
                        _barrelGlowMat.SetColor("_EmissiveColor", new Color(0.8f, 0.07f, 0.008f, 1f) * (glowT * 0.35f));
                }
            }
            if (_muzzleHeatHaze != null) _muzzleHeatHaze.SetStrength(glowActive ? glowT * 0.22f : 0f);
        }

        private void ClearBarrelHeat()
        {
            _barrelHeat = 0f;
            _consecutiveHeatShots = 0;
            _lastHeatShotTime = -10f;
            _heatFxUnlocked = false;
            if (_barrelSmoke != null) _barrelSmoke.Clear(true);
            UpdateBarrelHeatFX();
        }

        private static Texture2D _softSmokeTexture;
        private static Texture2D CreateSoftSmokeTexture()
        {
            if (_softSmokeTexture != null) return _softSmokeTexture;
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "LR2 Soft Thermal Smoke Mask", wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave
            };
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = ((x + 0.5f) / size) * 2f - 1f;
                float v = ((y + 0.5f) / size) * 2f - 1f;
                float edge = Mathf.Clamp01((1f - Mathf.Sqrt(u * u + v * v)) * 1.8f);
                float n1 = Mathf.PerlinNoise(x * 0.105f + 0.3f, y * 0.105f + 0.8f);
                float n2 = Mathf.PerlinNoise(x * 0.26f + 5.1f, y * 0.26f + 1.7f);
                float noise = Mathf.Lerp(0.38f, 1f, n1 * 0.72f + n2 * 0.28f);
                pixels[y * size + x] = new Color(1f, 1f, 1f, edge * edge * noise);
            }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            _softSmokeTexture = texture;
            return texture;
        }

        private static Texture2D _glowTexture;
        private static Texture2D CreateGlowTexture()
        {
            if (_glowTexture != null) return _glowTexture;
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "LR2 Diffuse Muzzle Glow Mask", wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave
            };
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = ((x + 0.5f) / size) * 2f - 1f;
                float v = ((y + 0.5f) / size) * 2f - 1f;
                float alpha = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Sqrt(u * u + v * v)), 2.4f);
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            _glowTexture = texture;
            return texture;
        }

        private void OnDestroy()
        {
            if (_barrelSmokeMaterial != null) Destroy(_barrelSmokeMaterial);
            if (_barrelGlowMat != null) Destroy(_barrelGlowMat);
            if (_runtimeAdsSettings != null) Destroy(_runtimeAdsSettings);
            if (_runtimeProfile != null) Destroy(_runtimeProfile);
            if (_grenadeRuntime != null) Destroy(_grenadeRuntime);
        }

        private static readonly RaycastHit[] EchoHits = new RaycastHit[1];

        /// <summary>
        /// Early reflections: probes 8 horizontal directions from the muzzle; every wall / building face between 5 and
        /// 90 m bounces a delayed (speed of sound, there and back), quieter copy of the tail back from that surface.
        /// Courtyards and long walls slap back, open forest stays soft.
        /// </summary>
        private void PlayEchoes(Vector3 origin, SoundSet tail)
        {
            if (tail == null || !tail.IsValid) return;
            int played = 0;
            float spin = (_shotCounter * 37f) % 45f;
            for (int i = 0; i < 8 && played < 3; i++)
            {
                Vector3 dir = Quaternion.Euler(UnityEngine.Random.Range(-4f, 6f), spin + i * 45f, 0f) * Vector3.forward;
                if (Physics.RaycastNonAlloc(origin, dir, EchoHits, 90f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore) == 0) continue;
                var hit = EchoHits[0];
                if (hit.distance < 5f || hit.collider is TerrainCollider) continue;
                float delay = hit.distance * 2f / 343f;
                float vol = Mathf.Lerp(0.7f, 0.22f, hit.distance / 90f) * (_indoors ? 0.6f : 1f);
                LR2Audio.PlayAt(tail, hit.point + hit.normal * 0.5f, vol, delay);
                played++;
            }
        }

        // ------------------------------------------------------------------ reload
        public override bool OnReload()
        {
            if (IsReloading || AmmoInMagazine >= ReloadCapacity || ReserveAmmo <= 0 || Time.time < _equipReadyAt) return false;

            ClearBarrelHeat();
            _reloadEmpty = AmmoInMagazine == 0;
            bool hasClip = FPSAnimationAsset.IsValid(reloadClip);
            // Only empty-reload clips exist, so a tactical reload plays the same clip and ends right after the mag seats.
            var cues = definition != null ? definition.audio : null;
            bool tacticalNow = tacticalReload && !_reloadEmpty && hasClip && cues != null;
            _activeReloadRate = ReloadRate * (tacticalNow ? TacticalSpeedUp : 1f);
            if (hasClip && _reloadRuntime == null)
            {
                _reloadRuntime = Instantiate(reloadClip);
                var bt = _reloadRuntime.blendTime;
                bt.rateScale = ReloadRate;
                _reloadRuntime.blendTime = bt;
            }
            if (hasClip && _tacticalRuntime == null)
            {
                _tacticalRuntime = Instantiate(reloadClip);
                var bt = _tacticalRuntime.blendTime;
                bt.rateScale = ReloadRate * TacticalSpeedUp;
                bt.blendOutTime = TacticalBlendOut;
                _tacticalRuntime.blendTime = bt;
            }
            _reloadDuration = hasClip
                ? reloadClip.clip.length / _activeReloadRate
                : (definition != null ? (_reloadEmpty ? definition.emptyReloadTime : definition.reloadTime) / reloadSpeedMultiplier : 2.4f);
            float emptyEnd = (hasClip && cues != null) ? Mathf.Clamp(cues.emptyReloadEndAt, 0.60f, 0.96f) : (hasClip ? 0.84f : 1f);
            float tacTarget = cues != null ? cues.magInAt + Mathf.Max(tacticalEndAfterMagIn, 0.02f) : 0.56f;
            float tacMax = cues != null && cues.boltAt > cues.magInAt + 0.03f ? cues.boltAt - 0.035f : 0.90f;
            _reloadEndAt = tacticalNow
                ? Mathf.Clamp(tacTarget, cues.magInAt + 0.02f, Mathf.Min(tacMax, 0.90f))
                : emptyEnd;
            _reloadStart = Time.time;
            IsReloading = true;
            _magOutPlayed = _magInPlayed = _boltPlayed = false;
            OnFireReleased();

            _tacticalBolt = null;
            if (tacticalReload && !_reloadEmpty)
            {
                _tacticalBolt = FindActiveWeaponPart(transform, "Charge")
                    ?? FindActiveWeaponPart(transform, "ChargingHandle")
                    ?? FindActiveWeaponPart(transform, "Bolt")
                    ?? FindActiveWeaponPart(transform, "Slider");
                if (_tacticalBolt != null) _tacticalBoltClosedPos = _tacticalBolt.localPosition;
            }

            if (hasClip && _playables != null) _playables.PlayAnimation(tacticalNow ? _tacticalRuntime : _reloadRuntime, 0f);
            if (_weaponAnimator != null)
            {
                _weaponAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                _weaponAnimator.Rebind();
                _weaponAnimator.speed = _activeReloadRate;
                _weaponAnimator.Play("Reload", 0, 0f);
                _weaponAnimator.Update(0f);
                if (hasClip && _weaponAnimator.GetCurrentAnimatorClipInfoCount(0) > 0)
                {
                    var clip = _weaponAnimator.GetCurrentAnimatorClipInfo(0)[0].clip;
                    if (clip != null && clip.length > 0.05f && reloadClip.clip != null && reloadClip.clip.length > 0.05f)
                        _weaponAnimator.speed = _activeReloadRate * (clip.length / reloadClip.clip.length);
                }
                if (tacticalReload && !_reloadEmpty && _tacticalBolt != null)
                    _tacticalBolt.localPosition = _tacticalBoltClosedPos;
            }
            if (_cameraController != null) _cameraController.PlayCameraAnimation(cameraReloadAnimation);

            ReloadStarted?.Invoke();
            GameEvents.RaiseNoise(Owner != null ? Owner.Combatant : null, transform.position, 9f, NoiseType.Reload);
            return true;
        }

        private void LateUpdate()
        {
            if (IsReloading && tacticalReload && !_reloadEmpty && _tacticalBolt != null)
                _tacticalBolt.localPosition = _tacticalBoltClosedPos;
        }

        private static Transform FindActiveWeaponPart(Transform root, string targetName)
        {
            if (root == null || !root.gameObject.activeSelf) return null;
            if (string.Equals(root.name, targetName, StringComparison.Ordinal)) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindActiveWeaponPart(root.GetChild(i), targetName);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Aggressive reload feel: camera jolts on the local player's reload cues (B27/B29).</summary>
        private void LocalReloadJolt(int cue) { if (Owner != null && Owner.IsLocalPlayer) CameraShake.ReloadCue(cue); }

        private void UpdateReload()
        {
            float t = ReloadProgress;
            var a = definition != null ? definition.audio : null;
            if (a != null)
            {
                if (!_magOutPlayed && t >= a.magOutAt) { _magOutPlayed = true; LR2Audio.PlayAt(a.magOut, transform.position); LocalReloadJolt(0); }
                if (!_magInPlayed && t >= a.magInAt) { _magInPlayed = true; LR2Audio.PlayAt(a.magIn, transform.position); LocalReloadJolt(1); }
                if (!_boltPlayed && _reloadEmpty && t >= a.boltAt) { _boltPlayed = true; LR2Audio.PlayAt(a.boltCharge, transform.position); LocalReloadJolt(2); }
            }
            if (t < _reloadEndAt) return;

            bool earlyExit = _reloadEndAt < 0.99f;
            bool tactical = _reloadEmpty == false && earlyExit;
            int need = ReloadCapacity - AmmoInMagazine;
            int take = Mathf.Min(need, ReserveAmmo);
            AmmoInMagazine += take;
            ReserveAmmo -= take;
            IsReloading = false;
            if (earlyExit)
            {
                // Cut before the robot's recovery gun rotation at the tail of the clip and smoothly blend back to ready (B136k).
                if (_playables != null) _playables.StopAnimation(tactical ? TacticalBlendOut : EmptyBlendOut);
                if (_weaponAnimator != null) { _weaponAnimator.Rebind(); _weaponAnimator.Play("Empty", 0, 0f); _weaponAnimator.Update(0f); }
            }
            if (_weaponAnimator != null) _weaponAnimator.speed = 1f;
            AmmoChanged?.Invoke();
            ReloadFinished?.Invoke();
        }

        /// <summary>
        /// Sprinting interrupts a reload like Call of Duty (B16/D40): once the new magazine has seated (mag-in cue) the
        /// rounds count, otherwise the magazine is unchanged; the arms ease back to ready instead of reloading off-screen.
        /// </summary>
        public void InterruptReload()
        {
            if (!IsReloading) return;
            var a = definition != null ? definition.audio : null;
            bool seated = a != null && ReloadProgress >= a.magInAt;
            if (seated)
            {
                int take = Mathf.Min(ReloadCapacity - AmmoInMagazine, ReserveAmmo);
                AmmoInMagazine += take;
                ReserveAmmo -= take;
            }
            IsReloading = false;
            if (_playables != null) _playables.StopAnimation(0.3f);
            if (_weaponAnimator != null)
            {
                _weaponAnimator.speed = 1f;
                _weaponAnimator.Rebind();
                _weaponAnimator.Play("Empty", 0, 0f);
                _weaponAnimator.Update(0f);
            }
            if (seated)
            {
                AmmoChanged?.Invoke();
                ReloadFinished?.Invoke();
            }
        }

        public override void CancelActions()
        {
            _triggerHeld = false;
            StopRecoil();
            // B59a: Always release ADS on cancel/death/unequip so rebuilt playables never stay locked in ADS.
            OnAimReleased();
            if (!IsReloading) return;
            IsReloading = false;
            if (_weaponAnimator != null)
            {
                _weaponAnimator.speed = 1f;
                _weaponAnimator.Rebind();
                _weaponAnimator.Play("Empty", 0, 0f);
                _weaponAnimator.Update(0f);
            }
            if (_playables != null) _playables.StopAnimation(0.15f);
        }

        public override void RefillAmmo()
        {
            _ammoInitialized = true;
            AmmoInMagazine = MagazineSize;
            ReserveAmmo = definition != null ? definition.startingReserve : 90;
            IsReloading = false;
            _bloom = 0f;
            AmmoChanged?.Invoke();
        }

        public override int AddReserveAmmo(int rounds)
        {
            int max = definition != null ? definition.maxReserve : 210;
            int accepted = Mathf.Clamp(max - ReserveAmmo, 0, rounds);
            ReserveAmmo += accepted;
            if (accepted > 0) AmmoChanged?.Invoke();
            return accepted;
        }
    }
}
