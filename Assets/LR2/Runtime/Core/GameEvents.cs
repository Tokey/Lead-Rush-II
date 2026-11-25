using System;
using LR2.Combat;
using UnityEngine;

namespace LR2.Core
{
    public enum NoiseType { Footstep, Gunshot, Impact, Explosion, Voice, Reload }

    public enum MatchState { None, Warmup, Countdown, Playing, Ended }

    /// <summary>Raised after damage has been applied to a combatant.</summary>
    public struct HitEvent
    {
        public Combatant attacker;
        public Combatant victim;
        public float damage;          // total soaked by armour + health
        public float armorDamage;     // part of damage the armour absorbed (B74)
        public bool armorBroken;      // this hit took the victim's armour to zero
        public HitZone zone;
        public DamageType type;
        public bool killed;
        public Vector3 point;
        public Vector3 direction;
    }

    public struct KillEvent
    {
        public Combatant killer;      // null for environment / suicide
        public Combatant victim;
        public string weaponName;
        public DamageType type;
        public bool headshot;
        public bool wallbang;
        public float distance;
        public Vector3 victimPosition;
        public Vector3 direction;
    }

    public struct ShotEvent
    {
        public Combatant shooter;
        public Vector3 origin;
        public Vector3 direction;
        /// <summary>B132: where the first round ended (hit point or range limit), after spread; the killcam draws to it.</summary>
        public Vector3 endPoint;
        public bool hasEndPoint;
        public float audibleRadius;
        public bool suppressed;
        public bool aiming;
        public bool indoors;
        public string weaponName;
        public LR2.Weapons.WeaponAudioProfile audioProfile;
    }

    public struct NoiseEvent
    {
        public Combatant source;
        public Vector3 position;
        public float radius;
        public NoiseType type;
    }

    public struct ScoreEvent
    {
        public Combatant combatant;
        public int points;
        public string reason;
    }

    public struct MedalEvent
    {
        public Combatant combatant;
        public string id;
        public string title;
        public int points;
    }

    public struct MatchStateEvent
    {
        public MatchState previous;
        public MatchState current;
    }

    public struct NearMissEvent
    {
        public Combatant shooter;
        public Combatant listener;
        public Vector3 closestPoint;
        public float distance;
    }

    /// <summary>
    /// Global, allocation-free event hub. Every system (UI, AI, scoring, audio) listens here so that gameplay
    /// code never needs direct references to presentation code. Cleared automatically when play mode starts.
    /// </summary>
    public static class GameEvents
    {
        public static event Action<HitEvent> Hit;
        public static event Action<KillEvent> Killed;
        public static event Action<ShotEvent> ShotFired;
        public static event Action<NoiseEvent> Noise;
        public static event Action<ScoreEvent> ScoreAwarded;
        public static event Action<MedalEvent> MedalAwarded;
        public static event Action<MatchStateEvent> MatchStateChanged;
        public static event Action<NearMissEvent> NearMiss;
        public static event Action<Combatant> CombatantSpawned;
        public static event Action<Combatant> CombatantDespawned;

        public static void RaiseHit(in HitEvent e)
        {
            var handlers = Hit;
            if (handlers == null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((Action<HitEvent>)d)(e); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }
        public static void RaiseKilled(in KillEvent e) => Killed?.Invoke(e);
        public static void RaiseShotFired(in ShotEvent e) => ShotFired?.Invoke(e);
        public static void RaiseNoise(in NoiseEvent e) => Noise?.Invoke(e);
        public static void RaiseScore(in ScoreEvent e) => ScoreAwarded?.Invoke(e);
        public static void RaiseMedal(in MedalEvent e) => MedalAwarded?.Invoke(e);
        public static void RaiseMatchState(in MatchStateEvent e) => MatchStateChanged?.Invoke(e);
        public static void RaiseNearMiss(in NearMissEvent e) => NearMiss?.Invoke(e);
        public static void RaiseSpawned(Combatant c) => CombatantSpawned?.Invoke(c);
        public static void RaiseDespawned(Combatant c) => CombatantDespawned?.Invoke(c);

        public static void RaiseNoise(Combatant source, Vector3 position, float radius, NoiseType type)
        {
            Noise?.Invoke(new NoiseEvent { source = source, position = position, radius = radius, type = type });
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Hit = null;
            Killed = null;
            ShotFired = null;
            Noise = null;
            ScoreAwarded = null;
            MedalAwarded = null;
            MatchStateChanged = null;
            NearMiss = null;
            CombatantSpawned = null;
            CombatantDespawned = null;
        }
    }
}
