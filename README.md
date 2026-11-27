# Lead Rush II

Tactical first-person shooter built in **Unity 6 (`6000.5.0f1`) HDRP** ([github.com/Tokey/Lead-Rush-2](https://github.com/Tokey/Lead-Rush-2)).

**Lead Rush II** pairs full-body procedural first-person weapon & locomotion animation (`FPSGenericPlayer` + `GenericBoneMirror`) with multi-layer volumetric clouds and HDRP lighting, utility-driven & Groq LLM tactical bots, custom per-mode match rules, persistent camera/audio settings, and synchronized first-person killcam replays across four combat maps.

![Lead Rush II — Main Menu & Tactical Recon](Docs/Media/main_menu_hero.png)

**Latest (11.27.26.1, 2026-11-27): ADS is two to three times as fast.**
- Every gun's ADS time is about 30 % shorter (AK-12 0.21 to 0.15 s), the gun and the zoom move at full speed from the moment you press (and again when you let go), the aiming wobble ends with the ADS, and stopping no longer throws the sight off. Measured sight-on-target times: 111-170 ms standing (was 236-478 ms; the AK-12 131 ms, was 339-371 ms), 131-295 ms out of a sprint (was 591-662 ms).
- (11.26.26.2) Strafing: tracers stay with the gun, shots leave from where you are this frame, and in ADS they follow the sight's sway.

**11.26.26.1 (2026-11-26): a QA pass over the 11-24 work, measured in the editor.**
- Hands stay on the gun: a late "sight lock" that slid the gun out of the hands is gone, and the visible hands now follow the animation rig's grip (fingers within 18-19 mm instead of 36-38, thumb out of the sight).
- Bullets land on the sight: Kinemation's own ADS centres every sight within 4 px at 1080p, and shots use the current frame's aim.
- Iron sights work on every gun (the FAL had none, the MK18 had no rear sight); the AK-12 is the default rifle.
- The optic no longer slides into your eye while firing, bots take the same recoil as you, and a scripted run through every stance (sprint, crouch, prone, jump, slide, deaths and respawns) shows no pops or state leaks.
- Kinemation's own code is back to the vendor version (plus four small, documented patches), the menu and calm music got a warmer, hand-played "focus" layer, and the LLM bots got reliability fixes. See [Weapon IK](Docs/Animation/weapon-ik-report.standalone.html) and the [board](AGENT_COORDINATION.md) rows B161-B170.

> **Built by 4 AI agents.** One developer directs; **Claude Code** (`claude-opus-5-5`), **Antigravity ("Gemini")** (`gemini-4-hydrogen` / `orchestrator-neon` / `argon`), **GitHub Copilot** (`gpt-5.6-luna`), and **OpenAI Codex** (`gpt-6`) write the code, verify metrics, and cross-review through a shared board. The full story — with prompts, token usage rankings (`B150`), live LLM bot benchmarks (`B149`), and data-driven human-likeness research (`B152–B158`):
>
> - **[How AI agents are building Lead Rush II](Docs/AI_Collaboration/README.md)** (includes per-model token usage ranking charts!)
> - **[Human-Likeness Evaluation, Heatmaps & Equation Fitting (`11.24.26.1`)](Docs/Research/HUMAN_LIKENESS_EVALUATION.md)** (Composite HLI, 5-fold C2ST Turing test & `C1–C3` 20 Hz heatmaps)
> - **[Experimental `Data/` Folder & Zero-Stutter CSV Column Schema](Docs/Research/DATA_COLUMNS.md)** (`InvasionFramerate`-modeled `SessionState` / `SessionInfo` / `SessionLog` / `Ticks` / `Flicks` / `Recoil`)
> - **[Interactive LLM Architecture, 9 Tactical Maneuvers & 20 Hz Heatmap Lab](Docs/Bot/llm-architecture.html)** (plain-English guide, 9 live key-pulsing animations & C1–C3 heatmaps)
> - **[Who decided what: the developer's and the AI's decisions, feature by feature](Docs/AI_Collaboration/DECISION_PROVENANCE.md)**
> - **[LLM Squad AI Study (`M1–M8` Live Groq Benchmark & Humanization Roadmap)](Docs/AI_Collaboration/LLM_BOT_STUDY.md)** (read its 11-26 note first: rate limits and unequal teams limit what it shows)
> - **[How the LLM Bot Was Implemented (C# & Groq API Guide)](Docs/Guides/LLM_BOT_IMPLEMENTATION.md)**
> - **[How the AI agents test Lead Rush II (QA process & pose/aim telemetry)](Docs/AI_Collaboration/QA_PROCESS.md)**
>
> Project documentation:
>
> - [Project history](Docs/PROJECT_HISTORY.md)
> - [Tooling](Docs/TOOLING.md)
> - [P3 + HDRP 17.5 Upscaler Benchmark Report](Docs/AI_Collaboration/specs/P3_Report.md)

### By the numbers

Through **`11.24.26.1` (`2026-11-24`)**. Prompts are counted from [PROMPTS.md](Docs/AI_Collaboration/PROMPTS.md), tasks and defects
from the [ledgers](Docs/AI_Collaboration/data), board items from [AGENT_COORDINATION.md](AGENT_COORDINATION.md).

| Developer prompts | Tasks delivered | Defects logged | Features traced | Commits |
|---:|---:|---:|---:|---:|
| **319** (`329` archive sections) | **237** (`238` rows) | **106** | **287** | **165+** |

- **Tasks by agent:** Claude `113`, Gemini `71`, Copilot `35`, Codex `19`. **Defects:** `106` logged; `50` developer-reported, `56` caught by AI reviews/QA/pre-push checks.
- **Scope:** 9 calendar days (`2026-11-16` to `11-24`), 4 AI agents, 4 combat maps, dual-tab Skirmish/Experiments lobby with 4 bot archetypes (`HumanLike`, `DumbRobotic`, `LlmEnhanced`, `HumanFitted`), `20–60 Hz` zero-stutter telemetry, and live Groq LLM benchmarks (`38,405` exact API tokens across `M1–M8` + `C3`).

What changed recently: **[What's new on 2026-11-24 (`11.24.26.1`)](Docs/Updates/2026-11-24.md)** (Dual-tab `SKIRMISH` | `EXPERIMENTS` lobby + `AI VS AI` moved `B153`, `InvasionFramerate`-modeled `Data/` zero-stutter telemetry + Saving overlay `B155`, K/D-matched `DumbRobotic` + solo 1v1/FFA LLM `B154`, 5-pillar HLI / C2ST / 20 Hz heatmaps & `HumanFitted` profile loader `B152`/`B156`/`B157`, 9-maneuver animated + heatmap HTML lab `B158`) · earlier: [2026-11-23 (`11.23.26.1`)](Docs/Updates/2026-11-23.md), [2026-11-22](Docs/Updates/2026-11-22.md), [2026-11-20](Docs/Updates/2026-11-20.md), [2026-11-19](Docs/Updates/2026-11-19.md).

### Who built what

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="Docs/AI_Collaboration/figures/contribution_score_dark.png">
  <img alt="Bar chart: contribution score per AI agent and model (tasks delivered, bugs found, suggestions adopted, minus defects caused)" src="Docs/AI_Collaboration/figures/contribution_score_light.png">
</picture>

Contribution score per agent and model, from the task and defect ledgers in `Docs/AI_Collaboration/data` (tasks delivered and
verified, bugs found in other agents' work, adopted suggestions, minus defects that reached the developer). Method, token rankings
and the protocol experiments: [How AI agents are building Lead Rush II](Docs/AI_Collaboration/README.md#quality-which-model-produced-what-quality-of-work).

### Who decided what

<p align="center"><img src="Docs/AI_Collaboration/figures/prov_growth.gif" alt="Animated chart: 287 features appear in the order they shipped, in five rows by origin" width="820"></p>

Every one of the **287** tracked features and fixes is traced back to its origin, in five kinds: the developer's exact words (`33.4 %`), an AI's design for
the developer's goal (`28.9 %`), an AI's own idea (`12.2 %`), the developer's bug report (`20.9 %`), or an AI test that found a defect (`4.5 %`).

- The developer set the goal for **83.3 %** of features (`239 / 287`); an AI chose the implementation design for **66.6 %** (`191 / 287`).
- **79.6 %** of finished features were kept as built. All four reverts were AI design decisions.

The full study, with case studies and an evaluation using published research methods: **[Who decided what](Docs/AI_Collaboration/DECISION_PROVENANCE.md)**.
Every feature in one table, with the developer's words and the AI's decisions: **[Atomic feature table](Docs/AI_Collaboration/FEATURES_TABLE.md)** (snapshot from `11-20`, raw dataset updated to `281` rows in `data/features.csv`).

---

## Screenshots

### Main menu and match setup (2026-11-20)

The title screen is PLAY, SETTINGS and QUIT as tiles, a CALLSIGN field (your name in every match), and a camera that cuts between
b-roll shots of your soldier: the gun, a pan across the mask, a low angle, the profile.

<p align="center"><img src="Docs/Media/updates/2026-11-20/title_broll.gif" alt="The title screen's b-roll loop: gun close-up, face pan, low angle and profile shots of the soldier, with the tiles on the left" width="820"></p>

| Gun close-up | Face pan |
|---|---|
| ![Title screen: gun close-up](Docs/Media/updates/2026-11-20/title_gun.jpg) | ![Title screen: a pan across the mask](Docs/Media/updates/2026-11-20/title_face.jpg) |

![The skirmish lobby: PLAY opens a roster of up to 20 bots, each with its own difficulty and team, presets, and match settings](Docs/Media/match_setup.jpg)

| AI vs AI: chase cam on a bot | The headshot killcam |
|---|---|
| ![AI vs AI spectating with the AI test bench panel](Docs/Media/updates/2026-11-19/aivsai_chase.gif) | ![Headshot killcam: the replay, then the bullet cam](Docs/Media/updates/2026-11-19/killcam_headshot.gif) |

More from this build, with what changed: [What's new on 2026-11-19](Docs/Updates/2026-11-19.md).

In-game captures, 1920 x 1080, build **`11.22.26.1` (`2026-11-22`)**.

### Gameplay Clips & `11.22.26.1` Highlights

| Live Combat (Saltworks MK18, Tracers & `KILLCAM` Font) | Circular PiP Scope ADS (`AK-12`) |
|---|---|
| ![Live combat in Saltworks with MK18 iron sights, green/red lasers, armour HUD, and tracers](Docs/Media/gameplay_combat.gif) | ![AK-12 circular clipped PiP scope ADS](Docs/Media/ak12_pip_scope_ads.png) |

| Minimalistic Obsidian Loading Screen | Updated `CONTROLS` Tab & Key Reference |
|---|---|
| ![Minimalistic matte obsidian sector loading screen](Docs/Media/loading_screen.png) | ![Settings Controls tab with Toggle Aim, Toggle Sprint, Auto Sprint, and Controls Reference](Docs/Media/settings_controls.png) |

| Gunfight & Hitmarkers (`11.18.26.4`) | Locomotion & Sprint Transition |
|---|---|
| ![First-person gunfight with hitmarkers and tracers](Docs/Media/gifs/gunfight.gif) | ![First-person sprint cadence and locomotion transition](Docs/Media/gifs/sprint.gif) |

| Tactical & Empty Reloads | Ragdoll Death & Killcam Transition |
|---|---|
| ![First-person MK18 reload sequence](Docs/Media/gifs/reload.gif) | ![Elimination vignette transitioning into the killer killcam](Docs/Media/gifs/death_and_killcam.gif) |

| Armour HUD: 100 health + 50 armour | MK18 iron sights |
|---|---|
| ![Armour bar above the health bar](Docs/Media/hud_armour.png) | ![MK18 aiming down its iron sights](Docs/Media/mk18_ironsight_ads.png) |

| Green (friendly) and red (enemy) lasers | Overheated barrel after a long spray |
|---|---|
| ![Green and red weapon lasers](Docs/Media/lasers_green_vs_red.png) | ![Barrel smoke after sustained fire](Docs/Media/mk18_overheat_smoke.png) |

| Foundry: enemy by the burning trench | Saltworks: storm and fire |
|---|---|
| ![Enemy with a red laser near a fire in Foundry](Docs/Media/foundry_trench_01.png) | ![Saltworks with fog, trees and fire](Docs/Media/saltworks_fire_02.png) |

| Deadsector: fog in the streets | Blackline at dusk |
|---|---|
| ![Deadsector street in ground fog](Docs/Media/deadsector_fog_01.png) | ![Blackline firing lane with fire](Docs/Media/testrange_fire_02.png) |

| Killcam: the killer's own sights | Killcam: the replay ends on your ragdoll |
|---|---|
| ![Killcam replay through the killer's iron sights](Docs/Media/killcam_ads_replay.png) | ![Killcam end with the victim's ragdoll on the floor](Docs/Media/killcam_victim_down.png) |

| Deploy screen | Graphics Settings (`11.22.26.1`) |
|---|---|
| ![Deploy screen with loadout, map and bot team mode](Docs/Media/menu_deploy.png) | ![Graphics & HDRP upscaler settings page](Docs/Media/settings_graphics_v2.png) |

### Dual-Tab Lobby & `11.24.26.1` 20 Hz Bot Research (`HumanLike` vs `LlmEnhanced` vs `DumbRobotic`)

| `SKIRMISH` Tab (Casual Play + Type Steppers) | `EXPERIMENTS` Tab (N-Match Designer + `AI VS AI`) |
|---|---|
| ![Skirmish Lobby Tab](Docs/Media/lobby_skirmish_tab.png) | ![Experiments Lobby Tab](Docs/Media/lobby_experiments_tab.png) |

| 20 Hz Spatial Occupancy Heatmaps | Composite Human-Likeness Index (`HLI`) Scorecard |
|---|---|
| ![Movement Heatmaps](Docs/Media/experiments/heatmap_movement_comparison.png) | ![HLI Scorecard](Docs/Media/experiments/human_likeness_scorecard.png) |

### How the bots behave

| Armour break and resupply | Flank a camper |
|---|---|
| ![Bot armour breaks, it hunts, then walks to an ammo station](Docs/Media/bot/lab-6-armour-break-and-resupply.gif) | ![Bot leaves the lane and flanks a player who camps](Docs/Media/bot/lab-4-flank-a-camper.gif) |

Eight scenarios run on a compact copy of the game's bot brain in the interactive **[LR2 Bot Lab](Docs/Bot/bot-lab.html)**
(also a [Claude artifact](https://claude.ai/artifact/Mj3oVBaf6NpdgRJSsxicPo)). Since 11-18 it also holds the former Bot Brain page, in three parts: the bot
(detection and sightlines, the priority waterfall, pathfinding, Fitts flicks and the human miss pattern, strafe tracking,
recoil, armour and resupply, presets and adaptive skill, the LLM hook), game modes (rotating CTF, chat orders) and how it
was built (agent ledgers, protocol results, research specs). All eight scenarios as GIFs:
[Bot explainer](Docs/LR2_Bot_Explainer.md#bot-lab-scenarios).

---

## Maps

| Map | Description | Preview |
|---|---|---|
| **Saltworks — Reclaimed Harbour** (`LR2_Saltworks`) | Coastal salt-flat industrial harbour with shipping containers, flooded sightlines, and sniper lanes. | ![Saltworks Preview](Assets/LR2/Resources/LR2/MapPreviews/Preview_LR2_Saltworks.png) |
| **Deadsector — War-Torn City** (`LR2_Deadsector`) | Rubble-choked urban quarantine sector tuned for aggressive flanking, street fights, and interior breaches. | ![Deadsector Preview](Assets/LR2/Resources/LR2/MapPreviews/Preview_LR2_Deadsector.png) |
| **Foundry — Pipe Refinery** (`LR2_Foundry`) | Multi-level chemical pipe refinery featuring steel catwalks, heavy cover, and tight chokepoints. | ![Foundry Preview](Assets/LR2/Resources/LR2/MapPreviews/Preview_LR2_Foundry.png) |
| **Blackline — Proving Grounds** (`LR2_TestRange`) | Open tactical proving ground with clean symmetrical cover for recoil drills and bot scrimmages. | ![Test Range Preview](Assets/LR2/Resources/LR2/MapPreviews/Preview_LR2_TestRange.png) |

---

## Features

- **OG Kinemation Full-Body Rig & Mechanics (`Assets/LR2/Runtime/Characters/`):** Kinemation's OG Generic robot rig (`FPSGenericPlayer`, `LR2ContentBuilder.Robot.cs` — zero grip/finger drift `0.000m`, crisp `AA_LR2_GrenadeThrow`, upright `LR2_Torch`, left-handed `Q/E` lean, rest-pose respawn restoration) alongside `RetargetProxy.cs`, `ReloadMotionBridge.cs`, `SoldierMovement.cs`, and `BodycamRig.cs`. Includes a research spec for 1-flag BF6-style soldier visuals ([`B136g_BF6_Soldier_Candidates.md`](Docs/AI_Collaboration/specs/B136g_BF6_Soldier_Candidates.md)).
- **Weapons, Circular PiP Sights, Overheat & Audio (`Assets/LR2/Runtime/Weapons/`, `Combat/`, `Audio/`):** **MK18**, **AK-12**, **AK-74N**, **FAL**, and suppressed **MK23** with circular clipped HDRP `LR2/PipScope` optics (`M_Acog`, `M_1P29`, `M_LongRangeScope`), suppressed fire & indoor/outdoor tails (`FAL -> FAL`), 23 silence-trimmed reload clips synced to `.anim` curves (`emptyReloadEndAt`), physical-impact `PainVoice` death sub-drop (`LR2_PlayerDeathImpact`), `HealthReplenished` chime, distinct self vs target armor-break SFX (`LR2_HF_SelfArmorBreak` / `LR2_HF_TargetArmorBreak`), 19-stem per-map adaptive music (`Overdrive` climax & Win/Lose stingers), frame-0 visible team tracers, and top-of-weapon `GunBloodFX`.
- **Tactile Combat Feedback, UI & Killcams:** 100 HP + 50 armour layer (`Health.cs`), Battlefield-inspired hit/headshot/armour/kill markers (`HitFeedbackHUD.cs`), unified opaque obsidian loading screen (`LR2LoadingIntro.cs`), `UITheme`-styled `"KILLCAM"` replay overlays (`KillcamDirector.cs`), non-overlapping bottom-right weapon/ammo HUD (`HUDController.cs`), and 5-tab Settings (`GAMEPLAY`, `GRAPHICS`, `CONTROLS` w/ `ToggleAim`/`ToggleSprint`/`AutoSprint`, `KEYBINDS` w/ `Slot1`/`Slot2`, `AUDIO`).
- **Game Modes & Persistent Custom Rules (`Assets/LR2/Runtime/Game/`):** **Duel (1v1)**, **Free For All**, **Team Deathmatch**, and **Rotating Capture the Flag** (`FlagZoneController.cs` BF6-inspired proximity-weighted capture speed across 5 `BotTeamAlignment` modes including **Half & Half 50/50 split**, Squads, FFA, Pack, and Co-op) plus live **Tactical Bot Chat** (`[T]` keyword directives & delayed banter in `TacticalChatSystem.cs`). Horizontal FOV slider (`65–120`), Master/Effects/Music/UI audio sliders, `< VALUE >` stepper controls for graphics quality, **HDRP 17.5 multi-upscaler backend (`NATIVE`, `STP`, `TAAU`, `FSR 1.0`, `DLSS`, `FSR 2` in `UpscalerBackend.cs`)**, anti-aliasing (`None`, `FXAA`, `SMAA`, `TAA`), frame rate limit, bot difficulty, live keybind rebinding (`LR2Bindings.cs` -> `PlayerInputSource.cs`), and FPS counter persist via `PlayerPrefs` (`LR2Settings.cs`, `SettingsPanel.cs`, `MainMenuController.cs`).
- **Four-Map Optimization & Automated Telemetry (`Assets/LR2/Runtime/World/PerformanceFrameTelemetry.cs`, `specs/P3_Report.md`):** Quality-aware shadow tiers, camera-distance light culling (`fadeDistance`/`shadowFadeDistance`), single-light burning barrels, time-sliced arena reflection probes, throttled `WeatherSystem` & `FoliageSway`, and spawn-to-spawn cover safety across all four maps bring steady 1080p frame times to `6.8–9.6 ms` avg (`15.7–19.1 ms` p99, `0` QA failures across all 5 scenes).
- **War-Torn Procedural Arenas (`Assets/LR2/Editor/LR2MapBuilder*.cs`, `Assets/LR2/Meshes/Procedural/`):** Leartes/ScansFactory pack props preserving tactical cover heights (`B75`), tileable `_DetailMap` wear, HDRP `SSAO` + `ContactShadows`, per-map `ShadowsMidtonesHighlights` colour grading, ground-level `AtmosphereFX` fog, `FireHazard` burning barrels (`heat haze`), destructible thin-refraction `M_LR2_Glass_*` panes (`B30`), and tuned HDRP lighting across all four combat maps (`B1`/`B72`).
- **Dual-Tab Lobby (`SKIRMISH` | `EXPERIMENTS`) & Spectator Bench (`Assets/LR2/Runtime/UI/SkirmishLobby.cs`, `ExperimentsTabView.cs`, `Game/ExperimentSession.cs`, `SpectatorCamera.cs`):** The main menu **PLAY** tile opens a two-tab lobby: **`SKIRMISH`** configures quick 2/3/4-way, Duel, or FFA matches with up to 20 bots (`TYPE`: `HUMAN` / `DUMB` / `LLM` / `FITTED`, `DIFF`, `TEAM`), while **`EXPERIMENTS`** hosts **`AI VS AI`** plus customizable N-match experimental protocols (Blind Mode, Order Randomization, `20/30/60 Hz` telemetry, JSON presets in `Data/Presets/`, and post-match 0–100% Turing ratings). The menu background cuts to slow-motion shots per screen (`World/MenuCinematic.cs`).
- **`InvasionFramerate`-Modeled Zero-Stutter Telemetry & Equation Fitting (`ExperimentPaths.cs`, `ExperimentTelemetryRecorder.cs`, `Tools/analyze_experiment_session.py`):** Logs continuous `20–60 Hz` player + bot kinematics (`Ticks_*.csv`, `Events_*.csv`, `Flicks_*.csv`, `Recoil_*.csv`, `Summary_*.csv`, `Meta_*.json` with `frameDelivery` stats) into `<project|exe>/Data/Logs/<sessionId>_<runId>/` using pre-allocated buffers flushed off-thread behind `SAVING EXPERIMENT TELEMETRY`. `Tools/analyze_experiment_session.py` scores bots against humans on a **0–100% Human-Likeness Index (`HLI`)** + 5-fold C2ST Turing test and exports fitted equations to `Data/Profiles/human_behavior_fit.json` (`HumanBehaviorProfile.cs` -> `HumanFitted` clones).
- **LLM Integration Depths (`Assets/LR2/Runtime/AI/LLM/`):** Supports `Commander` [Macro], `TacticalMicro` (stance/peek/burst/utility + `>= 3.2 s` reactive interrupts), and `AvatarPilot` [60 Hz Action Chunks] across **both team squads and solo 1v1 Duel / FFA matches** via **SETTINGS -> GAMEPLAY** or CLI flags (`-lr2LlmTeams`, `-lr2LlmMode`). Fog-of-war safe state (`LLMCommander.BuildState`); Groq API key resolves safely from `-lr2GroqKeyFile`, `GROQ_API_KEY`, or persistent `groq_api_key.txt` and is never logged or committed.
- **Tactical Bot AI (`Assets/LR2/Runtime/AI/`):** Staggered line-of-sight & noise perception (`BotPerception.cs`), 4 Hz utility brain with cover retreat, disengaged-only `AmmoCrate` ammo/armour resupply (`B74b`), stealth/flank goals, hunting & squad following (`TacticalBotBrain.cs`), NavMesh locomotion with unstuck mantling, Fitts/ESTA/CSKnow/Aim Lab biomechanical aiming (`BotController.cs`), skill-matched **`DumbRobotic`** mechanical control bots (`B154`), **`HumanFitted`** data-driven clones (`B157`), Adaptive difficulty (`AdaptiveDifficultyTracker.cs`), and external commander hooks (`IBotDirectiveProvider`). Explore the interactive **[LLM Architecture, 9 Maneuvers & Heatmap Lab](Docs/Bot/llm-architecture.html)**, **[LR2 Bot Lab](Docs/Bot/bot-lab.html)**, or the **[Bot explainer with animated GIFs](Docs/LR2_Bot_Explainer.md)**.

---

## Controls

| Keyboard / Mouse | Gamepad | Action |
|---|---|---|
| `W` `A` `S` `D` | Left Stick | Move |
| `Mouse` | Right Stick | Look / Aim |
| `Left Click` | `RT` | Fire |
| `Right Click` | `LT` | Aim Down Sights (ADS) |
| `R` | `X` (West) | Reload |
| `Left Shift` | `L3` | Sprint |
| `Space` | `A` (South) | Jump / Mantle / Stand Up |
| `C` / `Left Ctrl` / `X` | `B` (East) | Crouch (`X` Hold Crouch / `XX` Slide) |
| `Z` | `D-Pad Down` | Toggle Prone |
| `Q` / `E` | — | Lean Left / Right |
| `1` / `2` / `3` / `4` / `Scroll` | `Y` (North) | Switch Loadout (Primary / Sidearm / Knife / Torch / Scroll Swap) |
| `G` | `RB` | Frag Grenade |
| `L` | — | Toggle Tactical Laser |
| `B` / `V` / `I` | — | Fire Mode / Cycle Optic (ADS) / Inspect (`1`/`2`/`3`) |
| `F` | — | Interact (Ammo Crates) |
| `Tab` | — | Scoreboard |
| `Esc` | — | Pause / Back |
| `T`, type, `Enter` | - | Squad orders by chat (team modes, no LLM needed): **flank left** / **flank right**, **hold**, **push** / **flag**, **regroup** / **on me**. Every teammate bot obeys for 12 s. |

*(Toggle Aim, Toggle Sprint, Auto Sprint Forward, and full keybind remapping live under **SETTINGS > CONTROLS** and **KEYBINDS**.)*

---

## Opening & Building in Unity

> **Note on Third-Party Assets:** This public repository excludes paid Unity Asset Store packs and non-redistributable local audio (see **[THIRD_PARTY_ASSETS.md](THIRD_PARTY_ASSETS.md)** and `.gitignore`).

1. Open the project in **Unity `6000.5.0f1` (HDRP)**.
2. Import the required Asset Store packages listed in [THIRD_PARTY_ASSETS.md](THIRD_PARTY_ASSETS.md) into their expected `Assets/` folders so GUID references resolve.
3. Run **LR2 > Build Everything** (`LR2BuildPipeline.BuildAllBatch`) to generate derived textures/masks (woodland/desert uniform dyes, skull mask textures, packed HDRP masks), weapons, character prefabs, UI prefabs, NavMeshes, minimaps, and all 5 scenes (`LR2_MainMenu`, `LR2_Saltworks`, `LR2_Deadsector`, `LR2_Foundry`, `LR2_TestRange`).
4. Open `Assets/LR2/Scenes/LR2_MainMenu.unity` and press **Play**.

---

## Project Structure

```text
Assets/LR2/
├── Data/           # Weapon definitions, audio profiles, bot difficulties, HDRP profiles, UI presets
├── Editor/         # Procedural content, UI, map, NavMesh, preview capture, and smoke-test builders
├── FX/             # Blood/decal/shell/tracer materials and prefabs
├── Materials/      # Character, weapon, and environment materials
├── Meshes/         # Procedurally deformed war-torn containers, walls, masonry, and rubble (.asset)
├── Prefabs/        # Operator/bot prefabs, weapon prefabs, UI prefabs
├── Resources/LR2/  # Runtime-loaded MapPreviews sprites and themes
├── Runtime/
│   ├── AI/         # BotPerception, TacticalBotBrain, BotController, HumanBehaviorProfile, SeekerDrone, BotTypes, LLM/
│   ├── Audio/      # LR2Audio spatial/layered mixer & ambush/reverb ambience
│   ├── Characters/ # SoldierController, SoldierMovement, RetargetProxy, GenericBoneMirror, ReloadMotionBridge, BodycamRig
│   ├── Combat/     # Ballistics, Hitbox, Health, DestructibleProp
│   ├── Core/       # GameEvents, LR2Settings, LR2Layers
│   ├── FX/         # GunBloodFX, MuzzleFlash, Tracer, ShellEjector
│   ├── Game/       # MatchManager, MatchSettings (LR2Session), ExperimentSession, ExperimentPaths, GameModeRules
│   ├── UI/         # MainMenuController, SkirmishLobby, ExperimentsTabView, SettingsPanel, HUDController, HitFeedbackHUD, CombatPostFX
│   ├── Weapons/    # LR2Firearm, WeaponDefinition, LR2Item
│   └── World/      # KillcamDirector, ExperimentTelemetryRecorder, MediaDirector, WeatherSystem, FoliageSway, PlaytestRobot
└── Scenes/         # LR2_MainMenu + Saltworks, Deadsector, Foundry, TestRange + baked NavMeshes
Data/               # External runtime telemetry & fitted profiles (Presets/, Profiles/, git-ignored Logs/ + SessionState.csv)
Docs/
├── AI_Collaboration/  # How the AI agents build the game: story, prompts, protocol, research log, token rankings, LLM study
├── Bot/               # llm-architecture.html (9 maneuvers + 20 Hz heatmaps), bot-lab.html
├── Guides/            # LLM_BOT_IMPLEMENTATION.md
├── Media/             # README screenshots, lobby tabs, LLM charts & experiments/ 20 Hz heatmaps
├── Research/          # HUMAN_LIKENESS_EVALUATION.md & DATA_COLUMNS.md
├── Updates/           # Daily release notes (2026-11-19 .. 2026-11-24)
└── LR2_Bot_Explainer.md
Tools/
└── analyze_experiment_session.py  # Offline 20-60 Hz heatmap, 5-fold C2ST, HLI scorer & equation fitter
```
