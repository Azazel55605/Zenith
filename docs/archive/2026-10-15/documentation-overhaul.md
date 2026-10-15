# Documentation overhaul — 2026-10-15

**Result:** completed documentation-only reorganization. Product source baseline
is `f502085`; the operator-confirmed installation remains kernel 0.0.12. No new
kernel/package release or server deployment is part of this slice.

## Changes

- Added current status and eight component implementation/acceptance checklists.
- Replaced roadmap with ordered slices and milestone exits; reduced active progress
  from a 2,504-line ledger to a short handoff.
- Preserved all six superseded progress/planning records with historical banners
  and rebased links. Original content was compared against `git show f502085:docs/…`
  after reversing the banner/link transformation; all six matched.
- Refreshed architecture, system/package/server/userland summaries and stale
  reference claims, including online delivery, read-only native ABI and boot timeout.
- Added technical-page navigation, repaired incoming links and documented how to
  update component steps and archive completed evidence during future work.

## Validation

- Checked every non-vendored repository Markdown file: relative file targets and
  heading anchors, ignoring fenced code and external URLs. All links passed.
- `git diff --check` passed. Changes are Markdown only; no OS rebuild or runtime
  test was needed. The existing runtime results are explicitly labeled prior runs.
- Pinned `docs/cosmos/`, product/test sources, deployment configuration and real
  VM disks were unchanged. The dashboard runbook only received an updated link.

## Handoff

Resume native-tool release acceptance before the next input/write API slice.
Full 0.0.13 distributor gates, publication and installed acceptance remain unreported.
Use [Status](../../STATUS.md), [Roadmap](../../ROADMAP.md) and
[active work](../../PROGRESS.md) rather than the archived task lists.
