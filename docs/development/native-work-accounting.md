# Native-work accounting

Tracked by [#332](https://github.com/Jellman86/optimisarr/issues/332). This records how workers
account for memory and scratch space used by quality-first audio/image work, and how they recover
after a crash.

## Windows and Linux workers (shared .NET core)

`PerceptualJobRuntime.Reserve` admits a job only when the live free memory and the scratch
volume's live free space cover its peak requirement after subtracting what this process has already
reserved. Reservations live in the in-process `PerceptualJobResourcePool` and are released when the
job ends. Capability reporting subtracts the same reservations, so the server never offers more
work than the worker can admit.

After a crash or restart:

- Owned scratch recovery (`NativeScratchWorkspace.Recover`) runs before native admission. It removes
  abandoned job and capability workspaces and retains any still held by a surviving tool's
  inherited guard.
- Anything a surviving process still occupies is already reflected in the live memory and disk
  readings, so a restarted worker cannot overcommit it.
- The server re-queues the job when its lease expires. Original files are never at risk: replacement
  requires the server's independent final verification.

**Operator decision, 21 October 2026:** Optimisarr is a home-lab application. Crash and restart
recovery is the worker qualification bar; power-loss durability of worker accounting is a recorded
limit, not a release gate.

## Archived: durable .NET journal

An unconnected durable journal for standalone workers was built and qualified in isolation: a
canonical obligation ledger, independent checkpoint, serialized transaction boundary, Linux and
experimental Windows NTFS storage adapters, a Linux bootstrap, a physical-volume pool mode and
power-cut/process-death fixtures. No worker ever called it. Following the decision above it was
removed on 22 October 2026 and is preserved at the Git tag
[`archive/dotnet-native-work-journal`](https://github.com/Jellman86/optimisarr/tree/archive/dotnet-native-work-journal).
The dated hardware-validation records that cite it remain as history.

## macOS sidecar

The Mac app keeps its own Swift accounting journal. It is connected and in use: it records owned
RAM-disk allocations so they can be retired safely after a crash, and restores outstanding debt
before claiming new work. See the
[runtime qualification and limits](../engineering/hardware-validation/2026-10-15-native-work-runtime.md).
