# Development

## Requirements

- Windows 10/11 x64, .NET 10 SDK (pinned in `global.json`, latest feature band allowed).
- Any editor; the solution is `WinNetworkManager.slnx`.
- For signing: Windows SDK (`signtool`). For the MSI: nothing extra — the WiX Toolset 5 SDK is restored from NuGet.

## Layout

| Project | Content |
|---------|---------|
| `src/WinNetworkManager.Core` | Platform-independent model: addresses, units, rates, time series, filters, settings schema, English error texts |
| `src/WinNetworkManager.Platform.Windows` | All Win32/COM interop: ETW, TDH, IP Helper, processes, signatures, services, shell, notification icon |
| `src/WinNetworkManager.Telemetry` | ETW network collector: decoders, process tracking, bucketing, pipe protocol, helper host/client |
| `src/WinNetworkManager.Engine` | Monitoring engine: process catalog, connections, interfaces, accounting, snapshots, history recorder |
| `src/WinNetworkManager.Diagnostics` | Ping, traceroute, TCP test, path MTU, DNS (system and wire-format client), DNS cache, HTTP/TLS |
| `src/WinNetworkManager.Infrastructure` | Settings store, SQLite history, JSON-lines logger, CSV/JSON export, data paths |
| `src/WinNetworkManager.UI` | Design system: themes, tokens, control templates, charts, converters |
| `src/WinNetworkManager.App` | WPF application: shell, pages, view models, services, composition root |
| `src/WinNetworkManager.Collector` | Elevated helper executable |
| `installer/` | WiX 5 MSI project |
| `build/publish.ps1` | Publish, package, sign, checksums |

Design and rationale: [ARCHITECTURE.md](ARCHITECTURE.md).

## Build and run

```powershell
dotnet build WinNetworkManager.slnx
$env:WNM_DATA_DIR = "$env:TEMP\wnm-dev"   # optional: keep development data apart
.\src\WinNetworkManager.App\bin\Debug\net10.0-windows\WinNetworkManager.exe
```

Warnings are errors (`TreatWarningsAsErrors`, latest .NET analyzers). Package versions are central
(`Directory.Packages.props`).

## Tests

```powershell
dotnet test --solution WinNetworkManager.slnx                                   # everything
dotnet test --project tests\WinNetworkManager.Telemetry.Tests\WinNetworkManager.Telemetry.Tests.csproj
dotnet test --solution WinNetworkManager.slnx --filter-trait "Category=Performance"
```

xUnit v3 on Microsoft.Testing.Platform. Suites:

| Project | Covers |
|---------|--------|
| Core.Tests | Addresses, classification, units and formatting, filters, rates, rings, counters, settings validation, error texts |
| Platform.Tests | Native structure layouts, IP Helper and process readers against the live system, ETW session control, notification icon |
| Telemetry.Tests | Decoders (Windows 11 manifests), PID reuse, bucketing, pipe protocol (+ fuzzing), **real ETW**: exact byte counts for TCP IPv4/IPv6 and UDP, flows, child-process attribution, collector restart, pipe host, the **real helper executable**; decoder and bucketer throughput/allocation budgets |
| Engine.Tests | Engine cycles with fake sources: attribution, connections, interfaces, notifications, history (exact bytes per second/minute, partial minute on exit, failing database), load test (500 processes, 5 000 connections) |
| Diagnostics.Tests | DNS codec (all record types, compression, malformed input fuzzing), DNS over UDP/TCP against local fake servers, ping/traceroute/path MTU on loopback, TCP test, HTTP probe incl. redirects and an untrusted TLS certificate, DNS cache |
| Infrastructure.Tests | Settings (defaults, corruption, validation), logger, history store, CSV/JSON export |
| App.Tests | View-model logic: row synchronisation, history averaging, interface filtering, formatting |

Tests that need kernel event tracing or start the helper are **skipped** unless the test host is elevated: run the
suite from an elevated terminal before a release. They create tracing sessions named `WinNetworkManager-Test-*` and
the helper test uses the normal session name, so close WinNetworkManager while running them.

## Packaging

```powershell
.\build\publish.ps1                                  # tests, x64 publish, portable ZIP, MSI, SHA256SUMS
.\build\publish.ps1 -Runtime win-x64, win-arm64 -SkipTests
```

Output in `artifacts\`: `publish\<rid>\` (self-contained, ReadyToRun, no symbols), the portable ZIP, the MSI and
`SHA256SUMS.txt`. The version comes from `VersionPrefix` in `Directory.Build.props` (override with `-Version x.y.z`).
Only English resources are shipped (`SatelliteResourceLanguages=en`).

### Signing

```powershell
.\build\publish.ps1 -CertificateThumbprint <SHA-1 thumbprint of a code-signing certificate in your store>
```

Signs `WinNetworkManager.exe`, `WinNetworkManager.Collector.exe` and the MSI with SHA-256 and an RFC 3161 timestamp
(`-TimestampUrl`, DigiCert by default). Without a certificate, packages are built unsigned.

### Release checklist

1. Update `VersionPrefix` in `Directory.Build.props`.
2. Run `dotnet test --solution WinNetworkManager.slnx` from an elevated terminal: all tests pass, none skipped.
3. Run `.\build\publish.ps1` with the signing certificate.
4. On a clean machine: install the MSI, start per-process monitoring (prompt shows the publisher), check every page,
   upgrade from the previous version, uninstall, and verify Program Files, the Start menu and the Run key are clean.
5. Publish the MSI, the portable ZIP and `SHA256SUMS.txt`.

## Code rules

- Nothing is invented: unknown values are `null`/`NaN`/`—`, never zero; every figure has a documented source.
- No blocking calls on the UI thread; no `async void` except UI event handlers; native resources in safe handles or
  `using`; every Win32 failure carries the API name and code.
- All user-visible text is English. Exception messages from Windows are localized by the OS, so show
  `ErrorText.Describe(exception)` instead of `exception.Message`; mark your own English exceptions with
  `IUserFacingMessage`; use `Shell.Dialogs` instead of `MessageBox`.
- Numbers and dates are formatted with the current (regional) culture in the UI and with the invariant culture in
  exports and logs.
- New native structures get a layout test; new parsers get malformed-input tests.
