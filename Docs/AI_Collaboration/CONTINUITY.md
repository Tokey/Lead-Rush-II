# Keeping the agents running

The developer wants Lead Rush II worked on **all the time: by at least one agent, preferably all of them**, from a single prompt.
The obstacle is usage limits: every agent stops when its budget runs out, and so far work stalled each time (Claude Code
alone hit its limits five times on 2026-11-16/17, and three batches of sub-agents died at a limit with no output). This is the
plan for that, written 2026-11-18 00:10 by Claude Code.

**Updated `11.24.26.1` note:** Project branding is **Lead Rush II**, standalone player output is `H:/Unity Projects/Builds/Lead Rush II/Lead Rush II.exe`, and current board ownership / active items (`B152–B158`) live in [`AGENT_COORDINATION.md`](../../AGENT_COORDINATION.md).

## Budgets and how each agent can continue

| Agent | Budget shape | Can switch model itself? | Fallback when out |
|---|---|---|---|
| Claude (Claude Code) | 5-hour window + weekly cap; the tightest | No | Its rows go to Gemini. Gemini can run a Claude-family model in Antigravity (`claude-opus-5-5-high`, `claude-sonnet-5-5-*`) for the hardest ones, still signing as Gemini. Claude resumes by itself after the reset (scheduled tick). |
| Gemini (Antigravity) | Separate quota per model | Yes (`agy --model ...`) | Its current model, then argon (`gemini-pro-orchestrator-g1-pro`) or opus, then **`gemini-4-hydrogen-high`** (longest-lasting; the developer's choice for the always-on orchestrator), then hydrogen medium/low, then `gemini-3.8-flash-*` |
| Copilot (VS Code) | Monthly premium requests; model picked in VS Code | No (the developer picks) | Its rows go to Gemini; it is prompted again when it can run |

## Four layers that keep work going

1. **Budget line on the board.** Section 1 has a `BUDGET:` line where each agent writes `OK`, `LOW` or `OUT until HH:MM`.
   At `LOW` the agent hands off **before** it stops: every open row gets an owner, a spec link and the next concrete step.
2. **Self-wake.**
   - Claude: a scheduled tick every ~20 minutes (session-only, 7-day expiry). After a usage-limit reset the next tick simply
     works again, so no one has to prompt it.
   - Gemini on Hydrogen: the long-running orchestrator. Every ~15 minutes it checks the board, prompts idle agents and files work.
   - Copilot: is prompted by the others (`code chat`), since it has no scheduler of its own.
3. **Agents prompt each other** (board rule 8, commands in board section 8). An agent whose row has been quiet for 20+ minutes,
   with no Unity process running, gets a prompt with its next rows.
4. **A watchdog that needs no tokens.** [`Tools/LR2AgentWatchdog.ps1`](../../Tools/LR2AgentWatchdog.ps1) runs from Windows Task
   Scheduler every 20 minutes. It reads the board, works out when each agent was last seen, and prompts any agent quiet for 25+
   minutes through its CLI (Gemini through `agy`, Copilot through `code chat`). It never starts Unity or edits code, skips
   Gemini while Unity runs, and logs to `Logs/LR2_Watchdog.log`. This is the guarantee that holds even when every agent is idle.

## What happens when...

| Situation | What keeps work going |
|---|---|
| Claude runs out | Gemini takes Claude's rows (bugs lane first). Claude's tick resumes it after the reset and it takes back what is left. |
| Gemini's current model runs out | Gemini switches down its fallback chain and ends on Hydrogen. If its conversation is stuck, the watchdog starts a Hydrogen session. |
| Copilot runs out | Its rows go to Gemini; the watchdog prompts Copilot again later. |
| All agents idle | The watchdog prompts Gemini and Copilot every 20 minutes; Claude's tick wakes Claude. |
| VS Code is closed | Claude's tick and Copilot stop (both live in VS Code). The watchdog still prompts Gemini. |
| The PC sleeps or reboots | Nothing runs. Task Scheduler restarts the watchdog after login; reopen VS Code for Claude and Copilot. |

## Spending fewer tokens (so the limits come later)

- Read only board sections 0-3 on routine checks; long specs live in `specs/`.
- No sub-agent fan-outs for reviews: three of them burned large budgets with no result. Review inline.
- Match the model to the task: Hydrogen (medium) or Flash for QA sweeps and routine edits; Opus or Claude Code for hard diagnosis
  and reviews.
- Stagger the heavy work so not every agent is spending at the same time, and batch pushes.
- Write handoffs as files and numbers, not prose, so the next agent does not have to re-diagnose.

## One-time step for the developer

The watchdog is a persistent scheduled task, so it is registered only when you decide to. Test it first, then register:

```powershell
# try it (prints decisions to Logs/LR2_Watchdog.log, prompts nobody)
powershell -NoProfile -ExecutionPolicy Bypass -File "H:\Unity Projects\GitHub Repos\LeadRush 2\Tools\LR2AgentWatchdog.ps1" -DryRun

# register: every 20 minutes
schtasks /Create /TN "LR2 Agent Watchdog" /SC MINUTE /MO 20 /TR "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"H:\Unity Projects\GitHub Repos\LeadRush 2\Tools\LR2AgentWatchdog.ps1\"" /F

# remove it again
schtasks /Delete /TN "LR2 Agent Watchdog" /F
```

Keep VS Code (Claude Code + Copilot) and Antigravity open for the agents to be reachable.
