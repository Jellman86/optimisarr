# Library path safety

The first hardening slice rejects symbolic links and Windows reparse points visible at
library-path checkpoints. Scans skip linked entries before directory descent. Existing
inventory is checked again for original-media streaming, worker source transfer, probing,
work preparation and replacement/rollback recovery. Blocked records are retained; fixing
the path and re-scanning does not require losing replacement history.

Configured library roots must be accessible, absolute directories and must not themselves
be links or reparse points. Source files must be strictly below their saved root; sibling
prefixes and parent traversal are refused. Every directory component from the root to the
file is checked, including the leaf. A linked directory is refused even if its target is
inside the same library. Configure a library at the real directory instead of adding links
under another library. Hard links remain subject to the separate library hard-link policy.

Ancestors **above** a configured root belong to the operator's trusted storage configuration.
They may include established home-directory or OS aliases; this slice does not rewrite
them or restrict mounts. Unassociated legacy inventory has no narrower trusted root, so
its existing components are checked from the filesystem root. A legacy path through an OS
alias may therefore need association with an explicitly configured library before use.

These are point-in-time path checks, **not race-resistant filesystem confinement**. They
do not retain directory authority across external decoder execution, hashing, asynchronous
work or filesystem mutations. A writer able to swap ancestors between checks can still race
the application. Keep library directories, their trusted ancestors, configured work/trash
storage and mounts under trusted administration. These checks do not sandbox FFmpeg or
establish that every secondary file a decoder might open is confined to the library.

The remaining [security roadmap](../roadmap.md#security-hardening-priorities-reviewed-2026-10-21)
requires a separately reviewed handle-pinned/no-follow design across the whole source,
verification, replacement and recovery lifecycle, with deterministic ancestor-swap tests
and native Linux/macOS/Windows qualification. Existing dedupe identity and process claims
cannot substitute for that proof. This first slice does not enable protocol 11.
