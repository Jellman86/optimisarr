# Queue and Settings polish review — 16 September 2026

## Queue dialog correction — 17 September 2026

The initial inline-detail decision failed for long queues: selecting a lower row expanded
details above the list and disconnected the result from the click. The initial review
missed this long-list interaction. The Queue now uses the same native modal mechanism as
Inventory, with a poster, title and status header, a scrolling detail body, and persistent
job actions. Working-card artwork and titles also open the dialog.

The new regression opens job 75 in an 80-row queue. Opening, Escape and backdrop dismissal
preserve the main scroll position and restore the original row's focus. Existing tests
now exercise dialog behaviour, live telemetry, transition into the history list, action
failure, paused status and completed jobs with no action footer. A short-window test covers
long titles, artwork, the full quality-retry action set and native focus containment.

Visual inspection covered desktop dark/light, a 375 × 667 phone, 667 × 375 landscape,
queued, working, failed and completed jobs. The body scrolls independently; the title,
close button and actions stay visible. No horizontal overflow or overlapping controls
were found in these reviewed states. Data and artwork are deterministic test fixtures.

The original comparative assessment below records the earlier implementation; its inline
detail choice is superseded by this correction. Backend behaviour and replacement safety
remain unchanged.

Validation: 116 Chromium tests, 18 Queue/Inventory WebKit tests, 24 frontend unit tests
and 2,019 backend tests passed. Frontend checks and the backend build have zero errors or
warnings; the production frontend build retains its existing bundle-size advisory.

## Comparative assessment

The Queue follows the selected **Now & next** direction. Settings follows the user's
correction: Libraries supplies the surface, colour and content width; clickable room
cards retain the earlier hover lift and deeper shadow.

| Area | Previous behaviour | Result and tradeoff |
| --- | --- | --- |
| Working jobs | The main job and table repeated work; details opened in a bottom sheet | One prominent poster-led job, compact additional jobs and inline details. The lead stays stable during progress updates. Details use more vertical space but preserve context and keep the next jobs accessible. |
| Progress | Encoding could appear complete before the process exited | Encoding caps at 99%, with live speed, frame rate, ETA or finishing state when available. Unknown transfer progress stays indeterminate. The sidebar uses the same percentage calculation. |
| Processing stages | Status and telemetry were spread across the view | Probe, encode, verify and replace describe the actual pipeline. Remote transfer, adaptive measuring and awaiting verification keep their specific explanations. |
| Waiting and history | Working jobs shared the same filter/table | Filters and counts apply to the waiting/recent list. Working jobs stay visible when a history filter has no matches. Native buttons expose details by keyboard; closing restores focus. |
| Job actions | Dense row and detail controls | Replace remains directly available for verified outputs. Details retain stop/remove, retry, higher-quality retry and exclude. Bulk cleanup lives under Manage queue. Verification gates and confirmations are unchanged. |
| Settings width | Overview capped at 64rem and child rooms at 48rem inside the app wrapper | Overview, heading and every child room fill the shared app content width used by Libraries. Field alignment and mobile reflow remain intact. |
| Settings surfaces | Separate room gradients, reduced shadows and flat section headers | Shared card gradients and colours restore consistency with Libraries. Room links lift by 1px and deepen their shadow on hover; reduced motion keeps the existing shared behaviour. |
| Artwork | A completed cached image could remain transparent | Loading state resets before the keyed image mounts, preventing a post-render reset from erasing a successful load. Fixed artwork dimensions and silent missing-image fallback remain. |

The implementation uses real filenames, queue state and proxied artwork. It adds no
invented synopsis, title catalogue data, speed or ETA. Host usage is explicitly labelled
as this server's usage and is not presented as a remote worker's measurement.

## Second-order review and fixes

- A GET started before a SignalR update could rewind progress. Request generations reject
  superseded responses; per-job progress revisions preserve updates received during the
  accepted request. A real status or remote-stage change resets telemetry instead of
  carrying encode progress into verification or transfer.
- Polling previously shared the action-error state. Load errors are now separate, so a
  periodic successful read cannot erase an unsuccessful stop/remove error.
- Local suspension does not imply remote suspension. Only the API's confirmed full local
  suspension labels an encode paused; partial results retain the backend's aggregate
  explanation. Remote work and verification keep their own state.
- A selected job can finish while details are open. Details resolve from the current job
  list and update available actions. Closing returns focus to its new history-row button
  when the working-job button has disappeared.
- Stop and remove remains sequential: a failed cancel never sends DELETE. Bulk replacement
  still requires one confirmation and uses the existing backend verification policy.
- A second pass reduced secondary working-card height, removed an empty management menu,
  corrected tab spacing, and checked progress agreement between the Queue and sidebar.
- Visual inspection caught the cached-artwork reset race. The shared thumbnail fix applies
  to Queue, Inventory and the sidebar; their regression suites remain green.
- Settings regression checks cover every room's width, retained drafts, return focus,
  language menus, and separation of System cards and encoder tiles.

## Verification and limits

- Backend build with warnings treated as errors: **zero warnings/errors**; **2,019 tests passed**.
- Svelte and TypeScript checks: **zero errors/warnings**. Locale audit passed for all eight
  translations; **24 frontend unit tests passed**.
- Full Chromium suite: **114 tests passed**.
- Queue and Settings in WebKit: **23 tests passed**.
- Production frontend build passed. The existing advisory for the main JavaScript chunk
  above 500 kB remains; bundle splitting is outside this change.
- Separate visual inspection covered dark and light desktop layouts, resting and hovered
  Settings cards, full-width Encoding, narrow System cards, loaded and missing posters,
  expanded job details at 375px, and reachable actions in 667 × 375 WebKit landscape.
  No overlapping cards, horizontal page overflow or unreachable controls were found in
  those reviewed states. Scrolling is intentional for long settings and job details.

Browser checks use deterministic API and artwork fixtures. They verify layout and
interaction, not live provider availability or a real encode. No backend, schema,
verification gate or original-media replacement policy changed.
