# Native network client VM test

> Reusable test procedure. Results and remaining acceptance are tracked only in
> [the master roadmap](../ROADMAP.md#state-and-evidence); version-specific examples
> are historical handoffs, not new release instructions.

The tagged `zenith-net-tools/v0.1.0` package provides the original file downloader,
which requires `zenith-native-network-abi` 1.0. Version 0.2.0 adds the curl-style client.
Its signed manifest additionally requires `zenith-native-web-transport-abi` 1.0.
Run the public HTTPS checks below with net-tools 0.2.0 and kernel 0.0.80 or later.

## Distributor

When no build is running, pull the controller so it recognizes the new package
recipe, then start a poll:

```sh
cd /home/zenith-release/Zenith
git pull --ff-only
sudo systemctl start zenith-distributor.service
```

Check delivery of the kernel, net-tools and manual pages in the dashboard. Source/tag
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

Record the net-tools version from the installed receipt. Command routing should
show signed native net-tools. A present modified/unowned executable refuses; `recovery COMMAND ...`
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

## Public HTTPS checks for the curl-style client

Confirm that the running kernel lists the web transport capability and the installed
client lists the new options. Tagged net-tools 0.1.0 can still run on this kernel
with its original command syntax.

```sh
pkg list | grep zenith-native-web-transport-abi
fetch --help
fetch -I https://curl.se/ca/cacert-2026-11-05.pem
fetch -sL https://curl.se/ca/cacert-2026-11-05.pem | wc -c
fetch -sL -o ~/cli-net/public-ca.pem https://curl.se/ca/cacert-2026-11-05.pem
wc -c ~/cli-net/public-ca.pem
```

Expect capability 1.0, a successful HEAD response and matching nonzero byte
counts for the pipe and file. The public file is test data for the byte-count check. Repeat the private mirror check above to
confirm that its configured CA also works.

File downloads default to 8 MiB. `--kernel` raises the file bound to 32 MiB +
4096 bytes. Native stdout supports 1 MiB. The network service permits one
active client connection. A framing or write error discards the staged file.

Record the package and kernel versions, session, boot confirmation and results
against N3 in the master roadmap.
