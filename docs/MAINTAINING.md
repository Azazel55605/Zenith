# Documentation ownership

**One work tracker: [ROADMAP.md](ROADMAP.md).** It owns priorities, current focus,
all accepted open work, implementation state, dependencies, exits and release /
installed acceptance. Do not create another project status/progress/plan ledger.

## What belongs where

| Location | Owns | Must not become |
|---|---|---|
| ROADMAP | Master work IDs, order, current state and evidence links | A chronological build log |
| components/ | Smaller checklists/source boundaries under those same IDs | An independent schedule or duplicate release table |
| Technical guides | Current APIs, commands, limits and requirements | Unowned TODOs or historical test-count/status appendices |
| DESIGN-LANGUAGE | Accepted visual/material/interaction contract and parity criteria | A claim unsupported effects already run |
| MERIDIAN-PAGES / MERIDIAN-PLAN | Exact page/control coverage and feature contracts mapped to master IDs | A competing project roadmap |
| testing/ | Reusable procedures; results go in evidence/master state | A second pending-acceptance queue |
| archive/ | Dated historical evidence and superseded investigations | Current requirements or active tasks |
| PROGRESS | Compatibility pointer for agent entry instructions | Another checklist |

## During work

1. Read the master current-focus section and the relevant component/contract.
2. Add/map every newly accepted requirement to a master ID before it can be lost.
   Component substeps keep that ID; completing one does not finish the whole row.
3. Update the master state and the component's useful implementation detail in the
   same change. Remove superseded blockers; preserve actual ABI/permission limits.
4. Keep source, local tests, distributor publication and operator-installed evidence
   distinct. Never infer installation from a tag or a successful local test.
5. Archive detailed completed evidence once, with revision/artifacts/environment and
   remaining limits. Link it; do not copy its execution history into active pages.
6. Before a kernel release retain full clean-source qualification. Documentation or
   a visual concept cannot waive gates or turn a menu stub into a backend.
7. Run `python3 -B tools/check-documentation.py` after reorganizing docs. New open
   work outside master/component pages must link its master owner rather than grow
   a new checkbox list. Pinned `docs/cosmos/` is upstream reference: leave it alone.

Consolidate obsolete reference files into their owner and rewrite links. Preserve
unique historical measurements only in the archive, explicitly dated/superseded.
Do not accumulate root-level redirect files for each removed duplicate. Optional
experiments remain outside the main product sequence unless explicitly promoted.
