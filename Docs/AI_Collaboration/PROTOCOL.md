# The coordination protocol

These are the rules the AI agents on Lead Rush II follow, each with the reason it exists. The live board,
[`AGENT_COORDINATION.md`](../../AGENT_COORDINATION.md), carries the short version in its section 5. Most rules were added right
after something went wrong; the incident is named in brackets so it can be traced in the [research log](RESEARCH_LOG.md).

## Setting

- Several AI coding agents work in **one git repository at the same time**, each inside its own tool (Claude Code, Antigravity,
  GitHub Copilot), with its own model and its own usage limit.
- They have **no direct channel** to each other. The board is the only shared state besides the code itself.
- One resource cannot be shared: the **Unity editor**. Unity locks a project to a single process.
- A human developer sets goals, playtests and makes decisions. They can prompt any agent, but they are often away or asleep.

## Rules

1. **Identity: names go by tool, never by model.** "Claude" means Claude Code, "Gemini" means the Antigravity agent, "Copilot" means
   GitHub Copilot, whatever model each one runs. Every board edit is signed `[Name model HH:MM]`, and the time comes from the
   clock, not from an estimate.
   *Why:* two tools running the same model both believed they were "Claude" and took the other's tasks (13:14 and 23:16 on 11-17).
   Estimated times put events in the wrong order (23:44-23:58 on 11-17).

2. **Unity lock.** Before launching Unity, write `UNITY: <name> <HH:MM> <purpose> (ETA HH:MM)` and check that no LeadRush
   `Unity.exe` is running. Set the line to `free` when Unity has exited. A claim more than 15 minutes past its ETA with no running
   process is stale: log that, then take it. Never kill another project's Unity.
   *Why:* two agents started Unity on the same project four times; one launch failed with a confusing licensing error because the
   other run was still open, and claims were left behind after the run ended.

3. **Visible test runs.** Playtests, QA and pose checks run in a visible window; hidden batch mode is only for silent builds.
   *Why:* the developer heard gunfire from a Unity window nobody could see (22:59) and wants to watch the tests.

4. **Ownership.** Each agent edits only its own files (board section 4). Changes elsewhere are requested in section 6. If the owner is
   offline and something is blocking, the other agent may fix it and tags the log entry `[OWNERSHIP]` so the owner reviews it.
   *Why:* silent edits in someone else's file were reverted or broke assumptions (13:28, 18:13-23:00, 23:20).

5. **Heartbeat and handoff.** Update your section 1 row whenever you start or finish a task. Before going offline or hitting a usage
   limit, hand off: give your rows a new owner, link the spec, set yourself OFFLINE.
   *Why:* usage limits stopped Claude Code five times; the handoffs that listed exact files, values and checks let work continue,
   while a run that died mid-edit lost its results (18:13).

6. **Cross-review.** The agent that builds something never marks it done. It sets `READY FOR REVIEW` with the files changed and
   evidence; another agent checks the diff and re-tests, then sets `VERIFIED` or `REOPENED` with reasons. Things that are about feel
   (recoil, motion) also need the developer's playtest.
   *Why:* asked for by the developer (11-17 18:07). It has caught a fix that was wired but never triggered, a deprecated API in new
   code, and a QA check that passed while the bug was plainly visible in the screenshot.

7. **Every bug gets a row, with its cause.** Anything an agent or the QA bot notices becomes a tracker row owned by the file owner,
   with the **model** that did the work and the task that **caused** the bug.
   *Why:* the developer wants to measure the quality of each agent's and model's work (11-17 23:09).

8. **Push at stable points, often.** Push to GitHub at least every ~2 hours of work, but only when both code assemblies compile
   (`dotnet build`), the latest QA run has no real game errors, and nobody is mid-edit. Record the hash on the board. Never force-add
   ignored files (paid Asset Store packs, private audio, logs).
   *Why:* the developer wants the public repo current, and two in-flight edits broke compilation right before a planned push (23:41).

9. **Short board.** Log entries are one or two lines. Long specs and reports go in `Docs/AI_Collaboration/specs/<ID>.md`.
   Superseded text moves to the archive.
   *Why:* board v1 grew to 361 lines with stale sections, which cost every agent time and tokens on every read.

10. **Never idle.** With an empty queue, run the QA bot on all maps, look at every screenshot and file what you find.
    *Why:* the developer wants all agents working continuously and some agents stop when their list runs out.

11. **Record the process.** Notable events get a tag in the log (`[HANDOFF]` `[LOCK]` `[REVIEW-CATCH]` `[OWNERSHIP]` `[MISATTRIB]`
    `[LIMIT]` `[PUSH]` `[PROMPT]` `[USER]` `[BOARD]`), so the process can be measured later.

## Board layout (v2)

| Section | Contents | Who edits it |
|---|---|---|
| Header | Name-by-tool table | each agent fills in its model |
| 1. Now | `UNITY:` and `PUSH:` lines, one status row per agent | each agent its own row |
| 2. Needs the user | Decisions and actions only the developer can take | any agent |
| 3. Tracker | ID, kind, item, owner, model, reviewer, status, caused by, updated, evidence | owner and reviewer of each row |
| 4. Ownership | Areas and files per agent | changed only with the developer's say-so |
| 5. Rules | The short version of this file | board maintainer |
| 6. Requests | Open cross-area requests (resolved ones move to the log) | anyone |
| 7. START HERE (per agent) | Onboarding and the agent's ordered queue | the assigning agent |
| 8. Reference | Commands and known pitfalls | anyone |
| 9. Log | Newest at the bottom, about 20 entries; older ones move to the archive | anyone, own entries only |
