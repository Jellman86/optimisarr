# Unraid integration assessment — 1 October 2026

The integration was checked against the published Community Apps listing, the repository's
released `main` assets, the container contract and the current Unraid submission documentation.
The local follow-up also implements the operator's request to enable workers on new installs.

## Findings and corrections

| Area | Assessment |
|---|---|
| Discovery | [Optimisarr is already listed](https://ca.unraid.net/apps/optimisarr-0y5bjeh0aktq6l). The old guide/roadmap incorrectly described listing as future work; corrected. |
| Application and profile icons | Both XML files reference the same released transparent 192 × 192 Precession PNG. The raw URL returns HTTP 200, matches the local asset byte-for-byte, and visually matches the native application icon. No icon replacement needed. |
| Listing overview | The old overview covered movies/TV but omitted audio/images, VMAF and distributed verification. Updated both overview and legacy description; explain retention rather than implying indefinite rollback. |
| Helpful metadata | Added the setup guide, actual GPL license and two existing fabricated Dashboard/Queue screenshots. Expanded relevant search terms. No invented support forum, donation link or compatibility certification. |
| Container image | Anonymous registry inspection found `:latest` and `:0.2.17` resolve to the same manifest, `sha256:9464c3a91777c898a31e5c0149a5727ef3293450d79b569095d9a9d6bd6d5b42`. This is the released channel; `:dev` remains distinct. |
| Port, identity and persistence | Bridge networking, no privileged mode, TCP 8787, masked empty admin token, `/config`, common read-write `/data`, Unraid UID/GID 99/100 and umask 002 match the runtime contract. Work/quarantine stay below `/data/.optimisarr`. |
| Hardware | Intel/AMD device and NVIDIA runtime guidance remain valid. CPU-only users must remove the optional `/dev/dri` mapping; an optional XML field is not host-device autodetection. Actual filesystem/mount relationships must be checked in setup. |
| Installation instructions | Prefer Apps → search → Install. Corrected the manual alternative: save raw XML into `templates-user`, then select the saved template. The dropdown is not an arbitrary-URL input. |
| Worker defaults | Availability now defaults on when the existing environment variable is unset. Fresh available installations persist enabled workers and strict verification; historical enabled/disabled choices are preserved, including disabled when the old key is missing. Explicit pairing, placement and replacement policy remain separate. |
| Disable control | The template exposes the existing environment name with value `true`. `false` disables deployment availability. Empty/unknown overrides fail closed. The saved UI switch stops pairing/check-ins without deleting paired records. |
| Shutdown/update | The app's graceful timeout does not override Unraid's stop timeout. The guide advises pausing new work and waiting for active jobs before update, preserving config/storage mappings. No host-wide timeout was changed. |

The repository profile meets the required root filename, root element and non-empty profile
contract. Its real project/support links remain correct. Supported tags were checked against
[Unraid's current parser-backed field reference](https://ca.unraid.net/submit/help/xml-field-reference)
and [repository profile rules](https://ca.unraid.net/submit/help/repository-info-xml).

## Verification and second-order effects

- New startup/default tests failed before the implementation. They now prove fresh enabled,
  explicitly unavailable, historical missing-key disabled, saved true/false and repeated startup.
- Mac and Quark Linux each passed 2,511 backend tests with zero-warning Release builds. PICARD
  passed all 29 changed worker-control/settings tests with a zero-warning native backend build.
- An isolated real API, with no worker environment override, proved availability, fresh enabled
  workers, strict verification, dry-run, admin-protected PIN issuance and explicit pairing. Saving
  disabled blocked heartbeat while retaining pairing. Restart preserved disabled; an explicitly
  disabled fresh deployment refused pairing but could still save other settings.
- The first documentation recapture refused three newly required API routes instead of silently
  accepting a partial UI. Updated its explicit appearance/results fixtures and derived displayed
  application/sidecar versions from project metadata instead of the obsolete 0.2.13 literal.
  The fixture now honours the live-jobs query instead of displaying finished/queued jobs as
  in-progress work on the Dashboard. Frontend CI now runs the complete isolated capture harness
  to detect future fixture/API drift.
- All 195 Chromium E2E tests and 75 frontend unit tests passed; type, locale and documentation
  checks were clean. UI wording was updated in all nine languages. Documentation captures use
  the established isolated Playwright harness and fabricated data.
- Six metadata regression tests cover stable-channel drift, icon drift, broken WebUI mapping,
  incorrect worker defaults and work outside the common storage root. The new offline metadata
  validator is in the existing documentation CI gate. All 70 Python tests passed.

The initial Windows installer CI attempt failed at SCM service startup, after successful native
rendering, unpaired upgrade and pairing. No worker binary changes were in this branch. Its upload
contained only the MSI/checksum: the harness wrote logs to the user TEMP directory while CI uploaded
runner.temp. Corrected the evidence root and added failure-only service/event diagnostics before
cleanup, without retrying or suppressing failed service starts. CI now verifies the retained
installation log before uploading, on successful and failed smoke runs. The initial attempt
would fail that evidence gate. PICARD passed native PowerShell parsing and proved that the
existing-installed-host guard still refuses smoke installation before any mutation. A native
non-installing regression test failed against the original harness, then passed with retained
service/event evidence and original-error preservation, including when the diagnostic provider
itself fails. The initial failure's cause cannot
be established from the retained evidence; the passing final checks must not be described as a
proven service-start bug fix.

No command/protocol, codec, VMAF threshold, worker lease or replacement implementation changes
are part of this default change. It does not auto-discover or trust another machine, re-enable
upgraded installations, alter library placement, or start jobs while first-run dry-run is active.
Existing fully disabled choices remain disabled. The deployment override takes precedence over
availability even if an older database contains enabled workers.

## Publication boundary

At assessment time, public `main` and the listing still contain the prior overview and the
released 0.2.17 software. The icon is already current. These corrections require the ordinary
reviewed release promotion to `main`; merging to `dev` alone does not update Community Apps or
`:latest`. After promotion, validate/scan the repository in the
[submission portal](https://ca.unraid.net/submit/help), then check the live listing and cached
readme/images. Existing Unraid installations retain their personal template and settings.

A native Unraid host and authenticated submission session were not available for this assessment.
No submission was made, and native Docker Manager installation, cache refresh and stop-timeout
behavior are not claimed as tested. The remaining publication/native checks are distinct from the
passing source/container-contract and startup tests.
