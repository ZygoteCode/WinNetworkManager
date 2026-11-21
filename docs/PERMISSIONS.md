# Permissions, security and privacy

## Least privilege

| Component | Runs as | Why |
|-----------|---------|-----|
| `WinNetworkManager.exe` (the window) | The signed-in user, never elevated by itself (manifest `asInvoker`) | Connection tables, adapters, processes, diagnostics, history and settings need no special rights |
| `WinNetworkManager.Collector.exe` (helper) | Administrator, only after you approve the Windows prompt (manifest `requireAdministrator`) | Windows allows kernel event tracing only to administrators and members of *Performance Log Users* |

- The helper starts only when you click **Turn on per-process monitoring**, or at startup if you choose *At startup*
  in Settings. It is never started silently.
- If the window itself already runs as administrator (you chose *Run as administrator*), or your account is in
  *Performance Log Users*, the collector runs inside the window and no prompt or helper is used.
- The status bar always shows the current privilege level; Settings explains what each missing right disables.
- If you decline the prompt nothing else changes; you can try again at any time.

## The helper process

- It has no window and no console, writes no files and opens no network connections.
- It is started with a random pipe name, the PID of the window and the window's user SID. The pipe:
  - is created with `FirstPipeInstance` (no other process can create it first),
  - has an access list that allows only that user (and denies network access),
  - checks that the connecting client is exactly the parent process (by PID); the window in turn checks that the
    server is the helper it started.
- The protocol is a small length-prefixed binary format with strict bounds checks; the only command the helper accepts
  is "shut down". Malformed data closes the connection.
- It exits when the window asks it to, when the window exits or disconnects, and stops its event tracing session on
  exit. The session name is fixed per Windows logon session, so a session left behind by a crash is replaced on the
  next start; real-time tracing sessions otherwise survive until a reboot.

## What WinNetworkManager does not do

No packet capture or content inspection, no traffic interception or modification, no credential access, no hidden
monitoring, no persistence beyond the optional start-with-Windows entry you turn on yourself, no attempt to bypass
Windows security or security software, no network scans: diagnostics act on one destination you enter, only when you
start them.

## Network activity of WinNetworkManager itself

None by default. It happens only when you:
- run a diagnostic (ping, traceroute, TCP test, DNS query, HTTP/TLS probe, path MTU),
- turn on *Show host names of remote addresses* or *Resolve names* (reverse DNS queries to your DNS server),
- tick *Check revocation* in the HTTP/TLS probe (contacts the certificate authority's CRL/OCSP servers).

The HTTP probe identifies itself as `WinNetworkManager/<version> (connection diagnostics)` and sends no cookies or
credentials.

## Stored data

| Data | Where | Contents | Control |
|------|-------|----------|---------|
| Settings | `settings.json` | Preferences and column layouts | Reset to defaults |
| History (optional, on by default) | `history.db` (SQLite) | Per second (24 h) and per minute (retention, 30 days by default): bytes per adapter total, per application (executable path and name), per adapter. **No** remote addresses, host names, DNS queries, ports or packet contents | Settings → History: turn off, change retention, Clear history |
| Logs | `logs\wnm-YYYYMMDD.jsonl` | Events and errors of WinNetworkManager itself (no traffic data) | Settings → Log file: level, retention, off |

Locations are listed in [INSTALL.md](INSTALL.md#data-locations). The DNS cache, command lines and diagnostic results are
shown but never stored. Nothing is sent anywhere.

## Signing

Release packages should be signed with an Authenticode certificate (see [DEVELOPMENT.md](DEVELOPMENT.md#signing)).
Unsigned builds work but Windows SmartScreen may warn on first run, and the administrator prompt shows "Unknown
publisher" for the helper.
