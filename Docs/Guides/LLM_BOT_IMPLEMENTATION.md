# How the LLM Bot Was Implemented — Lead Rush II Architecture Guide

This tutorial walks through every C# layer that connects the **Groq Chat Completions API** to **Lead Rush II**'s utility AI, combat micro-behaviors, 60 Hz locomotion/aim controller, Settings UI, and repeatable Windows benchmark harness.

```mermaid
flowchart TB
    subgraph UI["Settings & Launch Overrides"]
        S["LR2Settings (LR2.v1.LlmMode / Teams / Interval / Model)"]
        UI["SettingsPanel + LR2UIBuilder (Tab 0: GAMEPLAY)"]
        CLI["CLI Flags (-lr2LlmMode, -lr2LlmTeams, -lr2LlmInterval)"]
        S --> CFG
        UI --> S
        CLI --> CFG["LLMCommanderConfig"]
    end

    subgraph System2["System 2 — Asynchronous Groq Loop (3–60 s + Interrupts)"]
        HOST["LLMCommanderHost (RuntimeInitializeOnLoadMethod)"]
        CMD["LLMCommander.Loop() + BuildState()"]
        GC["GroqClient.Chat() (UnityWebRequest + Key Fallback)"]
        JSON["Order / Micro / Action-Chunk Parser"]
        HOST --> CMD --> GC --> JSON
    end

    subgraph System1["System 1 — 60 Hz Deterministic Engine"]
        BR["TacticalBotBrain.Think() (Step 3 Directive & Interrupts)"]
        BC["BotController (NavMeshAgent + TryGetActiveMicroStep)"]
        KIN["Kinemation FPSAnimator + GenericBoneMirror"]
        JSON -->|"BotDirective + microQueue"| BR --> BC --> KIN
        BC -.->|"BotBlackboard snapshots"| CMD
    end
```

---

## 1. Key Security & Non-Blocking Transport (`GroqClient.cs`)

`Assets/LR2/Runtime/AI/LLM/GroqClient.cs` handles key resolution, rate-limit backoff, and HTTP transport using Unity's `UnityWebRequest`:

1. **Multi-location key lookup (`GroqClient.LoadKey`):**
   - Never hardcodes or logs keys and is excluded from git via `.gitignore`.
   - Resolution order:
     1. `-lr2GroqKeyFile <path>` CLI argument
     2. `GROQ_API_KEY` process environment variable
     3. `Application.persistentDataPath/groq_api_key.txt` (plus case-tolerant `groq_api_key*.txt` fallback and legacy `LeadRush 2` persistent folder so existing user keys keep working across the Lead Rush II rename)
2. **Global concurrency & rate-limit guard (`CanSendNow`):**
   - Serializes requests (`Busy` flag) with `MinGapSeconds` (`2.5f` s minimum spacing) so multi-team LLM matches never fire simultaneous bursts.
   - Parses HTTP `429` `retry-after` headers into `_blockedUntil` (`Mathf.Clamp(ra, 2f, 120f)`), and backs off `6f` s on `5xx` or transport failures.
3. **Structured JSON request (`GroqClient.Chat`):**
   - Posts to `https://api.groq.com/openai/v1/chat/completions` with `response_format: {"type":"json_object"}` (when `jsonMode == true`), `max_tokens: 700`, `temperature: 0.4`, and `req.timeout = 15`.
   - Captures exact server telemetry on every call (`status`, `seconds`, `promptTokens`, `completionTokens`, `x-ratelimit-remaining-requests`, `x-ratelimit-remaining-tokens`, and `modelRejected` on HTTP 400/404 model rejections).

---

## 2. Three Selectable LLM Integration Depths (`BotTypes.cs`, `LLMCommander.cs`, `LR2Settings.cs`)

Instead of forcing one fixed prompt cadence, Lead Rush II exposes **four modes** (`LlmIntegrationMode`: `Off = 0`, `Commander = 1`, `TacticalMicro = 2`, `AvatarPilot = 3`):

| Mode | C# Enum | Typical Cadence | Model Chain Priority | What the LLM Controls | What System 1 Controls (60 Hz) |
|---|---|---|---|---|---|
| **0 · Off** | `LlmIntegrationMode.Off` | — | — | None (pure C# utility rules + Fitts/Balabit human motor kinematics) | Everything |
| **1 · Commander** | `LlmIntegrationMode.Commander` | `6–20 s` (default `8 s` CLI / `12 s` UI) | `DefaultModels` (`openai/gpt-oss-20b` → `llama-3.1-8b-instant` → `llama-3.3-70b-versatile`) | Squad `plan` + macro `order` (`hold`, `move`, `attack`, `flank`, `retreat`, `regroup`, `free`) + `(x, z)` + `target` | Stance, peek style, burst fire, frag/reload utility, strafing, aiming, cover |
| **2 · Tactical Micro** | `LlmIntegrationMode.TacticalMicro` | `<= 8 s` + reactive interrupts (`>= 3.2 s` cooldown) | `DefaultModels` | Macro orders **plus** per-bot combat posture: `stance` (`stand\|crouch\|prone`), `peek` (`hold\|jiggle\|wideswing`), `burst` (`tap\|burst\|fullauto`), `utility` (`frag\|reload\|holdfire`), `aggression` (`-1..+1`) | Pathing, reactive emergency overrides, continuous ADS/aim tracking, shooting |
| **3 · Avatar Pilot** | `LlmIntegrationMode.AvatarPilot` | `<= 5.5 s` + reactive interrupts (`>= 3.2 s` cooldown) | `FastPilotModels` (`llama-3.1-8b-instant` first for sub-250 ms RTT) | Macro orders + micro posture **plus** 2–4 timed 60 Hz motor **Action Chunks** (`sprint_to`, `strafe_fire`, `crouch_peek`, `hold_angle`, `throw_frag`, `fallback_reload`, `prone_ambush`) with `dir`, `dur` (`0.5–4.5 s`), and `ads` | Real-time execution of active chunk + continuous Fitts/minimum-jerk aiming & recoil pull-down |

### Fair-Play State Serialization (`LLMCommander.BuildState`)
`BuildState()` builds a compact JSON snapshot (~450–680 prompt tokens) containing:
- Map name, mode, team score vs highest enemy score, match time remaining, NavMesh triangulation bounds, and active CTF `flagZone` `(x, z)` when playing Objective modes.
- Per-ally snapshot (`id`, `x`, `z`, `hp`, `armor`, `ammo` %, current `doing` goal, active `order`, visible target + distance; human players carry `"player":true` and are ignored as order targets).
- **Fair-play enemy fog-of-war:** Enemies are included ONLY if at least one ally currently sees them (`bb.targetVisible`) or saw them within `20 s` (`bb.TimeSinceSeen < 20f`), recording their last-known `(x, z)`, `seenAgo`, and spotter `by` ID. Zero omniscient wallhack coordinates are ever sent.

### Reactive Event Interrupts (`LLMCommander.CheckReactiveInterrupt`)
In `TacticalMicro` and `AvatarPilot` modes, instead of sitting idle until the fixed interval timer expires, the polling loop checks every `0.25 s` (after a `3.2 s` minimum cooldown and `GroqClient.CanSendNow` check) and wakes early if:
1. An ally was eliminated since the last prompt (`alive < _lastAliveAllies`), OR
2. New hostile line-of-sight contacts appeared (`vis > _lastVisibleContacts`), OR
3. Any squadmate dropped into critical danger under fire (`health01 < 0.35f && underFire`).

---

## 3. Bridging Orders into Utility AI & 60 Hz Motor Execution (`TacticalBotBrain.cs` & `BotController.cs`)

### Directive Data Contract (`BotTypes.cs`)
Every parsed order populates a `BotDirective` struct containing:
- `order` (`BotOrder`), `position`, `target`, `duration` (`Interval * 1.6f`), `rationale`
- Micro overrides (`BotStanceOrder`, `BotPeekStyle`, `BotBurstStyle`, `BotUtilityAction`, `aggression`)
- `microQueue` (`BotMicroAction[]` of up to 4 sequenced chunks, each with `BotMicroActionKind`, `targetPos`, `strafeDir`, `duration`, `ads`, stepped via `BotController`'s `_microIdx`, `_microStepUntil`, and `_microStepActionFired`).

### Utility Precedence & Interrupt Safety (`TacticalBotBrain.Think`)
Bots never freeze waiting on HTTP responses:
1. Survival (critical HP flee / medical retreat) still outranks stale macro move orders.
2. **Urgent CTF Contest (`Step 2.5`)**: Uncontested enemy flag capture inside 35 m takes priority.
3. **LLM Directive Stage (`Step 3` in `TacticalBotBrain.Think`)**:
   - When `bb.hasDirective && now < bb.directiveUntil && bb.directive.order != BotOrder.FreeRoam`, `Think()` propagates the LLM's `stance`, `peek`, `burst`, `utility`, and `aggression` onto `BotIntent`, advances timed `microQueue` movement when active, or yields smoothly into live `Engage()` if a close threat `< 14 m` opens fire.
4. **Emergency Expiry**: When `Time.time >= bb.directiveUntil` (if Groq stalls or drops), bots seamlessly revert to pure `TacticalBotBrain` C# utility rules on the very next tick.

### Action-Chunk Motor Step (`BotController.TryGetActiveMicroStep`)
Inside `BotController.Update()`, before building the per-frame `SoldierInput`:
- **`BotController.ApplyMovement` & combat firing control** translate `BotIntent` LLM posture fields into physical locomotion & weapon flags:
  - `BotStanceOrder`: forces crouch/prone states without overriding emergency obstacle hops.
  - `BotPeekStyle`: overrides combat strafe reversal cadence (`Jiggle` reverses briskly, `WideSwing` commits longer lateral pushes, `HoldAngle` locks steady aim).
  - `BotBurstStyle`: adjusts burst length & trigger reset cadence (`Tap` controlled shots at range, `Burst`, `FullAuto` close suppression).
  - `BotUtilityAction`: triggers one-shot `ThrowFrag`, `SafeReload`, or stealth `HoldFire` trigger discipline.
- **`TryGetActiveMicroStep(now, out step)`** sequences steps from `Blackboard.directive.microQueue` at full frame rate (`SprintTo`, `StrafeFire`, `CrouchPeek`, `HoldAngle`, `ThrowFrag`, `FallBackReload`, `ProneAmbush`) while `BotController`'s human Fitts/Balabit minimum-jerk aiming (`HumanPause` / saccades) and Harris-Wolpert noise model continue handling physical mouse deltas, recoil pull-down, and lagged velocity lead at 60+ Hz.

---

## 4. In-Game Settings & Headless/Windowed Bench Automation

### Player-Facing Settings (`LR2Settings.cs`, `SettingsPanel.cs`, `LR2UIBuilder.cs`)
Under **SETTINGS → GAMEPLAY**, players can live-configure LLM squads without touching CLI flags:
- **LLM INTEGRATION**: `OFF` / `COMMANDER [MACRO]` / `TACTICAL MICRO` / `AVATAR PILOT [CHUNKS]`
- **LLM TEAMS**: `NONE` / `ALPHA [BLUE]` / `BRAVO [RED]` / `BOTH [ALPHA+BRAVO]`
- **LLM TICK INTERVAL**: `4–30 s` continuous slider
- Live Key & Architecture Hint label confirming active mode, tick budget, and Groq key readiness.

### Automated AI-vs-AI Bench & Telemetry CLI (`MediaDirector.cs`)
To benchmark matchups reproducibly (`Docs/AI_Collaboration/LLM_BOT_STUDY.md`, `Docs/Research/HUMAN_LIKENESS_EVALUATION.md`), launch the standalone executable with:

```powershell
"H:\Unity Projects\Builds\Lead Rush II\Lead Rush II.exe" `
  -screen-fullscreen 0 -screen-width 1120 -screen-height 630 `
  -lr2AimBench -lr2BenchSeconds 75 -lr2BenchMode TDM `
  -lr2BenchArchetypes Alpha=LlmEnhanced,Bravo=HumanLike `
  -lr2LlmTeams Alpha -lr2LlmMode Commander `
  -lr2AlphaDiff 1 -lr2BravoDiff 1 `
  -lr2TelemetryOut "Logs\LR2_Experiments\cal_session\match_03_llm_reg" -lr2QuitOnDone `
  -lr2OutDir "Logs\LR2_Experiments\cal_llm_reg"
```

- **Four Selectable Bot Archetypes (`BotArchetype`, `B154`/`B157`):**
  - `HumanLike`: Procedural Fitts's Law + Balabit `Beta(2.0, 3.3)` kinematics + ESTA `125 ms` velocity lag + tactical cover/flanks.
  - `DumbRobotic`: Skill-calibrated linear-slew mechanical baseline (matches `HumanLike` K/D while stripping human movement/flick cues).
  - `LlmEnhanced`: Groq Cloud planning (**now also active in 1v1 Duel & FFA** via per-bot/team `ExperimentModeOverride` in `LLMCommander.cs`).
  - `HumanFitted`: Data-driven clone loading `Data/Profiles/human_behavior_fit.json` via `HumanBehaviorProfileLoader` and scaling timers across tiers (`0.72× / 0.90× / 1.00× / 1.15×`).
- `-lr2AlphaDiff` / `-lr2BravoDiff`: `0` (`Recruit`), `1` (`Regular`), `2` (`Hardened`), `3` (`Veteran`), or `mixed` (1 of each tier per 4-bot team).
- Outputs written to `-lr2OutDir` (`teams.csv`, `bots.csv`, `bench.csv`, `llm_<Team>_<timestamp>.jsonl`) and `-lr2TelemetryOut` (`Ticks_*.csv`, `Events_*.csv`, `Flicks_*.csv`, `Recoil_*.csv`, `Summary_*.csv`, `Meta_*.json`).
