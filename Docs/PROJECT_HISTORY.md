# Lead Rush II Project History

## Origins: LeadRush HD

Lead Rush II is the Unity 6 HDRP successor/rebuild of the older **LeadRush HD** mother
project. The mother repository is
[Tokey/LeadRushHD](https://github.com/Tokey/LeadRushHD), and the local legacy build is
`H:/Unity Projects/Builds/Lead Rush HDRP/LeadRushHDRP.exe`.

The legacy project established the original arena-shooter direction: first-person combat,
enemy and round management, HUD/minimap concepts, local player/enemy CSV data, and
standalone HDRP build workflows. Lead Rush II deliberately preserves some legacy entry
points in `Assets/` for compatibility while moving active gameplay onto the LR2 runtime.

## Legacy scripts still in the repository root

- `Assets/GameManager.cs`, `Assets/RoundManager.cs`, and
  `Assets/InGameScripts/EnemyManager.cs` belong to the earlier project generation.
- `Assets/PlayerStats.cs`, `Assets/GameUI.cs`, `Assets/LightSwapper.cs`,
  `Assets/SkySwapper.cs`, `Assets/TextureSwapper.cs`, and
  `Assets/EnvironmentPEDisabler.cs` remain from legacy scenes/builds or utility passes.
- Active LR2 work lives under `Assets/LR2/Runtime`, `Assets/LR2/Editor`, and
  `Assets/LR2/Scenes`; derived/generated content should be produced through LR2 builders
  rather than editing generated scenes or prefabs by hand.

## Rebuild timeline

- **2026-11-16:** Lead Rush II rebuild began, with Claude establishing generated maps,
  weapons, soldier rigs, bots and the initial AI workflow.
- **2026-11-17, morning:** HumanPose and shoulder-camera diagnosis, Kinemation
  retargeting, reload motion, recoil/TTK tuning and Hor+ FOV work.
- **2026-11-17, afternoon/evening:** procedural and atmospheric map work, weapon FX,
  menu/HUD work, automated QA, visible pose checks and cross-agent reviews.
- **2026-11-17 late evening:** Copilot joined for laser, audio, bot-AI documentation,
  glass, typography research, QA and world/atmosphere components.
- **2026-11-18:** protocol v3 added clocked work, process locks, measured cross-review
  ledgers and bug-first lane priorities. The rebuild also added Free-for-All Capture
  the Flag (per-combatant capture progress and scoring), the `-lr2Weapon <id>` pose
  check selector for per-gun first-person verification, nine looping 1:1 bot-AI
  explainer GIFs, and the three-tab interactive manual at
  [`Docs/Bot/bot-brain.html`](Bot/bot-brain.html).
- **2026-11-19 to 11-22 (`11.22.26.1`):** Unified **Bot Lab** (`Bot/bot-lab.html`), resolved B125 shared `IkMotion` race conditions, added title tiles & `CALLSIGN` (`MainMenuController`), implemented [`DECISION_PROVENANCE.md`](AI_Collaboration/DECISION_PROVENANCE.md), reverted the default character animation driver cleanly onto Kinemation's OG `FPSGenericPlayer` (`B136`), upgraded circular `LR2/PipScope` sights, and rebuilt the audio/music stack across all 4 maps.
- **2026-11-23 (`11.23.26.1`):** Re-enabled visible Mixamo SWAT + Ghost skull mask via `GenericBoneMirror` (`handErr <= 0.004 m`) & `KillcamDirector` execution order 600 parity, added multi-layer HDRP `VolumetricClouds`, completed the **Lead Rush II** global rename (`B148`), shipped 3-mode Groq LLM integration + 8-matchup study ([`LLM_BOT_STUDY.md`](AI_Collaboration/LLM_BOT_STUDY.md), [`LLM_BOT_IMPLEMENTATION.md`](Guides/LLM_BOT_IMPLEMENTATION.md)), and published per-model token rankings (`B150`).
- **2026-11-24 (`11.24.26.1`):** Split Combat Lobby into **SKIRMISH** | **EXPERIMENTS** (moving **AI vs AI** and customizable N-match blocks into Experiments with Blind Mode & presets), added **`InvasionFramerate`-style `Data/` zero-stutter telemetry** + async Saving overlay, implemented & calibrated **`DumbRobotic`** (`24–25` Veteran K/D parity vs `HumanLike`, yet `47.6%` HLI failing the Turing bar), enabled **1v1/FFA `LlmEnhanced` bots**, built offline heatmap/C2ST/equation fitting ([`Tools/analyze_experiment_session.py`](../Tools/analyze_experiment_session.py)) + **`HumanFitted`** clones (`human_behavior_fit.json`), and upgraded [`Docs/Bot/llm-architecture.html`](Bot/llm-architecture.html) with 9 live maneuver animations and C1–C3 20 Hz heatmaps ([`HUMAN_LIKENESS_EVALUATION.md`](Research/HUMAN_LIKENESS_EVALUATION.md), [`DATA_COLUMNS.md`](Research/DATA_COLUMNS.md)).

## What LR2 added

LR2 is a Unity 6000.5.0f1 HDRP project organized around generated content and reusable
runtime contracts. It adds humanoid retargeting and full-body first-person/third-person
presentation, procedural reload and recoil, surface-aware impacts, killcams, bot utility &
Groq LLM AI (`Commander` / `TacticalMicro` / `AvatarPilot`), data-driven `HumanFitted` &
robotic control archetypes, map-specific lighting/weather, HDRP glass and environment
realism components, camera shake, persistent settings, automated QA, and `InvasionFramerate`-modeled
`20–60 Hz` experiment telemetry logging.
