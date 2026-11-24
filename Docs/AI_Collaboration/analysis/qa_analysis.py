"""QA comparative analysis for Lead Rush II (how the agents test the game, and which methods find what).

Reads the local QA artifacts (Logs/ is not in git, so this script turns them into committed data):
  Logs/LR2_PoseCheck*/report.txt   scripted first-person pose checks (FirstPersonPoseCheck via LR2SmokeTest.RunBatch -lr2PoseCheck)
  Logs/LR2_AimTest_*/report.txt    real bot AI shooting test (-lr2AimTest)
  Logs/LR2_Playtest*/report.txt    PlaytestRobot runs (-lr2Robot: the game plays itself, screenshots + report)
  Logs/LR2_QABot/qa_*.txt          QA bot per scene (LR2QABot.RunBatch: PASS/FAIL lines, known-bad fixtures)
  data/bugs.csv                    the defect ledger (who found each defect, at which stage)
Writes:
  data/qa_pose_runs.csv, data/qa_bot_latest.csv, data/qa_defects_by_method.csv
  figures/qa_footslide_{light,dark}.png, figures/qa_methods_{light,dark}.png, figures/qa_summary.md
Run: python Docs/AI_Collaboration/analysis/qa_analysis.py
"""
from pathlib import Path
import csv
import datetime as dt
import re

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

HERE = Path(__file__).resolve().parent
AI = HERE.parent
ROOT = AI.parents[1]
LOGS = ROOT / "Logs"
DATA, FIG = AI / "data", AI / "figures"

THEMES = {
    "light": dict(surface="#fcfcfb", ink="#0b0b0b", ink2="#52514e", muted="#898781", grid="#e1e0d9", base="#c3c2b7",
                  s1="#2a78d6", s2="#eb6834", s3="#1baf7a", other="#a3a19a", band="#eef5ea"),
    "dark": dict(surface="#1a1a19", ink="#f0efec", ink2="#c3c2b7", muted="#898781", grid="#2c2c2a", base="#383835",
                 s1="#3987e5", s2="#d95926", s3="#199e70", other="#6f6e69", band="#1f2a1f"),
}

# who ran each pose check (folder name, board log); "unattributed" where the log does not say
RUNNER = {"Claude": "Claude", "Copilot7": "Copilot", "AK74N": "Copilot", "Mk18": "Copilot", "FAL": "Copilot",
          "C7_mk18": "Claude", "C8_mk18": "Claude", "C9_mk18": "Claude", "C10_mk18": "Claude"}


def runner_of(name: str) -> str:
    key = name.replace("LR2_PoseCheck_", "").replace("LR2_PoseCheck", "first")
    for k, v in RUNNER.items():
        if key.startswith(k):
            return v
    return "unattributed"


def num(pattern: str, text: str):
    m = re.search(pattern, text)
    return float(m.group(1)) if m else None


def pose_runs():
    rows = []
    for d in sorted(LOGS.glob("LR2_PoseCheck*")):
        f = d / "report.txt"
        if not f.exists():
            continue
        t = f.read_text(encoding="utf-8", errors="replace")
        when = dt.datetime.fromtimestamp(f.stat().st_mtime)
        rows.append(dict(
            run=d.name, finished=when.strftime("%Y-%m-%d %H:%M"), runner=runner_of(d.name),
            walk_foot_slide=num(r"GAIT \[walk\][^\n]*?stanceFootSlide=([-0-9.]+)", t),
            sprint_foot_slide=num(r"GAIT \[sprint\][^\n]*?stanceFootSlide=([-0-9.]+)", t),
            mag_travel_m=num(r"RELOAD-TACTICAL[^\n]*?magMaxDisp=([0-9.]+)", t),
            walking_reload_gun_vs_cam=num(r"RELOAD-ANGLES \[walking tactical[^\n]*?gunFwdVsCam=([0-9.]+)", t),
            end_jump_walking=num(r"RELOAD-END-JUMP \[walking tactical\] maxRot=([0-9.]+)", t),
            end_jump_tactical=num(r"RELOAD-END-JUMP \[tactical\] maxRot=([0-9.]+)", t),
            killcam_lens_y=num(r"KILLCAM \[0\.30\][^\n]*?cam=\([-0-9.]+, ([0-9.]+),", t),
            killcam_head_hidden=("killerFirstPersonHidden=[Soldier" in t) if "KILLCAM [" in t else None,
            harness_errors=len(re.findall(r"ERROR \[PoseCheck\]", t)),
            exceptions=len(re.findall(r"Exception:", t)),
            screenshots=len(list(d.glob("*.png"))),
        ))
    return rows


def aim_runs():
    rows = []
    for d in sorted(LOGS.glob("LR2_AimTest_*")):
        f = d / "report.txt"
        if not f.exists():
            continue
        t = f.read_text(encoding="utf-8", errors="replace")
        st = re.search(r"AIM-TEST \[stationary\] shots=(\d+) hits=(\d+)", t)
        mv = re.search(r"AIM-TEST \[moving\] shots=(\d+) hits=(\d+)[^\n]*?\((\d+)% of misses behind", t)
        rows.append(dict(run=d.name, stationary_shots=int(st.group(1)) if st else 0, stationary_hits=int(st.group(2)) if st else 0,
                         moving_shots=int(mv.group(1)) if mv else 0, moving_hits=int(mv.group(2)) if mv else 0,
                         moving_misses_behind_pct=int(mv.group(3)) if mv else None))
    return rows


def qa_bot():
    rows = []
    for f in sorted((LOGS / "LR2_QABot").glob("qa_*.txt")):
        t = f.read_text(encoding="utf-8", errors="replace")
        rows.append(dict(scene=f.stem.replace("qa_", ""), passed=len(re.findall(r"^\s*\[PASS\]", t, re.M)),
                         failed=len(re.findall(r"^\s*\[FAIL\]", t, re.M)),
                         known_bad_fixtures=len(re.findall(r"\[PASS\] QA\.KnownBad", t)),
                         finished=dt.datetime.fromtimestamp(f.stat().st_mtime).strftime("%Y-%m-%d %H:%M")))
    return rows


def playtests():
    rows = []
    for d in sorted(LOGS.glob("LR2_Playtest*")):
        f = d / "report.txt"
        if not f.exists():
            continue
        t = f.read_text(encoding="utf-8", errors="replace")
        rows.append(dict(run=d.name, runtime_errors=num(r"Runtime errors/exceptions: (\d+)", t), screenshots=len(list(d.glob("*.png")))))
    return rows


RUNTIME_WORDS = re.compile(r"pose check|QA |QA check|QA bot|C9|C10|kc_|frames|probe|report|screenshot|close-up|visible", re.I)
COMPILE_WORDS = re.compile(r"compile|uncompilable|literal|does not exist in HDRP", re.I)


def defect_methods():
    out = []
    with (DATA / "bugs.csv").open(newline="", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            desc, who, stage = r["description"], r["found_by"], r["stage"]
            if who == "user":
                m = "Developer playing the game"
            elif stage == "pre-push" or who == "push-check" or COMPILE_WORDS.search(desc):
                m = "Compile and push gate"
            elif RUNTIME_WORDS.search(desc):
                m = "Automated runtime check (pose check, QA bot, captured frames)"
            else:
                m = "Agent reading code (review)"
            measured_wrong = bool(re.search(r"passes while|reads IK error|hidden duplicate|never restored|measured", desc, re.I))
            out.append(dict(id=r["id"], method=m, found_by=who, stage=stage, measured_wrong_thing=measured_wrong))
    return out


def write_csv(path: Path, rows):
    if not rows:
        return
    with path.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        w.writeheader()
        w.writerows(rows)


def style(ax, t):
    ax.set_facecolor(t["surface"])
    for sp in ax.spines.values():
        sp.set_color(t["base"])
    ax.tick_params(colors=t["muted"], labelsize=9)
    ax.grid(True, color=t["grid"], linewidth=0.8)
    ax.set_axisbelow(True)


def chart_footslide(runs, theme):
    t = THEMES[theme]
    pts = [r for r in runs if r["walk_foot_slide"] is not None]
    xs = [dt.datetime.strptime(r["finished"], "%Y-%m-%d %H:%M") for r in pts]
    fig, ax = plt.subplots(figsize=(11, 4.6), dpi=110, facecolor=t["surface"])
    style(ax, t)
    ax.axhspan(0, 0.5, color=t["band"], zorder=0)
    ax.text(xs[0], 0.52, "target: under 0.5 m/s", color=t["ink2"], fontsize=9, va="bottom")
    ax.plot(xs, [abs(r["walk_foot_slide"]) for r in pts], color=t["base"], linewidth=1, zorder=1)
    colors = {"Claude": t["s1"], "Copilot": t["s2"], "unattributed": t["other"]}
    for who in ("Claude", "Copilot", "unattributed"):
        sel = [(x, abs(r["walk_foot_slide"])) for x, r in zip(xs, pts) if r["runner"] == who]
        if sel:
            ax.scatter([a for a, _ in sel], [b for _, b in sel], s=46, color=colors[who], edgecolors=t["surface"], linewidths=1.5, label=who, zorder=3)
    last = pts[-1]
    ax.annotate(f"{abs(last['walk_foot_slide']):.2f} m/s", (xs[-1], abs(last["walk_foot_slide"])), xytext=(6, 8), textcoords="offset points", color=t["ink"], fontsize=9)
    ax.set_ylabel("foot slide while walking (m/s)", color=t["ink2"])
    ax.set_title("Foot sliding at 3.5 m/s across every pose check (lower is better)", color=t["ink"], fontsize=13, loc="left")
    fig.autofmt_xdate()
    leg = ax.legend(frameon=False, fontsize=9, loc="upper right")
    for tx in leg.get_texts():
        tx.set_color(t["ink2"])
    fig.tight_layout()
    fig.savefig(FIG / f"qa_footslide_{theme}.png", facecolor=t["surface"])
    plt.close(fig)


def chart_methods(defects, theme):
    t = THEMES[theme]
    order = ["Developer playing the game", "Agent reading code (review)", "Automated runtime check (pose check, QA bot, captured frames)", "Compile and push gate"]
    counts = [sum(1 for d in defects if d["method"] == m) for m in order]
    fig, ax = plt.subplots(figsize=(11, 3.6), dpi=110, facecolor=t["surface"])
    style(ax, t)
    ax.grid(True, axis="x", color=t["grid"], linewidth=0.8)
    ax.grid(False, axis="y")
    ys = list(range(len(order)))[::-1]
    ax.barh(ys, counts, color=t["s1"], height=0.55, edgecolor=t["surface"], linewidth=2)
    for y, c in zip(ys, counts):
        ax.text(c + 0.2, y, str(c), va="center", color=t["ink"], fontsize=10)
    ax.set_yticks(ys)
    ax.set_yticklabels(order, color=t["ink2"], fontsize=10)
    ax.set_xlabel("defects in the ledger (data/bugs.csv)", color=t["ink2"])
    ax.set_title("How each defect was found", color=t["ink"], fontsize=13, loc="left")
    fig.tight_layout()
    fig.savefig(FIG / f"qa_methods_{theme}.png", facecolor=t["surface"])
    plt.close(fig)


def main():
    runs, aims, bots, plays, defects = pose_runs(), aim_runs(), qa_bot(), playtests(), defect_methods()
    write_csv(DATA / "qa_pose_runs.csv", runs)
    write_csv(DATA / "qa_bot_latest.csv", bots)
    write_csv(DATA / "qa_defects_by_method.csv", defects)
    for th in THEMES:
        chart_footslide(runs, th)
        chart_methods(defects, th)
    md = ["<!-- generated by analysis/qa_analysis.py; do not edit by hand -->", "",
          f"Pose checks: {len(runs)} runs, {sum(r['screenshots'] for r in runs)} screenshots. Aim tests: {len(aims)}. "
          f"Playtest robot runs: {len(plays)}. QA bot scenes: {len(bots)}.", "",
          "| Run | Finished | Runner | Walk foot slide (m/s) | Mag travel (m) | Walking reload end jump (deg/frame) | Killcam lens y (m) | Harness ERROR lines |",
          "|---|---|---|---|---|---|---|---|"]
    fmt = lambda v, f="{:.2f}": "-" if v is None else f.format(v)
    for r in sorted(runs, key=lambda r: r["finished"]):
        md.append(f"| {r['run'].replace('LR2_PoseCheck', 'PoseCheck')} | {r['finished']} | {r['runner']} | {fmt(r['walk_foot_slide'])} | {fmt(r['mag_travel_m'], '{:.3f}')} | "
                  f"{fmt(r['end_jump_walking'])} | {fmt(r['killcam_lens_y'])} | {r['harness_errors']} |")
    md += ["", "| QA bot scene | PASS | FAIL | known-bad fixtures passing | finished |", "|---|---|---|---|---|"]
    for b in bots:
        md.append(f"| {b['scene']} | {b['passed']} | {b['failed']} | {b['known_bad_fixtures']} | {b['finished']} |")
    md += ["", "| How the defect was found | Defects | of which the check measured the wrong thing |", "|---|---|---|"]
    for m in sorted({d["method"] for d in defects}):
        sel = [d for d in defects if d["method"] == m]
        md.append(f"| {m} | {len(sel)} | {sum(d['measured_wrong_thing'] for d in sel)} |")
    md += ["", "| Aim test | Stationary shots | hits | Strafing shots | hits | misses behind the motion |", "|---|---|---|---|---|---|"]
    for a in aims:
        md.append(f"| {a['run']} | {a['stationary_shots']} | {a['stationary_hits']} | {a['moving_shots']} | {a['moving_hits']} | "
                  f"{'-' if a['moving_misses_behind_pct'] is None else str(a['moving_misses_behind_pct']) + ' %'} |")
    (FIG / "qa_summary.md").write_text("\n".join(md) + "\n", encoding="utf-8")
    print("\n".join(md))


if __name__ == "__main__":
    main()
