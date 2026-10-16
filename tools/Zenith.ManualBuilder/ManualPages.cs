namespace Zenith.Core.Storage;

/// <summary>
/// Package-owned manual pages emitted by the host builder
/// (they are system files, so a newer kernel brings newer pages). Commands without one get a
/// page generated from their usage line by <c>man</c>.
/// </summary>
internal static class ManualPages
{
    public const string Directory = "/usr/share/man";

    public static readonly (string Name, string Text)[] Pages =
    {
        ("network", """
            IPV4 NETWORKING

            ip | ifconfig | netcfg status
            netcfg apply | dhcp | off  (root)
            netcfg static IP MASK GATEWAY DNS  (root)
            nslookup HOST [SERVER [PORT]]
            ping HOST
            nc -z HOST PORT
            ntp HOST [PORT]  (root)
            netcfg ntp HOST [PORT] | ntp off  (root; saved boot target)
            netcfg dns auto | dns IP [IP [IP]]  (root)
            tls selftest | trust FILE SHA256  (trust import: root)
            fetch [--replace] [--kernel] http(s)://HOST[:PORT]/PATH FILE

            Single primary IPv4 uplink; DHCP configures asynchronously at boot.
            Configuration: /etc/zenith/network.conf; hostname, hosts and resolv.conf
            under /etc. Resolver nameserver lines override DHCP DNS. Installed saves
            persist; live changes are volatile. Missing NIC does not block the shell.
            apply validates and reloads hostname/config/hosts/resolvers from disk.
            dns auto clears local overrides; dns IP saves up to three IPv4 servers. Failed DHCP retries after thirty seconds.

            NTP adjusts software UTC only and is unauthenticated. No default server.
            fetch accepts status 200 with Content-Length; default max 8 MiB, headers 8 KiB,
            --kernel allows 32 MiB + 4096 bytes, total timeout 180 seconds;
            two attempts, idle 3s/body 30s per attempt plus DNS/connect time. Ctrl+C
            cancels polling; Cosmos TCP connect observes cancellation after up to 5s.
            Downloads stage privately beside FILE and commit only when complete.
            Existing names require --replace and must be regular files. Errors and
            cancellation preserve the prior destination and remove staging.
            HTTPS verifies CA chain, DNS/IP identity and certificate validity dates
            with Mbed TLS 3.6.7, TLS 1.2 ECDHE AES-GCM. tls trust checks the SHA256
            of the exact ASCII PEM file against a hash from a trusted channel.
            tls bootstrap URL SHA256 downloads/imports that bundle in one step,
            bounded to 4096 bytes; failed verification preserves existing trust.
            No trust is installed by default. No insecure bypass, redirects,
            compression or chunking. NTP/BCL/filesystem time remains unauthenticated.
            TCP pending data 128 KiB; UDP/ICMP queues 32 packets; IPv4 frames bounded.
            nc supports probes only. Package authentication and Internet-wide
            protocol hardening remain future work.
            """),
        ("settings", """
            SETTINGS AND SESSION FOUNDATION

            settings show
            settings set keyboard|timezone|session VALUE  (root)
            session [status | list | set-default MODE]

            Keyboard/timezone changes share validation and staged persistence with
            localectl/timedatectl. Installed saves persist; live saves are volatile.
            loadkeys changes the active keyboard only. Session defaults take effect
            next boot. The test DE and x64 COM1 headless are available; WM is planned.
            Boot session= overrides saved defaults; no display falls back to headless.
            Unknown session schema versions are preserved and cannot be downgraded.
            Config paths: /etc/vconsole.conf, /etc/timezone, /etc/zenith/session.conf.
            Reads are bounded regular files. See the repository settings/session guide.
            """),
        ("headless", """
            SERIAL HEADLESS SESSION

            Select with session=headless at boot or session set-default headless (root).
            x64 COM1, 115200 8N1; ASCII input. No canvas/fonts/windows are initialized.
            Missing supported display selects headless with a capability diagnostic.
            Ctrl+C cancels; Ctrl+D ends read or resets an idle shell; Ctrl+U clears.
            Backspace/DEL edit; CR, LF and CRLF submit; multiline shell syntax works.
            Limits: line 4096, script 16384, queued lines 8, output 65536, history 64.
            Commands run on one worker; output waits for bounded draining.
            passwd/login/su hide passwords. Wait for each prompt before entering them.
            Installed configured systems start as nobody; exit/logout revoke identity.
            Live and unconfigured ext2 bootstrap as root; live mode is maintenance only.
            No SSH, local text console, profile auto-run, ANSI editing or job control.
            open explains unavailable desktop capability. Panic uses serial R to restart.
            Console text is separate from persistent kernel logs; serial still contains
            boot diagnostics and explicitly logged command output. Physical/VPN UART
            access is an administrator-controlled console, not encrypted transport.
            """),
        ("pkg", """
            SIGNED ONLINE/OFFLINE SCRIPT/DATA PACKAGES

            pkg repos              core/extra definitions; mirrors initially unset
            pkg list               bundled tools and independently installed data
            pkg trust FILE SHA256  root pins exact public PEM fingerprint
            pkg kernel check|install FILE.zpk  signed x64 kernel, active after reboot
            pkg mirror HTTPS_BASE_URL   save core/extra mirror (root)
            pkg refresh                 verify/cache signed repository indexes (root)
            pkg search [TEXT]           search fresh authenticated cached metadata
            pkg plan NAME...            preview dependencies/conflicts without downloads
            pkg install [--yes] NAME...  plan, confirm, download and install atomically
            pkg update [--yes]         refresh/update installed data and stage newer kernel
            pkg upgrade [--yes]        data-only upgrades from refreshed catalog
            native run NAME [ARGS...]  execute signed installed x86_64 text program
            sunbeam cat [FILE...]     independently packaged file/stdin reader
            sunbeam tee [-a] FILE     write piped input; -a appends
            sunbeam ls [-a] [-l] DIR   directory listing; pwd/stat also available
            pkg kernel update [--yes]   fetch/stage newer kernel; reboot/confirm remains
            boot status|confirm|rollback|recover  kernel generations
            pkg check FILE.zpk     verify RSA-PSS signature and complete payload
            pkg install FILE...    root installs/upgrades on installed ext2
            pkg remove NAME        root removes; dependent/modified files block
            pkg verify             check installed owned file digests/modes
            pkg recover            root resolves a durable pending transaction
            pkg inspect MANIFEST   unsigned experimental metadata inspection
            pkg plan MANIFEST      unsigned payload verification and dry-run

            Build/seal with tools/Zenith.PackageTool. Keep private signing keys off
            the served tree and guest. TLS CA trust and package public-key trust differ.
            Install up to 16 supplied packages atomically; dependencies may use the
            installed set or other candidates. Numeric ranges and declared conflicts
            apply to the full result. No forced overwrites, hooks or background updates.
            Preserve local edits; downgrades and bundled-tool replacement are refused.
            Payload paths: /usr/share/doc and /usr/share/zenith, except inventory.
            P1: no /etc/boot/bin writes, links, cross-mount extraction or hooks.
            Kernel containers use a separate signed type/coordinator and retain the
            previous kernel. No bootloader updates, root snapshots or automatic fallback.
            Existing compiled recovery builtins/DE require reboot; Sunbeam executes
            independently through native ABI 1. New native bundles require filesystem
            and I/O capabilities: update/confirm a compatible kernel before tools.
            File access uses invoking permissions/cwd. Stdin is a finite UTF-8
            pipe/redirect snapshot (1 MiB), not an interactive TTY. File writes are
            bounded to 1 MiB; stdout to 64 KiB. Writes are not rolled back on failure.
            Online indexes are signed.
            System update commits data and boot staging separately, then requires
            explicit reboot/boot confirm. No global root/ESP atomicity or hot reload.
            Recovery covers journaled package changes; ext2 power-loss repair may
            still require host tools. Kernel update undo is separate: boot recover. No root snapshot recovery.
            Limits: 32 installed packages; 32 KiB manifest; 64 files; 256 KiB/file;
            4 MiB/package; 8 MiB transaction payload. See docs/PACKAGES.md.
            """),
        ("accounts", """
            NAME
                accounts - installed ext2 authentication and access

            FIRST SETUP
                No default passwords. Initial root console: passwd root, then
                passwd user, then login user. Later boots require login NAME.
                Live/legacy FAT boots remain root-only maintenance sessions.

            COMMANDS
                login NAME switches the desktop, closes old windows and clears
                the clipboard. su [NAME] switches only this terminal.
                logout ends the desktop session. whoami/id use credentials.
                passwd [NAME] uses hidden prompts; passwords never go in argv.
                useradd NAME (root) creates a locked account and private home.
                passwd NAME unlocks it. Non-root passwd verifies the old password.

            ACCESS
                Owner/group/other read, write and directory search are enforced.
                Creation uses caller UID/GID and umask 022. /tmp is RAM ext2 1777.
                Sticky deletion and setgid directory inheritance are supported.
                chmod requires owner/root; chown requires root. Setuid/setgid do
                not grant execution privilege; apps still run inside the kernel.
                Held file I/O rechecks modes on every operation. Raw disk reads,
                installation, mounts, formatting, saved settings and power need root.

            LIMITS
                Passwords use PBKDF2-SHA256 and CPU/hypervisor RDSEED entropy.
                Missing entropy refuses password creation. Accounts require ext2.
                Malformed account files fail closed; repair from a trusted live boot.
                Native text processes have isolated address spaces; current compiled
                system services remain trusted kernel code. No ACLs, account aging
                or crash-atomic account updates.
            """),
        ("ln", """
            NAME
                ln - create a symbolic link on an ext2 volume

            SYNOPSIS
                ln -s TARGET LINK

            DESCRIPTION
                Stores TARGET verbatim at LINK. Relative targets refer to the
                link's parent directory; absolute targets refer to the VFS root.
                The target need not exist. Existing names are refused.
                Only symbolic links are supported. FAT does not support them.
                Ext2 targets must contain 1..1023 UTF-8 bytes with no NUL.
                Targets below 60 bytes are stored inline; others use one block.
                readlink LINK prints the stored target without following it.
                unlink LINK removes the link, preserving the target, including
                when the link is dangling. unlink also removes regular files;
                directories are refused. rm does not recognize dangling links.
                The installed VFS refuses .. components in link targets and
                stops following links after eight hops. Ownership and modes are
                persisted and enforced; ls -l shows real metadata. See man accounts.

            EXAMPLE
                ln -s persist.txt /mnt/new/alias
                readlink /mnt/new/alias
                cat /mnt/new/alias
                unlink /mnt/new/alias
            """),
        ("fsck.ext2", """
            NAME
                fsck.ext2 - check an unmounted supported ext2 partition

            SYNOPSIS
                fsck.ext2 PARTITION

            DESCRIPTION
                Check-only; never writes or repairs. Requires Zenith's supported
                Linux ext2 profile, a partition up to 512 MiB, and at most 65536
                inodes. Mounted partitions are refused. Checks bitmaps/counters,
                block ownership, inode sectors, directory records, dot/parent
                structure, reachability and link counts. Shows up to 32 errors
                and a total count. Exit status 0 means checks passed, 1 indicates
                errors or an unsupported/unavailable volume. This is not a full
                replacement for host e2fsck; use that for repairs/crash recovery.
            """),
        ("fsprobe", """
            NAME
                fsprobe - test file I/O and open-file deletion

            SYNOPSIS
                fsprobe DIRECTORY

            DESCRIPTION
                Creates a unique temporary directory, exercises two open handles,
                writes and reads after deletion, closes one then the other, and
                checks final deletion and inode reuse. Cleans up afterward.
                Cosmos currently retains an open file's name until final close;
                this is reported as deferred deletion, not Unix immediate unlink.
                Exit status 0 means the probe passed.
            """),
        ("mkfs.ext2", """
            NAME
                mkfs.ext2 - format an unmounted secondary partition

            SYNOPSIS
                mkfs.ext2 PARTITION [--yes] [-L LABEL]

            DESCRIPTION
                Writes a fresh ext2 filesystem, erasing the existing filesystem.
                PARTITION is a name from lsblk, such as sata2p0. Whole disks,
                mounted/overlapping partitions and the running root disk are
                refused. Without --yes, no writes occur. LABEL may contain up to
                16 printable ASCII characters.
                The supported profile uses 1 KiB blocks, 128-byte inodes and
                only the filetype feature, with backups in every block group.
                It creates / and lost+found. Partition sizes must be 1..512 MiB;
                a final group too small for metadata is rejected before writing.
                Data is not securely wiped. UUID remains unassigned until an
                entropy source is available. Installation/root discovery still
                use FAT32; this command does not change the running root.

            EXAMPLE
                mkfs.ext2 sata2p0 --yes -L SCRATCH
                mkdir /mnt/new
                mount -t ext2 sata2p0 /mnt/new
            """),
        ("files", """
            NAME
                files - simple graphical file manager

            SYNOPSIS
                open files [DIRECTORY]

            DESCRIPTION
                Starts in /home/user unless a directory is supplied. Select a row
                and use Open or Enter to browse folders or open text in Editor.
                Up goes to the parent; Refresh reloads the list.
                New file / Folder creates an empty file or directory. Rename
                changes the selected name. Copy / Move remembers the selection in
                this window; browse to the destination and choose Paste.
                Existing names are never overwritten. Delete asks for confirmation
                and removes only files or empty folders. Errors appear at the bottom.

            KEYS
                Arrows, Home/End, PageUp/Down    select items
                Enter                          open selected item / confirm prompt
                Backspace                      parent directory
                Ctrl+L                         change location
                Ctrl+N / Ctrl+Shift+N           new file / folder
                F2 / F5                        rename / refresh
                Ctrl+C / Ctrl+X / Ctrl+V        copy / move / paste
                Delete or Ctrl+D / Escape      ask to delete / cancel prompt
                Ctrl+A                         clear name/location prompt
                Ctrl+Q                         close window

            LIMITS
                File copies are limited to 8 MiB, Editor opening to 128 KiB.
                Folder copying/moves between parents and opening/copying device, virtual or linked files
                are unavailable. Moving across filesystems may fail; errors are
                shown without falling back to copy/delete. This is a built-in
                desktop app, not a separate executable or a permission boundary.
            """),
        ("zenith", """
            NAME
                zenith - introduction to the Zenith operating system

            DESCRIPTION
                Zenith is a small Unix-style operating system written in C# on Cosmos Gen 3.
                It has a desktop, a terminal with a POSIX-flavoured shell, and a Unix
                directory tree. Everything here is built into the kernel; there are no
                separate programs yet, but shell scripts in /bin run like commands.

            GETTING AROUND
                help            list every command
                man COMMAND     this kind of page, for any command
                man sh          the shell language: variables, loops, functions
                man hier        what the directories are for
                edit FILE       open a file in the text editor
                install         put Zenith on a disk (see 'man install')

            SETTINGS
                localectl set-keymap de           keyboard layout, saved in /etc/vconsole.conf
                timedatectl set-timezone Europe/Berlin   saved in /etc/timezone

            FILES
                /etc/profile    run by every new terminal
                /var/log/boot.log   the kernel log of this boot (boot.log.1: the previous one)
            """),

        ("licenses", Zenith.Core.Storage.Ext2.Ext2License.Text + "\n\n" + Zenith.Core.Network.MbedTlsLicense.Text),

        ("mount", """
            NAME
                mount - list mounts or attach a filesystem

            SYNOPSIS
                mount
                mount [-t fat|ext2] PARTITION DIRECTORY

            DESCRIPTION
                With no arguments, show the mount table. Otherwise mount the named
                partition (see lsblk) at an existing directory. FAT is the default.
                Use -t ext2 explicitly for supported secondary ext2 volumes.
                Already-mounted partitions and occupied mount points are rejected.
                umount DIRECTORY flushes and detaches a volume; / stays mounted.

            EXT2
                Requires clean revision 1, 1 KiB blocks, 128-byte inodes and only
                the filetype feature. This profile check is not a consistency check.
                Use fsck.ext2 on unmounted supported volumes; use host e2fsck for
                repairs. New installed roots use ext2; old FAT roots remain
                recognized. Installed users/access checks require ext2; see accounts.

            EXAMPLE
                mkdir /mnt/ext
                mount -t ext2 sata1p0 /mnt/ext
                cat /mnt/ext/host.txt
                umount /mnt/ext
            """),

        ("dd", """
            NAME
                dd - copy a bounded number of binary blocks

            SYNOPSIS
                dd if=INPUT of=OUTPUT count=N [bs=N]

            DESCRIPTION
                Copy up to count input blocks, stopping at EOF. bs is a byte count
                from 1 through 1048576 (default 512). A short read counts as one
                block. count=0 creates/truncates OUTPUT without copying data.
                INPUT and OUTPUT are required file/device paths. OUTPUT is replaced.
                Identical paths (including case-only aliases on FAT) are rejected.
                Numeric operands are decimal only. No stdin/stdout, skip, seek, conv
                or suffix operands are supported. Ctrl+C cancels between I/O calls.

            EXAMPLES
                dd if=/dev/zero of=/tmp/zeros bs=512 count=2
                dd if=/tmp/zeros of=/dev/null count=2

            DEVICES
                /dev/null reads EOF and discards writes.
                /dev/zero fills every read with zero bytes and discards writes.
                Both are character devices; they store no data.
                Disks and partitions appear under their Cosmos names, e.g.
                /dev/sata0 and /dev/sata0p1, as read-only block devices. They expose
                their capacity as file size, support bounded byte reads and seeks,
                and reject writes/truncation even on unmounted disks. Reopen after
                removal or a partition rescan; old handles do not follow replacements.
                /dev/random is read-only hardware entropy (up to 4096 bytes per read).
                Missing RDSEED fails closed. Raw disks are root-readable 0400.
                Use bounded reads
                for /dev/zero. Text filters currently read entire inputs before
                filtering, so head /dev/zero does not stop at a fixed number of lines.
            """),

        ("sh", """
            NAME
                sh - the Zenith command language

            SIMPLE COMMANDS
                name arg...             run a command
                a | b | c               pipe output into the next command
                cmd > file, >> file, < file     redirect output (replace/append) or input
                a; b    a && b    a || b        sequence, and-then, or-else
                ! cmd                   invert the exit status
                NAME=value              set a variable (export makes no difference yet)

            WORDS AND EXPANSION
                'literal'   "with $expansion"   back\slash escapes one character
                $NAME ${NAME}           variable
                $? $# $0 $1..$9 $@      status, argument count, script name, arguments
                $(command) `command`    command substitution
                $((1 + 2 * x))          integer arithmetic: + - * / % ** == != < <= > >= && || !
                ~  *  ?                 home directory, file name wildcards
                Unquoted expansions are split into words on spaces; quote them to keep one word.

            COMPOUND COMMANDS
                if cond; then ...; elif cond; then ...; else ...; fi
                for x in a b c; do ...; done        (without 'in': loops over "$@")
                while cond; do ...; done            until cond; do ...; done
                { cmd; cmd; }                       group, e.g. to redirect all output
                name() { ...; }                     define a function; $1.. are its arguments
                break [n]  continue [n]  return [status]  exit [status]
                Any command can be redirected: while read line; do ...; done < file

            TESTS
                [ -f file ] [ -d dir ] [ -e path ] [ -s file ]   file exists / is a directory / non-empty
                [ -z str ] [ -n str ] [ a = b ] [ a != b ]       strings
                [ 1 -lt 2 ]  -eq -ne -lt -le -gt -ge             integers
                [ ! expr ]  [ e1 -a e2 ]  [ e1 -o e2 ]           not, and, or

            SCRIPTS
                sh script.sh args       run a script in a child shell (its variables don't leak)
                . script.sh             run it in the current shell
                Scripts in $PATH (/bin) run by name. '#!' lines are comments.
                read [-p prompt] name   read a line of input into variables

            INTERACTIVE USE
                Up/Down history, Tab completion, Ctrl+C stops a command, Ctrl+L clears,
                Ctrl+D ends input for 'read' (or closes the terminal at an empty prompt).
                An unfinished line (open quote, 'if' without 'fi') continues with '> '.
            """),

        ("hier", """
            NAME
                hier - the Zenith directory layout

            DIRECTORIES
                /bin        shell scripts that run as commands (on $PATH)
                /boot       boot files (the EFI partition holds the bootloader)
                /dev        null/zero and read-only disks/partitions
                /etc        system configuration: hostname, passwd, profile, timezone, vconsole.conf
                /home/user  your files
                /mnt        mount points for other disks: mount sata0p1 /mnt/disk
                /proc       kernel memory, mounts, uptime and command line
                /root       the administrator's home
                /tmp        scratch space in memory, emptied on every boot
                /usr/share  read-only data such as these manual pages
                /var/log    logs: boot.log

            NOTES
                Without an installed disk, the whole tree lives in memory (live mode).
                The disk format is FAT32 for now, so names are case-insensitive and
                there are no permissions or symbolic links.
            """),

        ("install", """
            NAME
                install - install Zenith onto a disk

            SYNOPSIS
                install                 list disks Zenith can be installed on
                install DISK --setup    guided account setup and erase confirmation
                install DISK --yes      erase DISK and install

            DESCRIPTION
                Requires a disk of at least 256 MiB with 512-byte sectors. Creates
                GPT with a 128 MiB FAT32 EFI partition and an ext2 root up to
                512 MiB. Copies /etc, /home, /root, /usr and /var plus its own
                original kernel ELF and Limine UEFI bootloader. No host copying
                step is required. Mounted disks and the running root are refused.

                After success: sync; poweroff, eject the ISO, boot the disk with
                x64 UEFI firmware. Installed boot binds the root PARTUUID and a
                versioned kernel hash. The menu offers a RAM rescue entry.
                boot status reports the running kernel and installed boot identity.
                From the live ISO, use Sunrise in the app menu or 'open sunrise',
                or use --setup in an interactive terminal. Choose a hostname, user
                and passwords; explicit confirmation precedes erasure. --yes keeps
                the low-level live-tree copy behavior without an account wizard.
                Signed kernel updates: pkg kernel install FILE, then reboot and
                boot confirm after testing. boot rollback selects the previous kernel.
                Keep the external ISO; no automatic failed-boot rollback or snapshots.
                BIOS boot and physical hardware acceptance remain future work.
            """),
    };
}
