# Lead Rush II Tooling

Run commands from the Unity project root unless a command explicitly names a project path.

## Builds and generators

| Tool | Purpose | Command / entry | Output |
|---|---|---|---|
| LR2 build pipeline | Builds all generated LR2 content and scenes | Unity menu `LR2 > Build Everything`; `LR2.EditorTools.LR2BuildPipeline.BuildAllBatch` / `BuildAllAndPlayerBatch` | Generated prefabs, scenes, UI, maps, derived assets & standalone `.exe` |
| LR2 parallel Unity worker | Junctions `Assets`/`Packages`/`ProjectSettings` to `H:\Unity Projects\Builds\LR2_ParallelWorker` so a second `Unity.exe` builds or tests concurrently | `powershell -ExecutionPolicy Bypass -File Tools\LR2ParallelWorker.ps1` | Non-blocking builds, visible QA, or `-lr2PoseCheck` while main Editor is open |
| LR2 content builder | Builds FX, audio, weapons and soldier content | `LR2ContentBuilder` methods through `LR2BuildPipeline` | Runtime assets under `Assets/LR2` |
| LR2 map builder | Creates the map scenes and map presentation | `LR2MapBuilder` / `LR2BuildPipeline` | Four map scenes plus preview assets |
| LR2 scope & grenade builder | Upgrades PiP optic materials to circular `LR2/PipScope` & binds `AA_LR2_GrenadeThrow` | `LR2ScopeBuilder.UpgradePipScopesAndGrenades` | Circular clipped scope materials + grenade throw clip |
| LR2 prop & spawn audit | Audits thin geometry, squashed pack props (>2.2x), prop overlap, floating props (>8 cm) & spawn capsule clearance | Unity menu `LR2 > Prop Audit` / `LR2.EditorTools.LR2PropAudit.RunBatch` | `Logs/LR2_PropAudit/report.txt` |
| LR2 glass builder | Creates clean, dirty and cracked HDRP glass materials | Unity menu `LR2 > Build Glass Materials` | `M_LR2_Glass_*` materials and mask textures |
| README/media builders | Creates and refreshes in-game screenshots, previews, dual-tab lobby captures & 24-frame combat GIFs (`-lr2GifDir`) | `LR2MapPreviewCapture` (`CaptureLobbyTabsBatch`) and `LR2ReadmeMediaRunner` | `Docs/Media`, `lobby_*_tab.png`, `Temp/GifFrames` / `-lr2GifDir`, and map preview PNGs |
| Offline experiment analyzer | Mines `Data/Logs/<id>_<runId>/`, computes 5-pillar HLI + 5-fold C2ST AUC + Sliced Wasserstein EMDs, plots 4 heatmaps & fits `human_behavior_fit.json` | `python Tools/analyze_experiment_session.py --session <dir> --out <dir>` | `report.md`, `human_behavior_fit.json`, `artifact_heatmaps.json`, 4 PNGs |

## QA and smoke tests

| Tool | Purpose | Command / entry | Output / acceptance |
|---|---|---|---|
| LR2 QA bot | Runs measurable four-map checks for visibility, glare, SFX, hit feedback, motion and settings | `LR2.EditorTools.LR2QABot.RunBatch` | `Logs/LR2_QABot/qa_<scene>.txt` with PASS/FAIL lines |
| LR2 extra QA checks | Adds reload baseline, tactical ammo/charge, foot planting, hands, trauma and FOV invariants | Called by `LR2QABotRunner` | Extra `CheckExtra` PASS/FAIL lines in QA reports |
| LR2 smoke test | Runs a timed visible or batch play session and records runtime errors | `LR2.EditorTools.LR2SmokeTest.RunBatch` | `Logs/LR2_SmokeTest.txt` plus captured scene state |
| First-person pose check | Samples reload, idle, ADS, gait, hand and rig alignment | `LR2SmokeTest` `-lr2PoseCheck` flags | `Logs/LR2_PoseCheck` reports and screenshots |
| HDRP fidelity screenshot runner | Captures separated lighting, texture and VFX views | `LR2.EditorTools.LR2FidelityScreenshotRunner` when available | PNG captures and scene diagnostics |

## Bot explainer manual and media

- [`Docs/Bot/llm-architecture.html`](Bot/llm-architecture.html) is the interactive **LLM Architecture, 9 Tactical Maneuvers & 20 Hz Heatmap Lab** (plain-English walkthrough, 9 live key-pulsing maneuver canvases, and measured C1–C3 + M1–M8 charts).
- [`Docs/Bot/bot-lab.html`](Bot/bot-lab.html) is the interactive manual in three parts: the bot, game modes, and how it
  was built. Its artifact source is `Docs/Bot/src/bot-lab.html`; `python Tools/make_bot_lab_gifs.py` rebuilds the
  GitHub copy and the eight scenario GIFs. `Docs/Bot/bot-brain.html` redirects to it (merged 2026-11-18).
- `Tools\make_bot_gifs.py` regenerates the nine looping 1:1 explainer GIFs in
  `Docs\Media\bot\`, including `watch-a-bot-play.gif`, perception, Fitts flick,
  human miss pattern, recoil, adaptive skill, dual pathfinding, CTF/chat, and the
  multi-agent workflow.
- Select a weapon for a pose run with `-lr2Weapon <id>` (`AK12`, `Mk23`, `AK74N`,
  `Mk18`, or `FAL`). The harness captures hand close-ups plus idle, ADS, gait and
  reload views for the selected weapon.
- The game-mode builder includes Free-for-All CTF (`GameModeType.FreeForAllCaptureTheFlag`);
  `FlagZoneController` tracks each combatant's independent capture progress and
  `MatchManager` awards the capture to that individual.

## Compile and visible Unity checks

- Runtime assembly:
  `dotnet build LR2.Runtime.csproj -nologo -v q`
- Editor assembly:
  `dotnet build LR2.Editor.csproj -nologo -v q`
- Visible QA bot:
  launch Unity with `-executeMethod LR2.EditorTools.LR2QABot.RunBatch` in a
  1120x630 window.
- Visible pose check:
  launch Unity with `LR2SmokeTest.RunBatch -lr2PoseCheck
  -lr2Scene Assets\LR2\Scenes\LR2_TestRange.unity
  -lr2OutDir "<project>\Logs\LR2_PoseCheck" -lr2Duration 52`.

## Agent coordination tools

| Tool | Purpose | Command / file |
|---|---|---|
| Agent board | Single shared state for ownership, tracker status, requests and logs | `AGENT_COORDINATION.md` |
| Agent bridge | Lets an open Unity editor execute agent commands without acquiring a second process | `Temp/LR2_AgentCommand.txt`; results in `Temp/LR2_AgentResult.txt` |
| Agent bridge commands | Refresh, rebuild weapons/characters/maps, build Windows player, play or stop | `refresh`, `build-weapons`, `rebuild-chars`, `rebuild-all`, `build-player`, `play <scene> <seconds>`, `stop` |
| Copilot prompt | Continue Copilot in VS Code agent mode | `code chat -m agent -r -a AGENT_COORDINATION.md '<prompt>'` |
| Gemini prompt | Continue or start the Gemini/Antigravity work conversation | `agy --continue --mode auto -p '<prompt>'` or the Hydrogen model command from the board |

## Collaboration research ledgers

- `Docs/AI_Collaboration/data/tasks.csv`: delivered work by agent/model.
- `Docs/AI_Collaboration/data/bugs.csv`: escaped, reviewed and pre-push defects.
- `Docs/AI_Collaboration/data/token_estimates.csv`: estimated coding model + exact Groq runtime API token counts (`B150`).
- `Docs/AI_Collaboration/analysis/analyze.py`: computes model/task quality summaries, protocol comparisons, and token usage ranking plots (`token_usage_ranking_by_model`, `token_efficiency_per_task_by_model`, `token_share_by_agent_and_phase`).
- `Docs/AI_Collaboration/analysis/provenance.py`: decision provenance (who decided what), the atomic feature table page, figures and the animated chart.
- `Docs/AI_Collaboration/RESEARCH_LOG.md`: narrative research and protocol evidence.
- `Docs/Guides/LLM_BOT_IMPLEMENTATION.md`: C# & Groq Cloud architecture tutorial for `GroqClient.cs` and `LLMCommander.cs`.
- `Docs/Research/DATA_COLUMNS.md` & `Docs/Research/HUMAN_LIKENESS_EVALUATION.md`: `InvasionFramerate`-modeled `Data/` external telemetry layout (`Data/SessionState.csv`, `Data/Presets/`, `Data/Profiles/human_behavior_fit.json`, `Data/Logs/<id>_<runId>/`), CSV schemas, HLI formulas, and C1–C3 heatmap results.
- Standalone Windows build output path (`11.23.26.1+`): `H:/Unity Projects/Builds/Lead Rush II/Lead Rush II.exe`.

## Weapon-stability, LLM & Experiment Telemetry switches (`B125`, `B127`, `B151`, `B154`–`B156`)

| Switch | What it does |
|---|---|
| `-lr2AimBench` | AI vs AI bench with the weapon-flip detector: per-frame muzzle vs aim, a 40-frame black box per event, spike counters, `teams.csv` |
| `-lr2AblateLayers Sway,Ads,...` | Sets those Kinemation layer types to weight 0 during the bench, to find which layer turns the weapon |
| `-lr2AdsWallCheck` | First-person check: aiming into a wall head-on, at 45 degrees, into a soldier and dying against a wall (`adswall.csv`) |
| `-lr2FlipGuard` | Turns on the after-animation flip clamp (off by default since the root causes were fixed) |
| `-lr2SharedIkMotions`, `-lr2NoItemReset` | Restore the old behaviour (shared motion assets, no gun-pose reset) for A/B tests |
| `-lr2LlmTeams Alpha,Bravo`, `-lr2LlmMode Commander\|TacticalMicro\|AvatarPilot`, `-lr2LlmModel <name>`, `-lr2LlmInterval <s>`, `-lr2GroqKeyFile <path>` | LLM integration depth & model/interval switches; key reads from `GROQ_API_KEY`, `-lr2GroqKeyFile`, or fallback `groq_api_key.txt` |
| `-lr2BenchArchetypes Alpha=DumbRobotic,Bravo=HumanLike`, `-lr2AlphaDiff 0..3`, `-lr2BravoDiff 0..3` | Assigns per-team `BotArchetype` (`HumanLike`, `DumbRobotic`, `LlmEnhanced`, `HumanFitted`) and difficulty tier during `-lr2AimBench` |
| `-lr2RecordTelemetry`, `-lr2TelemetryOut <dir>` | Enables zero-stutter 20/30/60 Hz telemetry recording (`Ticks_*.csv`, `Events_*.csv`, `Flicks_*.csv`, `Recoil_*.csv`, `Summary_*.csv`, `Meta_*.json`) outside the Experiments tab or redirects output directory |

## Tuning ADS sway (Kinemation animator layers)

- **Rig note (`B136`, `2026-11-22`):** Soldiers run on Kinemation's OG `FPSGenericPlayer` Generic rig (`LR2ContentBuilder.Robot.cs`) with its native `AnimatorProfile` Generic weapon profiles (`LR2ScopeBuilder.UpgradePipScopesAndGrenades` upgrades PiP scope materials to `LR2/PipScope` and binds `AA_LR2_GrenadeThrow`).
Every LR2 rifle (MK18, AK-12, AK-74N, FAL) uses Kinemation's `Assets/KinemationDemo/AnimatorProfiles/AnimatorProfile_Rifle_Humanoid` (or OG Generic profile under `B136`)
and the MK23 uses `AnimatorProfile_Pistol_Humanoid`. Select the profile and press **Edit Layer** next to a layer, as in Kinemation's videos. All 13 layers are there.

| What to change | Where |
|---|---|
| How much sway is left at full ADS | `SwayLayerSettings` > Curve Blending > `AimingWeight` (Mask) **Clamp Min**: 0 = none, 1 = full; LR2 default 0.22 |
| Weapon lag when turning | `SwayLayerSettings` > Aim Sway Position / Rotation Spring |
| Weapon motion when walking | `SwayLayerSettings` > Move Sway Position / Rotation Spring |
| Idle breathing on top | `AdditiveLayerSettings` |

- The builder writes its sway defaults once and marks the profile with the asset label `LR2SwayTuned`. After that, rebuilds leave
  the Sway layer alone. Remove the label to get the defaults back.
- The ADS speed in `AdsLayerSettings` and the wall tuck in ADS (`CollisionLayerSettings`, 12 % at full ADS) are set per weapon at
  run time by `LR2Firearm` (from `WeaponDefinition.adsTime`), so edit those in code, not in the profile.
- The profiles are cloned per weapon when it is equipped. Change values before pressing Play, or switch weapons to pick them up.
- `Assets/KinemationDemo` is a paid pack and is not in git, so profile edits stay on this machine.

