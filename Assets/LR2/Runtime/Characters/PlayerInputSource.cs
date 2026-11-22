using LR2.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LR2.Characters
{
    /// <summary>
    /// Keyboard/mouse + gamepad bindings for the local player (Input System, defined in code so nothing can be
    /// lost in an asset). Produces <see cref="SoldierInput"/> for the SoldierController.
    /// </summary>
    [RequireComponent(typeof(SoldierController))]
    public class PlayerInputSource : MonoBehaviour, ISoldierInputSource
    {
        [Header("Mouse")]
        [Tooltip("Degrees of rotation per mouse count at sensitivity 1.0 (0.07 = snappy CoD-style 1:1 mouse feel).")]
        [SerializeField] private float degreesPerCount = 0.07f;

        [Header("Gamepad")]
        [SerializeField] private float gamepadYawSpeed = 260f;
        [SerializeField] private float gamepadPitchSpeed = 180f;
        [SerializeField, Range(0f, 0.5f)] private float stickDeadzone = 0.12f;

        [Header("Behaviour")]
        [SerializeField] private bool toggleAim;
        [SerializeField] private bool toggleSprint;
        [Tooltip("Sprint automatically while moving forward (no key needed).")]
        [SerializeField] private bool autoSprint;

        private InputAction _move, _look, _lookStick, _fire, _aim, _sprint, _jump, _crouch, _prone, _reload, _leanL, _leanR,
            _slot1, _slot2, _next, _scroll, _interact, _laser, _crouchHold,
            _slot3, _slot4, _grenade, _cycleScope, _fireMode, _inspect; // B136
        private bool _aimToggled, _sprintToggled;
        // B90 (user 00:13): hold X to crouch, double-tap X to slide
        private const float SlideDoubleTapSeconds = 0.3f;
        private float _lastCrouchHoldPress = -10f;

        private void OnEnable()
        {
            _move = new InputAction("Move", InputActionType.Value);
            _move.AddCompositeBinding("2DVector").With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
            _move.AddBinding("<Gamepad>/leftStick");

            _look = new InputAction("Look", InputActionType.Value, "<Mouse>/delta");
            _lookStick = new InputAction("LookStick", InputActionType.Value, "<Gamepad>/rightStick");
            _fire = new InputAction("Fire", InputActionType.Button, "<Mouse>/leftButton");
            _fire.AddBinding("<Gamepad>/rightTrigger");
            _aim = new InputAction("Aim", InputActionType.Button, "<Mouse>/rightButton");
            _aim.AddBinding("<Gamepad>/leftTrigger");
            _sprint = new InputAction("Sprint", InputActionType.Button, "<Keyboard>/leftShift");
            _sprint.AddBinding("<Gamepad>/leftStickPress");
            _jump = new InputAction("Jump", InputActionType.Button, "<Keyboard>/space");
            _jump.AddBinding("<Gamepad>/buttonSouth");
            _crouch = new InputAction("Crouch", InputActionType.Button, "<Keyboard>/c");
            _crouch.AddBinding("<Keyboard>/leftCtrl");
            _crouch.AddBinding("<Gamepad>/buttonEast");
            _prone = new InputAction("Prone", InputActionType.Button, "<Keyboard>/z");
            _prone.AddBinding("<Gamepad>/dpad/down");
            _crouchHold = new InputAction("CrouchHold", InputActionType.Button, "<Keyboard>/x");
            _crouchHold.AddBinding("<Gamepad>/rightStickPress");
            _reload = new InputAction("Reload", InputActionType.Button, "<Keyboard>/r");
            _reload.AddBinding("<Gamepad>/buttonWest");
            _leanL = new InputAction("LeanLeft", InputActionType.Button, "<Keyboard>/q");
            _leanR = new InputAction("LeanRight", InputActionType.Button, "<Keyboard>/e");
            _slot1 = new InputAction("Slot1", InputActionType.Button, "<Keyboard>/1");
            _slot2 = new InputAction("Slot2", InputActionType.Button, "<Keyboard>/2");
            _next = new InputAction("NextWeapon", InputActionType.Button, "<Gamepad>/buttonNorth");
            _scroll = new InputAction("Scroll", InputActionType.Value, "<Mouse>/scroll/y");
            _interact = new InputAction("Interact", InputActionType.Button, "<Keyboard>/f");
            _laser = new InputAction("Laser", InputActionType.Button, "<Keyboard>/l");
            // B136: knife (3) and torch (4) slots, and Kinemation's demo actions on its own keys (G, V, B, I)
            _slot3 = new InputAction("Slot3", InputActionType.Button, "<Keyboard>/3");
            _slot4 = new InputAction("Slot4", InputActionType.Button, "<Keyboard>/4");
            _grenade = new InputAction("Grenade", InputActionType.Button, "<Keyboard>/g");
            _grenade.AddBinding("<Gamepad>/rightShoulder");
            _cycleScope = new InputAction("CycleScope", InputActionType.Button, "<Keyboard>/v");
            _cycleScope.AddBinding("<Gamepad>/dpad/up");
            _fireMode = new InputAction("FireMode", InputActionType.Button, "<Keyboard>/b");
            _fireMode.AddBinding("<Gamepad>/dpad/left");
            _inspect = new InputAction("Inspect", InputActionType.Button, "<Keyboard>/i");
            _inspect.AddBinding("<Gamepad>/dpad/right");

            ApplyBindingsFromRegistry();
            LR2Bindings.Changed -= ApplyBindingsFromRegistry;
            LR2Bindings.Changed += ApplyBindingsFromRegistry;

            foreach (var a in All()) a.Enable();
        }

        private void OnDisable()
        {
            LR2Bindings.Changed -= ApplyBindingsFromRegistry;
            foreach (var a in All())
            {
                if (a == null) continue;
                a.Disable();
                a.Dispose();
            }
        }

        public string GetActiveBindingPath(string actionName)
        {
            var a = FindActionByName(actionName);
            if (a == null) return string.Empty;
            int idx = a == _move ? 1 : 0;
            return idx < a.bindings.Count ? a.bindings[idx].effectivePath : string.Empty;
        }

        private InputAction FindActionByName(string actionName) => actionName switch
        {
            "Move" => _move,
            "Fire" => _fire,
            "Aim" => _aim,
            "Sprint" => _sprint,
            "Jump" => _jump,
            "Crouch" => _crouch,
            "Prone" => _prone,
            "CrouchHold" => _crouchHold,
            "Reload" => _reload,
            "LeanLeft" => _leanL,
            "LeanRight" => _leanR,
            "Interact" => _interact,
            "Laser" => _laser,
            "Slot1" => _slot1,
            "Slot2" => _slot2,
            "Slot3" => _slot3,
            "Slot4" => _slot4,
            "Grenade" => _grenade,
            "CycleScope" => _cycleScope,
            "FireMode" => _fireMode,
            "Inspect" => _inspect,
            _ => null
        };

        private void ApplyBindingsFromRegistry()
        {
            SyncAction(_move, "Move", 1, "<Keyboard>/w");
            SyncAction(_fire, "Fire", 0, "<Mouse>/leftButton");
            SyncAction(_aim, "Aim", 0, "<Mouse>/rightButton");
            SyncAction(_sprint, "Sprint", 0, "<Keyboard>/leftShift");
            SyncAction(_jump, "Jump", 0, "<Keyboard>/space");
            SyncAction(_crouch, "Crouch", 0, "<Keyboard>/c");
            SyncAction(_prone, "Prone", 0, "<Keyboard>/z");
            SyncAction(_crouchHold, "CrouchHold", 0, "<Keyboard>/x");
            SyncAction(_reload, "Reload", 0, "<Keyboard>/r");
            SyncAction(_leanL, "LeanLeft", 0, "<Keyboard>/q");
            SyncAction(_leanR, "LeanRight", 0, "<Keyboard>/e");
            SyncAction(_interact, "Interact", 0, "<Keyboard>/f");
            SyncAction(_laser, "Laser", 0, "<Keyboard>/l");
            SyncAction(_slot1, "Slot1", 0, "<Keyboard>/1");
            SyncAction(_slot2, "Slot2", 0, "<Keyboard>/2");
            SyncAction(_slot3, "Slot3", 0, "<Keyboard>/3");
            SyncAction(_slot4, "Slot4", 0, "<Keyboard>/4");
            SyncAction(_grenade, "Grenade", 0, "<Keyboard>/g");
            SyncAction(_cycleScope, "CycleScope", 0, "<Keyboard>/v");
            SyncAction(_fireMode, "FireMode", 0, "<Keyboard>/b");
            SyncAction(_inspect, "Inspect", 0, "<Keyboard>/i");
        }

        private static void SyncAction(InputAction target, string registryName, int bindingIndex, string fallbackPath)
        {
            if (target == null || bindingIndex < 0 || bindingIndex >= target.bindings.Count) return;
            string path = LR2Bindings.GetPrimaryBindingPath(registryName, fallbackPath);
            if (string.IsNullOrEmpty(path)) return;
            bool wasEnabled = target.enabled;
            if (wasEnabled) target.Disable();
            target.ApplyBindingOverride(bindingIndex, path);
            if (wasEnabled) target.Enable();
        }

        private InputAction[] All() => new[]
        {
            _move, _look, _lookStick, _fire, _aim, _sprint, _jump, _crouch, _prone, _reload, _leanL, _leanR, _slot1, _slot2, _next,
            _scroll, _interact, _laser, _crouchHold, _slot3, _slot4, _grenade, _cycleScope, _fireMode, _inspect
        };

        public void GetInput(SoldierController soldier, ref SoldierInput input)
        {
            if (soldier == null || soldier.IsDead)
            {
                _aimToggled = false;
                _sprintToggled = false;
                input.ClearAll();
                return;
            }
            bool gameplayFocused = Cursor.lockState == CursorLockMode.Locked && Time.timeScale > 0f;
            if (!gameplayFocused)
            {
                _aimToggled = false;
                input.ClearAll();
                return;
            }

            input.absoluteLook = false;
            input.move = Vector2.ClampMagnitude(_move.ReadValue<Vector2>(), 1f);

            float sens = LR2Settings.MouseSensitivity * (soldier.IsAiming ? LR2Settings.AdsSensitivityMultiplier * soldier.ScopeSensitivity : 1f);
            Vector2 mouse = _look.ReadValue<Vector2>() * (degreesPerCount * sens);
            Vector2 stick = _lookStick.ReadValue<Vector2>();
            if (stick.magnitude < stickDeadzone) stick = Vector2.zero;
            stick = stick * stick.magnitude; // response curve
            Vector2 pad = new Vector2(stick.x * gamepadYawSpeed, stick.y * gamepadPitchSpeed) * (Time.deltaTime * sens);
            Vector2 look = mouse + pad;
            input.look = new Vector2(look.x, LR2Settings.InvertY ? look.y : -look.y);

            input.fire = _fire.IsPressed();
            bool useToggleAim = toggleAim || LR2Settings.ToggleAim;
            if (useToggleAim)
            {
                if (_aim.WasPressedThisFrame()) _aimToggled = !_aimToggled;
                input.aim = _aimToggled;
            }
            else input.aim = _aim.IsPressed();

            bool useAutoSprint = autoSprint || LR2Settings.AutoSprint;
            bool useToggleSprint = toggleSprint || LR2Settings.ToggleSprint;
            if (useAutoSprint) input.sprint = input.move.y > 0.7f;
            else if (useToggleSprint)
            {
                if (_sprint.WasPressedThisFrame()) _sprintToggled = !_sprintToggled;
                if (input.move.y < 0.3f) _sprintToggled = false;
                input.sprint = _sprintToggled;
            }
            else input.sprint = _sprint.IsPressed();

            input.jump |= _jump.WasPressedThisFrame();
            input.crouch |= _crouch.WasPressedThisFrame();
            input.prone |= _prone.WasPressedThisFrame();
            input.crouchHold = _crouchHold.IsPressed();
            if (_crouchHold.WasPressedThisFrame())
            {
                // second press inside the window: slide (SoldierMovement checks that the player still carries sprint speed)
                if (Time.unscaledTime - _lastCrouchHoldPress <= SlideDoubleTapSeconds) { input.slide = true; _lastCrouchHoldPress = -10f; }
                else _lastCrouchHoldPress = Time.unscaledTime;
            }
            input.reload |= _reload.WasPressedThisFrame();
            input.interact |= _interact.WasPressedThisFrame();
            if (_laser.WasPressedThisFrame()) LR2Settings.LaserEnabled = !LR2Settings.LaserEnabled;
            input.lean = (_leanR.IsPressed() ? 1f : 0f) - (_leanL.IsPressed() ? 1f : 0f);

            input.grenade |= _grenade.WasPressedThisFrame();
            input.cycleScope |= _cycleScope.WasPressedThisFrame();
            input.fireMode |= _fireMode.WasPressedThisFrame();
            input.inspect |= _inspect.WasPressedThisFrame();

            if (soldier.IsEditingAttachments)
            {
                // Kinemation's attachment editing: 1 barrel, 2 grip, 3 sight (the slot keys are free while the gun is held up)
                if (_slot1.WasPressedThisFrame()) input.attachmentSlot = 1;
                if (_slot2.WasPressedThisFrame()) input.attachmentSlot = 2;
                if (_slot3.WasPressedThisFrame()) input.attachmentSlot = 3;
                return;
            }
            if (_slot1.WasPressedThisFrame()) input.weaponSlot = 0;
            if (_slot2.WasPressedThisFrame()) input.weaponSlot = 1;
            if (_slot3.WasPressedThisFrame()) input.weaponSlot = 2; // B136: knife
            if (_slot4.WasPressedThisFrame()) input.weaponSlot = 3; // B136: torch
            float scroll = _scroll.ReadValue<float>();
            input.nextWeapon |= _next.WasPressedThisFrame() || Mathf.Abs(scroll) > 0.1f;
        }
    }
}
