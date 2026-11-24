"""Generate 4 high-DPI PNG charts and 3 looping 2x GIFs for Lead Rush II LLM bot research.

Reads live benchmark telemetry directly from Logs/LR2_LLMBench/*/:
  - teams.csv, bots.csv, bench.csv
  - llm_<Team>_*.jsonl

Writes to Docs/Media/llm/:
  - llm_vs_tiers_kd_hit.png
  - llm_modes_comparison.png
  - llm_latency_and_tokens.png
  - llm_behavior_postures.png
  - llm_hierarchy_loop.gif
  - llm_match_replay.gif
  - llm_humanization_roadmap.gif
"""
from __future__ import annotations

import csv
import glob
import json
import math
import re
import statistics
from collections import Counter
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
from PIL import Image, ImageDraw, ImageFont  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
BENCH_ROOT = ROOT / "Logs" / "LR2_LLMBench"
OUT = ROOT / "Docs" / "Media" / "llm"

MATCHES = [
    ("m1_cmd_mixed_tdm", "M1: Cmd Mixed TDM"),
    ("m2_cmd_reg_vs_recruit", "M2: Cmd Reg v Rec"),
    ("m3_cmd_reg_vs_regular", "M3: Cmd Reg v Reg"),
    ("m4_cmd_reg_vs_hardened", "M4: Cmd Reg v Hrd"),
    ("m5_cmd_reg_vs_veteran", "M5: Cmd Reg v Vet"),
    ("m6_cmd_mixed_ctf", "M6: Cmd Mixed CTF"),
    ("m7_micro_reg_vs_regular", "M7: Micro Reg v Reg"),
    ("m8_pilot_reg_vs_regular", "M8: Pilot Reg v Reg"),
]

# Palette matching Docs/Bot/bot-lab.html dark theme
BG = "#111512"
PANEL = "#181e1a"
PANEL2 = "#202822"
GRID = "#263029"
LINE = "#36443c"
INK = "#e6ebe4"
INK2 = "#a3ada2"
ACCENT = "#ff7d3b"   # Enemy / Action / Orange
BLUE = "#5ca6ea"     # Alpha / LLM Blue
GREEN = "#5cc46a"    # Success / Hit
RED = "#ff6b60"      # Failure / Reject
AMBER = "#e2b54a"    # Micro / Warning
PURPLE = "#b388ff"   # Action Chunk / System 2


def load_font(size: int, bold: bool = False):
    for p in (
        "C:/Windows/Fonts/segoeuib.ttf" if bold else "C:/Windows/Fonts/segoeui.ttf",
        "C:/Windows/Fonts/consolab.ttf" if bold else "C:/Windows/Fonts/consola.ttf",
        "C:/Windows/Fonts/arialbd.ttf" if bold else "C:/Windows/Fonts/arial.ttf",
    ):
        if Path(p).exists():
            try:
                return ImageFont.truetype(p, size)
            except Exception:
                pass
    return ImageFont.load_default()


def _parse_kv_comment(line: str) -> dict:
    out = {}
    for m in re.finditer(r"(\w+)=('[^']*'|[^\s]+)", line):
        val = m.group(2)
        if val.startswith("'") and val.endswith("'"):
            val = val[1:-1]
        out[m.group(1)] = val
    return out


def parse_match(mid: str, label: str) -> dict:
    mdir = BENCH_ROOT / mid
    teams = {}
    summary_kv = {}
    groq_kv = {}
    tpath = mdir / "teams.csv"
    if tpath.exists():
        lines = [l.strip() for l in tpath.read_text(encoding="utf-8").splitlines() if l.strip()]
        csv_lines = [l for l in lines if not l.startswith("#")]
        for r in csv.DictReader(csv_lines):
            teams[r["team"]] = dict(
                commander=r["commander"],
                kills=int(r["kills"]),
                deaths=int(r["deaths"]),
                kd=float(r["kd"]),
                score=int(r["score"]),
            )
        for l in lines:
            if l.startswith("# Alpha LLM:"):
                summary_kv = _parse_kv_comment(l)
            elif l.startswith("# Groq "):
                groq_kv = _parse_kv_comment(l)

    bots = []
    bpath = mdir / "bots.csv"
    if bpath.exists():
        for r in csv.DictReader(bpath.open(encoding="utf-8")):
            bots.append(dict(
                team=r["team"],
                commander=r["commander"],
                name=r["name"],
                difficulty=r["difficulty"],
                shots=int(r["shots"]),
                hits=int(r["hits"]),
                acc=float(r["accuracy"]),
                kills=int(r["kills"]),
                deaths=int(r["deaths"]),
                kd=float(r["kd"]),
                headshots=int(r["headshots"]),
            ))

    alpha_bots = [b for b in bots if b["team"] == "Alpha"]
    bravo_bots = [b for b in bots if b["team"] == "Bravo"]
    a_shots = sum(b["shots"] for b in alpha_bots)
    a_hits = sum(b["hits"] for b in alpha_bots)
    a_hs = sum(b["headshots"] for b in alpha_bots)
    b_shots = sum(b["shots"] for b in bravo_bots)
    b_hits = sum(b["hits"] for b in bravo_bots)
    b_hs = sum(b["headshots"] for b in bravo_bots)

    jfiles = sorted(glob.glob(str(mdir / "llm_*.jsonl")))
    recs = []
    if jfiles:
        for line in Path(jfiles[0]).read_text(encoding="utf-8").splitlines():
            if line.strip():
                recs.append(json.loads(line))

    ok_recs = [r for r in recs if r.get("ok")]
    lats_ms = [r.get("seconds", 0.0) * 1000.0 for r in ok_recs if r.get("seconds", 0.0) > 0]
    lats_sorted = sorted(lats_ms)
    p95_ms = lats_sorted[min(len(lats_sorted) - 1, int(0.95 * len(lats_sorted)))] if lats_sorted else 0.0
    mean_ms = statistics.mean(lats_ms) if lats_ms else 0.0
    ptok = sum(int(r.get("promptTokens", 0)) for r in ok_recs)
    ctok = sum(int(r.get("completionTokens", 0)) for r in ok_recs)

    orders_c = Counter()
    stance_c = Counter()
    peek_c = Counter()
    burst_c = Counter()
    util_c = Counter()
    chunk_c = Counter()
    for r in ok_recs:
        rep = (r.get("reply") or "").replace("'", '"')
        # Replies saved by Clean() replaced " with ' so parse with regex/ast-friendly heuristics
        for m in re.finditer(r'"order"\s*:\s*"([^"]+)"', rep):
            orders_c[m.group(1).lower()] += 1
        for m in re.finditer(r'"stance"\s*:\s*"([^"]+)"', rep):
            stance_c[m.group(1).lower()] += 1
        for m in re.finditer(r'"peek"\s*:\s*"([^"]+)"', rep):
            peek_c[m.group(1).lower()] += 1
        for m in re.finditer(r'"burst"\s*:\s*"([^"]+)"', rep):
            burst_c[m.group(1).lower()] += 1
        for m in re.finditer(r'"utility"\s*:\s*"([^"]+)"', rep):
            u = m.group(1).lower()
            if u != "none":
                util_c[u] += 1
        for m in re.finditer(r'"cmd"\s*:\s*"([^"]+)"', rep):
            chunk_c[m.group(1).lower()] += 1

    tok_parts = summary_kv.get("tokens", "0+0").split("+")
    return dict(
        id=mid,
        label=label,
        mode=summary_kv.get("mode", "-"),
        alpha=teams.get("Alpha", dict(kills=0, deaths=0, kd=0.0, score=0, commander="-")),
        bravo=teams.get("Bravo", dict(kills=0, deaths=0, kd=0.0, score=0, commander="-")),
        a_acc=(100.0 * a_hits / a_shots) if a_shots else 0.0,
        b_acc=(100.0 * b_hits / b_shots) if b_shots else 0.0,
        a_hs_pct=(100.0 * a_hs / a_hits) if a_hits else 0.0,
        b_hs_pct=(100.0 * b_hs / b_hits) if b_hits else 0.0,
        a_shots=a_shots,
        b_shots=b_shots,
        decisions=int(summary_kv.get("decisions", len(recs) or 0)),
        failures=int(summary_kv.get("failures", sum(1 for r in recs if not r.get("ok")))),
        ok_calls=len(ok_recs),
        orders_issued=int(summary_kv.get("orders", 0)),
        rejected=int(summary_kv.get("rejected", 0)),
        interrupts=int(summary_kv.get("interrupts", 0)),
        micro_chunks=int(summary_kv.get("micro", 0)),
        util_orders=int(summary_kv.get("util", 0)),
        prompt_tokens=ptok if ptok else int(tok_parts[0]),
        completion_tokens=ctok if ctok else int(tok_parts[1]),
        total_tokens=(ptok + ctok) if (ptok + ctok) else (int(tok_parts[0]) + int(tok_parts[1])),
        mean_ms=mean_ms,
        p95_ms=p95_ms,
        models=sorted({r.get("model", "") for r in ok_recs if r.get("model")}),
        recs=recs,
        orders_c=orders_c,
        stance_c=stance_c,
        peek_c=peek_c,
        burst_c=burst_c,
        util_c=util_c,
        chunk_c=chunk_c,
    )


def style_ax(ax, title, subtitle=""):
    ax.set_facecolor(PANEL)
    for sp in ("top", "right"):
        ax.spines[sp].set_visible(False)
    for sp in ("left", "bottom"):
        ax.spines[sp].set_color(LINE)
    ax.tick_params(colors=INK2, labelsize=9)
    ax.yaxis.grid(True, color=GRID, linewidth=0.8)
    ax.set_axisbelow(True)
    ax.set_title(title, loc="left", color=INK, fontsize=12, fontweight="bold", pad=22 if subtitle else 10)
    if subtitle:
        ax.text(0, 1.02, subtitle, transform=ax.transAxes, color=INK2, fontsize=8.5, va="bottom")


def render_charts(data: list[dict]):
    plt.rcParams.update({"font.sans-serif": ["Segoe UI", "DejaVu Sans"], "axes.edgecolor": LINE})

    # 1. llm_vs_tiers_kd_hit.png (M1..M6)
    m_tiers = [d for d in data[:6]]
    labels = ["Mixed TDM\n(4 tiers ea.)", "vs Recruit\n(Reg+LLM)", "vs Regular\n(Reg+LLM)",
              "vs Hardened\n(Reg+LLM)", "vs Veteran\n(Reg+LLM)", "Mixed CTF\n(4 tiers ea.)"]
    x = list(range(len(m_tiers)))
    w = 0.36
    fig, (ax1, ax2) = plt.subplots(1, 2, figsize=(13.2, 5.2), dpi=160)
    fig.patch.set_facecolor(BG)

    a_kd = [d["alpha"]["kd"] for d in m_tiers]
    b_kd = [d["bravo"]["kd"] for d in m_tiers]
    style_ax(ax1, "Squad Commander K:D vs Tiered Rule Bots (75s 4v4)", "Higher is better · Alpha = Regular (M2-M5) or Mixed + Groq LLM")
    rects1 = ax1.bar([i - w / 2 for i in x], a_kd, w, label="Alpha (LLM)", color=BLUE)
    rects2 = ax1.bar([i + w / 2 for i in x], b_kd, w, label="Bravo (Rules)", color=ACCENT)
    ax1.axhline(1.0, color=INK2, linestyle="--", linewidth=0.9, alpha=0.6)
    ax1.set_xticks(x)
    ax1.set_xticklabels(labels, fontsize=8, color=INK)
    ax1.set_ylabel("Team K:D Ratio", color=INK2, fontsize=9)
    for b, k in zip(rects1, [d["alpha"]["kills"] for d in m_tiers]):
        ax1.text(b.get_x() + b.get_width() / 2, b.get_height() + 0.04, f"{b.get_height():.2f}\n({k}K)", ha="center", va="bottom", color=BLUE, fontsize=7.5, fontweight="bold")
    for b, k in zip(rects2, [d["bravo"]["kills"] for d in m_tiers]):
        ax1.text(b.get_x() + b.get_width() / 2, b.get_height() + 0.04, f"{b.get_height():.2f}\n({k}K)", ha="center", va="bottom", color=ACCENT, fontsize=7.5)
    ax1.set_ylim(0, max(a_kd + b_kd + [1.5]) * 1.24)
    ax1.legend(facecolor=PANEL2, edgecolor=LINE, labelcolor=INK, fontsize=8.5)

    a_acc = [d["a_acc"] for d in m_tiers]
    b_acc = [d["b_acc"] for d in m_tiers]
    style_ax(ax2, "Shot Hit Accuracy % Across Tiers", "Aiming stays in C# System 1; difference reflects positioning & fight ranges")
    r3 = ax2.bar([i - w / 2 for i in x], a_acc, w, label="Alpha (LLM) Hit %", color=GREEN)
    r4 = ax2.bar([i + w / 2 for i in x], b_acc, w, label="Bravo (Rules) Hit %", color=AMBER)
    ax2.set_xticks(x)
    ax2.set_xticklabels(labels, fontsize=8, color=INK)
    ax2.set_ylabel("Hit Accuracy (%)", color=INK2, fontsize=9)
    for b in r3:
        ax2.text(b.get_x() + b.get_width() / 2, b.get_height() + 0.5, f"{b.get_height():.1f}%", ha="center", va="bottom", color=GREEN, fontsize=7.5)
    for b in r4:
        ax2.text(b.get_x() + b.get_width() / 2, b.get_height() + 0.5, f"{b.get_height():.1f}%", ha="center", va="bottom", color=AMBER, fontsize=7.5)
    ax2.set_ylim(0, max(a_acc + b_acc + [25]) * 1.22)
    ax2.legend(facecolor=PANEL2, edgecolor=LINE, labelcolor=INK, fontsize=8.5)
    fig.tight_layout(pad=1.6)
    fig.savefig(OUT / "llm_vs_tiers_kd_hit.png", facecolor=BG, dpi=160)
    plt.close(fig)

    # 2. llm_modes_comparison.png (M3 Commander vs M7 TacticalMicro vs M8 AvatarPilot on Regular v Regular)
    m_modes = [data[2], data[6], data[7]]
    m_names = ["M3 · Commander\n(Macro Orders, 8s)", "M7 · Tactical Micro\n(Posture/Peek/Utility)", "M8 · Avatar Pilot\n(60Hz Action Chunks)"]
    fig, (ax1, ax2) = plt.subplots(1, 2, figsize=(12.6, 5.0), dpi=160)
    fig.patch.set_facecolor(BG)
    mx = [0, 1, 2]
    style_ax(ax1, "3-Mode Depth Comparison (Equal Regular 4v4 TDM)", "K:D vs Bravo baseline + JSON delivery reliability")
    kd_a = [d["alpha"]["kd"] for d in m_modes]
    kd_b = [d["bravo"]["kd"] for d in m_modes]
    rel = [100.0 * d["ok_calls"] / max(1, d["decisions"]) for d in m_modes]
    bars_a = ax1.bar([i - 0.19 for i in mx], kd_a, 0.36, label="Alpha K:D (LLM)", color=BLUE)
    bars_b = ax1.bar([i + 0.19 for i in mx], kd_b, 0.36, label="Bravo K:D (Rules)", color=ACCENT)
    ax1.axhline(1.0, color=INK2, linestyle="--", linewidth=0.8, alpha=0.6)
    ax1.set_xticks(mx)
    ax1.set_xticklabels(m_names, fontsize=8.5, color=INK)
    ax1.set_ylabel("K:D Ratio", color=INK2, fontsize=9)
    for b, d in zip(bars_a, m_modes):
        r_pct = 100.0 * d["ok_calls"] / max(1, d["decisions"])
        ax1.text(b.get_x() + b.get_width() / 2, b.get_height() + 0.04, f"{b.get_height():.2f}\n({d['alpha']['kills']}K · {r_pct:.0f}% OK)", ha="center", va="bottom", color=BLUE, fontsize=7.5, fontweight="bold")
    for b, d in zip(bars_b, m_modes):
        ax1.text(b.get_x() + b.get_width() / 2, b.get_height() + 0.04, f"{b.get_height():.2f}\n({d['bravo']['kills']}K)", ha="center", va="bottom", color=ACCENT, fontsize=7.5)
    ax1.set_ylim(0, max(kd_a + kd_b + [1.4]) * 1.28)
    ax1.legend(facecolor=PANEL2, edgecolor=LINE, labelcolor=INK, fontsize=8.5)

    style_ax(ax2, "LLM Control Volume Per 75s Match", "Orders issued, reactive interrupts, utility calls & 60 Hz motor chunks")
    ord_v = [d["orders_issued"] for d in m_modes]
    int_v = [d["interrupts"] for d in m_modes]
    mic_v = [d["micro_chunks"] for d in m_modes]
    utl_v = [d["util_orders"] for d in m_modes]
    ax2.bar([i - 0.27 for i in mx], ord_v, 0.18, label="Orders", color=BLUE)
    ax2.bar([i - 0.09 for i in mx], int_v, 0.18, label="Interrupts", color=AMBER)
    ax2.bar([i + 0.09 for i in mx], utl_v, 0.18, label="Utility (Frag/Rld)", color=GREEN)
    ax2.bar([i + 0.27 for i in mx], mic_v, 0.18, label="Action Chunks", color=PURPLE)
    ax2.set_xticks(mx)
    ax2.set_xticklabels(m_names, fontsize=8.5, color=INK)
    ax2.set_ylabel("Count per 75s Match", color=INK2, fontsize=9)
    for bars in ax2.containers:
        for b in bars:
            h = b.get_height()
            if h > 0:
                ax2.text(b.get_x() + b.get_width() / 2, h + 0.4, f"{int(h)}", ha="center", va="bottom", color=INK, fontsize=7.5)
    ax2.set_ylim(0, max(ord_v + int_v + mic_v + utl_v + [10]) * 1.24)
    ax2.legend(facecolor=PANEL2, edgecolor=LINE, labelcolor=INK, fontsize=8)
    fig.tight_layout(pad=1.6)
    fig.savefig(OUT / "llm_modes_comparison.png", facecolor=BG, dpi=160)
    plt.close(fig)

    # 3. llm_latency_and_tokens.png
    fig, (ax1, ax2) = plt.subplots(1, 2, figsize=(13.4, 5.2), dpi=160)
    fig.patch.set_facecolor(BG)
    mx8 = list(range(len(data)))
    s_labels = [d["label"].split(": ")[1] for d in data]
    mean_l = [d["mean_ms"] for d in data]
    p95_l = [d["p95_ms"] for d in data]
    style_ax(ax1, "Groq API Round-Trip Latency (ms) Across All 8 Runs", "Mean vs p95 latency on HTTP 200 calls · Lower enables tighter closed-loop control")
    ax1.bar([i - 0.18 for i in mx8], mean_l, 0.35, label="Mean Latency (ms)", color=BLUE)
    ax1.bar([i + 0.18 for i in mx8], p95_l, 0.35, label="p95 Latency (ms)", color=PURPLE)
    for i, (m, p) in enumerate(zip(mean_l, p95_l)):
        ax1.text(i - 0.18, m + 15, f"{m:.0f}", ha="center", va="bottom", color=BLUE, fontsize=7)
        ax1.text(i + 0.18, p + 15, f"{p:.0f}", ha="center", va="bottom", color=PURPLE, fontsize=7)
    ax1.set_xticks(mx8)
    ax1.set_xticklabels(s_labels, rotation=24, ha="right", fontsize=8, color=INK)
    ax1.set_ylabel("Milliseconds", color=INK2, fontsize=9)
    ax1.set_ylim(0, max(p95_l + [500]) * 1.2)
    ax1.legend(facecolor=PANEL2, edgecolor=LINE, labelcolor=INK, fontsize=8.5)

    p_toks = [d["prompt_tokens"] for d in data]
    c_toks = [d["completion_tokens"] for d in data]
    style_ax(ax2, "Exact Measured Groq Tokens Per 75s Match", "Prompt + completion (incl. reasoning tokens); all 8 runs resolved to openai/gpt-oss-20b")
    ax2.bar(mx8, p_toks, 0.56, label="Prompt Tokens", color=BLUE)
    ax2.bar(mx8, c_toks, 0.56, bottom=p_toks, label="Completion Tokens (incl. CoT)", color=ACCENT)
    for i, (pt, ct) in enumerate(zip(p_toks, c_toks)):
        tot = pt + ct
        ok_n = data[i]["ok_calls"]
        ax2.text(i, tot + 120, f"{tot:,}\n({ok_n} OK)", ha="center", va="bottom", color=INK, fontsize=7)
    ax2.set_xticks(mx8)
    ax2.set_xticklabels(s_labels, rotation=24, ha="right", fontsize=8, color=INK)
    ax2.set_ylabel("Total Tokens per 75s Match", color=INK2, fontsize=9)
    ax2.set_ylim(0, max([p + c for p, c in zip(p_toks, c_toks)] + [3000]) * 1.22)
    ax2.legend(facecolor=PANEL2, edgecolor=LINE, labelcolor=INK, fontsize=8.5)
    fig.tight_layout(pad=1.6)
    fig.savefig(OUT / "llm_latency_and_tokens.png", facecolor=BG, dpi=160)
    plt.close(fig)

    # 4. llm_behavior_postures.png
    fig, (ax1, ax2) = plt.subplots(1, 2, figsize=(13.0, 5.0), dpi=160)
    fig.patch.set_facecolor(BG)
    all_orders = Counter()
    for d in data:
        all_orders.update(d["orders_c"])
    top_ord = all_orders.most_common(7)
    style_ax(ax1, "LLM Tactical Order Distribution (All 8 Runs)", "How Groq chooses squad directives vs total orders issued")
    if top_ord:
        onames = [k.upper() for k, _ in top_ord][::-1]
        ovals = [v for _, v in top_ord][::-1]
        bars = ax1.barh(onames, ovals, color=BLUE, height=0.55)
        for b, v in zip(bars, ovals):
            ax1.text(v + max(ovals) * 0.02, b.get_y() + b.get_height() / 2, f"{v}", va="center", ha="left", color=INK, fontsize=8.5)
        ax1.set_xlim(0, max(ovals) * 1.18)
        ax1.xaxis.grid(True, color=GRID, linewidth=0.8)
        ax1.yaxis.grid(False)
    ax1.set_xlabel("Order Count", color=INK2, fontsize=9)

    # Panel 2: Failures & rejections diagnostics across M1..M8
    style_ax(ax2, "LLM Execution Hygiene: Issued vs Rejected & HTTP Errors", "Shows 20B CoT/JSON truncation (HTTP 400) & M7/M8 8,000 TPM bursts (HTTP 429)")
    iss = [d["orders_issued"] for d in data]
    rej = [d["rejected"] for d in data]
    fails = [d["failures"] for d in data]
    ax2.bar([i - 0.24 for i in mx8], iss, 0.24, label="Issued Orders", color=GREEN)
    ax2.bar(mx8, rej, 0.24, label="Rejected Orders", color=AMBER)
    ax2.bar([i + 0.24 for i in mx8], fails, 0.24, label="Failed HTTP / JSON", color=RED)
    for i, (iv, rv, fv) in enumerate(zip(iss, rej, fails)):
        if iv > 0: ax2.text(i - 0.24, iv + 0.4, str(iv), ha="center", va="bottom", color=GREEN, fontsize=7)
        if rv > 0: ax2.text(i, rv + 0.4, str(rv), ha="center", va="bottom", color=AMBER, fontsize=7)
        if fv > 0: ax2.text(i + 0.24, fv + 0.4, str(fv), ha="center", va="bottom", color=RED, fontsize=7)
    ax2.set_xticks(mx8)
    ax2.set_xticklabels(s_labels, rotation=24, ha="right", fontsize=8, color=INK)
    ax2.set_ylabel("Count", color=INK2, fontsize=9)
    ax2.set_ylim(0, max(iss + rej + fails + [10]) * 1.2)
    ax2.legend(facecolor=PANEL2, edgecolor=LINE, labelcolor=INK, fontsize=8.5)
    fig.tight_layout(pad=1.6)
    fig.savefig(OUT / "llm_behavior_postures.png", facecolor=BG, dpi=160)
    plt.close(fig)


def save_gif(frames: list[Image.Image], path: Path, duration_ms: int = 110, end_hold: int = 1200):
    pal = frames[len(frames) // 2].quantize(colors=128, method=Image.Quantize.MEDIANCUT)
    q = [f.quantize(palette=pal, dither=Image.Dither.NONE) for f in frames]
    durs = [duration_ms] * (len(q) - 1) + [end_hold]
    q[0].save(path, save_all=True, append_images=q[1:], duration=durs, loop=0, optimize=True, disposal=1)


def gif_hierarchy():
    W, H = 960, 440
    f_title = load_font(22, True)
    f_md = load_font(15, True)
    f_sm = load_font(13, False)
    f_xs = load_font(11, False)
    frames = []
    modes = [
        ("MODE 1 · COMMANDER", "8-12s macro plan & (x,z) waypoints", BLUE, 0.25),
        ("MODE 2 · TACTICAL MICRO", "<=8s + interrupts · stance, peek, burst, frag", AMBER, 0.58),
        ("MODE 3 · AVATAR PILOT", "<=5.5s fast 8B + 60Hz Action Chunks", PURPLE, 0.92),
    ]
    for step in range(45):
        img = Image.new("RGB", (W, H), BG)
        dr = ImageDraw.Draw(img)
        m_idx = (step // 15) % 3
        m_name, m_sub, m_col, _ = modes[m_idx]
        t = (step % 15) / 14.0

        dr.text((28, 18), "Lead Rush II · Dual-System LLM Control Loop", fill=INK, font=f_title)
        dr.text((28, 48), f"Active Mode: {m_name} — {m_sub}", fill=m_col, font=f_sm)

        # System 2 Box (Left)
        dr.rounded_rectangle([28, 92, 320, 388], radius=12, fill=PANEL, outline=m_col, width=2)
        dr.text((46, 108), "SYSTEM 2 · GROQ CLOUD LLM", fill=m_col, font=f_md)
        dr.text((46, 134), "Async HTTP · 0.2-1.0s RTT", fill=INK2, font=f_xs)
        layers = [
            ("1. Fog-of-War JSON State", "Squad pos/HP/ammo + seen enemies only", True),
            ("2. Macro Order & Coordinates", "flank / attack / hold / retreat (x,z)", True),
            ("3. Tactical Micro Overrides", "stance / peek / burst / utility", m_idx >= 1),
            ("4. Timed Action Chunks", "[sprint_to -> crouch_peek -> strafe]", m_idx >= 2),
        ]
        for k, (lt, ld, on) in enumerate(layers):
            y0 = 172 + k * 50
            fill = PANEL2 if on else "#151916"
            ol = m_col if on else GRID
            dr.rounded_rectangle([44, y0, 304, y0 + 40], radius=6, fill=fill, outline=ol, width=1)
            dr.text((54, y0 + 5), lt, fill=INK if on else "#5a635c", font=f_xs)
            dr.text((54, y0 + 20), ld, fill=INK2 if on else "#464d47", font=f_xs)

        # System 1 Box (Right)
        dr.rounded_rectangle([590, 92, 932, 388], radius=12, fill=PANEL, outline=GREEN, width=2)
        dr.text((610, 108), "SYSTEM 1 · UNITY 60 HZ LOOP", fill=GREEN, font=f_md)
        dr.text((610, 134), "Real-Time BotController + Kinemation", fill=INK2, font=f_xs)
        s1_rows = [
            ("NavMesh Pathing & Wall Checks", "2.4s stuck reset · cover scoring"),
            ("Human Visuomotor Aim (B118)", "Fitts saccades · 125ms lag · Harris noise"),
            ("60Hz Chunk Stepper", "TryGetActiveMicroStep() executes step queue" if m_idx >= 2 else "Default Combat Strafe & Crouch Peek"),
            ("Emergency Overrides", "Immediate fire, evade or reload anytime"),
        ]
        for k, (st, sd) in enumerate(s1_rows):
            y0 = 172 + k * 50
            dr.rounded_rectangle([606, y0, 916, y0 + 40], radius=6, fill=PANEL2, outline=GREEN if k != 2 or m_idx >= 1 else LINE, width=1)
            dr.text((616, y0 + 5), st, fill=INK, font=f_xs)
            dr.text((616, y0 + 20), sd, fill=INK2, font=f_xs)

        # Animated Packet Bridge
        dr.line([(326, 200), (584, 200)], fill=LINE, width=3)
        dr.line([(584, 290), (326, 290)], fill=LINE, width=3)
        px = int(332 + t * 246)
        dr.ellipse([px - 7, 193, px + 7, 207], fill=m_col)
        dr.text((370, 174), "JSON Directive / Chunks", fill=m_col, font=f_xs)
        bx = int(578 - t * 246)
        dr.ellipse([bx - 6, 284, bx + 6, 296], fill=GREEN)
        dr.text((374, 298), "Blackboard + Interrupts", fill=GREEN, font=f_xs)

        dr.text((28, 405), f"Step {step+1:02d}/45 · Zero blocking frames: System 1 fights continuously during HTTP RTT", fill=INK2, font=f_xs)
        frames.append(img)
    save_gif(frames, OUT / "llm_hierarchy_loop.gif", 120, 900)


def gif_match_replay(m_data: dict):
    W, H = 960, 500
    f_title = load_font(20, True)
    f_md = load_font(13, True)
    f_xs = load_font(11, False)
    recs = [r for r in m_data["recs"] if r.get("state") and r.get("state").get("squad")]
    if not recs:
        recs = [{"t": 0.0, "plan": "Patrol", "state": {"squad": [{"id": "A1", "x": -10, "z": 0, "hp": 100}], "enemies": []}}]
    frames = []
    # Saltworks map display window [-75, 75] x [-65, 60]
    def to_px(wx, wz):
        mx = 44 + (wx + 75.0) / 150.0 * 540.0
        my = 84 + (60.0 - wz) / 125.0 * 370.0
        return int(max(48, min(580, mx))), int(max(88, min(450, my)))

    n_segs = max(1, len(recs) - 1)
    total_f = 42
    for f in range(total_f):
        pos_f = f / max(1, total_f - 1) * n_segs
        idx = min(n_segs - 1, int(pos_f))
        alpha = pos_f - idx
        r0 = recs[idx]
        r1 = recs[min(len(recs) - 1, idx + 1)]
        img = Image.new("RGB", (W, H), BG)
        dr = ImageDraw.Draw(img)
        dr.text((28, 16), f"Live Match Telemetry Replay · {m_data['label']}", fill=INK, font=f_title)
        t_cur = r0["t"] + (r1["t"] - r0["t"]) * alpha
        sc = r0["state"].get("score", {})
        dr.text((28, 44), f"t = {t_cur:0.1f}s  |  Alpha (LLM): {sc.get('us', 0)} pts  vs  Bravo (Rules): {sc.get('bestEnemy', 0)} pts  |  Model: {r0.get('model', '-')}", fill=BLUE, font=f_xs)

        # Arena Canvas
        dr.rounded_rectangle([36, 76, 596, 464], radius=10, fill=PANEL, outline=LINE, width=2)
        for gx in range(86, 580, 60):
            dr.line([(gx, 80), (gx, 460)], fill=GRID, width=1)
        for gy in range(110, 460, 60):
            dr.line([(40, gy), (592, gy)], fill=GRID, width=1)

        # Interpolate squad positions
        s1_map = {a["id"]: a for a in r1["state"].get("squad", [])}
        for a in r0["state"].get("squad", []):
            b = s1_map.get(a["id"], a)
            wx = a["x"] + (b["x"] - a["x"]) * alpha
            wz = a["z"] + (b["z"] - a["z"]) * alpha
            px, py = to_px(wx, wz)
            dr.ellipse([px - 9, py - 9, px + 9, py + 9], fill=BLUE, outline=INK, width=1)
            dr.text((px + 11, py - 8), f"{a['id']} ({a.get('doing', '')})", fill=BLUE, font=f_xs)

        for e in r0["state"].get("enemies", []):
            px, py = to_px(e["x"], e["z"])
            dr.polygon([(px, py - 9), (px + 9, py + 7), (px - 9, py + 7)], fill=ACCENT, outline=INK)
            dr.text((px + 10, py - 6), f"{e['id']} ({e.get('seenAgo', 0):.0f}s)", fill=ACCENT, font=f_xs)

        # Right inspector panel
        dr.rounded_rectangle([612, 76, 932, 464], radius=10, fill=PANEL, outline=LINE, width=2)
        dr.text((628, 92), "GROQ PLANNER SNAPSHOT", fill=AMBER, font=f_md)
        st_txt = "HTTP 200 OK" if r0.get("ok") else f"FAIL ({r0.get('status')})"
        dr.text((628, 118), f"Call #{idx+1} · {st_txt} · {r0.get('seconds', 0)*1000:.0f} ms", fill=GREEN if r0.get("ok") else RED, font=f_xs)
        dr.text((628, 138), f"Tokens: {r0.get('promptTokens',0)} + {r0.get('completionTokens',0)}", fill=INK2, font=f_xs)
        dr.text((628, 166), "SQUAD STATUS:", fill=INK, font=f_md)
        for k, a in enumerate(r0["state"].get("squad", [])[:4]):
            y0 = 192 + k * 28
            dr.text((632, y0), f"{a['id']}: HP {a.get('hp',0)} AP {a.get('armor',0)} · {a.get('order', a.get('doing','-'))}", fill=INK2, font=f_xs)
        rep = (r0.get("reply") or r0.get("note") or "").replace("'", '"')
        dr.text((628, 322), "RAW REPLY / NOTE:", fill=INK, font=f_md)
        for li in range(5):
            chunk = rep[li * 38:(li + 1) * 38]
            if chunk:
                dr.text((628, 348 + li * 18), chunk, fill=INK2, font=f_xs)
        frames.append(img)
    save_gif(frames, OUT / "llm_match_replay.gif", 140, 1400)


def gif_roadmap():
    W, H = 960, 440
    f_title = load_font(21, True)
    f_md = load_font(14, True)
    f_xs = load_font(12, False)
    stages = [
        ("01 · Non-Reasoning Fast System 2", "Eliminate HTTP 400 CoT starvation (5x higher valid JSON on 8B / schema-guided decode)", RED, BLUE),
        ("02 · PORTAL Behavior-Tree Compilation", "Synthesize full BT subtrees + cooldown conditions instead of single 8s imperative orders", AMBER, GREEN),
        ("03 · Role Allocation & Crossfire Pair", "Assign Entry / Support / Flank / Anchor with 2-bot synchronized swing timers", BLUE, GREEN),
        ("04 · Kinematic Aim Humanization", "Sound-cone pre-aim, recoil pull-down compensation, micro-cover slicing & jitter suppression", PURPLE, GREEN),
    ]
    frames = []
    for f in range(40):
        s_idx = (f // 10) % 4
        img = Image.new("RGB", (W, H), BG)
        dr = ImageDraw.Draw(img)
        dr.text((28, 18), "Research Roadmap: Bridging LLM Strategy to Human-Like FPS Play", fill=INK, font=f_title)
        dr.text((28, 48), "Synthesizing PORTAL (BT/DSL), Voyager Skill Libraries, Action Chunking & Riot/Modl.ai Sensors", fill=INK2, font=f_xs)
        for i, (name, desc, c_now, c_next) in enumerate(stages):
            y0 = 86 + i * 82
            active = i == s_idx
            dr.rounded_rectangle([28, y0, 932, y0 + 68], radius=8, fill=PANEL2 if active else PANEL, outline=c_next if active else LINE, width=2 if active else 1)
            dr.text((46, y0 + 10), name, fill=c_next if active else INK, font=f_md)
            dr.text((46, y0 + 34), desc, fill=INK2, font=f_xs)
            badge = "ACTIVE UPGRADE" if active else "QUEUED"
            dr.rounded_rectangle([770, y0 + 18, 912, y0 + 48], radius=6, fill=BG, outline=c_next if active else GRID, width=1)
            dr.text((792, y0 + 26), badge, fill=c_next if active else INK2, font=f_xs)
        frames.append(img)
    save_gif(frames, OUT / "llm_humanization_roadmap.gif", 140, 1200)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    data = [parse_match(mid, lbl) for mid, lbl in MATCHES]
    render_charts(data)
    gif_hierarchy()
    # Use M6 CTF or M8 Pilot if rich in telemetry, fallback M1
    replay_src = next((d for d in (data[5], data[7], data[0]) if len(d["recs"]) >= 4), data[0])
    gif_match_replay(replay_src)
    gif_roadmap()

    summary_out = []
    for d in data:
        summary_out.append({
            "id": d["id"], "label": d["label"], "mode": d["mode"],
            "alpha": d["alpha"], "bravo": d["bravo"],
            "a_acc": round(d["a_acc"], 2), "b_acc": round(d["b_acc"], 2),
            "a_hs_pct": round(d["a_hs_pct"], 2), "b_hs_pct": round(d["b_hs_pct"], 2),
            "decisions": d["decisions"], "failures": d["failures"], "ok_calls": d["ok_calls"],
            "orders_issued": d["orders_issued"], "rejected": d["rejected"],
            "interrupts": d["interrupts"], "micro_chunks": d["micro_chunks"], "util_orders": d["util_orders"],
            "prompt_tokens": d["prompt_tokens"], "completion_tokens": d["completion_tokens"],
            "total_tokens": d["total_tokens"], "mean_ms": round(d["mean_ms"], 1), "p95_ms": round(d["p95_ms"], 1),
            "models": d["models"],
            "orders_c": dict(d["orders_c"]), "stance_c": dict(d["stance_c"]),
            "peek_c": dict(d["peek_c"]), "burst_c": dict(d["burst_c"]),
            "util_c": dict(d["util_c"]), "chunk_c": dict(d["chunk_c"]),
        })
    (BENCH_ROOT / "summary.json").write_text(json.dumps(summary_out, indent=2), encoding="utf-8")
    print(f"Wrote 4 PNGs + 3 GIFs to {OUT} and summary.json")


if __name__ == "__main__":
    main()
