using UnityEngine;

namespace LR2.Core
{
    /// <summary>
    /// Physics layer layout for Lead Rush II. Indices are fixed (see ProjectSettings/TagManager.asset) so code keeps
    /// working even if a layer name is edited. The collision matrix is also enforced at runtime.
    /// </summary>
    public static class LR2Layers
    {
        public const int Default = 0;
        public const int TransparentFX = 1;
        public const int IgnoreRaycast = 2;
        public const int Obstacle = 3;      // legacy "Osbtacle" layer used by the original LeadRush levels
        public const int Water = 4;
        public const int UI = 5;
        public const int Character = 8;     // CharacterController capsules (never hit by bullets)
        public const int Hitbox = 9;        // per-bone damage colliders (raycast only, never collide)
        public const int Ragdoll = 10;      // dead bodies
        public const int PlayerClip = 11;   // invisible walls: block characters, not bullets
        public const int Debris = 12;       // shell casings, small props that must not block movement

        /// <summary>Static world geometry.</summary>
        public static readonly int EnvironmentMask = (1 << Default) | (1 << Obstacle);

        /// <summary>Everything a bullet can hit.</summary>
        public static readonly int BulletMask = EnvironmentMask | (1 << Water) | (1 << Hitbox) | (1 << Ragdoll);

        /// <summary>Things that block line of sight for AI perception.</summary>
        public static readonly int VisionMask = EnvironmentMask;

        /// <summary>Surfaces used for ground/footstep checks.</summary>
        public static readonly int GroundMask = EnvironmentMask | (1 << Water);

        /// <summary>Used to check if a spawn spot is occupied.</summary>
        public static readonly int CharacterMask = 1 << Character;

        public static int ToMask(params int[] layers)
        {
            int mask = 0;
            foreach (int l in layers) mask |= 1 << l;
            return mask;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ConfigurePhysics()
        {
            for (int i = 0; i < 32; i++) Physics.IgnoreLayerCollision(Hitbox, i, true);
            Physics.IgnoreLayerCollision(Character, Ragdoll, true);
            Physics.IgnoreLayerCollision(Character, Debris, true);
            Physics.IgnoreLayerCollision(Ragdoll, Debris, true);
            Physics.IgnoreLayerCollision(Ragdoll, PlayerClip, true);
            Physics.IgnoreLayerCollision(Debris, PlayerClip, true);
        }
    }
}
