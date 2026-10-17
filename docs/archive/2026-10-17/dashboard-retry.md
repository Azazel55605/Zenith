# Dashboard retry controls — 2026-10-17

Implemented Retry build on failed/interrupted dashboard rows, including abandoned
Building records with no controller lock. The POST endpoint validates origin,
CSRF, body/tag and eligibility, invokes a root-owned narrow helper as
zenith-release, and starts the existing systemd service nonblockingly.
The helper takes the controller lock and atomically queues the original tag;
release records/commit pins are unchanged. Completed/pending/unknown records,
active controllers and duplicate queues are refused. Existing CLI queue-retry
uses the same helper implementation. A queued request survives failed service
startup for the next timer; the UI explains this outcome.

The installer deploys the helper, exact executable sudo rule and sandbox state
carve-out. Dashboard Unix permissions remain read-only; signing-directory
permissions stay unchanged. The helper accepts one validated tag, no path or
command options, and runs as publisher rather than root.

## Local evidence

- `python3 -m unittest discover -s tools -p 'test_distributor*.py' -q`:
  94 tests passed, including real queue/lock/commit preservation, background poll
  consumption, HTTP eligibility/CSRF/argument rejection, fixed sudo argv,
  duplicate handling and queue/start failures. Initial sandbox attempt could not
  create an existing release test's disposable /var/tmp directory; rerun with
  appropriate filesystem access passed. No native guest builds were launched.
- `python3 -B deploy/distributor/validate.py --controller-only`: 112 tests passed;
  queue-helper regressions join the shared CI/local/server pre-build set.
- `python3 -m unittest discover -s tools -p 'test_dashboard_setup.py' -q`:
  5 nginx setup tests passed.
- `visudo -cf deploy/distributor/dashboard.sudoers`: rule parsed OK (local
  environment emitted a separate /etc/sudo.conf ownership warning).
- `bash -n deploy/distributor/install-dashboard.sh`, `node --check
  deploy/distributor/web/app.js`, `git diff --check`: passed.
- Browser plugin not available; used bundled Playwright with existing Chromium
  on disposable real dashboard HTTP fixture at 127.0.0.1:18090/manage/.
  Systemd launch was stubbed; helper wrote actual fixture queue.
  Desktop 1440x1000 and mobile 390x844: correct page identity, meaningful content,
  no framework overlay or relevant console errors; failed/interrupted controls,
  completed-row omission, click -> queued notice and duplicate disable verified.
  Screenshots inspected outside the repo. Mobile tables retain horizontal scroll.

## Deployment and acceptance

Source changes require `git pull --ff-only` followed by
`sudo bash deploy/distributor/install-dashboard.sh` on the distributor, then a
browser refresh. Existing credentials are preserved. No new kernel tag is needed.
Actual Debian sudo/systemd/nginx activation and a retry surviving browser/SSH
closure remain operator checks. This slice does not establish why the earlier
builds were interrupted or add suspend/resume of an active build.
