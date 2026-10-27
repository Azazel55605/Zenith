# Native network client VM test

> Reusable test procedure. Results and remaining acceptance are tracked only in
> [the master roadmap](../ROADMAP.md#state-and-evidence); version-specific examples
> are historical handoffs, not new release instructions.

Use the current compatible kernel/net-tools/manuals receipts. The network ABI first
shipped in 0.0.23; compatibility is capability-based, not a request to install that
historical version. Installed results live in the master evidence table.

## Distributor

When no build is running, pull the controller so it recognizes the new package
recipe, then start a poll:

```sh
cd /home/zenith-release/Zenith
git pull --ff-only
sudo systemctl start zenith-distributor.service
```

Await successful delivery of all three release tags in the dashboard. Source/tag
publication is separate from distributor signing, delivery and installed tests.

## Installed Zenith

Install the compatible kernel before the new package:

```sh
pkg refresh
pkg kernel update --yes
reboot
```

After checking boot/login:

```sh
boot confirm
pkg update --yes
pkg install --yes zenith-net-tools
pkg verify
net-tools --version
which fetch nslookup nc
```

Expected version: net-tools 0.1.0; command routing should show signed native
net-tools. A present modified/unowned executable refuses; `recovery COMMAND ...`
explicitly selects the earlier builtin. Configuration/trust/NTP remain trusted
services. `man native-cli` and `man network` document the profile.

## Home network checks

These use the deployment names/addresses already used for package delivery:

```sh
ip
nslookup zenith-stable.net.local
ping 10.0.20.7
nc -z zenith-stable.net.local 443
mkdir -p ~/cli-net
fetch https://zenith-stable.net.local/core/index-v3.zri ~/cli-net/index.zri
wc -c ~/cli-net/index.zri
```

DNS should return your configured proxy address; TCP should connect to port 443.
The completed fetch must report verified TLS 1.2. Fetching an index does not install
or trust a package; package signatures remain independently verified.

Repeat the fetch without --replace: it should refuse the existing destination.
Then explicitly replace it:

```sh
fetch --replace https://zenith-stable.net.local/core/index-v3.zri ~/cli-net/index.zri
pkg refresh
```

For cancellation, use a known sufficiently large download, press Ctrl+C during
progress, then run the TCP probe again. The cancelled command should return to a
usable prompt, preserve an existing destination, discard staging and release the
network lease. Do not overwrite a real installed executable or trust file for this
check; malicious-input/tamper/certificate tests use disposable automated fixtures.

The service remains serialized: overlapping clients can report network busy.
There is no insecure TLS flag, redirect/chunk/compression support, arbitrary raw
packet/listener interface or general POSIX socket compatibility. Default fetch is
bounded to 8 MiB; --kernel raises the file bound without granting boot-file writes.
Native fetch does not replay GET after body failure; connect retains bounded retry.

Please report exact versions, session used, commands/output, and boot confirmation.
After installed network acceptance, the next stage is protected graphical-client
and settings services, followed by independent Settings and shared UI design work.
