using System;
using LR2.Combat;
using UnityEngine;

namespace LR2.AI
{
    /// <summary>High-level orders an external commander (squad AI, debug console, future LLM) can give a bot.</summary>
    public enum BotOrder { None, FreeRoam, HoldPosition, MoveTo, Attack, FollowLeader, Flank, Retreat }

    public enum BotArchetype { HumanLike = 0, DumbRobotic = 1, LlmEnhanced = 2, HumanFitted = 3 }

    public enum BotStanceOrder { Auto, Stand, Crouch, Prone }
    public enum BotPeekStyle { Auto, HoldAngle, Jiggle, WideSwing }
    public enum BotBurstStyle { Auto, Tap, Burst, FullAuto }
    public enum BotUtilityAction { None, ThrowFrag, SafeReload, HoldFire }
    public enum BotMicroActionKind { None, SprintTo, StrafeFire, CrouchPeek, HoldAngle, ThrowFrag, FallBackReload, ProneAmbush }

    [Serializable]
    public struct BotMicroAction
    {
        public BotMicroActionKind kind;
        public Vector3 targetPos;
        public float strafeDir;
        public float duration;
        public bool ads;
    }

    [Serializable]
    public struct BotDirective
    {
        public BotOrder order;
        public Vector3 position;
        public Combatant target;
        [Tooltip("Seconds the directive stays active (<= 0 = until replaced).")]
        public float duration;
        [Tooltip("Free text reason (useful for LLM traces / debugging).")]
        public string rationale;
        // B151 optional deep tactical/avatar LLM parameters (0 / Auto keeps default Commander & chat behavior unchanged)
        public BotStanceOrder stance;
        public BotPeekStyle peek;
        public BotBurstStyle burst;
        public BotUtilityAction utility;
        public float aggression;
        public BotMicroAction[] microQueue;
    }

    /// <summary>What the tactical layer decided to do this moment.</summary>
    public enum BotGoal { Idle, Patrol, Investigate, Hunt, Engage, TakeCover, Retreat, Flank, FollowLeader, HoldPosition, MoveTo, Resupply }

    /// <summary>Output of a brain tick, consumed by the motor/aim layer in BotController.</summary>
    public struct BotIntent
    {
        public BotGoal goal;
        public Vector3 destination;
        public bool hasDestination;
        public Combatant target;
        public Vector3 lookPoint;
        public bool hasLookPoint;
        public bool allowSprint;
        public bool crouch;
        public bool prone;
        public bool allowFire;
        public float desiredRange;
        public bool throwGrenade;
        public bool forceReload;
        public BotPeekStyle peekStyle;
        public BotBurstStyle burstStyle;
        public float overrideStrafeDir;
        public bool forceAds;
    }

    /// <summary>Pluggable decision maker. The default is <see cref="TacticalBotBrain"/>; an LLM-driven brain can
    /// implement this (or, better, an <see cref="IBotDirectiveProvider"/>) without touching aiming/movement code.</summary>
    public interface IBotBrain
    {
        void Initialize(BotController bot);
        BotIntent Think(BotBlackboard blackboard, float deltaTime);
        void Reset();
    }

    /// <summary>Slow-tick strategic input (polled ~every 1.5 s). Implement on a MonoBehaviour and assign it to
    /// BotController.directiveProvider — e.g. an LLM client that reads <see cref="BotBlackboard.ToJson"/> and returns orders.</summary>
    public interface IBotDirectiveProvider
    {
        bool TryGetDirective(BotController bot, BotBlackboard blackboard, out BotDirective directive);
    }

    /// <summary>All the world knowledge a brain may use. Rebuilt every frame by BotController.</summary>
    [Serializable]
    public class BotBlackboard
    {
        public float time;
        public Vector3 position;
        public float yaw;
        public float health01;
        public bool hasArmor;
        public float armor01;
        public bool armorBroken;
        public float ammo01;
        public int ammoInMagazine;
        public int reserveAmmo;
        public bool lowAmmo;
        public bool isReloading;
        public bool recentlyDamaged;
        public float lastDamageTime;
        public Vector3 lastDamageDirection;
        public bool underFire;

        public Combatant target;
        public bool targetVisible;
        public Vector3 targetLastKnown;
        public float targetLastSeenTime = -999f;
        public float targetDistance;
        public int knownEnemies;

        public bool hasNoise;
        public Vector3 noisePosition;
        public float noiseTime = -999f;

        public Combatant squadLeader;
        public BotDirective directive;
        public bool hasDirective;
        public float directiveUntil;

        public float effectiveRange = 30f;
        public BotDifficulty difficulty;

        public float TimeSinceSeen => time - targetLastSeenTime;

        [Serializable]
        private struct Snapshot
        {
            public string goal;
            public float health;
            public float armor;
            public float ammo;
            public int reserveAmmo;
            public bool reloading;
            public bool underFire;
            public string target;
            public bool targetVisible;
            public float targetDistance;
            public float secondsSinceSeen;
            public Vector3 position;
            public Vector3 targetLastKnown;
            public int knownEnemies;
            public string directive;
        }

        /// <summary>Compact JSON world-state for LLM prompts / logging.</summary>
        public string ToJson(BotGoal currentGoal)
        {
            var s = new Snapshot
            {
                goal = currentGoal.ToString(),
                health = Mathf.Round(health01 * 100f),
                armor = Mathf.Round(armor01 * 50f),
                ammo = Mathf.Round(ammo01 * 100f),
                reserveAmmo = reserveAmmo,
                reloading = isReloading,
                underFire = underFire,
                target = target != null ? target.DisplayName : "",
                targetVisible = targetVisible,
                targetDistance = Mathf.Round(targetDistance),
                secondsSinceSeen = Mathf.Round(Mathf.Min(TimeSinceSeen, 999f) * 10f) / 10f,
                position = position,
                targetLastKnown = targetLastKnown,
                knownEnemies = knownEnemies,
                directive = hasDirective ? directive.order.ToString() : "None"
            };
            return JsonUtility.ToJson(s);
        }
    }
}
