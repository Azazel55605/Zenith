# Single work tracker and material-ready design contract

**2026-10-27**, audited against source `a558f66` and its qualification/operator
records. This audit does not assert new distributor or installed-VM results.

## Documentation outcome

The master ROADMAP now owns all product work, current focus, state, dependencies,
exits and release acceptance: 43 work IDs and coverage of all 43 Meridian pages.
Component pages expand those IDs; technical guides are contracts/procedures.
Root Markdown files decrease from **39 to 26**. PROGRESS becomes a short agent
compatibility pointer, not another execution ledger. Repository/agent/maintenance
instructions enforce the new ownership model.

Consolidations:

- STATUS and SYSTEM-PLAN → roadmap state/product definition; older exact evidence
  already lives in the archive, rather than another active version table.
- USERLAND migration plan → userland component + roadmap U1–U6.
- PACKAGE-MANAGER overview → PACKAGES architecture/activation + PK1–PK4.
- NATIVE-APP-MIGRATION → sessions/apps component; 605/612 wire contract → GRAPHICAL-CLIENTS.
- BASH-PORT/developer-tool proposals → DEVELOPER-TOOLS, with current native/stream
  foundations distinguished from missing POSIX process/TTY/libc contracts.
- Historical display notes, unsigned P0 reference and narrative history → archive.
- Operator test handoffs → testing/, with results owned by the master table.

Stale facts removed/reconciled: 0.0.13 retry/publication blockers, already-extracted
network/base/fs commands, statements denying existing jobs/raw/graphics interfaces,
the old ext2 fix's deployment as a current blocker and inherited duplicate status
lists. Unknown sustained-path/hardware/restore evidence stays explicitly unknown.
Source TODO scan found no additional explicit TODO/FIXME/NotImplementedException
markers in current src/userland/deploy (excluding generated directories); the audit
also checked actual module/service/recipe boundaries, not only text markers.
Optional Doom remains outside the main product sequence.

## Design/material outcome

The accepted original specimen remains the target. The design contract now lists
actual parity gaps: mode segments, circular swatches, settings rows/navigation,
shared density/chrome/icons and richer text/scale. These are unfinished shared
implementation, not an invitation to redefine the visual identity.

A six-panel SVG compares the same layout in solid, tint-only and blurred modes in
both appearances. It is explicitly conceptual. The future transport needs bounded
host-owned material regions and foreground coverage, not alpha applied to today's
flattened XRGB image. Geometry/hit testing/text/control state remain fixed; opaque
content, protected UI and fallback remain readable. No blur or transparency is shipped.

The client library adds a role-aware **opaque** surface painter and explicit
candidate roles. Existing canvases use the content role with unchanged pixels.
Tests compare all role paints against the legacy opaque path across 12 palettes,
including clipping/rounding, unknown-role refusal and solid content/control policy.
No kernel/service/wire-format change is made.

## Verification and boundary

- Documentation checker passes local paths/anchors, master ID references,
  exact 43-page coverage and single-tracker constraints; it is included in CI/
  controller regressions so future changes cannot quietly restore duplicate ledgers.
- **239 controller tests pass**, including actual C UI ASan/UBSan tests.
- GCC and Clang freestanding Settings builds pass; pixel-equivalence checks cover
  the small renderer seam. No native/installed effect or full preview-parity claim.
- The material SVG is rendered and visually inspected. Detailed UI redesign,
  actual effects, native acceptance and future release publication remain D1/D3/Q2.
