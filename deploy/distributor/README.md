# Debian build and package distributor

This P2 kit runs the builder and static distributor on one dedicated x86_64
Debian VM. The user is creating the VM from a template; version, IP, hostname,
SSH access, LAN/VPN routes and backups are still pending. Nothing has been deployed.

Start with a budget of 4 vCPUs, 8 GiB RAM and 80 GiB disk, expandable for retained
builds/releases. This is a suggested starting budget, not a measured minimum.
Use a stable LAN address reachable through the existing VPN and DNS (10.0.10.3).
Allow SSH for administration and HTTP from the intended LAN/VPN clients. The VM
also needs outbound HTTPS for Git/NuGet/toolchain installation. Do not add router
port forwarding for this LAN/VPN service. Record actual permitted subnets before
changing host/network firewall policy.

The initial workflow is manual:

`selected trusted commit → host tests → kernel build → signing → verification → atomic publication → manual guest fetch/install`

The Linux VM builds the kernel; Zenith does not need a compiler. Build logs/work
directories and signing keys are outside the nginx root. nginx workers only read
the published tree and cannot upload files or trigger builds. The `zenith-release`
account can build, sign and publish trusted project code; these capabilities are
not isolated from each other. Do not use it for untrusted PR/third-party builds.
A future automated CI builder should use a separate unprivileged build worker and
controlled signing/release handoff.

## Install the VM dependencies

Confirm the template with `cat /etc/os-release` and `uname -m`. This kit targets
Debian 12/13 on x86_64. Configure Microsoft's package feed for the actual Debian
version using the [official .NET Debian instructions](https://learn.microsoft.com/en-us/dotnet/core/install/linux-debian).
Then install as the VM administrator:

```sh
sudo apt-get update
sudo apt-get install -y git ca-certificates wget curl python3 nginx util-linux \
  clang binutils openssl e2fsprogs dosfstools mtools parted qemu-system-x86 ovmf \
  dotnet-sdk-10.0
sudo adduser --disabled-password --gecos '' zenith-release
sudo install -d -o zenith-release -g zenith-release -m 0755 /srv/zenith /srv/zenith/packages
```

The dependency list follows Zenith's actual CI/toolchain; QEMU/OVMF/filesystem
tools support native release validation. CPU virtualization/nested KVM is optional
for TCG validation; do not assume the template exposes usable `/dev/kvm`.

## Transfer the exact source and existing signing key

Local commits may not be pushed to GitHub yet. To deliver the same source without
pushing, create a bundle on the development host, after committing the release:

```sh
cd /home/azazel/cosmos-test/MyOS
git bundle create obj/zenith-source.bundle HEAD
```

Transfer that bundle over SSH to the VM. As `zenith-release`, clone it into
`/home/zenith-release/Zenith`, and compare `git rev-parse HEAD` with the development
host. A normal clone from `https://github.com/Azazel55605/Zenith.git` also works
when the required commit has actually been pushed. Do not automatically pull/build
an arbitrary latest branch as a release.

Import the **same signing key used for the successful installed kernel update**
through SSH/private storage. The earlier suggested development-host location was
`/home/azazel/.local/share/zenith/signing`; confirm your actual location. Never
generate a replacement key silently: the installed guest pins that public PEM.
Keep an offline recovery copy of the signing key and its exact PEM fingerprint.

On the VM use `/home/zenith-release/signing` with mode 0700, owned by
`zenith-release`; `private.pem` must be mode 0600. Neither key nor build credentials
belongs under `/srv/zenith/packages`. The web server publishes only a copy of the
public PEM, with a fingerprint conveyed through a trusted channel.

As `zenith-release`, install the pinned released Cosmos tools (not as root):

```sh
export PATH="$HOME/.dotnet/tools:$PATH"
dotnet tool install -g Cosmos.Tools --version 3.0.89
dotnet tool install -g Cosmos.Patcher --version 3.0.89
cosmos install -y --tools
dotnet --version
```

These are VM provisioning commands. Do not rerun the Cosmos bootstrap on an
existing development machine to deploy the distributor.

## Enable the static service

As the administrator, with the source checkout available:

```sh
sudo install -m 0644 /home/zenith-release/Zenith/deploy/distributor/nginx.conf \
  /etc/nginx/sites-available/zenith-packages
sudo rm -f /etc/nginx/sites-enabled/default
sudo ln -s /etc/nginx/sites-available/zenith-packages /etc/nginx/sites-enabled/zenith-packages
sudo nginx -t
sudo systemctl enable --now nginx
sudo systemctl reload nginx
```

This configuration is for a dedicated VM with no existing web sites. It serves
`/srv/zenith/packages` on port 80, disables browsing/symlink access and accepts
GET/HEAD only. An empty root returns 403/404; this does not indicate a broken service.
nginx configuration/runtime checks must happen on the actual VM.

Initial LAN HTTP matches the successful signed-package bootstrap. Package signatures
remain mandatory; HTTP does not authenticate the fingerprint downloaded alongside a
package. HTTPS/CA bootstrap will follow once the hostname/certificate policy is known;
there is no automatic redirect to HTTPS in this kit. Never use `https://` against this
HTTP endpoint. See [nginx static serving](https://docs.nginx.com/nginx/admin-guide/web-server/serving-static-content/)
and [access/method controls](https://nginx.org/en/docs/http/ngx_http_core_module.html#limit_except).

## Build and publish the next kernel

Run as `zenith-release` from a clean committed checkout:

```sh
cd /home/zenith-release/Zenith
bash deploy/distributor/build-kernel.sh 0.0.2 \
  /home/zenith-release/signing /srv/zenith/packages
```

The example follows the user's first 0.0.1 upgrade; use a greater version than
the actual guest high-water mark. A previously used ELF hash is also refused by
the guest even if the version increases. Make a real release change before
attempting another upgrade of an already-installed identical kernel.

The script serializes builds, refuses dirty/untracked source or a local Cosmos
override, records the full commit, tests host logic, builds a Release ISO/ELF,
signs the ELF and invokes the verifying publisher. Work/logs remain in
`obj/distributor/release-*`; inspect them after failure. It does not run native
QEMU acceptance automatically and does not activate any guest update. Before
treating a release as usable, run the appropriate native smoke tests and then
perform the real installed VM/reboot checks. Retain the external ISO/old generation.

Publication yields an immutable directory:

```
/srv/zenith/packages/
  core/zenith-kernel/0.0.2/
    zenith-kernel.zpk
    public.pem
    release.json
  extra/                  future independently installable scripts/data
```

The publisher verifies signatures and signed name/version/repository identity
on a staged copy. It flushes the files, then renames the complete directory into
place on the same filesystem. Concurrent publication fails clearly; an existing
version is never replaced. Staging and the lock are siblings outside the served
root. An interruption before activation may leave an orphan staging directory;
inspect/remove it only when no publisher is active. Do not delete published
releases automatically: retained clients/rollback generations may need them.
This is bounded local publication, not a general filesystem power-loss guarantee.

`release.json` is an **unsigned operator receipt**, recording archive digest/size,
key fingerprint and caller-provided source commit. It is not signed trust metadata,
a dependency index or a guest-consumable update channel. No `latest` pointer is
published. P4 will add signed indexes, freshness/rollback floors and downloads by
name; the current guest still uses explicit fetch/check/install commands.

## Verify distribution and guest installation

From another LAN machine, substitute the actual VM IP or DNS hostname:

```sh
curl -f http://VM_ADDRESS/core/zenith-kernel/0.0.2/release.json
curl -f -o kernel.zpk http://VM_ADDRESS/core/zenith-kernel/0.0.2/zenith-kernel.zpk
sha256sum kernel.zpk
```

Compare bytes/hash with the publisher receipt and independently verify the package
against the pinned public key. Repeat reachability from a VPN-connected client.
In Zenith, as root on the active installed disk kernel:

```text
fetch --kernel http://VM_ADDRESS/core/zenith-kernel/0.0.2/zenith-kernel.zpk /root/zenith-kernel-0.0.2.zpk
pkg kernel check /root/zenith-kernel-0.0.2.zpk
pkg kernel install /root/zenith-kernel-0.0.2.zpk
boot status
sync
poweroff
```

If the key is already pinned, it does not need importing again. For a new install,
fetch `public.pem`, verify its independently supplied exact SHA256 with `pkg trust`,
then check/install. Reboot from disk, test networking/files/package verification,
and run `boot confirm`. The first networking rescue bootstrap needed an ISO download
followed by an offline disk-kernel install; future working installed kernels can
download and install in one session. See [kernel updates](../../docs/KERNEL-UPDATES.md).

## Publish independently installable data packages

Use the existing PackageTool `build`/`seal` commands for committed payload recipes,
then publish a signed artifact with the same verifier. Example for an already built
and signed `zenith-docs` 0.0.1 archive:

```sh
python3 -B tools/publish-package.py --root /srv/zenith/packages \
  --package /path/to/zenith-docs.zpk --public-key /home/zenith-release/signing/public.pem \
  --tool tools/Zenith.PackageTool/bin/Release/net10.0/Zenith.PackageTool.dll \
  --kind data --repository extra --name zenith-docs --version 0.0.1 \
  --source-commit "$(git rev-parse HEAD)"
```

The signed manifest must match the requested name/version/repository. Package
recipes/mode support and useful initial packages remain their own next slice.
This kit does not relabel compiled built-in tools as independent executables.

## Validation and backup boundaries

`python3 -B tools/test_publish_package.py` uses disposable real signed kernel/data
fixtures and checks byte-exact HTTP delivery, signature/key mismatch, immutable
versions, signed identity, traversal/symlinks/private-key rejection, concurrent
publication and invisible/failed staging. It does not provision Debian or substitute
for nginx, DNS/VPN, VM build and installed Zenith acceptance.

Back up the signing key separately from the published tree. Retain source commits,
release receipts and build/test logs with releases. The published tree is restorable
without signing keys; restoring signing capability requires its private backup.
Actual VM OS/SSH/network/certificate/backup values will be recorded when supplied.
