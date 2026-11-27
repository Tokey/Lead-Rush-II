# Lead Rush II (`11.27.26.1`) — Engineering & Continuity Handoff Notes

- **Project / Brand:** **Lead Rush II** (`LR2`) — `Tokey/Lead-Rush-2` (physical local repo path remains `h:\Unity Projects\GitHub Repos\LeadRush 2`)
- **Unity Version:** `6000.5.0f1` (`HDRP 17.5`) + Kinemation FPS Animation Framework (`Assets/Kinemation` + OG `FPSGenericPlayer` driver + `GenericBoneMirror`)
- **Standalone Windows Player Output:** `H:\Unity Projects\Builds\Lead Rush II\Lead Rush II.exe`
- **External Telemetry & Profile Root (`InvasionFramerate`-modeled `B155`):**
  - Editor: `<project root>/Data/` (`SessionState.csv`, `Presets/`, `Profiles/human_behavior_fit.json`, `Logs/<sessionId>_<runId>/`)
  - Built Player: `<Lead Rush II.exe dir>/Data/`
- **Groq API Key Storage (git-ignored & outside repo):** `C:\Users\<user>\AppData\LocalLow\DefaultCompany\LeadRush 2\groq_api_key..txt` (`GroqClient.cs` fallback reads `-lr2GroqKeyFile`, `GROQ_API_KEY`, or persistent path automatically; **never commit or log**)

> **Single Unity Process Rule:** Keep the Unity Editor GUI **closed** when running `-batchmode`, `BuildWindowsPlayerBatch`, `LR2MapPreviewCapture`, or `-lr2QaBot`. Claim/release `UNITY:` in [`AGENT_COORDINATION.md`](AGENT_COORDINATION.md) and confirm `0` `Unity.exe` processes before launching. Use `Tools\LR2ParallelWorker.ps1` only with explicit process checks.

---

## 1. Current Architecture Summary (`11.24.26.1`)

### A. Soldier Rig, Weapons & Visuals (`B136`–`B147`)
- **Rig (`GenericBoneMirror` + `LR2BoneMap` + `FPSGenericPlayer`):** the hidden Kinemation robot (`FPSGenericPlayer`) is the real character: Kinemation animates it and its IK puts the robot's hands on the gun. `GenericBoneMirror` (execution order 550) then poses the visible military mesh from it: bone rotations through T-pose offsets, arms and legs solved onto the robot's wrists and ankles, and (11-26) each palm aligned to the robot's, each finger bone aimed along its robot twin, and the hands sized to the robot's. Before that fix the visible thumb stood up into the iron sight and the fingers sat 36-38 mm from the robot's.
- **Kinemation core is vendor code (11-26 rule, from the developer: "don't mess up Kinemation").** `Assets/Kinemation` is git-ignored and must match the vendor package plus exactly four LR2 patches: `FPSBoneController.UpdateEntity` passes the entity to every layer; `FPSCameraController.Update` parent check; `KRigComponent.GetRigTransform(KRigElement)` name-checked lookup; `[ReadOnly]` on `AttachHandLayerJob._leftHandChain` (Unity 6.5 job-safety exception). Reference copy: `H:/Unity Projects/Backup/LR2_KinemationDemo_pre_OG_2026-11-22/Kinemation_core` (the first three). Gemini's 11-24 edits to nine core files were reverted; they are kept in `H:/Unity Projects/Backup/LR2_Kinemation_Gemini_edits_2026-11-24`. Fix problems in LR2 code; touch the core only with a measured reason, and list the patch here.
- **ADS and the bullet:** Kinemation's ADS layer moves the camera onto the active sight (`cameraBlend = 1`); nothing moves the gun after Kinemation (`WeaponAntiClip` only zeroes the item's local offset). Shots leave the camera (predicted for this frame's movement) along this frame's look direction (`SoldierController.UpdateAim`, before the gun ticks). In ADS they follow the sight: the gun's sway is applied to the shot and the recoil animation's cosmetic kick is not (`FollowSight`, capped at 6°). Measured: 0.0-0.3 px between bullet and sight on every gun and sight standing still, at most 2.8 px while strafing (the sight itself swings up to 32 px). Tracers fly at 720 m/s and start from the muzzle's current position every frame (`Tracer`, LateUpdate 900), so they stay with a strafing shooter. The player's recoil uses a copy of Kinemation's data with 15 % of the backward push and 30 % of the ADS kick-back (`LR2Firearm.playerPushScale/playerAdsKickbackScale`), because the full push put the optic 2-5 cm behind the eye.
- **ADS speed (11-27, developer: "fast paced shooter, ADS time cannot be sluggish"):** `WeaponDefinition.adsTime` is MK23 0.11, MK18 0.14, AK-12 0.15, AK-74N 0.16, FAL 0.19 s, with sprint-to-fire just above it (0.10-0.21 s); both are set in the `LR2ContentBuilder` weapon specs. The ADS time drives Kinemation's ADS blend (`aimingSpeed = 1 / adsTime` on the per-gun runtime copy), the camera zoom and LR2's linear `AimBlend` (spread, crosshair, shot blend). The blend and the zoom start fast: on every press and release `LR2Firearm.SetAdsCurve` gives Kinemation's ADS layer a new curve that heads for the chosen side at full speed from the current point, so a reversal half way never jumps; it is built in Update and never changed afterwards, because the animation job evaluates it on a worker thread (B125). Kinemation's aiming IK motion plays at 60 % size and ends within 1.5x the ADS time (`SoldierController.PlayAimingMotion`), and the 1.2 s stop lurch is skipped while aiming. Measured (`aim-audit <tag> adstime`, sight within 10 px of its resting point and staying there): standing 111-170 ms on every gun and sight, was 236-478 ms (AK-12 iron 339 -> 131 ms, red dot 371 -> 132 ms); out of a sprint 131-295 ms, was 591-662 ms (AK-12 158 ms forward, 217 ms sprinting the other way, 591 before; the last part is the body rising out of the sprint pose). Runs differ by about 20 ms. Pressing or releasing half way never jumps: the gun's top speed stays within 2x a plain ADS move. ADS-out now covers half the way in the first quarter of the ADS time, in step with the zoom. About 5 px of slow breathing sway stays in ADS on purpose. Old code: `Logs/LR2_AimGrip/ads_before_clean`; new code: `ads_final2` (the test also sprints both ways and reverses ADS half way).
- **Measuring aim, grip and animation:** `aim-audit <tag> [force] [motion|strafe|adstime]` through `LR2AgentBridge` (or `-executeMethod LR2.EditorTools.LR2AimGripAudit.RunBatch`) writes `Logs/LR2_AimGrip/<tag>/report.txt`, `metrics.csv` and screenshots: sight vs bullet, hand slip, visible-vs-robot fingers, thumb position; with `motion` a per-frame stance run (walk, sprint, crouch, prone, jump, slide, ADS, fire, reload, deaths and respawns); with `strafe` tracer and shot-origin lag while strafing; with `adstime` ADS in/out timing per gun, standing and out of a sprint (`ads_trace.csv` has every frame).
- **Killcam (`KillcamDirector.cs`, order `600`):** 60 Hz snapshot buffer + gun-relative arm IK locks (`hasHandLocks`) match third-person replay hands identically to first-person gameplay.
- **Weather & Lighting (`WeatherSystem.cs`, `SkyCloudMotion.cs`, `LR2MapBuilder*.cs`):** Multi-layer HDRP `VolumetricClouds`, doorway-visible outdoor rain, `SkyCloudMotion.ambientLightProbeDimmer >= 0.84f`, and dynamic storm indirect diffuse fill (`>= 2.65f`, `exposure >= +0.15f`) keeping Saltworks cleanly above QA thresholds (`worstMeanLum=0.065`, `worstEnemyContrast=0.058`, `Failures: 0`).

### B. Dual-Tab Combat Lobby & Experimental Suite (`B152`–`B158`)
- **`SKIRMISH` Tab (`SkirmishLobby.cs`):** Casual 2-Team, 4-Way, Duel, and FFA matches with per-slot `TYPE` (`HUMAN` / `DUMB` / `LLM` / `FITTED`), `DIFF` (`RECRUIT..ADAPTIVE`), and `TEAM` steppers (`AI VS AI` spectator removed from Skirmish).
- **`EXPERIMENTS` Tab (`ExperimentsTabView.cs`, `ExperimentSession.cs`):** Build customizable N-match sessions (`8-Match: 4 Human vs 4 LLM`, `6-Match: 1v1 Blind Turing`, `4-Match: AI vs AI Bench`, or custom), enable **Blind Mode** & **Randomized Order**, select `20 / 30 / 60 Hz` telemetry, save/load JSON presets (`Data/Presets/`), and collect subjective `0–100%` believability ratings (`Ratings_*.csv`).
- **Four Bot Archetypes (`BotTypes.cs`, `BotController.cs`, `HumanBehaviorProfile.cs`):**
  1. `HumanLike` — Fitts's Law + Balabit `Beta(2.0, 3.3)` flicks, `125 ms` lagged velocity lead, `1.55:1` Perlin tremor, recoil lag, ADAD/crouch/jiggle peeks.
  2. `DumbRobotic` — Mechanical linear angular slew (`145–260 °/s`), fixed reaction delay (`0.24–0.50 s`), isotropic error cone (`1.95–3.45°`), `0%` crouch/sprint/ADAD — calibrated to Veteran K/D parity (`24–25`, `0.96` vs `1.04`) while failing HLI (`47.6%`, `C2ST AUC 0.797`).
  3. `LlmEnhanced` — Groq `Commander` / `TacticalMicro` / `AvatarPilot` (now works in **1v1 Duel & FFA** too; scored `65.1%` HLI and `1.31` K/D in `C3_LlmReg`).
  4. `HumanFitted` — Loads `Data/Profiles/human_behavior_fit.json` fitted from player CSVs and scales timers across tiers (`0.72× / 0.90× / 1.00× / 1.15×`).

### C. Zero-Stutter Telemetry & Offline Human-Likeness Pipeline
- `ExperimentTelemetryRecorder.cs`: Pre-allocates value-struct buffers (`32,768` ticks / `16,384` frame times) and flushes `Ticks_*.csv`, `Events_*.csv`, `Flicks_*.csv`, `Recoil_*.csv`, `Summary_*.csv`, `Meta_*.json` (`frameDelivery`), and `SessionLog_*.csv` off-thread on `Task.Run` behind `SAVING EXPERIMENT TELEMETRY`.
- `Tools/analyze_experiment_session.py`: Mines match logs, renders 4 PNG heatmaps (`Docs/Media/experiments/`), runs 5-fold stratified C2ST (`scipy`), scores 0–100% HLI, and writes `human_behavior_fit.json`.
- Full documentation: [`Docs/Research/DATA_COLUMNS.md`](Docs/Research/DATA_COLUMNS.md), [`Docs/Research/HUMAN_LIKENESS_EVALUATION.md`](Docs/Research/HUMAN_LIKENESS_EVALUATION.md), [`Docs/Updates/2026-11-24.md`](Docs/Updates/2026-11-24.md), and [`Docs/Bot/llm-architecture.html`](Docs/Bot/llm-architecture.html).

---

## 2. Next Open Experiment Milestone (`B157`)
- **Action for Developer (`B157`):** Launch `Lead Rush II.exe` -> `PLAY` -> `EXPERIMENTS`, play a real human session (e.g., `8-MATCH: 4 HUMAN vs 4 LLM` or `6-MATCH: 1V1 TURING BLIND`), then run:
  ```powershell
  python Tools/analyze_experiment_session.py --session "Data/Logs/<sessionId>_<runId>" --reference Human
  Copy-Item "Data/Logs/<sessionId>_<runId>/analysis/human_behavior_fit.json" "Data/Profiles/human_behavior_fit.json" -Force
  ```
  Then spawn `FITTED` bots in Skirmish/Experiments to benchmark your personal clone's HLI against `HumanLike`, `LlmEnhanced`, and `DumbRobotic`.

---

## 3. Standard CLI Verification Commands

```powershell
# Compile gate (zero Unity process required)
dotnet build LR2.Runtime.csproj -nologo -v q; if ($LASTEXITCODE -eq 0) { dotnet build LR2.Editor.csproj -nologo -v q }

# Full player build
$unity = 'C:\Program Files\Unity\Hub\Editor\6000.5.0f1\Editor\Unity.exe'
$proj  = 'h:\Unity Projects\GitHub Repos\LeadRush 2'
$p = Start-Process -FilePath $unity -WorkingDirectory $proj -ArgumentList @('-batchmode','-nographics','-quit','-projectPath',$proj,'-executeMethod','LR2.EditorTools.LR2BuildPipeline.BuildWindowsPlayerBatch','-logFile',"$proj\Logs\LR2_BuildPlayer.log") -PassThru
$p.WaitForExit()

# Visible scene QA (requires singular -lr2Scene + -lr2QaBot)
$p = Start-Process -FilePath $unity -WorkingDirectory $proj -ArgumentList @('-projectPath',$proj,'-executeMethod','LR2.EditorTools.LR2QABot.RunBatch','-lr2QaBot','-lr2Scene','Assets/LR2/Scenes/LR2_Saltworks.unity','-logFile',"$proj\Logs\LR2_QA_Saltworks.log") -PassThru
$p.WaitForExit()
```
