namespace Zenith.Core.Storage;

/// <summary>Small live/recovery manuals. Full manuals ship as zenith-man-pages.</summary>
internal static class ManualPages
{
    public const string Directory = "/usr/share/man";
    public const string PackageDirectory = "/usr/share/zenith/man";
    public static readonly (string Name, string Text)[] Pages =
    {
        ("zenith", """
            ZENITH LIVE AND RECOVERY GUIDE

            help lists commands; man COMMAND shows its usage. Full manuals are in
            the independently installed zenith-man-pages package.
            Live mode has a temporary RAM root. Sunrise or install DISK --setup
            installs ext2 and boot files; carefully review the erase confirmation.
            poweroff/reboot automatically sync and unmount filesystems.
            On an installed system, configure TLS/signing trust and pkg mirror,
            then pkg install zenith-man-pages zenith-fonts for full manuals/fonts.
            pkg update refreshes and updates installed data and stages a new kernel;
            reboot, test and boot confirm. Never skip pending/recovery diagnostics.
            See man headless for the serial recovery console and man licenses.
            """),
        ("sh", """
            SHELL RECOVERY GUIDE

            COMPOUND COMMANDS
            if COMMAND; then COMMAND; elif COMMAND; then COMMAND; else COMMAND; fi
            for NAME in WORDS; do COMMAND; done
            while COMMAND; do COMMAND; done; until COMMAND; do COMMAND; done
            Use quotes, $VAR, ${VAR}, $?, $(COMMAND) and $((ARITHMETIC)).
            Pipes |, redirects > >> <, && || ; and functions are supported.
            sh FILE runs a script. Ctrl+C cancels; Up/Down recall session history.
            Install zenith-man-pages for the full shell grammar and command guides.
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
        ("licenses", Zenith.Core.Storage.Ext2.Ext2License.Text + "\n\n" + Zenith.Core.Network.MbedTlsLicense.Text),
    };
}
