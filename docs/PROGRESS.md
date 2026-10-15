# Active work

Updated **2026-10-15**. This is a short handoff, not an append-only changelog.
[Status](STATUS.md) summarizes the system; [Roadmap](ROADMAP.md) orders future work.
Completed evidence lives in the [archive](archive/README.md).

## Latest completed slice: documentation overhaul

**Scope:** make done / pending acceptance / planned work easy to find, document
component steps, and preserve the existing engineering history. Feature work is
paused at the user's request.

- [x] Review source, release tags and operator acceptance boundaries.
- [x] Add one current status page and component implementation checklists.
- [x] Replace the mixed roadmap with ordered slices and explicit exit criteria.
- [x] Replace the long progress list with this bounded handoff.
- [x] Archive superseded progress and planning without losing technical history.
- [x] Refresh architecture, userland, system/package/server summaries and navigation.
- [x] Define documentation maintenance rules for future implementation slices.
- [x] Validate local links/anchors and review the final documentation diff.

**Validation:** all non-vendored repository Markdown links/anchors and
`git diff --check` pass. Six archive snapshots match their original content after
reversing banner/link changes. Markdown only; no runtime rebuild needed.
[Completion record](archive/2026-10-15/documentation-overhaul.md).

**Active implementation:** none; feature work remains paused after this docs task.

## Waiting for external acceptance

Kernel **0.0.13** and Sunbeam **0.2.0** are committed/tagged and locally verified
(`f502085`). Full server gates/publication and installed VM acceptance have not
been reported. Kernel **0.0.12** and the login/resource packages are operator
confirmed. See [Status](STATUS.md#release-and-installation-boundary).

After the documentation task, resume [native userland](components/USERLAND.md):
first finish release acceptance, then select the next bounded input/write slice.
Do not mark M5 complete or start unrelated desktop/multicore implementation from
this handoff alone.
