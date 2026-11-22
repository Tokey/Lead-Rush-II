using System;
using UnityEngine;

namespace LR2.Characters
{
    public enum SoldierStance { Standing, Crouching, Prone }

    public enum SoldierMoveState { Idle, Walking, Sprinting, InAir, Sliding, Mantling }

    /// <summary>
    /// One frame of "controller" input. Humans fill it from devices, bots from their brain. Because both drive the
    /// exact same SoldierController, bots move, aim, recoil and reload under the same rules as the player.
    /// </summary>
    [Serializable]
    public struct SoldierInput
    {
        public Vector2 move;
        public Vector2 look;
        public bool absoluteLook;
        public float aimYaw;
        public float aimPitch;
        public bool fire;
        public bool aim;
        public bool sprint;
        public bool reload;
        public bool jump;
        public bool crouch;
        public bool prone;
        /// <summary>B90: crouch while held (X). A held state, not a one-shot.</summary>
        public bool crouchHold;
        /// <summary>B90: slide request (double tap of the crouch-hold key). One-shot.</summary>
        public bool slide;
        public float lean;
        public int weaponSlot;
        public bool nextWeapon;
        public bool interact;
        /// <summary>B136 (Kinemation demo features): throw a grenade (G). One-shot.</summary>
        public bool grenade;
        /// <summary>B136: switch to the next sight group while aiming (V, e.g. optic to canted sight). One-shot.</summary>
        public bool cycleScope;
        /// <summary>B136: cycle semi / burst / auto (B). One-shot.</summary>
        public bool fireMode;
        /// <summary>B136: enter or leave attachment editing (I, Kinemation's inspect pose). One-shot.</summary>
        public bool inspect;
        /// <summary>B136: while editing, cycle attachment group 1 barrel / 2 grip / 3 sight (-1 = none). One-shot.</summary>
        public int attachmentSlot;

        public static SoldierInput Empty => new SoldierInput { weaponSlot = -1, attachmentSlot = -1 };

        public void ResetOneShots()
        {
            reload = false;
            jump = false;
            crouch = false;
            prone = false;
            slide = false;
            nextWeapon = false;
            interact = false;
            weaponSlot = -1;
            grenade = false;
            cycleScope = false;
            fireMode = false;
            inspect = false;
            attachmentSlot = -1;
        }

        public void ClearAll()
        {
            this = Empty;
        }
    }

    public interface ISoldierInputSource
    {
        void GetInput(SoldierController soldier, ref SoldierInput input);
    }
}
