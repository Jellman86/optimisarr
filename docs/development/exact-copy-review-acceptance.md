# Cross-library exact-copy review acceptance

D2 of [issue #397](https://github.com/Jellman86/optimisarr/issues/397), checked 11 October 2026.
This development slice adds explicit library scope and saved review preferences after stable
0.2.23. It introduces no file action, perceptual matcher, worker protocol or installer change.

## Behaviour checked

- Same-sized files in separate selected libraries form exact groups; unselected libraries
  cannot become candidates or keepers. The starting library owns the report. Up to sixteen
  libraries are explicit, with a total limit of 50,000 candidates.
- Real hard links across library roots collapse to one physical copy with labelled aliases.
  Alias IDs cannot masquerade as additional keepers. Any unknown or multiple link-count
  observation makes additional storage unknown. Conflicting physical observations are excluded.
- Keep-many and ignore preferences survive unchanged rescans and restart. Repeating a save does
  not rewrite its timestamp. Review-again clears the preference. No original bytes change.
- Changed scope, ownership, roots, membership or generations invalidate preferences. Visible
  file identity/size/time/path checks and the save-time checks fail closed. A deleted secondary
  library stays in the saved scope rather than silently disappearing.
- A D1-schema snapshot upgrades with its observations and library scope intact. Reapplying
  migrations is a no-op. Old observations do not acquire identity evidence from a newer cache;
  an explicit rescan establishes evidence before preferences can be saved.
- Response limits include aliases. Large alias lists cannot hide the second physical copy.
  Shortened groups cannot save preferences about unseen references.
- Library cards and the library overview open a deep-linked review without starting a scan.
  The Inventory entry remains. Artwork uses the existing proxy, lazy loading, fixed boxes and
  quiet placeholders. Manual, keep-all and preferred-library presets edit a draft; saving is
  explicit. Exact-byte size and quality ties are explained.

## Platform verification

The full .NET solution builds with warnings treated as errors. The native backend suite runs
against disposable SQLite databases and synthetic files on the Mac, Quark and Picard. Tests
exercise the actual filesystem identity and hard-link implementations. These are local SSD
fixtures, not the user's media library or a USB/SMR qualification.

| Platform | SDK | Backend result |
| --- | --- | --- |
| Mac, Arm64 | 10.0.302 | 2,946 passed |
| Quark, Linux | 10.0.300 | 2,946 passed |
| Picard, native Windows | 10.0.401 | 2,938 passed, eight existing platform skips |

Riker's migration backup is active. Its storage and production services were left alone.
Picard's installed worker and application-control configuration were not changed.

The final Chromium suite passes all 325 tests. Safari/WebKit passes all 36 audit tests.
Frontend type checks are clean with zero warnings; nine locale catalogs and 95 unit tests pass.
The production frontend build succeeds. Python checks pass all 105 tests; generated OpenAPI,
Unraid metadata, documentation links and screenshot-manifest hashes are current.

Browser checks use fabricated API responses. Coverage includes 375, 812, 1440 and 2048 pixel
widths in both themes, WCAG AA checks, keyboard selection, broken artwork, failed saves, reload,
explicit scope, ignored groups and 200% text. Native integration tests exercise the actual
API, SQLite and filesystem. Documentation images use only fabricated media and artwork.

The completed final Chromium run uses two workers because this Mac was under heavy ambient
load. Earlier parallel runs hit existing dashboard timing checks and found the now-fixed
200% text overflow. The final complete suite passes without weakening its assertions.

## Limits of this evidence

Metadata and identity checks cannot discover every timestamp-preserving in-place edit. A saved
preference is not fresh content verification or permission for removal. The review remains a
snapshot; forced content refresh is explicit. Unsupported identities disable preference saving.
Windows physical identity is checked, but link counts remain unknown and savings are withheld.

The disposable 100,016-entry indexing study is repeated on this source as a regression check:
16 MiB on the first read, zero bytes on an unchanged rescan, and 1 MiB with fifteen reused hashes
after changing one file. It qualifies candidate selection and cache correctness, not cold-disk
throughput, similarity accuracy, encoding interference or maximum-size cross-library reports.
[Raw D2 study results and source hashes](exact-copy-review-study.json) record these regression runs.
D1's [dated resource measurements](exact-copy-index-acceptance.md) remain historical evidence.

Quality-, smallest- and largest-file recommendations await independently qualified non-identical
matching. Manual exact-copy quarantine/restore is D3 and still needs a durable rollback journal,
shared multi-file claims and fresh hashes before any file action can be enabled.
