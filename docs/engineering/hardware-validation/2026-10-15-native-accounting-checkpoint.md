# Independent Mac accounting checkpoint, 15 October 2026

Development component evidence for [draft #415](https://github.com/Jellman86/optimisarr/pull/415).
The production runner remains on private helper protocol v1. Startup/runner integration,
typed RAM creation and full packaged-work acceptance are still open; automatic protocol 11
remains disabled. This does not close the Mac app resource-lifetime P1.

## Independently retained state

A coordinator UUID alone cannot detect a valid older journal copied over the latest ledger.
The new optional journal checkpoint binds its store UUID, fixed directory and complete ledger
SHA-256 in a separate Keychain item. Production integration must use this anchored constructor;
the original unanchored constructor remains available for isolated component fixtures.

Each transition runs while the existing coordinator lock remains held:

1. Validate the current ledger and independent committed checkpoint.
2. Persist and read back a pending checkpoint containing exactly the old and new hashes.
3. Replace the ledger and confirm file/directory durability.
4. Persist and read back the new committed checkpoint before exposing the transition.

Any uncertain checkpoint mutation closes the journal, including failures before file replacement.
Reopening accepts only the committed hash or one of the two hashes from an interrupted pending
transition. It confirms filesystem durability and commits the chosen checkpoint before exposing
records. A first-start interruption may leave an empty unanchored journal; a missing anchor cannot
adopt nonempty accounting. Missing pinned data, substituted stores, changed live anchors and
unrelated older snapshots remain unavailable.

Both pending alternatives are conservative because subsequent work requires a successful commit.
Earlier effects already support replay: targeted writer retirement, receipt consumption after
durable retirement, identity-bound RAM removal and idempotent accounting release. Independent
checkpoint failure cannot authorize the next effect.

This protects against journal-only rollback. Coordinated restoration of both Keychain and ledger,
malicious changes by the account owner and whole-system backup recovery are outside this component
claim. A restored nonempty journal without its independent item remains blocked.

## Keychain and interaction

The backend is selected explicitly and never changes automatically after an error. The default
file-based backend works with the current Developer ID packaging. A separate signed test app
could not create a data-protection item without additional entitlements (`-34018`); that refusal
is retained as negative evidence. Explicit data-protection selection cannot borrow an existing
file-based checkpoint. See [Apple's distinction between the two implementations](https://developer.apple.com/documentation/technotes/tn3137-on-mac-keychains).

The item does not synchronize and has a separate service/account from pairing. Only an actual
item-not-found result means absence. Authentication, interaction, entitlement, malformed-data,
suppression and restoration errors block admission; this store never deletes an unreadable item.
The optional data-protection backend uses a noninteractive authentication context and device-only
accessibility. Those device-only accessibility semantics are not claimed for file-based Keychain
backups or migration.

Legacy Keychain interaction is process-wide. Credential and checkpoint operations now share one
serialization gate that verifies suppression and restoration, including thrown operation errors.
Reads and every legacy credential mutation use that gate. Pairing's existing unreadable-credential
policy remains confined to its distinct item. None of these tests accessed installed pairing.

## Verification

A separate disposable app was signed with the existing Developer ID identity and linked against
the current release SidecarCore library. Its 35-operation physical-Mac trial passed:

- Bootstrap, durable reservation, process restart, restored debt and reconciliation.
- Four actual hard exits immediately before/after pending and final checkpoint writes. Restart
  resolves old/new durable bytes, confirms the checkpoint and releases never-launched fixture debt.
- Refusal of valid older empty ledger bytes and missing-anchor nonempty state, with bytes retained.
- A differently identified signed executable receives authentication failure without a prompt or
  deletion; the original executable still reads and reconciles the outstanding record.
- Explicit backend selection refuses cross-backend adoption. All six temporary anchors are removed.

These fixtures authorize no helper launch, RAM allocation or media operation. They exercise the
real journal/registry, signing and Keychain boundaries using small disposable accounting records.
They do not qualify the installed menu-bar app or all production resource stages under hard death.

Unit regressions cover uncertain checkpoint writes/readbacks, filesystem replacement/sync failures,
reopening failures, both pending alternatives, recovery finalization failure, old snapshots,
strict bounded decoding and changed anchors. Five registry boundary cases retain charges and veto
the next effect when final checkpoint confirmation fails. Four injected legacy-interaction tests
cover refusal, error restoration, restoration failure and overlapping callers.

The full local Swift suite passes with 440 registered core tests and nine UI tests. Release build
passes with two warnings from the existing deprecated legacy Keychain APIs, now centralized in the
shared gate. Astra's static review found no remaining actionable defects; it did not execute tests.
C# and web source are unchanged from their preceding qualified revisions.

The preceding CI runs exposed shared-thread pressure and Python fixture readiness failures.
Event-driven command drains remove idle reader threads. Lightweight FIFO-backed native fixtures
now install their readiness state without Python startup and retain the same deadline assertions;
their own ten-second fallback prevents an unbounded test helper. Exact-head CI remains mandatory.

No original media, installed pairing, protection settings or services changed. Continue with
production startup recovery before capability advertisement, registry-backed execution, typed RAM
creation failures and the remaining [parity matrix](../../development/adaptive-audio-image-parity.md).
