# User guide

## The window

- **Navigation rail** (left): Dashboard (Ctrl+1), Processes (Ctrl+2), Connections (Ctrl+3), Interfaces (Ctrl+4),
  Diagnostics (Ctrl+5), History (Ctrl+6), Settings (Ctrl+,). The ☰ button collapses the rail to icons; Alt+Left goes
  back (for example from a process's details).
- **Title bar chip**: state of per-process monitoring (off, starting, active, retrying, failed).
- **Status bar**: privilege level, ETW events per second, how far traffic data lags behind real time (about 1.2 s:
  events are grouped in 250 ms intervals and an interval is final one second after it ends), the refresh interval and
  **Pause** (Ctrl+P), which freezes the display while monitoring continues.

## Dashboard

Download and upload of the physical adapters with 60-second sparklines, traffic attributed to applications, active
applications, connection counts, a 60-second throughput chart (adapters, and optionally applications), the busiest
applications right now, the connection used for the Internet (interface, adapter, addresses, gateway, DNS servers)
and "where the bytes are counted": physical adapters, attributed to applications, TCP/UDP split, loopback and the
part that cannot be attributed. These figures come from different sources and are never added together — see
[Data sources](DATA-SOURCES.md).

## Processes

One row per running (or recently exited) process: download and upload rates, a 60-second sparkline, totals since
monitoring started, sockets, publisher and status. Exited processes stay listed for the time set in Settings.

- **Group by app** aggregates the processes of the same executable; **Hide system** hides kernel pseudo-processes and
  system accounts; **Show idle** toggles processes that never used the network.
- Double-click or Enter opens the process details; the context menu copies name, PID or path, opens the file
  location, shows the connections of the process or hides it.
- **End process…** (context menu or Del) closes the selected process — or every running instance of a selected group —
  after confirmation, like Task Manager's *End task*: unsaved work in it is lost. The instance is identified by PID and
  creation time, so a PID that Windows reused for another program is never ended. Critical Windows processes are
  refused (ending them crashes Windows). Without administrator rights only your own, non-elevated processes can be
  ended; for others Windows denies access and WinNetworkManager says so.
- Right-click a column header to show or hide columns; widths, order and sorting are remembered.

### Search and filters

Plain text matches name, path, publisher and PID. Filters can be combined; prefix one with `-` to exclude:

| Filter | Example | Meaning |
|--------|---------|---------|
| `name:` `path:` `publisher:` `user:` `state:` | `publisher:microsoft` | Text contains |
| `pid=` | `pid=4` | Exact process id |
| `system:yes` / `system:no` | `-system:yes` | System processes |
| `rate` `down` `up` `total` with `>` `>=` `<` `<=` | `rate>1MB`, `total>=100MB` | Byte values; units kB, MB, GB, KiB, MiB… |
| `conn` with comparisons | `conn>5` | Number of sockets |

Quotes allow spaces: `path:"Program Files"`.

## Process details

Opened from any process reference (Processes, Connections, Dashboard, notifications). Header: icon, name, PID,
state, path, signature, SHA-256 of the executable (computed locally when the page opens; **Verify…** opens it in
Diagnostics › File hash to compare with a published checksum). Tiles: download, upload, CPU, memory, handles. Tabs:

- **Network**: current rates and totals split TCP/UDP, peaks, averages, monitored time, loopback, retransmissions,
  sockets, remote addresses, interfaces used, last activity, and a 5-minute chart (download/upload or TCP/UDP).
- **Connections**: the process's TCP connections and UDP endpoints.
- **Details**: identity (PID and creation time), parent, path, description, product, company, file version,
  signature and publisher, user, session, start time, state, services hosted, and the command line (read through WMI
  when you click **Load**; processes of other users need administrator rights).

## Connections

**TCP**: every connection and listener with process, local and remote address and port, remote host (optional),
scope (Loopback, Private, Public, Link-local, …), state, age, current rates, totals, retransmissions and service.
**UDP**: bound endpoints with owner, traffic and observed peers (UDP has no connections: the remote endpoints are the
peers seen in datagrams while per-process monitoring runs).

Toggles: **Listening**, **Closed** (TIME_WAIT and recently closed connections), **Loopback**, **Resolve names**
(reverse DNS in the background; your DNS server sees these queries). The context menu copies addresses, filters by
process, address or port, opens the process and starts **Ping**, **Traceroute**, **TCP test** or a **reverse DNS
lookup** of the remote address on the Diagnostics page.

Filters: `pid=`, `process:`, `ip:` (either side), `lip:`, `rip:`, `port=` (either side), `lport=`, `rport=`, `state:`,
`scope:public`, `host:`, `service:`; prefix with `-` to exclude.

## Interfaces

Connected interfaces that carry IP traffic. **Show all interfaces** adds disconnected adapters, loopback and the
virtual helper interfaces Windows hides (WAN miniports, virtual switch ports). NDIS filter layers are never listed:
their counters repeat those of the adapter they sit on. For the selected interface: receive/send rates and chart,
status, type, link speed, MAC address, MTU, addresses (with origin: DHCP, manual, link-local…), gateway, DNS servers
and suffix, DHCP server, traffic since monitoring started and error counters. **Copy details** copies everything as
text.

## Diagnostics

Every tool works on the destination you enter (host name, IPv4 or IPv6 address, or a URL — the host is taken from it),
runs only when you start it and can be stopped. Ranges, subnets and lists are rejected on purpose. The timeout and
defaults are in Settings → Diagnostics.

| Tool | What it does | Limits |
|------|--------------|--------|
| **Ping** | ICMP echo with count or until stopped, payload size, don't-fragment; replies, loss, min/avg/max, jitter, chart | Round trips have 1 ms resolution (Windows ICMP API); hosts that block ICMP look unreachable |
| **Traceroute** | ICMP with increasing TTL, 3 probes per hop, optional reverse DNS | Round trips are timed by WinNetworkManager (Windows reports none for "TTL expired"); routers that drop ICMP show `*` |
| **TCP port** | Full TCP handshake then immediate close, on one or all addresses of a name | Refused connections are retried by Windows for about a second, so no time is shown for them |
| **DNS** | *Windows resolver*: like applications (hosts file, cache, DNS, LLMNR/mDNS). *Query a server*: one recursive query over UDP with EDNS(0), TCP when truncated; A, AAAA, CNAME, MX, TXT, NS, SOA, SRV, CAA, PTR; response code, flags (AA, RA, AD), TTLs, all sections | The Windows resolver does not expose TTLs; an IP address is looked up in reverse |
| **HTTP / TLS** | HEAD or GET over one connection: DNS, TCP connect, TLS handshake, server response and total times; status and headers; TLS version, cipher suite, ALPN; certificate, alternative names, validity, key, SHA-256 fingerprint, chain and chain problems; optional redirects and online revocation check | HTTP/1.1 only; the body is not downloaded; an invalid certificate stops the request (validation is never bypassed) |
| **Path MTU** | Largest IPv4 packet that reaches the destination unfragmented (echo with don't-fragment, binary search) | IPv4 only (Windows has no don't-fragment option for ICMPv6 echo); needs ICMP replies |
| **Routes** | IPv4 and IPv6 routing table with metrics; **Find route** shows the interface, gateway and source address Windows would use for a destination | Computed from the table, no packets sent |
| **DNS cache** | The Windows DNS client cache (like `ipconfig /displaydns`), with filter | Read only when you click **Read cache**; never stored |
| **File hash** | SHA-256, SHA-512, SHA-1 and MD5 of any file in one read (type the path, **Browse…** or drop the file on the page); paste a published checksum to compare — the algorithm is recognised from its length and `sha256sum` output, `SHA256:` prefixes, upper case and separators are accepted | Computed locally, nothing is uploaded or looked up; SHA-1 and MD5 only detect accidental corruption, not tampering |

## History

Choose 60 s, 5 min, 15 min, 1 h, 24 h, 7 days, 30 days or a custom range (local time, for example
`2026-11-21 08:00`). Up to the in-memory window (15 minutes by default) data comes from memory at 1-second resolution;
longer ranges come from the history database: 1-second data for the last 24 hours, 1-minute data beyond. Charts with
many points are averaged (the summary says by how much).

- **Throughput**: adapters (link layer) and applications (TCP/UDP payload).
- **TCP vs UDP**: attributed traffic, stacked.
- **Applications in this range**: per-minute totals by executable; select one to chart it.
- **Per interface**: one physical adapter per minute.

Intervals when per-process monitoring was off are gaps, not zeros; intervals with lost events are flagged as lower
bounds. Per-application and per-interface history is written a few seconds after each minute ends; the minute in
progress is saved when WinNetworkManager exits. With **Keep traffic history on this PC** turned off, only the
in-memory window is available.

## Notification area and notifications

The notification-area icon shows live adapter rates in its tooltip. Click it to open WinNetworkManager; right-click
for Open, turn per-process monitoring on or off, pause the display, Exit. With **Close button → Keep monitoring in the
notification area**, closing the window hides it and monitoring continues.

Notifications (Settings → Notifications): per-process monitoring stopped unexpectedly, Internet connectivity lost or
restored, an application used the network for the first time in this session, an application exceeded a rate for a
given time. Windows quiet hours are respected. Clicking a notification about an application opens its details.

## Export

Processes, Connections and History export the rows shown (after filters) to CSV or JSON.

- Values are raw: bytes and bytes per second, not "1.5 MB". Timestamps are UTC, ISO 8601. Numbers use invariant
  formatting (decimal point). Unknown values are empty (CSV) or `null` (JSON), never zero.
- A header (`#` lines in CSV, a `metadata` object in JSON) records the application version, page, export time, data
  timestamp, units, rate window, whether loopback is included, collector state and source, lost events and the
  Windows APIs behind every column.
- Cells that start with `=`, `+`, `-` or `@` are prefixed with `'` so spreadsheet programs do not run them as formulas.
- The CSV separator (comma or semicolon) is set in Settings.

## Settings reference

All settings apply immediately and are saved to `settings.json` (see [INSTALL.md](INSTALL.md#data-locations)).

| Section | Setting | Default |
|---------|---------|---------|
| Per-process monitoring | Start per-process monitoring | Only when I turn it on (or *At startup*) |
| Monitoring | Refresh interval (also the rate window) | 1 s (0.25–5 s) |
| | Connection table update / Process list update | 1 s / 2 s |
| | Count loopback traffic in totals | Off |
| | Keep exited processes listed | 30 s |
| | Show host names of remote addresses | Off |
| Appearance | Theme · Accent colour · Units · Table rows · Animations · Text size | Windows setting · Windows accent · Bytes (decimal) · Comfortable · Windows setting · 100 % |
| Window and startup | Close button · Start when I sign in · Start in the notification area | Exit · Off · Off |
| Process list | Hide system processes · Show processes without network use · Group by application · Always hide these processes | Off · On · Off · none |
| History and privacy | Keep traffic history on this PC · Keep history for · Per-second history in memory | On · 30 days · 15 minutes |
| Notifications | Master switch and the four notification kinds; rate threshold and duration | On; failures and connectivity on; 10 MB/s for 10 s |
| Diagnostics and export | Timeout · Ping count · Ping interval · Traceroute hops · Follow redirects · CSV separator | 3000 ms · 4 · 1000 ms · 30 · Off · Comma |
| Log file | Write a log file · Detail · Daily files kept | On · Information · 7 |

**Reset to defaults** restores every setting (stored history and column layouts are kept; start with Windows is turned
off). Invalid values in a hand-edited `settings.json` are replaced by safe values and listed at the top of Settings.

Numbers and dates follow the regional format chosen in Windows; all text is English.

## Keyboard shortcuts

| Keys | Action |
|------|--------|
| Ctrl+1 … Ctrl+6, Ctrl+, | Pages, Settings |
| Alt+Left | Back |
| Ctrl+P | Pause or resume the display |
| Ctrl+F | Search (Processes, Connections) |
| Esc | Clear the search |
| Enter | Open the selected process |
| Del | End the selected process (asks for confirmation) |
| Ctrl+C | Copy the selected row |
| Shift+F10 / Menu key | Context menu (also on column headers) |
| F5 | Reload the history range |
| Left/Right, Home/End on a chart | Move the readout cursor |

## Command line

| Argument | Effect |
|----------|--------|
| `--minimized` | Start hidden in the notification area (used by start with Windows; needs *Start in the notification area* and *Keep monitoring in the notification area*) |
| `--cleanup` | Remove the start-with-Windows entry of the current user and exit (used by the uninstaller) |

Environment variable `WNM_DATA_DIR` overrides the data folder (useful for testing).
