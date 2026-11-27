using LR2.Audio;
using LR2.Combat;
using LR2.Core;
using LR2.FX;
using UnityEngine;

namespace LR2.Weapons
{
    public struct ShotResult
    {
        public Vector3 endPoint;
        public bool hitCombatant;
        public bool killed;
        public bool headshot;
        public float damage;
    }

    /// <summary>
    /// Hitscan bullets: sorted multi-hit raycast, self filtering, CoD-style damage tiers x hit-zone multipliers,
    /// penetration through thin cover (wallbangs), blood/impact FX, tracers and near-miss (whizz/crack) feedback.
    /// </summary>
    public static class Ballistics
    {
        private static readonly RaycastHit[] Hits = new RaycastHit[48];
        private const float NearMissRadius = 4.2f;

        /// <param name="muzzleTransform">Optional: the tracer follows this muzzle while it flies, so it stays with a moving shooter.</param>
        public static ShotResult Fire(Combatant shooter, WeaponDefinition def, Vector3 origin, Vector3 direction,
            Vector3 visualMuzzle, bool spawnTracer, GameObject source, Transform muzzleTransform = null)
        {
            var result = new ShotResult();
            direction.Normalize();
            float range = def != null ? def.maxRange : 200f;
            result.endPoint = origin + direction * range;

            Physics.SyncTransforms();
            int count = Physics.RaycastNonAlloc(origin, direction, Hits, range, LR2Layers.BulletMask, QueryTriggerInteraction.Ignore);
            SortByDistance(count);

            float penetrationLeft = def != null ? def.penetrationDepth : 0.2f;
            float damageScale = 1f;
            bool penetrated = false;
            Health lastHealth = null;

            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = Hits[i];
                Collider col = hit.collider;
                if (col == null || !col.enabled || !col.gameObject.activeInHierarchy) continue;

                // ---------------------------------------------------------------- soldiers (bone hitboxes)
                var hitbox = col.GetComponent<Hitbox>();
                if (hitbox != null)
                {
                    if (shooter != null && hitbox.Combatant == shooter) continue;
                    if (hitbox.Owner == lastHealth) continue;
                    lastHealth = hitbox.Owner;

                    if (hitbox.Owner == null || !hitbox.Owner.IsAlive)
                    {
                        // Corpse: blood + push the ragdoll.
                        if (!LR2.World.KillcamDirector.IsPlaying) ImpactSystem.SpawnBlood(hit.point, hit.normal, direction, false);
                        var rb = col.attachedRigidbody;
                        if (rb != null && !rb.isKinematic) rb.AddForceAtPosition(direction * 45f, hit.point, ForceMode.Impulse);
                        result.endPoint = hit.point;
                        break;
                    }

                    var info = new DamageInfo
                    {
                        amount = BaseDamage(def, hit.distance) * (def != null ? def.ZoneMultiplier(hitbox.Zone) : 1f) * damageScale,
                        type = DamageType.Bullet,
                        attacker = shooter,
                        source = source,
                        weaponName = def != null ? def.shortName : "Rifle",
                        point = hit.point,
                        normal = hit.normal,
                        direction = direction,
                        distance = Vector3.Distance(origin, hit.point),
                        force = 85f,
                        penetrated = penetrated
                    };
                    float applied = hitbox.ApplyDamage(ref info);
                    bool head = hitbox.Zone == HitZone.Head;
                    if (!LR2.World.KillcamDirector.IsPlaying) ImpactSystem.SpawnBlood(hit.point, hit.normal, direction, head);
                    result.hitCombatant |= applied > 0f;
                    result.damage += applied;
                    result.headshot |= head && applied > 0f;
                    result.killed |= !hitbox.Owner.IsAlive;
                    result.endPoint = hit.point;
                    if (shooter != null && applied > 0f) shooter.stats.shotsHit++;
                    break;
                }

                // ---------------------------------------------------------------- other damageables (orb drones, destructible props)
                var damageable = col.GetComponentInParent<IDamageable>();
                if (damageable != null)
                {
                    var h = damageable as Health;
                    bool isActor = h != null;
                    if (isActor && shooter != null && h.Owner == shooter) continue;
                    var hitSurface = isActor ? SurfaceType.Metal : SurfaceDetector.Detect(col);
                    if (damageable.CanBeDamaged)
                    {
                        var info = new DamageInfo
                        {
                            amount = BaseDamage(def, hit.distance) * damageScale,
                            type = DamageType.Bullet,
                            zone = HitZone.Generic,
                            attacker = shooter,
                            source = source,
                            weaponName = def != null ? def.shortName : "Rifle",
                            point = hit.point,
                            normal = hit.normal,
                            direction = direction,
                            distance = Vector3.Distance(origin, hit.point),
                            force = 40f,
                            penetrated = penetrated
                        };
                        float applied = damageable.ApplyDamage(ref info);
                        if (isActor)
                        {
                            result.hitCombatant |= applied > 0f;
                            result.damage += applied;
                            result.killed |= !h.IsAlive;
                            if (shooter != null && applied > 0f) shooter.stats.shotsHit++;
                        }
                    }
                    if (!LR2.World.KillcamDirector.IsPlaying) ImpactSystem.SpawnBulletImpact(hit.point, hit.normal, direction, hitSurface, false);
                    if (!isActor && !damageable.CanBeDamaged && penetrationLeft > 0.02f)
                    {
                        damageScale *= 0.78f;
                        penetrated = true;
                        continue; // bullet punches through freshly shattered props
                    }
                    result.endPoint = hit.point;
                    break;
                }

                // ---------------------------------------------------------------- world
                var surface = SurfaceDetector.Detect(col);
                if (!LR2.World.KillcamDirector.IsPlaying) ImpactSystem.SpawnBulletImpact(hit.point, hit.normal, direction, surface, false);
                var body = col.attachedRigidbody;
                if (body != null && !body.isKinematic) body.AddForceAtPosition(direction * 6f, hit.point, ForceMode.Impulse);
                GameEvents.RaiseNoise(shooter, hit.point, 6f, NoiseType.Impact);

                float cost = PenetrationCost(surface);
                if (cost > 0f && penetrationLeft > 0.001f)
                {
                    float maxThickness = penetrationLeft / cost;
                    Vector3 far = hit.point + direction * maxThickness;
                    if (col.Raycast(new Ray(far, -direction), out var exit, maxThickness))
                    {
                        float thickness = Vector3.Distance(hit.point, exit.point);
                        penetrationLeft -= thickness * cost;
                        damageScale *= def != null ? def.penetrationDamageScale : 0.6f;
                        penetrated = true;
                        if (!LR2.World.KillcamDirector.IsPlaying) ImpactSystem.SpawnBulletImpact(exit.point, exit.normal, direction, surface, true);
                        continue;
                    }
                }

                result.endPoint = hit.point;
                break;
            }

            ProcessNearMisses(shooter, origin, result.endPoint);
            // B132: the match keeps running behind the killcam; its live rounds must not fly through the replay
            if (spawnTracer && !LR2.World.KillcamDirector.IsPlaying)
            {
                Combatant local = Combatant.FindLocalPlayer();
                bool friendlyTracer = shooter == null || shooter.IsLocalPlayer || (local != null && !TeamUtil.AreHostile(local, shooter));
                TracerSystem.Spawn(visualMuzzle, result.endPoint, friendlyTracer, muzzleTransform);
            }
            return result;
        }

        private static float BaseDamage(WeaponDefinition def, float distance) => def != null ? def.DamageAtDistance(distance) : 25f;

        private static float PenetrationCost(SurfaceType s)
        {
            switch (s)
            {
                case SurfaceType.Glass: return 0.25f;
                case SurfaceType.Fabric: return 0.5f;
                case SurfaceType.Wood: return 1f;
                case SurfaceType.Plastic: return 0.8f;
                case SurfaceType.Metal: return 2.5f;
                default: return 0f; // concrete, dirt, water: stop
            }
        }

        private static void ProcessNearMisses(Combatant shooter, Vector3 origin, Vector3 end)
        {
            var all = Combatant.All;
            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (c == null || c == shooter || !c.IsAlive) continue;
                Vector3 head = c.HeadPosition;
                Vector3 closest = LR2Math.ClosestPointOnSegment(origin, end, head);
                float dist = Vector3.Distance(closest, head);
                if (dist > NearMissRadius || Vector3.Distance(closest, origin) < 1.0f) continue;
                if (Vector3.Distance(end, head) < 0.35f) continue; // it hit them

                GameEvents.RaiseNearMiss(new NearMissEvent { shooter = shooter, listener = c, closestPoint = closest, distance = dist });
                if (c.IsLocalPlayer)
                {
                    float closeness = 1f - Mathf.Clamp01(dist / NearMissRadius);
                    LR2MusicDirector.Instance?.NotifyCombatEvent(Mathf.Lerp(0.16f, 0.34f, closeness), 3.2f);
                    var sounds = LR2CommonSounds.Instance;
                    if (sounds == null) continue;
                    float shooterDist = Vector3.Distance(origin, head);
                    float whizPitch = Mathf.Lerp(0.92f, 1.12f, closeness);
                    // Open-air bullet flyby bypasses cover occlusion so passing whiz and supersonic snap stay crisp
                    if (sounds.bulletWhizz.IsValid)
                        LR2Audio.PlayAt(sounds.bulletWhizz, closest, Mathf.Lerp(0.42f, 1.0f, closeness), 0f, true, whizPitch);
                    if (sounds.bulletCrack.IsValid && (shooterDist > 8f || dist < 2.2f))
                        LR2Audio.PlayAt(sounds.bulletCrack, closest, Mathf.Lerp(0.45f, 0.96f, closeness), 0.01f, true, Mathf.Lerp(0.96f, 1.06f, closeness));
                }
            }
        }

        private static void SortByDistance(int count)
        {
            for (int i = 1; i < count; i++)
            {
                var key = Hits[i];
                int j = i - 1;
                while (j >= 0 && Hits[j].distance > key.distance)
                {
                    Hits[j + 1] = Hits[j];
                    j--;
                }
                Hits[j + 1] = key;
            }
        }
    }
}
