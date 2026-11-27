# LR2 artifacts: sources, mirrors, and how to work on them

Claude publishes these pages as private claude.ai artifacts for the developer. Other agents (Gemini, Codex) cannot open
claude.ai links, so every artifact has its source and a standalone copy in this repo. Edit the **source**; Claude republishes
it to claude.ai and rebuilds the mirror. If you change a source while Claude is away, say so on the board and Claude syncs it.

| Artifact | claude.ai (developer only) | Source (edit this) | Standalone mirror (GitHub, open in any browser) |
|---|---|---|---|
| LR2 Bot Lab: how a bot notices, decides, moves, aims, holds its gun; damage and gun specs; measured presets; how the agents built it | `Mj3oVBaf6NpdgRJSsxicPo` (v13, 11-20; the source has about 7,500 lines of unreviewed 11-24 edits, so the claude.ai page was not republished on 11-26. For the LLM bots use LR2 LLM Bots below) | `Docs/Bot/src/bot-lab.html` | `Docs/Bot/bot-lab.html` |
| LR2 LLM Architecture, 9 Tactical Maneuvers & 20 Hz Heatmap Lab: plain-English guide, 9 animated canvases w/ virtual keys, measured C1–C3 heatmaps, HLI scorecard & M1–M8 Groq study | Conversation artifact `LR2_LLM_Architecture.html` (11-24, `orchestrator-neon`) | `Docs/Bot/llm-architecture.html` (self-contained single HTML) | `Docs/Bot/llm-architecture.html` |
| LR2 Weapon IK: today's robot-driver pipeline and the 11-26 measurements (sight lock removed, hands, scope-in-eye, stance run), plus the B125 record | `A9w3NHWzNKcb5VJghzQhrg` (v4, 11-26) | `Docs/Animation/weapon-ik-report.html` | `Docs/Animation/weapon-ik-report.standalone.html` |
| LR2 LLM Bots: how the Groq squad commander works (read from the code), measured request failure rates, which study numbers hold up, 11-26 fixes and open work | `HhDwuywk5baY69hsUqXs9P` (v1, 11-26) | `Docs/Bot/src/llm-bots.html` | none yet (the source is a fragment; wrap it like the Weapon IK mirror) |
| LR2 Bot Brain (merged into the Bot Lab on 11-18) | `NrTa76HJCAAFzydVUuD2Zo` | redirect only | `Docs/Bot/bot-brain.html` |

## Rules for the sources

- A source is a page *fragment*: no `<!doctype>`, `<html>`, `<head>` or `<body>`; claude.ai wraps it at publish time. Start
  with `<title>`, then `<style>`, then the content.
- Colours are CSS tokens on `:root`, redefined under `@media (prefers-color-scheme: dark)` (guarded by
  `:root:not([data-theme="light"])`) and again under `:root[data-theme="dark"]`. No literal colours in components.
- Scripts only from cdnjs, jsDelivr or unpkg, pinned to an exact version; fonts only from Google Fonts. Everything else inline.
- Works at 400 px wide with no sideways page scroll.
- Numbers must match the game. The truth lives in `Assets/LR2/Runtime/AI/*.cs`, the presets in
  `LR2ContentBuilder.cs` (`Diff("Recruit", ...)`), weapons in `Assets/LR2/Data/Weapons/WD_*.asset`, bench results in
  `Logs/LR2_*/bench.csv`. Say where a number comes from next to it.

## Rebuild the mirrors

- Bot Lab: `python Tools/make_bot_lab_gifs.py` (rebuilds `Docs/Bot/bot-lab.html` and the GIFs; `build_standalone()` alone
  rebuilds just the page).
- Weapon IK report: the standalone copy is the source wrapped in a document skeleton (see the comment at its top).

## Test before handing back

Headless Chrome catches script errors and NaN/undefined text without anyone opening the page:

1. Copy the source to a scratch folder; prepend a script that records `error` and `unhandledrejection` events, and append
   one that clicks every button, steps every range input to min/mid/max, toggles every checkbox and select, then writes the
   recorded errors and any visible `NaN`, `undefined` or `Infinity` text into a `<pre id="__report">`.
2. `chrome.exe --headless=new --disable-gpu --virtual-time-budget=180000 --dump-dom file:///<copy>` and read `__report`.
3. For layout, screenshot isolated sections (`--screenshot`, `--window-size=1280,1800` and `400,1800`, light and dark via
   `document.documentElement.setAttribute('data-theme','dark')`) and look at them.

Bot Lab v13 passed step 1 with zero errors (11-20 02:3x).
