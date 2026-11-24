using System;
using System.Collections.Generic;
using LR2.Combat;
using LR2.Core;
using UnityEngine;

namespace LR2.AI
{
    public class ThreatRecord
    {
        public Combatant combatant;
        public Vector3 lastKnownPosition;
        public float lastSeenTime = -999f;
        public float lastHeardTime = -999f;
        public float detection;     // 0..1+, >= 1 means spotted
        public bool visible;
        public bool aware;
        public float awareSince = -999f;
        public bool attackedMe;

        public float LastInfoTime => Mathf.Max(lastSeenTime, lastHeardTime);
    }

    /// <summary>
    /// Bot senses: vision with a human-like detection meter, hearing (GameEvents.Noise), damage/near-miss awareness,
    /// team callouts (delayed sharing) and short-term memory of last known positions.
    /// </summary>
    public class BotPerception : IDisposable
    {
        private struct PendingShare
        {
            public Combatant enemy;
            public Vector3 position;
            public float applyAt;
        }

        private static event Action<TeamId, Combatant, Combatant, Vector3> TeamSighting;

        private const float VisionInterval = 0.1f;
        private const float ShareDelay = 0.6f;

        private readonly BotController _bot;
        private readonly Combatant _self;
        private readonly Dictionary<Combatant, ThreatRecord> _threats = new Dictionary<Combatant, ThreatRecord>();
        private readonly List<PendingShare> _pending = new List<PendingShare>();
        private readonly List<Combatant> _toRemove = new List<Combatant>();
        private float _nextVision;

        public float MemorySeconds = 12f;
        public ThreatRecord BestThreat { get; private set; }
        public bool HasNoise { get; private set; }
        public Vector3 NoisePosition { get; private set; }
        public float NoiseTime { get; private set; } = -999f;
        public float LastNearMissTime { get; private set; } = -999f;
        public Vector3 LastDamageDirection { get; private set; }
        public int KnownEnemyCount { get; private set; }
        public IEnumerable<ThreatRecord> Threats => _threats.Values;

        public BotPerception(BotController bot)
        {
            _bot = bot;
            _self = bot.GetComponent<Combatant>();
            _nextVision = Time.time + UnityEngine.Random.value * VisionInterval; // stagger bots
            GameEvents.Noise += OnNoise;
            GameEvents.NearMiss += OnNearMiss;
            TeamSighting += OnTeamSighting;
            var h = bot.GetComponent<Health>();
            if (h != null) h.Damaged += OnDamaged;
        }

        public void Dispose()
        {
            GameEvents.Noise -= OnNoise;
            GameEvents.NearMiss -= OnNearMiss;
            TeamSighting -= OnTeamSighting;
            var h = _bot != null ? _bot.GetComponent<Health>() : null;
            if (h != null) h.Damaged -= OnDamaged;
        }

        public void Clear()
        {
            _threats.Clear();
            _pending.Clear();
            BestThreat = null;
            HasNoise = false;
            NoiseTime = -999f;
        }

        public void Tick(float dt)
        {
            float now = Time.time;
            if (now >= _nextVision)
            {
                _nextVision = now + VisionInterval;
                UpdateVision(VisionInterval);
            }

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (now < _pending[i].applyAt) continue;
                var p = _pending[i];
                _pending.RemoveAt(i);
                if (p.enemy == null || !p.enemy.IsAlive) continue;
                var rec = Get(p.enemy);
                if (now - rec.lastSeenTime > 0.5f)
                {
                    rec.lastKnownPosition = p.position;
                    rec.lastHeardTime = now;
                    rec.aware = true;
                    rec.detection = Mathf.Max(rec.detection, 0.5f);
                }
            }

            _toRemove.Clear();
            KnownEnemyCount = 0;
            foreach (var kv in _threats)
            {
                var r = kv.Value;
                if (kv.Key == null || !kv.Key.IsAlive || now - r.LastInfoTime > MemorySeconds && !r.visible)
                    _toRemove.Add(kv.Key);
                else if (r.aware) KnownEnemyCount++;
            }
            foreach (var c in _toRemove) _threats.Remove(c);

            BestThreat = PickBest(now);
            if (HasNoise && now - NoiseTime > 10f) HasNoise = false;
        }

        private ThreatRecord Get(Combatant c)
        {
            if (!_threats.TryGetValue(c, out var r))
            {
                r = new ThreatRecord { combatant = c, lastKnownPosition = c.Position };
                _threats[c] = r;
            }
            return r;
        }

        private void UpdateVision(float step)
        {
            var diff = _bot.ActiveDifficulty;
            float range = diff != null ? diff.visionRange : 70f;
            float halfFov = (diff != null ? diff.fieldOfView : 110f) * 0.5f;
            float speedMul = diff != null ? diff.detectionSpeed : 1f;
            Vector3 eye = _self.EyePosition;
            Vector3 view = _self.ViewDirection.sqrMagnitude > 0.01f ? _self.ViewDirection : _bot.transform.forward;
            float now = Time.time;

            var all = Combatant.All;
            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (c == null || c == _self || !c.IsAlive || !_self.IsHostileTo(c)) continue;

                Vector3 toHead = c.HeadPosition - eye;
                float dist = toHead.magnitude;
                bool candidate = dist <= range;
                float angle = candidate ? Vector3.Angle(view, toHead) : 180f;
                bool inCone = angle <= halfFov || (dist < 6f && angle < 100f);
                bool los = candidate && inCone && (HasLos(eye, c.HeadPosition) || HasLos(eye, c.ChestPosition));

                _threats.TryGetValue(c, out var rec);
                if (los)
                {
                    rec ??= Get(c);
                    float rate;
                    if (_bot != null && _bot.Archetype == BotArchetype.DumbRobotic)
                    {
                        rate = speedMul * 2.25f;
                    }
                    else
                    {
                        rate = speedMul * (0.9f + 9f / Mathf.Max(dist, 1.5f));
                        if (c.Velocity.sqrMagnitude > 4f) rate *= 1.35f;
                        if (now - c.LastLoudShotTime < 1f) rate *= 2.2f;
                        if (angle < 12f) rate *= 1.4f;
                        if (c.HeadPosition.y - c.Position.y < 1.3f) rate *= 0.7f; // crouched
                    }
                    rec.detection = Mathf.Min(1.25f, rec.detection + rate * step);
                    if (rec.detection >= 1f)
                    {
                        if (!rec.visible) Share(c, c.Position);
                        rec.visible = true;
                        rec.lastSeenTime = now;
                        rec.lastKnownPosition = c.Position;
                        if (!rec.aware)
                        {
                            rec.aware = true;
                            rec.awareSince = now;
                        }
                    }
                }
                else if (rec != null)
                {
                    rec.visible = false;
                    rec.detection = Mathf.Max(0f, rec.detection - 0.5f * step);
                }
            }
        }

        private static bool HasLos(Vector3 from, Vector3 to)
        {
            return !Physics.Linecast(from, to, LR2Layers.VisionMask, QueryTriggerInteraction.Ignore);
        }

        private ThreatRecord PickBest(float now)
        {
            ThreatRecord best = null;
            float bestScore = float.MinValue;
            Vector3 pos = _bot.transform.position;
            foreach (var r in _threats.Values)
            {
                if (!r.aware || r.combatant == null || !r.combatant.IsAlive) continue;
                float age = now - r.LastInfoTime;
                if (!r.visible && age > MemorySeconds) continue;
                float dist = Vector3.Distance(pos, r.lastKnownPosition);
                float score = (r.visible ? 100f : 0f) - dist * 0.6f - age * 4f;
                if (r == BestThreat) score += 18f;             // hysteresis: keep current target
                if (r.attackedMe && now - r.lastSeenTime < 4f) score += 25f;
                if (r.combatant.Team == TeamId.Hostile) score -= 10f;  // drones are lower priority than soldiers
                if (score > bestScore)
                {
                    bestScore = score;
                    best = r;
                }
            }
            return best;
        }

        private void Share(Combatant enemy, Vector3 position)
        {
            if (_self.Team == TeamId.None || _self.Team == TeamId.Hostile) return;
            TeamSighting?.Invoke(_self.Team, _self, enemy, position);
        }

        private void OnTeamSighting(TeamId team, Combatant spotter, Combatant enemy, Vector3 position)
        {
            if (spotter == _self || team != _self.Team || enemy == null) return;
            _pending.Add(new PendingShare { enemy = enemy, position = position, applyAt = Time.time + ShareDelay });
        }

        /// <summary>D61: noises come from muzzles (about 1.45 m up) and bullet impacts (anywhere on a wall); the brain looks
        /// 1.4 m above a known position, so ungrounded noise points made searching bots stare at the sky. Snap to the floor.</summary>
        internal static Vector3 GroundPoint(Vector3 p)
        {
            if (Physics.Raycast(p + Vector3.up * 0.3f, Vector3.down, out var hit, 4f, LR2Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                return hit.point;
            return p;
        }

        private void OnNoise(NoiseEvent e)
        {
            if (e.source == _self) return;
            Vector3 pos = _bot.transform.position;
            float hearing = _bot.Difficulty != null ? _bot.Difficulty.hearingMultiplier : 1f;
            float dist = Vector3.Distance(pos, e.position);
            if (dist > e.radius * hearing) return;

            if (e.source != null && !_self.IsHostileTo(e.source)) return;
            float now = Time.time;
            Vector3 fuzz = UnityEngine.Random.insideUnitSphere * Mathf.Min(6f, dist * 0.08f);
            fuzz.y = 0f;
            Vector3 heardAt = GroundPoint(e.position);
            if (e.source != null)
            {
                var rec = Get(e.source);
                if (!rec.visible)
                {
                    rec.lastKnownPosition = heardAt + fuzz;
                    rec.lastHeardTime = now;
                }
                rec.aware = true;
                if (e.type == NoiseType.Gunshot) rec.detection = Mathf.Max(rec.detection, 0.65f);
            }
            if (e.type == NoiseType.Gunshot || e.type == NoiseType.Explosion || e.source == null || dist < 12f)
            {
                HasNoise = true;
                NoisePosition = heardAt + fuzz;
                NoiseTime = now;
            }
        }

        private void OnNearMiss(NearMissEvent e)
        {
            if (e.listener != _self || e.shooter == null || !_self.IsHostileTo(e.shooter)) return;
            LastNearMissTime = Time.time;
            var rec = Get(e.shooter);
            rec.aware = true;
            rec.detection = Mathf.Max(rec.detection, 0.9f);
            if (!rec.visible)
            {
                rec.lastKnownPosition = e.shooter.Position;
                rec.lastHeardTime = Time.time;
            }
        }

        private void OnDamaged(DamageInfo info)
        {
            if (info.direction.sqrMagnitude > 0.001f) LastDamageDirection = info.direction;
            if (info.attacker == null || info.attacker == _self || !_self.IsHostileTo(info.attacker)) return;
            var rec = Get(info.attacker);
            rec.aware = true;
            rec.attackedMe = true;
            rec.detection = Mathf.Max(rec.detection, 1f);
            rec.lastKnownPosition = info.attacker.Position;
            rec.lastHeardTime = Time.time;
            Share(info.attacker, info.attacker.Position);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => TeamSighting = null;
    }
}
