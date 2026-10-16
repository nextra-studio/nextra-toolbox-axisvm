# CLAUDE.md

## Subagent routing (token saving; accuracy first)

Authorised without asking. Tiers for writing code: `haiku-worker` < `codex-luna` < `codex-sol` < `sonnet-worker` < main. Codex agents only review (code and plans) and do computer use, never implement. Review is advice; the main model makes every final decision.

- **Plan review** only for non-trivial plans (multi-file, interop/COM, data loss risk): write the plan to a scratch file, send the path plus the key source paths. **Code review** after non-trivial changes. Skip both for small or obvious work.

- **Inline by default.** Delegate only when the work is many tool calls or lots of reading and you need just the conclusion back. Not if: ≤3 tool calls, files already in context, or the brief ≈ the work.
- **Pick the tier** by judgment needed: none → haiku; some → sonnet (skip if main is already Sonnet); a lot → main. Review: luna for small diffs, sol otherwise. Borderline → tier above: one round on the right tier beats retries on a cheap one.
- **Max two rounds.** After a second round, finish it inline. Don't reuse a tier that already failed at that task type this session.
- **Main decides, subagents execute.** The brief settles every decision and gives: goal, paths, pattern, verify step, short return format. Subagents stop and ask instead of guessing.
- **Main verifies everything** before using or reporting it: read the diff, rerun the check, judge each review finding before fixing. "Done"/"looks good" is not verification. Check in proportion; don't redo the work.
- **Subagent claims are cheap hints, not facts.** Don't let weak findings pull you into long investigations. Check each one only against code already in context, or with one quick read. If it doesn't hold up straight away, drop it without saying so. Never argue back and forth with a subagent, and never re-run a review to "confirm" it.
- **Computer use** (Codex) drives the real desktop: only when the user asked for UI work, scratch AxisVM/Rhino instance only.
