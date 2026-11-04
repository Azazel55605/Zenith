# C4 settings gate: wait for Home before measuring a wheel notch

The 0.0.44 distributor passed Page Down/Home, then timed out waiting for an exact
40-pixel wheel translation. Retained screenshots establish the failure:

- `form-home` was still roughly 29 pixels from the original form position.
- `wheel-before` was 3 pixels from Home (30 matching comparison rows).
- `wheel-notch` reached the correct 40-pixel position (30 matching rows).
- The original before/after assertion measured 37 pixels: all 35 rows matched at
  that offset; only 18 matched at 40. Scroll completion records were present.

The helper's `settled()` accepted two identical intermediate frames. A slow guest
may present an animation frame twice while its next frame is still pending. One
background pixel also cannot prove that Home finished.

Current input policy now gates only `wheel-before.ppm` on the exact Home position
of the original `form-top.ppm` controls/text, anchored by the verified initial
six-swatch layout. The accent hover row is excluded from this Home witness.
The tagged harness retains its original full-strip 40-pixel assertion and its
animation checks. The controller only captures/waits: no key, wheel or click is
replayed, no coordinate correction or relaxed displacement is introduced.
Missing geometry or a frozen intermediate frame still fails within 20 seconds.

Validation: 27 input policy tests pass, including stable-but-wrong 3px frames,
hover exclusion, missing geometry, delayed correct presentation and read-only
frozen failure. The retained healthy local guest baseline is accepted; shifting
it by the server's measured three pixels is rejected. The complete isolated **491-test controller suite** passes in 103.430 seconds.
A real four-vCPU TCG Settings guest passes the unchanged exact wheel-step and
animation checks with the repaired baseline guard. That run reached final disk
verification but failed its exact kernel hash check because local publication
replaced output-x64 during the run. It is not a complete gate pass; the final
regression uses fixed artifacts and passes in 289.996 seconds
(`obj/display-settings-final.log`), including final kernel identity and ext2 audit.
Evidence: `/tmp/zenith-wheel-controller-final.log` and
`obj/wheel-settings-regression.log`. Distributor verification remains required.
