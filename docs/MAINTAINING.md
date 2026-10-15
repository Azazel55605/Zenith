# Maintaining the documentation

## One owner for each kind of information

| Information | Owner | Update when |
|---|---|---|
| Cross-component state and release acceptance | [STATUS](STATUS.md) | Implementation or operator evidence changes |
| Active task and immediate handoff | [PROGRESS](PROGRESS.md) | Each implementation/validation step |
| Component implementation and next steps | [Component pages](README.md#components) | A component's behavior, limits or acceptance changes |
| Order, dependencies and milestone exits | [ROADMAP](ROADMAP.md) | Priorities or scope change |
| Exact API, commands and limits | Technical guide linked by the component | The contract changes |
| Product requirements and design choices | [SYSTEM-PLAN](SYSTEM-PLAN.md), linked design pages | A decision is accepted or reopened |
| Completed execution evidence | [Archive](archive/README.md) | A slice completes |
| Historical narrative | [HISTORY](HISTORY.md) | A significant project transition occurs |

## During a slice

1. Put the scope, concrete steps and validation plan in PROGRESS. Keep only current
   work and immediate acceptance blockers there.
2. Update the component checklist as steps land. A checked item means implemented;
   separately record which host/native/operator checks passed, on what revision.
3. Update the technical guide alongside API/command changes. State existing limits
   and compatibility or migration requirements before examples.
4. Record failures and corrections honestly. Build success does not prove guest
   behavior; local success does not prove server publication or installation.
5. On completion, move detailed evidence to `archive/YYYY-MM-DD/<slice>.md`, link
   it from the archive index and component evidence, and leave a concise handoff.
   Include revision, environment, test commands/results, limitations and remaining
   operator action. Do not rely solely on ephemeral `/tmp` log paths.
6. Update STATUS and ROADMAP only where the result changes their claims. Keep
   completed history out of the roadmap and old task lists out of PROGRESS.

Do not repeat exact protocol constants across summary pages; link the owning
reference. Do not label a source split/resource package as a protected executable.
Do not turn a proposed design into an implemented claim by copying it into another
page. Keep legacy milestone IDs for orientation, not as competing task lists.

## Link and scope checks

Check relative file links and heading anchors after moving pages, including links
from the repository README and deployment runbooks. Existing public guide paths
remain stable in this overhaul. Archived pages carry a historical banner and
rebased relative links. `docs/cosmos/` is pinned upstream material: do not rewrite
it as Zenith documentation. No kernel rebuild is needed for a Markdown-only change.
