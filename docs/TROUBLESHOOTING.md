# Troubleshooting

Start with the log: Settings → Log file → **Open log folder** (`wnm-YYYYMMDD.jsonl`, one JSON object per line). For a
detailed log set *Detail* to *Debug*, reproduce the problem, then set it back.

## Per-process traffic shows "Requires per-process monitoring" or —

Per-process figures need kernel event tracing, which Windows reserves for administrators. Click **Turn on per-process
monitoring** (Dashboard, Settings or the notification-area menu) and approve the prompt, or start WinNetworkManager as
administrator. If the button is missing, the helper `WinNetworkManager.Collector.exe` is not next to
`WinNetworkManager.exe`: reinstall or re-extract the package.

## Per-process monitoring fails to start or keeps retrying

- The title bar chip and Settings show the reason. *Access denied* means the account lacks administrator rights.
- *Insufficient system resources*: Windows allows at most 64 event tracing sessions; other tools may be using them.
  List them with `logman query -ets` in an elevated prompt.
- A tracing session left by a crashed instance is replaced automatically at the next start. It can also be stopped
  manually: `logman stop WinNetworkManager-Collector-1 -ets` (elevated).
- Security software may block the helper; allow `WinNetworkManager.Collector.exe`.

## "Partial data" or lost events

Under extreme load ETW buffers can overflow. Affected intervals are marked partial and their values are lower bounds.
A longer refresh interval does not change collection; closing other heavy tracing tools does.

## Figures do not match Task Manager or the router

They measure different things (see [DATA-SOURCES.md](DATA-SOURCES.md)): adapters count headers and every protocol,
applications count TCP/UDP payload, loopback is separate, decimal vs binary units differ, and rates depend on the
averaging window. Task Manager's per-process "Network" column also uses ETW but a different window.

## A process shows "Access denied" for path, user or command line

Windows protects some processes (other users, system services, protected processes). Start WinNetworkManager as
administrator to read more; protected processes stay partially unreadable by design.

## Interfaces list looks incomplete

By default only connected interfaces that carry IP traffic are shown. Use **Show all interfaces** for disconnected
adapters, loopback and hidden virtual interfaces (WAN miniports, virtual switch ports). NDIS filter layers are never
listed: they repeat the counters of their adapter.

## History has no per-application data yet

Per-application and per-interface history is stored per minute, a few seconds after each minute ends (the minute in
progress is saved on exit). If *Keep traffic history on this PC* is off, only the in-memory window (15 minutes by
default) exists. "The history database could not be opened" in History or Settings means `history.db` is locked or
the folder is not writable; the reason is shown and logged.

## DNS query "is not accepting DNS queries" / "No answer"

The server did not reply on UDP port 53 (or refused TCP): it may not be a DNS server, a firewall may block DNS to other
servers than the configured one, or the network drops the traffic. Try the *Windows resolver* mode to compare.

## HTTP/TLS probe fails at the TLS stage

The certificate panel shows what Windows found: name mismatch, untrusted root (missing intermediate or private CA),
expiry. The request is not continued with an invalid certificate. Corporate TLS inspection replaces certificates with
those of the inspection proxy.

## Notifications do not appear

Check Settings → Notifications, Windows *Notifications* settings for WinNetworkManager, and Focus / Do not disturb
(quiet hours are respected).

## The window does not open when started again

Only one instance runs per user session: starting it again brings the existing window to the front (it may be hidden
in the notification area — check the overflow area of the taskbar).

## Reporting a problem

Include the Windows version (Settings → About shows it), the log file of the day, what you did and what you expected.
Logs contain no traffic data; review them before sharing.
