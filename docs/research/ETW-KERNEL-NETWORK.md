# Microsoft-Windows-Kernel-Network — empirical findings

Measured on Windows 11 Pro 10.0.26200 (x64), elevated, 2026-11-21, with a throw-away consumer written against the raw
ETW API (`StartTrace` / `EnableTraceEx2` / `OpenTrace` / `ProcessTrace` / `TdhGetEventInformation`). These facts drive
the decoder and are re-verified by the integration tests in `tests/WinNetworkManager.Telemetry.Tests` on every
supported Windows build.

## Provider

| Item | Value |
|------|-------|
| Name / GUID | `Microsoft-Windows-Kernel-Network` `{7DD42A49-5329-4832-8DFD-43D979153A88}` |
| Keywords | `0x10` IPv4, `0x20` IPv6 (`0x8000000000000000` = Analytic channel bit, set on every event) |
| Level | 4 (Informational) |
| Session type | Ordinary real-time session (no NT Kernel Logger needed, no conflict with Resource Monitor/xperf) |
| Privilege | Elevated token (or *Performance Log Users*) to create the session |

## Event IDs and layouts (version 0)

All integer fields are little-endian except ports, which are **network byte order** (`TDH_OUTTYPE_PORT`).
IPv4 addresses are a `UINT32` in network byte order (`TDH_OUTTYPE_IPV4`); IPv6 addresses are 16 raw bytes.

| ID | Meaning | Layout (offset:field) | Size v4 / v6 |
|---:|---------|-----------------------|--------------|
| 10 / 26 | TCP data sent | 0 PID · 4 size · 8 daddr · (12/24) saddr · (16/40) dport · (18/42) sport · startime · endtime · seqnum · connid | 36 / 60 |
| 11 / 27 | TCP data received | PID · size · daddr · saddr · dport · sport · seqnum · connid | 28 / 52 |
| 12 / 28 | TCP connect attempted | PID · size · daddr · saddr · dport · sport · mss · sackopt · tsopt · wsopt · rcvwin · rcvwinscale · sndwinscale · seqnum · connid | 44 / 68 |
| 13 / 29 | TCP disconnect | as 11 | 28 / 52 |
| 14 / 30 | TCP retransmit | as 10 | 36 / 60 |
| 15 / 31 | TCP connection accepted | as 12 | 44 / 68 |
| 16 / 32 | TCP reconnect attempt | as 11 | 28 / 52 |
| 17 | TCP connect failed (v4+v6 keyword) | PID? · error code | — |
| 18 / 34 | TCP "copied in protocol on behalf of user" | as 11 | 28 / 52 |
| 42 / 58 | UDP data sent | as 11 | 28 / 52 |
| 43 / 59 | UDP data received | as 11 | 28 / 52 |
| 49 | UDP failure | error code | — |

The decoder does **not** hard-code these offsets: it resolves them once per (event id, version) through
`TdhGetEventInformation` and caches them, so a layout change in a future Windows build is picked up automatically
(and reported if a required field disappears).

## Findings

1. **Byte counts are exact transport payload.** Loopback TCP 8 000 000 B sent → `Send` Σsize = 8 000 000,
   `Recv` Σsize = 8 000 000 (both with an immediately posted receive and with a deliberately slow reader). UDP
   500 × 1000 B → 500 events, 500 000 B each direction. HTTPS download of a 2 441 966 B body → `Recv`
   Σsize = 2 454 146 B (difference = TLS records + HTTP headers + handshake, ≈0.5 %).
2. **`TCPCopy` (18/34) must not be added to receive totals.** It was never emitted in any of the scenarios above, while
   `Recv` already accounted for every byte. Third-party traces report it as a duplicate of receive data. It is
   counted separately (diagnostics only).
3. **Use the payload `PID`, never the header `ProcessId`.** For remote traffic the header PID was `4` (System) in
   385 of 386 events of an HTTPS download and for the UDP receive of a DNS reply; the payload PID was always the
   socket owner.
4. **Address semantics differ between TCP and UDP receive.**
   * TCP (all event types): `daddr`/`dport` = **remote**, `saddr`/`sport` = **local** — in both directions.
   * UDP send: `daddr` = remote, `saddr` = local.
   * UDP receive: `daddr` = **local** (datagram destination), `saddr` = **remote** (datagram source).
   The manifest message strings ("received from %4:%6 to %3:%5") are misleading and must not be trusted.
5. **Delivery latency.** With the default 1 s flush timer, average delivery latency was ≈1.0 s and max ≈2.1 s.
   With `EVENT_TRACE_USE_MS_FLUSH_TIMER` (`LogFileMode |= 0x10`) and `FlushTimer = 200` ms: average ≈316 ms,
   max ≈550 ms. Rates are therefore computed from **event timestamps** bucketed on the QPC timeline, never from
   arrival time.
6. **Timestamps.** Without `PROCESS_TRACE_MODE_RAW_TIMESTAMP` the consumer receives FILETIME-converted timestamps.
   With it (and `Wnode.ClientContext = 1`) timestamps are raw QPC values directly comparable with
   `Stopwatch.GetTimestamp()` (both 10 MHz on the test machine).
7. **Throughput.** A 2 GB loopback transfer at ≈3.2 GB/s generated ~10 700 events (large coalesced segments); the
   decoding callback spent < 1 ms in total. 0 events lost with 64 KB × 16–128 buffers.
8. **Kernel-Process (`{22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716}`, keyword `0x10`) in the same session** delivers
   `ProcessStart` (id 1, v4: ProcessID, **ProcessSequenceNumber** (UINT64, unique per boot), CreateTime, ParentProcessID,
   …, ImageName) and `ProcessStop` (id 2, v2: ProcessID, ProcessSequenceNumber, CreateTime, ExitTime, ExitCode, …).
   This gives exact process lifetimes in the same ordered stream as network events and removes PID-reuse
   ambiguity.

## Consequences for the design

* Count: TCP `Send`(10/26) as TCP upload, TCP `Recv`(11/27) as TCP download, UDP 42/58 upload, 43/59 download.
* Count separately (never in totals): retransmits (14/30), TCPCopy (18/34), connect/accept/disconnect/fail.
* Attribute by payload PID → (PID, CreateTime) process instance, resolved with Kernel-Process start/stop events.
* Classify every event as loopback / external using the remote address, so loopback can be shown separately and
  excluded from "network" totals.
* Bucket by event QPC timestamp (250 ms buckets), finalize a bucket once `now > bucketEnd + 1 s`; late events go to
  the oldest open bucket and are counted as "late".
