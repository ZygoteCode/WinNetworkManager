# WinNetworkManager v1 — Technical Audit

Audit date: 2026-11-21 · Audited revision: `93fa0f4` ("First commit") · Auditor role: systems / .NET / network / UX / QA review.

This document records the state of the original WinForms application before the v2 rebuild, so that every
decision taken in the rebuild can be traced back to a concrete finding. File references point to the v1 sources as
they existed at that revision (they are still available in git history).

---

## 1. Inventory

| File | Lines | Purpose |
|------|------:|---------|
| `Program.cs` | 13 | WinForms entry point. |
| `MainForm.cs` | 301 | All application logic: ETW capture, rate math, process list, connection lookup. |
| `MainForm.Designer.cs` | 250 | Fixed-layout form built with Guna.UI2 and MetroSuite controls. |
| `MidnightListView.cs` | 178 | Owner-drawn dark `ListView`. |
| `NetworkConnections.cs` | 157 | P/Invoke wrappers for `GetExtendedTcpTable` / `GetExtendedUdpTable`. |
| `WinNetworkManager.csproj` | 196 | Legacy (non-SDK) project, .NET Framework 4.8.1, `packages.config`. |
| `packages.config` | 30 | 27 NuGet packages (most of them transitive or unused). |
| `App.config` | 38 | Binding redirects. |
| `FodyWeavers.xml/.xsd` | — | Costura.Fody (embeds dependencies into the exe). |
| `Network_23224.ico` | — | Single 128×128 icon of unknown provenance. |

There were no tests, no README, no documentation, no logging, no settings and no application manifest.

## 2. Current architecture

```
┌──────────────────────────── MainForm (UI thread) ────────────────────────────┐
│ WinForms Timer (1 s) ─► UpdateProcessList()                                  │
│   Process.GetProcesses() ─► for each process: moving average of 5 deltas     │
│   rebuild/refresh ListView items + groups                                    │
│ SelectedIndexChanged ─► NetworkConnections.GetTcp/UdpConnections() (IPv4)    │
└──────────────────────────────────────────────────────────────────────────────┘
          ▲ reads ConcurrentDictionary<int pid, long bytes> ×4
┌─────────┴─────────── background thread: TrackNetworkEvents() ────────────────┐
│ TraceEventSession("NetworkMonitorSession_<guid>")                            │
│ EnableKernelProvider(NetworkTCPIP); TcpIpSend/Recv, UdpIpSend/Recv handlers   │
└──────────────────────────────────────────────────────────────────────────────┘
```

Everything lives in one class. Capture, aggregation, rate computation, process enumeration and presentation are
interleaved; nothing can be tested in isolation.

## 3. Features actually available in v1

| Feature | Status |
|---------|--------|
| Per-process TCP/UDP download/upload rate | Works only when started as administrator, IPv4 only (see 4.2), 5-sample moving average. |
| Process list grouped by name | Works; PID + name only. |
| Search by process name | Works; triggers a full synchronous refresh on every keystroke. |
| TCP remote endpoints of the selected process | IPv4 only, includes listening sockets and TIME_WAIT rows, ports ≥ 32768 broken (4.4). |
| "UDP IP Addresses" of the selected process | Actually shows *local* bound addresses, mislabelled. |
| Dark theme | Hard-coded colours, fixed 1042×606 non-resizable window. |

Not present: totals, history, charts, connection states, IPv6, interfaces, diagnostics, settings, export,
keyboard/screen-reader support, DPI awareness, error reporting.

## 4. Defects and risks

Severity: **C** critical (wrong data / system impact), **H** high, **M** medium, **L** low.

### 4.1 Build and dependencies
| # | Sev | Finding |
|---|-----|---------|
| B1 | C | `Guna.UI2.dll` and `MetroSuite 2.0.dll` are referenced through a `HintPath` *outside* the repository (`..\..\..\..\DLLs\CSharpBeautifulThemes\...`, csproj lines 60-65). A clean clone cannot build. Guna.UI2 is a commercial library with its own license terms. |
| B2 | M | Legacy csproj + `packages.config` + binding redirects + Costura.Fody. 27 packages, of which `Microsoft.Extensions.DependencyInjection/Logging/Options`, `Microsoft.Diagnostics.NETCore.Client`, `System.IO.Pipelines`, … are never used by the code. |
| B3 | M | Targets .NET Framework 4.8.1; `AllowUnsafeBlocks` only in Release\|x64; mixed AnyCPU/x64 configurations. |
| B4 | L | The icon has unknown provenance/licensing (`Network_23224.ico`, file name pattern of free icon sites). |

### 4.2 ETW capture (`MainForm.TrackNetworkEvents`, lines 62-84)
| # | Sev | Finding |
|---|-----|---------|
| E1 | C | **ETW session leak.** The session name is randomised per run and the capture thread is a background thread: on exit `Process()` never returns, `using` never disposes, and the real-time session survives the process (ETW sessions are kernel objects). Every run leaks one session; Windows allows a limited number of concurrent sessions (64, of which few system loggers) so repeated runs degrade *other* tracing tools system-wide. |
| E2 | C | **IPv6 traffic is never counted.** Only `TcpIpSend/TcpIpRecv/UdpIpSend/UdpIpRecv` are subscribed; the IPv6 events (`TcpIpSendIPV6`, `TcpIpRecvIPV6`, `UdpIpSendIPV6`, `UdpIpRecvIPV6`) are separate events. On dual-stack networks a large part of real traffic (CDNs, Microsoft, Google services) is invisible. |
| E3 | C | **Silent failure without elevation.** Kernel providers require administrative rights; there is no manifest and no check. The exception is swallowed (`Debug.WriteLine`) and the UI shows `0 B/s` for everything, i.e. it reports *false zeros* instead of "unavailable". |
| E4 | H | Totals are keyed by PID only (`ConcurrentDictionary<int,long>`). PIDs are recycled by Windows, so a new process inherits the counters of a dead one. Combined with E5 this produces fake spikes. |
| E5 | H | **Fake spike on PID reuse.** `CleanupHistory` removes `last*` entries of dead PIDs but never the `total*` entries. When the PID is reused, `previousTotal` is 0 and the whole historical total of the dead process is reported as one second of traffic of the new process (`GetMovingAverage`, line 171). |
| E6 | H | No lost-event accounting (`EventsLost`, `RealTimeBuffersLost` are never read); no session restart if ETW stops; no flush/latency control. |
| E7 | M | Attribution relies on `data.ProcessID`. Empirically (see `docs/research/ETW-KERNEL-NETWORK.md`) the ETW *header* PID is `4` (System) for most receive traffic; only the payload `PID` is correct. v1 depends on TraceEvent's fix-up behaviour without any test proving it. |
| E8 | M | Loopback traffic is counted as network traffic and is counted twice when both ends are local processes. |
| E9 | L | `AddOrUpdate(..., (_, old) => old + data.size)` allocates a closure per event on the hot path. |

### 4.3 Rate computation (`GetMovingAverage`, lines 167-184)
| # | Sev | Finding |
|---|-----|---------|
| R1 | H | Rates assume the WinForms timer fires exactly every 1000 ms. The timer runs on the UI thread that also enumerates processes and rebuilds the list; real intervals vary (and stretch when the UI is busy), so values are biased. No monotonic clock is used. |
| R2 | H | Rates are computed from *delivery* time, not *event* time. ETW real-time delivery is batched (measured average latency ≈1 s, max ≈2 s with the default 1 s flush timer), so a 1 s window aliases badly: bursts show as 0 followed by double. |
| R3 | M | 5-sample moving average presented as "current" speed: it lags and flattens peaks; there is no peak, average or total. |
| R4 | L | `FormatBytes` uses 1024-based divisions with SI labels ("KB", "MB"); there is no TB step. |

### 4.4 IP Helper usage (`NetworkConnections.cs`)
| # | Sev | Finding |
|---|-----|---------|
| N1 | H | Ports are converted with `IPAddress.NetworkToHostOrder((short)row.remotePort)`; ports ≥ 32768 become **negative** numbers. Ephemeral ports (49152-65535) are exactly that range. |
| N2 | H | IPv4 only (`ipVersion = 2`). No `AF_INET6` tables. |
| N3 | M | Size-query/fetch race: the table can grow between the two calls; `ERROR_INSUFFICIENT_BUFFER` is turned into an exception that is swallowed → empty result. No retry loop. |
| N4 | M | TCP rows of every state are shown as "addresses" (LISTEN shows `0.0.0.0:0`, TIME_WAIT rows belong to PID 0). State is read but discarded. |
| N5 | M | UDP: local addresses are shown under a label suggesting remote addresses. |
| N6 | L | `Marshal.PtrToStructure` per row; allocations; `catch {}` around each row hides structural errors. |

### 4.5 UI / UX / accessibility
| # | Sev | Finding |
|---|-----|---------|
| U1 | H | All work runs on the UI thread every second (`Process.GetProcesses()`, list rebuild, O(n²) lookups with `Cast<>().FirstOrDefault` per item and per group). Each keystroke in the search box triggers the same full refresh. |
| U2 | H | GDI handle leak in `MidnightListView.OnDrawSubItem`: `bgBrush` is allocated and then overwritten by a second `new SolidBrush(...)`; the first brush is never disposed — once per painted row. |
| U3 | M | No DPI awareness (no manifest), fixed pixel layout, non-resizable window, no sorting (headers non-clickable), whole control invalidated on every mouse move. |
| U4 | M | No keyboard model, no automation names, owner draw bypasses accessibility; colour is the only state cue. |
| U5 | L | `Process` objects from `GetProcesses()` are never disposed; dead helper `ListViewExtensions.DoubleBuffered`. |

### 4.6 Error handling, robustness, observability
| # | Sev | Finding |
|---|-----|---------|
| X1 | H | Empty `catch {}` blocks in 9 places; failures are invisible to the user and to the developer. |
| X2 | M | `int.Parse(SelectedItems[0].Text)` throws when the selection is cleared (swallowed). |
| X3 | M | No logging, no diagnostics of the capture pipeline, no tests. |

## 5. Technical limits inherent to the platform (apply to any implementation)

These are not v1 bugs but constraints the rebuild must respect and document:

1. **Per-process byte accounting needs ETW kernel network events**, which require an elevated token (or the
   *Performance Log Users* group). IP Helper connection tables expose ownership but not byte counts; TCP extended
   statistics (`GetPerTcpConnectionEStats`) also require elevation and are TCP-only.
2. **ETW sizes are transport payload bytes** (TCP/UDP data) — no IP/TCP/UDP/link headers, no retransmissions in
   `Send`. Interface counters (`MIB_IF_ROW2.InOctets/OutOctets`) include headers and *all* protocols (ICMP, ARP,
   ND, DHCP, VPN encapsulation, …). The two can never be equal and must not be summed.
3. **Ownership is creator-based**: traffic is attributed to the process that owns the socket/endpoint. Services hosted
   in `svchost.exe` share a PID; kernel-mode clients (SMB, WireGuard, …) appear as `System` (PID 4).
4. **UDP has no connections.** The UDP table lists local endpoints only; remote peers exist only in observed
   datagrams (ETW).
5. **TIME_WAIT rows have no owner** (PID 0) — the owning process may already be gone.
6. **VPNs and loopback** cause the same bytes to appear on more than one interface and/or in more than one process.
7. **Some process information is protected** (protected/PPL processes, other users' processes when unelevated):
   paths, command lines and signatures may legitimately be unavailable.

## 6. Conclusion

v1 is a proof of concept. Its data is unreliable in common conditions (IPv6, PID reuse, non-elevated start,
ephemeral ports), it degrades the system over time (ETW session leak, GDI leak), and it cannot be built from a
clean clone. Nothing in the code base is worth keeping as-is: the rebuild keeps the *intent* (per-process TCP/UDP
upload/download, process grouping, search, per-process endpoints) and re-implements every part on a tested,
documented foundation. The mapping of v1 features to v2 is in `docs/ARCHITECTURE.md` §12.
