# Distributor delivery and failed-build visibility — 2026-10-16

## Operator evidence

The kernel/v0.0.14 server run passed native repository (987.8 s), native programs
(400.3 s) and kernel recovery (1572.3 s), then signed and published:

`/srv/zenith/packages/core/zenith-kernel/0.0.14`

Reported archive SHA256:
`f5613bce37416244047880514420e7e4c3d5d106b0aae46188d743df949a5b16`.
Both legacy and extended core/extra indexes were published at generation 15.
The actual final failure was:

```text
Checking complete delivery: https://zenith-stable.net.local/core/zenith-kernel/0.0.14/zenith-kernel.zpk
Distributor polling failed: <urlopen error [Errno -2] Name or service not known>
```

Build/sign/publication succeeded according to the operator's logs; the full
delivery check and installed acceptance remain outstanding. Do not delete the
published release, move its tag or rebuild it under another version for DNS repair.

## Dashboard defect and correction

Auto output always selected the last subprocess phase. It showed `Index extra
extended · passed` and only `sealed extra generation=15 entries=4`, hiding the
controller's subsequent delivery error. Failure state was visible, but the useful
error was in another view and the row's error existed only as a hover title.

Failed/interrupted runs now default to controller history when their final phase
passed. An explicit failure banner remains visible while choosing any phase.
Actual failed phases still open their own output; completed and running behavior
is preserved. The backend still reads only bounded, no-follow allowed log/state
files. This changes observation, not validation, trust or retry state.

## Local validation

- 42 dashboard/poll tests passed, including late delivery failure, explicit phase
  selection, genuine failed phases, interrupted runs, and immutable archive bytes
  preserved when a resumed verification fails. Existing recovery tests prove a
  published release skips the kernel build and resumes verification/index renewal.
- Browser plugin unavailable; used existing Python Playwright and installed
  Chromium without adding dependencies.
- Disposable HTTP fixture at http://127.0.0.1:39157/manage/, stubbed systemd and
  representative failed 0.0.14 state. Desktop 1440x1080 and mobile 390x844.
- Page identity/content, visible actual DNS error, persistent failure banner,
  phase selection and return to Auto passed. No page/console errors or mobile
  document overflow. Screenshots inspected; production proxy/auth/service not tested.

## DNS and operator recovery

The VM's NetworkManager-generated resolv.conf lists 10.0.10.3, then public DNS
fallbacks. NSS hosts order is `files myhostname mdns4_minimal [NOTFOUND=return] dns`.
These settings alone do not establish whether the record, DNS reachability or NSS
path caused the lookup failure. Requested:

```sh
getent -s dns ahostsv4 zenith-stable.net.local
nslookup zenith-stable.net.local 10.0.10.3
```

The hostname must resolve to the HTTPS Zoraxy proxy, not blindly to the backend
HTTP package VM. nslookup bypasses NSS; see the [nss-mdns documentation](https://github.com/avahi/nss-mdns#activation).
Keep certificate/hostname checks enabled.

After DNS is fixed, verify and resume the existing immutable package as
zenith-release, with no active distributor poll:

```sh
cd ~/Zenith
bash deploy/distributor/release.sh verify 0.0.14
python3 -B deploy/distributor/poll.py --queue-retry kernel/v0.0.14
sudo systemctl start --no-block zenith-distributor.service
```

Retry checks the existing receipt's source identity, builds the small package tool,
verifies the existing archive and its HTTPS delivery, then renews indexes. It does
not rerun the kernel build/native matrix or overwrite the published archive.

To deploy the UI correction, pull main, rerun
`sudo bash deploy/distributor/install-dashboard.sh` and reload the browser.
Existing dashboard credentials are retained. No new kernel release is needed.
