namespace Zenith.Core.Storage;

/// <summary>
/// Hand-written manual pages, installed to <c>/usr/share/man/&lt;name&gt;.txt</c> on every boot
/// (they are system files, so a newer kernel brings newer pages). Commands without one get a
/// page generated from their usage line by <c>man</c>.
/// </summary>
internal static class ManualPages
{
    public const string Directory = "/usr/share/man";

    public static readonly (string Name, string Text)[] Pages =
    {
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

        ("licenses", Zenith.Core.Storage.Ext2.Ext2License.Text),

        ("mount", """
            NAME
                mount - list mounts or attach a filesystem

            SYNOPSIS
                mount
                mount [-t fat|ext2] PARTITION DIRECTORY

            DESCRIPTION
                With no arguments, show the mount table. Otherwise mount the named
                partition (see lsblk) at an existing directory. FAT is the default.
                Use -t ext2 explicitly for experimental secondary ext2 volumes.
                Already-mounted partitions and occupied mount points are rejected.
                umount DIRECTORY flushes and detaches a volume; / stays mounted.

            EXT2
                Requires clean revision 1, 1 KiB blocks, 128-byte inodes and only
                the filetype feature. This profile check is not a consistency check.
                Check disposable images with host e2fsck before mounting. Root and
                installer still use FAT32. Users/permission enforcement is pending.

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
                /dev/random is pending a suitable kernel entropy source.
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
                install DISK --yes      erase DISK and install

            DESCRIPTION
                Creates a GPT partition table with a 64 MiB EFI system partition and a FAT32
                root partition, then copies /etc, /home, /root, /usr and /var. The kernel and
                the bootloader still have to be added from the host with
                tools/make-bootable.sh before the disk boots on its own.

                On the next boot Zenith finds the installed root partition by its
                /etc/os-release and mounts it at /.
            """),
    };
}
