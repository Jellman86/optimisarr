# Shared native worker host

This executable lets a native client use the same audio/image search, transfer, metadata and
strict verification implementation as the .NET workers. It is not a second worker identity
and never pairs, heartbeats or claims work itself. The parent keeps those responsibilities.
The shipped protocol maximum remains 10 while native-client integration and packaging are
qualified.

The private pipe protocol is version 1. The executable takes no arguments. Its parent writes
one UTF-8 JSON request followed by a newline and keeps stdin open until completion. Requests
are limited to 256 KiB and depth 32. Duplicate fields, including case aliases, are refused.
Credentials belong only to that inherited pipe; never place them in arguments, environment
variables, files, logs or diagnostic events.

`capabilities` uses explicit absolute tool/working paths and the shared synthetic capability
collector. It returns tool proof only, without inventing current memory or storage facts.
`run` additionally requires a pairing identity, a complete native audio/image assignment and
an allocation of memory already reserved by the parent. That allocation is not an advertised
host-memory reading. The parent must retain the native/RAM workspace reservation until the
host exits and every owned working volume is cleaned up. The shared runner independently
checks the assignment and available disk space before fetching its size/hash-bound source.

`requirements` accepts the same complete native assignment but forbids both pairing credentials
and a memory grant. It validates the frozen contract and returns the shared engine's native
peak and required scratch bytes for that job, without starting tools, touching scratch or
contacting a server. This is a budget, not current capacity or permission to encode. The parent
must add the full allocation of any RAM volume, atomically reserve that total against live
memory and its RAM-storage preference, and only then send a separate `run` request with its
credential and held grant. A requirements reply is successful only together with exit code 0.

Stdout contains versioned JSON events, one per line. Status/progress are bounded and may be
coalesced under backpressure; completion is final and is never displaced by later progress.
Details are bounded and the pairing secret is redacted. A broken output pipe cancels work.
Closing stdin or sending SIGTERM cancels the owned operation; native children stop before
scratch cleanup and the host's completion. The parent must confirm both the terminal event
and process exit, and still use the server's recorded verdict as authority.
Request reads and output writes have bounded waits even on platforms whose console streams
ignore cancellation. A cancelled pipe operation is never reused; the single-use process exits.

The host is built by the main and Windows-worker solutions and tested by the shared worker
suite. Distribution must publish a self-contained runtime for the native client's architecture,
include the runtime's required notices, and qualify the actual bundle, tool dependencies,
parent-death/cancellation behavior and signing/runtime entitlements. The Swift client and ordinary job runner now route complete native assignments to this host when
it is bundled; an installation missing it hands back the job. The AcceptanceWorker exercises the
same claim, admission and execution path with live Mac resources. The ordinary Swift capability
collector now obtains bounded, typed tool proof through `capabilities` when the helper is bundled.
Pairing and check-ins refresh live memory and the chosen physical disk fallback, subtracting app
reservations. Unknown resources clear capacity; the helper cannot supply resource claims in its
tool proof. Unpair, sleep and quit cancel pending qualification, and volume classification runs
away from the menu's main actor. Packaged advertisement, restart recovery and exact bundle
qualification remain in progress.
Do not advertise protocol 11 on the strength of the host or bridge alone.

The Mac packager now publishes this helper self-contained for the build host's architecture,
retains notices from each exact runtime pack and signs all nested Mach-O code before the app.
Developer ID builds give only the helper the JIT entitlement required by
[.NET on macOS](https://learn.microsoft.com/en-us/dotnet/core/deploying/macos).
Ad-hoc development builds do not enable the hardened runtime's team-based library validation.
`verify-app.sh` checks a relocated bundle's managed startup, real JPEG/ICC/EXIF/marker proof,
both pinned metrics, empty scratch and native UI fixtures. Pinned ExifTool modules use system
Perl, including its compression support; missing support cannot advertise metadata or image
encoders. This qualification does not enable protocol 11. WebP packaging, full packaged job
lifecycles and restart recovery remain outstanding.
