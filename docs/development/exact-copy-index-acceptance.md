# Durable exact-copy index acceptance

D1 of [issue #397](https://github.com/Jellman86/optimisarr/issues/397), measured 11 October 2026.
These changes are in development after stable 0.2.23. Detection remains read-only and scoped
to one library. There is no quarantine, removal or perceptual-match action.

## Reproduce the disposable study

```sh
dotnet run --project tools/Optimisarr.ExactCopyStudy
```

The tool accepts no library paths. It creates and migrates its own temporary SQLite database,
adds 100,000 uniquely sized inventory entries without opening their paths, then writes sixteen
1 MiB fixture files as eight exact pairs. The application's actual candidate selector returns
only those sixteen files. It runs the production scanner with its default 16 ms pause per
128 KiB block, checkpoints and durable store, then deletes its own temporary directory.
The command prints JSON and exits unsuccessfully if any expected byte count, reused count or
result-group count differs.

The three measured phases are a first complete content read, an unchanged rescan, and a
same-sized edit of one file with a changed modification time and updated inventory record.
Every host read 16 MiB initially, zero bytes on the unchanged rescan, and exactly 1 MiB after
that single edit. The latter reused fifteen hashes and correctly reduced eight groups to seven.

## Observed costs

| Host | Select candidates | First content read | Unchanged rescan | One-file change | Database growth after first scan |
| --- | ---: | ---: | ---: | ---: | ---: |
| Mac | 165 ms | 2.35 s | 59 ms | 205 ms | 16,384 B |
| Quark | 317 ms | 2.42 s | 130 ms | 280 ms | 8,192 B |
| Picard | 280 ms | 3.70 s | 148 ms | 337 ms | 8,192 B |

[Raw measurements and source hashes](exact-copy-index-study.json) record OS, architecture,
.NET runtime, catalog creation time, process CPU time, managed memory and resident memory
at each phase, total database size and the source hashes of the measured implementation.
The first scan's database growth is a page-allocation observation for sixteen fingerprints
and observations, not a universal per-file storage estimate. Database paths differ between
hosts, so the total inventory database sizes differ too.

These are single runs on SSD-backed disposable fixtures under ambient host load. Newly
written files can remain in the OS cache: "first content read" does not mean cold physical
disk. Resident/managed memory figures are point samples, not peak memory. This is a correctness
and initial resource study, not a throughput ranking or a benchmark of USB, SMR, network storage,
100,000 actual content reads, maximum-size result sets or encoding interference. The existing
50,000-candidate and approximately 8 MiB/s read budgets remain unchanged. Production-scale
storage and interference qualification remains part of the later operational work.

## Verification

- Mac: 2,930 backend tests pass; build has zero warnings.
- Quark, Fedora Linux x64: 2,930 backend tests pass.
- Picard, native Windows x64: 2,922 backend tests pass, eight existing platform-specific skips.
- Chromium: all 314 browser tests pass.
- Safari/WebKit: 26 layout, accessibility and exact-copy tests pass, including mobile/wide
  desktop and light/dark review, pagination, reload, interrupted work and forced refresh.
- Frontend checks: zero errors/warnings; all 95 unit tests and nine locale catalogs pass.
- Python: all 105 script tests pass. Documentation links, generated OpenAPI and 61 screenshot
  hashes pass. The refreshed Exact copies screenshot uses fabricated media and artwork.

D1 regressions cover unknown identity and old contract refusal, original read-date preservation,
new size collisions, one-file incremental invalidation, missing/unreadable files, replacement
identities and open handles, full-content refresh of timestamp-preserving edits, cancellation,
checkpoint retries, restart recovery, inventory-scope staleness, result pagination, repeated
migrations and deleting a library during its scan. Windows deliberately blocks replacement
while the scanner's read handle is open; Unix identity checks bind observations to the opened
file even when its directory entry changes. Neither behaviour guarantees exclusion of all
external Unix writers, and metadata reuse cannot discover every in-place edit.

Riker was inspected but not used for content-read or build testing because its migration
backup was active. Its production container and libraries were not modified. This acceptance
changes no installed sidecar, protocol, encoder, verification placement or production deployment.
The CI container build is a separate required PR gate.

## Remaining slices

D2 adds explicit cross-library scope and durable keep/ignore decisions. D3 separately introduces
fresh keeper/copy reads, multi-file claims and journalled same-filesystem quarantine/restore.
Image, audio and video similarity remain separate qualification programmes. Cached hashes and
these timing results never grant removal permission.
