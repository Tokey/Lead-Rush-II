# `B152` — Human-Likeness Evaluation, Data Mining & Equation-Fitting Research Specification

**Status:** Active research & engineering spec (`Lead Rush II` `11.24.26.1`)  
**Goal:** Build a mathematically grounded, reproducible experimental pipeline in Lead Rush II to (1) collect comprehensive zero-stutter gameplay telemetry from a human player and AI/LLM/Robotic bots under controlled experimental blocks, (2) evaluate how close any bot's movement, aiming, recoil control, timing, and overall behavior is to real human play on a calibrated **0–100% Human-Likeness Index (HLI)**, and (3) fit human empirical equations directly back into the non-LLM bot parameters across difficulty tiers.

---

## 1. Literature Synthesis — How Prior Work Measures & Achieves Human-Likeness in FPS Games

| Study / Benchmark | Domain | Core Method | Primary Metric(s) & Key Insight |
|---|---|---|---|
| **Hingston (2009–2012) & 2K BotPrize** (`UT^2` Schrum et al., 2012; `MirrorBot` Polceanu, 2013) | *Unreal Tournament 2004* FPS Bot Turing Test | Human judges and players fight alongside/against bots in shared 3D arenas using a "judging gun" to classify Human vs. Bot live | **Humanness Score (% mistaken for human)**; `> 50%` clears the Turing bar. Key failures of scripted bots: instant target snapping, zero reaction latency, straight-line movement, wall-hugging, and robotic navigation precision. |
| **Pearce & Zhu (2022, NeurIPS / arXiv)** — *Counter-Strike Deathmatch with Large-Scale Behavioural Cloning* | *CS:GO* pixel-to-action behavioral cloning | Trained neural agents on millions of human frames; compared BC agents vs. built-in heuristic bots vs. human matches | **2D Spatial Map Coverage Heatmaps + Earth Mover's Distance (EMD / Wasserstein-1)**. Visual heatmaps + EMD expose unhuman routing, choke neglect, and uniform patrol loops even when K/D matches. |
| **Devlin, Hofmann et al. (2021, Microsoft Research / Project Paidia)** — *Navigation Turing Test (HNTT / ANTT)* & *"Navigates Like Me"* | 3D game navigation (*Bleeding Edge*) | Human assessors rate trajectory clips (**HNTT**) and supervised classifiers (**ANTT**: video barcodes, 2D top-down trajectories, frame GRUs) predict Human vs. AI | Smooth physical flow vs. jerky micro-corrections, erratic pathing, and unnatural obstacle stops are the strongest discriminators of non-human movement. |
| **Flash & Hogan (1985); Meyer et al. (1988); Harris & Wolpert (1998)** | Psychophysics & sensorimotor control | Minimum-jerk bell kinematics, Fitts's Law (`MT = a + b * log2(D/W + 1)`), Optimized Dual-Submovement model, Signal-Dependent Noise (SDN) | Explains asymmetric saccade + corrective submovement timing, overshoot under urgency, and why endpoint spread scales linearly with motor command magnitude. |
| **Balabit Mouse Dynamics; ESTA / CSKnow / Aim Lab Studies** | Real human mouse & FPS crosshair datasets | Skewed speed profiles `Beta(2.0, 3.3)` peaking at ~34% movement duration, 8% lateral wrist bow, 125 ms visuomotor tracking lag, `1.55:1` horizontal-to-vertical error anisotropy | Captures wrist/arm anisotropy (`sigma_along ≈ 2.27 * sigma_perp`) and log-normal pause distributions (`mu=-0.373, sigma=1.036`) missing from generic spring dampers. |
| **Lopez-Paz & Oquab (2017, ICLR)** — *Classifier Two-Sample Tests (C2ST)* + **Wasserstein / Sliced EMD** | High-dimensional distribution indistinguishability | Train a binary critic classifier on held-out folds of fixed-duration human vs. bot feature windows | **Held-out ROC AUC -> Similarity Score**: `AUC ≈ 0.50` (`50%` accuracy) = statistically indistinguishable (`100%` C2ST score); `AUC → 1.0` = trivially detectable bot signature. |

---

## 2. Three Competing Bot Archetypes Under Comparison

To isolate **how closely a bot behaves like a human** independently of **how lethal it is**, Lead Rush II evaluates three archetypes matched on gun skill (`K/D` and hit accuracy calibrated to parity in 4v4 & 1v1):

1. **`BotArchetype.HumanLike` (Current Procedural Motor + Behavioral Brain):** Fitts's Law + Balabit `Beta(2.0, 3.3)` flicks, wrist bow, `125 ms` lagged velocity lead, `1.55:1` Perlin tremor, recoil pull-down delay, ADAD strafing, crouch peeking, cover retreat, flanking, and ammo/armour resupply.
2. **`BotArchetype.DumbRobotic` ("Dumb Bot" — Control Baseline):** Purely mechanical bot with **zero human kinematics**: linear constant-speed angular slew (`MoveTowardsAngle`), fixed constant reaction latency (no eccentricity curve), discrete 0.65s stepped isotropic error cone, straight NavMesh pursuit that stops dead at engagement range, no ADAD strafing, no jiggle/crouch peeks, no slides, no cover ducking on reload, and no corner slicing — **calibrated via slew speed, burst length, and cone radius so its K/D (~`1.0`) and hit percentage match `HumanLike` bots**. If a human still feels `DumbRobotic` is obviously artificial even at identical K/D, our HLI framework must quantify *why*.
3. **`BotArchetype.LlmEnhanced` (`Commander`, `TacticalMicro`, `AvatarPilot`):** Asynchronous System 2 Groq planning driving System 1 bots (now supporting both team squads and solo 1v1 Duel/FFA matches).
4. **`BotArchetype.HumanFitted` (Target Data-Driven Model):** Loads `human_behavior_fit.json` fitted directly from the player's own Experiment telemetry CSVs and scales timers across `Recruit` (`0.72×`), `Regular` (`0.90×`), `Hardened` (`1.00×`), and `Veteran` (`1.15×`).

---

## 3. Composite Human-Likeness Index (`HLI`, 0–100%)

Every candidate bot class $Q$ is scored against the human reference distribution $P$ on identical maps and match rules using five orthogonal pillars:

$$\text{HLI}(Q \parallel P) = 0.20\,S_{\text{move}} + 0.25\,S_{\text{aim}} + 0.15\,S_{\text{recoil}} + 0.15\,S_{\text{timing}} + 0.25\,S_{\text{C2ST}} \in [0, 100]$$

| Pillar | Weight | Observable Features Compared ($Q$ vs. $P$) | Scoring Transform |
|---|---:|---|---|
| **$S_{\text{move}}$ (Spatial & Locomotion)** | `20%` | 2D map occupancy heatmap ($28 \times 28$ grid, Gaussian smoothed) via **2D Sliced Wasserstein Distance (SWD / EMD)** + speed PDF JSD + ADAD lateral direction-reversal rate (`Hz`) + sprint/crouch duty ratios | $100 \cdot \exp\!\left(-\frac{\text{SWD}_{\text{norm}}}{0.22}\right) \times 0.6 + 100(1 - \text{JSD}_{\text{speed}})\times 0.4$ |
| **$S_{\text{aim}}$ (Crosshair & Flick Kinematics)** | `25%` | 2D target-relative crosshair error `(err_yaw, err_pitch)` heatmap SWD + flick speed peak-time fraction `time_to_peak_frac` (`~0.34` vs symmetric `0.50` vs constant `1.0`) + path straightness ratio + angular jerk (`deg/s^2`) + overshoot rate + `MT vs ID` Fitts $R^2$ | Weighted exponential similarity across normalized Wasserstein-1 distances & Fitts slope/peak alignment |
| **$S_{\text{recoil}}$ (Spray & Pull-Down Control)** | `15%` | Downward compensatory mouse delta `raw_pulldown_pitch` across shots `1..12` + cognitive recoil onset lag (`recoilLagSec`) + burst length distribution JSD | $100 \cdot \exp(-|\Delta\bar{g}_{\text{comp}}| / 0.35) \times 0.6 + 100(1 - \text{JSD}_{\text{burst}})\times 0.4$ |
| **$S_{\text{timing}}$ (Combat Timing & Decisions)** | `15%` | Visuomotor reaction latency W1 (`TARGET_ACQUIRED -> FIRST_SHOT_ON_TARGET`), engagement distance CDF, ADS duty cycle during combat, reload magazine fraction threshold | Normalized 1D Wasserstein & KS similarity across timing/decision distributions |
| **$S_{\text{C2ST}}$ (Classifier Two-Sample Turing Test)** | `25%` | Non-overlapping `2.0 s` behavioral feature windows (`mean_speed, strafe_flip_hz, crouch_duty, mean_ang_vel, p95_ang_vel, ang_jerk, aim_err_std, ads_duty, fire_duty, dist_to_cover`) evaluated with 5-fold stratified **L2 Regularized Logistic Regression** + **Depth-1 Decision-Stump Ensemble** (`scipy` + `numpy`) | $\text{HLI}_{\text{C2ST}} = 100 \cdot \max\bigl(0,\; 1 - 2\max(0, \text{AUC}_{\text{CV}} - 0.5)\bigr)$ |

* **Reference calibration check:** Split-half **Human vs. Human** (`50%` odd vs. even windows) sets the empirical upper noise ceiling (`~88–97%`), whereas **`DumbRobotic`** must score far lower (typically `< 45%`, especially on $S_{\text{aim}}$ and $S_{\text{C2ST}}$) even when its `K/D` equals `HumanLike`.
* **Optional Subjective Turing Judge Score ($\text{HLI}_{\text{judge}}$):** When **Blind Mode** + **Post-Match Rating** are enabled in the Experiments tab, the developer's own `0%–100%` human judgment after each blinded match is stored alongside objective `HLI` in `session_meta.json` and `session_ratings.csv`.

---

## 4. Comprehensive Telemetry Recording Schema (`B155`)

Recorded lock-free into pre-allocated in-RAM struct buffers at `20 Hz` (configurable `20 / 30 / 60 Hz`) for **both human players and every bot** on every frame cadence, then serialized on a background thread at match end behind an explicit `SAVING TELEMETRY DATA...` overlay so zero micro-stutter corrupts aim timestamps:

1. **`ticks.csv`** (continuous state @ 20–60 Hz):
   `match_time,participant_id,display_name,is_human,archetype,difficulty,team,alive,pos_x,pos_y,pos_z,vel_x,vel_y,vel_z,speed,yaw,pitch,raw_look_dx,raw_look_dy,recoil_dx,recoil_dy,ang_vel_yaw,ang_vel_pitch,move_x,move_y,is_sprinting,is_crouching,is_proning,is_aiming,aim_blend,lean,is_firing,is_reloading,weapon,ammo_mag,hp,armor,has_visible_enemy,nearest_enemy_dist,err_yaw_deg,err_pitch_deg,err_total_deg,enemy_vel_lat,near_cover_dist,bot_goal,bot_order,is_flicking`
2. **`events.csv`** (discrete combat & motor transitions):
   `match_time,event_type,actor_id,target_id,pos_x,pos_y,pos_z,yaw,pitch,dist_m,err_deg,damage,zone,weapon,extra`
3. **`flicks.csv`** (segmented ballistic saccades & casual scans):
   `actor_id,is_human,archetype,difficulty,start_t,end_t,duration_s,start_ang_deg,peak_ang_vel,time_to_peak_frac,path_len_deg,straightness_ratio,overshoot_deg,final_err_deg,reaction_latency_s,is_combat`
4. **`recoil_bursts.csv`** (per-shot spray compensation & accuracy):
   `actor_id,is_human,archetype,difficulty,burst_id,shot_idx,weapon,is_ads,dt_since_burst_start,recoil_kick_pitch,raw_pulldown_pitch,net_pitch_drift,yaw_weave,err_yaw_deg,err_pitch_deg,hit_target`
5. **`summary.csv`** & **`session_meta.json`** (per-participant aggregate K/D/A, shots, hits, accuracy, HS%, reaction time, blind judge rating, and map coordinate bounds).

---

## 5. Data-Driven Equation Fitting Targets (`human_behavior_fit.json`)

`Tools/analyze_experiment_session.py` fits the following equations directly from logged `Human` rows using `scipy.optimize` (and falls back cleanly when analyzing pure bot calibration runs):

- **Fitts's Law Saccade Duration:** $MT = a + b \log_2(D / W + 1)$ (`fittsA`, `fittsB`)
- **Balabit Skewed Velocity Profile:** $\text{Beta}(\alpha, \beta)$ fitted by method-of-moments from `time_to_peak_frac` (`betaA`, `betaB`)
- **Log-Normal Visuomotor Reaction Time:** $\ln(\tau_{\text{react}}) \sim \mathcal{N}(\mu_r, \sigma_r^2)$ (`reactMu`, `reactSigma`)
- **Anisotropic Endpoint Spread & Overshoot:** `sigmaAlongFactor`, `sigmaPerpRatio`, `overshootRate`
- **Recoil Compensation & Burst Discipline:** `recoilLagSec`, `recoilCompGain`, `burstMean`, `burstPauseSec`, `reloadAmmoFrac`
- **Combat Movement Cadence:** `strafeSwitchHz`, `crouchDuty`, `adsRangeMeters`, `preferredRangeMeters`
- **Difficulty Tier Scaling Curve:** Multipliers scaling fitted baseline equations cleanly across `Recruit` (`0.72×`), `Regular` (`0.90×`), `Hardened` (`1.00×`), and `Veteran` (`1.15×`).
