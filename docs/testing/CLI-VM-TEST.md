# CLI package VM test

> Reusable test procedure. Results and remaining acceptance are tracked only in
> [the master roadmap](../ROADMAP.md#state-and-evidence); version-specific examples
> are historical handoffs, not new release instructions.

Check the current compatible kernel, Sunbeam, base/filesystem and manuals versions
against distributor receipts. Do not downgrade to the original 0.0.21 examples.
This procedure records expectations, not installed acceptance state.

## Distributor

When no build is running, update the controller so it recognizes the required
package recipes, then start a poll:

```sh
cd /home/zenith-release/Zenith
git pull --ff-only
sudo systemctl start zenith-distributor.service
```

The release tags trigger the existing build/sign/publish pipeline. Check the
dashboard for successful delivery of all five releases. Do not stop an active
build to update the controller.

## Installed Zenith VM

Install the kernel first. The new tool packages deliberately require capabilities
that kernel 0.0.20 does not provide; a combined update on 0.0.20 can refuse those
dependencies before staging its kernel.

```sh
pkg refresh
pkg kernel update --yes
reboot
```

After verifying the new kernel boots and login works:

```sh
boot confirm
pkg update --yes
pkg install --yes zenith-base-tools zenith-fs-tools
pkg verify
```

## Everyday command checks

```sh
which cat ls
base-tools --version
fs-tools --version
export CLI_TEST=ready
base-tools env | grep CLI_TEST
mkdir -p ~/cli-test
echo hello > ~/cli-test/one
cp ~/cli-test/one ~/cli-test/two
cat ~/cli-test/one ~/cli-test/two
wc -l ~/cli-test/one ~/cli-test/two
ls ~/cli-test/one
stat ~/cli-test/one ~/cli-test/two
```

`which` should select the signed native base/file tools. `cat` prints two hello
lines; `wc` reports one line per file and a total of two. The tools support a
bounded Zenith utility profile, not every GNU option.

Try `sleep 2 &`, then `jobs` and `wait %$!`. `$!` is the shell job ID, not a native
PID. Try `cat` interactively: enter a line, then Ctrl+D at an empty line to finish;
Ctrl+C should return to a usable prompt. Check these in the local text console and
the graphical terminal. `recovery COMMAND ...` invokes the trusted builtin when
needed. Do not modify installed executables to test tamper refusal; the automated
gates use disposable fixtures for that.

Please report package versions, console/session used, any failing command and its
exact output. Remaining text/traversal tools and owned network interfaces are the
next CLI work; GUI Settings and the design language follow that milestone.
