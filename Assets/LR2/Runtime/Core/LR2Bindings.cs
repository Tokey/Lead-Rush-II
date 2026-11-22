using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LR2.Core
{
    /// <summary>
    /// Shared controls menu backend. The gameplay component may keep its own readers,
    /// while menus use this asset to edit, display and persist the same named actions.
    /// </summary>
    public static class LR2Bindings
    {
        private const string OverridesKey = "LR2.InputBindingOverrides";
        private static InputActionAsset _asset;
        private static InputActionMap _gameplay;

        public static event Action Changed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Changed = null;
            _asset = null;
            _gameplay = null;
        }

        public static InputActionAsset Asset
        {
            get
            {
                EnsureCreated();
                return _asset;
            }
        }

        public static IReadOnlyList<InputAction> Actions
        {
            get
            {
                EnsureCreated();
                return _gameplay.actions;
            }
        }

        public static InputAction GetAction(string actionName)
        {
            EnsureCreated();
            return _gameplay.FindAction(actionName, false);
        }

        public static string GetDisplayString(string actionName, int bindingIndex = 0)
        {
            var action = GetAction(actionName);
            return action != null && bindingIndex >= 0 && bindingIndex < action.bindings.Count
                ? action.GetBindingDisplayString(bindingIndex)
                : string.Empty;
        }

        public static InputActionRebindingExtensions.RebindingOperation BeginRebind(
            string actionName, int bindingIndex, Action<string> completed, Action cancelled = null)
        {
            var action = GetAction(actionName);
            if (action == null) throw new ArgumentException("Unknown LR2 action: " + actionName, nameof(actionName));
            if (bindingIndex < 0 || bindingIndex >= action.bindings.Count)
                throw new ArgumentOutOfRangeException(nameof(bindingIndex));

            action.Disable();
            var operation = action.PerformInteractiveRebinding(bindingIndex)
                .WithCancelingThrough("<Keyboard>/escape")
                .OnCancel(_ =>
                {
                    action.Enable();
                    cancelled?.Invoke();
                })
                .OnComplete(op =>
                {
                    action.Enable();
                    Save();
                    completed?.Invoke(action.GetBindingDisplayString(bindingIndex));
                    op.Dispose();
                });
            operation.Start();
            return operation;
        }

        public static string GetPrimaryBindingPath(string actionName, string fallback = null)
        {
            var action = GetAction(actionName);
            if (action == null || action.bindings.Count == 0) return fallback;
            string effective = action.bindings[0].effectivePath;
            return string.IsNullOrEmpty(effective) ? fallback : effective;
        }

        public static void SetBindingOverride(string actionName, int bindingIndex, string controlPath)
        {
            var action = GetAction(actionName);
            if (action == null) throw new ArgumentException("Unknown LR2 action: " + actionName, nameof(actionName));
            if (bindingIndex < 0 || bindingIndex >= action.bindings.Count)
                throw new ArgumentOutOfRangeException(nameof(bindingIndex));

            action.ApplyBindingOverride(bindingIndex, controlPath);
            Save();
        }

        public static void Save()
        {
            EnsureCreated();
            PlayerPrefs.SetString(OverridesKey, _asset.SaveBindingOverridesAsJson());
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        public static void ResetToDefaults()
        {
            EnsureCreated();
            _asset.RemoveAllBindingOverrides();
            PlayerPrefs.DeleteKey(OverridesKey);
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        private static void EnsureCreated()
        {
            if (_asset != null) return;
            _asset = ScriptableObject.CreateInstance<InputActionAsset>();
            _asset.name = "LR2 Runtime Bindings";
            _gameplay = new InputActionMap("Gameplay");
            _asset.AddActionMap(_gameplay);
            Add("Move", InputActionType.Value, "<Keyboard>/w", "<Gamepad>/leftStick");
            Add("Look", InputActionType.Value, "<Mouse>/delta", "<Gamepad>/rightStick");
            Add("Fire", InputActionType.Button, "<Mouse>/leftButton", "<Gamepad>/rightTrigger");
            Add("Aim", InputActionType.Button, "<Mouse>/rightButton", "<Gamepad>/leftTrigger");
            Add("Sprint", InputActionType.Button, "<Keyboard>/leftShift", "<Gamepad>/leftStickPress");
            Add("Jump", InputActionType.Button, "<Keyboard>/space", "<Gamepad>/buttonSouth");
            Add("Crouch", InputActionType.Button, "<Keyboard>/c", "<Gamepad>/buttonEast");
            Add("Prone", InputActionType.Button, "<Keyboard>/z", "<Gamepad>/dpad/down");
            Add("CrouchHold", InputActionType.Button, "<Keyboard>/x", "<Gamepad>/rightStickPress"); // B90: hold to crouch, double-tap to slide
            Add("Reload", InputActionType.Button, "<Keyboard>/r", "<Gamepad>/buttonWest");
            Add("LeanLeft", InputActionType.Button, "<Keyboard>/q");
            Add("LeanRight", InputActionType.Button, "<Keyboard>/e");
            Add("Interact", InputActionType.Button, "<Keyboard>/f");
            Add("Laser", InputActionType.Button, "<Keyboard>/l");
            Add("Slot1", InputActionType.Button, "<Keyboard>/1");
            Add("Slot2", InputActionType.Button, "<Keyboard>/2");
            // B136: Kinemation demo features (its own keys where LR2 had none: G, V, B, I) and the knife / torch slots
            Add("Slot3", InputActionType.Button, "<Keyboard>/3");
            Add("Slot4", InputActionType.Button, "<Keyboard>/4");
            Add("Grenade", InputActionType.Button, "<Keyboard>/g", "<Gamepad>/rightShoulder");
            Add("CycleScope", InputActionType.Button, "<Keyboard>/v", "<Gamepad>/dpad/up");
            Add("FireMode", InputActionType.Button, "<Keyboard>/b", "<Gamepad>/dpad/left");
            Add("Inspect", InputActionType.Button, "<Keyboard>/i", "<Gamepad>/dpad/right");
            Add("NextWeapon", InputActionType.Button, "<Gamepad>/buttonNorth");
            Add("Scroll", InputActionType.Value, "<Mouse>/scroll/y");

            var json = PlayerPrefs.GetString(OverridesKey, string.Empty);
            if (!string.IsNullOrEmpty(json)) _asset.LoadBindingOverridesFromJson(json);
        }

        private static void Add(string name, InputActionType type, params string[] bindings)
        {
            var action = _gameplay.AddAction(name, type);
            foreach (var binding in bindings) action.AddBinding(binding);
        }
    }
}
