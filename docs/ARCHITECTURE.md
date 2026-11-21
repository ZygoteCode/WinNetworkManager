# WinNetworkManager v2 — Architecture

Status: approved design for the v2 rebuild (2026-11-21). Inputs: `docs/AUDIT.md` (v1 findings) and
`docs/research/ETW-KERNEL-NETWORK.md` (empirical ETW facts).

## 1. Goals

In priority order: **data accuracy → stability → architecture → user experience → real time → diagnostics →
efficiency → compatibility.** A metric that cannot be measured reliably is shown as *unavailable* or *partial*, never
as zero and never estimated silently.

Non-goals: packet capture/payload inspection, traffic shaping or blocking, kernel drivers, cloud services, telemetry
sent anywhere, scanning networks the user does not control.

## 2. Technology decisions

### 2.1 Runtime: .NET 10 (LTS), C# 14, `net10.0-windows`
.NET 10 is the current LTS (support until Nov 2028). .NET 11 (installed on the dev machine) is STS and is not used.
`global.json` pins the 10.0 SDK feature band (`rollForward: latestFeature`).

### 2.2 UI framework: WPF + an original design system

| Criterion | WinUI 3 (WinAppSDK) | **WPF (.NET 10)** | Avalonia |
|-----------|--------------------|-------------------|----------|
| Native look | Best (Fluent controls, Mica) | Very good with custom templates + DWM Mica/dark title bar | Good, non-native |
| Dense data tables (thousands of rows, sort, resize/reorder/hide columns, keyboard, UIA grid pattern) | **No first-party DataGrid**; community DataGrid unmaintained | **Mature virtualized `DataGrid`** with full UIA Table/Grid patterns | DataGrid/TreeDataGrid, weaker UIA on Windows |
| Accessibility (UIA, high contrast, keyboard) | Very good | Very good, decades of tooling | Improving |
| Per-monitor DPI v2 | Yes | Yes (manifest) | Yes |
| Deployment | Requires WinAppSDK runtime or self-contained (+~40 MB), packaging quirks | Part of .NET Desktop runtime | Bundled |
| Elevation / native interop | Fine | Fine | Fine |
| Build/test tooling (CLI, CI, UI automation) | `dotnet build` works but XAML compiler/packaging quirks | Plain `dotnet build`, FlaUI | Plain |

Decision: **WPF**. The product is a data-dense monitoring tool; the decisive factor is a robust, virtualized,
accessible data grid and charts that update every second. WPF's built-in Fluent theme (`ThemeMode`) is still marked
experimental (`WPF0001`) in .NET 10, so the app ships **its own design system** (tokens, templates, focus visuals,
light/dark/high-contrast) — which is also what the product needs to have an identity of its own. The immersive dark
title bar is applied through the documented DWM attribute; the window uses solid theme backgrounds (no Mica backdrop
in this release).

### 2.3 Capture: raw ETW consumer, not TraceEvent
TraceEvent (used by v1) was evaluated and rejected for the capture path:
* the events needed are manifest-based (Kernel-Network, Kernel-Process); TraceEvent would parse them through its
  dynamic TDH parser, which allocates per event and exposes addresses as `IPAddress` objects;
* the needed surface (`StartTrace`, `EnableTraceEx2`, `OpenTrace`, `ProcessTrace`, `ControlTrace`,
  `TdhGetEventInformation`) is small and fully documented;
* it removes a large dependency with native support binaries.

The custom consumer resolves field offsets through TDH once per event version (robust to layout changes), parses
payloads with zero allocations, uses `[UnmanagedCallersOnly]` callbacks and `LibraryImport` source-generated
P/Invoke.

### 2.4 Other platform APIs (all documented)
| Need | API |
|------|-----|
| TCP/UDP endpoints with owner PID, creation time, owner module (service) | `GetExtendedTcpTable` / `GetExtendedUdpTable` (`*_OWNER_MODULE_ALL`), IPv4 + IPv6 |
| Owning service of an endpoint | `GetOwnerModuleFromTcpEntry` / `...Udp...` (lazy) and `EnumServicesStatusEx` |
| Interface counters | `GetIfTable2` (64-bit `InOctets/OutOctets`, discards, errors) |
| Interface configuration (addresses, gateways, DNS, DHCP, MTU, speed) | `GetAdaptersAddresses` |
| Address → interface mapping | `GetUnicastIpAddressTable` |
| Routes, best route | `GetIpForwardTable2`, `GetBestRoute2` |
| Connectivity | Network List Manager (`INetworkListManager::GetConnectivity`) |
| Process enumeration, CPU, memory | `CreateToolhelp32Snapshot`, `WTSEnumerateProcessesEx`, `GetProcessTimes`, `QueryFullProcessImageName`, `K32GetProcessMemoryInfo` |
| Signature / publisher | `WinVerifyTrust` (embedded + catalog, no network revocation), `CryptCAT*` |
| Command line | WMI `Win32_Process` (on demand) |
| DNS client cache | WMI `MSFT_DNSClientCache` (`root/StandardCimv2`, on demand) |
| Ping / traceroute | `System.Net.NetworkInformation.Ping` (ICMP API, no elevation) |

WFP was evaluated: its net events/flow statistics are aimed at filtering diagnostics, require elevation, and do not
give per-process byte counts more reliably than Kernel-Network; it is not used. No kernel driver is used.

### 2.5 Libraries
| Package | Why |
|---------|-----|
| `CommunityToolkit.Mvvm` | Source-generated observable properties/commands, no runtime reflection. |
| `Microsoft.Extensions.Hosting` (+ Logging, Options, DI) | Composition root, lifetimes, graceful shutdown, structured logging abstractions. |
| `Microsoft.Data.Sqlite` | Local history store (WAL, batched writes). |
| `System.Management` | WMI queries used on demand (command line, DNS cache). |
| Tests: `xunit.v3`, `Microsoft.NET.Test.Sdk` | Unit, integration, reliability and performance tests. |

Everything else (charts, file logger, CSV/JSON export, DNS wire protocol, IPC protocol) is implemented in-house
because each is small and must be tightly controlled.

## 3. Process and privilege model

```
                ┌───────────────────────────────────────────────┐
 user (medium   │ WinNetworkManager.exe  (asInvoker, WPF)        │
 integrity)     │  • connections, interfaces, processes, routes  │
                │  • diagnostics, history, export, settings      │
                │  • Engine (accounting, rates, history)         │
                └───────────────┬───────────────────────────────┘
                                │ named pipe (per-launch random name, ACL = launching user SID,
                                │ server PID and client PID verified both ways)
                ┌───────────────┴───────────────────────────────┐
 elevated, on   │ WinNetworkManager.Collector.exe               │
 user request   │  • ETW session "WinNetworkManager-Collector-N" │
 (UAC prompt)   │  • Kernel-Network + Kernel-Process             │
                │  • decodes + buckets, sends TelemetryFrames    │
                │  • exits when the UI exits or disconnects      │
                └───────────────────────────────────────────────┘
```

* The UI **never requires elevation**. Without the collector every non-privileged feature works and per-process byte
  columns show "Requires collector" (not zero).
* When the UI itself is already elevated (user chose "Run as administrator"), or the user is in *Performance Log
  Users*, the same collector runs **in-process** (no helper, no IPC).
* The helper is minimal (no UI, no network access, no file writes except optional logs), accepts exactly one client
  (verified PID), and cannot be instructed to do anything except stream telemetry.
* The session name is fixed per Windows logon session (`WinNetworkManager-Collector-<session id>`): a new collector
  stops a stale session left by a crashed one (v1 leaked one per run, and real-time sessions outlive their process),
  while users signed in at the same time keep separate sessions instead of stopping each other's.
* The privilege state is always visible in the status bar and on the Dashboard, with an explanation of what each
  missing privilege disables.

## 4. Solution layout

```
WinNetworkManager.slnx
├─ src/
│  ├─ WinNetworkManager.Core              net10.0         Domain model, units, rate math, time series, IP
│  │                                                       classification, filters, settings schema. No Win32.
│  ├─ WinNetworkManager.Platform.Windows  net10.0-windows All P/Invoke & COM interop: IP Helper, ETW primitives,
│  │                                                       TDH, processes, services, signatures, NLM, DWM.
│  ├─ WinNetworkManager.Telemetry         net10.0-windows ETW network collector: decoder, process tracker,
│  │                                                       bucketer, frames, in-proc host, pipe protocol/server/client.
│  ├─ WinNetworkManager.Engine            net10.0-windows Monitoring engine: process catalog, connection tracker,
│  │                                                       interface monitor, traffic accounting, snapshots, history.
│  ├─ WinNetworkManager.Diagnostics       net10.0-windows Ping, traceroute, TCP connect, DNS (system + direct wire
│  │                                                       queries), HTTP/TLS probe, DNS cache, routes.
│  ├─ WinNetworkManager.Infrastructure    net10.0-windows Settings store, SQLite history, JSON-lines logger, export.
│  ├─ WinNetworkManager.UI                net10.0-windows Design system: tokens, themes, control templates, charts,
│  │                                                       converters, icons (WPF class library).
│  ├─ WinNetworkManager.App               net10.0-windows WPF app: shell, pages, view models, composition root.
│  └─ WinNetworkManager.Collector         net10.0-windows Elevated helper host (no UI).
├─ tests/  Core / Platform / Telemetry / Engine / Diagnostics / Infrastructure / App tests (+ perf suites)
├─ installer/  WiX 5 MSI project (per-machine, major upgrades, uninstall cleanup)
├─ build/  publish.ps1 (tests, self-contained publish, portable ZIP, MSI, signing, checksums)
└─ docs/
```

Dependency rule (enforced by project references): `Core` ← `Platform.Windows` ← `Telemetry` ← `Engine` ←
`App`; `Diagnostics` and `Infrastructure` depend on `Core` (+`Platform.Windows` where needed); `UI` depends only
on `Core`. The engine never references WPF; the UI never calls Win32 capture APIs.

## 5. Data flow and threading

```
 Collector (consumer thread, 1)                Engine (sampler thread, 1)              UI (dispatcher)
 ProcessTrace ─► decode (no alloc) ─►          TelemetryFrame queue (bounded) ─►       latest-snapshot mailbox
 bucket[250 ms by event QPC time]              ProcessCatalog ◄─ process polls (2 s)   (drop stale, never queue)
 finalize when now > end + 1 s ─► frame ─────► ConnectionTracker ◄─ table polls (1 s) ─► reconcile row VMs in place
                                               InterfaceMonitor ◄─ GetIfTable2 (1 s)      (only changed values notify)
                                               TrafficAccountant ─► EngineSnapshot ─────►
                                               HistoryRecorder ─► SQLite writer (batched, background)
```

* Exactly one consumer thread (ETW), one sampler loop (`PeriodicTimer`, monotonic `Stopwatch`), one history writer;
  IP Helper polls run on the sampler loop; diagnostics run on the thread pool with cancellation. No thread per
  process/connection, no `Task.Run` storms.
* Frames are pushed through a bounded `Channel` (drop-oldest with a counter → visible "frames dropped").
* The UI receives immutable snapshots through a single-slot mailbox: if rendering is slower than sampling,
  intermediate snapshots are skipped (backpressure) rather than queued.
* All snapshots are immutable records; no locks are shared with the UI.

## 6. Accuracy model

### 6.1 Process identity
`ProcessKey = (PID, CreateTime)` (FILETIME, 100 ns). Sources, in order of authority:
1. Kernel-Process start/stop events (collector): exact lifetime, `ProcessSequenceNumber`, image path.
2. Snapshot polling (`Toolhelp32` + `GetProcessTimes`): detects processes when no collector is available.
   A PID whose CreateTime changes is a new instance; if CreateTime is unreadable (access denied) the key falls back
   to (PID, first-seen) plus image name, and the record is flagged `IdentityConfidence.Partial`.
Network events are attributed to the instance that owned the PID *at the event's timestamp* (the tracker keeps the
previous instance per PID to resolve out-of-order delivery across CPUs).

### 6.2 Byte accounting (collector)
| Counter | Source events | Meaning |
|---------|---------------|---------|
| TCP download / upload | 11+27 / 10+26 | TCP payload delivered to / sent by the socket owner |
| UDP download / upload | 43+59 / 42+58 | UDP payload |
| Retransmitted | 14+30 | Shown separately (network-quality signal), not part of totals |
| Loopback | any of the above with loopback remote | Shown separately; excluded from "network" totals by default |

### 6.3 Rates
* Bucketed by **event timestamp** on the QPC timeline (250 ms). A bucket is final once `now > end + 1 s`; late
  events are added to the oldest open bucket and counted (`LateEvents`).
* *Current rate* = bytes in the last *W* seconds of final buckets / *W* (W = refresh interval, ≥ 250 ms).
* *Peak* = maximum 1-second rate observed; *Average* = total / observed duration.
* Interface rates = Δcounter / Δ`Stopwatch` time between actual samples. A counter that goes backwards (reset,
  driver reload, adapter re-enable) produces **no** rate for that interval and a "counter reset" flag — never a
  negative or wrapped value. Intervals longer than 3× nominal (suspend/resume, stalls) are recorded as gaps.
* Lost ETW events (`EventsLost`, `RealTimeBuffersLost` deltas, queried every second) mark the affected interval as
  *partial* in the UI and in exports.

### 6.4 Three distinct traffic totals (never summed together)
1. **Interface traffic** — `GetIfTable2` octets: all protocols, including headers. Physical vs virtual adapters
   are distinguished; loopback pseudo-interface excluded.
2. **Attributed process traffic** — ETW TCP/UDP payload, external only (loopback separate).
3. **Unattributed** — shown as the difference *interface − attributed*, explained as headers + non-TCP/UDP
   protocols + kernel traffic, and only when the collector is running.

### 6.5 Measured vs derived vs unavailable
| Metric | Class |
|--------|-------|
| TCP/UDP payload bytes per process, per flow | Measured (ETW) |
| Interface octets/packets/errors | Measured (IF-MIB counters) |
| Rates, averages, peaks | Derived from measured counters with stated window |
| Connection state, owner PID, creation time | Measured (IP Helper snapshot, polling granularity) |
| Connection duration | Derived from creation timestamp; "≥ observed" when creation time is 0 |
| Per-interface attribution of a process | Derived from local address → interface mapping (unknown for wildcard binds) |
| UDP remote peers | Measured only while the collector runs (observed datagrams) |
| Bytes before the app started | **Unavailable** (Windows keeps no per-process history) |
| Per-process traffic without collector | **Unavailable** (shown as such) |

## 7. Engine components

* **ProcessCatalog** — instances keyed by `ProcessKey`; lazy, cached enrichment (path, version info, publisher &
  signature, icon, services, command line) with explicit `AccessDenied`/`NotAvailable` states. Exited instances
  stay visible for a configurable time, then move to history only.
* **ConnectionTracker** — TCP4/TCP6/UDP4/UDP6 tables into reusable native buffers (grow-and-retry on
  `ERROR_INSUFFICIENT_BUFFER`), diffed by key → first seen / closed events; joins collector flows for per-connection
  bytes; UDP sockets and observed UDP peers are separate entities.
* **InterfaceMonitor** — counters every tick; configuration refreshed on `NotifyIpInterfaceChange` /
  address-change notifications; default route interface via best route.
* **TrafficAccountant** — per-instance and global accumulators, sliding windows, per-protocol and per-interface
  splits, loopback split.
* **HistoryRecorder** — in-memory tiers (1 s × 15 min system + active processes) and SQLite tiers
  (system 1 s × 24 h; system/app/interface 1 min × retention). Per-app history is keyed by executable path, not PID.

## 8. Diagnostics

All operations are async, cancellable, have per-operation timeouts, stream progress, never share threads with the
engine and only target destinations the user types. No port ranges, no subnet sweeps.
* DNS: system resolver timing; an own RFC 1035 client for direct queries to a chosen server (UDP with EDNS(0), TCP
  fallback on truncation; A, AAAA, CNAME, MX, TXT, NS, SOA, SRV, CAA, PTR; strict bounds-checked decoder; answers with
  a wrong ID or question are ignored) with RCODE diagnosis; configured servers per interface; client cache (WMI
  `MSFT_DNSClientCache`, read on request only).
* TCP/IP: ping statistics (loss, min/avg/max, jitter), traceroute (ICMP TTL, self-timed hops), TCP connect test,
  IPv4 path MTU (DF echo + binary search), routing table and best-route lookup (no packets sent).
* HTTP/HTTPS: one connection measured stage by stage — DNS / TCP connect / TLS handshake / server response / total;
  status and headers (HTTP/1.1, body not downloaded); TLS version, cipher suite, ALPN; certificate chain, validity,
  SANs, key, SHA-256 fingerprint and chain problems. Certificate validation is never bypassed: an invalid certificate
  is reported and the request stops.
* Destinations are validated: one host or address only (ranges, subnets, lists and wildcards are rejected).

## 9. Persistence, settings, logging, export

* Settings: `%LOCALAPPDATA%\WinNetworkManager\settings.json`, versioned schema, validated and clamped on load,
  atomic writes, corrupted file preserved as `settings.json.corrupt-*` and defaults restored with a visible notice.
* History: `%LOCALAPPDATA%\WinNetworkManager\history.db` (SQLite WAL). Stores only aggregated byte counts per
  application/interface — **no remote addresses**. Persistence can be disabled and cleared from Settings.
* Logs: JSON lines, daily files, size-capped, 7 files retained, collector log records forwarded over the pipe.
* Export: CSV and JSON with header metadata (UTC timestamps ISO-8601, units, resolution, data source, partial
  flags, app version).

## 10. UI and design system

* Shell: custom title bar (WindowChrome, snap layouts, theme-aware caption), navigation rail (Dashboard, Processes,
  Connections, Interfaces, Diagnostics, History, Settings), status bar (privilege, events/s, data latency, refresh
  interval, pause), notification-area icon (live tooltip, menu, notifications), themed dialogs.
* Process Inspector: dedicated page (tiles, Network / Connections / Details tabs, 5-minute chart) reachable from every
  process reference; connections link back to their owner and vice versa.
* Design tokens: colour (light/dark + high-contrast mapped to `SystemColors`), typography scale (Segoe UI Variable,
  tabular numerals for metrics, monospace for addresses), 4-px spacing grid, radii, elevation, motion durations
  (disabled when the system requests reduced animation).
* Accessibility: full keyboard model and shortcuts, UIA names/help text, visible focus ring, no colour-only state
  (icons/glyphs + text), text scaling, per-monitor DPI v2.
* Language: the interface is English. Windows localizes system error messages, message-box buttons and built-in WPF
  menus, so error codes are translated by `ErrorText`, dialogs are the app's own, the UI culture is en-US and only
  English resources ship; numbers and dates follow the Windows regional format.
* Charts: in-house `TimeSeriesChart`/`Sparkline` rendered with `DrawingContext`/`StreamGeometry`, keyboard-navigable
  crosshair, automation peer exposing a textual summary.

## 11. Compatibility (target matrix)

| OS | Arch | Status |
|----|------|--------|
| Windows 11 24H2 / 25H2 | x64 | Supported, tested (dev machine 10.0.26200) |
| Windows 11 22H2 / 23H2 | x64 | Supported (same API surface), not tested in this cycle |
| Windows 10 22H2 | x64 | Supported (same API surface), not tested in this cycle |
| Windows 11 / 10 | ARM64 | Builds from the same sources (`publish.ps1 -Runtime win-arm64`); not built or tested in this cycle |

Every API listed in §2.4 exists since Windows 8 or earlier; `EVENT_TRACE_USE_MS_FLUSH_TIMER` falls back to a 1 s
timer if rejected.

## 12. v1 → v2 feature mapping

| v1 | v2 |
|----|----|
| Per-process TCP/UDP up/down rate (IPv4, admin) | Processes page: TCP/UDP × up/down rates, totals, peak, average, IPv4+IPv6, loopback split, collector-aware states |
| Grouping by process name | Group by application (executable) with aggregate rows |
| Search by name | Instant search over name/PID/path/publisher + structured filters |
| TCP addresses of selected process | Connection Explorer + Inspector connections tab (states, IPv6, owner, durations, bytes) |
| "UDP addresses" | UDP endpoints (local) + observed UDP peers (remote) as distinct models |
| Dark theme | Light/dark/system themes, accent colours, high contrast |

## 13. Roadmap

| Phase | Content | Exit criteria |
|-------|---------|---------------|
| A Audit | `docs/AUDIT.md`, ETW research | done |
| B Design | this document | done |
| C Foundation | solution, Core, Platform interop, Telemetry collector, settings, logging | unit + ETW integration tests green |
| D Core features | Engine, design system, shell, Dashboard, Processes, Connections, Interfaces, Inspector | app runs on live data, VM tests green |
| E Advanced | Diagnostics, history (SQLite), charts, export, settings UI | feature tests green |
| F Hardening | reliability suite (PID reuse, crashes, lost events, permissions, API errors), perf suite | budgets met |
| G Release | elevated helper + IPC hardening, MSI (WiX), portable zip, docs | clean install/uninstall verified |

## 14. Status (v2.0.0)

All phases are implemented. 325 automated tests pass on Windows 11 build 26200 (elevated run, so the kernel event
tracing tests and the helper end-to-end test are included). The MSI and portable ZIP for x64 are produced by
`build\publish.ps1`; the MSI payload was verified by administrative extraction and the published Release build was
started and checked. Not yet done: an install/upgrade/uninstall pass of the MSI on a clean machine, code signing (no
certificate available), ARM64 builds, Windows 10 testing.

Known limits (by design or by Windows): see [DATA-SOURCES.md](DATA-SOURCES.md#what-windows-cannot-provide-and-what-is-shown-instead).
