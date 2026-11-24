# Lead Rush II — Human-Like Bot Biomechanics, Adaptive AI & Physics Systems Specification

*Engineering & Academic Reference Prepared for Claude Artifact Generation*

---

## 1. Biomechanical Mouse Aiming & Motor Control (`BotController.cs`)

To make bots control their character avatars identically to human players — and to support future LLM / agent commanders issuing high-level tactical directives through the exact same `ISoldierInputSource` pipeline — bots **never** set `input.absoluteLook = true` or snap quaternions. Every rotation, tracking sweep, and recoil counter-pull is transmitted strictly as mouse delta increments (`input.look`) into `SoldierController.UpdateLook()`.

### 1.1 Fitts's Law (Fitts 1954; MacKenzie 1992)
Rapid target acquisition duration ($MT$) scales logarithmically with angular Index of Difficulty ($ID$) for angular offset $D = \sqrt{\Delta\psi^2 + \Delta\theta^2}$ and perceived angular target width $W = 2 \arctan\left(\frac{r_{\text{target}}}{d_{\text{target}}}\right)$ (with $r_{\text{target}} = 0.16\,\text{m}$ for head shots and $0.34\,\text{m}$ for chest):

$$ID = \log_2\left(1 + \frac{D}{W}\right)$$

$$MT = \operatorname{clamp}\Big((0.065 + 0.042 \cdot ID) \cdot \text{skillScale},\; 0.085\,\text{s},\; 0.38\,\text{s}\Big), \quad \text{skillScale} = \operatorname{clamp}\left(\frac{7.5}{\max(2.5, \omega_{\text{aim}})},\; 0.62,\; 1.55\right)$$

*Implemented in:* [BotController.cs](file:///h:/Unity%20Projects/GitHub%20Repos/LeadRush%202/Assets/LR2/Runtime/AI/BotController.cs#L480-L501) (`_flickDuration`).

### 1.2 Minimum-Jerk Saccadic Trajectory (Flash & Hogan 1985; Todorov 2004)
Unconstrained human ballistic aiming movements minimize integrated squared jerk $\int_0^{MT} \left(\frac{d^3\theta}{dt^3}\right)^2 dt$, yielding the canonical quintic position profile with zero boundary velocity/acceleration and a bell-shaped speed curve:

$$s(u) = 10u^3 - 15u^4 + 6u^5, \quad u = \operatorname{clamp}\left(\frac{t - t_0}{MT},\; 0,\; 1\right)$$

During `_flickActive` (triggered for angular jumps $D > 10.5^\circ$),yaw/pitch deltas track $s(u)$ toward `_flickGoalYaw` / `_flickGoalPitch`, and instantaneous velocities `_yawVel`, `_pitchVel` stay synchronized (`stepDeg / dt`) so the handoff into closed-loop tracking has zero jerk discontinuity.

*Implemented in:* [BotController.cs](file:///h:/Unity%20Projects/GitHub%20Repos/LeadRush%202/Assets/LR2/Runtime/AI/BotController.cs#L503-L520).

### 1.3 Meyer's Stochastic Optimized Submovement Model (Meyer et al. 1988)
Rapid human flicks decompose into two stages:
1. **Open-loop primary ballistic submovement** aimed near the target center, subject to signal-dependent endpoint variance ($\sigma_{\text{SDN}} = \operatorname{clamp}(D \cdot (0.045 + 0.045 \cdot \text{combatStress}) \cdot \text{skillScale},\; 0.35^\circ,\; 6.5^\circ)$).
2. **Closed-loop visual feedback refractory delay (`110–165 ms`, `_nextFlickAllowedAt`)** followed by **secondary corrective submovements** driven by critically/under-damped spring tracking ($\omega = \text{aimSpeed}$, $\zeta = 0.80$ in combat) until error stabilizes on target.

*Implemented in:* [BotController.cs](file:///h:/Unity%20Projects/GitHub%20Repos/LeadRush%202/Assets/LR2/Runtime/AI/BotController.cs#L494-L535).

### 1.4 Harris–Wolpert Signal-Dependent Motor Noise (Harris & Wolpert 1998)
Rather than injecting constant artificial spread, neuromuscular control tremor scales proportionally with the instantaneous rotational control command magnitude ($\text{cmdMag} = \sqrt{e_\psi^2 + e_\theta^2}$) and `_combatStress`:

$$A_{\text{SDN}} = \min\big(1.6^\circ,\; \text{cmdMag} \cdot 0.032 \cdot (0.55 + 0.65 \cdot \text{combatStress})\big)$$

Slow fine tracking carries near-zero tremor; large violent turns induce proportional motor jitter and corrective re-centering.

*Implemented in:* [BotController.cs](file:///h:/Unity%20Projects/GitHub%20Repos/LeadRush%202/Assets/LR2/Runtime/AI/BotController.cs#L537-L543).

### 1.5 Human Mouse Recoil Pull-Down Physics
- `SoldierController` applies upward weapon kick directly to camera pitch (`Pitch < 0`), shared identically by players and bots.
- On `OnGunFired()` burst onset (`_sprayHeat < 0.08`), `_recoilReactAt = t + U(0.065, 0.135)` enforces a **65–135 ms human perceptual-motor reaction delay** during which vertical pull-down is attenuated (`0.12x`), preventing robotic frame-0 recoil cancellation.
- After reaction delay, pull-down strength scales smoothly with skill (`clamp(aimSpeed / 10.5, 0.55, 0.92)`) and degrades under sustained spray heat (`1 - 0.22 * _sprayHeat`), movement speed, and combat stress (`_combatStress`).

*Implemented in:* [BotController.cs](file:///h:/Unity%20Projects/GitHub%20Repos/LeadRush%202/Assets/LR2/Runtime/AI/BotController.cs#L525-L535) and [`OnGunFired`](file:///h:/Unity%20Projects/GitHub%20Repos/LeadRush%202/Assets/LR2/Runtime/AI/BotController.cs#L644-L660).

---

## 2. Perceptual Latency, Tactical Peeking & Believability (`BotController.cs`, `TacticalBotBrain.cs`)

- **Context-Aware Foveal vs. Peripheral Latency** (grounded in *BotPrize* $\text{UT}^2$ constraints — Schrum, Karpov & Miikkulainen 2011/2012): On new visual acquisition, baseline reaction time is multiplied by eccentricity factor $\in [0.76, 1.58]$ linearly across $[0^\circ, 115^\circ]$ off current gaze vector (`0.76x` fast on pre-aimed angles; up to `1.58x` slower on blindside ambushes).
- **Corner Slicing & Hesitation Peeks** (*F.E.A.R.* GOAP / *Halo* pacing): While hunting a recently lost target inside $2.5\text{–}10\,\text{m}$ (`TacticalBotBrain.Hunt`), bots slow out of sprint, maintain pre-aim toward `targetLastKnown + 1.42m`, and execute periodic corner-slicing pauses + flanks (`TryPickFlank`), plus peripheral horizon sweeps during navigation (`_nextGlanceAt`, $\pm 28^\circ$).
- **Difficulty Presets Preserved:** Static presets `Recruit`, `Regular`, `Hardened`, and `Veteran` remain untouched; realism improvements apply purely through motor/perceptual physics and decision pacing.

---

## 3. Live Adaptive Difficulty Engine (`AdaptiveDifficultyTracker.cs`)

When `LR2Settings.BotDifficulty == 4` (`"ADAPTIVE"`), bots refresh a dynamic runtime `BotDifficulty` profile every `0.75 s` via exponential moving averages over live match events (`ShotFired`, `Hit`, `Killed`, `MatchStateChanged`):
- Tracks `PlayerAccuracy01` as an EMA ($\alpha = 0.15$) of hits/max(`8`, shots), `PlayerHeadshotRate01` as bounded cumulative headshots/hits with a minimum denominator of `4`, `PlayerRushTendency01` ($\text{dist} < 15\,\text{m}$, EMA $\alpha = 0.25$), `PlayerCrouchRate01` (EMA $\alpha = 0.25$), and rolling win/loss streak (`Skill01` $\in [0.12, 0.96]$).
- Counter-adapts playstyle: aggressive CQB rushers face stronger crossfires/cover anchoring while passive campers face tighter anti-camp flank windows (`4.0–7.2 s` in `TacticalBotBrain`).

*Implemented in:* [AdaptiveDifficultyTracker.cs](file:///h:/Unity%20Projects/GitHub%20Repos/LeadRush%202/Assets/LR2/Runtime/AI/AdaptiveDifficultyTracker.cs).

---

## 4. 1:1 60 Hz First-Person Killcam (`KillcamDirector.cs`)

- Upgraded sampling to **60 Hz** (`SampleInterval = 1f / 60f`, `MaxSamplesPerSoldier = 330` = ~5.5 s buffer).
- Replays exact recorded `eyePos`, `eyeRot` (preserving real human flick curves, motor noise, upward recoil kick, and mouse pull-down), and ADS `fov`, while driving synchronized full-body `_killerPuppet`, `_killerGunPuppet`, `TracerSystem`, audio, and `MuzzleFlash` components.

---

## 5. Additional Systems & Engineering Math Summary

1. **BF6-Style Rotating Capture The Flag DM (`FlagZoneController.cs`):**
   - Rotates strategic `FlagAnchor` plazas every 55 s or upon capture (+3 score/capture), awarding proximity-weighted speed ("closer faster, far harder"):
     $$M(d) = \begin{cases} 2.25, & d \le 2.6\,\text{m} \\ \operatorname{lerp}\!\left(2.25,\; 0.28,\; \left(\frac{d - 2.6}{11.5 - 2.6}\right)^{0.65}\right), & 2.6 < d \le 11.5\,\text{m} \\ 0, & d > 11.5\,\text{m} \end{cases}$$
   - Core presence ($\le 2.6\,\text{m}$) actively overpowers outer-perimeter defenders.
2. **Continuum-Mechanics War-Torn Meshes (`LR2MapBuilder.cs`):**
   - `GetWarpedContainerMesh`: Kirchhoff–Love clamped rectangular plate mode $\sin^2(\pi\xi)\sin^2(\pi\eta)$ + Ricker Poisson ($\nu = 0.3$) blast-crater halo $(1 - 1.4r^2)e^{-r^2}$ + Euler flange buckling, bottom sill clamped flush.
   - `GetWarpedWallMesh` & `GetRubbleMesh`: Euler–Bernoulli cantilever blast deflection $\eta^2(1.5 - 0.5\eta)$ + $38^\circ$ Mohr–Coulomb conjugated shear planes + Hertzian impact spalls + principal-stress cleavage rubble planes (pure position-only offsets preserve watertight seams).
3. **HDRP Environment, DXR Toggle & Weather (`WeatherSystem.cs`, `LR2RayTracingController.cs`):**
   - All 4 maps + Main Menu use `HDRISky` cubemaps (`T_HDRI_01.HDR`, `T_HdriSky.hdr`, `T_HDRI_02.HDR`) at physical EV100 (`11.4–12.2 EV`), full-res SSR, optional **Ray Tracing (DXR)** toggle in Settings, dynamic storm darkening + lightning flashes, high-altitude rain streaks with ground splashes/ripples, and cloth/glove non-gloss material exclusion.

---

4. **Empirical Human Miss Pattern — ESTA / CSKnow / Aim Lab Telemetry (`B53`, `BotController.cs`):**
   - **Visuomotor Velocity Lag (`125 ms`):** `_perceivedTargetVel` passes through a discrete low-pass filter (`dt / (0.125f + dt)`) before computing lead prediction (`0.055 s`), creating realistic trailing-edge misses on sudden `ADAD` reversals (*CSKnow*, Durst et al. 2022/2024; *Aim Lab*, Listman et al. 2021).
   - **Anisotropic Flick Covariance & Calm Undershoot (`\sigma_{\parallel} / \sigma_{\perp} \approx 2.27`):** Longitudinal scatter along the 2D saccade vector is `2.27×` wider than perpendicular scatter, carrying a calm `-7.5%` primary undershoot bias (`Lerp(-0.075, +0.035, stress) * D`) (Meyer et al. 1988; van Beers et al. 2004).
   - **1.55 : 1 Wrist-Yaw vs. Vertical-Arm Asymmetry & Late-Spray Horizontal Weave:** Perlin sway, SDN tremor (`1.15x` vs `0.74x`), and late-spray lateral oscillation (`sprayHeat > 0.25`) match pro demo horizontal placement variance (*ESTA*, Xenopoulos & Silva 2022).

### Primary References Cited
1. Fitts, P. M. (1954). *The information capacity of the human motor system in controlling the amplitude of movement.* Journal of Experimental Psychology, 47(6), 381–391.
2. MacKenzie, I. S. (1992). *Fitts' law as a research and design tool in human-computer interaction.* Human-Computer Interaction, 7(1), 91–139.
3. Flash, T., & Hogan, N. (1985). *The coordination of arm movements: An experimentally confirmed mathematical model.* Journal of Neuroscience, 5(7), 1688–1703.
4. Meyer, D. E., Abrams, R. A., Kornblum, S., Wright, C. E., & Smith, J. E. K. (1988). *Optimality in human motor performance: Ideal control of rapid aimed movements.* Psychological Review, 95(3), 340–370.
5. Harris, C. M., & Wolpert, D. M. (1998). *Signal-dependent noise determines motor planning.* Nature, 394, 780–784.
6. Todorov, E. (2004). *Optimality principles in sensorimotor control.* Nature Neuroscience, 7, 907–915.
7. Schrum, J., Karpov, I. V., & Miikkulainen, R. (2011/2012). *$\text{UT}^2$: Human-like Behavior via Neuroevolution of Combat Behavior and Replay of Human Traces* (*Believable Bots*, Springer / IEEE CIG).
8. van Beers, R. J., Haggard, P., & Wolpert, D. M. (2004). *The role of execution noise in movement variability.* Journal of Neurophysiology, 91(2), 1050–1063.
9. Xenopoulos, P., & Silva, C. (2022). *ESTA: An Esports Trajectory and Action Dataset.* arXiv:2209.09861.
10. Durst, D. B. et al. (2022/2024). *CSKnow: Learning Human-Like Movement and Aiming from Match Telemetry.*
11. Listman, J. B., Tsay, J. S., Kim, H. E., Mackey, W. E., & Heeger, D. J. (2021). *Long-term motor learning in the "wild" with high-volume video game data.* PLOS ONE, 16(12), e0261361.
