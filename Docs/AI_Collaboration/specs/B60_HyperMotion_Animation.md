# `B60` — HyperMotion / HyperMotion V Research & LR2 Hybrid Animation Architecture

**Authors:** Gemini (`gemini-4-hydrogen`, Lead Coordinator) & Copilot (`gpt-5.6-luna`)  
**Companion Spec:** [B60.md](B60.md) (Copilot Motion-Matching Baseline & Subtasks `B60.1–B60.10`)  
**Target Subsystem:** `RetargetProxy.cs`, `ReloadMotionBridge.cs`, `SoldierMovement.cs`, `SoldierFoley.cs`, `BodycamRig.cs`, `HitReactions.cs`, `AnimatorProfile_Rifle_Humanoid.asset`, `LR2QAExtraChecks.cs`, `Docs/Bot/bot-brain.html`

---

## 1. Executive Summary

Can EA Sports' **HyperMotion / HyperMotion V** principles improve **Lead Rush II** (`Unity 6000.5.0f1 HDRP`, `Kinemation FPSAnimator` + `RetargetProxy` Humanoid muscle retargeting)?

**Yes — selectively at the mathematical & kinematic layer, NOT by importing full-team optical capture or replacing Kinemation's procedural firearm solvers with a multi-gigabyte ML black box.**

| HyperMotion Pillar | Source / Engineering Basis | Applicable to LR2? | Concrete LR2 Mechanism | Expected Impact |
|---|---|---|---|---|
| **1. Inertialization Transitions** | Bollo (GDC 2017 / EA FC / UE5) — C2-continuous cubic/quintic offset decay on joint deltas instead of double-evaluating two clips | **HIGH — Apply Immediately (`B60a`)** | `RetargetProxy.cs` & `ReloadMotionBridge.cs`: decay residual position/rotation offsets smoothly over 0.12–0.22 s on reload exit, sprint-out, ADS toggle, and prone exit | Eliminates hard pose snaps (`B16`/`B35`/`B50`) at zero extra clip cost |
| **2. Contact-Aware Foot-Plant Locking & Stride Sync** | HyperMotion TrueBall / Kinetic foot IK + gait phase locking | **HIGH — Apply Immediately (`B60b`)** | `RetargetProxy.AlignBody` + `SoldierFoley.cs`: lock stance-foot world XZ when ankle speed drops below threshold; trigger footsteps from actual bone plant instead of flat timer | Cuts walking `stanceFootSlide` (`1.50–2.38 m/s` -> `< 0.35 m/s`, unblocking `B9` & `B34`) |
| **3. AcceleRATE Movement Archetypes** | EA FC 23/24 Explode / Controlled / Lengthy kinematics + inertia banking | **HIGH — Apply Soon (`B60c`)** | `SoldierMovement.cs` + `BotController.cs` + `RetargetProxy.cs`: per-weapon-class accel/decel curves (Pistol/MK18 Explosive vs AK-12/AK-74N Controlled vs FAL Lengthy) + hip/spine cut bank angle | Gives distinct tactical weight per firearm and organic deceleration |
| **4. Kinetic Close-Combat & Environmental Tactile IK** | HyperMotion kinetic jostle / limb yield & obstacle avoidance | **HIGH — Apply Immediately (`B60d`)** | `CollisionLayerSettings` + `RetargetProxy.cs` + `HitReactions.cs`: forward/upward low-ready or high-port barrel tuck instead of `-Z` through chest (`B59b`), plus hit-impulse upper-spine torque | Fixes weapon-through-body clipping in close quarters & adds kinetic hit feedback |
| **5. ML-Flow Procedural Micro-Motion Synthesis** | HyperMotion ML-Flow / Sapien micro-adjustments | **MEDIUM-HIGH — Procedural Approximation (`B60e`)** | `BodycamRig.cs` + `RetargetProxy.cs`: trigger-finger pre-tension squeeze on fire, breathing/fatigue stamina sway on support hand, post-burst grip re-settle | Makes weapon handling feel alive without runtime ML inference overhead |
| **6. Motion Matching Locomotion Query** | EA GDC 2023 (*Motion Matching at EA: 5 Years Later*, Delannoy) | **MEDIUM — Hybrid Locomotion Experiment (`B60.1–B60.10`)** | Lower-body locomotion-only feature search (`[vel, futureTraj(0.25,0.5s), footVel, stanceTag]`) evaluated every 4–8 frames behind A/B flag | Replaces stiff walk/strafe blending on bots & 3P models while keeping Kinemation weapon layers intact |
| **7. Volumetric 11v11 Stadium Optical Capture / Generative Net** | HyperMotion V broadcast volumetric reconstruction | **NO — Not Applicable** | — | Wrong domain: full-body soccer stadium capture does not solve FPS first-person ADS, scope alignment, or firearm grip constraints |

---

## 2. Detailed Principle Breakdown & LR2 Architectural Fit

### 2.1 Principle A — Inertialization (C2-Continuous Pose Blending, `B60a`)
- **HyperMotion Principle:** Instead of crossfading two complete skeleton evaluators (`A` and `B`) during rapid transitions (sprint-to-ADS, reload cancel, crouch-to-prone), compute the instantaneous pose difference $\Delta x_0 = x_A(t_0) - x_B(t_0)$ and velocity $\Delta v_0 = \dot{x}_A(t_0) - \dot{x}_B(t_0)$ at transition time $t_0$, then decay $(\Delta x, \Delta v) \to 0$ analytically via a quintic polynomial or critically damped spring over duration $T$.
- **Why it fits LR2:** `RetargetProxy` runs `GetHumanPose` / `SetHumanPose` + `ReloadMotionBridge` + two-bone IK every frame (`LateUpdate`). Right now, stopping a reload early (`tactical` or `CancelActions`) or changing weapons/stances causes subtle instantaneous deltas in wrist target `lPos/rPos` and spine rotation. Adding an inertialized residual `(posOffset, rotOffset)` in `RetargetProxy` decays any step jump smoothly over `0.14 s` with zero extra `Animator` graph complexity.

### 2.2 Principle B — Contact-Aware Foot-Plant Locking & Stride Phase Sync (`B60b`)
- **HyperMotion Principle:** Prevent "ice-skating" by identifying when each foot enters stance phase (minimum vertical height + low horizontal velocity) and anchoring its world position with two-bone leg IK until toe-off, while emitting contact/audio cues strictly at plant events.
- **Why it fits LR2:** Copilot's Pose QA runs (`#7–#10`) measured `stanceFootSlide` at `2.38 m/s` (`AK-12`), `2.24 m/s` (`AK-74N`), `2.22 m/s` (`MK18`), and `1.50 m/s` (`FAL`) against the `< 0.5 m/s` (`B34`) acceptance target! Why? `RetargetProxy.AlignBody()` currently shifts `_vHips` onto camera/driver eyes and moves both feet equally (`lf + horiz`, `rf + horiz`), sliding planted feet across the floor! Locking each foot horizontally during its stance contact phase directly solves both `B9` (footstep animation sync) and `B34` (`stanceFootSlide < 0.35 m/s`).

### 2.3 Principle C — AcceleRATE Archetypes & Lean-Into-Cut (`B60c`)
- **HyperMotion Principle:** Characters differentiate acceleration curves into *Explosive* (fast burst 0–3 m, lighter gear), *Controlled* (balanced curve), and *Lengthy* (slower initial ramp, higher momentum sustain) while procedurally banking pelvis/spine into lateral cuts.
- **Why it fits LR2:** Mapping LR2's weapon classes (`Pistol` / `MK18 5.56` = *Explosive* sprint-out & burst accel, `AK-12` / `AK-74N` = *Controlled*, `FAL 7.62 NATO` = *Lengthy* momentum) into `SoldierMovement.cs` and coupling velocity derivative ($\vec{a}$) into subtle pelvis/spine tilt in `RetargetProxy` makes swapping from `FAL` to `MK18` tangibly felt in movement as well as recoil.

### 2.4 Principle D — Kinetic Close-Combat Weapon Compression & Hit Reactions (`B60d`)
- **HyperMotion Principle:** Physical obstacles and close-range opponents compress posture outward/upward along contact normals instead of interpenetrating meshes.
- **Why it fits LR2:** Fixes `B59b` permanently! In `AnimatorProfile_Rifle_Humanoid.asset`, `CollisionLayerSettings` had `m_Bits: 64` and `primaryPose.z = -0.15` / `barrelLength = 1.32`, which shoved weapons backward through the shooter's torso. Replacing linear `-Z` pull-back with a lateral/high-port cant (`primaryPose.z = -0.04`, `EnvironmentMask` only) plus a hard forward-plane guard in `RetargetProxy` ensures no weapon ever clips backward through the body.

### 2.5 Principle E — ML-Flow Procedural Micro-Motion Synthesis (`B60e`)
- **HyperMotion Principle:** Synthesize secondary finger tension, grip re-settling, and stamina-coupled torso breathing on top of coarse locomotion/action loops.
- **Why it fits LR2:** Adding a `triggerPull` muscle bias on `_rIndexProxIdx`/`_rIndexDistIdx` synced to `_triggerHeld` + `Fired`, plus subtle post-burst support-hand re-grip and stamina-coupled breathing sway in `BodycamRig` / `RetargetProxy`, bridges the gap between static finger poses and live trigger/recoil articulation.

### 2.6 Principle F — Hybrid Motion Matching Database (Lower-Body Locomotion Only, `B60.1–B60.10`)
- As specified in Copilot's companion spec [B60.md](B60.md), full pose+trajectory feature search stays strictly scoped to **lower-body locomotion** (`idle/walk/run/start/stop/turn/crouch/prone`), queried at 4–8 frame cadence behind an A/B flag, never touching `ReloadMotionBridge` or Kinemation weapon layers.

---

## 3. Subtask Breakdown & Multi-Agent Ownership (`B60a`–`B60g` + `B60.1`–`B60.10`)

| Subtask | Focus Area | Owner(s) | Reviewer | Target Files & Acceptance Target | Maps to Copilot Subtasks |
|---|---|---|---|---|---|
| **`B60a`** | **Inertialization Transition Layer** | **Gemini** | Copilot / Codex | `RetargetProxy.cs`, `ReloadMotionBridge.cs`: C2 smooth offset decay on reload exit / state switch / ADS toggle (`maxPoseJump < 1.2 cm/frame`, initial velocity hook landed) | `B60.6` |
| **`B60b`** | **Contact-Aware Foot-Plant Locking & Foley Sync** | **Copilot** | Gemini / Codex | `RetargetProxy.cs` (`AlignBody`), `SoldierFoley.cs`: world XZ lock during stance contact phase (`stanceFootSlide < 0.35 m/s` on Pose QA) | `B60.1`, `B9`, `B34` |
| **`B60c`** | **AcceleRATE Weapon-Weight Movement & Cut Banking** | **Copilot + Gemini** | Codex | `SoldierMovement.cs`, `BotController.cs`, `RetargetProxy.cs`: per-class accel/decel curve + cut tilt (`MK18` explosive vs `FAL` weighty) | `B60.2`, `B60.5` |
| **`B60d`** | **Kinetic Close-Combat Weapon Tuck & Directional Hit Torque** | **Gemini** | Codex / Copilot | `AnimatorProfile_Rifle_Humanoid.asset`, `RetargetProxy.cs`, `HitReactions.cs`: forward-plane safety clamp (`dot >= 0.08 m`) + directional hit torque | `B60.7`, `B59b` |
| **`B60e`** | **ML-Flow Procedural Trigger & Micro-Motion Synthesis** | **Copilot** | Gemini / Claude | `RetargetProxy.cs`, `BodycamRig.cs`: live trigger-finger pull on fire, post-burst re-grip, fatigue sway | `B60.5`, `B60.6` |
| **`B60f`** | **Automated HyperMotion Telemetry & C2 Continuity QA** | **Codex** | Gemini / Copilot | `LR2QAExtraChecks.cs`, `LR2QABotRunner.cs`, `FirstPersonPoseCheck.cs`: measure `stanceFootSlide`, pose jerk spike, hand IK drift, close-wall weapon tuck (`B60.8`–`B60.9`) | `B60.8`, `B60.9`, `B60.10` |
| **`B60g`** | **HyperMotion Architecture & Telemetry Tab (`LR2 Bot Brain` & AI Docs)** | **Claude + Gemini** | Copilot / user | `Docs/Bot/bot-brain.html`, `Docs/AI_Collaboration/README.md`: interactive Inertialization vs Linear Blend chart + foot-plant & AcceleRATE curves | `B60` Docs |
| **`B60.1–B60.10`** | **Locomotion-Only Motion Matching Feature DB & Query Prototype** | **Copilot + Claude + Codex** | Gemini / user | See [B60.md](B60.md) for feature schema, clip DB, tag filtering, 4–8 frame query, action lockout, and A/B rollback | Direct |

---

## 4. Citations & Engineering References
1. **Daniel Bollo (GDC 2017 / Unreal Engine):** *Inertialization: High-Performance Animation Transitions* — analytic quintic polynomial pose-offset decay replacing dual-graph crossfading.
2. **Jean-Christophe Delannoy (EA GDC 2023):** *Motion Matching at EA: 5 Years Later* ([slides](https://media.gdcvault.com/gdc2023/Slides/Motion+Matching+at+EA_Delannoy_JC.pdf)) — trajectory + pose feature vectors, tag filtering, query cadence, and inertial blend transitions.
3. **EA SPORTS FC 24 / FIFA 23 HyperMotion2 Pitch Notes:** [HyperMotion V & AcceleRATE / ML-Flow deep dive](https://www.ea.com/games/ea-sports-fc/fc-24/news/fc-24-gameplay-deepdive) — Explode/Controlled/Lengthy kinematics, TrueBall kinetic contact IK, and procedural micro-motion synthesis.
