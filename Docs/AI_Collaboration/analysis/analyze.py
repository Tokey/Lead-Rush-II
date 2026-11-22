#!/usr/bin/env python3
"""
Quality of AI work per agent and model on LeadRush 2.

Reads ../data/tasks.csv and ../data/bugs.csv (see ../data/README.md for the columns) and writes, to ../figures/:
  - contribution_score_{light,dark}.png   contribution score with peer-rated task-difficulty weighting
  - escaped_per_10_tasks_{light,dark}.png   bugs found after delivery, per 10 tasks
  - review_catches_per_10_tasks_{light,dark}.png   bugs another agent caught at review, per 10 tasks
  - tasks_delivered_{light,dark}.png   completed task count per agent and model
  - summary.md   the same numbers as a table (embedded in ../README.md)

Run:  python Docs/AI_Collaboration/analysis/analyze.py      (needs matplotlib: pip install matplotlib)

Definitions
  task       a counted item in tasks.csv (outcomes other than "open" or "in-progress")
  difficulty a peer-rated band (small / standard / hard); unrated work receives the neutral standard weight
  escaped    a defect found after delivery (developer playtest or QA run), weighted by how sure the attribution is:
             high 1.0, medium 0.6, low 0.3
  review     a defect another agent caught before the developer saw it
  pre-push   a broken build stopped by the push check
Agents and models with very few tasks give noisy rates; every chart label carries n (the task count).
"""
import csv
import os
from collections import OrderedDict

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
DATA = os.path.join(ROOT, "data")
FIG = os.path.join(ROOT, "figures")

WEIGHT = {"high": 1.0, "medium": 0.6, "low": 0.3}
DIFFICULTY_WEIGHT = {"small": 0.75, "standard": 1.0, "hard": 1.25}
# One hue per chart (single series), text in ink tokens, recessive grid; light and dark get their own steps.
THEMES = {
    "light": dict(surface="#fcfcfb", ink="#0b0b0b", ink2="#52514e", grid="#e2e1dc", bar="#2a78d6"),
    "dark": dict(surface="#1a1a19", ink="#f0efec", ink2="#c3c2b7", grid="#34342f", bar="#3987e5"),
}


def read(name):
    with open(os.path.join(DATA, name), newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def key(agent, model):
    return f"{agent} · {model}"


def compute():
    tasks, bugs = read("tasks.csv"), read("bugs.csv")
    stats = OrderedDict()

    def row(k):
        if k not in stats:
            stats[k] = dict(tasks=0, pending=0, reopened=0, escaped=0.0, escaped_n=0, review=0, prepush=0, verified=0, ready=0,
                            finds_others=0, finds_own=0, sugg_yes=0, sugg_pending=0,
                            raw_task_credit=0.0, difficulty_task_credit=0.0, difficulty_rated=0,
                            difficulty_retro=0, difficulty_prework=0)
        return stats[k]

    for t in tasks:
        outcome = t["outcome"].strip()
        r = row(key(t["agent"], t["model"]))
        if outcome in ("open", "in-progress"):
            r["pending"] += 1
            continue
        r["tasks"] += 1
        r["reopened"] += outcome == "reopened"
        r["verified"] += outcome == "verified"
        r["ready"] += outcome == "ready-for-review"
        completion_credit = 1.0 if outcome == "verified" else (0.5 if outcome == "ready-for-review" else 0.0)
        difficulty = (t.get("difficulty") or "unrated").strip().lower()
        multiplier = DIFFICULTY_WEIGHT.get(difficulty, 1.0)
        r["raw_task_credit"] += completion_credit
        r["difficulty_task_credit"] += completion_credit * multiplier
        r["difficulty_rated"] += difficulty in DIFFICULTY_WEIGHT
        timing = (t.get("difficulty_timing") or "").strip().lower()
        r["difficulty_retro"] += difficulty in DIFFICULTY_WEIGHT and timing == "retrospective-peer-consensus"
        r["difficulty_prework"] += difficulty in DIFFICULTY_WEIGHT and timing == "pre-work-peer-consensus"
    for b in bugs:
        cause_agent = (b.get("caused_by_agent") or "").strip()
        cause_model = (b.get("caused_by_model") or "").strip()
        stage = b["stage"].strip()
        # Findings with an unknown cause must not create a phantom blank-agent row
        # or be charged as an escaped/review/pre-push defect to an invented owner.
        if cause_agent:
            r = row(key(cause_agent, cause_model or "unknown"))
            if stage == "escaped":
                r["escaped"] += WEIGHT.get(b["confidence"].strip(), 0.3)
                r["escaped_n"] += 1
            elif stage == "review":
                r["review"] += 1
            elif stage == "pre-push":
                r["prepush"] += 1
        finder = (b.get("found_by_agent") or "").strip()
        if finder and stage in ("review", "pre-push"):
            f = row(key(finder, (b.get("found_by_model") or "unknown").strip()))
            if cause_agent and finder == cause_agent:
                f["finds_own"] += 1
            else:
                f["finds_others"] += 1
    for sg in read("suggestions.csv"):
        r = row(key(sg["agent"], sg["model"]))
        adopted = sg["adopted"].strip()
        r["sugg_yes"] += adopted == "yes"
        r["sugg_pending"] += adopted == "pending"
    for k, r in stats.items():
        # The developer's rule: finding new bugs and suggesting meaningful features score best.
        other_score = (3 * r["finds_others"] + 2 * r["finds_own"] + 3 * r["sugg_yes"] + 1 * r["sugg_pending"]
                       - 2 * r["escaped"] - 1 * r["prepush"] - 0.5 * r["review"])
        r["raw_score"] = other_score + r["raw_task_credit"]
        r["score"] = other_score + r["difficulty_task_credit"]
        n = max(1, r["tasks"])
        r["escaped_per10"] = 10.0 * r["escaped"] / n
        r["review_per10"] = 10.0 * r["review"] / n
    return stats


def bar_chart(stats, metric, title, subtitle, xlabel, fname, fmt="{:.1f}", show_rated=False):
    items = [(k, v) for k, v in stats.items() if v["tasks"] > 0 or metric == "score"]
    items.sort(key=lambda kv: kv[1][metric])
    if show_rated:
        names = [f"{k}  (n={v['tasks']}, peer-rated={v['difficulty_rated']})" for k, v in items]
    else:
        names = [f"{k}  (n={v['tasks']})" for k, v in items]
    vals = [v[metric] for _, v in items]
    vmax = max([abs(v) for v in vals] + [1e-6])
    vmin = min(vals + [0.0])
    for theme, c in THEMES.items():
        fig, ax = plt.subplots(figsize=(9.8, 0.55 * len(items) + 2.3), dpi=150)
        fig.patch.set_facecolor(c["surface"])
        ax.set_facecolor(c["surface"])
        ax.barh(names, vals, height=0.5, color=c["bar"])
        for i, v in enumerate(vals):
            # Place negative bar labels cleanly on the +0 side so they never collide with y-axis labels.
            if v >= 0:
                ax.text(v + vmax * 0.015, i, fmt.format(v), va="center", ha="left", color=c["ink"], fontsize=9)
            else:
                ax.text(vmax * 0.015, i, fmt.format(v), va="center", ha="left", color=c["ink2"], fontsize=9)
        ax.set_xlim(vmin * 1.25 if vmin < 0 else 0, vmax * 1.22)
        if vmin < 0:
            ax.axvline(0, color=c["ink2"], linewidth=0.8)
        ax.xaxis.grid(True, color=c["grid"], linewidth=0.8)
        ax.set_axisbelow(True)
        for side in ("top", "right", "left"):
            ax.spines[side].set_visible(False)
        ax.spines["bottom"].set_color(c["grid"])
        ax.tick_params(colors=c["ink2"], labelsize=9, length=0)
        for lbl in ax.get_yticklabels():
            lbl.set_color(c["ink"])
        ax.set_xlabel(xlabel, color=c["ink2"], fontsize=9)
        ax.set_title(title, loc="left", color=c["ink"], fontsize=12, fontweight="bold", pad=28)
        ax.text(0, 1.02, subtitle, transform=ax.transAxes, color=c["ink2"], fontsize=8.5, va="bottom")
        fig.tight_layout()
        fig.savefig(os.path.join(FIG, f"{fname}_{theme}.png"), facecolor=c["surface"], bbox_inches="tight", pad_inches=0.25)
        plt.close(fig)


def summary(stats):
    lines = [
        "| Agent / model | Raw score | Difficulty-adjusted score | Task rows (counted including reopened / pending / reopened) | Peer-rated tasks (retro / pre-work) | Weighted task credit | Bugs found (others / own) | Suggestions (adopted / pending) | Escaped defects, weighted (count) | Escaped per 10 tasks | Review defects in owned work | Pre-push defects in owned work |",
        "|---|---|---|---|---|---|---|---|---|---|---|---|",
    ]
    for k, r in sorted(stats.items(), key=lambda kv: -kv[1]["score"]):
        rated = f"{r['difficulty_rated']} ({r['difficulty_retro']} / {r['difficulty_prework']})"
        lines.append(f"| {k} | {r['raw_score']:.1f} | **{r['score']:.1f}** | {r['tasks']} / {r['pending']} / {r['reopened']} | {rated} | {r['difficulty_task_credit']:.2f} | {r['finds_others']} / {r['finds_own']} | "
                     f"{r['sugg_yes']} / {r['sugg_pending']} | {r['escaped']:.1f} ({r['escaped_n']}) | {r['escaped_per10']:.1f} | "
                     f"{r['review']} | {r['prepush']} |")
    return "\n".join(lines)


def task_log_markdown(tasks):
    def esc(val):
        return (val or "").replace("|", "\\|").replace("\n", " ").strip()

    lines = [
        "<!-- generated by analysis/analyze.py from data/tasks.csv; shows the explicit origin/source of every task -->",
        "| ID | When (ET) | Agent / model | Board | Kind | Outcome | Difficulty | Rating timing | Rating basis | Task | Source (Prompt / Board / Evidence) |",
        "|---|---|---|---|---|---|---|---|---|---|---|",
    ]
    for t in reversed(tasks):
        lines.append(
            f"| `{esc(t.get('id'))}` | {esc(t.get('when'))} | {esc(t.get('agent'))} / `{esc(t.get('model'))}` | "
            f"{esc(t.get('board_id')) or '-'} | {esc(t.get('kind'))} | {esc(t.get('outcome'))} | "
            f"{esc(t.get('difficulty') or 'unrated')} | {esc(t.get('difficulty_timing')) or '-'} | {esc(t.get('difficulty_basis')) or '-'} | "
            f"{esc(t.get('title'))} | {esc(t.get('source'))} |"
        )
    return "\n".join(lines) + "\n"


# ---------------------------------------------------------------- protocol periods (v3 retro check)
PERIODS = [
    ("v1: before 11-17 23:25", None, "2026-11-17 23:25"),
    ("v2: 11-17 23:25 to 11-18 00:05", "2026-11-17 23:25", "2026-11-18 00:05"),
    ("v3: after 11-18 00:05", "2026-11-18 00:05", None),
]


def period_of(ts):
    ts = (ts or "").strip()[:16]
    for name, start, end in PERIODS:
        if (start is None or ts >= start) and (end is None or ts < end):
            return name
    return PERIODS[-1][0]


def period_stats():
    """Quality per protocol version: escaped defects are counted in the period when the CAUSING task was done."""
    weight = globals().get("WEIGHT", {"high": 1.0, "medium": 0.6, "low": 0.3})
    tasks, bugs = read("tasks.csv"), read("bugs.csv")
    when = {t["id"].strip(): t.get("when", "") for t in tasks}
    st = OrderedDict((p[0], dict(tasks=0, escaped=0.0, review=0, prepush=0)) for p in PERIODS)
    for t in tasks:
        if (t.get("outcome") or "").strip() != "open":
            st[period_of(t.get("when"))]["tasks"] += 1
    for b in bugs:
        stage = (b.get("stage") or "").strip()
        if stage == "escaped":
            cause = (b.get("caused_by_task") or "").strip()
            st[period_of(when.get(cause) or b.get("found"))]["escaped"] += weight.get((b.get("confidence") or "").strip(), 0.3)
        elif stage == "review":
            st[period_of(b.get("found"))]["review"] += 1
        elif stage == "pre-push":
            st[period_of(b.get("found"))]["prepush"] += 1
    for v in st.values():
        n = max(1, v["tasks"])
        v["escaped_per10"] = 10.0 * v["escaped"] / n
        v["caught_per10"] = 10.0 * (v["review"] + v["prepush"]) / n
    return st


def period_chart(st, metric, title, subtitle, xlabel, fname):
    names = [f"{k}  (n={v['tasks']})" for k, v in st.items()][::-1]  # chronological top to bottom
    vals = [v[metric] for v in st.values()][::-1]
    vmax = max(vals + [1e-6])
    for theme, c in THEMES.items():
        fig, ax = plt.subplots(figsize=(8.6, 0.55 * len(vals) + 2.0), dpi=150)
        fig.patch.set_facecolor(c["surface"])
        ax.set_facecolor(c["surface"])
        ax.barh(names, vals, height=0.5, color=c["bar"])
        for i, v in enumerate(vals):
            ax.text(v + vmax * 0.015, i, f"{v:.1f}", va="center", ha="left", color=c["ink"], fontsize=9)
        ax.set_xlim(0, vmax * 1.2)
        ax.xaxis.grid(True, color=c["grid"], linewidth=0.8)
        ax.set_axisbelow(True)
        for side in ("top", "right", "left"):
            ax.spines[side].set_visible(False)
        ax.spines["bottom"].set_color(c["grid"])
        ax.tick_params(colors=c["ink2"], labelsize=9, length=0)
        for lbl in ax.get_yticklabels():
            lbl.set_color(c["ink"])
        ax.set_xlabel(xlabel, color=c["ink2"], fontsize=9)
        ax.set_title(title, loc="left", color=c["ink"], fontsize=12, fontweight="bold", pad=24)
        ax.text(0, 1.02, subtitle, transform=ax.transAxes, color=c["ink2"], fontsize=8.5, va="bottom")
        fig.tight_layout()
        fig.savefig(os.path.join(FIG, f"{fname}_{theme}.png"), facecolor=c["surface"])
        plt.close(fig)


def period_table(st):
    lines = ["| Protocol period | Tasks | Bugs after delivery (weighted) | per 10 tasks | Caught before the developer (review + pre-push) | per 10 tasks |",
             "|---|---|---|---|---|---|"]
    for k, v in st.items():
        lines.append(f"| {k} | {v['tasks']} | {v['escaped']:.1f} | {v['escaped_per10']:.1f} | {v['review'] + v['prepush']} | {v['caught_per10']:.1f} |")
    return "\n".join(lines)


def main():
    os.makedirs(FIG, exist_ok=True)
    tasks = read("tasks.csv")
    stats = compute()
    pst = period_stats()
    period_chart(pst, "escaped_per10",
                 "Bugs found after delivery per 10 tasks, by protocol version (lower is better)",
                 "Each bug counts in the period when the task that caused it was done; confidence-weighted. v2/v3 tasks have had less time to be playtested.",
                 "after-delivery bugs per 10 tasks (lower is better)", "protocol_escaped_per_10_tasks")
    period_chart(pst, "caught_per10",
                 "Bugs stopped before the developer per 10 tasks, by protocol version (higher is better)",
                 "Peer-review catches plus pre-push build breaks. Cross-review started 11-17 18:07; v3 added check-the-check and compile gates.",
                 "bugs caught per 10 tasks (higher is better)", "protocol_caught_per_10_tasks")
    with open(os.path.join(FIG, "protocol_periods.md"), "w", encoding="utf-8") as f:
        f.write("<!-- generated by analysis/analyze.py; do not edit by hand -->\n" + period_table(pst) + "\n")
    print(period_table(pst))
    bar_chart(stats, "score",
              "Difficulty-adjusted contribution score (higher is better)",
              "n = scored tasks; peer-rated = median of 3 request-scope reviews. Historic ratings are retrospective estimates; unrated tasks get 1x.",
              "adjusted contribution points (higher is better)", "contribution_score", show_rated=True)
    bar_chart(stats, "escaped_per10",
              "Bugs found after delivery per 10 tasks (lower is better)",
              "Counts defects found by QA or the developer. Higher-confidence attributions count more.",
              "after-delivery bugs per 10 tasks (lower is better)", "escaped_per_10_tasks")
    bar_chart(stats, "review_per10",
              "Bugs caught in peer review per 10 tasks (higher is better)",
              "Counts defects another agent caught before delivery. More catches mean more issues were stopped early.",
              "bugs caught per 10 tasks (higher is better)", "review_catches_per_10_tasks")
    bar_chart(stats, "tasks",
              "Completed task count",
              "Counts rows whose outcome is neither open nor in progress; n is the counted task rows for each agent and model.",
              "counted task rows", "tasks_delivered", fmt="{:.0f}")
    table = summary(stats)
    with open(os.path.join(FIG, "summary.md"), "w", encoding="utf-8") as f:
        f.write("<!-- generated by analysis/analyze.py from data/*.csv; do not edit by hand -->\n" + table + "\n")
    with open(os.path.join(FIG, "tasks_log.md"), "w", encoding="utf-8") as f:
        f.write(task_log_markdown(tasks))
    print(table)


if __name__ == "__main__":
    main()
