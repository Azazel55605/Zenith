# Active work

Updated **2026-10-16**. [Status](STATUS.md) summarizes delivery; [Roadmap](ROADMAP.md)
orders future work. Detailed completed evidence belongs in the [archive](archive/README.md).

## Current slice: 0.0.14 delivery verification and dashboard errors

The operator's server log confirms 0.0.14 passed every native gate, was signed
and published, and core/extra indexes reached generation 15. The final HTTPS
archive verification failed with `Name or service not known`. This is a server
hostname-resolution problem; the published package is retained.

- [x] Identify the final delivery error in controller history, beyond the passed
  extra-index phase the dashboard selected.
- [x] Default failed/interrupted builds to controller history when their last
  phase passed; retain an error banner while viewing individual phases.
- [x] Pass 42 dashboard/polling tests, including immutable-archive preservation
  after delivery failure. Verify desktop/mobile rendering and phase selection
  with installed Playwright; no browser console errors.
- [ ] Confirm distributor DNS-only/direct queries for zenith-stable.net.local.
  The VM lists 10.0.10.3; NSS also lists mDNS before DNS. Do not assume the cause
  or change resolution policy until the query results distinguish it.
- [ ] After DNS works, verify the existing archive and queue the same 0.0.14 tag
  for background recovery. Existing publication resumes without rebuilding.
- [ ] Deploy the dashboard update and confirm installed Zenith acceptance.

See [delivery record](archive/2026-10-16/distributor-delivery-diagnostics.md)
for exact server evidence, tests and recovery commands. No new kernel tag or
kernel rebuild is needed for these controller/dashboard changes.

## Completed prerequisite and external acceptance

The ext2 metadata fix and local common acceptance are complete in c622c13/ce42cfd;
[corruption investigation](archive/2026-10-16/kernel-update-ancestor-investigation.md).
Tag kernel/v0.0.14 points to tested source fe1f5a9. Immutable 0.0.13 remains failed
and unchanged. Kernel 0.0.12 and supporting login/resource packages are operator
confirmed. Sunbeam 0.2.0 installed acceptance with the fixed kernel is unconfirmed.

After verified delivery and installed acceptance, resume the next bounded
[native userland](components/USERLAND.md) slice. M5 remains incomplete.
