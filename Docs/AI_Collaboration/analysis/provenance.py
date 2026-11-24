#!/usr/bin/env python3
"""
Who decided what: decision provenance for Lead Rush II.

Reads (all under Docs/AI_Collaboration/data/):
  features.csv     one row per atomic feature, coded with its origin (see the codebook below)
  prompts.csv      every developer prompt, coded with an interaction pattern and a specificity level
  bugs.csv         defect ledger (who found each bug, at which stage)
  tasks.csv        task ledger (agent and model per task)
  autonomy_ticks.csv  Claude Code's self-prompting coordinator ticks per hour

Writes to Docs/AI_Collaboration/figures/:
  prov_units, prov_by_area, prov_by_agent, prov_by_day, prov_kept, prompt_patterns, automation_share,
  prompt_specificity, elaboration, bug_finders, autonomy_timeline   ({name}_light.png and {name}_dark.png)
  prov_growth.gif            animated unit chart: every feature appears in the order it shipped
  provenance_summary.md      the numbers behind the page, as tables
and to data/:
  provenance_stats.json      the same numbers, machine-readable
  provenance_validation.csv  a blind, stratified sample for the developer to label (created once)
  ../FEATURES_TABLE.md       the atomic feature table as a GitHub page

Run:  python Docs/AI_Collaboration/analysis/provenance.py
      python Docs/AI_Collaboration/analysis/provenance.py --rebuild-prompts   (re-parse PROMPTS.md, keep existing codes)

Codebook (origin of a feature)
  dir   Direct from prompt     the developer asked for it and the prompt fixed what the player gets
  ai    AI-designed            the developer asked for the outcome; the AI chose what the player gets
                               (assets, values, mechanism, rule or placement). "Improvement over the request."
  init  AI-initiated           nobody asked; an AI added it (tools, guards, extra features, plans)
  dbug  Developer bug report   the developer found a defect while playing; an AI found the cause and fixed it
  qa    AI QA finding          an AI test, harness, cross-review or runtime check found the defect; an AI fixed it
  Test used for dir vs ai: given only the prompt, would different competent developers build noticeably
  different things? If yes, the AI made the design decision (ai). If no, the prompt made it (dir).

Prompt codes
  pattern  D directive, F feedback loop (reports what the running game did), I task iteration (refine existing work),
           V validation (check or test), L learning (explain), P process or coordination (not a task)
           Automation = D + F, augmentation = I + V + L, as in the Anthropic Economic Index.
  spec     E exact (names the means, value, asset or rule), G goal (names the outcome), O open mandate
"""
import argparse
import csv
import json
import math
import os
import random
import re
from collections import Counter, OrderedDict, defaultdict

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
from matplotlib import font_manager  # noqa: E402
from matplotlib.patches import Rectangle  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
DATA = os.path.join(ROOT, "data")
FIG = os.path.join(ROOT, "figures")

ORIGINS = OrderedDict([
    ("dir", dict(label="Direct from prompt", light="#2a78d6", dark="#3987e5", mark="🟦",
                 desc="You asked for it and your words fixed the result")),
    ("ai", dict(label="AI-designed (from your goal)", light="#eb6834", dark="#d95926", mark="🟧",
                desc="You asked for the outcome; the AI chose the design")),
    ("init", dict(label="AI-initiated", light="#1baf7a", dark="#199e70", mark="🟩",
                  desc="Nobody asked; an AI added it")),
    ("dbug", dict(label="Your bug report", light="#eda100", dark="#c98500", mark="🟨",
                  desc="You found a defect; an AI found the cause and fixed it")),
    ("qa", dict(label="AI QA finding", light="#e87ba4", dark="#d55181", mark="🟪",
                desc="An AI test or review found the defect; an AI fixed it")),
])
THEMES = {
    "light": dict(surface="#fcfcfb", ink="#0b0b0b", ink2="#52514e", grid="#e2e1dc", bar="#2a78d6", bar2="#1baf7a",
                  ord=["#104281", "#2a78d6", "#86b6ef"]),
    "dark": dict(surface="#1a1a19", ink="#f0efec", ink2="#c3c2b7", grid="#34342f", bar="#3987e5", bar2="#199e70",
                 ord=["#9ec5f4", "#3987e5", "#184f95"]),
}
AREAS = OrderedDict([
    ("gunplay", "Gunplay and weapons"), ("animation", "Animation and body"), ("bots", "Bots and AI"),
    ("audio", "Audio"), ("vfx", "Visual effects"), ("maps", "Maps and lighting"), ("ui", "Menus and HUD"),
    ("camera", "Camera and killcam"), ("modes", "Game modes"), ("qa", "QA and test tools"),
    ("docs", "Docs and research"), ("process", "Collaboration process"),
])
AGENTS = ["Claude", "Gemini", "Copilot", "Codex"]
DEFAULT_MODEL = {"Claude": "claude-opus-5-5", "Copilot": "gpt-5.6-luna", "Codex": "gpt-6", "Gemini": "gemini-4-hydrogen"}
OUTCOMES = OrderedDict([("kept", "Kept as built"), ("changed", "Changed later"), ("reverted", "Reverted"),
                        ("open", "Open or untested")])
PATTERNS = OrderedDict([("D", "Directive"), ("F", "Feedback loop"), ("I", "Task iteration"), ("V", "Validation"),
                        ("L", "Learning"), ("P", "Process / coordination")])
SPECS = OrderedDict([("E", "Exact"), ("G", "Goal"), ("O", "Open mandate")])
# Published reference points used in the evaluation section (see DECISION_PROVENANCE.md, References).
REFERENCE = {
    "claude_code_automation": 79.0, "claude_ai_coding_automation": 49.0,          # Anthropic, Apr 2025
    "claude_code_directive": 43.8, "claude_ai_coding_directive": 27.5,
    "claude_code_feedback": 35.8, "claude_ai_coding_feedback": 21.3,
    "claude_ai_all_automation": 42.6,                                              # Anthropic, Feb 2025
    "agentic_pr_merged": 83.8, "agentic_pr_no_revision": 54.9, "human_pr_no_revision": 58.5,  # Watanabe et al.
    "agentic_pr_no_modification_multi": 74.1,                                      # arXiv 2601.20106
}


# ----------------------------------------------------------------------------------------------------------------- io
def read(name):
    with open(os.path.join(DATA, name), newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def write(name, rows, fields):
    with open(os.path.join(DATA, name), "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=fields, lineterminator="\n")
        w.writeheader()
        for r in rows:
            w.writerow({k: r.get(k, "") for k in fields})


# ----------------------------------------------------------------------------------------------------------- prompts
def parse_prompts_md(path):
    """Every developer prompt in PROMPTS.md, in file order (P001, P002 ...)."""
    lines = open(path, encoding="utf-8").read().split("\n")
    date, section, out, cur = None, "", [], None
    hdr = re.compile(r"^\*\*(\d\d:\d\d)(?:[–-]\d\d:\d\d)? · (.+?)\*\*(.*)$")
    bul = re.compile(r"^- `(\d\d:\d\d)(?:[–-]\d\d:\d\d)?` \(([^)]*)\): (.*)$")

    def flush():
        nonlocal cur
        if cur:
            cur["text"] = "\n".join(cur.pop("lines")).strip()
            if cur["text"]:
                out.append(cur)
        cur = None

    for ln in lines:
        m = re.match(r"^##+ (\d{4}-\d\d-\d\d)\s*$", ln)
        if m:
            flush(); date = m.group(1); section = "day"; continue
        if ln.startswith("## ") or ln.startswith("### "):
            flush()
            t = ln.lstrip("#").strip(); section = t
            m2 = re.match(r"(\d{4}-\d\d-\d\d)", t)
            if m2:
                date = m2.group(1)
            if "Late 2026-11-17" in t:
                date = "late17"
            if "Session Transition" in t:
                date = "2026-11-18"
            if "automatic tick" in t or "Agent-to-agent" in t:
                date = None
            continue
        m = hdr.match(ln)
        if m and date:
            flush()
            tm, who, extra = m.groups()
            d = date if date != "late17" else ("2026-11-17" if tm >= "12:00" else "2026-11-18")
            kind = "verbatim"
            if "Agent Prompt" in who or "->" in who or (who.startswith("Claude Code to")):
                kind = "agent"
            if "from board" in extra:
                kind = "board"
            if kind == "agent":
                to = "Copilot" if "Copilot" in who else "Gemini"
            elif "Claude Code / " in who:
                to = "Claude+Gemini" if "Gemini" in who else "All"
            elif "Claude Code" in who:
                to = "Claude"
            elif "Gemini" in who:
                to = "Gemini"
            elif "Copilot" in who:
                to = "Copilot"
            else:
                to = who
            cur = dict(date=d, time_et=tm, to=to, source=kind, lines=[])
            continue
        m = bul.match(ln)
        if m and date:
            flush()
            tm, tag, body = m.groups()
            d = date if date != "late17" else "2026-11-18"
            q = re.findall(r'\*"(.+?)"\*', body)
            out.append(dict(date=d, time_et=tm, to="Gemini" if "Session Transition" in section else "mixed",
                            source="summary", text=" / ".join(q) if q else body))
            continue
        if cur is not None:
            if ln.startswith(">"):
                cur["lines"].append(ln[1:].strip())
            elif ln.strip() == "":
                cur["lines"].append("")
    flush()
    for i, p in enumerate(out, 1):
        p["pid"] = f"P{i:03d}"
        p["text"] = re.sub(r"\n\s*\*\d+ prompts to Claude Code.*$", "", p["text"], flags=re.S).strip()
        p["words"] = len(p["text"].split())
    return out


def build_prompts(rebuild, codes_path=None):
    path = os.path.join(DATA, "prompts.csv")
    if os.path.exists(path) and not rebuild and not codes_path:
        return read("prompts.csv")
    codes = {}
    if os.path.exists(path):
        codes.update({r["pid"]: (r["pattern"], r["spec"]) for r in read("prompts.csv")})
    if codes_path:
        with open(codes_path, newline="", encoding="utf-8") as f:
            codes.update({r["pid"]: (r["pattern"], r["spec"]) for r in csv.DictReader(f)})
    rows = parse_prompts_md(os.path.join(ROOT, "PROMPTS.md"))
    for r in rows:
        pat, spec = codes.get(r["pid"], ("", ""))
        if r["source"] in ("agent", "board"):
            pat, spec = "", ""            # not developer prompts / duplicates of verbatim ones
        r["pattern"], r["spec"] = pat, spec
    write("prompts.csv", rows, ["pid", "date", "time_et", "to", "source", "words", "pattern", "spec", "text"])
    return read("prompts.csv")


# ---------------------------------------------------------------------------------------------------------- features
FEATURE_FIELDS = ["fid", "date", "area", "feature", "origin", "agent", "model", "board", "task", "bug", "prompt",
                  "confidence", "asked", "ai_decided", "outcome"]


def import_features(paths):
    """One-time import of the pipe-separated coding sheets into features.csv."""
    task_model = {t["id"]: t["model"] for t in read("tasks.csv")}
    rows = []
    for p in paths:
        with open(p, encoding="utf-8") as f:
            for ln in f:
                ln = ln.rstrip("\n")
                if not ln.strip() or ln.startswith("fid|"):
                    continue
                parts = ln.split("|")
                assert len(parts) == 15, (len(parts), ln[:80])
                r = dict(zip(FEATURE_FIELDS, [x.strip() for x in parts]))
                r["date"] = "2026-" + r["date"]
                r["confidence"] = {"h": "high", "m": "medium", "l": "low"}[r["confidence"]]
                for k in ("asked", "ai_decided", "board", "task", "bug", "prompt"):
                    if r[k] == "-":
                        r[k] = ""
                if not r["model"]:
                    first = r["task"].split(" ")[0] if r["task"] else ""
                    r["model"] = task_model.get(first) or DEFAULT_MODEL[r["agent"]]
                rows.append(r)
    ids = [r["fid"] for r in rows]
    assert len(ids) == len(set(ids)), "duplicate fid"
    for r in rows:
        assert r["origin"] in ORIGINS and r["area"] in AREAS and r["outcome"] in OUTCOMES, r["fid"]
    write("features.csv", rows, FEATURE_FIELDS)


# ------------------------------------------------------------------------------------------------------------- stats
def pct(a, b):
    return 100.0 * a / b if b else 0.0


def cohen_kappa(a, b):
    labels = sorted(set(a) | set(b))
    n = len(a)
    po = sum(1 for x, y in zip(a, b) if x == y) / n
    ca, cb = Counter(a), Counter(b)
    pe = sum(ca[l] * cb[l] for l in labels) / (n * n)
    return (po - pe) / (1 - pe) if pe < 1 else 1.0, po


def compute(features, prompts, bugs, ticks):
    s = OrderedDict()
    n = len(features)
    s["features"] = n
    oc = Counter(f["origin"] for f in features)
    s["origin_counts"] = {k: oc[k] for k in ORIGINS}
    s["origin_pct"] = {k: round(pct(oc[k], n), 1) for k in ORIGINS}
    # who set the goal x who chose the design
    s["goal_by_developer"] = oc["dir"] + oc["ai"] + oc["dbug"]
    s["goal_by_ai"] = oc["init"] + oc["qa"]
    s["design_by_developer"] = oc["dir"]
    s["design_by_ai"] = n - oc["dir"]
    s["by_agent"] = {a: {k: sum(1 for f in features if f["agent"] == a and f["origin"] == k) for k in ORIGINS}
                     for a in AGENTS}
    s["by_area"] = {a: {k: sum(1 for f in features if f["area"] == a and f["origin"] == k) for k in ORIGINS}
                    for a in AREAS}
    days = sorted({f["date"] for f in features})
    s["by_day"] = {d: {k: sum(1 for f in features if f["date"] == d and f["origin"] == k) for k in ORIGINS} for d in days}
    s["by_model"] = dict(Counter(f"{f['agent']} · {f['model']}" for f in features).most_common())
    s["outcomes"] = {k: {o: sum(1 for f in features if f["origin"] == k and f["outcome"] == o) for o in OUTCOMES}
                     for k in ORIGINS}
    closed = [f for f in features if f["outcome"] != "open"]
    s["kept_pct_all"] = round(pct(sum(1 for f in closed if f["outcome"] == "kept"), len(closed)), 1)
    s["kept_pct"] = {}
    for k in ORIGINS:
        c = [f for f in closed if f["origin"] == k]
        s["kept_pct"][k] = dict(n=len(c), kept=round(pct(sum(1 for f in c if f["outcome"] == "kept"), len(c)), 1))
    s["reverted"] = [dict(fid=f["fid"], feature=f["feature"], origin=f["origin"], agent=f["agent"])
                     for f in features if f["outcome"] == "reverted"]
    s["confidence"] = dict(Counter(f["confidence"] for f in features))
    # prompts
    dev = [p for p in prompts if p["source"] in ("verbatim", "summary")]
    s["prompts"] = len(dev)
    s["prompts_verbatim"] = sum(1 for p in dev if p["source"] == "verbatim")
    s["prompts_by_agent"] = dict(Counter(p["to"] for p in dev).most_common())
    words = sorted(int(p["words"]) for p in dev)
    s["prompt_words_median"] = words[len(words) // 2]
    s["prompt_words_total"] = sum(words)
    pc = Counter(p["pattern"] for p in dev if p["pattern"])
    s["pattern_counts"] = {k: pc[k] for k in PATTERNS}
    task_p = [p for p in dev if p["pattern"] in "DFIVL" and p["pattern"]]
    s["task_prompts"] = len(task_p)
    s["pattern_pct_of_task_prompts"] = {k: round(pct(pc[k], len(task_p)), 1) for k in "DFIVL"}
    s["automation_pct"] = round(pct(pc["D"] + pc["F"], len(task_p)), 1)
    spec_p = [p for p in dev if p["spec"] in ("E", "G", "O")]
    scc = Counter(p["spec"] for p in spec_p)
    s["spec_counts"] = {k: scc[k] for k in SPECS}
    s["spec_pct"] = {k: round(pct(scc[k], len(spec_p)), 1) for k in SPECS}
    s["spec_by_day"] = {}
    for d in sorted({p["date"] for p in spec_p}):
        c = Counter(p["spec"] for p in spec_p if p["date"] == d)
        s["spec_by_day"][d] = {k: c[k] for k in SPECS}
    # elaboration: features traced to each prompt
    per_prompt = Counter()
    for f in features:
        for pid in f["prompt"].split():
            per_prompt[pid] += 1
    pw = {p["pid"]: int(p["words"]) for p in prompts}
    s["features_with_prompt"] = sum(1 for f in features if f["prompt"])
    s["prompts_with_features"] = len(per_prompt)
    s["top_prompts"] = [dict(pid=k, features=v, words=pw.get(k, 0)) for k, v in per_prompt.most_common(6)]
    s["ai_choices_from_short_prompts"] = sum(1 for f in features if f["origin"] == "ai" and f["prompt"]
                                              and pw.get(f["prompt"].split()[0], 99) <= 15)
    # bugs
    def finder(b):
        return "developer" if b["found_by"] == "user" else "ai"
    s["bugs"] = len(bugs)
    s["bugs_found_by"] = dict(Counter(finder(b) for b in bugs))
    s["bugs_stage"] = dict(Counter(b["stage"] for b in bugs))
    s["bugs_by_finder_detail"] = dict(Counter(b["found_by"] for b in bugs).most_common())
    s["bugs_by_day"] = {}
    for b in bugs:
        d = b["found"][:10]
        s["bugs_by_day"].setdefault(d, Counter())[finder(b)] += 1
    s["bugs_by_day"] = {d: dict(c) for d, c in sorted(s["bugs_by_day"].items())}
    # autonomy
    s["ticks"] = sum(int(t["ticks"]) for t in ticks)
    s["ticks_by_day"] = {}
    for t in ticks:
        s["ticks_by_day"][t["hour_et"][:10]] = s["ticks_by_day"].get(t["hour_et"][:10], 0) + int(t["ticks"])
    s["prompts_by_day"] = dict(sorted(Counter(p["date"] for p in dev).items()))
    return s


# ----------------------------------------------------------------------------------------------------------- figures
def style(ax, c, grid_axis="x"):
    ax.set_facecolor(c["surface"])
    for sp in ("top", "right", "left"):
        ax.spines[sp].set_visible(False)
    ax.spines["bottom"].set_color(c["grid"])
    ax.tick_params(colors=c["ink2"], labelsize=9.5, length=0)
    ax.grid(axis=grid_axis, color=c["grid"], linewidth=0.8)
    ax.set_axisbelow(True)


def titles(fig, c, title, subtitle, extra=0.0):
    """Title and subtitle at fixed inch offsets; reserves the space above the axes. Returns a y for a legend row."""
    H = fig.get_size_inches()[1]
    n_sub = subtitle.count("\n") + 1
    fig.text(0.012, 1 - 0.10 / H, title, ha="left", va="top", fontsize=13.5, fontweight="bold", color=c["ink"])
    fig.text(0.012, 1 - 0.40 / H, subtitle, ha="left", va="top", fontsize=10, color=c["ink2"], linespacing=1.35)
    head = 0.40 + 0.2 * n_sub + 0.14
    fig.subplots_adjust(top=1 - (head + extra) / H)
    return 1 - (head + 0.12) / H


def top_legend(ax, c):
    leg = ax.legend(loc="lower left", bbox_to_anchor=(0, 1.02), frameon=False, fontsize=9.5, ncol=3, borderaxespad=0)
    for t in leg.get_texts():
        t.set_color(c["ink"])


def save(fig, name, theme, c):
    fig.savefig(os.path.join(FIG, f"{name}_{theme}.png"), facecolor=c["surface"], dpi=150, bbox_inches="tight",
                pad_inches=0.2)
    plt.close(fig)


def legend_row(fig, c, theme, keys, y=0.86, labels=None):
    x = 0.012
    for k in keys:
        lab = (labels or {}).get(k, ORIGINS[k]["label"] if k in ORIGINS else k)
        col = ORIGINS[k][theme] if k in ORIGINS else k
        fig.patches.append(Rectangle((x, y - 0.012), 0.012, 0.03, transform=fig.transFigure, color=col))
        t = fig.text(x + 0.017, y, lab, ha="left", va="center", fontsize=9.5, color=c["ink"])
        x += 0.017 + 0.0072 * len(lab) + 0.03


def fig_units(features, s):
    order = list(ORIGINS)
    cols = 46
    for theme, c in THEMES.items():
        fig, ax = plt.subplots(figsize=(11, 3.9))
        fig.subplots_adjust(top=0.80, bottom=0.02, left=0.01, right=0.99)
        fig.patch.set_facecolor(c["surface"]); ax.set_facecolor(c["surface"]); ax.axis("off")
        ax.set_anchor("N")
        y = 0
        for k in order:
            items = [f for f in features if f["origin"] == k]
            rows = max(2, math.ceil(len(items) / cols))
            for i, _ in enumerate(items):
                r, q = divmod(i, cols)
                ax.add_patch(Rectangle((q, -(y + r)), 0.82, 0.82, color=ORIGINS[k][theme], linewidth=0))
            ax.text(-0.6, -(y) + 0.4, f"{ORIGINS[k]['label']}", ha="right", va="center", fontsize=10.5, color=c["ink"],
                    fontweight="bold")
            ax.text(-0.6, -(y) - 0.55, f"{len(items)} features · {s['origin_pct'][k]:.0f} %", ha="right", va="center",
                    fontsize=9.5, color=c["ink2"])
            y += rows + 0.9
        ax.set_xlim(-17, cols + 0.5); ax.set_ylim(-(y) + 0.5, 1.4)
        ax.set_aspect("equal")
        titles(fig, c, f"Where {s['features']} features of Lead Rush II came from",
               "One square per atomic feature or fix, Nov 16-20 2026. Coded from prompts, ledgers and commits.")
        save(fig, "prov_units", theme, c)


def stacked_h(name, rows, labels, title, subtitle, share=True, height=None, note=None):
    keys = list(ORIGINS)
    for theme, c in THEMES.items():
        fig, ax = plt.subplots(figsize=(11, height or (1.0 + 0.42 * len(rows))))
        fig.patch.set_facecolor(c["surface"]); style(ax, c)
        ys = list(range(len(rows)))[::-1]
        for y, (rk, vals) in zip(ys, rows):
            tot = sum(vals.values())
            left = 0
            for k in keys:
                v = vals.get(k, 0)
                w = pct(v, tot) if share else v
                if w <= 0:
                    continue
                ax.barh(y, w, left=left, color=ORIGINS[k][theme], height=0.62, edgecolor=c["surface"], linewidth=1.5)
                if share and w >= 9:
                    ax.text(left + w / 2, y, f"{w:.0f}%", ha="center", va="center", fontsize=8.5, color="#ffffff"
                            if k in ("dir", "ai") else "#0b0b0b")
                elif not share and w >= 6:
                    ax.text(left + w / 2, y, f"{v}", ha="center", va="center", fontsize=8.5, color="#ffffff"
                            if k in ("dir", "ai") else "#0b0b0b")
                left += w
            ax.text((100 if share else left) + (1.2 if share else 1.5), y, f"n={tot}", va="center", fontsize=9,
                    color=c["ink2"])
        ax.set_yticks(ys); ax.set_yticklabels([labels.get(r[0], r[0]) for r in rows], fontsize=10, color=c["ink"])
        if share:
            ax.set_xlim(0, 108); ax.set_xticks([0, 25, 50, 75, 100]); ax.set_xticklabels(["0", "25", "50", "75", "100 %"])
        else:
            mx = max(sum(v.values()) for _, v in rows)
            ax.set_xlim(0, mx * 1.1)
        ly = titles(fig, c, title, subtitle, extra=0.36)
        legend_row(fig, c, theme, keys, y=ly)
        if note:
            fig.text(0.012, 0.005, note, fontsize=8.5, color=c["ink2"], ha="left", va="bottom")
        save(fig, name, theme, c)


def fig_kept(s):
    keys = list(ORIGINS)
    for theme, c in THEMES.items():
        fig, ax = plt.subplots(figsize=(11, 3.6))
        fig.patch.set_facecolor(c["surface"]); style(ax, c)
        ys = list(range(len(keys)))[::-1]
        for y, k in zip(ys, keys):
            v = s["kept_pct"][k]
            ax.barh(y, v["kept"], color=ORIGINS[k][theme], height=0.6)
            ax.text(v["kept"] + 1, y, f"{v['kept']:.0f} %  (n={v['n']})", va="center", fontsize=9.5, color=c["ink"])
        ax.axvline(REFERENCE["agentic_pr_no_revision"], color=c["ink2"], linestyle=(0, (3, 3)), linewidth=1.2)
        ax.set_yticks(ys); ax.set_yticklabels([ORIGINS[k]["label"] for k in keys], fontsize=10, color=c["ink"])
        ax.set_xlim(0, 112); ax.set_xticks([0, 25, 50, 75, 100]); ax.set_xticklabels(["0", "25", "50", "75", "100 %"])
        titles(fig, c, "How much of each kind of work was kept as built",
               "Share of finished features never changed or reverted later (open items excluded). Dashed line: Claude Code\n"
               "pull requests merged without any revision on GitHub, 54.9 % (Watanabe et al., 567 PRs).")
        save(fig, "prov_kept", theme, c)


def fig_prompt_patterns(s):
    keys = ["D", "F", "I", "V", "L"]
    for theme, c in THEMES.items():
        fig, ax = plt.subplots(figsize=(11, 3.4))
        fig.patch.set_facecolor(c["surface"]); style(ax, c)
        ys = list(range(len(keys)))[::-1]
        for y, k in zip(ys, keys):
            v = s["pattern_pct_of_task_prompts"][k]
            ax.barh(y, v, color=c["bar"], height=0.6)
            if v >= 12:
                ax.text(v - 0.6, y, f"{v:.0f} %  ({s['pattern_counts'][k]})", va="center", ha="right", fontsize=9.5,
                        color="#ffffff")
            else:
                ax.text(v + 0.8, y, f"{v:.0f} %  ({s['pattern_counts'][k]})", va="center", fontsize=9.5, color=c["ink"])
        refs = {"D": REFERENCE["claude_code_directive"], "F": REFERENCE["claude_code_feedback"]}
        for y, k in zip(ys, keys):
            if k in refs:
                ax.plot([refs[k]], [y], marker="D", markersize=7, color=c["ink"], zorder=3)
        ax.text(0.99, 0.04, "◆ Claude Code average (Anthropic Economic Index, Apr 2025)", transform=ax.transAxes,
                ha="right", fontsize=8.5, color=c["ink2"])
        names = {"D": "Directive: do X", "F": "Feedback loop: X is broken", "I": "Task iteration: change X",
                 "V": "Validation: check X", "L": "Learning: explain X"}
        ax.set_yticks(ys); ax.set_yticklabels([names[k] for k in keys], fontsize=10, color=c["ink"])
        ax.set_xlim(0, 60); ax.set_xticks([0, 15, 30, 45, 60]); ax.set_xticklabels(["0", "15", "30", "45", "60 %"])
        titles(fig, c, f"What the developer's {s['task_prompts']} task prompts asked for",
               "Interaction patterns from the Anthropic Economic Index, coded per prompt. Process prompts excluded.")
        save(fig, "prompt_patterns", theme, c)


def fig_automation(s):
    items = [("Lead Rush II (this project)", s["automation_pct"]),
             ("Claude Code, Apr 2025", REFERENCE["claude_code_automation"]),
             ("Claude.ai coding, Apr 2025", REFERENCE["claude_ai_coding_automation"])]
    for theme, c in THEMES.items():
        fig, ax = plt.subplots(figsize=(11, 2.5))
        fig.patch.set_facecolor(c["surface"]); style(ax, c)
        ys = [2, 1, 0]
        for y, (lab, v) in zip(ys, items):
            ax.barh(y, v, color=c["bar"] if y == 2 else c["grid"], height=0.6,
                    edgecolor=c["ink2"] if y != 2 else c["bar"], linewidth=0.8)
            ax.text(v + 0.8, y, f"{v:.0f} %", va="center", fontsize=10, color=c["ink"])
        ax.set_yticks(ys); ax.set_yticklabels([i[0] for i in items], fontsize=10, color=c["ink"])
        ax.set_xlim(0, 100); ax.set_xticks([0, 25, 50, 75, 100]); ax.set_xticklabels(["0", "25", "50", "75", "100 %"])
        titles(fig, c, "Automation share: directive + feedback-loop prompts",
               "Ours is per prompt (hand-coded); Anthropic's is per conversation (Clio). Similar idea, different unit.")
        save(fig, "automation_share", theme, c)


def fig_specificity(s):
    days = list(s["spec_by_day"])
    for theme, c in THEMES.items():
        fig, ax = plt.subplots(figsize=(11, 3.8))
        fig.patch.set_facecolor(c["surface"]); style(ax, c, grid_axis="y")
        x = list(range(len(days)))
        bottom = [0] * len(days)
        for i, k in enumerate(SPECS):
            vals = [s["spec_by_day"][d][k] for d in days]
            ax.bar(x, vals, bottom=bottom, color=c["ord"][i], width=0.6, edgecolor=c["surface"], linewidth=1.5,
                   label=SPECS[k])
            for xi, (b, v) in enumerate(zip(bottom, vals)):
                if v >= 4:
                    ax.text(xi, b + v / 2, str(v), ha="center", va="center", fontsize=8.5,
                            color="#ffffff" if (theme == "light" and i < 2) or (theme == "dark" and i == 2) else "#0b0b0b")
            bottom = [b + v for b, v in zip(bottom, vals)]
        ax.set_xticks(x); ax.set_xticklabels([d[5:] for d in days], fontsize=10, color=c["ink"])
        ax.set_ylabel("task prompts", color=c["ink2"], fontsize=9.5)
        top_legend(ax, c)
        titles(fig, c, "How precisely the developer specified each request, by day",
               "Exact = named the means or value. Goal = named the outcome. Open = a mandate like 'keep improving'.",
               extra=0.35)
        save(fig, "prompt_specificity", theme, c)


def fig_elaboration(features, prompts, s):
    per = Counter()
    ai_per = Counter()
    for f in features:
        for pid in f["prompt"].split():
            per[pid] += 1
            if f["origin"] in ("ai", "init"):
                ai_per[pid] += 1
    pw = {p["pid"]: int(p["words"]) for p in prompts}
    pts = [(pw[k], v, k) for k, v in per.items() if k in pw]
    for theme, c in THEMES.items():
        fig, ax = plt.subplots(figsize=(11, 4.6))
        fig.patch.set_facecolor(c["surface"]); style(ax, c, grid_axis="both")
        rnd = random.Random(7)
        xs = [p[0] for p in pts]
        ys = [p[1] + rnd.uniform(-0.12, 0.12) for p in pts]
        ax.scatter(xs, ys, s=46, color=c["bar"], edgecolor=c["surface"], linewidth=1.2, zorder=3)
        ax.set_xscale("log")
        ax.set_xlabel("words in the prompt (log scale)", color=c["ink2"], fontsize=9.5)
        ax.set_ylabel("features traced to it", color=c["ink2"], fontsize=9.5)
        for w, v, k in sorted(pts, key=lambda p: -p[1])[:4] + [p for p in pts if p[2] in ("P273", "P224")]:
            ax.annotate(k, (w, v), xytext=(6, 4), textcoords="offset points", fontsize=8.5, color=c["ink2"])
        titles(fig, c, "One prompt, many decisions",
               f"Each dot is a prompt that produced at least one feature ({len(pts)} prompts). Labels are prompt ids.")
        save(fig, "elaboration", theme, c)


def fig_bugs(s):
    days = list(s["bugs_by_day"])
    series = [("developer", "Found by the developer while playing", "dbug"), ("ai", "Found by an AI (review, QA, test)", "qa")]
    for theme, c in THEMES.items():
        fig, ax = plt.subplots(figsize=(11, 3.6))
        fig.patch.set_facecolor(c["surface"]); style(ax, c, grid_axis="y")
        x = list(range(len(days)))
        bottom = [0] * len(days)
        for key_, lab, ok in series:
            vals = [s["bugs_by_day"][d].get(key_, 0) for d in days]
            ax.bar(x, vals, bottom=bottom, color=ORIGINS[ok][theme], width=0.6, edgecolor=c["surface"], linewidth=1.5,
                   label=lab)
            for xi, (b, v) in enumerate(zip(bottom, vals)):
                if v >= 2:
                    ax.text(xi, b + v / 2, str(v), ha="center", va="center", fontsize=8.5, color="#0b0b0b")
            bottom = [b + v for b, v in zip(bottom, vals)]
        ax.set_xticks(x); ax.set_xticklabels([d[5:] for d in days], fontsize=10, color=c["ink"])
        ax.set_ylabel("bugs logged", color=c["ink2"], fontsize=9.5)
        top_legend(ax, c)
        titles(fig, c, f"Who found the {s['bugs']} logged bugs, by day",
               "From bugs.csv. AI finds include cross-reviews, QA bots, test harnesses and self-checks.", extra=0.35)
        save(fig, "bug_finders", theme, c)


def fig_autonomy(prompts, ticks):
    import datetime as dt
    t0 = dt.datetime(2026, 11, 16, 0)
    hours = int((dt.datetime(2026, 11, 20, 13) - t0).total_seconds() // 3600)
    dev = [0] * hours
    for p in prompts:
        if p["source"] not in ("verbatim", "summary"):
            continue
        h = int((dt.datetime.fromisoformat(f"{p['date']} {p['time_et']}") - t0).total_seconds() // 3600)
        if 0 <= h < hours:
            dev[h] += 1
    tk = [0] * hours
    for t in ticks:
        h = int((dt.datetime.fromisoformat(t["hour_et"]) - t0).total_seconds() // 3600)
        if 0 <= h < hours:
            tk[h] += int(t["ticks"])
    for theme, c in THEMES.items():
        fig, ax = plt.subplots(figsize=(11, 3.8))
        fig.patch.set_facecolor(c["surface"]); style(ax, c, grid_axis="y")
        xs = list(range(hours))
        ax.bar([x - 0.2 for x in xs], dev, width=0.45, color=ORIGINS["dir"][theme], label="Developer prompts")
        ax.bar([x + 0.25 for x in xs], tk, width=0.45, color=ORIGINS["init"][theme], label="Claude prompting itself (20-min ticks)")
        ax.set_xticks([24 * d for d in range(5)] + [hours - 1])
        ax.set_xticklabels(["Nov 16", "Nov 17", "Nov 18", "Nov 19", "Nov 20", ""], fontsize=9.5, color=c["ink"])
        ax.set_ylabel("prompts per hour", color=c["ink2"], fontsize=9.5)
        top_legend(ax, c)
        titles(fig, c, "Who was driving, hour by hour (US Eastern time)",
               "Developer prompts to all agents vs Claude Code's self-scheduled coordinator ticks (from Nov 18).", extra=0.35)
        save(fig, "autonomy_timeline", theme, c)


def gif_growth(features, s):
    """Animated unit chart: the features appear in the order they shipped."""
    from PIL import Image, ImageDraw, ImageFont
    c = THEMES["light"]
    W = 1100
    cols, cell, gap = 42, 14, 3
    x0, y0 = 300, 96
    order = list(ORIGINS)
    reg = font_manager.findfont(font_manager.FontProperties(family="DejaVu Sans"))
    bold = font_manager.findfont(font_manager.FontProperties(family="DejaVu Sans", weight="bold"))
    f_title, f_lab, f_small = ImageFont.truetype(bold, 22), ImageFont.truetype(bold, 15), ImageFont.truetype(reg, 13)
    rows_needed = {k: max(2, math.ceil(s["origin_counts"][k] / cols)) for k in order}
    row_y, y = {}, y0
    for k in order:
        row_y[k] = y
        y += rows_needed[k] * (cell + gap) + 22
    H = y + 34
    seq = sorted(features, key=lambda f: (f["date"], int(f["fid"][1:])))
    steps = list(range(0, len(seq) + 1, 6)) + [len(seq)]
    frames = []

    def hexrgb(h):
        return tuple(int(h[i:i + 2], 16) for i in (1, 3, 5))
    for n in steps:
        im = Image.new("RGB", (W, H), hexrgb(c["surface"]))
        d = ImageDraw.Draw(im)
        shown = seq[:n]
        day = shown[-1]["date"] if shown else seq[0]["date"]
        d.text((24, 18), "Lead Rush II: who decided what", font=f_title, fill=hexrgb(c["ink"]))
        d.text((24, 50), f"Features shipped up to {day[5:].replace('-', '/')}/2026: {n} of {len(seq)}", font=f_small,
               fill=hexrgb(c["ink2"]))
        counts = Counter(f["origin"] for f in shown)
        idx = Counter()
        for f in shown:
            k = f["origin"]
            i = idx[k]; idx[k] += 1
            r, q = divmod(i, cols)
            xx, yy = x0 + q * (cell + gap), row_y[k] + r * (cell + gap)
            d.rectangle([xx, yy, xx + cell, yy + cell], fill=hexrgb(ORIGINS[k]["light"]))
        for k in order:
            d.text((24, row_y[k] - 2), ORIGINS[k]["label"], font=f_lab, fill=hexrgb(c["ink"]))
            d.text((24, row_y[k] + 18), f"{counts[k]}", font=f_small, fill=hexrgb(c["ink2"]))
        d.text((24, H - 26), "One square = one feature or fix. Data: Docs/AI_Collaboration/data/features.csv",
               font=f_small, fill=hexrgb(c["ink2"]))
        frames.append(im)
    frames += [frames[-1]] * 1
    durations = [110] * (len(frames) - 2) + [110, 3500]
    frames[0].save(os.path.join(FIG, "prov_growth.gif"), save_all=True, append_images=frames[1:], duration=durations,
                   loop=0, optimize=True)


# ---------------------------------------------------------------------------------------------------------- outputs
def esc(v):
    return (v or "").replace("|", "\\|").replace("\n", " ")


def features_page(features, s):
    agent_mark = {"Claude": "Claude", "Gemini": "Gemini", "Copilot": "Copilot", "Codex": "Codex"}
    lines = ["# Atomic feature table", "",
             "Every feature and fix in Lead Rush II, one row each, with who built it, which model, and where the idea and the design",
             "came from. The analysis and the coding rules are on [DECISION_PROVENANCE.md](DECISION_PROVENANCE.md); the same",
             "rows are in [data/features.csv](data/features.csv). Generated by `analysis/provenance.py`.", "",
             "| Mark | Origin | Meaning | Features |", "|---|---|---|---|"]
    for k, o in ORIGINS.items():
        lines.append(f"| {o['mark']} | **{o['label']}** | {o['desc']} | {s['origin_counts'][k]} |")
    lines += ["", "Outcome: **kept** as built, **changed** later at the developer's request or by a later fix, **reverted**,",
              "or **open** (not finished or not yet tested). Prompt ids (P###) point into [data/prompts.csv](data/prompts.csv);",
              "task ids into [data/tasks.csv](data/tasks.csv); bug ids into [data/bugs.csv](data/bugs.csv); B### rows are on the",
              "coordination board.", ""]
    lines.append("Areas: " + " · ".join(f"[{AREAS[a]}](#{AREAS[a].lower().replace(' ', '-')})" for a in AREAS))
    for a, label in AREAS.items():
        rows = [f for f in features if f["area"] == a]
        if not rows:
            continue
        c = Counter(f["origin"] for f in rows)
        lines += ["", f"## {label}", "",
                  f"{len(rows)} features: " + ", ".join(f"{ORIGINS[k]['mark']} {c[k]} {ORIGINS[k]['label'].lower()}"
                                                         for k in ORIGINS if c[k]), "",
                  "| # | Feature | Origin | Built by | Developer's words | What the AI decided | Outcome | Refs |",
                  "|---|---|---|---|---|---|---|---|"]
        for f in rows:
            words = f"“{esc(f['asked'])}” ({f['prompt']})" if f["asked"] else "—"
            refs = " ".join(x for x in (f["board"], f["task"], f["bug"]) if x)
            lines.append(f"| {f['fid']} | {esc(f['feature'])} | {ORIGINS[f['origin']]['mark']} {ORIGINS[f['origin']]['label']} | "
                         f"{agent_mark[f['agent']]} · `{f['model']}` | {words} | {esc(f['ai_decided']) or '—'} | "
                         f"{f['outcome']} | {esc(refs)} |")
    with open(os.path.join(ROOT, "FEATURES_TABLE.md"), "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(lines) + "\n")


def validation(features):
    path = os.path.join(DATA, "provenance_validation.csv")
    if not os.path.exists(path):
        rnd = random.Random(20261120)
        sample = []
        for k in ORIGINS:
            pool = [f for f in features if f["origin"] == k]
            sample += rnd.sample(pool, min(8, len(pool)))
        rnd.shuffle(sample)
        rows = [dict(fid=f["fid"], feature=f["feature"], developer_words=f["asked"], what_the_ai_decided=f["ai_decided"],
                     developer_label="", developer_note="") for f in sample]
        write("provenance_validation.csv", rows, ["fid", "feature", "developer_words", "what_the_ai_decided",
                                                  "developer_label", "developer_note"])
        return dict(status="pending", n=len(rows))
    rows = [r for r in read("provenance_validation.csv") if r["developer_label"].strip()]
    if len(rows) < 10:
        return dict(status="pending", n=len(read("provenance_validation.csv")), labelled=len(rows))
    lab = {f["fid"]: f["origin"] for f in features}
    a = [lab[r["fid"]] for r in rows]
    b = [r["developer_label"].strip() for r in rows]
    k, po = cohen_kappa(a, b)
    return dict(status="done", n=len(rows), kappa=round(k, 3), agreement=round(100 * po, 1))


def summary_md(s, val):
    L = ["# Decision provenance: numbers", "", "Generated by `analysis/provenance.py`. The page is "
         "[DECISION_PROVENANCE.md](../DECISION_PROVENANCE.md).", "",
         f"Features: **{s['features']}**. Developer prompts: **{s['prompts']}** ({s['prompts_verbatim']} verbatim). "
         f"Logged bugs: **{s['bugs']}**. Claude self-prompts (ticks): **{s['ticks']}**.", "",
         "| Origin | Features | Share | Kept as built (closed items) |", "|---|---|---|---|"]
    for k, o in ORIGINS.items():
        L.append(f"| {o['label']} | {s['origin_counts'][k]} | {s['origin_pct'][k]} % | {s['kept_pct'][k]['kept']} % of {s['kept_pct'][k]['n']} |")
    L += ["", "| Agent | " + " | ".join(o["label"] for o in ORIGINS.values()) + " | Total |",
          "|---|" + "---|" * (len(ORIGINS) + 1)]
    for a, v in s["by_agent"].items():
        L.append(f"| {a} | " + " | ".join(str(v[k]) for k in ORIGINS) + f" | {sum(v.values())} |")
    L += ["", "| Prompt pattern | Count | Share of task prompts |", "|---|---|---|"]
    for k in "DFIVL":
        L.append(f"| {PATTERNS[k]} | {s['pattern_counts'][k]} | {s['pattern_pct_of_task_prompts'][k]} % |")
    L.append(f"| {PATTERNS['P']} (excluded) | {s['pattern_counts']['P']} | — |")
    L += ["", f"Automation share (directive + feedback loop): **{s['automation_pct']} %**.",
          "", "| Specificity | Prompts | Share |", "|---|---|---|"]
    for k in SPECS:
        L.append(f"| {SPECS[k]} | {s['spec_counts'][k]} | {s['spec_pct'][k]} % |")
    L += ["", f"Validation sample: {val}"]
    with open(os.path.join(FIG, "provenance_summary.md"), "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(L) + "\n")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rebuild-prompts", action="store_true")
    ap.add_argument("--codes", help="CSV with pid,pattern,spec to merge into prompts.csv")
    ap.add_argument("--import-features", nargs="*", help="pipe-separated coding sheets to import into features.csv")
    args = ap.parse_args()
    os.makedirs(FIG, exist_ok=True)
    if args.import_features:
        import_features(args.import_features)
    prompts = build_prompts(args.rebuild_prompts, args.codes)
    features, bugs, ticks = read("features.csv"), read("bugs.csv"), read("autonomy_ticks.csv")
    s = compute(features, prompts, bugs, ticks)
    fig_units(features, s)
    stacked_h("prov_by_area", [(a, s["by_area"][a]) for a in AREAS if sum(s["by_area"][a].values())],
              AREAS, "Where the decisions came from, by area of the game",
              "Share of features in each area by origin. n = number of features in the area.")
    stacked_h("prov_by_agent", [(a, s["by_agent"][a]) for a in AGENTS], {}, "What each AI agent built, by origin",
              "Feature counts. Claude: claude-opus-5-5. Gemini: argon and gemini-4 variants. Copilot: gpt-5.6-luna. Codex: gpt-6.",
              share=False)
    stacked_h("prov_by_day", [(d, s["by_day"][d]) for d in s["by_day"]], {d: d[5:].replace("-", "/") for d in s["by_day"]},
              "Features shipped per day, by origin", "Nov 16 to Nov 20, 2026 (US Eastern dates).", share=False)
    fig_kept(s)
    fig_prompt_patterns(s)
    fig_automation(s)
    fig_specificity(s)
    fig_elaboration(features, prompts, s)
    fig_bugs(s)
    fig_autonomy(prompts, ticks)
    gif_growth(features, s)
    features_page(features, s)
    val = validation(features)
    s["validation"] = val
    summary_md(s, val)
    with open(os.path.join(DATA, "provenance_stats.json"), "w", encoding="utf-8", newline="\n") as fh:
        json.dump(s, fh, indent=1, ensure_ascii=False)
    print(json.dumps({k: s[k] for k in ("features", "origin_counts", "origin_pct", "goal_by_developer", "goal_by_ai",
                                        "design_by_developer", "design_by_ai", "kept_pct_all", "kept_pct",
                                        "prompts", "prompts_verbatim", "pattern_counts", "pattern_pct_of_task_prompts",
                                        "automation_pct", "spec_counts", "spec_pct", "prompt_words_median",
                                        "features_with_prompt", "prompts_with_features", "top_prompts",
                                        "ai_choices_from_short_prompts", "bugs", "bugs_found_by", "bugs_stage",
                                        "ticks", "ticks_by_day", "prompts_by_day", "by_agent", "reverted",
                                        "confidence", "validation", "by_model")}, indent=1, ensure_ascii=False))


if __name__ == "__main__":
    main()
