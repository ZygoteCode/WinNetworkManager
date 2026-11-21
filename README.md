# WinNetworkManager

Per-process network monitoring and diagnostics for Windows 10 and 11.

WinNetworkManager shows which applications use the network, how much they transfer, which connections they hold,
what your adapters are doing and how the network behaves — with figures whose source and accuracy are stated, never
guessed. Version 2 is a complete rewrite of the original WinForms tool (see [docs/AUDIT.md](docs/AUDIT.md)).

## Highlights

- **Per-process traffic** from kernel event tracing (ETW `Microsoft-Windows-Kernel-Network`): TCP and UDP, download and
  upload, IPv4 and IPv6, loopback kept apart, exact byte counts, rates over the refresh window, peaks, averages,
  retransmissions. Processes are identified by PID **and** start time, so a reused PID never inherits old traffic.
- **Connections**: every TCP connection and listener and every UDP endpoint (IPv4/IPv6) with owner, state, age,
  per-connection traffic, observed UDP peers, optional reverse DNS, structured filters.
- **Interfaces**: link-layer counters, rates, addresses, gateways, DNS, DHCP, MTU, errors; the adapter that carries the
  default route is marked.
- **Process details**: path, version information, publisher and Authenticode signature, user, session, parent,
  services, command line, CPU and memory, network figures, sockets and a 5-minute chart.
- **Diagnostics**: ping (loss, min/avg/max, jitter), traceroute, TCP port test, DNS (Windows resolver or direct
  queries to any server, all common record types, TCP fallback), HTTP/TLS probe (DNS / connect / TLS / server timings,
  TLS version, cipher, certificate chain and validity), path MTU, routing table and route lookup, DNS client cache.
- **History**: 60 seconds to 30 days and custom ranges; adapters vs applications, TCP vs UDP, per application, per
  adapter; intervals that were not measured are gaps, never zeros.
- **Export** to CSV and JSON with raw values, UTC timestamps and a header that documents sources and units.
- Light, dark and high-contrast themes, keyboard navigation, screen-reader names, text scaling, per-monitor DPI.

## Requirements

- Windows 11, or Windows 10 22H2, 64-bit (x64). See the [compatibility matrix](docs/INSTALL.md#compatibility).
- The installer and the portable ZIP are self-contained: no .NET installation is needed.
- Per-process traffic needs administrator rights once per session (see below). Everything else works without them.

## Install

| Package | Use it when |
|---------|-------------|
| `WinNetworkManager-<version>-win-x64.msi` | Normal installation in Program Files with a Start menu entry and clean uninstall. |
| `WinNetworkManager-<version>-win-x64-portable.zip` | Run from any folder (USB drive, no installation). Settings and history stay in a `Data` folder next to the program. |

Details, upgrades and complete removal: [docs/INSTALL.md](docs/INSTALL.md).

## Permissions in one paragraph

WinNetworkManager itself always runs **without** administrator rights. Windows allows kernel event tracing only to
administrators, so per-process traffic is collected by a small helper, `WinNetworkManager.Collector.exe`, which starts
after one administrator prompt when you click **Turn on per-process monitoring** and stops when WinNetworkManager
exits. It has no window, accepts only its parent process on a private pipe and can do nothing but stream measurements.
If you start WinNetworkManager as administrator, the collector runs inside it and no prompt appears.
More in [docs/PERMISSIONS.md](docs/PERMISSIONS.md).

## Privacy

WinNetworkManager never connects to the Internet by itself: no telemetry, no update checks, no online lookups. Network
activity happens only for diagnostics you start, reverse DNS lookups you turn on, and certificate revocation checks you
ask for. The optional history database stores byte counts per interval, per application and per adapter — never remote
addresses, host names, DNS queries or packet contents — and can be turned off or cleared in Settings.

## Documentation

| Document | Contents |
|----------|----------|
| [User guide](docs/USER-GUIDE.md) | Every page, filters, notifications, export formats, settings reference, shortcuts |
| [Data sources and limits](docs/DATA-SOURCES.md) | Where each figure comes from, how it is computed, what Windows cannot provide |
| [Permissions, security and privacy](docs/PERMISSIONS.md) | Privilege model, helper process, pipe security, stored data |
| [Installation](docs/INSTALL.md) | MSI, portable mode, upgrades, uninstall, data locations, compatibility matrix |
| [Troubleshooting](docs/TROUBLESHOOTING.md) | Common problems and how to diagnose them |
| [Development](docs/DEVELOPMENT.md) | Building, tests, packaging, signing, release checklist, code rules |
| [Architecture](docs/ARCHITECTURE.md) | Design decisions, process model, data flow, accuracy model |
| [Audit of version 1](docs/AUDIT.md) | Defects of the original tool that motivated the rewrite |

## Building from source

```powershell
dotnet build WinNetworkManager.slnx
dotnet test --solution WinNetworkManager.slnx
.\build\publish.ps1
```

Requires the .NET 10 SDK. Run the tests from an elevated terminal to include the kernel event tracing integration tests
(they are skipped otherwise). See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).
