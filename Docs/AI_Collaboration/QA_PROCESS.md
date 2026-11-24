# How the AI agents test Lead Rush II

The agents cannot watch a screen the way a person does. They test the game by **running it and reading what it writes**:
numbers in a report, log lines, CSV files and screenshots. This page explains every tool, walks through one full test cycle,
shows how the results are read, and compares the testing methods by what they actually found.

All numbers here come from the project's own run artifacts (`Logs/`, which is not in git) through
[`analysis/qa_analysis.py`](analysis/qa_analysis.py). The extracted data is committed: [`data/qa_pose_runs.csv`](data/qa_pose_runs.csv),
[`data/qa_bot_latest.csv`](data/qa_bot_latest.csv), [`data/qa_defects_by_method.csv`](data/qa_defects_by_method.csv) and the generated
tables in [`figures/qa_summary.md`](figures/qa_summary.md).

## Ground rules

- **Visible runs only.** Every test opens a normal, visible Unity editor window. Hidden `-batchmode` playtests are banned since
  they played game audio in the background while the developer worked (defect D16). Builds may run in batch mode.
- **One editor per project.** Before any run an agent writes its claim on the board's `UNITY:` line and checks the process list.
  A parallel worker (`Tools/LR2ParallelWorker.ps1`) is a second project that shares the `Assets` folder through junctions, so it
  can build or run QA while the main editor is busy; it still counts in the lock check.
- **Compile first.** `dotnet build LR2.Runtime.csproj` and `LR2.Editor.csproj` must pass before a script is saved, before Unity
  starts and before a push (protocol rule 2). Test runs also freeze Unity's auto-refresh so another agent's save cannot recompile
  the game in the middle of a run.
- **Check the check.** A new check counts only after it has been shown to fail on a known-bad case (rule 1).

## The tools

| Tool | Start | What it does | What it writes |
|---|---|---|---|
| Compile gate | `dotnet build LR2.Runtime.csproj` and `LR2.Editor.csproj` | Compiles all game and editor code outside Unity in seconds | errors and warnings |
| Build everything | `-executeMethod LR2.EditorTools.LR2BuildPipeline.BuildAllBatch` | Regenerates prefabs, materials, maps and UI from the builder scripts (the source of truth) | `Logs/build_all_*.log`, ends with `BUILD EVERYTHING: SUCCESS` |
| **Pose check** | `-executeMethod LR2.EditorTools.LR2SmokeTest.RunBatch -lr2PoseCheck [-lr2Weapon mk18]` | Takes over the player's input, freezes the other soldiers and plays a fixed script: idle, look up and down, aim down sights, an A/B reload comparison, tactical and empty reloads, pistol swap, walking and sprinting with foot-slide probes, reloads while walking and sprinting, crouch, prone, jump, an enemy close-up, then a bot kills the player and the killcam replay is measured | `report.txt` (lines such as `GAIT`, `RELOAD-ANGLES`, `RELOAD-END-JUMP`, `FINGERS`, `KILLCAM`), `motion_baseline.csv`, about 40 screenshots: `fp_*` first person, `tp_*` from fixed third-person cameras with a red marker on the eye, `kc_*` killcam frames |
| **Bot aim test** | the same with `-lr2AimTest` | One hostile bot runs its full AI 15 m away; the player stands still 12 s, then strafes 12 s; every bot shot is intersected with the plane through the player and matched to the real hit | `bot_aim_shots.csv`, `AIM-TEST` lines, two screenshots |
| **Death and wall test** | the pose check with `-lr2DeathTest [-lr2DeathCases prone,sprinting]` | Kills the player in six states (standing, ADS, crouched, sprinting, reloading, prone) and logs the gun pose before death, 0.1 to 3 s after the respawn, in ADS after the respawn and after one shot; then walks and sprints into the nearest wall and backs off. Every line carries the driver Animator state and the curves Kinemation reads (`fb`, `fbEff`, `prw`, `prEff`) | `POSE` and `ADS-ALIGN` lines (gun angle and roll against the camera, gun position in camera cm, hand IK error), `death_*` and `wall_*` frames |
| Playtest robot | `-executeMethod LR2.EditorTools.LR2SmokeTest.RunBatch -lr2Robot -lr2Scene <map> -lr2Duration N` | The game plays itself: moves, aims, shoots and reloads for N seconds | screenshots every 4 s plus reload frames, a report with match state, kills, shots and runtime errors |
| **QA bot** | `-executeMethod LR2.EditorTools.LR2QABot.RunBatch` | Per map, a list of PASS/FAIL checks: visibility floor, enemy readability, ammo stations placed, fog and fire present, glass materials, aim reset on death, weapon safety at close range, music gain under gunfire, the bot resupply rule, audio tail rollback, reload invariants, runtime errors; plus *known-bad fixtures* that prove a check can fail | `Logs/LR2_QABot/qa_<map>.txt` and a `*_vis.png` per map |
| README media runner | `-executeMethod LR2.EditorTools.LR2ReadmeMediaRunner.CaptureReadmeMediaBatch` | Plays scripted shots (menu, deploy, aiming, lasers, fog and fire per map) and saves them | `Docs/Media/*.png` |
| Web page tests | Electron (VS Code's runtime) with a stub page; headless Microsoft Edge | Runs the Bot Lab page's simulation for eight scenarios without a browser and checks it for errors; captures its GIFs | console log, `Docs/Media/bot/lab-*.gif` |

## One full cycle, step by step: the killcam fix (2026-11-18)

1. **The complaint.** The developer: the killcam "should look exactly like the player's view". Two code reviews had not found why.
2. **A check that can see it.** Claude added a killcam stage to the pose check: a scripted bot aims down sights and kills the
   player; the harness photographs eight moments of the replay and logs the camera position, field of view, which of the killer's
   renderers are hidden and where the gun sits in the frame.
3. **Claim and run.** `UNITY: Claude 09:18 visible pose check C9 ...`, compile both assemblies, launch the visible editor. About
   nine minutes later the editor quits by itself and leaves `report.txt` and 40 PNGs.
4. **Numbers first.** `KILLCAM-EYE driverCam=(23.89, 1.48, 7.77) visualEye=(23.89, 1.58, 7.79) |d|=0.104 m`: the replay camera sat
   10 cm above the eye socket the sights are aligned to.
5. **Then the pictures.** Twenty-odd screenshots go into one contact sheet so a single look covers the whole run. The frames showed
   a hip-fire view instead of the sight picture, the dead player's red screen grade over the replay, and at the end the victim
   gone: the scripted collapse had pushed the body 0.82 m into the floor.
6. **Fix and re-run.** Run C10, same script. Camera 1.49 to 1.53 m (was 1.59 to 1.62), killer head hidden like the live player,
   true sight picture, the victim's real ragdoll on the floor at the end.
7. **Record it.** Five defects (D45 to D48 plus D49 found in C10) go into `data/bugs.csv` with who caused them, the board rows move
   to READY FOR REVIEW with the evidence, and another agent reviews them.

The same loop handled the developer's 14:10 report ("weapon going sideways after dying, losing its IK", "MK18 sideways in
ADS", "hitting walls when the gun gets back, everything starts to break"). The death test (C16 to C24) showed a prone death
leaving the gun 30 degrees off at the hip and 60 degrees in ADS for the whole next life, a sprint death keeping the sprint
look weights, and walking or pushing into a wall handing the rifle to the leg animation (`fb=0.88`). The `fbEff`/`prEff`
columns then showed the gated values at 0 while the raw curves stayed stale, which is the proof the fix works for the right
reason. Against the wall the fix first had no effect (74 degrees, C23); the unchanged number pointed at a different cause, a
secondary pose Kinemation applies regardless of its `useSecondaryPose` flag (fixed in C24: a 23 degree tuck).

The same loop measured the developer's "the gun jumps at the end of the reload": a new `RELOAD-END-JUMP` line reports the largest
change of the gun's position relative to the camera from one frame to the next around the end of a reload. It read 17.2 degrees in
one frame while walking (C9), which located the cause; after the fix it read 5.3 (C10).

## Reading the results

- **Numbers first.** Report lines are built to be compared between runs (same script, same order, same units).
- **Pictures second.** A contact sheet of every frame is one image to look at; problems a number does not describe (a translucent
  overlay, a body under the floor, a hand off the grip) show up here.
- **Logs third.** The Unity log shows exceptions, compile errors and the final `Runtime errors/exceptions: N` count.
- **Compare against the last run.** A fix counts when the same measurement moves the right way, not when the code looks right.

### When the check itself was wrong

Probes are code too, and five times this project's checks measured the wrong thing. Each was caught only because a result
looked impossible next to the screenshots or another number:

| Defect | The check | What was really wrong |
|---|---|---|
| D17 | QA "reload, walking, no flip" passed | the rifle stood vertical on screen; the check read the wrong angle |
| D22 | QA "hands on the gun" passed | it read the IK solver's error, not the distance between hand and grip |
| D43 | "MK18 magazine never moves" (0.000 m) | the probe found a hidden duplicate magazine inside an inactive copy of the gun; the real one moves 0.43 m. Copilot's MK18 run at 02:02 still read 0.427 m, before that duplicate was added at 03:04 |
| D44 | reload A/B | it switched a setting for the comparison and never switched it back, so later numbers measured the wrong setting |
| aim test C11 to C14 | "the bot fired 0 shots", then "0 hits from 106 shots" | the bot's path agent stayed at its old position after the teleport; then the bot turned away before noticing the player; then hits arrived after the next shot (bullets are projectiles) and were matched to nothing, while the player's health showed 1,168 damage taken |

## Comparative analysis

### Which method finds which defects

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="figures/qa_methods_dark.png">
  <img alt="Bar chart: defects found by the developer playing, by agents reading code, by automated runtime checks and by the compile gate" src="figures/qa_methods_light.png">
</picture>

| Method | Defects found | Typical defects | Cost per use | Blind spots |
|---|---|---|---|---|
| Developer playing the game | 23 | feel and look: hand pose, field of view, darkness, music, killcam, reload jank | minutes of play, but only when the developer plays | cannot measure; reports arrive hours later |
| Agent reading code (review) | 14 | wrong API or property, missing trigger, stacking damage, unreleased audio slots | 1 to 5 minutes per diff | cannot see the running game or the picture |
| Automated runtime check | 10 | hidden duplicate parts, end-of-reload jump, killcam camera offset, victim under the floor, copy-time exceptions | 2 to 10 minutes per run | sees only scripted situations; its probes can be wrong (3 of the 10 were defects in the checks) |
| Compile and push gate | 4 | broken string literals, missing fields, an HDRP property that does not exist | seconds | logic and visuals |

The developer still finds the most defects, almost all of them about how the game *feels* or *looks*. Code review is the cheapest
and catches logic and API mistakes before they run. Runtime checks find a third kind: problems that only exist while the game runs
and are too small or too fast to notice, which they also turn into numbers a fix can be judged by. The compile gate is the only
method that never produced a false alarm, and it stopped every broken tree before a push.

### How one measurement moved across every pose check

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="figures/qa_footslide_dark.png">
  <img alt="Scatter over time: foot slide while walking in 22 pose checks, falling from 4.25 to 0.36 m/s" src="figures/qa_footslide_light.png">
</picture>

Foot sliding while walking at 3.5 m/s was measured in every pose check from the first one (11-17 14:01, 4.25 m/s) to the latest
(11-18 09:37, 0.36 m/s). The big drops came from measuring the real stride of the walk and sprint animations and matching the
playback speed to it (C8, C9). The full run-by-run table, with magazine travel, the reload end jump and the killcam camera height,
is in [`figures/qa_summary.md`](figures/qa_summary.md).

### The QA bot on every map (latest run)

| Map | PASS | FAIL | Known-bad fixtures that correctly fail on the broken case |
|---|---|---|---|
| Deadsector | 9 | 0 | - |
| Foundry | 9 | 0 | - |
| Main menu | 7 | 0 | - |
| Saltworks (full suite) | 57 | 0 | 5 |
| Test Range | 9 | 0 | - |

Saltworks runs the full list of checks (visibility, readability, spawn sightlines `MapFlowB46`, multi-upscaler switching, live keybind rebinding `LR2Bindings`, directional damage HUD, `Audio.TailReservationRollback`, `BotAI.DisengagedResupplyGate`, reloads, aim reset, and 5 known-bad fixtures); the other maps run visibility, spawn safety (`MapFlowB46`), performance telemetry, and runtime-error checks with `0` failures. In aim test `LR2_AimTest_C15`, the real `BotController` recorded `22/60` stationary hits (`37%`) and `8/45` strafing hits (`18%`, `54%` of misses behind motion).

### Who ran the tests

22 pose checks (724 screenshots), 5 playtest-robot runs, 5 aim tests (`C11–C15`) and 5-scene QA runs over two days. Claude ran most pose checks and the aim tests, Copilot ran the per-gun sweep (`AK-74N`, `MK18`, `FAL` at 02:00 to 02:04), Gemini ran the builds, multi-upscaler `P3` benchmarks, 5-scene QA, and README captures, and the developer played the builds. Codex reviewed code statically.

## What we learned

- **Measure the result, not the code.** Two code reviews missed the killcam's five problems; one run of a check that photographed
  the replay found all five.
- **Numbers for motion, pictures for looks.** The end-of-reload jump is invisible in a still and obvious in a per-frame number; a
  translucent overlay or a body under the floor is the other way round.
- **Probes need review like code.** Five of the checks were wrong at some point; each was caught by comparing against a picture or a
  second number.
- **The developer is the final judge of feel.** No check here can say whether a reload feels good. The checks make sure the developer
  only spends play time on what they cannot measure.
