# Lead Rush II — Experimental Data Layout & Telemetry Column Schema (`11.24.26.1`)

Modeled on the **InvasionFramerate** study logging architecture (`ExperimentPaths.cs`, `ExperimentSession.cs`, `ExperimentTelemetryRecorder.cs`), **Lead Rush II** writes all experimental presets, fitted behavior profiles, and zero-stutter match logs into a self-contained external `Data/` folder right beside the launched executable (or `<project root>/Data/` in the Unity Editor), rather than hiding files inside OS `AppData`.

> **Design Goal:** Zero mid-match GC allocations or synchronous disk flushes. Every gameplay tick sample and combat event is recorded into pre-allocated value-struct buffers (`32,768` ticks / `4,096` events / `1,024` flicks / `2,048` recoil shots / `16,384` frame times) and written on a background thread (`Task.Run`) at round end behind the full-screen `SAVING EXPERIMENT TELEMETRY` progress overlay.

---

## 1. External `Data/` Directory Hierarchy (`ExperimentPaths.cs`)

```text
<Lead Rush II.exe dir | Project root>/
└── Data/
    ├── SessionState.csv                        # Next participant/session integer ID + ISO timestamp (committed only when completed)
    ├── Presets/
    │   └── <session_slug>.json                 # Shareable JSON ExperimentSessionConfig presets exported from Experiments tab
    ├── Profiles/
    │   └── human_behavior_fit.json             # Offline fitted human parameters loaded at runtime by BotArchetype.HumanFitted
    └── Logs/                                   # Git-ignored participant telemetry storage (or -lr2TelemetryOut dir)
        └── <sessionId>_<runId>/                # e.g. 1_Ab21/ (collision-safe 4-char Base58 runId; aborted runs never overwrite)
            ├── SessionInfo_<sessionId>_<runId>.csv   # Hardware, OS, display, input device, LR2Settings & config SHA-256
            ├── SessionLog_<sessionId>_<runId>.csv    # One summary row appended per completed match + frame delivery stats
            ├── Ratings_<sessionId>_<runId>.csv       # Optional 0–100 subjective Blind Turing ratings & notes
            └── match_01_<condition_tag>/             # e.g. match_01_player_h2_d0_l1_f0/
                ├── Ticks_<matchTag>.csv              # 20 / 30 / 60 Hz continuous kinematics, mouse/recoil deltas & gaze error
                ├── Events_<matchTag>.csv             # Discrete transitions (ACQUIRE, FIRST_SHOT_ON_TARGET, SHOT, HIT, KILL, RELOAD)
                ├── Flicks_<matchTag>.csv             # Segmented ballistic saccades & casual eye scans (time-to-peak, overshoot)
                ├── Recoil_<matchTag>.csv             # Per-shot burst spray compensation, yaw weave & post-flight hit outcome
                ├── Summary_<matchTag>.csv            # Per-participant K/D, accuracy, headshot %, strafe flips, duty ratios
                └── Meta_<matchTag>.json              # Schema v2 match metadata, arena bounds, counts & frameDelivery audit
```

### Collision & Abandonment Safety
1. **`SessionState.csv` Peek-First Semantics:** `ExperimentPaths.PeekNextSessionId()` reads the next integer ID without incrementing on launch; `AdvanceSessionId()` only commits `completedSessionId + 1` once every match in the queued experiment finishes cleanly. If a participant quits mid-session, `runId` (`4` unambiguous alphanumeric characters) prevents folder collisions.
2. **`UniqueFile` Deduplication:** Every CSV/JSON output passes through `ExperimentPaths.UniqueFile(dir, prefix, idTag, ext)`, automatically appending `_2`, `_3`, … if a file already exists so prior participant data is never truncated or overwritten.
3. **Self-Identifying Rows:** Every CSV carries `session_id`, `run_id`, `match_index`, and `condition_tag` (`{player|aivsai}_h{n}_d{n}_l{n}_f{n}`), letting `Tools/analyze_experiment_session.py` ingest mixed folders directly.

---

## 2. Session-Level Index & Audit Files

### `SessionState.csv`
| Column | Type | Description |
|---|---|---|
| `nextSessionId` | `int` | Next sequential integer session counter (starts at `1`) |
| `lastUpdatedIso` | `string` | Local ISO-8601 timestamp when last updated |

### `SessionInfo_<sessionId>_<runId>.csv`
Key-value hardware and configuration audit written once at session start:
- `session.sessionId`, `session.runId`, `session.writtenIso`
- `app.productName`, `app.version`, `app.unityVersion`, `app.platform`, `app.isEditor`, `app.buildGuid`
- `system.os`, `system.cpu`, `system.cpuCount`, `system.memoryMB`, `system.gpu`, `system.gpuApi`, `system.gpuMemoryMB`
- `display.width`, `display.height`, `display.fullScreenMode`, `display.refreshHz`, `display.dpi`, `quality.level`, `quality.vSyncCount`
- `input.mouse`, `input.keyboard`
- `lr2.playerName`, `lr2.mouseSensitivity`, `lr2.adsSensitivityMult`, `lr2.fov`, `lr2.upscalerMode`, `lr2.recordTelemetry`
- `config.sessionSha256`, `config.sessionBytes` (SHA-256 digest of `ExperimentSessionConfig` JSON for exact protocol verification)

### `SessionLog_<sessionId>_<runId>.csv`
One row appended atomically at the end of each match (`21` columns):
`session_id`, `run_id`, `match_index`, `condition_tag`, `label`, `map`, `mode`, `blind_mode`, `duration_s`, `alpha_score`, `bravo_score`, `human_kills`, `human_deaths`, `human_acc_pct`, `human_hs_pct`, `frame_count`, `avg_frame_ms`, `p95_frame_ms`, `p99_frame_ms`, `slow_frames_33ms`, `written_iso`

### `Ratings_<sessionId>_<runId>.csv`
Appended when **Prompt Human Rating** is enabled in the Experiments tab (`8` columns):
`session_id`, `run_id`, `queue_idx`, `match_idx`, `label`, `condition`, `rating_0_100`, `note`

---

## 3. Per-Match Telemetry Tables (`ExperimentTelemetryRecorder.cs`)

> **Sampling Cadence:** Selectable **`20 Hz`** (default), **`30 Hz`**, or **`60 Hz`** in the Experiments tab. Between ticks, `SoldierController` reports lossless per-frame voluntary look deltas (`raw_look_dx/dy`) vs. procedural recoil kick (`recoil_dx/dy`) into per-actor accumulators so nothing is lost if the match renders faster than the log cadence.

### A. `Ticks_*.csv` (`51` Columns)
| # | Column | Unit / Values | Meaning |
|---:|---|---|---|
| 1–4 | `session_id, run_id, match_index, condition_tag` | string/int | Session identity & condition descriptor (`player_h2_d0_l1_f0`) |
| 5 | `match_time` | `s` | Seconds elapsed since match start |
| 6–8 | `participant_id, display_name, is_human` | `int, str, 0\|1` | Combatant actor index, callsign/gamertag, `1` for local player |
| 9–11 | `archetype, difficulty, team` | enum, `0..4`, enum | `HumanLike`, `DumbRobotic`, `LlmEnhanced`, `HumanFitted` & tier (`0..4`) |
| 12 | `alive` | `0\|1` | Whether combatant is alive on this sample |
| 13–15 | `pos_x, pos_y, pos_z` | `m` | World position in active arena |
| 16–19 | `vel_x, vel_y, vel_z, speed` | `m/s` | Instantaneous world velocity components & horizontal/3D speed |
| 20–21 | `yaw, pitch` | `deg` | View orientation (camera pitch clamped negative = looking up) |
| 22–23 | `raw_look_dx, raw_look_dy` | `deg` | Summed voluntary mouse / bot-input rotation deltas since last tick (`+dy` = upward aim input, negative = downward pull) |
| 24–25 | `recoil_dx, recoil_dy` | `deg` | Summed weapon recoil kick injected into view orientation since last tick |
| 26–27 | `ang_vel_yaw, ang_vel_pitch` | `deg/s` | Instantaneous angular velocity of view orientation |
| 28–29 | `move_x, move_y` | `-1..+1` | Strafe (`move_x` `L/R`) and forward/back (`move_y`) locomotion inputs |
| 30–34 | `is_sprinting, is_crouching, is_proning, is_aiming, aim_blend` | `0\|1`, `0..1` | Movement stance, ADS active bit, and continuous ADS blend weight |
| 35–38 | `lean, is_firing, is_reloading, weapon` | `deg/unit`, `0\|1`, str | Procedural lean, trigger state, reload active state, equipped gun ID |
| 39–41 | `ammo_mag, hp, armor` | int, pts, pts | Current magazine rounds, health, and body armour |
| 42 | `has_visible_enemy` | `0\|1` | `1` if any hostile has unobstructed line-of-sight inside torso/eye cone |
| 43 | `nearest_enemy_dist` | `m` | Euclidean distance to nearest visible (or tracked) hostile |
| 44–46 | `err_yaw_deg, err_pitch_deg, err_total_deg` | `deg` | Crosshair angular offset from nearest visible enemy head/chest (`err_yaw > 0` right of target, `err_pitch > 0` above target; corrected 11-26, the code has always used this sign) |
| 47 | `enemy_vel_lat` | `m/s` | Signed lateral screen-plane speed of target relative to shooter gaze |
| 48 | `near_cover_dist` | `m` | Distance to nearest tagged `CoverPoint` (`2.5 m` cover-proximity threshold) |
| 49–50 | `bot_goal, bot_order` | enum | `TacticalBotBrain` active goal (`Engage`, `SeekCover`, `Flank`, `Resupply`, …) and active LLM `BotOrder` |
| 51 | `is_flicking` | `0\|1` | `1` while ballistic saccade detector is inside an active flick |

### B. `Events_*.csv` (`19` Columns)
`session_id, run_id, match_index, condition_tag, match_time, event_type, actor_id, target_id, pos_x, pos_y, pos_z, yaw, pitch, dist_m, err_deg, damage, zone, weapon, extra`
- **Core `event_type` values:** `TARGET_ACQUIRED` (new visible enemy locks timestamp), `FIRST_SHOT_ON_TARGET` (`err_deg` stores reaction latency in seconds `dt` from `TARGET_ACQUIRED`), `SHOT`, `HIT`, `KILL`, `DEATH`, `RELOAD_START`, `RELOAD_END`.

### C. `Flicks_*.csv` (`20` Columns)
`session_id, run_id, match_index, condition_tag, actor_id, is_human, archetype, difficulty, start_t, end_t, duration_s, start_ang_deg, peak_ang_vel, time_to_peak_frac, path_len_deg, straightness_ratio, overshoot_deg, final_err_deg, reaction_latency_s, is_combat`
- **`time_to_peak_frac`:** Fraction $\in [0, 1]$ of total flick duration at which `peak_ang_vel` occurred (`~0.34` skewed human `Beta(2.0, 3.3)` vs. symmetric `0.50` linear/PID vs. constant slew).
- **`straightness_ratio`:** Chord angle `start_ang_deg / path_len_deg` quantifying curved wrist-bow vs. straight robotic interpolation.

### D. `Recoil_*.csv` (`20` Columns)
`session_id, run_id, match_index, condition_tag, actor_id, is_human, archetype, difficulty, burst_id, shot_idx, weapon, is_ads, dt_since_burst_start, recoil_kick_pitch, raw_pulldown_pitch, net_pitch_drift, yaw_weave, err_yaw_deg, err_pitch_deg, hit_target`
- **`raw_pulldown_pitch`:** Positive (`+deg`) values denote downward mouse compensation opposing upward weapon kick (`ExperimentTelemetryRecorder` accumulates downward mouse delta during the inter-shot interval; `analyze_experiment_session.py` also chronologically cross-matches `SHOT -> HIT` up to `+0.25 s` so longer-range ballistic flight times register `hit_target=1`).

### E. `Summary_*.csv` (`26` Columns)
`session_id, run_id, match_index, condition_tag, participant_id, display_name, is_human, archetype, difficulty, team, kills, deaths, assists, score, kd, shots, hits, acc_pct, headshots, hs_pct, avg_flick_s, avg_react_s, strafe_flips, crouch_duty, ads_duty, sprint_duty`

### F. `Meta_*.json` (`schemaVersion: 2`)
Records match identity (`sessionId, runId, matchIndex, conditionTag, scene, mode, label, llmMode, blind, durationSeconds, sampleHz`), active arena spatial bounds (`bounds: {minX, maxX, minZ, maxZ}`), exact row counts (`counts: {ticks, events, flicks, recoilShots}`), and `InvasionFramerate`-style `frameDelivery`:
```json
"frameDelivery": {
  "frameCount": 6414,
  "avgFrameMs": 11.70,
  "p95FrameMs": 14.09,
  "p99FrameMs": 17.69,
  "slowFrames33ms": 18
}
```

---

## 4. Command-Line Telemetry & Bench Flags

| CLI Argument | Scope | Effect |
|---|---|---|
| `-lr2RecordTelemetry` | Player / Editor | Forces `ExperimentTelemetryRecorder` active even outside the Experiments tab |
| `-lr2TelemetryOut <dir>` | Bench CLI | Redirects match CSV/JSON output to `<dir>` (also updates `<parent(dir)>/SessionLog_cli.csv`) |
| `-lr2AimBench -lr2BenchSeconds 75` | `MediaDirector` | Launches AI-vs-AI benchmark run for fixed seconds before flushing telemetry and quitting (`-lr2QuitOnDone`) |
| `-lr2BenchArchetypes Alpha=DumbRobotic,Bravo=HumanLike` | `MediaDirector` | Assigns specific `BotArchetype` values (`HumanLike`, `DumbRobotic`, `LlmEnhanced`, `HumanFitted`) per team |
| `-lr2AlphaDiff <0..3>` / `-lr2BravoDiff <0..3>` | `MediaDirector` | Sets bot difficulty (`0=Recruit`, `1=Regular`, `2=Hardened`, `3=Veteran`) per team |
| `-lr2LlmTeams Alpha` `-lr2LlmMode Commander` | `LLMCommanderConfig` | Enables live Groq planning on designated teams |
