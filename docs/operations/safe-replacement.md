# Safe replacement and rollback

```text
scan → eligibility → queue → transcode in /work → verify → ready to replace
                                                        │
                                   manual replacement or opt-in auto-replace
                                                        │
original → /trash quarantine → verified output → library path
                                                        │
                                             approve purge or roll back
```

Before the first replacement, Quarantine is empty. Replaced originals appear
there for review and rollback.

**Libraries → Configure → Schedule & replace → Auto-accept passed jobs** removes
the manual replacement step for every library type, including already-ready jobs.
It is off by default. Enabling it requires a risk acknowledgement and confirmation,
then **Save**. Misconfigured settings can damage files or lose quality, tracks and
metadata; passing only covers the checks you enabled. Test a small manual batch
and keep an independent backup first.

Auto-accept preserves originals in Quarantine. It does not approve or purge them.
Approval and retention cleanup still remove rollback ability. Switching the option
off and saving stops new automatic replacements, including jobs still encoding or
preparing to replace. An action already moving files finishes its protected moves.

Screenshots in this page use fabricated dummy media created for documentation.
No copyrighted material is used.

![Quarantine page showing replaced or finished entries, savings, and rollback state](../images/optimisarr-quarantine-main-dark.png)

A clean FFmpeg exit never replaces an original by itself. Optimisarr probes and
verifies the output, including decode health, stream policy, duration, and the
configured saving requirement. Failed jobs leave originals untouched.

Replacement first records a pending rollback path in the database, then
quarantines the original, moves the verified output into its place, and validates
the final path. If the process stops between those steps, startup recovery uses
the pending record to restore the original or finish the already-completed
replacement before queue processing resumes. Same-filesystem paths use atomic
moves; cross-filesystem copy-plus-delete is an opt-in fallback.

The source and candidate must still match the SHA-256 identities that earned the
passing verdict. Both are checked again before replacement and after their moves.
Historical ready outputs without those identities require a fresh verified attempt;
the upgrade does not invent evidence from an old pass flag or matching file size.
Refusal records a failed **File identity** gate, so the job can be retried normally.

If another file appears at a destination during replacement, recovery preserves it
and the quarantined original. The pending recovery record remains protected from
queue clearing and retention. Inspect the paths and logs before resolving that
conflict; automatic recovery does not overwrite an unidentified file. A partial
remnant whose identity cannot be proven is protected in the same way.

Strict sidecar verification still offloads media probing, decoding and quality
scoring. The server retains database coordination, candidate delivery, file hashing,
replacement and quarantine ownership. These additional full-file hash reads cost
storage I/O; strict sidecar verification does not mean zero server work.

Auto-replace is per-library, disabled by default, and runs only after every
verification gate passes. It does not bypass quarantine or rollback.

The optional **Require soundtrack quality** gate applies to retained re-encoded video audio.
Every supported track/channel/sample must meet your explicit limit; unavailable evidence
blocks replacement. Reporting alone leaves the verdict unchanged, and copied audio uses the
existing retention checks. Sampled evidence does not guarantee unassessed sections or lip-sync.
See [soundtrack controls and limits](../setup/configuration.md#assess-re-encoded-video-soundtracks).
Strict soundtrack assessment requires protocol 8/contract 3 and stays on the worker.

Dry-run mode is global. When enabled in **Settings → Files & safety → Replacement and cleanup**, Optimisarr
still scans, queues, transcodes, and verifies, but replacement and quarantine
purge actions are refused. Verified outputs stop at **Ready to replace** so you
can review the exact work that would have been applied. The timed cleanup may
still remove expired failed outputs from `/work`; it retains their diagnostic
records and never touches an original.

In **Quarantine**, open an entry to use its full-page review with a breadcrumb back to the list.
Compare the original and replacement players, sizes, and verification report. Reject a replacement to restore the original or approve it to
allow purge. Once an original is purged, Optimisarr cannot restore it; keep an
independent backup for media that cannot be replaced.

![Full-page Quarantine review with a breadcrumb, original and replacement sizes, and synthetic media comparison players](../images/optimisarr-quarantine-review-dark.png)

![Quarantine verification report with passed decode, duration, stream, VMAF, and size checks](../images/optimisarr-quarantine-verification-dark.png)

Rollback stages the optimised file before restoring the quarantined original. If
the restore fails, the optimised file is put back at the library path and the
replacement remains available for another rollback attempt.

## What can be safely cleared

- **Queue → Clear errored** removes failed/cancelled queue entries and any retained
  `/work` outputs, except jobs with a live or pending rollback record; originals are untouched. This also removes their diagnostic rows.
- **Queue → Clear completed** removes completed queue entries only.
- **Queue → Clear pending** removes queued and ready-to-replace work and stops
  running jobs; originals are not touched, but verified outputs are discarded.
- **Quarantine → Clear finished** removes history rows for already purged or
  rolled-back entries; it does not touch active files.
- **Approve** permanently deletes a quarantined original. Do this only after
  reviewing the replacement.

The **Cleanup retention** setting applies to both quarantined originals and failed
outputs under `/work`. Every six hours, and once at startup, expired files are
removed. Failed-job reports and FFmpeg logs remain after their scratch output is
deleted. A value of `0` disables timed deletion for both. **Clean up now** previews
the eligible files and bytes, then runs the same policy after confirmation. It does
not purge quarantined originals in dry-run mode or remove files inside the retention
window.

Dry-run mode blocks replacement and purge actions. It does not block scanning,
probing, preview, transcoding, verification, retry, or rollback records that
already exist.
