# Research data: which model produces what quality of work

Two hand-maintained ledgers feed [`../analysis/analyze.py`](../analysis/analyze.py), which writes the charts in
[`../figures/`](../figures/). Agents append a row whenever they finish an item (`tasks.csv`) or cause or find a defect
(`bugs.csv`); see rule 7 and request R9 on the board. Rows before 2026-11-17 23:25 were reconstructed afterwards from the
developer's prompts, the board archive and git; their `source` says so.

## tasks.csv

| Column | Meaning |
|---|---|
| `id` | Unique ID (`C..` Claude Code, `G..` Antigravity/"Gemini", `P..` Copilot, `X..` Codex) |
| `when` | Local time (America/New_York) the work or task state was recorded, `YYYY-MM-DD HH:MM` |
| `agent` | Board name: Claude, Gemini, Copilot, Codex |
| `model` | Exact model id, or the developer's name for it (e.g. `argon`), or `unknown` |
| `title` | What was built or fixed |
| `kind` | feature, fix, tune, tooling, docs, review |
| `board_id` | Tracker ID (B.., P..) if any |
| `outcome` | verified, reopened, ready-for-review, open, in-progress, partial, delivered (pre-tracker work that shipped without review) |
| `source` | Where the row comes from (board row, log time, prompt time, reconstruction) |
| `difficulty` | `small`, `standard`, `hard`, or `unrated` |
| `difficulty_timing` | `retrospective-peer-consensus` for the audited historic rows; `pre-work-peer-consensus` for future ratings made before implementation |
| `difficulty_basis` | Consensus score and short evidence note based on scope/integration, uncertainty, and verification burden |

`open` and `in-progress` rows are pending and excluded from delivered-task counts and completion credit. `partial` records work that shipped only in part; it remains a counted task row but earns no completion credit until it reaches a completed outcome.

Difficulty ratings use the rubric in [`../specs/B66_Task_Difficulty.md`](../specs/B66_Task_Difficulty.md). At the developer's request, three independent peer reviews rated the existing task definitions retrospectively; the median total sets the band. [`task_difficulty_reviews.csv`](task_difficulty_reviews.csv) preserves their dimension scores and consensus. These ratings estimate requested scope, not actual effort. Future tasks should be rated before implementation. Any missing or `unrated` value receives the standard 1x multiplier.

## task_difficulty_reviews.csv

One row per existing task, with the three independent reviewers' scope, uncertainty, and verification scores; their total/band; and the median-total consensus used by `tasks.csv`. `underspecified_votes` records how many reviewers flagged the surviving task request as incomplete. Reviewer labels A/B/C do not identify implementers.

## bugs.csv

| Column | Meaning |
|---|---|
| `id` | Unique ID |
| `found` | Local time it was reported |
| `found_by` | user, qa-bot, claude-review, copilot-review, gemini-review, codex-review, push-check, claude-self |
| `found_by_agent`, `found_by_model` | Who gets the credit for finding it (empty when the developer found it) |
| `stage` | **escaped** (found after delivery), **review** (caught by another agent before the developer saw it), **pre-push** (broken build stopped before a push) |
| `description` | Short description |
| `caused_by_task` | `id` from tasks.csv |
| `caused_by_agent`, `caused_by_model` | Copied from that task |
| `confidence` | high, medium, low: how sure the attribution is |
| `status` | open, fixed |

## suggestions.csv

Ideas that came from an agent, not from the developer (features, tools, process changes).

| Column | Meaning |
|---|---|
| `id`, `when`, `agent`, `model` | As above |
| `suggestion` | The idea |
| `kind` | feature, tooling, process |
| `adopted` | yes, no, pending |
| `board_id` | Tracker row it became, if any |

## Contribution score

The developer's rule (2026-11-18): **finding new bugs and suggesting meaningful features should score best.** `analyze.py` scores each agent
and model: +3 per defect found in another agent's work before the developer saw it, +2 per defect found in its own work, +3 per adopted
suggestion, +1 per pending suggestion, +1 per verified task, +0.5 per task ready for review, -2 per escaped defect (confidence-weighted),
-1 per pre-push break, -0.5 per own defect caught at review.

## Who may edit a defect row

Only the finder (or the reviewer) writes a defect's `found_by`, `stage` and attribution. The agent that caused it may add context in its log entry but must not change those fields; disagreements go to the reviewer or the developer. (Added 2026-11-18 after an agent re-filed a defect the developer had seen as its own pre-push catch.)

If the source task or cause is not established, leave its cause fields blank and explain the uncertainty in `description`. `analysis.py` skips the unknown-cause penalty instead of inventing a blank-agent score row; a named finder can still receive review credit when the review stage is known.

## features.csv (decision provenance)

One row per atomic feature or fix (270 rows), coded for [`../DECISION_PROVENANCE.md`](../DECISION_PROVENANCE.md) and rendered as
[`../FEATURES_TABLE.md`](../FEATURES_TABLE.md) by [`../analysis/provenance.py`](../analysis/provenance.py).

| Column | Meaning |
|---|---|
| `fid` | Feature id (`F001`...) |
| `date` | Day it shipped (US Eastern) |
| `area` | gunplay, animation, bots, audio, vfx, maps, ui, camera, modes, qa, docs, process |
| `feature` | What the player or tester gets |
| `origin` | `dir` direct from prompt, `ai` AI-designed from the developer's goal, `init` AI-initiated, `dbug` developer bug report, `qa` AI QA finding (codebook in the script header) |
| `agent`, `model` | Who built it, with the exact model |
| `board`, `task`, `bug`, `prompt` | Ids in `AGENT_COORDINATION.md`, `tasks.csv` (or a commit), `bugs.csv` and `prompts.csv` |
| `confidence` | The coder's confidence in the origin label: high, medium, low |
| `asked` | The developer's words, verbatim (empty when nobody asked) |
| `ai_decided` | What the AI decided beyond the words |
| `outcome` | kept, changed, reverted, open (as of 2026-11-20) |

## prompts.csv

Every developer prompt parsed from [`../PROMPTS.md`](../PROMPTS.md): `pid` (order in the file), `date`, `time_et`, `to`, `source`
(`verbatim`, `summary` quote, `board` paraphrase or `agent` prompt; the last two are excluded from the analysis), `words`, `pattern`
(D directive, F feedback loop, I task iteration, V validation, L learning, P process; after the Anthropic Economic Index), `spec`
(E exact, G goal, O open mandate) and `text`.

## autonomy_ticks.csv, provenance_validation.csv, provenance_stats.json

- `autonomy_ticks.csv`: Claude Code's self-prompting coordinator ticks per hour (from its session log).
- `provenance_validation.csv`: a blind, stratified sample of 40 features for the developer to label (`developer_label`); the script then reports Cohen's kappa against the coder.
- `provenance_stats.json`: every number on the provenance page, machine-readable.

