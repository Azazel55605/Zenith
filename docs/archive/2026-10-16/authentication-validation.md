# Authentication validation policy — 2026-10-16

Scope: controller/direct-harness policy only, with unchanged kernel 0.0.13
(`f502085`). No credential algorithm, installed account or product binary changes.

## Evidence and diagnosis

The operator's native-program gate passed New/Repeat password prompts in
0.101/0.051 seconds, then failed its final password-update result wait at
30.044 seconds (15.6 QEMU CPU seconds), under actual TCG. The assertion called
this “hidden password input” despite waiting for hashing/account-save completion.
Other harnesses allowed substantially longer. SerialTerminal enables hidden input
and clears stale input before printing the prompt; this evidence does not identify
an input-readiness race. PasswordHash retains 600,000 PBKDF2-HMAC-SHA256 iterations.

## Change

[Shared policy](../../../tools/guest_auth.py) distinguishes entry prompts,
confirmation, password-update completion and authentication. Fixed minimum
budgets are 60 seconds for prompts and 180 seconds for completion; larger caller
budgets survive. Five-second observation windows permit early explicit failure
classification. Noise cannot renew the deadline; the helper sends no input.
Account results require exact username termination and a completed line.

The [current controller](../../../deploy/distributor/validation-runner.py) applies
the same policy to unchanged tagged helpers, including tags without this module.
Direct harnesses import it through headless-smoke. Phase metrics record requested
and effective budgets and safe failure reasons. No raw input/secret is added to
public diagnostics. Expected rejection tests continue to require real rejection.

## Validation

- 85 policy/status/profile/accelerator/controller/recovery regression tests pass.
  Includes delayed completion beyond 30 seconds, bounded noisy stalls, wrong
  identity/partial output, rejection/mismatch/entropy/fatal/process exit, privacy,
  transport exceptions, wrapper idempotence and unchanged-tag subprocess adaptation.
- Final login/session/styling TCG acceptance: **69 checks passed in 242.241 s**,
  including wrong-password rejection, displayed recovery, ordinary-user/root
  identity, local console, logout guards, signed style upgrade and independent
  clean ext2/secret-free transcript checks.
- Final native-program TCG acceptance **passed in 246.195 s**, including actual
  password save/root authentication, native file permissions, fault containment,
  timeout/cancellation cleanup, executable upgrade, filesystem/package checks,
  secret-free transcripts and installed graphical login/fonts. Password-update
  metrics show requested 30/effective 180 seconds (passed in 9.185 s); root
  authentication passed in 9.389 s. These local timings do not predict server speed.
- The interrupted final runs left no surviving completion evidence; both suites
  above were rerun sequentially on the final code, using the existing ISO.
- Changed Python syntax, relative documentation file links and `git diff --check`
  pass. No host C# tests/rebuild or full repository/kernel-recovery rerun was
  needed for this Python-only policy change; full distributor gates remain required.

Reproduce regression tests:

```sh
PYTHONPATH=tools python3 -B -m unittest tools/test_guest_auth.py tools/test_validation_status.py tools/test_validation_profile.py tools/test_validation_accelerator.py tools/test_distributor_validation.py tools/test_recovery_console.py
ZENITH_VALIDATION_ACCELERATOR=tcg python3 -B deploy/distributor/validation-runner.py tools/login-session-smoke.py /tmp/zn-login13-auth
ZENITH_VALIDATION_ACCELERATOR=tcg python3 -B deploy/distributor/validation-runner.py tools/native-process-smoke.py /tmp/zn-native13-auth-final
```

## Operator acceptance

Full server gates/signing/publication and installed 0.0.13 acceptance remain
unconfirmed. Pull the controller and retry the same immutable tag when no build
is active:

```sh
cd ~/Zenith
git pull --ff-only
python3 -B deploy/distributor/poll.py --queue-retry kernel/v0.0.13
sudo systemctl start --no-block zenith-distributor.service
```

Do not move the tag, weaken hashing, replay secrets or count a prompt/CPU activity
as authentication. Broader release gates remain required; this slice does not
claim to prevent unrelated future failures or fix guest storage throughput.
