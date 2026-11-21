# Data sources and limits

WinNetworkManager only shows what Windows measures, and says where each figure comes from. When something cannot be
measured it is shown as unknown (`—`, a gap in a chart, an empty export cell) — never as zero.

## Sources

| Figure | Windows source | Granularity |
|--------|----------------|-------------|
| Per-process TCP/UDP bytes, per connection/flow bytes, UDP peers, retransmissions | ETW provider `Microsoft-Windows-Kernel-Network` (TCP send/receive 10/11 and 26/27, UDP send/receive 42/43 and 58/59, retransmit 14/30), real-time session | Every event, grouped in 250 ms intervals by event timestamp |
| Process starts and exits | ETW provider `Microsoft-Windows-Kernel-Process` (start/stop) | Every event |
| Processes (fallback) | Toolhelp snapshot + `GetProcessTimes` | Process list update interval (2 s) |
| TCP connections and listeners, UDP endpoints, owner PID, creation time, owning service | IP Helper `GetExtendedTcpTable` / `GetExtendedUdpTable` (`*_OWNER_MODULE`), IPv4 and IPv6 | Connection table update interval (1 s) |
| Adapter counters (octets, packets, errors, discards), status, speed | IP Helper `GetIfTable2` | Every refresh |
| Addresses, gateways, DNS servers, DHCP, MTU | `GetAdaptersAddresses` (through .NET `NetworkInterface`) | When Windows reports an address change, and every 30 s |
| Default route, route table, route lookup | `GetIpForwardTable2`, `GetBestRoute2` | On demand / every refresh |
| Internet connectivity | Network List Manager (`INetworkListManager`) | Every refresh |
| Executable path, version info, signature, user, session, services, command line | `QueryFullProcessImageName`, version resources, `WinVerifyTrust` (embedded and catalog signatures, no online revocation), token queries, Service Control Manager, WMI `Win32_Process` | Once per process, cached |
| DNS cache | WMI `root\StandardCimv2:MSFT_DNSClientCache` | When you click Read cache |

## Three totals that are never added together

1. **Physical adapters** — octets counted by the network adapters: every protocol (TCP, UDP, ICMP, ARP, DHCP, VPN
   encapsulation…), **including** IP/TCP/UDP headers. Only hardware adapters that are up count; virtual adapters and
   NDIS filter layers are excluded so nothing is counted twice.
2. **Attributed to applications** — TCP and UDP **payload** delivered to or sent by each process (headers excluded),
   external traffic only.
3. **Loopback** — traffic between processes on this computer, including traffic to the computer's own addresses. It
   never reaches an adapter. It is shown separately and excluded from application totals unless you turn on *Count
   loopback traffic in totals*.

The Dashboard also shows **Not attributable**, the approximate difference *adapters − attributed*: headers, protocols
other than TCP/UDP, traffic of the kernel, and traffic that happened before monitoring started. It is approximate
because the two sources measure different things at slightly different moments.

## How values are computed

- **Rates** = bytes in the last refresh window (default 1 s) of completed intervals ÷ window length. Application data
  is about 1.2 s behind real time: an interval is final one second after it ends, so late events are still counted.
- **Peak** = highest 1-second rate observed; **average** = total ÷ monitored time.
- **Adapter rates** = counter difference ÷ measured elapsed time (monotonic clock). When a counter goes backwards
  (adapter reset, driver reload) that interval has no rate and is flagged; long pauses (sleep, stalls) are gaps.
- **Units**: decimal bytes (1 kB = 1000 B) by default; binary (KiB) or bits per second in Settings. Exports always
  contain raw bytes.

## What Windows cannot provide (and what is shown instead)

| Limit | Consequence in WinNetworkManager |
|-------|----------------------------------|
| Kernel event tracing requires administrator rights (or the *Performance Log Users* group) | Per-process traffic needs the elevated helper (one prompt) or an elevated window; without it, per-process figures show — and a banner offers to turn monitoring on, never 0 |
| Windows keeps no per-process traffic history | Traffic before WinNetworkManager started is unknown; totals are "since monitoring started" |
| ETW can lose events when buffers fill (extreme load) | Lost events are counted; affected intervals are marked partial (values are lower bounds) in the UI and in exports |
| Connection tables are snapshots | Connections shorter than the polling interval may only be seen through their traffic (as "observed" flows) |
| UDP is connectionless | UDP sockets are local endpoints; remote peers are those seen in datagrams while monitoring runs |
| A process listening on all addresses has no single interface | "Interfaces used" is shown only when connections or bound addresses determine it |
| The ICMP API reports round trips in whole milliseconds and none for "TTL expired" | Ping shows `<1 ms`; traceroute times are measured by WinNetworkManager around each probe |
| No "don't fragment" option for ICMPv6 echo | Path MTU is IPv4 only |
| Command lines and some details of other users' processes need elevation | Shown as "Access denied" with the reason |
| Windows formats error messages in the system language | WinNetworkManager translates error codes to English itself |
| Process names reported by Windows for protected processes can be unavailable | The PID and "Access denied" are shown |

## Accuracy verification

The ETW integration tests transfer a known number of bytes over loopback TCP (IPv4 and IPv6) and UDP and require the
exact count per process instance, per protocol and per flow; a child process's traffic must be attributed to the
child instance; the collector must recover after its session is stopped externally. The real helper executable is
tested end to end over its pipe. Unit tests cover PID reuse, late and lost events, counter resets, the history store
(exact bytes per second and per minute) and the export formats. See [DEVELOPMENT.md](DEVELOPMENT.md#tests).
