# Active work

Updated **2026-10-16**. This is a short handoff, not an append-only changelog.
[Status](STATUS.md) summarizes the system; [Roadmap](ROADMAP.md) orders future work.
Completed evidence lives in the [archive](archive/README.md).

## Current slice: complete guest exit-status records

The server's 0.0.13 kernel gate now fails `K_UPDATE expected status 0` after the
status-marker wait. Unlike a timeout, this may be a real nonzero install result
or an incomplete/interleaved serial status. Operator serial evidence now confirms
a real `K_UPDATE=1`: `pkg: linked/missing package ancestor` after signature
verification. Both preceding cut/recovery scenarios passed. The failing ancestor
is not included in 0.0.13's error, so its disk/runtime cause remains unconfirmed.

- [x] Inspect exact tagged harness assertion and adapter status handling.
- [x] Find an independent race: prefix waits return on `user: K_UPDATE=` before
  requiring the numeric status and line terminator.
- [x] Reproduce fragmented status records and require complete real results.
- [x] Preserve genuine nonzero results and report the observed code in metrics.
- [x] Pass 104 controller regressions; local signed-install probe returns a complete
  real `K_UPDATE=0` and powers off cleanly. This does not reproduce the server error.
- [x] Add read-only retained-disk inspection; verify local healthy paths, invalid
  image refusal and unchanged source-disk SHA256.
- [x] Handle Debian shells missing sbin on PATH: inspector resolves executable
  debugfs from PATH, /usr/sbin or /sbin; fallback verified locally. The server
  inspection stopped before metadata checks because debugfs could not launch.
- [ ] Inspect the server's failed disk and identify the rejected ancestor before
  choosing a product fix or another release attempt.

Inspection command (from updated controller, with the failed guest stopped):

```sh
python3 -B tools/inspect-kernel-update-disk.py ~/.local/state/zenith/distributor/build-kernel-v0.0.13/obj/distributor/release-_ltpswzx/native/k/upgrade/disk.img
```

See [investigation](archive/2026-10-16/kernel-update-ancestor-investigation.md).
The status-reader fix is independent of this confirmed real filesystem failure.

Previous [common acceptance](archive/2026-10-16/release-validation-stability.md)
passed all four local TCG gates (`e4b1d46`), but that does not establish the server
outcome. Do not weaken signing gates, repeat the install or move its immutable tag.

## Waiting for external acceptance

Kernel **0.0.13** and Sunbeam **0.2.0** are committed/tagged and locally verified
(`f502085`). Full server gates/publication and installed VM acceptance have not
been reported. Kernel **0.0.12** and the login/resource packages are operator
confirmed. See [Status](STATUS.md#release-and-installation-boundary).

After resolving the release failure, resume [native userland](components/USERLAND.md):
first finish release acceptance, then select the next bounded input/write slice.
Do not mark M5 complete or start unrelated desktop/multicore implementation from
this handoff alone.
