# Beacon — the network manager (U11)

[Master roadmap](../ROADMAP.md) · [Applications](../APPLICATIONS.md) · [Networking](NETWORKING.md)

Work IDs below are detailed steps of the master tracker (U11), not a separate schedule.

Beacon is Zenith's network manager, `zenith-beacon`, built from the approved
[concept](../design/beacon-concept.svg) (after NetworkManager, with a clearer flow). Requested 2026-11-18 together
with network sections in Meridian and Observatory and network and sound centers on the taskbar.

## B1 — status, traffic, history, diagnostics and safe changes (local, kernel 0.0.69, zenith-beacon 0.1.0)

- **Opens on an answer** in plain words: "Connected to the internet", "Connected to the local network only", "The
  cable is out", "Disconnected", with the kind, the mode (DHCP or manual) and how long the connection has been up.
  Disconnect/Connect and Edit sit beside it.
- **Connections grouped by kind:** Wired, and Wi-Fi and VPN, which say there is no adapter or service instead of
  hiding (Zenith has neither yet).
- **Traffic** for the last minute (per second), hour (per minute), day (per quarter hour) and week (per hour), down
  and up, on the overview and on its own page with the totals since start.
- **This connection:** address and prefix, gateway, DNS servers (marked when fixed by `/etc/resolv.conf`), hardware
  address, the DHCP lease's renewal and the host name.
- **History** of what happened, in plain words: connected (with the address), lease renewed, could not connect,
  cable unplugged and plugged in again (with how long), disconnected, turned off, settings changed, previous
  settings kept, slow DNS. The last 64 events and the day and week of traffic survive restarts in
  `/var/lib/zenith/network-history` (a fixed-size text file, written at most once a minute).
- **Diagnostics,** step by step from this machine outward: adapter, address, gateway (ping), DNS (a lookup of the
  package mirror's name, or `one.one.one.one`), internet (a TCP connection to the mirror, or 1.1.1.1) and secure web
  (a verified TLS connection to the package mirror; skipped when none is set, since Zenith trusts only its own
  CA). They stop at the first real failure and give one concrete suggestion ("Try another server" opens the editor at
  the DNS field). A slow DNS answer is a warning, not a failure.
- **Ping:** four echoes to a name or address (the gateway by default), each with its time, and a summary. Trace is
  not available yet: the network stack always sends a fixed hop limit and routes ICMP replies only to a client
  registered for their source, so routers' "time exceeded" answers cannot be received (network stack work, N2).
- **Edit connection:** Automatic (DHCP) or Manual (address, prefix, gateway, DNS). Applying keeps the old settings
  until the new ones connect: DHCP must answer, and a manual gateway must answer a ping; otherwise the previous
  settings come back, stay saved, and the history says why. Disconnect lasts until Connect or the next start and
  does not change the saved settings.
- **Permission:** reading is open to every user. Changes need root or the Desktop Portal grant
  `org.zenith.network.manage` ("change the network connection", until the program closes); Beacon asks when a change
  is refused. Meridian's existing Unlock (`org.zenith.settings.system`) grants it too.
- Keys: Ctrl+1..6 pages, Ctrl+E edit, Ctrl+R run diagnostics, Ctrl+P ping, Esc closes the editor or Beacon.
- Without a window: `native run beacon --status | --events | --history | --check | --ping HOST | --connect on|off |
  --apply auto | --apply manual ADDRESS/PREFIX GATEWAY DNS`.

### Native ABI: `zenith-native-network-center-abi` 1.0 (call 667, kernel 0.0.69)

[Header](../../userland/include/zenith_network.h). Request `(request, 64, 0)`: version 1, operation, output buffer and
capacity, value, input and its size, reserved zero. Older kernels answer -38.

| Operation | Value / input | Output |
|---|---|---|
| 1 status | | 512 bytes: state, saved mode, flags, address, mask, gateway, up to 4 DNS servers, MAC, connection seconds, lease and renewal seconds, bytes and rates, event count, adapter, host name, state text, saved settings, Wi-Fi/VPN presence, adapter model, UTC offset and time |
| 2 history | range 0..3 | version, range, count, seconds per sample, then pairs (bytes/s down, up), oldest first |
| 3 events | | version, count, total, then 64-byte events (time, kind, value, text), newest first |
| 4 check | step 1..6 | version, step, result (ok, slow, failed, skipped), milliseconds, subject, plain-words detail |
| 5 ping | host name or address | milliseconds, or -5 with the reason |
| 6 apply | 48-byte settings (mode, address, mask, gateway, DNS) | 0, -13 without permission, -5 with the reason (previous settings back) |
| 7 connect | 0 disconnect, 1 connect | 0, -13, -5 |
| 8 programs | | up to 16 programs that used the network: PID, open connections, bytes in and out, peer (only the caller's own unless root) |

Calls run outside the filesystem service gate (a check or DHCP can take seconds) and count with the interactive calls,
so an open window is not stopped by the general call budget. Ping and the checks are cancelled with the program.

### Code and checks

`src/Core/Network/NetworkCenter.cs` (status, checks, apply, sampling from `NetworkService.Tick`, persistence),
`NetworkHistory.cs` (the rings and events, host-tested), the 667 route in `Process.s`, `ProcessNetwork.Programs`,
`userland/beacon/main.c`.

- `NetworkHistoryTests`: seconds averaged into minutes, quarter hours and hours; bounded and sanitized events; the
  file format round trip and its fixed size; the program registry; the network grant.
- `tools/beacon-smoke.py`: the signed package requires the ABI; headless, as root: status, history ranges,
  diagnostics, ping, manual settings, a gateway that does not answer (previous settings back), automatic again,
  disconnect and connect, the history of all of it, an ordinary user refused a change but allowed to read; on the
  desktop the window, its pages, diagnostics and the editor by keyboard.

## Network sections elsewhere (same request)

- **Meridian › Network › Connections** shows the connection live (state, address, gateway, DNS, hardware, lease,
  traffic), Connect/Disconnect, and IPv4 settings (Automatic, Manual with address, prefix, gateway and DNS, or Off)
  with Apply and the same keep-old-until-connected rule. Without permission it offers Unlock. On kernels before
  0.0.69 it shows the text summary as before. Host-tested in `test_meridian.c`.
- **Observatory › Performance › Network** adds the connection (state and time, DHCP or manual), gateway and DNS, and
  "Programs using the network" (name and PID, open connection's peer, bytes in and out). `--probe` prints
  `OBSERVATORY_CONNECTION`.
- **Taskbar network and sound centers** (`src/Gui/Shell/TaskbarCenters.cs`): two buttons beside the clock. Network:
  the state in words and the address, the last minute of traffic, Connect/Disconnect (root sessions; others are sent
  to Beacon, which asks for permission), Diagnostics, Open Beacon and Network settings. Sound: the selected output,
  mute, volume (slider, -5/+5 %, or the wheel over the button), the programs playing with their levels, and Sound
  settings. Both go through the services' own rules (network permission, the audio session check). Escape or a click
  outside closes them.

## Next

- Trace (needs hop-limit control and ICMP error delivery in the network stack), link speed from the driver, several
  connection profiles with priorities, Wi-Fi and VPN once drivers and services exist (N2).
