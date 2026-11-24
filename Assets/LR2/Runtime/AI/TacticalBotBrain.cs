using LR2.Combat;
using LR2.Core;
using UnityEngine;
using UnityEngine.AI;

namespace LR2.AI
{
    /// <summary>
    /// Default bot brain: utility-style goal selection with hysteresis, re-evaluated ~4 times per second.
    /// Retreat-to-regen > directives > engage > take cover > hunt last-known > investigate noise > follow leader > patrol.
    /// </summary>
    public class TacticalBotBrain : IBotBrain
    {
        private BotController _bot;
        private BotGoal _goal;
        private float _goalSince;

        private CoverPoint _cover;
        private bool _retreating;
        private float _retreatUntil;

        private TacticalPoint _patrolPoint;
        private Vector3 _patrolDest;
        private float _patrolRepickAt;

        private Vector3 _flankDest;
        private float _flankUntil;
        private float _nextFlankAllowed;
        private Vector3 _targetAnchor;
        private float _targetAnchorTime;

        private Vector3 _followOffset;
        private float _followRepickAt;
        private float _lastUtilityDirectiveUntil = -1f;

        public BotGoal CurrentGoal => _goal;

        public void Initialize(BotController bot)
        {
            _bot = bot;
            Reset();
        }

        public void Reset()
        {
            _goal = BotGoal.Idle;
            ReleaseCover();
            _retreating = false;
            _patrolPoint = null;
            _patrolRepickAt = 0f;
            _flankUntil = 0f;
            _followRepickAt = 0f;
        }

        public BotIntent Think(BotBlackboard bb, float deltaTime)
        {
            var d = bb.difficulty;
            float aggression = d != null ? d.aggression : 0.55f;
            var intent = new BotIntent { allowFire = true, desiredRange = bb.effectiveRange * Mathf.Lerp(0.8f, 0.35f, aggression) };
            float now = bb.time;

            if (_bot != null && _bot.Archetype == BotArchetype.DumbRobotic)
                return ThinkDumb(bb, intent, now);

            // ---------------------------------------------------------- 1. retreat to regenerate health
            float retreatAt = d != null ? d.retreatHealth : 0.38f;
            if (!_retreating && bb.health01 < retreatAt && (bb.recentlyDamaged || bb.underFire) && bb.target != null)
            {
                _retreating = true;
                _retreatUntil = now + 7f;
                PickCover(bb, bb.targetLastKnown, 22f);
            }
            if (_retreating)
            {
                if (bb.health01 > 0.85f || now > _retreatUntil) _retreating = false;
                else
                {
                    SetGoal(BotGoal.Retreat, now);
                    intent.goal = BotGoal.Retreat;
                    intent.hasDestination = true;
                    intent.destination = _cover != null ? _cover.transform.position : AwayFrom(bb.position, bb.targetLastKnown, 12f);
                    intent.allowSprint = !bb.targetVisible;
                    intent.crouch = _cover != null && _cover.crouchCover && Vector3.Distance(bb.position, _cover.transform.position) < 1.2f;
                    intent.target = bb.target;
                    intent.allowFire = bb.targetVisible && bb.targetDistance < 15f; // fight back when cornered
                    LookAtThreat(ref intent, bb);
                    return intent;
                }
            }

            // ---------------------------------------------------------- 2. directives (squad commands / B151 Tactical Micro & Avatar Pilot)
            if (bb.hasDirective && now < bb.directiveUntil && bb.directive.order != BotOrder.FreeRoam)
            {
                var dir = bb.directive;
                intent.peekStyle = dir.peek;
                intent.burstStyle = dir.burst;
                if (dir.stance == BotStanceOrder.Crouch) intent.crouch = true;
                else if (dir.stance == BotStanceOrder.Prone) intent.prone = true;

                if (dir.utility == BotUtilityAction.ThrowFrag && !Mathf.Approximately(_lastUtilityDirectiveUntil, bb.directiveUntil))
                {
                    if (bb.target != null && (bb.targetVisible || bb.TimeSinceSeen < 12f) && bb.targetDistance >= 7f && bb.targetDistance <= 28f)
                    {
                        _lastUtilityDirectiveUntil = bb.directiveUntil;
                        intent.throwGrenade = true;
                    }
                }
                else if (dir.utility == BotUtilityAction.SafeReload && bb.ammo01 < 0.78f && !bb.isReloading && !bb.targetVisible)
                {
                    intent.forceReload = true;
                }

                bool holdFire = dir.utility == BotUtilityAction.HoldFire && (!bb.targetVisible || bb.targetDistance > 9f) && !bb.underFire;
                switch (dir.order)
                {
                    case BotOrder.HoldPosition:
                        if (bb.targetVisible)
                        {
                            var eng = Engage(bb, intent, now, false);
                            if (holdFire) eng.allowFire = false;
                            return eng;
                        }
                        SetGoal(BotGoal.HoldPosition, now);
                        intent.goal = BotGoal.HoldPosition;
                        intent.hasDestination = Vector3.Distance(bb.position, dir.position) > 1.5f;
                        intent.destination = dir.position;
                        intent.forceAds = dir.peek == BotPeekStyle.HoldAngle;
                        LookAtThreat(ref intent, bb);
                        return intent;
                    case BotOrder.MoveTo:
                        if (bb.targetVisible && (bb.targetDistance < 25f || dir.aggression > 0.2f))
                        {
                            var eng = Engage(bb, intent, now, true);
                            if (holdFire) eng.allowFire = false;
                            return eng;
                        }
                        SetGoal(BotGoal.MoveTo, now);
                        intent.goal = BotGoal.MoveTo;
                        intent.hasDestination = true;
                        intent.destination = dir.position;
                        intent.allowSprint = dir.aggression >= -0.3f;
                        return intent;
                    case BotOrder.Attack:
                        if (dir.target != null && dir.target.IsAlive)
                        {
                            if (bb.target == dir.target && bb.targetVisible)
                            {
                                var eng = Engage(bb, intent, now, true);
                                if (holdFire) eng.allowFire = false;
                                return eng;
                            }
                            SetGoal(BotGoal.Hunt, now);
                            intent.goal = BotGoal.Hunt;
                            intent.hasDestination = true;
                            intent.destination = dir.target.Position;
                            intent.allowSprint = true;
                            intent.target = dir.target;
                            return intent;
                        }
                        break;
                    case BotOrder.Retreat:
                        SetGoal(BotGoal.Retreat, now);
                        intent.goal = BotGoal.Retreat;
                        intent.hasDestination = true;
                        intent.destination = dir.position != Vector3.zero ? dir.position : AwayFrom(bb.position, bb.targetLastKnown, 15f);
                        intent.allowSprint = true;
                        return intent;
                    case BotOrder.Flank:
                        _flankDest = dir.position;
                        _flankUntil = now + 8f;
                        break;
                    case BotOrder.FollowLeader:
                        if (!bb.targetVisible && bb.squadLeader != null) return Follow(bb, intent, now);
                        break;
                }
            }

            // ---------------------------------------------------------- 2.5 urgent CTF flag contest (stop hostile near-capture before distant hunting)
            var flag = LR2.Game.FlagZoneController.Instance;
            var selfCombatant = _bot != null ? _bot.Combatant : null;
            if (flag != null && flag.IsHostileCapturingOrContesting(selfCombatant, out float flagUrgency)
                && flagUrgency >= 0.35f && (!bb.targetVisible || bb.targetDistance > 14f))
            {
                return ObjectiveFlag(bb, intent, flag, selfCombatant, now, urgent: true);
            }

            // ---------------------------------------------------------- 3. engage visible / just-lost target
            if (bb.target != null && (bb.targetVisible || bb.TimeSinceSeen < 0.7f))
            {
                // Reloading in the open: break line of sight if cover is close.
                if (bb.isReloading && d != null && Random.value < d.useCoverChance
                    && ((_goal == BotGoal.TakeCover && _cover != null && _cover.gameObject.activeInHierarchy) || PickCover(bb, bb.targetLastKnown, 10f)))
                {
                    SetGoal(BotGoal.TakeCover, now);
                    intent.goal = BotGoal.TakeCover;
                    intent.hasDestination = true;
                    intent.destination = _cover.transform.position;
                    intent.allowSprint = true;
                    intent.target = bb.target;
                    intent.allowFire = false;
                    LookAtThreat(ref intent, bb);
                    return intent;
                }

                // Target camping in one spot -> occasionally flank instead of trading head-on.
                if (Vector3.Distance(_targetAnchor, bb.targetLastKnown) > 3f)
                {
                    _targetAnchor = bb.targetLastKnown;
                    _targetAnchorTime = now;
                }
                float antiCampFlankWindow = _bot != null && _bot.IsAdaptiveMode
                    ? Mathf.Lerp(4.0f, 7.2f, AdaptiveDifficultyTracker.PlayerRushTendency01)
                    : 6f;
                if (d != null && now - _targetAnchorTime > antiCampFlankWindow && now > _nextFlankAllowed && Random.value < d.flankChance
                    && TryPickFlank(bb, out var flank))
                {
                    _flankDest = flank;
                    _flankUntil = now + 9f;
                    _nextFlankAllowed = now + 18f;
                    _targetAnchorTime = now;
                }

                if (now < _flankUntil && !bb.targetVisible) return Flank(bb, intent, now);
                return Engage(bb, intent, now, true);
            }

            ReleaseCover();

            // ---------------------------------------------------------- 4. flank in progress
            if (now < _flankUntil) return Flank(bb, intent, now);

            // ---------------------------------------------------------- 5. hunt last known position (with tactical corner slicing & peek discipline)
            if (bb.target != null && bb.TimeSinceSeen < 10f)
            {
                SetGoal(BotGoal.Hunt, now);
                intent.goal = BotGoal.Hunt;
                intent.target = bb.target;
                float huntDist = Vector3.Distance(bb.position, bb.targetLastKnown);
                // D65: an aggressive bot hunting a reloading or hurt target rushes it instead of slicing the corner slowly
                bool pressing = aggression >= 0.68f && TargetIsVulnerable(bb);
                bool slicingCorner = !pressing && huntDist < 10f && huntDist > 2.5f && bb.TimeSinceSeen > 0.9f && bb.TimeSinceSeen < 4.5f;
                intent.hasDestination = !slicingCorner || (huntDist > 4.2f);
                intent.destination = bb.targetLastKnown;
                intent.allowSprint = (huntDist > 16f && aggression > 0.5f) || (pressing && huntDist > 9f);
                intent.crouch = slicingCorner && (Mathf.FloorToInt(now * 0.9f) % 3 == 0);
                intent.hasLookPoint = true;
                intent.lookPoint = bb.targetLastKnown + Vector3.up * 1.42f; // pre-aim angle while slicing corner
                return intent;
            }

            // ---------------------------------------------------------- 6. investigate noise
            if (bb.hasNoise && now - bb.noiseTime < 8f)
            {
                SetGoal(BotGoal.Investigate, now);
                intent.goal = BotGoal.Investigate;
                intent.hasDestination = true;
                intent.destination = bb.noisePosition;
                intent.allowSprint = Vector3.Distance(bb.position, bb.noisePosition) > 20f;
                intent.hasLookPoint = true;
                intent.lookPoint = bb.noisePosition + Vector3.up * 1.4f;
                return intent;
            }

            // ---------------------------------------------------------- 6.5 active rotating Flag Zone (Smart Objective AI)
            if (flag != null)
            {
                Vector3 fPos = flag.ActiveFlagPosition;
                float distToFlag = Vector3.Distance(bb.position, fPos);
                if (!bb.targetVisible && (bb.target == null || bb.TimeSinceSeen > 3.2f || distToFlag > flag.ActiveRadius * 0.7f))
                    return ObjectiveFlag(bb, intent, flag, selfCombatant, now, urgent: false);
            }

            // ---------------------------------------------------------- 7. support the squad leader (TDM)
            if (bb.squadLeader != null && bb.squadLeader.IsAlive) return Follow(bb, intent, now);

            // ---------------------------------------------------------- 8. patrol
            return Patrol(bb, intent, now);
        }

        private BotIntent ObjectiveFlag(BotBlackboard bb, BotIntent intent, LR2.Game.FlagZoneController flag, Combatant self, float now, bool urgent)
        {
            SetGoal(BotGoal.MoveTo, now);
            intent.goal = BotGoal.MoveTo;
            Vector3 fPos = flag.ActiveFlagPosition;
            float distToFlag = Vector3.Distance(bb.position, fPos);
            flag.CountZoneOccupants(self, out int alliesInZone, out int alliesInCore, out int hostilesInZone, out Combatant closestHostile);

            int angleSlot = (self != null ? self.DisplayName.GetHashCode() : 0) & 7;
            Vector3 offsetDir = Quaternion.Euler(0f, angleSlot * 45f, 0f) * Vector3.forward;
            float aggression = bb.difficulty != null ? bb.difficulty.aggression : 0.55f;

            Vector3 desired;
            if (hostilesInZone > 0 || flag.State == LR2.Game.FlagZoneController.ZoneState.Contested)
            {
                // Contested Breach vs Tactical Ring Hold: aggressive bots crash closest hostile inside core; others pre-aim cross-pole cover
                if (aggression >= 0.55f && closestHostile != null)
                    desired = Vector3.Lerp(fPos, closestHostile.Position, 0.45f) + offsetDir * 0.9f;
                else
                    desired = fPos + offsetDir * Mathf.Min(flag.CoreRadius + 1.6f, flag.ActiveRadius * 0.5f);

                intent.hasLookPoint = true;
                Vector3 watchTarget = closestHostile != null ? closestHostile.Position : (bb.target != null ? bb.targetLastKnown : fPos);
                intent.lookPoint = watchTarget + Vector3.up * 1.38f;
            }
            else if (alliesInCore < 2)
            {
                // Core Anchor: claim inner 2.25x proximity core slot (<= 2.6m)
                desired = fPos + offsetDir * Mathf.Max(0.95f, flag.CoreRadius * 0.62f);
                if (bb.target != null)
                {
                    intent.hasLookPoint = true;
                    intent.lookPoint = bb.targetLastKnown + Vector3.up * 1.4f;
                }
            }
            else
            {
                // Perimeter Overwatch: allies already saturate 2.25x core -> fan out to outer ring and cover approach lanes
                float watchRad = Mathf.Clamp(flag.ActiveRadius * 0.58f, flag.CoreRadius + 1.8f, 7.2f);
                desired = fPos + offsetDir * watchRad;
                intent.hasLookPoint = true;
                intent.lookPoint = fPos + offsetDir * (watchRad + 10f) + Vector3.up * 1.4f;
            }

            if (NavMesh.SamplePosition(desired, out var nh, 3f, NavMesh.AllAreas)) desired = nh.position;
            intent.hasDestination = Vector3.Distance(bb.position, desired) > 1.3f;
            intent.destination = desired;
            intent.allowSprint = urgent ? (distToFlag > flag.CoreRadius + 2f) : (distToFlag > flag.ActiveRadius + 3.5f);
            intent.crouch = distToFlag <= flag.CoreRadius + 1.2f && hostilesInZone == 0 && _cover != null && _cover.crouchCover;
            intent.target = bb.target;
            intent.allowFire = true;
            return intent;
        }

        private BotIntent Engage(BotBlackboard bb, BotIntent intent, float now, bool allowMove)
        {
            SetGoal(BotGoal.Engage, now);
            intent.goal = BotGoal.Engage;
            intent.target = bb.target;
            intent.allowFire = true;
            float dist = bb.targetDistance;
            if (allowMove)
            {
                // D61: aggressive bots press an advantage instead of trading from range: the target is reloading, hurt, or
                // clearly weaker (user 11-18: "engage more aggressively", "it rarely wins against me"). D65 (Gemini review
                // 19:39): this test has to come first; below the range/sight branch it never saw an out-of-sight target, so
                // the sprint could not fire.
                bool press = dist > 4.5f && bb.target != null && bb.difficulty != null && bb.difficulty.aggression >= 0.68f && TargetIsVulnerable(bb);
                if (press)
                {
                    intent.hasDestination = true;
                    intent.destination = bb.targetLastKnown;
                    intent.allowSprint = !bb.targetVisible && dist > 9f;
                }
                else if (dist > intent.desiredRange * 1.35f || !bb.targetVisible)
                {
                    intent.hasDestination = true;
                    intent.destination = bb.targetLastKnown;
                }
                else if (dist < 5f && bb.difficulty != null && bb.difficulty.aggression < 0.5f)
                {
                    intent.hasDestination = true;
                    intent.destination = AwayFrom(bb.position, bb.targetLastKnown, 4f);
                }

                // Objective discipline: if fighting inside an active flag zone, stay anchored inside the zone instead of chasing out
                var flag = LR2.Game.FlagZoneController.Instance;
                if (intent.hasDestination && flag != null && Vector3.Distance(bb.position, flag.ActiveFlagPosition) <= flag.ActiveRadius + 1f)
                {
                    Vector3 fromFlag = intent.destination - flag.ActiveFlagPosition;
                    fromFlag.y = 0f;
                    float maxKeep = Mathf.Max(2f, flag.ActiveRadius * 0.85f);
                    if (fromFlag.magnitude > maxKeep)
                    {
                        Vector3 clamped = flag.ActiveFlagPosition + fromFlag.normalized * maxKeep;
                        if (NavMesh.SamplePosition(clamped, out var nh, 2.5f, NavMesh.AllAreas)) clamped = nh.position;
                        intent.destination = clamped;
                    }
                }
            }
            intent.crouch = _cover != null && _cover.crouchCover && Vector3.Distance(bb.position, _cover.transform.position) < 1.2f;
            return intent;
        }

        private static bool TargetIsVulnerable(BotBlackboard bb)
        {
            var target = bb.target;
            if (target == null) return false;
            float hp = target.Health != null ? target.Health.Normalized : 1f;
            var soldier = target.GetComponent<LR2.Characters.SoldierController>();
            bool reloading = soldier != null && soldier.IsReloading;
            return reloading || hp < 0.45f || bb.health01 > hp + 0.35f;
        }

        private BotIntent Flank(BotBlackboard bb, BotIntent intent, float now)
        {
            SetGoal(BotGoal.Flank, now);
            intent.goal = BotGoal.Flank;
            intent.target = bb.target;
            intent.hasDestination = true;
            intent.destination = _flankDest;
            intent.allowSprint = !bb.targetVisible;
            if (Vector3.Distance(bb.position, _flankDest) < 2f) _flankUntil = 0f;
            intent.hasLookPoint = bb.target != null;
            intent.lookPoint = bb.targetLastKnown + Vector3.up * 1.4f;
            return intent;
        }

        private BotIntent Follow(BotBlackboard bb, BotIntent intent, float now)
        {
            var leader = bb.squadLeader;
            SetGoal(BotGoal.FollowLeader, now);
            intent.goal = BotGoal.FollowLeader;
            if (now >= _followRepickAt || _followOffset == Vector3.zero)
            {
                _followRepickAt = now + Random.Range(3f, 6f);
                float side = Random.value < 0.5f ? -1f : 1f;
                _followOffset = new Vector3(side * Random.Range(2.5f, 5f), 0f, -Random.Range(4f, 8f));
            }
            Vector3 leaderFwd = leader.ViewDirection;
            leaderFwd.y = 0f;
            if (leaderFwd.sqrMagnitude < 0.01f) leaderFwd = leader.transform.forward;
            Quaternion basis = Quaternion.LookRotation(leaderFwd.normalized);
            Vector3 slot = leader.Position + basis * _followOffset;
            if (NavMesh.SamplePosition(slot, out var hit, 3f, NavMesh.AllAreas)) slot = hit.position;

            float distToSlot = Vector3.Distance(bb.position, slot);
            intent.hasDestination = distToSlot > 2.5f;
            intent.destination = slot;
            intent.allowSprint = distToSlot > 12f;
            // Watch the angle the leader is NOT watching (rear/flank security).
            intent.hasLookPoint = true;
            Vector3 watch = Quaternion.AngleAxis(Mathf.Sign(_followOffset.x) * 75f, Vector3.up) * leaderFwd.normalized;
            intent.lookPoint = bb.position + watch * 10f + Vector3.up * 1.5f;
            return intent;
        }

        private BotIntent Patrol(BotBlackboard bb, BotIntent intent, float now)
        {
            SetGoal(BotGoal.Patrol, now);
            intent.goal = BotGoal.Patrol;
            bool arrived = Vector3.Distance(bb.position, _patrolDest) < 2f;
            if (_patrolDest == Vector3.zero || arrived || now > _patrolRepickAt)
            {
                if (_patrolPoint != null) _patrolPoint.lastVisitTime = now;
                PickPatrol(bb);
                _patrolRepickAt = now + 25f;
            }
            intent.hasDestination = true;
            intent.destination = _patrolDest;
            intent.allowSprint = Vector3.Distance(bb.position, _patrolDest) > 14f && (bb.difficulty == null || bb.difficulty.aggression > 0.45f);
            return intent;
        }

        private void PickPatrol(BotBlackboard bb)
        {
            float best = float.MinValue;
            TacticalPoint choice = null;
            foreach (var p in TacticalPoint.All)
            {
                if (p == null || p == _patrolPoint) continue;
                float sinceVisit = Mathf.Min(60f, bb.time - p.lastVisitTime);
                float dist = Vector3.Distance(bb.position, p.transform.position);
                float score = p.weight * 20f + sinceVisit * 0.5f - Mathf.Abs(dist - 18f) * 0.3f + Random.value * 12f;
                if (score > best)
                {
                    best = score;
                    choice = p;
                }
            }
            _patrolPoint = choice;
            if (choice != null)
            {
                _patrolDest = choice.transform.position;
                return;
            }
            // No tactical points: random reachable point.
            for (int i = 0; i < 8; i++)
            {
                Vector3 rnd = bb.position + new Vector3(Random.Range(-20f, 20f), 0f, Random.Range(-20f, 20f));
                if (NavMesh.SamplePosition(rnd, out var hit, 4f, NavMesh.AllAreas))
                {
                    _patrolDest = hit.position;
                    return;
                }
            }
            _patrolDest = bb.position;
        }

        private bool TryPickFlank(BotBlackboard bb, out Vector3 flank)
        {
            flank = Vector3.zero;
            Vector3 toTarget = bb.targetLastKnown - bb.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 25f) return false;
            float best = float.MinValue;
            foreach (var p in TacticalPoint.All)
            {
                if (p == null) continue;
                Vector3 fromTarget = p.transform.position - bb.targetLastKnown;
                fromTarget.y = 0f;
                float d = fromTarget.magnitude;
                if (d < 6f || d > 30f) continue;
                // Prefer points that attack the target from ~90 degrees off the current line.
                float angle = Vector3.Angle(-toTarget, fromTarget);
                float score = -Mathf.Abs(angle - 90f) + p.weight * 10f + Random.value * 15f;
                if (score > best)
                {
                    best = score;
                    flank = p.transform.position;
                }
            }
            return best > float.MinValue;
        }

        private bool PickCover(BotBlackboard bb, Vector3 threat, float maxDistance)
        {
            ReleaseCover();
            CoverPoint best = null;
            float bestScore = float.MinValue;
            Vector3 threatEye = threat + Vector3.up * 1.5f;
            foreach (var c in CoverPoint.All)
            {
                if (c == null || (c.occupant != null && c.occupant != _bot)) continue;
                Vector3 p = c.transform.position;
                float dist = Vector3.Distance(bb.position, p);
                if (dist > maxDistance) continue;
                float h = c.crouchCover ? 0.9f : 1.5f;
                bool hidden = Physics.Linecast(threatEye, p + Vector3.up * h, LR2Layers.VisionMask, QueryTriggerInteraction.Ignore);
                if (!hidden) continue;
                float score = -dist - (Vector3.Distance(p, threat) < 6f ? 30f : 0f) + Random.value * 3f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }
            if (best == null) return false;
            _cover = best;
            _cover.occupant = _bot;
            return true;
        }

        private void ReleaseCover()
        {
            if (_cover != null && _cover.occupant == _bot) _cover.occupant = null;
            _cover = null;
        }

        private static Vector3 AwayFrom(Vector3 from, Vector3 threat, float distance)
        {
            Vector3 dir = from - threat;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = Random.insideUnitSphere;
            dir.y = 0f;
            Vector3 p = from + dir.normalized * distance;
            return NavMesh.SamplePosition(p, out var hit, 5f, NavMesh.AllAreas) ? hit.position : from;
        }

        private static void LookAtThreat(ref BotIntent intent, BotBlackboard bb)
        {
            if (bb.target == null) return;
            intent.hasLookPoint = true;
            intent.lookPoint = bb.targetLastKnown + Vector3.up * 1.4f;
        }

        private void SetGoal(BotGoal goal, float now)
        {
            if (_goal == goal) return;
            _goal = goal;
            _goalSince = now;
        }

        private BotIntent ThinkDumb(BotBlackboard bb, BotIntent intent, float now)
        {
            if (bb.target != null && bb.targetVisible)
            {
                SetGoal(BotGoal.Engage, now);
                intent.goal = BotGoal.Engage;
                intent.target = bb.target;
                intent.allowFire = true;
                intent.hasDestination = bb.targetDistance > intent.desiredRange * 0.95f;
                intent.destination = bb.targetLastKnown;
                intent.allowSprint = false;
                LookAtThreat(ref intent, bb);
                return intent;
            }
            if (bb.target != null && bb.TimeSinceSeen < 8f)
            {
                SetGoal(BotGoal.Hunt, now);
                intent.goal = BotGoal.Hunt;
                intent.target = bb.target;
                intent.hasDestination = true;
                intent.destination = bb.targetLastKnown;
                intent.allowSprint = false;
                return intent;
            }
            var flag = LR2.Game.FlagZoneController.Instance;
            if (flag != null)
            {
                SetGoal(BotGoal.MoveTo, now);
                intent.goal = BotGoal.MoveTo;
                intent.hasDestination = Vector3.Distance(bb.position, flag.ActiveFlagPosition) > 2f;
                intent.destination = flag.ActiveFlagPosition;
                intent.allowSprint = false;
                return intent;
            }
            var pat = Patrol(bb, intent, now);
            pat.allowSprint = false;
            return pat;
        }
    }
}
