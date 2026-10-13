# Implementation progress

## 2026-10-13 · Distributor DHCP marker recovery

- Operator's 0.0.8 native gate reached the live shell, then failed waiting for its
  DHCP success marker. Retained diagnostics contain `network: online address=10.0.2.`
  interrupted by Cosmos monitor debug output. This establishes an online transition,
  but the damaged line cannot establish the exact final address to the harness.
- Controller validation adapter now keeps the original wait, then probes the same
  live guest with read-only `ip` checks for DHCP online state and the exact expected
  address when the boot marker is missing. A damaged online transition triggers
  that probe after three seconds; absent online transitions retain the full original
  startup deadline. A unique logger response excludes echoed
  command text. Failed, dead or wrong-address guests still fail. No networking
  reconfiguration, DHCP retry, static fallback or acceptance-gate bypass occurs.
- Tagged harness source remains unchanged; import adaptation applies only to its
  headless Machine. Track fully constructed machines and reap them on harness exit,
  including failed boot checks before the harness assigns its outer cleanup variable.
- Validation: 15 controller/wrapper tests and 10 serial matching tests pass. Real
  disposable TCG guests prove healthy DHCP passes despite a deliberately unreadable
  boot marker and a no-NIC guest fails the probe. Both shut down cleanly. Local KVM
  is unavailable; actual nested-KVM retry remains operator acceptance. Evidence:
  /tmp/zenith-dhcp-adapter-tests.log, /tmp/zenith-dhcp-marker-tests.log,
  /tmp/zenith-dhcp-probe-smoke.log, /tmp/zenith-dhcp-probe-smoke-2.log and
  /tmp/zn-dhcp-probe-2.
- Current hosted CI 38453453030 independently failed the serial read/EOF host test
  and the guest `got=serial-answer` check. Both observers accepted prompt text in
  echoed `read -p` source before the read callback was ready. Assemble that prompt
  from quoted fragments so only actual emitted prompt text can trigger input.
  Focused serial host checks pass 13/13 and the full host suite passes 603/603.
  Following that fix exposed an existing headless fixture calling session=invalid
  while its test-only boot override whitelist rejected it; allow this exact fixture
  argument to preserve the invalid-session/future-schema fallback checks. The full
  installed-headless regression passes 73/73, including actual interactive read,
  hidden credentials, future-schema/invalid-session fallback, independent ext2 audit
  and serial panic restart. CI native kernel/repository/program
  gates now use the same controller adapter. No production serial behavior or
  kernel bytes changed. Evidence: /tmp/zenith-dhcp-full-host.log and
  /tmp/zenith-headless-fixed.log.
- Pull the updated controller, queue a service-owned retry of kernel/v0.0.8 and start
  the service. Existing source/version pins, signed packages and release tags remain.

## 2026-10-13 · Release refs and installed-boot CI follow-up

- Commit 764fb12 and independent kernel/v0.0.8, zenith-docs/v0.1.1,
  zenith-man-pages/v0.1.0, zenith-fonts/v0.1.0 and sunbeam-tools/v0.1.0 tags are
  pushed to the verified owner repository. Working tree was clean afterward.
  Server 0.0.7 receipt is reachable and identifies the expected immutable source
  and signing key; new 0.0.8 receipt still returns 404. Operator must pull controller
  recipes, install GCC and redeploy dashboard before consuming new data tags.
- Inspecting preceding hosted CI run 38435516631 identifies 9/10 installed EFI
  checks passing; boot, persistent ownership and independent ext2 audit succeeded.
  The failure is an obsolete assertion for the old offline-only `man pkg` title,
  not a boot hang. Current full manuals now ship independently, so the installed
  boot harness checks persisted settings, headless recovery documentation and pkg
  update help; full package-manual installation stays in native-process-smoke.
  Local disposable installed-UEFI regression passes 11/11 with session override,
  persisted settings/recovery help, clean poweroff and independent ext2 audit:
  /tmp/zenith-efi-008.log, /tmp/zn-efi-008. Base installed-boot checks also pass 7/7.
  Existing release tags remain immutable. Hosted unit tests for 764fb12 passed;
  hosted build/native jobs remain in progress.

## 2026-10-13 · Storage maintenance, system update and native userland (locally verified; distributor deployment next)

- Implemented bounded distributor storage reporting and cleanup under shared worker/publication locks: preserve active work, latest failed checkout and two diagnostic attempts, expire other inactive failures after seven days, cap inactive logs at 1 MiB. Registered orphan validation workspaces require matching ownership records before deletion. Dashboard shows disk space, build allocation and cleanup policy. Nine maintenance tests and 16 dashboard API tests pass. Chromium/Firefox desktop and mobile-width browser checks pass; rendered storage values, policy and warnings are verified.
- Implemented `pkg update [--yes]`: refresh signed catalogs, resolve all installed data updates and a newer kernel, show one confirmation, verify all downloads and kernel preflight before data writes, commit data then separately stage the kernel. Reboot and boot confirmation remain explicit. 603 host tests pass, including update planning and read-only kernel preflight. Combined native acceptance passes 83 checks: interactive decline, data plus real kernel staging, pending refusal, reboot to the exact new hash, confirmation, data-only/no-op updates, freshness/replay/tamper handling, ordinary-user refusal, secret-free transcripts and independent clean ext2 audit.
- User explicitly selected beginning native loader/process work. Implemented bounded static x86_64 ELF ABI 1, private user page tables, ring 3 entry, signed executable ownership, write/exit/identity calls, fault containment and interrupt-budget termination. Initial disposable guest proves native execution, bad-pointer rejection, kernel-memory/privileged-instruction containment and continued shell operation. Expanded native acceptance passes NX/read-only-code, CPU segment/FPU state restoration, root/user IDs, same-kernel app upgrade, clean ext2 and package verification. Independently signed full manuals and fonts install, and the installed virtio desktop renders using all three packaged fonts; this is a foreground prototype, not the complete scheduler/filesystem/network ABI. Compiled base/network/filesystem/desktop migration remains future work.
- Added independent manual/font generators and freestanding Sunbeam source/recipe; fonts keep embedded recovery fallbacks. Prepared independent versions: kernel 0.0.8, docs 0.1.1, manuals/fonts/Sunbeam 0.1.0. Actual server publication remains subject to controller pull/deployment and the normal immutable tagged build gates. No production keys or installed VM disks changed.
- Full kernel recovery regression passes 169/169, including power cuts, ISO recovery, actual new/previous kernel hashes, failed-entry fallback and independent ext2/FAT audits. The final protected-executable/manual/font acceptance also passes on the rebuilt release ISO. GCC and Clang both emit accepted ABI 1 ELFs. Package-tool 11, poller 20, release 17, validation-wrapper 10, maintenance 9 and dashboard 16 tests pass. Final desktop/filesystem/installed-root smoke passes 139/139; recovery manuals remain usable without full-manual installation.
- Evidence: /tmp/zenith-host-008.log (603), /tmp/zenith-update-2.log (83), /tmp/zenith-kernel-final.log (169), /tmp/zenith-release-008-native.log, /tmp/zenith-release-008-build.log, /tmp/zenith-smoke-008.log (139), /tmp/zenith-packaged-fonts-virtio.png, /tmp/zenith-storage-chromium.png and /tmp/zenith-storage-firefox.png. The final GUI font clone is deliberately terminated after capture; the clean ext2 audit applies to its main acceptance disk before cloning.

## 2026-10-13 · First named data-package install confirmed

- User confirms zenith-docs installed successfully by name through Zenith's package
  manager. This completes the first operator-confirmed server-built data-package
  delivery/install check following zenith-docs/v0.1.0 publication. The existing
  private repository client and server release workflow now have a real installed
  data-package acceptance result in addition to the kernel release.
- Next distributor slice remains storage visibility and automatic retention/cleanup
  of obsolete build artifacts, with active-work protection and diagnostic retention.

## 2026-10-13 · Clear old completed activity on new release discovery

- On newly discovered validated package tags, the poller persists dashboard_hidden
  on earlier Complete entries. Their durable pins/completion state and logs remain;
  subsequent polls skip them. No new tags means no clearing. Explicitly rerunning a
  hidden completion makes that attempt visible again. Failed/interrupted/pending/
  active entries stay visible even if a stale hidden marker exists.
- Dashboard omits only flagged Complete rows; published inventory and direct old
  output access remain. Selection moves away from a removed build on refresh.
  This does not delete build artifacts; automatic storage retention is still the
  next planned distributor task.
- Validation: 19 real-Git polling and 14 disposable HTTP dashboard tests pass,
  covering no-new-tag behavior, idempotence/pins, retained errors, retry visibility
  and hidden completion log access. Browser plugin unavailable; Playwright Chromium
  1505x1045 at http://127.0.0.1:8772/manage/ verifies automatic row removal, failed/new
  entries and package inventory retention, log selection recovery and healthy
  console/render. Screenshot: /tmp/zenith-history-hidden.png. Server deployment
  remains operator-run; no package rebuild, tag movement or file cleanup occurred.

## 2026-10-13 · Expandable package inventory and first docs release

- User confirms distributor VM restarted for CPU changes and latest controller
  fixes installed. Server-built kernel/v0.0.7 completed; no new kernel/tag rebuild.
- Dashboard groups versions by repository/package, collapsed initially with latest
  numeric version and count. Package-row clicks and keyboard buttons reveal ordered
  versions. Expansion/focus survive five-second refresh and repository filtering;
  name/version queries show matching versions with query-specific expansion state.
- Local actual zenith-docs 0.1.0 payload builds and inspects as one 872-byte installed
  package quick-start file, plans /usr/share/doc/zenith/packages.txt at 0644, and
  passes signed-container verification using disposable keys. Recipe targets extra;
  release tag zenith-docs/v0.1.0 selects this first docs package independently of the
  kernel version. Production signing/publication uses the server's tagged worker;
  user subsequently confirms successful guest installation by package name.
- Browser plugin unavailable; regular Playwright Chromium desktop 1505x1045/mobile
  390x844 and Firefox default viewport verify grouping, numeric 0.0.10 over 0.0.7,
  repo separation, click/Enter/Space expansion, focus/refresh persistence, filtering,
  meaningful render/identity, no framework overlay and healthy console. Screenshots:
  /tmp/zenith-groups-{chromium,firefox,mobile}.png. Fixture versions are illustrative.
- User adds storage management to the upcoming list: automatic bounded cleanup of
  old build files/disks/logs, storage visibility, active-work locking and preservation
  of useful failure evidence. Recorded in ROADMAP; current successful-checkout cleanup
  exists, broader retention/automatic pruning is not implemented in this UI slice.

## 2026-10-13 · Public repository permissions and partial inventory

- User's screenshot shows kernel/v0.0.7 Complete and signed core/extra indexes
  published, but Package inventory unavailable. Found/reproduced a service-umask
  defect: mkdir(mode=0755) is filtered by the distributor's 0077 umask, creating
  new public repository/package parents (including otherwise empty extra) as 0700.
  An inaccessible repository previously discarded even readable inventory rows.
  Actual server modes are not inspected; this matches the observed deployment path.
- Publisher and signed-index activation explicitly chmod public repository/parent
  directories to 0755. Public archives/receipts/indexes remain 0644, staging/key
  directories remain private. Dashboard now retains readable repositories/packages
  and reports the specific inaccessible path instead of dropping the entire list.
- Operator repair for existing publication: sudo find /srv/zenith/packages -type d
  -exec chmod 0755 {} +, then refresh. No rebuild, receipt rewrite, retag or replacement
  of the completed immutable release. Pull controller and reinstall dashboard for
  future publication behavior/UI API handling. Server repair remains operator-run.
- Validation passes 11 signed publisher, 17 release and 13 dashboard tests (41),
  including actual 0077 publication/index signing with public modes and unchanged
  private key permissions, and a denied-extra fixture preserving core rows/warnings.
  Concurrent fixture .NET builds collided during one startup; isolated publisher
  rerun passes. Browser plugin unavailable; Playwright Chromium 1505x1045 at
  http://127.0.0.1:8770/manage/ verifies identity, meaningful content, healthy console,
  precise permission warning, retained 0.0.7 row and core/extra filtering. No overlay.
  Screenshot inspected: /tmp/zenith-inventory-partial.png. No kernel changes or
  production disk/server access occurred.

## 2026-10-13 · Detailed distributor phase output

- User's native log has reached real kernel activation/confirmation and retained
  rollback verification. Fresh release-account diskless probe reports KVM enabled;
  collected QEMU logs from several attempts show permission denial/TCG fallback.
  Current guest acceleration is not independently identified from that mixed output.
  Supplied distributor unit has no device sandbox. Older process credentials versus
  fresh probe credentials remain a likely explanation; let current acceptance finish.
- New workers expose bounded phase snapshots in state BUILD-ID.details.json, mode
  0640, independent of private build-directory permissions. Publish phase stdout
  tails and latest QEMU stderr, not serial/password input. New retry resets the old
  snapshot; history remains append-only. Observation failure cannot alter gates.
- Dashboard selects current/completed phase or controller history; shows individual
  test results, phase status/duration, output age, build status and TCG diagnostics.
  Existing/active old-controller jobs retain history with an explanation rather
  than pretending detailed snapshots exist. Source pull and /opt dashboard reinstall
  are required after current worker finishes; no package tag movement needed.
- Validation: 17 poll, 16 release, 12 disposable HTTP dashboard and 10 copy/reporting
  regressions pass (55 total), covering bounded snapshots, permissions, no serial
  exposure, phase selection, invalid IDs/views and symlink refusal. Browser plugin
  not available; regular Playwright verifies Chromium 1505x1045/390x844 and Firefox
  default viewport against http://127.0.0.1:8769/manage/. Identity, meaningful render,
  no framework overlay, healthy console, phase/history switching, diagnostics and
  automatic output updates pass; mobile has no page overflow. Initial string-based
  wait predicate hit CSP unsafe-eval refusal; function predicates keep CSP intact.
  Screenshots visually inspected: /tmp/zenith-detail-{chromium,mobile,firefox}.png.
  Actual server UI installation and new-worker KVM behavior remain operator-run.

## 2026-10-13 · Distributor build efficiency

- User's TrueNAS-hosted Debian VM exposes /dev/kvm (root:kvm, group read/write).
  Release-account permissions and usable nested acceleration are not yet verified.
  Doctor now reports groups/device access; AUTOMATION documents a paused diskless
  QEMU probe and the administrator group fix. Current running build should finish
  before controller updates. Existing native guests use one vCPU and run sequentially.
- Explicit failed/interrupted retries retain ignored incremental compiler outputs
  only in clean, unchanged pinned checkouts; dirty sources, mismatched commits and
  local Cosmos overrides are refused without deleting evidence. Successful checkout
  cleanup remains. No cross-tag cache or native acceptance skipping was added.
- Current controller runs unchanged tagged harnesses through an image-copy adapter:
  independent reflinks when supported, sparse copies otherwise, ordinary-copy
  fallback. Preserve source/destination independence, same-file refusal, writable
  clone creation, symlinks and non-image semantics. Fresh publication avoids duplicate
  index signing and same-poll daily renewal; recovery/daily idle renewal remain.
- Phase logs now report elapsed seconds and retain atomic phase-times.json for
  passed/failed/interrupted phases; metric errors cannot hide a phase failure.
- Validation: 17 Git polling, 16 signed publication/delivery and 7 copy/metrics
  regressions pass. CI includes the new adapter regression suite. A local sparse
  256 MiB fixture copied in ~0.002s with 8 KiB allocation, versus shutil's ~0.100s
  and 256 MiB. This synthetic local result is not a VM end-to-end speed claim.
- Actual TCG installed clone boot, shell execution and clean poweroff pass with
  the new runner. GPT/EFI/kernel binding audit passes, original source SHA256 remains
  unchanged, and independent read-only e2fsck passes on the resulting ext2 root.
  First fixture had password-protected root and refused poweroff as nobody; reran
  with an earlier disposable installed fixture. No VMware/production disk touched.
  Evidence: /tmp/zenith-opt-native.log, /tmp/zn-opt-native-3, and
  /tmp/zenith-{poll,release,validation}-optimization.log. Full native release gates
  remain enabled; server timings/KVM probe and deployment remain operator-run.

## 2026-10-13 · Interrupted retry and browser-output diagnostics

- User's retained logs confirm full kernel-update native acceptance and all 70
  online repository checks passed. Controller log stops before recording the
  repository worker's successful return; no poll/QEMU processes remain. The
  inactive systemd status describes the separate service, not the foreground SSH
  retry. Its exact termination cause remains unconfirmed; no release-complete claim.
- VM loopback log API returns HTTP200; direct proxied navigation in the same browser
  also loads JSON. Backend permission/proxy-route failure is not established. Added
  ID-based build-output API, direct Open output link, explicit same-origin credentials
  and distinct transport/HTTP/non-JSON errors; legacy log route remains supported.
- Added durable queue-retry for the existing background service, avoiding dependence
  on a long foreground SSH session. SIGTERM/SIGHUP/keyboard cancellation records an
  Interrupted reason and stops the execution child; explicitly cancelled tags wait
  for an explicit retry. Signing still requires complete successful worker exits.
- Validation: 13 polling tests and 11 disposable HTTP dashboard checks pass,
  including queued failure retry, consumption, cancellation persistence/child stop
  and safe ID-based output access. Sixteen release regressions pass after adding
  sixty-second background progress messages. Worker exit codes are retained in
  failure reasons; cancellation exit 130 is recorded as Interrupted.
- Playwright fallback (Browser plugin absent) passes output fetch, direct-link
  viewing, simulated fetch blocking, actionable error and automatic recovery in
  Chromium and Firefox. Initial console/JS checks pass. Chromium uses 1505x1045
  desktop and 390x844 mobile; installed Firefox's older protocol requires its
  default viewport (custom viewport calls failed before navigation). Compared
  updated log-heading link, spacing, typography and mobile overflow via view_image.
  Evidence: /tmp/zenith-output-chromium.png, firefox.png and chromium-mobile.png.
  These fixtures do not reproduce the original browser's extensions/settings.
  Server deployment and the original browser's fetch behavior remain operator-run.

## 2026-10-13 · Manual distributor retry PATH parity

- User confirms controller ad9593f installed; newest retry log ends at preflight
  with missing e2fsck/debugfs/cosmos.patcher. The earlier AF_UNIX traceback is from
  the original attempt in the append-only per-tag log. Direct Python invocation
  bypassed release.sh/systemd PATH setup; tools were already present on this VM.
- Poll CLI now supplies the user dotnet-tool directory and standard bin/sbin paths
  for normal polls, retries and inherited execution/native-test children. Existing
  PATH entries are retained and repeat setup is idempotent. No gate bypass/tool
  reinstall/tag movement. Documented immediate export workaround for old controllers.
- Ten polling regressions pass, including actual fixture cosmos execution and
  child-Python tool discovery from a restricted login PATH. Server retry after
  environment correction remains operator-run; no new kernel artifact issued.

## 2026-10-13 · Distributor native-path and dashboard-route fixes

- User's server built e541c2d kernel/v0.0.7 through host tests, kernel build and tool
  build, then native acceptance failed because the deeply nested serial socket
  exceeded Linux AF_UNIX's path limit. Signing/publication had not started.
- Controller now gives both native gates real short /var/tmp/zn-*/k and r paths,
  independent of checkout depth/TMPDIR and older tagged harness path resolution.
  Native artifacts are retained under each release's native/ directory even after
  test failure; stale sockets are pruned before cross-filesystem moves. Large disks
  stay out of /tmp tmpfs. Retrying keeps the same pinned source/version and clears
  stale error/finish fields. No acceptance gate or immutable-tag check was weakened.
- User reports dashboard service healthy but HTTP404; the prior installer left
  nginx inclusion as a manual step. Added idempotent existing-site setup with backup,
  nginx validation/reload and restoration on validation failure. Ambiguous multiple
  server blocks require explicit selection. User subsequently confirms access fixed;
  direct verified-TLS probe also returns the expected nginx Basic-auth401.
- Validation passes 16 release tests, 8 Git polling tests and 5 nginx-setup tests,
  including real short serial/monitor socket bind/connect from a long release path,
  failed artifact retention, retry behavior and nginx rollback. Two real disposable
  QEMU live boots verify serial readiness and monitor commands at the new short
  paths; evidence survives relocation into the long release directory. Evidence:
  obj/native-short-socket.log, /tmp/zenith-short-native-tests.log and retry-tests.log.
  Actual server rerun/full native suites remain operator-run. No new kernel archive
  was issued, published or installed by this fix. Retry/setup guidance updated.

## 2026-10-13 · Lightweight distributor dashboard

- Added optional /manage/ interface: effective timer interval/last start/next run,
  Run now, tag/commit/status tables, recent live logs and core/extra inventory with
  repository/search filters. Static HTML/CSS/JS and standard-library Python require
  no Node runtime, frontend build, database or external assets on the server.
- Backend reads bounded state/receipts/log tails, refuses symlinks/traversal, detects
  poll locks, marks abandoned builds Interrupted and gates the fixed service start
  with same-origin/CSRF checks. Missing status/data and failed polls are visible.
  A separate root-code-owned loopback service user can read public state/logs but
  cannot read signing keys; its sudo rule only queues the existing release service.
  nginx Basic authentication is scoped to management, preserving public packages.
- Added Debian installer, service, sudoers and optional nginx snippet; operator
  must add/retain the include in their existing site and check/reload nginx. Existing
  credentials are retained. Poll state/log modes now permit read-only group access
  without making build checkouts or signing directories readable.
- Validation: 10/10 disposable HTTP API tests, 8/8 real-Git poll regressions; service
  unit and sudoers parse checks pass; shell and JS syntax checks pass. Playwright
  Chromium fallback used because Browser plugin is absent. At 1505x1045 and 390x844,
  checked identity/nonblank/no overlay/clean console, inventory, repo/search/empty
  filters, log selection and real API Run now -> busy/Building/log refresh behavior.
  systemd execution is stubbed in fixtures; no actual server release was triggered.
- Visual comparison used view_image on generated full-screen concept and final
  browser captures. Matched header/gutters, heading/button hierarchy, navy/amber
  palette, timer strip, table density, log frame and search icon. Corrected spacing,
  row height and button alignment. Primary copy matches; intentional differences
  are browser-local/live dates, actual raw logs, selected-build label, bounded-log
  hint, newest-first versions and queued/busy/error feedback. Mobile stacks schedule
  and filters and scrolls tables without page overflow. Concept:
  /home/azazel/.codex/generated_images/01a11bd5-2e10-7aa0-8351-3daa1ea80f4c/exec-d9b89a26-756b-40c7-b7f6-a49d075232aa.png.
  Browser evidence: /tmp/zenith-dashboard-desktop-idle.png, desktop.png, mobile.png.
- Real Debian nginx/login/sudo/service/proxy acceptance remains operator-run;
  setup and checks are in deploy/distributor/DASHBOARD.md. No new kernel package
  or signing-key change. Temporary preview fixture/server are cleaned up after QA.

## 2026-10-13 · Package-tag distributor automation

- User selects independent package tags and periodic Git polling on the build VM.
  Added administrator-maintained kernel/zenith-docs recipes, a fifteen-minute
  systemd timer, private Git cache and detached per-release checkouts. Ordinary
  commits/unmapped tags do not publish; mapped tags must be reachable from main.
- Recorded commit pins, status/timestamps, retained logs and failed checkouts.
  Completed tags are skipped; moved tags refused; normal failures require explicit
  retry. Interrupted publication resumes verification of the same immutable
  source-bound release. Successful checkouts are pruned. Index renewal runs daily.
- Automated kernels add actual kernel-update and online-repository QEMU gates
  before signing. Data recipes run host tests and existing format/signature checks.
  Manual kernel workflow remains host-gated. Current release commands share a
  publication-root lock across detached checkouts to serialize index generations.
- Validation passes 8/8 real-local-Git orchestration tests and 14/14 release tests,
  including cross-checkout exclusion and native failure preventing signing.
  systemd-analyze verifies both units outside sandbox socket restrictions.
  HTTP fixture initially hit sandbox socket denial; rerun outside sandbox passes.
  Native harnesses were previously verified for 0.0.7; no full automated server
  kernel build, remote Git polling or actual VM timer activation was performed here.
- Setup and recovery are documented in deploy/distributor/AUTOMATION.md. Signing
  and building still share the trusted release account; no isolated worker/signer
  claim or public-key rotation. Source delivery and operator timer activation remain
  pending. No kernel version/artifact was issued or replaced by this host slice.
  Unrelated Doom documentation remains outside this implementation.

## 2026-10-13 · 0.0.7 signed online repository client

- User confirms the distributor published 0.0.5/0.0.6 and selects the next
  package-install phase. Implemented bounded signed core/extra indexes, cached
  freshness/generation checks, deterministic dependency/conflict planning, named
  download/install/upgrade and a separate named kernel-update convenience path.
- The private LAN/VPN protocol reuses pinned RSA-PSS package trust and verified
  HTTPS; it is not TUF and does not complete public key rotation/revocation gates.
  Metadata expires, persists its generation floor as the atomically replaced signed
  cache, and never replaces a newer/same-but-different snapshot with older bytes.
- Server index signing verifies all indexed immutable archives. New kernel/data
  releases refresh signed indexes; publish-existing remains a no-private-key path,
  with explicit release.sh index afterward. Default index validity is seven days.
- Existing installed transaction engine handles the complete verified dependency
  set. Preview and interactive confirmation precede downloads; --yes supports
  explicit noninteractive use. Kernel activation retains P3 confirmation/rollback.
- Host checks pass 580/580; distributor checks pass 12/12; Release builds pass.
  Installed HTTPS acceptance passes 70 checks, including checksum rejection/retry,
  interactive refusal, dependency resolution, upgrade, persistent replay floors,
  interrupted cache-stage cleanup, named kernel staging/pending refusal/rollback,
  non-root denial and independent clean ext2 audit. Existing offline package
  regression passes 71/71 and full kernel regression passes 169/169, including
  actual ELF reboot/confirmation and clean independent ext2/EFI audits.
  Initial lab setup used the TLS fixture's tuple as a dictionary; corrected before
  guest execution. Actual VMware acceptance of 0.0.7 remains pending.
  Evidence: obj/online-host.log, online-build-validated.log, online-server-final.log,
  online-native-accepted.log, online-offline-regression.log and online-kernel-regression.log.
- Signed 0.0.7 with the existing trusted key: 17,220,510 bytes. Kernel SHA256
  b3beecea97cf5968367cc9c42e1d95098deafa63f8c098319350cc09ddee162b;
  archive SHA256 6aac8b8c5d0b1be0834fbd5c6eabd792d7a75821b4e5a4a73528464687e02d11.
  Relay retains previous versions. Existing signing/TLS trust is unchanged.
  Server publication/index minting and the user's installed test remain user-run.
  Unrelated Doom documentation is preserved outside this implementation commit.

## 2026-10-13 · Doom engine package feasibility

- Recorded the requested independent engine package with user-supplied WADs in
  APPLICATIONS. Reviewed doomgeneric upstream platform hooks and sound backend
  requirements against current native C integration and application boundaries.
- Port appears feasible, but the native loader/libc and display/input application
  interface remain prerequisites for an independently executable package. Existing
  scripts/data packaging cannot run the engine; kernel-linked C would only be an
  experiment. Audio is separate work. No engine port, WAD import, package release
  or guest gameplay validation was performed by this documentation slice.

## 2026-10-13 · Simplified manual distributor release workflow

- User retains the 0.0.5 then 0.0.6 installation sequence, requests simpler server
  building/signing now, and reserves 0.0.7 for package-install improvements afterward.
  No new kernel version is issued by this host-only workflow slice.
- Added release.sh setup/doctor/kernel/publish/verify: persistent JSON deployment
  settings, phase/log feedback with useful failure tails, early tools/permissions/
  clean-source/immutable-version checks, shared operation locking, source-commit
  stability, actual signing and the existing atomic verifying publisher. Existing
  issued archives can be published without rebuilding or accessing the private key.
- Delivery verify checks the locally signed container and exact full downloaded
  bytes/hash through the configured proxy, with explicit public TLS trust and
  hostname validation. Published artifacts survive transport failure for verify
  retry. No automatic git pull, signing-key rotation, scheduler or guest activation.
- Disposable workflow tests pass 12/12: saved config, invalid input, key permissions,
  immutable existing publication, build failure/no signing, source-change refusal,
  real kernel/data signing/publication, useful failure logs, full HTTP delivery,
  committed-payload enforcement and concurrent refusal. Host tests/build stages are mocked in orchestration coverage; real
  signing and publisher checks use disposable RSA keys/ELF, not production keys.
  Existing publisher regression passes 10/10. A full actual Release host-test,
  kernel-build, disposable signing/publication and 16 MB HTTP byte-verification
  run also passes; this is not just mocked orchestration. Its disposable 0.0.999
  fixture is not an issued production version. Added matching data/verify-data
  commands for committed supported payloads and core/extra identities.
  Evidence: obj/distributor-workflow-tests-complete.log, distributor-publisher-tests.log
  and distributor-workflow-acceptance.log. Actual Debian deployment remains
  user-run; no SSH access or service/key/guest configuration changed by the agent.

## 2026-10-13 · Keyboard, terminal recall and TLS bootstrap usability

- User confirms full ~16 MB package download through the named HTTPS proxy and
  successful signature verification. Slow university Wi-Fi is reported separately
  from transfer correctness. TrueNAS automated backups are user-confirmed; backup
  scope and restore acceptance have not been independently verified. Newer server
  release publication remains the last distributor walkthrough step.
- Investigating user German Shift+7/Caps Lock workaround and absent arrows:
  released ScanMapBase applies Num Lock to main keyboard shift columns; dedicated
  navigation scans share keypad identity. Build-copy adaptations limit Num Lock to
  physical keypad scans and retain dedicated PS/2/virtio navigation identity.
  Local German map corrects #/ISO positions and logical Y/Z identity; terminal
  printable AltGr input bypasses control-shortcut dispatch.
- GUI terminal already implemented session history; corrected arrow translation
  restores access, and recall now preserves the current draft. It remains
  session-only. New tls bootstrap URL SHA256 combines bounded public PEM download
  and hash-checked persistent import, with explicit cleanup for Cosmos exception
  behavior. Existing TLS/hostname/package signature checks remain enabled; no
  private mirror/certificate is hardcoded into portable defaults.
- Release build and 573/573 host checks pass. Final native networking passes
  184/184, including bootstrap, rejection of bad hash/oversize/non-root requests,
  preservation of existing trust and installed trust persistence. Desktop checks
  pass 139/139. Dedicated keyboard acceptance passes 18 checks across PS/2/virtio:
  Shift+7 with/without Num Lock, Caps Lock, #/ISO punctuation, AltGr, arrows,
  history and draft restoration. Second fixup pass is byte-identical on System,
  HAL and HAL.X64. Initial focused harness failures were test construction errors:
  Caps Lock capitalized the logger command, then unquoted # became a shell comment;
  the corrected harness passes. CI includes both keyboard backends.
  Evidence: obj/input-trust-host.log, input-trust-build-final.log,
  input-trust-network-final.log, input-trust-desktop.log and
  input-trust-keyboard-accepted.log, all under obj. Actual VMware input acceptance
  remains pending. Existing USB keyboard mappings are not separately adapted here.
- Signed 0.0.6 with the existing trusted key (16,107,334 bytes), verified locally
  and byte-exact through the NAT relay. Kernel SHA256
  ad80525257a5e5dadcf370dc8cd1830ff6bcffb6b591fbd206f592468a404842;
  archive SHA256 7e485879c1b3699f90e6411294803918612331ac763e968913c0b2572985f742.
  Existing 0.0.5 remains immutable; server publication of a newer release is still
  pending the user's walkthrough. Existing guest TLS trust persists across updates.
- After all native checks completed, pruned 15 generated validation images,
  0.69 GiB allocated/3.03 GiB apparent; kept logs/screenshots/signatures/relay and
  VMware recovery data. Evidence: obj/input-trust-prune.log.

## 2026-10-13 · Named proxy HTTPS accepted in Zenith

- User created a dedicated self-signed certificate with DNS SAN
  zenith-stable.net.local and loaded its key/certificate into Zoraxy's Docker
  certificate mount. Host SNI inspection confirms the matching name; explicit
  certificate trust and hostname verification pass. HTTPS release receipt fetch
  succeeds on the laptop. Served PEM SHA256:
  d8469fd6e6476aaea598a215db683d754d9207072da0d907afd849cecac15464.
- User screenshot confirms Zenith fetch of the 0.0.2 release receipt completes
  with verified TLS 1.2 and 469 bytes; cat displays the expected receipt and
  signing public-key fingerprint. No certificate checks were disabled. Full
  archive transfer/signature verification remains a separate acceptance step.
- User-supplied distributor inventory: Debian 13.7 x86_64, 4 CPUs, 7.8 GiB RAM,
  44 GB disk free, .NET 10.0.401, Cosmos tools/patcher 3.0.89, all inventoried
  tools present and clean source at 2c939867994705aa3056a304d838f816c02c127d.
  Existing signing key matches trusted fingerprint. Signing directory ownership/
  permissions correction was supplied; execution is not yet confirmed.
- Next walkthrough step is backup destination/policy and restore verification,
  then full guest delivery and publication of a newer immutable release.

## 2026-10-13 · Relay restarted and distributor completion audit

- Follow-up: user reports the server was still booting during the initial failed
  connection. Supplied nginx status is active/enabled, nginx -t passes and port 80
  listens on IPv4/IPv6. After boot, laptop HTTP retrieval of
  /core/zenith-kernel/0.0.2/release.json succeeds and reports the expected public
  PEM fingerprint. Initial connection failure is resolved; full archive/guest
  HTTPS acceptance and source/tool/backup inventory remain separate checks.
- Restarted the VMware NAT HTTP relay on 172.16.43.1:8081. A complete HTTP download
  of 0.0.5 matches archive SHA256
  1bad237c67528cf0187dd7d19adb6ed352a319f872745f0fae55eeaa00407996.
  User installation/confirmation is pending; bootstrap still uses explicit sync
  because the older kernel remains running until reboot.
- Fresh laptop checks: 10.0.20.7:80 could not connect; the named HTTPS proxy was
  reachable but hostname verification rejected its Zoraxy Self-host certificate.
  No diagnosis of the VM service/firewall or guest transport success is asserted.
- Added a read-only distributor inventory script; bash syntax validation passes.
  Corrected outdated provisioning status and documented concrete remaining service,
  certificate, guest delivery and backup acceptance steps. SSH login/key access was
  requested for actual VM inspection; no server settings were changed.
- P4 signed online discovery/named installs remain project work; automatic builds
  remain deferred until usable package delivery/install/upgrade acceptance.

## 2026-10-12 · Checked automatic sync after kernel updates and before power changes

- User reports losing the system after forgetting explicit sync, and confirms using
  Zenith reboot/poweroff commands. Those commands already called sync/unmount/device
  flush; missing a separate sync is not an established explanation of corruption.
- Kernel install now additionally syncs all mounted filesystems before returning.
  SyncAll rejects a false filesystem sync result; Shutdown rejects failed unmounts.
  Device-flush exceptions already propagate. Hardware reset/poweroff is reached only
  after successful shutdown. Explicit maintenance-gate cleanup handles failures
  without relying on Cosmos unwind-finally, preserving retry/inspection access.
- Kernel-update native harness no longer runs manual sync before any poweroff;
  activation uses a plain reboot as well. It checks the next kernel loads, installed
  data survives and independent root/EFI audits remain clean. Host checks pass
  567/567, including both power actions refusing a flush error and another thread
  reacquiring the maintenance gate afterward. Release ISO builds. Native validation
  passes 169/169, including plain reboot into the new signed ELF and plain poweroff,
  independent clean ext2/EFI audits and interrupted-update/fallback recovery.
  Evidence: obj/auto-sync-{build,host,kernel}.log. No guarantee about arbitrary power loss or prior incident root cause is asserted.
- Signed kernel 0.0.5 is prepared with the existing trusted key, including the
  preceding progress UI. Kernel SHA256 2563d0027a676887a7fb9fbd2ee00b8175575a74c59368f1a5a581ed0fb220d4;
  archive SHA256 1bad237c67528cf0187dd7d19adb6ed352a319f872745f0fae55eeaa00407996.
  Local relay serves it separately from 0.0.3/0.0.4. For the currently running older
  kernel, chain `pkg kernel install FILE && sync && reboot` as a one-time bootstrap;
  after booting 0.0.5 the normal power commands handle checked flushing themselves.
  Native tests established the no-manual-sync path; actual VMware acceptance remains
  pending and the previous corruption cause is unconfirmed.

## 2026-10-12 · VMware kernel 0.0.3 confirmed

- User confirms 0.0.3 is installed and boot-confirmed. This closes its actual
  installed activation/confirmation gate; no independent VMware throughput result
  or named HTTPS proxy acceptance was reported.
- Next implementation priority is P4 signed online package discovery and delivery:
  signed core/extra indexes and manual publisher support, refresh/search/planning,
  bounded verified download and installed script/data transactions by name, followed
  by upgrade planning/apply and installed-reboot checks. Preserve P3's separate
  kernel confirmation/rollback boundary. Automatic builds remain deferred.
- Signed 0.0.4 already contains the progress UI; it is a separate optional update
  before testing that UI, not a prerequisite for developing the online client.
  Proxy certificate hostname setup remains a parallel deployment prerequisite for
  using zenith-stable.net.local through verified HTTPS.

## 2026-10-12 · Public signing key on local relay

- Copied the existing signing public key to obj/vpn-relay/public.pem; the running
  VMware NAT relay serves it at http://172.16.43.1:8081/public.pem.
- Package-tool kernel-check verifies relay kernel 0.0.3 against this key. HTTP
  download matches the source byte-for-byte; key PEM SHA256 is
  87a2af412ce0f90024dd17ae801912d31dd360c5c1b1e23cbbb59662a06fa25a.
- Only the public key was published; VM fetch/trust acceptance remains pending.

## 2026-10-12 · Package usability, named proxy and validation disk cleanup

- User is updating to 0.0.3; preserve that signed archive and the running local
  relay. Automatic build pipelines are deferred in favor of proper installed
  package discovery/download/install/upgrade and progress usability.
- User assigned https://zenith-stable.net.local through Zoraxy. Host resolves it
  to 10.0.10.3; SNI inspection returns the default self-signed Zoraxy Self-host
  certificate with no SAN, valid 2023–2038. Even trusting that PEM, independent
  OpenSSL hostname verification fails. A certificate with this DNS SAN is needed
  before importing the public trust PEM using tls trust. No trust was installed,
  certificate checks bypassed or proxy/server configuration changed. Package
  signing trust remains separate. Current deployment notes are in PACKAGE-SERVER.
- Interactive fetch uses one byte-percentage/bar line, throttled to at most five
  numeric updates per second with the final byte count forced. Data installs show
  file-application progress and backup/database phases; kernel updates show copy
  percentages and activation phases on one line. Completion/errors still print;
  progress is omitted from redirected stdout. No unsupported overall commit
  percentage is inferred from file progress.
- GUI terminal now handles carriage-return overwrite and ANSI erase-line, preserving
  CRLF, colors and normal append/wrap behavior. Explicit success/failure cleanup
  closes the progress line without relying on Cosmos unwind-finally. Host regression
  covers same-line replacement, redirection, final newline and transaction recovery
  if an observer cancels after file application.
- Removed 311 inventoried generated validation images: 21.42 GiB allocated and
  70.91 GiB apparent. obj shrank to 3.8 GiB physical/7.6 GiB apparent immediately
  afterward. Logs, screenshots, PCAPs, latest checked ext2 root, build caches,
  recovery originals and signed update/relay files are retained. Old removed disk
  evidence requires rerunning its harness to audit again. tools/prune-validation.py
  defaults to preview; --apply removes only named validation image families after
  all tests stop. Never prune a live test or remove the whole obj during updates.
- Host tests pass 565/565; Release ISO builds; native signed scripts/data package
  checks pass 71/71. Native network checks pass 180/180, desktop checks 135/135 and kernel
  activation/recovery checks 169/169.
  Evidence stays in obj/progress-ui-{host,build,network,packages,de,kernel}.log.
  The focused desktop screenshot shows one percentage/bar line and a completed
  2 MiB transfer in the guest; its first harness run missed wc's extra padding, and the
  repeatable terminal-progress-smoke.py now checks count/path with that whitespace
  accounted for. Final rerun passes. Real VMware progress/TLS acceptance is pending; P4 install-by-name is next work,
  not implemented by the progress/cleanup slice.
- Final cleanup removed 30 newly generated validation images (1.76 GiB allocated),
  retaining all new logs/screenshots/audits. obj is now about 4.0 GiB physical and
  7.7 GiB apparent. Signed 0.0.4 contains these UI changes, without replacing 0.0.3;
  its kernel SHA256 is 555ef16604659298e621b245be48a55706331c825ec7324e4ab60ba628643b13,
  archive SHA256 40e5441b769f7f67798c95b7e815f2dbe64acb03a00cd7eaa913f3615de43f88.
  User should complete/confirm 0.0.3 before another activation. No server deployment.

## 2026-10-12 · Local transfer throughput first

- User prioritizes transfer speed; repository-driven package downloads/upgrades and
  automatic build-pipeline discussion are deferred. Boot UI is outside this slice.
- Added a burst HTTP benchmark on RAM ext2 and a disposable installed disk, with
  Ethernet PCAP window checks, exact disk payload comparison and independent clean
  e2fsck after shutdown. Original 4 MiB TCG timings: RAM 24.42 s (172 kB/s),
  disk 24.76 s (169 kB/s). This points to substantial receive-path overhead.
- Fetch drains productive reads immediately, sleeps only without progress, and
  reuses one bounded read array per attempt. Existing timeout, retry, cancellation,
  staging and destination-preservation policies remain unchanged.
- TCP advertises at most 32,768 bytes, shrinking/reopening with unread data while
  retaining the 128 KiB unread cap. Immutable 4 KiB chunks avoid copying the whole
  unread window on every append/advance; held read snapshots stay stable.
- PCAP exposed a separate outgoing-payload bug: SocketPlug.SendTcp used the peer's
  send window in request headers. Both payload constructor sites now use the local
  receive window, through a signature-checked build-copy adaptation. The old PCAP
  fails the new guard; final live/installed captures pass (maximum window 32,768).
  Plugs adaptation runs explicitly before the native compiler's cache check.
- Removed pinned successful IPv4/TCP packet traces and their initial per-packet
  formatting expression; error logs/counters remain. Repeat adaptation of copied
  System/Plugs DLLs is byte-idempotent. Upstream/global packages are unchanged.
  Ext2 uses rotating block-allocation hints, preserving freed-hole/wrap coverage;
  metadata writes and flush guarantees are not deferred.
- Final TCG measurement: RAM 4 MiB in 6.11 s (686 kB/s, 4.00x original RAM
  baseline); installed 16 MiB in 87.94 s (191 kB/s). Disk bytes match exactly and
  e2fsck is clean. An intermediate 4 MiB disk run measured 10.68 s (2.32x baseline).
  Larger installed writes still slow progressively: this is a remaining storage/
  allocation performance concern, not a demonstrated fully resolved throughput
  ceiling. These emulator timings do not establish VMware or direct VPN performance.
- Host suite passes 561/561; targeted filesystem tests pass 109/109. Release ISO
  builds. Final native regression passes 180/180 (HTTP/HTTPS, faults, cancellation,
  destination preservation, installed reboot and independent ext2 audit). CI adds the 16 MiB installed benchmark and retained PCAP/audit evidence;
  hosted CI awaits push. Final evidence is retained under ignored obj/transfer-*
  (build/host logs, transfer-bench-reuse/results.json and transfer-network-reuse.log).
- Prepared signed kernel package 0.0.3 with the existing production key and verified
  its signature. Kernel SHA256 a8410a0d1f91d843fd42019f953defeb8feff7d28f0728a90c5bcaebc47d6346;
  archive SHA256 dca517a2461b0fc529fd0b83b903f95b025613940e4845b7de06921869716fa8
  (16,089,406 bytes). Local VMware NAT relay 172.16.43.1:8081 serves exact verified
  bytes from obj/vpn-relay. Nothing was deployed to 10.0.20.7. Real VMware retry is pending. Version
  0.0.3 identifies this local update; a different subsequent kernel must use a newer version.

## 2026-10-12 · Slow VPN kernel fetch and stalled transfer investigation

- User confirms nc reaches distributor 10.0.20.7:80, then reports direct VPN
  kernel fetch advancing to about 10/16 MB before retry and stalling near 6 MB
  on the second attempt. Exact final error/time and guest TCP capture are pending.
  The previous --kernel limit was 180 seconds total with 3 seconds idle.
- Kernel transfers now allow 15 minutes total and 30 seconds idle per attempt;
  ordinary downloads retain 30/3 seconds. HTTPS body reads honor the same bounded
  deadline and remaining total budget. HTTP timeout errors distinguish idle/total
  expiry and report received/expected bytes. Staging, two attempts, cancellation,
  payload bounds and destination preservation remain unchanged.
- Host comparison uses the actual WireGuard route (MTU 1420): the same signed
  0.0.2 archive downloads in 9 seconds (16,082,678 bytes, HTTP 200) and verifies
  against the user's existing local public key. This demonstrates available host
  VPN throughput; a guest TCP/latency/loss issue remains possible. The timing
  change alone does not establish resolution of a sustained stall. Header capture
  requires user sudo access; sudo -n refused without credentials, no capture made.
- Started a temporary local relay on VMware NAT host 172.16.43.1:8081 serving only
  the verified archive, allowing the old installed guest to bypass the VPN segment
  for this download. This is not the permanent mirror or a guest networking fix.
- Release ISO builds; host tests pass 559/559, including old/new timeout budgets
  and diagnostic boundaries. Native regression passes 180/180 under TCG,
  including HTTP/HTTPS kernel fetch across a four-second pause, ordinary stall
  rejection, destination preservation, cancellation, link loss, installed reboot
  and independent clean ext2 inspection. Evidence: /tmp/zenith-vpn-fetch-host.log,
  /tmp/zenith-vpn-fetch-build.log, /tmp/zenith-vpn-fetch-network.log,
  /tmp/zenith-vpn-host-download.log. Actual user retry/relay acceptance is pending.

## 2026-10-12 · Installed VMware startup hang during fetch investigation

- User reports installed disk boot hanging at "Enabling interrupts", confirms
  no new kernel was installed, and reports the disk RAM rescue entry also hangs
  while ISO boot works. Disk rescue loads the retained disk kernel; it does not
  substitute the ISO kernel. Filesystem corruption is a user hypothesis, not
  established by these observations.
- Current source emits that line in Cosmos Kernel.Start before BeforeRun;
  Zenith's OnBoot is empty and mounts/accounts/network initialization run in
  BeforeRun. A hang there does not demonstrate failure in root mounting.
  Next evidence is boot status/mount output from the working ISO normal entry,
  which can establish whether the installed root is actually mounted and readable.
  No disk repair, format, boot metadata rewrite or forced update was performed.
  Boot recovery takes priority over packet capture/direct VPN acceptance.
- ISO screenshot shows running kernel 88c80ae4d800f512..., root=live/legacy,
  EFI unavailable, RAM root/tmp and the default user home. It therefore does not
  establish installed-root readability. Forced RAM selection, missing disk or
  rejected filesystem/root discovery remain possibilities. Next checks are the
  selected ISO entry, lsblk and check-only fsck.ext2 of the unmounted root.
- User's manual mount screenshot used the default FAT driver, so its error did
  not identify the root as FAT. Subsequent fsck.ext2 sata0p1 reports "ext2 volume
  is not clean" at the profile/state gate; no inode/block consistency audit has
  yet run. The partition is recognized as supported-profile ext2, but dirty versus
  error-marked state and actual consistency require independent inspection.
  Requested the VMX/folder path and full power-off before copying/auditing VMDK;
  e2fsck, qemu-img and losetup are available locally. Original disk remains untouched.
- User supplied /home/azazel/VMs/Zenith. Confirmed no vmware-vmx process and active
  SATA image Zenith-0.vmdk (1 GiB monolithicSparse, no backing image). Made a raw
  diagnostic copy and verified the original SHA256 stayed
  c486467054e784f88bbfa23927aa767ecbb6ccb7844b4943fe92e59c0aa4e6ab.
- Independent e2fsck -f -n finds real errors: inode 68 size/block accounting and
  one leaked block bitmap entry (6148). ncheck identifies only the incomplete
  /root/zenith-kernel-0.0.2.zpk.fetch-80E7EA206BF3780E at about 6 MB. GPT and
  retained kernel/EFI hashes/menu bindings verify. No completed package activation
  occurred; exact interruption cause is not established.
- Tested e2fsck repair on a second copy, then read-only audit passes (exit 0).
  passwd/shadow, pinned package key and boot-generation state retain exact bytes.
  The unmodified-copy TCG boot passes interrupt enable and reports requested-root
  mount failure; the repaired-copy boot loads the same retained kernel, mounts
  sata0p1 and reaches installed headless login. Cosmos disables early graphical
  logging before BeforeRun, so the last visible interrupt message did not locate
  the actual failure. Earlier interrupt-path suspicion was not a confirmed cause.
- Prepared separate Zenith-recovered.vmdk under ignored obj/vmware-recovery-3o35_wr3,
  with private recovery-directory/file permissions. Raw→VMDK→raw round trip is
  byte-exact, and original VMDK/VMX remain unchanged. Actual VMware boot of the
  recovered image was initially pending; no original-disk repair or configuration
  switch was made by the agent. User now reports the repaired disk boots again
  in VMware, confirming recovery acceptance. Cleanly shut down before relocating
  the active recovered VMDK from build scratch into the permanent VM folder and
  updating the VM's disk reference. The original VMDK remains the pre-repair backup.
  Evidence: that directory's e2fsck logs and copied serial boot transcripts;
  /tmp/zenith-vmware-original-boot.log and /tmp/zenith-vmware-repaired-boot.log.
- User confirmed the VM is off and authorized permanent relocation/configuration.
  With no vmware-vmx process, moved the active recovered VMDK into
  /home/azazel/VMs/Zenith/Zenith-recovered.vmdk and verified identical bytes
  (SHA256 515ab9c501d194c6f30f36bf44bc1a58af1c7daf3e77af0982392512a375f864).
  Backed up Zenith.vmx as Zenith.vmx.before-disk-move-20261012-8481420e and atomically
  changed only sata0:0.fileName to the relative recovered filename. The scratch
  source is removed; original Zenith-0.vmdk still matches the pre-repair checksum.
  Workstation must reload the VM configuration before its next start.
- User confirms boot works after permanent disk relocation and chooses the local
  relay as the temporary delivery workaround. Rechecked 172.16.43.1:8081 HTTP 200,
  byte-exact archive delivery and valid signed 0.0.2 kernel identity
  b0b4285568ccf4cd95b6569d25c6ad4b25815edccf688a3b189f6765e55f875b.
  User subsequently reports 0.0.2 installed and loaded, then confirms completing
  pkg verify, boot confirm and boot status. This is user-confirmed installed
  upgrade/activation acceptance through the local relay; no fresh status transcript
  was supplied. The previous pending generation correctly blocked another upgrade
  until confirmed. Direct VPN guest throughput/stall investigation remains open;
  the relay bypasses it rather than resolving it.

## 2026-10-12 · Distributor guest reachability investigation

- Package server address is 10.0.20.7, with no DNS name assigned. User reports
  laptop ping succeeds but Zenith VM ping fails; DNS server 10.0.10.3 is reachable
  from the VM. This does not yet distinguish ICMP filtering, guest route/mask,
  inter-subnet policy or a guest stack issue. TCP/HTTP delivery is unverified.
- Next evidence: current guest ip status, VMware NAT/bridged attachment, TCP port
  80 test from guest/laptop and, if needed, packet capture on the distributor.
  No network configuration or firewall changes have been made.

## 2026-10-12 · Debian distributor filesystem-tool lookup

- User's Debian 13 build reaches host tests: 541 pass, 17 fail because e2fsck or
  debugfs cannot be found. These are external filesystem inspection tools, not
  demonstrated filesystem assertion failures. Package installation/PATH still
  needs confirming on that VM; ordinary Debian users may lack /usr/sbin in PATH.
- Builder now adds standard sbin paths and checks host/native build prerequisites
  before testing, with an actionable missing-tools message. No tests are skipped.
  Documented the immediate e2fsprogs/PATH repair for the user's older checkout.
  Validation: shell syntax and controlled dependency/PATH preflight checks pass:
  starting with both filesystem tools absent from PATH, the script finds them
  under /usr/sbin; an unavailable prerequisite fails clearly before testing.
  User reports the new release worked after the repair, confirming the distributor
  build retry succeeded. No fresh logs, nginx/LAN fetch or installed 0.0.2 boot/
  confirmation evidence was supplied; those remain separate acceptance checks.
  No SSH endpoint/access has been supplied.

## 2026-10-12 · Debian package build/distribution kit (P2)

- User reports the signed kernel update worked in the installed VMware system;
  explicit boot-confirm status was not supplied. User requests a dedicated VM
  that both builds and distributes packages, and will provision it from a Debian
  template. Version, IP/name, SSH access, LAN/VPN subnets and backups are pending.
- Added a manual clean-commit kernel build/host-test/sign/publish workflow pinned
  to released Cosmos dependencies. It reuses the existing signing key, retains
  logs/source commit, serializes builds and does not pull arbitrary branches or
  activate guest updates. The trusted release account handles build/sign/publish;
  there is no isolated worker for untrusted source.
- Added a verifier/publisher for core kernels and core/extra signed data packages:
  bounded input, signed identity checks, private-key/symlink/path rejection,
  external staging, file/directory flushes and atomic directory activation.
  Versions are immutable, concurrent publication fails clearly and private keys
  stay outside the served tree. release.json is an unsigned operator receipt,
  not a signed repository index or automatic guest update channel.
- Added Debian instructions and nginx static config (GET/HEAD, no browsing or
  symlinks), existing-key/source transfer, manual guest workflow and backup scope.
  Initial transport is LAN HTTP with mandatory package signatures; HTTPS depends
  on the supplied hostname/CA policy. No remote service, DNS/firewall or VM was
  changed. P4 indexes/recipes/native tool unbundling remain separate milestones.
- Publisher integration tests pass 10/10 using real signed kernel/data fixtures,
  byte-exact HTTP fetch/reverification, tamper/wrong-key/identity rejection,
  immutable versions, invisible/failed staging and concurrent publisher refusal.
  Shell/Python syntax and `git diff --check` pass. CI includes the publisher suite;
  hosted CI awaits push. The full workflow also passes against committed source
  d24a4ae: 558/558 host tests, Release ISO/ELF build, real ELF-sized signing,
  signature verification and complete atomic publication. It used a disposable
  test key and temporary root; it did not alter the user's signing key or guest.
  Actual Debian
  provisioning/nginx syntax and VM/LAN/VPN/Zenith acceptance remain pending.
  Evidence: /tmp/zenith-distributor-tests.log, /tmp/zenith-distributor-workflow.log
  and obj/distributor/release-Zkjx62rf/{tests,build,package-tool}.log.

## 2026-10-11 · Receive polling when NIC interrupts are absent

- User's VMware status screenshot shows initial receive-filter validity true,
  NIC/RX interrupts zero, received/accepted frames zero and DHCP replies zero.
  With the earlier correlated host offers this isolates failure before guest
  DHCP parsing; it rules out the missing address-valid flag for this VM. Exact
  PCI/platform interrupt-routing or hardware receive cause remains unconfirmed.
- Registered the E1000E receive drain with the timer-driven kernel loop. Polling
  masks interrupts with a restoring scope and explicitly disposes on success or
  failure, serializing IRQ/poll consumption under the current single-CPU guest
  profile. Both paths drain at most one 32-descriptor ring per invocation.
  Polling leaves ICR and normal interrupt configuration untouched. `ip` adds
  poll-call and poll-delivered-frame counts. Adaptations affect build copies only.
- Release ISO builds; host regression passes 558/558. Native replay passes 38/38
  under TCG: normal unicast/broadcast offers, then a separate guest with PCI INTx
  deliberately disabled through qtest before DHCP reacquisition. That guest's
  NIC interrupts stay at 2 while polling delivers two new frames and the real
  Offer/ACK is accepted. No production fault switch was added. Kernel ELF hash
  for that run is e80e0300993504fee1aff6d33ac5b5434f2e299a7d67d0c6a521d29c29ed927d.
- Full native network regression passes 174/174 under TCG, including live/installed
  DHCP, wire captures, HTTP/TLS, fault/cancel/link loss, reboot persistence and
  independent clean ext2 checks. Evidence: /tmp/zenith-rx-poll-build.log,
  /tmp/zenith-rx-poll-host.log, /tmp/zenith-rx-poll-receive.log and
  /tmp/zenith-rx-poll-network.log. The new
  ISO tests the updated kernel without changing the installed disk's active kernel.
  Final source cleanup strengthens patch contract checks and updates a source
  comment; the Release rebuild passes with ELF hash
  88c80ae4d800f512fc8c7e450ab93bb605eed9520aaa1cd11a66c15ed9a800f9.
  Final-artifact DHCP replay passes 38/38, including interrupt-disabled acquisition;
  evidence is /tmp/zenith-rx-poll-incremental.log and
  /tmp/zenith-rx-poll-final-receive.log. Python syntax and `git diff --check` pass.
  CI already runs this replay fixture; hosted CI awaits push.
- User reports networking now works in VMware after the polling fix. This is
  user-confirmed recovery of the reported DHCP/networking failure; no new guest
  status, DNS/HTTPS results or disk-kernel update/reboot proof were supplied.
  The precise interrupt-routing cause remains unresolved. Booting this ISO does
  not permanently activate the fixed kernel on the installed disk; that remains
  a signed kernel package upgrade followed by disk boot and confirmation.

## 2026-10-11 · VMware DHCP receive investigation

- User confirms the fixed kernel hash and Ethernet broadcasts; VMware now returns
  correlated offers for 172.16.43.141 with a 1800-second lease, gateway/DNS
  172.16.43.2. Offers are unicast at Ethernet despite broadcast IPv4. Zenith still
  reports no accepted offer. Added the exact observed BOOTP/options profile to
  host tests and a raw-Ethernet native fixture for unicast and broadcast replies.
- Found a separate E1000E fast-path defect: a prepopulated RAL0/RAH0 MAC returns
  without activating RAH0.AV. Added a contract-checked adaptation of the copied
  x64 HAL reference and read-only status counters for driver-delivered/accepted
  frames, initial receive-filter validity, NIC/RX interrupts, DHCP replies/rejections
  and last reason.
  The SDK skips HAL.X64 in its plug-filtered list, so an explicit post-RunPatcher
  target applies the adaptation before ILC cache hashing; a first integration build
  demonstrated that applying later can incorrectly retain the old compiled driver.
  Global packages/upstream remain untouched.
- Release ISO and incremental builds pass. Incremental builds activate the copied
  driver before cache hashing and retain the same ELF hash (f23d32beea9cf45b...);
  no stale ILC artifact remains. Host tests pass 558/558, including the observed
  VMware offer fields. The raw-Ethernet fixture passes 22/22 under TCG with real
  NIC/RX interrupt observations and accepted Offer/ACKs for both destination types.
  Final full network regression passes 174/174, including live/installed wire
  captures, TLS, link loss, persistence and independent clean ext2 inspection.
  Capture-checker tests pass 5/5; Python syntax and `git diff --check` pass.
- Evidence: /tmp/zenith-dhcp-receive-build-final.log,
  /tmp/zenith-dhcp-receive-incremental.log, /tmp/zenith-dhcp-receive-host.log,
  /tmp/zenith-dhcp-receive-irqs.log and /tmp/zenith-dhcp-receive-network-final.log.
  CI includes the replay fixture; hosted CI awaits push. Subsequent VMware status
  confirms initial address-valid true and no receive interrupts or frames; see
  the polling investigation above. The filter defect did not explain this VM.

## 2026-10-11 · VMware DHCP broadcast regression

- User's host capture shows valid DHCP Discover IP/UDP payloads sent to Ethernet
  destination 00:00:00:00:00:00; no offers returned in VMware NAT or bridged mode.
  Cosmos 3.0.89's limited-broadcast enqueue path bypasses ARP without assigning
  the destination MAC. QEMU/slirp acceptance masked this invalid Ethernet header.
- Added a contract-checked build-copy adaptation assigning MACAddress.Broadcast
  only to limited IPv4 broadcasts, preserving unicast/ARP behavior. No upstream
  checkout or dependency cache edits. Added native live/installed wire captures
  checking DHCP Discover and Request broadcast destinations and source MACs.
- Release ISO build passes; packet-capture checker tests pass 5/5, including rejection
  of the user-observed zero MAC and truncated/mismatched frames. The rebuilt live
  guest obtains a lease and its captured Discover/Request both use the correct
  broadcast MAC. Full native networking regression passes 174/174 under TCG,
  including live/installed wire checks, DNS/HTTP/TLS, cancellation/link loss,
  reboot persistence and independent clean ext2 inspection. Python syntax and
  `git diff --check` pass. CI includes the capture-checker tests and native assertions;
  hosted CI and the user's VMware retest remain pending. The new ISO can test the
  fixed kernel with the existing disk attached; it does not update the disk's
  selected kernel. Permanent activation still uses a signed kernel package.
  Evidence: /tmp/zenith-dhcp-broadcast-build.log and /tmp/zenith-dhcp-broadcast-network.log.

## 2026-10-11 · Sunrise blocked-launch message

- User reports a VMware Workstation installation booted successfully; independent
  VMware persistence, networking and kernel-upgrade acceptance remain pending.
- Found that opening Sunrise outside a root live session reused the failed-write
  screen, falsely claiming installation did not complete without attempting a write.
  Added a separate unavailable screen with neutral live-session guidance and a
  `boot status` hint. Actual worker failures retain the failure screen.
- Release ISO build passes after adding the documented `~/.dotnet/tools` path;
  first attempt could not locate `cosmos.patcher`. `git diff --check` passes.
  Evidence: /tmp/zenith-sunrise-status-build.log. The changed screen has not yet
  been visually verified in VMware; no installer write logic changed.

## 2026-10-08 · Repository initialization

- Inspected the clean checkout, boot/storage architecture, host tests, smoke
  harness and milestone documentation. Existing Git history retained.
- Added shared `INSTRUCTIONS.md` with linked `AGENTS.md` and `CLAUDE.md` entry points.
- M0/M1 remain complete as recorded in the roadmap. M2 is the active milestone.

## M2 · Unix filesystem semantics (in progress)

| Slice | Status | Evidence / next step |
|---|---|---|
| `/proc`: meminfo, mounts, uptime, cmdline | Complete for initial scope | Read-only synthesized inodes, first-read snapshots, boot mount, six host tests and smoke coverage |
| `/dev`: null, zero, random, block devices | Partial: null/zero plus read-only block nodes | Dynamic disks/partitions implemented; entropy and raw writes pending |
| Read-write ext2 | Verified for supported bounded profile | Secondary and installed roots, allocation/reclamation, symlinks, ownership/modes and directory moves; 512 MiB volumes, double-indirect I/O; platform unlink behavior documented |
| Files desktop app | Complete for initial scope | 292 host tests; native keyboard/mouse workflow and independent persisted-file check pass |
| ext2 formatter | Verified for bounded secondary partitions | 306 host tests; 88/88 baseline + 88/88 large-profile QEMU checks; clean independent e2fsck |
| Runtime heap under large ext2 I/O | Verified for current stress workload | Proactive physical-page reserve; 9 MiB write/delete and subsequent commands pass at 512 MiB; general graceful OOM remains open |
| ext2 symbolic links | Verified for bounded targets | 332 host tests and 101/101 QEMU checks; inline/block-backed targets, dangling links, loops, remount and deletion; .. resolution remains unsupported |
| ext2 ownership/modes and metadata commands | Verified for secondary ext2 | 355 host tests; 114/114 checks in both Release QEMU profiles; persisted 32-bit IDs, chmod/chown, stat and ls -l; enforcement pending |
| CI keyboard injection | Fixed and verified locally | GC-aware pacing and explicit key release; both CI-equivalent Release profiles pass; hosted rerun awaits push |
| ext2 directory moves | Verified for secondary ext2 | 361 host tests; 122/122 Release large-profile QEMU checks; parent links, actual .. record, inode preservation and cycle refusal |
| ext2 installer root | Verified | FAT32 ESP + bounded ext2 root; both Release profiles 135/135, disk-only UEFI 7/7, reboot persistence and clean host e2fsck |
| Users/login, shadow, passwd, su, useradd | Pending | Persistent numeric ownership implemented; credentials/login/authentication pending |
| VFS permission enforcement | Pending | Audit every file-operation entry point |
| check-only fsck.ext2 | Verified for supported profile | Read-only consistency audit; corruption/no-write host regressions and native checks pass; repairs require host e2fsck |
| Optional ext4 read-only | Deferred | After required M2 scope |

M2 is not complete until an installed ext2 system enforces real users and
permissions and reports accurate metadata through `ls -l`.

### First M2 slice · `/proc`

- Added a Cosmos `IVfsFilesystemType` implementation mounted after root and `/tmp`.
  `meminfo` reads page-allocator totals/free pages; `mounts` reads the VFS table;
  `uptime` uses the boot stopwatch; `cmdline` reads the Limine command-line pointer.
- Files are case-sensitive and read-only. Each open handle samples on its first
  nonempty read (or end-relative seek) and releases the snapshot on close. Supports
  partial reads, EOF and seek; metadata uses directory 0555 / file 0444 and zero virtual file size.
- Deliberate API limits: no CPU idle counter, so uptime has one elapsed-seconds
  field; no retained mount flags in Cosmos, so mount options describe the current
  proc (ro) and FAT (rw) drivers. No per-process entries until the process model.
- Validation: `dotnet restore tests/Zenith.Tests --ignore-failed-sources` passed;
  `dotnet test tests/Zenith.Tests --no-restore` passed **204/204**, including six
  filesystem tests. Existing xUnit1031 warnings remain in shell/script tests.
- `dotnet publish -c Debug -r linux-x64 -o output-x64 --no-restore` passed and
  produced the ISO/ELF. Existing Cosmos patcher/layer warnings remain.
- Both VSTest and the NativeAOT task host require local IPC unavailable inside
  the sandbox; rerunning outside it succeeded.
- The first QEMU run passed 29/30 checks: the expected `proc-write=1` was present
  in raw serial output but lost when the harness stripped an interleaved Cosmos
  log tag. Updated marker matching to check both raw and cleaned output; three
  Python regression tests pass and run in CI. The fresh full smoke run passed
  **31/31 checks**, including all six `/proc` checks, installation, panic and clean
  poweroff. Evidence: `/tmp/zenith-m2-smoke-final.log` and
  `/tmp/zenith-m2-smoke-final/serial.log` (local scratch artifacts, not committed).
  Nonempty boot-command-line contents are not yet covered by the guest harness.
- Next after this slice: `/dev` (null/zero first, then entropy and block-device
  semantics), followed by ext2 groundwork. M2's ext2/users/permissions exit remains open.

### Second M2 slice · `/dev/null`, `/dev/zero` and bounded binary copy

- Added a fixed `/dev` VFS mount after `/proc`, with character-device metadata
  (0666, conventional major/minor IDs) and read-only directory entries (0555).
  `null` always returns EOF; `zero` fills each requested buffer and never reaches
  EOF. Both discard writes, store no data and allow no-op seek. The pinned Cosmos
  PAL requires a zero-size truncation callback when opening for shell output;
  this is accepted as a no-op. Nonzero resizing and other metadata changes fail.
  Read-only mounts reject device writes. Namespace changes fail.
- Added `dd if=INPUT of=OUTPUT count=N [bs=N]`: binary copying with a required
  finite block count, 512-byte default blocks, a 1 MiB buffer limit, EOF handling,
  and cancellation between I/O calls. Operands are validated before output is
  truncated; identical and case-only aliases are rejected for current FAT roots. This is a documented subset
  with file/device operands, not a stdin/stdout implementation.
- Added a manual page and refreshed `man hier`. Text filters still read entire
  inputs; use bounded `dd` for `/dev/zero`, rather than `cat`/`head`.
- Validation: **223/223** C# tests pass (19 new cases), and **3/3** Python
  serial-matcher regressions pass. Existing xUnit1031 warnings remain unchanged.
  ISO build passed with the existing Cosmos warnings. Eight additional QEMU
  checks cover device I/O, denied namespace changes, FAT alias rejection and
  Ctrl+C cancellation. The initial guest run exposed the zero-truncation callback
  requirement (output redirection failed); it was stopped for the fix. The fresh
  full guest run passed **39/39 checks**, including all eight new checks, install,
  desktop interactions, panic and clean poweroff. Local evidence:
  `/tmp/zenith-dev-smoke-final.log`, `/tmp/zenith-dev-smoke-final/serial.log`,
  `/tmp/zenith-dev-tests.log` and `/tmp/zenith-dev-build.log` (not committed).
- Remaining `/dev` work: entropy source plus random-device contract, and block
  device I/O with partition bounds, mounted-volume safety and hot-unplug behavior.
  No random or raw-disk node is exposed by this slice.

### Third M2 slice · Read-only disks and partitions under `/dev`

- Added live disk/partition nodes using Cosmos names (`sata0`, `sata0p0`,
  `sata0p1`, etc.). The kernel captures storage tables once per enumeration and
  excludes partitions whose host is absent. Node identity persists while the
  device instance remains present; removed instances are evicted on enumeration.
- Block nodes expose 0444 block-device metadata, mount-local device IDs and byte
  capacity. Reads handle unaligned offsets and partial sectors, batch aligned
  sectors, return short reads at EOF, and cannot exceed device/partition bounds.
  Seeks accept positions from zero through capacity, rejecting overflow and
  out-of-range positions. Sector buffers are limited to 1 MiB; geometries whose
  byte capacity exceeds `long.MaxValue` are omitted.
- Raw writes and every truncation/metadata change are rejected, including when
  the source is unmounted. This avoids aliasing writes behind mounted filesystem
  caches. Writable raw devices need coordination with mounts/partition rescans
  before they can be enabled. Ordinary reads can observe concurrent filesystem
  changes; they are not a disk snapshot.
- Presence is checked by device reference before each backing read, seek and
  flush. Old handles fail after removal/rescan or same-name replacement; driver
  I/O failures propagate. Host tests simulate removal between sector calls.
  Physical USB unplug behavior still requires hardware/guest hotplug validation.
- Entropy finding: Cosmos' `InteropSysPlug` currently routes its secure-random
  plug to timer-mixed XorShift. No supported entropy service was found. Random
  nodes remain pending; do not use this plug for `/dev/random` or password salts.
- Validation: **240/240** C# tests pass (17 new block-device cases), and
  **3/3** Python harness tests pass. Restore succeeded from cache with NU1900
  because the NuGet vulnerability endpoint was unreachable; existing xUnit1031
  warnings remain. The first build permission review timed out before execution;
  its permitted retry built the ISO successfully (existing Cosmos warnings).
  The first guest run exposed a Cosmos cached-interface dispatch failure at cell
  `FFFFFFFF80380A60`, mapped by the ELF symbol table to the contravariant
  `ReferenceEqualityComparer` call through `IEqualityComparer<IBlockDevice>`.
  Replaced it with an explicitly typed identity comparer. The fresh full QEMU
  run passed **46/46 checks**, including all seven new disk/partition checks,
  existing character devices, installation, desktop interactions, panic and
  clean poweroff. Local evidence: `/tmp/zenith-block-tests.log`,
  `/tmp/zenith-block-build.log`, `/tmp/zenith-block-smoke-final.log`, and
  `/tmp/zenith-block-smoke-final/serial.log` (scratch artifacts, not committed).
- Remaining: kernel entropy service/random device, coordinated raw writes, then
  ext2 groundwork. Shell `stat`/`ls -l` still use their existing generic display;
  truthful type/owner/permission presentation remains part of the M2 exit work.

### Fourth M2 slice · Experimental ext2 secondary volumes

- Verified the installed Cosmos 3.0.89 package already contains an experimental
  ext2 driver. Initial integration exposed an allocator defect; Zenith now
  adapts the upstream source locally, retaining its BSD license and source
  revision (also available through `man licenses`). Package versions and the
  sibling Cosmos checkout are unchanged. `mount -t ext2 PARTITION DIRECTORY` explicitly selects it;
  the existing two-operand mount still defaults to FAT. The mount table now
  names proc/dev/ext2 correctly. Added `man mount` and updated developer docs.
- Added a host-testable superblock gate: clean revision 1, 1 KiB blocks,
  128-byte inodes, filetype-only features, basic geometry and capacity checks.
  Unsupported devices are rejected before reading; probing reads only the
  superblock and performs no writes. This is a profile check, not fsck or a
  guarantee of consistency for arbitrary images.
- The QEMU harness creates a second disposable disk with an MBR Linux partition,
  formats it independently with host `mke2fs`, and seeds a host-created file.
  Guest checks cover rejecting FAT as ext2, reading host content, file/directory
  creation, case-sensitive names, rename/delete, a 20 KiB file using indirect
  blocks, remount persistence and final unmount. After QEMU exits, check-only
  host `e2fsck -f -n` and `debugfs` verify consistency and persisted file content.
  CI installs e2fsprogs; `--keep` retains the ext2 disk and check log.
- Validation: **265/265** C# tests (25 new cases), **3/3** Python harness
  tests, ISO build and pristine host-fixture e2fsck pass. Existing xUnit1031,
  Cosmos build and cached NU1900 vulnerability-endpoint warnings remain.
  The first QEMU attempts exposed automatic IDE port conflicts and then a
  changed disk enumeration order. Both disks now have explicit free SATA
  ports and units. Startup stderr is reported, and failed boots count skipped
  command checks as failures instead of reporting them as passes.
- The initial complete guest run passed all guest assertions but failed host
  e2fsck (**55/56**): the first new file reused the host seed's block, with
  multiply-claimed blocks and a bitmap mismatch. The allocator omitted
  `FirstDataBlock` when translating group bitmap bits. The local adaptation
  corrects allocation/free offsets, last-group bounds and group counting.
  Regression tests preserve an existing block, verify allocate/free/reuse,
  exclude the pre-data block from group counts, and reject allocation beyond
  the final block. Host verification also checks the seed remains unchanged.
- The fresh full QEMU run with the allocator correction passed **56/56**
  checks, including independent e2fsck (exit 0), persisted content and unchanged
  host seed. The final ISO build also includes the bundled license manual page.
  Local evidence: `/tmp/zenith-ext2-tests.log`, `/tmp/zenith-ext2-build.log`,
  `/tmp/zenith-ext2-fixed-smoke.log`, and
  `/tmp/zenith-ext2-fixed-smoke/ext2-check.log` / `ext2-disk.img` (not committed).
- Root discovery and installation remain FAT32. Next ext2 work: validate broader
  inode semantics (symlinks, ownership/mode changes and timestamps), multiple
  block groups, allocation limits and formatter interoperability before moving
  the installer/root. Users/permissions, check-only guest fsck and entropy remain
  pending; M2 stays open.

### 2026-10-09 · Fifth M2 slice · Ext2 resize and deletion lifecycle

- Replaced truncation's logical-block scan with bounded-depth pointer-tree
  pruning. Shrinking/unlinking releases data and empty indirect tables, preserves
  retained sparse branches and keeps inode sector counts accurate. Both shrink
  and growth clear the partial-block tail so bytes beyond the old EOF cannot
  reappear. Fast symlink deletion never interprets inline target bytes as blocks;
  generic size changes on symlink inodes are rejected.
- Successful overwrites now update mtime and ctime as well as extending writes;
  actual size changes update both timestamps. Empty tables from failed/short
  allocation are reclaimed and persisted. Writes and size changes reject sizes
  beyond the direct/single/double mapper (67,383,296 bytes for 1 KiB blocks).
  Triple-indirect file I/O remains unsupported; this does not implement permissions.
- Added 13 host lifecycle cases. The initial seven cases reproduced **six
  failures** in the previous driver: leaked single/double tables, stale tail
  bytes across remount, unsafe fast-link block freeing and unchanged overwrite
  timestamps. The corrected full suite passes **278/278**; **3/3** Python
  harness regressions pass. Existing xUnit1031 and cached NU1900 warnings remain.
- Expanded the independent host fixture with a 20 KiB file to delete and a sparse
  file whose last block uses double indirection. Seven added guest checks cover
  truncation, deletion, a 300 KiB double-indirect file create/delete cycle,
  empty directory removal, and truncated contents after remount. Host verification requires clean check-only
  e2fsck plus exact single-data-block counts and absent indirect pointers in
  retained truncated files; it still checks persisted content and the host seed.
- The first integration run passed guest checks but failed host e2fsck
  (**61/62**): a freed inode had zero deletion time. Inode teardown now persists
  zero links and dtime before freeing its bitmap bit. Directory removal uses the
  same tree reclamation path rather than freeing only its first block. Added
  regressions for deletion metadata after remount and expanded empty directories.
- The final ISO build and fresh full QEMU run pass **63/63** checks, including
  all seven new guest checks, clean independent e2fsck (exit 0), exact retained
  inode block counts, persisted content, unchanged host seed, existing install,
  desktop, panic and clean poweroff. Local evidence:
  `/tmp/zenith-ext2-lifecycle-tests.log`, `/tmp/zenith-ext2-lifecycle-build.log`,
  `/tmp/zenith-ext2-lifecycle-smoke-fixed.log`, and
  `/tmp/zenith-ext2-lifecycle-smoke-fixed/ext2-check.log` / `ext2-disk.img`
  (scratch artifacts, not committed). Existing Cosmos build warnings remain.
- Next: formatter interoperability and multiple block-group allocation, then
  ownership/mode/symlink and open-handle unlink behavior before installer and
  root migration. Installed roots still use FAT32; M2 remains open.

### 2026-10-09 · Bash portability assessment (implementation paused for review)

- Inspected Zenith command dispatch, buffered pipelines, terminal input and the
  Cosmos native/PAL interfaces, and checked Bash execution sources plus GNU build
  options and wasi-libc headers. Recorded [BASH-PORT.md](BASH-PORT.md).
- Bash is technically feasible as a later program-runtime target, but current
  Zenith lacks the C/POSIX execution contract. Standard WASI preview 1 does not
  supply fork/exec or terminal job-control semantics; M5 needs an explicit design
  extension if real Bash is a requirement.
- Assessment only: no Bash cross-build or guest execution, no kernel changes,
  and no roadmap/runtime architecture switch. M2's next implementation slice
  remains formatter/multiple-group validation after this review.

### 2026-10-09 · Sixth M2 slice · Files desktop app and multi-group ext2

- Added a small Files window in the launcher and `open files [DIRECTORY]`, with
  folder navigation, selected-item opening in Editor, file/folder creation,
  rename, file copy/move, paste to the chosen location, refresh and confirmed
  deletion of files or empty folders. Errors remain visible in the status line.
  Name validation and overwrite refusal preserve existing files; transfer state
  belongs to each window. `man files` and README document the controls.
- Plain .NET `FileBrowser` keeps operations host-testable. File copying is bounded
  to 8 MiB, Editor opening to 128 KiB; known virtual/device paths and final
  symlinks are rejected for copy/open. Folder copying and moves between parent
  directories are intentionally unavailable while ext2 directory-move parent
  link accounting remains unvalidated. Folder rename stays available. Operations
  run synchronously; this app is built into the desktop, not a separate program
  runtime or a users/permissions boundary.
- Expanded the independently formatted scratch ext2 volume to 16 MiB/two block
  groups, with an 8 MiB host filler exhausting the first group. Host verification
  additionally requires the retained guest-created inode to point into group two,
  and exact file manager text content after unmount and VM shutdown.
- A new synthetic full-first-group/remount regression reproduced a persisted
  counter mismatch (expected 25 free blocks, observed 26). Block and inode
  allocate/free paths now update volume counts before persisting group descriptors
  and superblock. Regressions cover second-group allocation/free/reuse and inode
  counts across remount. The complete host suite passes **292/292**; harness
  regressions pass **3/3**. The native ISO build succeeds; existing Cosmos and
  xUnit warnings remain.
- Initial native create/edit/rename/copy/move checks passed, but Delete was ignored
  by the bundled Cosmos virtio keyboard driver (its Linux-keycode mapping lacks
  KEY_DELETE). Added Ctrl+D for the same confirmation, retaining the toolbar and
  Delete handling on supported inputs. The expanded harness checks actual toolbar
  mouse deletion, keyboard confirmation/cancellation, overwrite refusal and the
  saved result. All **72/72 native checks** pass, including existing install,
  desktop, panic and poweroff. Visual inspection confirms the toolbar, selected
  filename/size and visible result status.
- The full run initially reported **72/73** because the new host checker expected
  saved text without a newline; `TextDocument.Save` deliberately appends one.
  Corrected the exact expected content to `file-manager-data\n`, then reran only
  the independent host verification on the retained disk: **1/1** passes, with
  check-only e2fsck exit 0, unchanged host seed, exact truncated inode counts,
  second-group allocation (retained data block 8787) and saved Editor text.
  All **73 checks** are now validated; no guest code changed after that run.
  Evidence: `/tmp/zenith-files-tests.log`, `/tmp/zenith-files-build.log`,
  `/tmp/zenith-files-smoke-final.log` (initial checker result), and
  `/tmp/zenith-files-smoke-final/ext2-check.log`, `ext2-disk.img`, `files.ppm`
  / `files.png` (retained scratch artifacts, not committed).
- Next: formatter interoperability, ownership/mode/symlink semantics, directory
  parent-link accounting and open-handle unlink behavior before installer/root
  migration. Installed roots still use FAT32; users/permissions and check-only
  guest fsck remain pending. M2 remains open.


### 2026-10-09 · Seventh M2 slice · Guarded ext2 formatter

- Added a local formatter for the existing profile: revision 1, 1 KiB blocks,
  128-byte inodes, filetype-only features, 8192 blocks/1024 inodes per group.
  Geometry is bounded to 1..512 MiB and fully preflighted, including partial final
  groups. Labels are validated printable ASCII, at most 16 bytes. Counts include
  reserved inodes and root/lost+found; bitmap padding is allocated, and every
  group has a correctly numbered superblock and descriptor-table backup.
- Added `mkfs.ext2 PARTITION [--yes] [-L LABEL]`. Formatting requires explicit
  confirmation and a named partition, refuses mounted/overlapping partitions
  and the running root disk, and serializes with secondary mount/unmount calls.
  Generic VFS formatting remains disabled. The primary signature is invalidated
  and flushed before metadata writes, and clean primary metadata is published
  last after flushes; interrupted metadata-write coverage verifies mount refusal.
  This is not a journal or secure erase; UUID awaits entropy support.
- All **306/306** host tests pass, including six independent e2fsck format cases
  from 1 MiB to 264 MiB (33 groups/two descriptor blocks), first-data-block and
  partial-group boundaries, 512/1024-byte sectors, allocation/free across remount,
  six no-write preflight refusals, an injected write failure and a scratch
  allocation-budget regression. CI host tests now
  explicitly install e2fsprogs. Harness regressions pass **3/3**. Native ISO build
  succeeds; existing cached NU1900, xUnit and Cosmos build warnings remain.
- Expanded QEMU with an initially blank disposable partition, formatter refusal
  checks, guest creation/remount and 9 MiB allocation/deletion across group two.
  Independent host checks require clean e2fsck, saved files, label, group-two data
  and unchanged partition-exterior guards/MBR. The second boot now uses the
  installed FAT scratch disk to exercise running-root-disk protection and clean
  poweroff. The first refusal test incorrectly targeted the already-unmounted
  installed scratch partition; it now explicitly remounts before testing refusal.
- The 9 MiB stress write exceeded both the default 60-second and CI's 180-second
  command budget. Read-only inspection showed it still advancing but slowing
  substantially (5,951,488 bytes after about 230 seconds). A new host regression
  measured **4,292,608 allocated bytes for 1,024 blocks**, matching four temporary
  arrays per block. Bitmap/zero/GDT/superblock scratch storage is now reused;
  metadata is still reread and persistence ordering unchanged. The same host
  regression now passes a **256 KiB** allocation ceiling. The unchanged
  9 MiB/1 KiB-block workload was rerun with CI's existing 180-second budget;
  native results are recorded below.
- With scratch reuse, the 9 MiB write/delete completes in about 50 seconds.
  The next command failed after runtime exhaustion: `PageAllocator` reported two
  free pages, then a 65-page thread-stack allocation faulted. ELF symbolication
  points to `ThreadContext.Initialize` / `SystemNative_CreateThread` / `Thread.StartCore`.
  A read-only snapshot still passes e2fsck and contains saved text. This is a real
  Cosmos runtime/heap limit at 512 MiB, not a clean low-memory exit; it remains open.
  Added an explicit `--memory 1024 --large-ext2-stress` profile for the large
  workload, preserving baseline 512 MiB RAM/60-second timeout and CI's existing
  180-second timeout. CI runs both profiles and retains both sets of artifacts.
- The final native ISO passes **88/88 baseline checks at 512 MiB** and **88/88
  large-profile checks at 1 GiB**. Both profiles verify formatting/refusals, saved
  contents after remount, clean independent e2fsck on both ext2 volumes, unchanged
  partition-exterior guards and MBR, actual installed FAT root selection, root-disk
  formatting refusal and clean poweroff. The large profile additionally verifies
  a retained data block in the second group after 9 MiB allocation/deletion.
  Host tests pass **306/306** and harness regressions **3/3**. Evidence:
  `/tmp/zenith-mkfs-tests.log`, `/tmp/zenith-mkfs-build.log`,
  `/tmp/zenith-mkfs-allocation-before.log`, `/tmp/zenith-mkfs-baseline.log`,
  `/tmp/zenith-mkfs-large.log`; retained disks/check reports and installed-boot
  serial logs are under `/tmp/zenith-mkfs-baseline/` and `/tmp/zenith-mkfs-large/`.
  The earlier 512 MiB large-workload failure remains evidenced in
  `/tmp/zenith-mkfs-smoke-fixed/serial.log`; scratch artifacts are not committed.
- Installer/root discovery remains FAT32. Next: investigate runtime heap growth
  and low-memory failure handling, then ownership/mode/symlink semantics,
  directory parent-link accounting and open-handle unlink behavior before root
  migration. Users/permissions and check-only guest fsck remain pending. M2 stays open.

### 2026-10-09 · Eighth M2 slice · Physical-memory reserve

- Inspected the installed 3.0.89 Core/HAL/System assemblies with Mono.Cecil,
  rather than assuming the sibling reference checkout matches. `AllocObjectSlow`
  collects only after TLAB refill fails; SATA ReadBlock/WriteBlock use spans and
  the existing reusable DMA path. The earlier serial trace had no collections
  before physical-page exhaustion and the following native stack allocation fault.
- Added a desktop-loop pressure check at one eighth of physical pages free,
  throttled to one collection per second. The check allocates nothing; collection
  logs before/after free pages. This reserves headroom for native allocations
  during a responsive GUI workload; it is not a general graceful-OOM solution.
- Enabled the 9 MiB ext2 smoke profile at the normal 512 MiB memory setting.
  Host suite passes 309/309, including
  reserve boundary, throttling/recovery and unavailable-metric cases. Python
  serial matcher suite passes 3/3.
- Native publish succeeds. Full **88/88** smoke checks pass with
  `--large-ext2-stress --memory 512 --step-timeout 180`, including commands
  after the 9 MiB allocation/deletion, remount reads, installed FAT root reboot,
  root-disk format refusal and clean poweroff. Independent e2fsck checks pass
  for both volumes, and retained data is in block group two with partition
  guards intact. One pressure collection raised free pages **15,752 -> 94,302**
  (about 307 MiB reclaimed). Lowest logged allocation-time free count: **15,752**.
- Evidence: `/tmp/zenith-pressure-tests.log`, `/tmp/zenith-pressure-build.log`,
  `/tmp/zenith-pressure-smoke.log` and `/tmp/zenith-pressure-smoke/` (serial,
  installed serial, screenshots, disposable images and independent reports).
- CI now runs the large profile at 512 MiB. This addresses the observed
  late-collection failure; it does not repair every possible runtime allocation
  failure or establish arbitrary live-set OOM safety. M2 remains open. Next:
  inode ownership/mode/symlink semantics, then permissions and ext2 root migration.

### 2026-10-09 · Ninth M2 slice · Bounded ext2 symbolic links

- Audit found symlink creation storing long-target blocks directly in all 15
  i_block slots (including indirect slots, with a 16th out-of-range access), and
  treating 60 bytes as an inline target without room for the terminator. Changed
  creation to Linux ext2's one-block target limit (1..1023 UTF-8 bytes here), with
  targets below 60 bytes inline and larger targets in one data block; mode 0777.
- Reads use i_blocks to distinguish storage, including imported short links
  backed by a block, and bound sizes before allocation. Empty, embedded-NUL and
  oversized targets are refused. Creation and namespace mutation validate names
  as non-dot components of at most 255 UTF-8 bytes, preventing inode allocation
  before rejection. Added host regressions and independent fsck checks for
  1/59/60/61/1023-byte targets, remount and deletion reclamation, multibyte
  boundaries, invalid names/targets, corrupt sizes and disk-full cleanup.
- Added guest `ln -s TARGET LINK`, `readlink LINK` and `unlink FILE` using the
  installed 3.0.89 directory-handle API (verified via Mono.Cecil). Final-entry
  lookup does not follow links; dangling links can be inspected and removed.
  Existing destinations are refused; hard links/FAT symlinks remain unsupported.
- Extended QEMU coverage and independent debugfs/raw-block verification for
  relative, absolute, directory, dangling and block-backed targets, link loops,
  duplicate refusal, removal preserving the target and remount behavior.
- Ownership/mode mutation remains the next slice. The installed VFS rejects ..
  in symlink targets and limits resolution to eight hops; ls -l still needs real
  metadata. Documented these limits in README/man ln.
- Initial native run passed link creation/inspection but exposed a harness
  mistake: `cat` reports absent files yet currently returns status 0. Stopped
  that run and changed dangling/loop assertions to `test -f`, which exercises
  VFS target resolution without relying on cat's error status. Shell filter
  error-status propagation remains a separate follow-up. Interrupted-run
  evidence is retained under `/tmp/zenith-links-smoke/`.
- Final validation: **332/332 host tests**, **3/3 Python matcher tests**, and
  native Debug publish pass. Full **101/101 QEMU checks** pass with the 9 MiB
  workload at 512 MiB RAM, including all link operations, remount reads, installed
  FAT-root reboot and clean poweroff. Independent e2fsck is clean for both ext2
  volumes. debugfs/raw-block checks confirm link type, mode 0777, exact targets,
  terminating NUL for the slow link, and absence of deleted temporary/cyclic links;
  retained regular data remains in group two and partition guards are untouched.
- Evidence: `/tmp/zenith-links-tests.log`, `/tmp/zenith-links-build.log`,
  `/tmp/zenith-links-smoke-verified.log` and `/tmp/zenith-links-smoke-verified/`.
  CI's existing 512 MiB large profile now exercises these checks automatically.
- M2 remains open. Next: persisted ownership/mode updates and truthful stat/ls
  output. Directory cross-parent link accounting, open-handle unlink semantics,
  users/enforcement and installed ext2 remain pending before root migration.

### 2026-10-09 · CI input-loss fix and tenth M2 slice · Ownership and modes

- Inspected failed GitHub Actions run 37935747572 at commit 6aa5bcb. Host tests
  and Release build passed; baseline QEMU finished 98/101. Its disk was clean,
  but the relative link stored "per" instead of "persist.txt". The serial trace
  places a GC pause during injection of that command. Keyboard event buffers
  cannot be recycled while the collector disables interrupts; blind key injection
  overflowed the finite virtio queue. The harness now tracks collection start/end
  incrementally in serial output and pauses key injection until GC completes.
  Explicit 20 ms key holds also avoid overlapping default 100 ms releases.
  No filesystem assertions or timeout budgets were relaxed. Six Python tests
  cover serial matching, split GC messages, successive collections and pacing.
- Changed ext2 UID/GID fields to uint and persist both Linux low/high 16-bit
  halves. The profile gate now requires Linux creator layout; the parser rejects
  non-Linux layouts and undersized inodes before ownership-field access. Corrected
  i_osd2 offset, and use the high size field only for regular files.
- SetAttr validates selected fields/unknown flags before mutating storage and
  updates ctime for mode/ownership changes. Unselected values remain untouched;
  mode changes preserve file type. No-op flags cause no writes.
- Added a plain shell metadata contract with the installed VFS bridge. ls -l/stat
  inspect the final entry without following it, show real numeric ownership,
  mode/special bits, link count, size, inode and block information, and display
  link targets (including dangling links). FAT's unavailable Unix fields use ?
  instead of invented user/users and fixed permission strings.
- Added numeric chmod OCTAL FILE... (0000..7777) and chown UID[:GID] FILE...;
  ordinary mutations follow final links and require ext2. Child scripts inherit
  the metadata backend. User accounts/access enforcement remain pending; newly
  created ext2 objects still default to uid/gid 0 until credentials are implemented.
- Host suite passes 355/355, including independent debugfs/e2fsck ownership and
  mode checks, 65535/65536 boundaries, high-ID inode reuse, attribute preflight,
  real/dangling metadata output, special permission bits and invalid arguments.
  Release publish passes; all 6 Python harness tests pass. Both CI-equivalent
  Release QEMU profiles pass **114/114 checks**: baseline and 512 MiB RAM with
  the 9 MiB large-volume workload. Independent e2fsck is clean; debugfs confirms
  persisted file/directory modes and 32-bit UID/GID values. Relative link creation,
  symlink-following mutations, remount reads, partition guards, installed FAT-root
  reboot and clean poweroff all pass.
- CI failure logs/artifacts: /tmp/zenith-ci-failure.log and
  /tmp/zenith-ci-37935747572/. Hosted CI has not been rerun with local changes.
- Validation evidence: `/tmp/zenith-metadata-tests.log`,
  `/tmp/zenith-metadata-release.log`, `/tmp/zenith-metadata-baseline.log`,
  `/tmp/zenith-metadata-baseline/`, `/tmp/zenith-metadata-large.log` and
  `/tmp/zenith-metadata-large/`.
- M2 remains open. Next: finish directory cross-parent link accounting and
  open-handle unlink semantics before installer/root migration; persistent users,
  permission enforcement and check-only fsck remain required.

### 2026-10-11 · Eleventh M2 slice · Safe directory moves

- Clean starting checkout at 50f2d17. Fixed ext2 cross-parent rename to maintain
  both parent link counts and rewrite the actual `..` record. Parent inode
  snapshots are refreshed and same-parent aliases compare inode numbers.
- Validate both handles belong to this volume and are directories. Reject moves
  into self/descendants, corrupt ancestry, more than 1024 ancestry levels, link
  underflow/overflow and existing destinations before writing. Existing-source
  same-entry rename succeeds without writes. Update parent mtime/ctime and moved
  inode ctime, preserving inode identity, permissions, ownership and contents.
- Directory `mv` uses filesystem rename instead of copy/delete. Cross-filesystem
  directory moves and destination replacement are refused. The Files app's
  separate folder-transfer restriction is retained until its UI path is tested.
- Host suite passes **361/361**, including remount identity/modes/high ownership,
  correct `..`/parent links, same-parent handles, rejected cycles/corrupt ancestry,
  link overflow, foreign volumes and destination conflicts. Independent e2fsck
  is clean after successful moves. **6/6** Python tests and Release publish pass.
- Added eight native checks for populated directory moves, retained ownership and
  special bits, parent link counts, cycle rejection, remount and moving back.
  Release QEMU passes **122/122 checks** at 512 MiB RAM with the 9 MiB workload.
  Cross-parent move/back, retained high ownership and special bits, correct parent
  link counts across remount, child contents and cycle refusal all pass. Both
  ext2 volumes pass independent e2fsck; saved data, second-group allocation and
  partition guards are intact. Installed FAT-root reboot and poweroff also pass.
- Evidence: `/tmp/zenith-rename-tests.log`, `/tmp/zenith-rename-release.log`,
  `/tmp/zenith-rename-large.log` and `/tmp/zenith-rename-large/`.
- M2 remains open. Next: open-handle unlink lifetime before installer/root
  migration, then persistent users, permission enforcement and check-only fsck.
  Rename is not crash-atomic; ext2 has no journal.

### 2026-10-11 · Ext2 installation and check-only consistency audit

- Continuing through the remaining ext2 scope from clean commit 0b4ff64. New
  installations use a Linux-filesystem GPT root partition with Zenith's supported
  ext2 profile; ESP remains FAT32. Root size is capped at 512 MiB; larger disks
  retain unused space. Installer refuses undersized disks and disks with mounted
  partitions before changing GPT. Existing FAT installations remain bootable.
- Boot discovery recognizes supported ext2 roots through /etc/os-release. fstab
  records ext2. Fixed sync to write superblock counters before device Flush.
- Added check-only fsck.ext2 for unmounted partitions: validates supported bounded
  geometry, bitmap padding/free counters, reserved metadata allocation, block
  ownership including indirect trees, inode sector counts, directory records,
  entry types/names, dot/parent relationships, link counts, reachability and group
  directory counts. Reports at most 32 diagnostics, total errors, and never repairs
  or writes. Mounted volumes are refused. This is not a full e2fsck replacement.
- Inspected installed Cosmos 3.0.89: VFS tracks open handles and defers unlink to
  final close. Added fsprobe DIRECTORY, using a unique scratch directory, to test
  shared open handles, writes after deletion, reads after one close, final-close
  deletion, inode reuse and cleanup. Unix immediate namespace removal remains a
  Cosmos API limitation, not claimed as implemented. Raw driver Unlink callers
  must go through VFS lifetime management.
- Host suite passes 374/374; eight Python harness tests and Release publish pass.
  Corruption fixtures cover link/free counters, bitmap allocation, malformed
  records/parents and duplicate block references, with zero checker writes.
- Native baseline and 512 MiB large profile are running. Both install ext2,
  execute guest fsck/probe, reboot installed roots twice, verify retained data,
  numeric ownership/modes/symlinks, and independently inspect the installed disk.
  Added a disk-only EFI boot test and CI dependencies for OVMF/mtools/parted.
- Harness assertions now start at each command's serial offset so an earlier
  identical marker cannot hide a later failure. Regression tests cover both stale
  and fresh markers. Logs/artifacts use /tmp/zenith-root-{baseline,large} and
  /tmp/zenith-root-{tests,release}.log.
- M2 remains open for users/login and permission enforcement. Features and broader
  documentation discussion follows verified completion of this ext2 scope.
- Additional review: mounted ext2 volumes are now durably marked unclean; sync
  preserves that state and only completed unmount restores clean state. Crash-dirty
  volumes require host e2fsck before writable mounting. Volatile-device tests verify
  both metadata-before-flush durability and dirty/clean transitions.
- First baseline/large runs passed every guest assertion and clean independent
  e2fsck, but the installed-image assertion expected debugfs mode 00640 while the
  tool prints 0640. Corrected only the leading-zero matcher and rechecked both
  retained images successfully. Final full-profile runs validate the mount-state
  change; no timeout or filesystem behavior assertions were weakened.
- Disk-only EFI validation passes 7/7 with the final kernel: no ISO, ext2 root,
  retained content/ownership/link, lifetime probe, clean poweroff and independent
  e2fsck. Evidence: /tmp/zenith-root-efi.log and /tmp/zenith-root-efi/.
- Final verification: **374/374 host tests**, **8/8 Python harness tests**, final
  Release publish, **135/135 baseline QEMU checks**, **135/135 large-profile
  checks** at 512 MiB RAM, and **7/7 disk-only EFI checks**. Both installed-root
  reboots preserve file data, mode 0640, UID 70000/GID 80000 and relative symlink;
  root-disk formatting and mounted-root checking are refused. Independent e2fsck
  is clean for both secondary volumes and installed roots; partition guards and
  second-group retained data remain intact. Saved roots also have clean state 1.
- Final evidence: /tmp/zenith-root-tests.log, /tmp/zenith-root-release-final.log,
  /tmp/zenith-root-final-baseline.log and directory, /tmp/zenith-root-final-large.log
  and directory, /tmp/zenith-root-efi.log and directory. CI now includes the same
  root persistence tests plus disk-only EFI boot; hosted rerun awaits push.
- Supported ext2 implementation is complete for this scope. Remaining boundaries:
  supported Linux feature/layout profile only, volumes up to 512 MiB, direct/single/
  double-indirect file I/O, no journal or guest repairs, Cosmos deferred namespace
  removal while open. M2 is still open for real users and permission enforcement.
  Pause implementation here for the requested features/documentation discussion.

## 2026-10-11 · System planning and documentation

- Paused implementation at the agreed feature/documentation boundary after bounded
  ext2 completion. M2 remains open for users/authentication and access enforcement.
- Added a documentation hub, current architecture, chronological history, detailed
  system plan and package/update design discussion. Current implementation is
  distinguished from requirements, proposed mechanisms and deferred capabilities.
- Recorded requested headless and DE/auto-tiling WM sessions, shared services/config,
  proper Settings pages, future Files workflow/transfer rework and acceptance gates.
- Proposed offline-first package foundation, separate data/system/runtime package
  classes, ownership/config handling, bounded parsing, transactions/recovery, kernel
  activation, trust/mirror publishing and staged validation. No package/runtime/trust
  format or system rollback layout is agreed by this documentation change.
- Corrected roadmap's stale FAT-only root and test totals; expanded M6/session and
  package workstream mapping without renumbering milestones. Reconciled obsolete
  technical-note architecture sections and linked README/shared instructions.
- No kernel code or deployment changed; mirror hosting, session modes, package
  operations and Settings remain unimplemented. Documentation validation: all 77 local Markdown link targets in the ten changed
  documents resolve, and git diff --check passes. Existing native results remain
  dated at 8389113; kernel/tests were not rerun for this documentation-only change.
- Next discussion: system-release versus app priority, recovery/layout strategy,
  package container/trust choices and initial WM/headless scope. Recommended next
  package slice is a builder/reader/dry-run plus guest verification feasibility,
  before writing an active installation.

## 2026-10-11 · Userland, customization, home repository and recovery clarification

- Accepted future architectural requirement: desktop/apps outside the kernel,
  independently packaged and isolated. Added USERLAND migration/API/IPC/process
  gates; revised M5 from assumed WASM direction to explicit userland investigation.
  Current kernel linkage remains accurately documented; no loader was implemented.
- Added desktop customization scope (themes, sizing, panels, shortcuts and WM
  preferences), config/upgrade preservation and concrete usability gates.
- User confirmed home-hosted repository starts LAN-only; host details pending.
  Added static-server/publishing/trust/operational plan; nothing deployed.
- Investigated upgrade saved states, trial boot and restore: ext2 has no snapshots;
  versioned kernels alone do not restore roots. Added comparison of root generations/
  A-B copies, persistent block CoW and Btrfs, with consistency and mutable-data scope.
- Inspected boot layout and local EFI binary (Limine 10.8.5); researched upstream
  menu/cmdline and filesystem-reader docs. Proposed kernels on ESP plus explicit
  root/generation selection and OS recovery tools, avoiding assumptions of native
  bootloader snapshot restore. No root selector/trial/restore code exists yet.
- Documentation-only follow-up: 81 local Markdown link targets across eleven
  changed documents resolve; git diff --check passes. No runtime code or
  infrastructure change; native tests were not rerun for documentation changes. Next: discuss ABI/isolation research,
  home host details, and desired saved-state/root/user-data rollback semantics.

## 2026-10-11 · Consolidated roadmap and dedicated repository VM

- Finished consolidating the discussed product requirements into ROADMAP: retained
  M0–M7 history and added explicit S session/configuration, P package/distribution
  and R saved-state/recovery workstreams with dependencies and exit evidence.
- Recorded future kernel/userland separation, headless plus DE/auto-tiling WM,
  Settings, Files rework, customization/usability, versioned kernel installation
  while the old kernel runs, independent app packages and saved-system trial/restore.
- User selected a dedicated home VM with its own IP; native web services avoid
  Docker/port-mapping constraints. LAN plus existing VPN/DNS provide private remote
  access. Revised server/client/system/shared guidance consistently. No VM deployed.
- Package/container/crypto/ABI and snapshot backend remain implementation gates;
  Btrfs is not selected or a prerequisite for staged kernel updates. Explicitly
  distinguished a host/VPN route from an unimplemented Zenith VPN client.
- Next implementation returns to M2 entropy/credentials/access-policy groundwork;
  offline package and userland/recovery research are separately selectable slices.
- Documentation validation: all 49 local Markdown link targets in six changed
  files resolve, and git diff --check passes. Previous runtime results remain
  unchanged; no kernel tests rerun or infrastructure changes performed.

## 2026-10-11 · M2 users and access enforcement (active)

- Added immutable thread-scoped credentials, Unix permission/sticky/setgid policy,
  bounded account parsing, fail-closed hardware entropy and PBKDF2-SHA256 passwords.
- Added mounted-driver guards and VFS path/deferred-unlink authorization plugs.
  Host security coverage passes 30 tests; native integration is under validation.
- Active: resolve released patcher dependency lookup, then integrate persistent
  accounts, hidden password input, session/app identities and guest regression tests.
  M2 remains open; authentication and native permission behavior are not yet verified.

- Integration evidence: HAL-only policy library and a local released-patcher wrapper
  avoid dependency lookup and redundant self-reference failures without editing
  Cosmos/global tools. Release ISO builds; all 413 host tests and eight Python
  harness tests pass. Initial native security run passed account setup, wrong/right
  authentication, filesystem/raw-device denials, sticky removal and reboot login.
- Expanded native run confirms ordinary-user Editor/Files creation and random-device
  reads. Its held-handle access probe exposed an unresolved deferred-deletion case;
  investigating cleanup versus handles retained after a denied operation. M2 stays open.

- Resolved denied-operation handle retention: metadata authorization runs before
  allocation, and the local patcher prepends a requested-access check to Cosmos's
  PAL `Open` before truncation/handle allocation. Native accessprobe now verifies
  held-handle denials, denied unlink/chmod/truncation cleanup, unchanged data,
  authorized deletion across identity changes, caller ownership and mount ancestry.
  An attempted private-enum UnsafeAccessor wrapper failed at native boot and was
  replaced with the signature-checked PAL precheck; the replacement probe passes.
- Protected account/log ancestors, reserved the nobody UID/GID, and restricted
  persistent logging to its root-configured destination with a narrow service scope.
  Added native thread-isolation checks alongside RDSEED/PBKDF2 vectors.
- Final account validation: 414/414 host tests, eight Python harness tests and
  Release publish pass; 32/32 account/access QEMU assertions pass across live,
  installation and authenticated reboot. No test password appears in serial logs.
  Independent e2fsck of the account-updated root is clean. Final UEFI disk-only
  boot passes 7/7. Evidence: `/tmp/zenith-security-host-final.log`,
  `/tmp/zenith-security-build.log`, `/tmp/zenith-m2-security-complete/` and
  `/tmp/zenith-m2-efi-complete/`.
- Corrected three baseline assertions that still treated `/tmp` as FAT; FAT
  unsupported-metadata/link checks now target the live FAT home instead. Corrected
  large-profile run passed 135/135 at 1024 MiB; final CI-equivalent 512 MiB pressure
  regression is running before milestone closure. Added account regression to CI;
  hosted CI has not run for these unpushed changes.

## 2026-10-11 · M2 completion

- Final CI-equivalent large workload at 512 MiB passed 135/135 checks, including
  live operations, GUI/shell regressions, formatter/checker guards, two installed
  ext2 boots, truthful persistent ownership/modes/symlinks and independent e2fsck.
  Memory-pressure collection ran successfully during the workload. Evidence:
  `/tmp/zenith-m2-complete.log` and `/tmp/zenith-m2-complete/`.
- M2 exit criteria are met: installed ext2 with real accounts and permissions,
  truthful metadata, proc/dev and check-only fsck. Roadmap, shared instructions,
  architecture, history, manual pages and account/development guides are reconciled.
- Scope retained: bounded non-journaled ext2 with host crash repair, trusted
  kernel-linked applications, root-maintenance FAT live/legacy mode, terminal login
  within the desktop and x64 hardware entropy. ARM64 entropy fails closed. ext4 is
  optional; independent userland, headless/DE/WM, Settings, Files rework, networking,
  package VM/client and saved-system recovery remain later workstreams.
- Next: review features/documentation and explicitly select S0/S1 or M3. No network,
  package infrastructure deployment or userland migration started. Hosted CI is
  configured with account regression but has not run for this unpushed work.
- Final rebuild includes the corrected `id` fallback label. A disposable ext2
  fixture with malformed `/etc/passwd` passes 3/3 native fail-closed assertions:
  UID/GID 65534 reported as nobody, shadow read denied, login refused. Evidence:
  `/tmp/zenith-bad-account-probe.log`, retained corrupt-account serial log in
  `/tmp/zenith-m2-security-complete/` and `/tmp/zenith-security-build-final.log`.
  Documentation validation: all 96 local Markdown targets resolve; git diff --check
  passes. No generated images/disks/build output are included in the commit.

## 2026-10-11 · S0 system settings/session seam and P0 offline package prototype

- User selected shared configuration/session foundations with the existing floating
  desktop retained as the test DE, then graphics-independent headless. Also requested
  early packaging work to reduce coupling in future changes.
- Implementing common system-settings validation/persistence for keyboard, timezone
  and versioned session defaults; extracting shared boot and desktop host lifecycle.
  Current build capability remains DE only; headless/WM must be reported unavailable.
- Implementing a P0 experimental offline data manifest, shared host/guest reader and
  dry-run planner, host bundle builder and truthful bundled-component inventory.
  No install/update executor, production container/trust format, network client or
  independent code loader is introduced. Future package defaults are separated from
  mutable /etc settings; package dry-runs reject collisions rather than overwrite.
- Implemented and runtime verified: shared settings load/validate/stage/save before
  applying keyboard/clock state, root-only mutations, versioned session defaults,
  boot-argument selection and truthful unavailable-host fallback. Shared boot now
  composes the existing desktop host without changing its test windows.
- Implemented the Linux host data-bundle builder, bounded shared manifest/planner,
  guest `pkg list`, `pkg inspect` and read-only `pkg plan`. Regular-file/type/size
  checks reject devices, links, FIFOs and unbounded trees; plans check digests,
  compatibility, dependencies and destination conflicts without writing payloads.
- Validation: 454/454 host tests, eight existing Python harness tests and nine
  real-process package-tool tests pass. Release NativeAOT publish passes. Foundation
  QEMU suite passes 33/33 across live and three installed boots; existing desktop/
  filesystem regression passes 135/135 at 512 MiB. Final disk-only EFI suite passes
  10/10, including boot-argument selection and new manual pages. Independent host
  e2fsck reports the settings-updated and EFI roots clean.
- Evidence: `/tmp/zenith-s0-host-final.log`, `/tmp/zenith-s0-package-tests-final.log`,
  `/tmp/zenith-s0-build-delivery.log`, `/tmp/zenith-s0-foundation-final.log`,
  `/tmp/zenith-s0-foundation-final/`, `/tmp/zenith-s0-baseline-final.log`,
  `/tmp/zenith-s0-baseline-final/`, `/tmp/zenith-s0-efi.log` and `/tmp/zenith-s0-efi/`.
  Preliminary debug runs were interrupted; the final complete suites above passed.
  Corrected harness punctuation injection after switching to GB, restoring US before
  subsequent pipe commands. Added foundation/package regressions to CI; hosted CI
  has not run for this unpushed work.
- S0 remains partial: per-user preferences and Settings GUI are later work. DE is
  the only available host; headless/WM and graphics-independent panic handling are
  not implemented. P0 remains experimental: no installer, installed database,
  signatures, network client, independent code loading or update recovery.
- Next: S1 serial headless host and serial failure reporting while keeping the test
  DE. Packaging continues with production container/trust specification and verifier
  gates before P1 transactional offline installation. Documentation records current
  behavior separately from planned package/runtime/desktop work.
- Documentation validation: all 137 local Markdown targets checked resolve;
  `git diff --check` passes. Generated bundles, images and build output are excluded.

## 2026-10-11 · S1 serial headless session delivered

- Delivered the next selected slice: reuse shared services/shell/accounts on serial,
  bypass graphical console/desktop initialization, and provide serial panic reporting.
- Keep the existing test DE available. Bound line/script input, queued commands and
  pending output; validate cancellation, hidden authentication, filesystem/scripts,
  session persistence and clean shutdown on disposable live/installed guests with no GPU.
- P0 remains inspect/plan only; this slice does not freeze a production package
  format or add network transport/installation.
- Implemented x64 COM1 host with bounded ASCII editing/multiline/read input, one
  credential-scoped worker, cooperative cancellation, bounded queues/history and
  output backpressure. Kernel bypasses Cosmos's graphical console initializer;
  supported display presence is checked without constructing a canvas. No display
  selects headless with a capability diagnostic. WM and ARM64 input remain unavailable.
- Shared passwd/login/su/logout work with hidden secret callbacks. Configured installed
  roots start as nobody; serial exit resets the shell and revokes identity. Successful
  login/logout renew the shell and clear previous variables/functions/history and
  queued commands; su retains the current shell. Console
  output bypasses persistent logs. Fatal headless/early errors and failed graphical
  panic rendering report on serial and accept R to restart.
- Initial native validation passed 66/66, then 70/70 with manual/failed-login checks.
  Review corrected idle Ctrl+C so it clears input without cancelling the next command,
  synchronized queue access and bounded history. The 71-check build passed; final
  delivery adds session-generation renewal and passes 73/73 across
  live and three disk-only UEFI boots without VGA/GPU/input devices. Checks cover
  scripts/read/cancellation, missing/invalid/saved/override selection, passwords absent
  from transcripts, authentication/permissions, persisted data/settings, future schema
  protection, clean shutdown, independent e2fsck and serial panic/restart.
- Final host suite passes 467/467, eight Python harness tests pass, and Release NativeAOT
  publish passes. Settings/package desktop foundation passes 33/33 after changing its
  unavailable-session fixture from headless to WM. Full desktop/filesystem baseline
  passes 135/135 at 512 MiB, including persistent attributes/symlinks and independent
  host checks. Delivery disk-only graphical EFI suite passes 10/10 with session override
  and manual pages. No hosted CI run is claimed.
- Evidence: `/tmp/zenith-s1-build-final.log`, `/tmp/zenith-s1-host-final.log`,
  `/tmp/zenith-s1-harness-tests.log`, `/tmp/zenith-s1-session-delivery.log`,
  `/tmp/zenith-s1-session-delivery/`, `/tmp/zenith-s1-foundation.log`,
  `/tmp/zenith-s1-foundation/`, `/tmp/zenith-s1-baseline.log`, `/tmp/zenith-s1-baseline/`,
  `/tmp/zenith-s1-efi.log` and `/tmp/zenith-s1-efi/`. A preliminary session check was
  interrupted to replace unsupported shell parameter-default syntax with `test -z`;
  the corrected complete suite above passed. Added the no-GPU suite to CI and updated architecture,
  settings/accounts/development guides, README, roadmap and manual pages.
- Limits: x64 QEMU COM1 verified; real UARTs/ARM64 receive/local text consoles/SSH,
  full ANSI editing/job control, automatic profile execution and service supervision
  remain future work. Input/output adapter bounds do not bound arbitrary existing
  command internals or create process isolation. Current DE/apps remain kernel-linked.
- Next main milestone: M3 networking. Next packaging gate: production container/trust
  specification and verifier feasibility before P1 transactional offline installation.
- Documentation validation: all 140 local Markdown targets resolve; `git diff --check`
  passes. Generated images, transcripts, test credentials and build output are excluded.

## 2026-10-11 · M3 IPv4 lab foundation delivered

- Selected M3 next. Implement shared IPv4 configuration/diagnostics, DHCP boot, bounded DNS/NTP codecs and HTTP lab fetching; retain the test DE and no-GPU session.
- Validate against disposable QEMU/slirp and local host servers; home VM/VPN details are still absent. Production TLS/package trust remain explicit gates.
- Implemented asynchronous primary-uplink DHCP, strict versioned static/off saves,
  hostname/hosts/resolver integration, half-lease bounded reacquisition, interface/route
  status, correlated ICMP/DNS, nc TCP probes, optional configured boot/manual NTP software
  UTC and staged bounded HTTP lab downloads with progress and at most one retry.
- Native DNS faults exposed Cosmos 3.0.89 skipping unwind-finally cleanup. Native
  Ctrl+C exposed unreliable typed catch selection; callbacks inside a misselected
  handler repeatedly threw until failfast. Owning-frame explicit disposal and one
  catch-all with classification/callbacks outside handlers fix those paths.
- Native streaming retry exposed upstream TCP partial-read/append accounting and IRQ
  races, culminating in a negative Buffer.BlockCopy count in a receive callback.
  Added a separate pure Zenith.Network support assembly and signature-checked build
  adaptation to immutable/CAS receive snapshots, capped at 128 KiB per connection;
  overflow closes the connection. No NuGet cache or sibling Cosmos checkout changed.
- Host validation: 498/498 tests pass, including strict settings, malformed/truncated
  DHCP/DNS/NTP, incremental HTTP boundaries/limits/cancellation and concurrent ordered
  TCP producer/consumer/snapshot/overflow tests. Eight Python harness tests pass.
  Release NativeAOT publish succeeds with the transport adaptation.
- Native lab passes 100/100 assertions: live DHCP route/DNS, wrong-ID reply rejection,
  ping/TCP, corrected software clock, streamed progress, retry, oversize/header/encoding/
  truncation/stall faults, status-130 Ctrl+C, staging cleanup, QMP link drop/restore,
  static/off state, invalid-save preservation, clean installed reboot with saved static
  settings/boot NTP, root/user allow/deny and recovery, persisted exact payloads,
  disk-only no-NIC authentication/shutdown and independent clean e2fsck/debugfs checks.
  Fixed two harness mistakes: disk name is sata0, and sync plus forced process stop
  leaves an intentionally dirty ext2 root. Reboot persistence now uses clean poweroff;
  the earlier dirty-volume fallback was expected profile enforcement, not data loss.
  Disposable credentials are absent from serial transcripts.
- Existing DE/filesystem regression passes 135/135 at 512 MiB and existing no-GPU
  live/installed/auth/panic regression passes 73/73. The final native clock transcript
  is Sat Oct 3 07:06:40 UTC 2026 from the known lab NTP timestamp, rather than host RTC.
  Tightened the harness assertion to require that date/time prefix.
- Evidence: /tmp/zenith-m3-host-final.log, /tmp/zenith-m3-harness.log,
  /tmp/zenith-m3-build-final.log, /tmp/zenith-m3-verified.log and
  /tmp/zenith-m3-verified/, /tmp/zenith-m3-baseline.log and
  /tmp/zenith-m3-baseline/, /tmp/zenith-m3-headless.log and
  /tmp/zenith-m3-headless/. Earlier m3-native/cleanup/bounded/delivery logs contain
  diagnostic failures and corrected fixture runs; they are not passing evidence.
- Added the loopback network suite and artifacts to CI. Updated commands/manual,
  instructions, architecture/history/development/README and roadmap. Hosted CI has
  not run for this unpushed work.
- M3 remains active: no TLS/SslStream support; documented Mbed TLS as an evaluated
  candidate with native ABI/crypto/RDSEED/certificate/time gates. Actual home VM/DNS/
  VPN details are absent. Framework packet/UDP queues, TCP protocol/table concurrency,
  seamless renewal, multi-NIC/IPv6, interactive nc and real hardware need further work.
  Software time is unauthenticated and currently feeds date/timezone only; BCL/FS clocks
  still use RTC. Plain HTTP is a lab transport and does not authorize package updates.
- Next M3 slice: harden transport/clock integration and prototype a pinned TLS bridge
  with certificate and missing-entropy tests; home routing follows supplied VM details.

## 2026-10-11 · M3 completion and LAN usability — delivered

- Completed the bounded IPv4 LAN client profile requested for home-network testing.
  The user supplied DNS 10.0.10.3; the package VM is not provisioned. Host and guest
  both resolve example.com through that DNS server. VM/VPN acceptance follows P2
  provisioning; real hardware, IPv6 and Internet/server hardening remain unverified.
- Added checksum-pinned offline Mbed TLS 3.6.7 source and retained its Apache license,
  attribution and guest man licenses text. The freestanding client uses a 1 MiB arena,
  two 32 KiB ciphertext queues and RDSEED entropy with fail-closed initialization.
  Verified HTTPS uses TLS 1.2 ECDHE RSA/ECDSA AES-GCM; chain/name/IP SAN/key strength
  and every chain certificate's dates are checked. Root-only exact-PEM SHA256 trust
  import preserves existing trust on rejection; no default trust or insecure bypass.
- Added signature-checked Ethernet/ARP/IPv4/ICMP/UDP/TCP bounds/checksum/option guards,
  removed unused unsafe upstream TCP option materialization and replaced UDP/ICMP
  receive queues with 32-packet immutable/CAS queues. Existing 128 KiB TCP receive
  bounds remain. Shared-table concurrency, ARP/outgoing queue limits, connection
  retirement and TCP retransmission/reassembly still require future hardening.
- Shared correction at the Cosmos RTC seam now feeds BCL DateTime/DateTimeOffset
  and filesystem timestamps. NTP remains unauthenticated software time; no RTC write
  or persisted trustworthy time floor is implied. Independent debugfs confirms the
  downloaded file's ext2 timestamp follows the known NTP fixture, rather than RTC.
- netcfg apply validates/reloads disk configuration, hostname, hosts and resolvers.
  Root netcfg dns saves canonical IPv4 overrides or restores DHCP DNS. Endpoint
  normalization fixes leading-zero address correlation. Launcher supports explicit
  user/TAP/off networking and desktop/headless; TAP consumes a preconfigured interface
  without changing host bridges or addresses. Corrected patcher cache invalidation
  so changes to PatcherFixup cannot silently leave stale transport/clock adaptations.
- Final Release NativeAOT publish succeeds. Host tests pass 504/504; the exact TLS
  bridge passes 35/35 OpenSSL checks, including leaf/root expiration/future dates,
  wrong identity/CA, tampered ciphertext, entropy failure, limits and reuse. Launcher
  tests pass 4/4 and Python smoke-harness tests pass 8/8.
- Final native acceptance passes 176/176: live DHCP/DNS/ping/TCP/NTP, actual malformed
  ingress calls, native crypto KATs, HTTP/HTTPS fault/cancel/retry/staging cleanup,
  correct IP/name trust and rejection cases, saved DNS 10.0.10.3 resolution, clean
  install/reboot with persisted trust/config/payloads, root/user permissions, no-NIC
  boot, missing RDSEED rejection, independent clean e2fsck and exact file bytes/time.
- Existing DE/filesystem regression passes 135/135 and no-GPU live/installed/auth/panic
  regression passes 73/73. These ran after TLS, clock and transport hardening; final
  DNS-save/manual changes were subsequently covered by the final network suite/build.
- Corrected validation issues: full tmpfs from prior disposable disks required moving
  six retained validation directories to ignored obj/prior-validation with symlinks
  preserving their /tmp paths. An intermediate no-RDSEED EFI fixture failed in Limine
  before the kernel (cause not established); the final missing-entropy case boots the
  live ISO, imports the disposable public CA and verifies native fail-closed behavior.
  That intermediate run is not passing evidence. No test evidence was discarded.
- Evidence: /tmp/zenith-m3-delivery-build.log, /tmp/zenith-m3-delivery-host.log,
  /tmp/zenith-tls-host-checks.log, /tmp/zenith-m3-launcher.log,
  /tmp/zenith-m3-harness.log, /tmp/zenith-m3-lan-delivery.log and
  /tmp/zenith-m3-lan-delivery/ (symlink to ignored obj/m3-lan-delivery),
  /tmp/zenith-m3-complete-desktop.log and /tmp/zenith-m3-complete-headless.log.
  Earlier completion/network/entropy probe logs are diagnostic history.
- CI now builds/tests the offline TLS bridge and runs native HTTPS acceptance without
  private LAN dependencies. Hosted CI has not run for this unpushed work. Updated
  networking/manual, source provenance, development, architecture, history, roadmap,
  instructions and package-server notes; 152 local documentation links resolve and
  git diff --check passes. M3 is complete within the documented client
  profile; transport success does not authorize package installation. Next packaging
  work defines the production container/trust contract and prepares the dedicated VM.

## 2026-10-11 · Native TLS build portability fix — delivered

- User's Cosmos Release build fails with undefined inet_pton from tls-x509_crt.o.
  The local Clang 23 ELF target still discovers host socket headers through
  __has_include; Mbed TLS consequently selects libc IP parsing in a kernel link.
- Isolate native C includes with -nostdinc plus compiler builtin headers and Zenith's
  freestanding shim. Explicitly select upstream's software IP parser in both native
  and host TLS profiles; preserve certificate/IP SAN verification.
- Every native build now checks all TLS object imports with nm; only kernel-provided
  memcpy, memset and zenith_rdseed_word are accepted. A compiled object importing
  inet_pton is rejected by this gate. CI builds the native profile independently and
  installs Clang/binutils explicitly; host OpenSSL fixtures use the same IP parser.
- The exact reported cosmos build -p /home/azazel/cosmos-test/MyOS -a x64 -c Release -v
  now succeeds (zero warnings/errors), produces output-x64/Zenith.iso and reports only
  the three expected native TLS imports. Host TLS checks pass 35/35.
- The first guest acceptance run passed live HTTPS/IP/name/date/cancel/fault checks,
  then failed a shutdown marker assertion. The serial transcript shows Cosmos debug
  lines inserted between "all filesystems " and "unmounted", followed by ACPI S5.
  Headless marker matching now reuses the existing raw-or-noise-stripped matcher;
  a captured-transcript test also checks stale offsets and incorrect markers. Python
  harness tests pass 9/9. No kernel shutdown behavior or timeout was changed.
- Final native acceptance passes 172/172 checks, including installed trust/reboot,
  permissions, fail-closed missing entropy and independent clean ext2/payload/time
  checks. This run uses local fixtures without the optional home DNS checks.
  Evidence: /tmp/zenith-inet-pton-release.log,
  /tmp/zenith-inet-pton-host-checks.log, /tmp/zenith-inet-pton-harness.log and
  /tmp/zenith-inet-pton-network-final.log; artifacts in ignored obj/inet-pton-network-final.
  The first failed run remains in /tmp/zenith-inet-pton-network.log and ignored
  obj/inet-pton-network. Hosted CI is not verified by this local run.

## 2026-10-11 · Cosmos Run NIC configuration — delivered

- User confirms tools/run.sh networking works but the default Cosmos run does not.
  Installed Cosmos VS Code 1.8.1 defaults networkCard to none and forwards --nic none;
  Zenith had no .cosmos/config.json. The CLI's unset NIC instead leaves QEMU defaults.
- Add portable project config selecting e1000e for the extension Run path. Document
  cosmos run -a x64 --nic e1000e for the CLI, which does not load extension config.
- Actual installed extension property parser/argument builder selects --nic e1000e
  from the committed project config. A live boot through the installed Cosmos CLI
  confirms e1000e arguments, headless readiness, DHCP 10.0.2.15, ip/DNS status zero
  and ACPI S5. Evidence: /tmp/zenith-cosmos-run-nic.log; transcript checks pass.
- The test initially expected Cosmos to exit after guest poweroff; Cosmos intentionally
  launches QEMU with -no-shutdown, so that process-exit assertion timed out. The test
  terminated its own launcher; completed shutdown is verified from ACPI S5 instead.
  No disk was attached or modified. VS Code button interaction itself was not automated.
  git diff --check passes. No kernel rebuild is needed for this launcher setting.

## 2026-10-11 · P1 signed offline script/data packages — delivered

- User selected package management with default core/extra sections, grouped base
  tools, dependency/conflict management and delivery design. Clarified sequencing:
  install scripts/data first, then unbundle compiled tools. Bring native userland
  foundation forward before independent compiled-tool packages; retain the test DE.
- Implemented RSA-PSS/SHA256 signed bounded offline containers, root-pinned public
  keys, grouped bootstrap inventory and default repository config without an invented
  mirror endpoint. Native verification reuses pinned Mbed TLS, requiring neither
  private keys nor entropy/time for public verification. TLS and package trust differ.
- Implemented installed ownership DB, multi-package dependency/range/conflict plans,
  upgrade/remove/verify/recover, root/ext2 enforcement and durable undo/commit detection.
  Preserve locally changed files; no maintainer hooks or overwrite force option.
- Host suite passes 517/517, including signed-header/payload/trailer tampering,
  dependencies/ranges/cycles, conflicts/ownership/local edits, bounds/space, cancellation
  and persistent faults across creation, upgrade and removal. Actual signed tool/native
  RSA-PSS checks pass 5/5, legacy unsigned tool 9/9, transcript harness 9/9 and TLS
  bridge 35/35. Package signature verification remains usable without entropy.
- Final Release NativeAOT ISO builds (existing Cosmos NAOT0007 layering warnings).
  Native package acceptance passes 71/71: live-root
  refusal, installed ext2, default repositories, public-key pinning/rejection/preservation,
  signatures/tampering, atomic dependencies, conflict/dependent-removal denial, upgrade
  and script execution without kernel rebuild, local-edit preservation, clean reboot
  with prepared undo, exact recovery and persisted DB, ordinary-user denial, removal,
  independent clean e2fsck and empty installed DB through debugfs.
- Acceptance fixture repairs: Cosmos debug lines split labels/progress, so both raw
  and noise-stripped transcripts are retained for command assertions; root re-login
  uses a hidden ephemeral test password. One corrected fixture edit had an indentation
  error caught before launch. The final signing-tool rerun initially named a nonexistent
  shared library; rerunning with the built host bridge passes. No product timeout,
  signature requirement or authentication check was weakened.
- Recovery validates all backup records before writes and bounds aggregate undo data;
  native reads allocate from one checked length and explicitly dispose on errors to
  accommodate Cosmos unwind limitations. CI includes signing and native package checks;
  ephemeral private fixture keys are excluded from uploaded artifacts. New standalone
  harnesses restore the tool project so fresh CI checkouts have package assets.
- Scope: no automatic repository refresh/dependency download, /etc merge, maintainer
  hooks, kernel/boot update, native tool execution or snapshot/power-cut guarantee.
  Ext2 still requires host repair after some unclean shutdowns. The VM is not provisioned.
  Documented future delivery uses signed per-section indexes and immutable content-
  addressed .zpk artifacts, with signing separate from serving and freshness/rotation
  gates before an online client. Next accepted direction is native ABI/process work
  before compiled tool unbundling, while retaining the current test DE.
- Evidence: /tmp/zenith-p1-host-verified.log, /tmp/zenith-p1-build-verified.log,
  /tmp/zenith-p1-native-final-checked.log, /tmp/zenith-p1-signed-verified.log,
  /tmp/zenith-p1-legacy-verified.log, /tmp/zenith-p1-harness.log and
  /tmp/zenith-p1-tls-host-final.log. Disposable guest artifacts are ignored under
  obj/p1-package-final-checked. Hosted CI has not been run. Documentation local links
  pass (131 checked); git diff --check passes.

- Network regression follow-up: KVM live/installed checks passed, then the no-NIC
  UEFI reboot stalled before Zenith output (a fresh copy stalled at Limine too).
  Preserved logs; stopped the identified hung guest. The same disk reaches COM1
  under TCG. Full TCG regression passes 172/172, including offline/no-entropy boots,
  installed trust, faults/cancellation/denials and independent ext2/payload/time checks.
  Evidence: /tmp/zenith-p1-network-tcg.log and ignored obj/p1-network-tcg; failed KVM
  transcript: /tmp/zenith-p1-network-verified.log. This is not classified as the older
  Cosmos APIC #GP because no such fault appears in these logs.
- Final review found native staging replacement could delete a preexisting .pkg-new
  file. Refuse occupied staging paths before preparing the transaction and in the
  native writer; host and guest checks verify unowned staging content is preserved.
  After an unclean shutdown, an occupied staging file may need root inspection/cleanup
  before recovery; no automatic deletion of unrelated content is attempted.

## 2026-10-11 · Delivery sequencing and future application planning

- User accepted the persistent installed-system workflow and order: self-contained
  ISO/system installer, declarative package recipes/builder, server deployment kit,
  useful scripts/data packages on their installed VM, signed online client, then
  native ABI/process support and compiled tool unbundling. Server setup can overlap.
- Recorded Files and GUI Editor overhaul requirements plus Zenith-specific celestial
  naming direction. Sunrise/Sunset and other suggested names are proposals, not
  selected renames or independent packages. Additional applications remain ideas.
- Added GCC-preferred host cross-SDK and eventual on-Zenith compiler goals, CPython
  and nano/Vim feasibility stages with libc/process/terminal/dependency and 512 MiB
  root-capacity gates. Checked upstream GCC/Python/nano/Vim documentation; no port
  was implemented or runtime feasibility proven. No server was deployed.
- Documentation-only slice; local links pass (80 checked) and git diff --check passes. No runtime
  code changed, so prior runtime counts remain historical package-slice evidence.

## 2026-10-11 · M4 self-contained installation and kernel-generation foundation

- Implementing guest-side x64 UEFI boot-file delivery through Limine executable/module
  responses, a 128 MiB ESP with content-addressed kernel directories, boot inventory,
  stable root PARTUUID selection, mounted /boot/efi and explicit RAM rescue entry.
  Source payload validation precedes disk erasure; boot menu is written after artifact
  flush/read-back. Package kernel updates remain a separate signed activation gate.
- Release ISO builds; host tests pass 524/524. Native validation results follow below.
  Removed host boot-file repair from installed test harnesses; independent read-only
  GPT/checksum/artifact verification now checks what the guest actually installed.

- Native source-path correction: the first prototype used a boot URI for Limine's
  internal module, which requires a filesystem path. Installation rejected it before
  GPT writes; an independent scan confirms the entire 256 MiB target stayed zero.
  Corrected to /EFI/BOOT/BOOTX64.EFI on the executable's volume. A first TCG serial
  regression attempt dropped two interactive-read characters; dedicated acceptance
  uses paced input. No product timeout or input handler was relaxed.
- Test fixture updates retain versioned kernel/root arguments when changing session;
  remove implicit host repairs; check the new 126 MiB root on a 256 MiB target and
  PARTUUID fstab. Fixed a stale manual-heading expectation in the UEFI session test.
  Initial acceptance fixture also needed new installer/status output assertions.
- Final review moved duplicate root rejection outside typed catches to avoid Cosmos
  native exception misclassification; added a two-clone negative boot test. Source
  validates architecture/size before erasure and explicitly releases owned handles
  on faults because native unwind-finally is unreliable.
- The CLI path is this slice's scope. Graphical installer/account wizard, USB/hardware,
  BIOS installation, root snapshots and signed boot-package activation are still
  planned. P1 restrictions remain scripts/data; ext2 crash recovery limits remain.
- Final Release ISO builds; host tests pass 524/524 and Python transcript tests 9/9.
  Native install/reboot/rescue passes 28 checks on the final kernel, with exact ELF
  hash, mounted ESP, persistent file across a second disk-only UEFI boot, independent
  clean e2fsck and matching root/ESP inventory. Duplicate UUID refusal passes 2/2 on
  two disk clones. The first negative fixture used an unsupported AHCI slave unit;
  corrected to a second port and reran only that check. Final signed package regression
  passes 71/71, including installed recovery and independent clean e2fsck/DB inspection.
- Evidence: /tmp/zenith-m4-build-final.log, /tmp/zenith-m4-host-final.log,
  /tmp/zenith-m4-install-checked.log, /tmp/zenith-m4-duplicate-checked.log and
  /tmp/zenith-m4-packages-checked.log. Disposable images/logs are ignored under
  obj/m4-install-checked, obj/m4-duplicate-checked and obj/m4-packages-checked.
  First complete acceptance (before the duplicate-catch refinement):
  /tmp/zenith-m4-install-final.log, 28/28. Final positive checks were rerun; the
  two-disk negative check then passed separately after its attachment fix.
- Desktop/filesystem regression passes 135/135 at 512 MiB, including installed-root
  persistence, user/group/mode/symlink attributes and independent ext2 checks. Evidence:
  /tmp/zenith-m4-de-final.log and ignored obj/m4-de-final. Read-only VS Code verification
  is a separate task, so it does not prevent booting legacy disks without inventory.

- Disk-only desktop UEFI/session override passes 10/10 with no ISO or host boot-file
  repair, including manual pages and independent ext2/attribute checks. Evidence:
  /tmp/zenith-m4-de-efi.log and ignored obj/m4-de-efi. Install/rescue/ambiguity and
  disk-only desktop acceptance used TCG; package and baseline desktop regressions
  used KVM. This does not establish that the historical no-NIC KVM stall is fixed.
- Local documentation links pass (187 checked), Python compilation/shell syntax and
  git diff --check pass. CI includes the new installation/rescue/ambiguity check;
  hosted CI awaits push. No real disk, home package VM or upstream checkout was modified.

## 2026-10-11 · Guided installer completion

- User requested finishing the installer, with verified kernel package updates required
  before their actual installation. Kernel activation is now the next deployment gate,
  ahead of real installation; no kernel updater is claimed by this installer work.
- Implemented Sunrise disk selection, account form, explicit erase confirmation,
  worker progress/error handling and completion, sharing the target setup backend with
  interactive headless install --setup. Initial password records are prepared before
  target erasure; custom first-user homes and reboot account behavior are included.
- Sunrise runs hashing/copying on a worker, reports kernel-copy percentages, masks
  password fields and releases password references on completion/close. Closing and
  normal shutdown are blocked while installation is active; overlapping installs
  are refused. This does not provide power-loss recovery or secure erasure of managed
  password strings. Existing plain install --yes and P1 script/data restrictions stay.
- Custom first-user homes are secured on install/boot; Files and Editor select the
  account home, and boot no longer creates a phantom /home/user after custom setup.
- Release ISO build and 532/532 host tests pass. Python transcript tests pass 9/9.
  Guided native GUI checks pass 38/38 and serial setup 47/47, including empty-input
  or declined-erasure refusal with untouched GPT, secret-free transcripts, configured
  logins/private home/shadow and persistent user data across two disk-only UEFI boots.
  Independent boot-artifact checks and e2fsck pass. Final GUI screenshots inspected.
- Desktop/filesystem regression passes 135/135; disk-only UEFI desktop passes 10/10
  with independent clean ext2/attribute checks. GUI used KVM; serial setup and UEFI
  desktop used TCG. Help/manual-only changes were rebuilt and final GUI acceptance
  rerun (38/38). No actual disk, home server VM or upstream checkout was modified.
- Signed package regression passes 71/71 under TCG, including installed transactions,
  reboot undo recovery, user access checks, secret-free transcripts and independent
  clean e2fsck/ownership DB inspection. KVM acceptance is not universally established:
  one installed boot panicked with denied /etc/passwd access during headless startup;
  an isolated retry of that verified image failed earlier at exact root UUID selection.
  Neither failure is claimed fixed or explained; TCG passed the full workflow.
- Evidence: /tmp/zenith-guided-build-documented.log, /tmp/zenith-guided-host-final.log,
  /tmp/zenith-guided-gui-documented.log (38/38), /tmp/zenith-guided-headless-final.log
  (47/47), /tmp/zenith-guided-de.log (135/135), /tmp/zenith-guided-de-efi.log (10/10),
  /tmp/zenith-guided-packages-tcg.log (71/71); failed KVM transcripts are in ignored
  obj/guided-packages/installed and obj/guided-package-kvm-retry. Initial build retry
  required adding ~/.dotnet/tools to PATH; host VSTest needed sandbox socket access.
  Documentation local links pass (187), and git diff --check passes. CI adds both
  guided paths; hosted CI awaits push.

## 2026-10-11 · P3 signed kernel upgrades

- Implementing a dedicated signed kernel container and offline builder, leaving P1
  payload paths unchanged. The executor binds root/ESP UUIDs, verifies the retained
  kernel/EFI loader, checks free space and version monotonicity, stages a new hash
  path and publishes a pending next-boot menu with the previous kernel retained.
- Confirmation, rollback and durable undo recovery are explicit operations. Manual
  boot-menu fallback and ISO repair remain necessary; automatic boot-attempt rollback
  and arbitrary FAT/ext2 power-loss guarantees are not assumed.
- Host fault/cancel/signature tests and native upgrade/reboot/recovery acceptance are
  in progress. All disks/signing keys used in validation are disposable fixtures.
- Implemented kernel-seal/kernel-check in the host tool and pkg kernel check/install,
  boot status/confirm/rollback/recover in the guest. Update records bind root/ESP,
  immutable EFI and hash-addressed kernels; explicit high-water versions prevent
  downgrade after rollback. Confirmation retains the last working kernel and removes
  only the recorded retired generation. A secondary Limine menu covers the tested
  root-menu rename gap; recovery leaves root accounts/data/packages unchanged.
- Full-sized ELF delivery required a separate fetch --kernel bound (32 MiB + 4096,
  180-second total; ordinary fetch stays 8 MiB/30 seconds). Actual Cosmos 3.0.89 source
  revealed missing ACKs for ACK-only payload, an incorrect receive-range test and
  advertising the peer's window. Signature-checked build-copy adaptations acknowledge
  payload, require contiguous in-window bytes and apply receive-window backpressure
  with reopening ACKs. No upstream checkout or dependency cache was changed.
- Native networking regression passes 172/172 under TCG after these adaptations;
  signed script/data package regression passes 71/71, including reboot undo,
  permission checks, secret-free transcripts and independent clean ext2 inspection.
  Evidence: /tmp/zenith-kernel-network.log and /tmp/zenith-kernel-packages.log.
- Actual forced termination exposed the prior clean-only root gate: the flushed
  ext2 root was refused because mounts deliberately leave it marked unclean. Boot
  now runs the existing bounded read-only checker before admitting a supported dirty
  root. Inconsistent/error-marked volumes remain refused; no repair/clean-bit override
  occurs. Host read-only dirty/corruption checks pass (553/553 full suite). Native
  crash acceptance is being rerun with a deliberately corrupt dirty-root negative.
- Crash checkpoints stop persistent logging and flush before emitting a serial-only
  marker, so the fixture does not introduce an unflushed log write after its boundary.
  The serial harness now scans incremental bytes (10/10 transcript regressions) rather
  than repeatedly rescanning full debug logs. A concurrent TCG transfer hit the fixed
  180-second guest deadline near completion; isolated transfers passed without
  increasing it. P0 tool tests pass 9/9 and signed tool/native RSA tests pass 5/5.
- Native crash checks now prove both disk-only and external-ISO recovery and reject
  an intentionally corrupt dirty-root copy. A subsequent actual-ELF reboot exposed
  Limine choosing the retained secondary configuration ahead of the updated primary;
  publication now keeps the old secondary through the primary rename gap and commits
  both menus to the same active generation before ending the journal. Confirmation
  also preserves that agreement. Final activation/fallback acceptance is being rerun.
- The native coordinator now requires the ESP mount to exist and rejects other mounts
  at/above/below boot metadata. CLI mount/unmount shares the maintenance gate so the
  namespace cannot change while an install/package/kernel mutation is active.
- Independent ext2 checks pass on both recovered crash images and the ISO-recovered
  image. Read-only fsck.fat finds no allocation/chain/directory errors; its two existing
  Cosmos formatter warnings (EFI label without directory label, unknown optional
  free-cluster hint) are explicitly recorded/whitelisted. No host image repair is
  performed; FAT errors outside those exact messages remain failures.
- Final Release ISO build and 557/557 host tests pass, including each coordinator
  mutation/flush fault boundary, cancellation, signatures, retained artifacts,
  generation cleanup, dirty-root read-only audit and error-marked-state refusal.
  Final native kernel acceptance passes 169/169 under TCG: full real ELF-sized HTTP
  delivery, tampered/P1 separation, root/user rules, absent-ESP refusal/remount,
  real guest termination at flushed staging and primary-menu rename gaps, disk-only
  and external-ISO undo, deliberately corrupt dirty-root refusal, actual new/previous
  ELF hashes after reboot, persistent user data, confirmation/version floor, signed
  entry-loop failure and real keyboard selection of the previous Limine menu entry.
- Final installed ext2 e2fsck is clean; FAT allocation/directory audit passes with
  only the two recorded formatter warnings. Credentials are absent from transcripts.
  The captured actual Limine current/previous/RAM-rescue menu was visually inspected.
  Evidence: /tmp/zenith-kernel-build-complete.log,
  /tmp/zenith-kernel-host-activation.log (557),
  /tmp/zenith-kernel-native-complete.log (169), ignored obj/kernel-updates-complete.
  Supporting regressions: /tmp/zenith-kernel-network.log (172),
  /tmp/zenith-kernel-guided.log (47), /tmp/zenith-kernel-transcript-complete.log (10),
  /tmp/zenith-kernel-p0-tool-final.log (9), /tmp/zenith-kernel-signed-tool.log (5).
- The final ISO also passes 71/71 signed script/data package checks under TCG,
  including native transactions, reboot undo, permissions, secret-free transcripts
  and independent clean ext2/ownership DB checks. Evidence:
  /tmp/zenith-kernel-packages-complete.log and ignored obj/kernel-packages-complete.
- The kernel-only deployment prerequisite passes local disposable-VM acceptance.
  Next is the user's persistent VM/LAN test using KERNEL-UPDATES. No actual disk,
  home server VM or upstream checkout was changed. Existing KVM startup failures,
  physical/USB acceptance, hosted CI (awaits push), arbitrary FAT/ext2 power loss,
  automatic boot-attempt rollback and matched root snapshots remain separate limits.
