# Native text/traversal VM test

Targets: kernel **0.0.22**, Sunbeam **0.6.0**, base/filesystem tools **0.2.0**,
manuals **0.1.4**. These extend the installed 0.0.21 CLI foundation. The native
algorithms use its existing ABI; the new kernel routes ordinary command names to
the signed packages. The [implementation profile](archive/2026-10-26/native-text-traversal.md)
records syntax and limits, which also ship in `man native-cli`.

After all five distributor releases are green, refresh/stage the kernel, reboot
and confirm it, then update the tools:

```sh
pkg refresh
pkg kernel update --yes
reboot
```

After login:

```sh
boot confirm
pkg update --yes
pkg verify
which grep find
base-tools --version
fs-tools --version
```

`which` should identify signed native base-tools and fs-tools. Until the package
update is complete, `recovery COMMAND ...` can select the older trusted builtin.

Use a test directory:

```sh
mkdir -p ~/cli-text
printf 'pear\napple\napple\n' > ~/cli-text/lines
sort ~/cli-text/lines | uniq
grep -En '^a.*' ~/cli-text/lines
seq 3 -1 1
printf 'a:b:c\n' | cut -d: -f1,3
printf 'aaab\n' | sed 's/a+/[&]/'
cp ~/cli-text/lines ~/cli-text/copy
diff ~/cli-text/lines ~/cli-text/copy
echo $?
find ~/cli-text -type f
du -s ~/cli-text
tree ~/cli-text
```

Expected: sort/uniq prints apple then pear; grep prints 2:apple and 3:apple;
seq prints 3, 2, 1; cut prints a:c; sed prints [aaa]b; diff prints nothing and
its status is 0. Find lists both files. Du reports logical-file KiB and tree
prints two file entries. Directory order is the natural VFS order.

Check controlled error statuses:

```sh
grep missing ~/cli-text/lines
echo $?
grep -E '[' ~/cli-text/lines
echo $?
```

Expected statuses are 1 for no match and 2 for invalid regex. Unsupported regex
features fail explicitly; `recovery grep`/`recovery sed` retain the previous
managed regex profile. No installed-executable corruption is needed for testing.

Please report exact command/output and the session used if anything differs.
Network clients remain the next extraction; xargs stays with shell execution.
