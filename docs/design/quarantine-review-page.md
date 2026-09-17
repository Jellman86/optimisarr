# Quarantine replacement review

Quarantine's bottom sheet could overlap the sidebar and consume the list's usable
height. Comparison now has a dedicated `/quarantine/:id` page inside the same
shared content width as Libraries and Settings. The existing theme tokens,
surface gradients, typography and hover shadows remain the visual reference.

The hierarchy is recognition, size comparison, original/replacement playback,
verification evidence, then the decision. File paths are available in a disclosure
below the players; the filename leads the page instead of a long absolute path.
At narrow widths the players stack and verification evidence wraps beneath its
check name. The page uses normal app scrolling with no overlay or fixed footer.

## Navigation and second-order review

- Rows expose real keyboard-accessible links; deep links and reloads fetch the
  requested replacement independently of the list.
- The app preserves the Quarantine page instance between list and review routes.
  Returning by breadcrumb or browser Back restores the list scroll and opener focus.
- Each detail request has a revision; stale results or failures cannot replace
  another review. Retry is explicit. A newer list refresh similarly supersedes an
  older response.
- The comparison is keyed by replacement ID, so native players and their timeline
  state reset when reviewing another replacement.
- Approve and rollback retain their existing explicit confirmations and API calls.
  Buttons disable together during a decision. Failures appear beside the decisions;
  list refreshes no longer erase partial bulk failures.
- Finished entries are read-only. They expose historical sizes and verification,
  without presenting a deleted or restored file as a current comparison pair.
- MediaCompare and VerificationChecks retain their behaviour while gaining theme
  tokens, larger touch targets and narrower-screen reflow. These components also
  serve Preview and Queue, so their regression suites are included in validation.

Browser verification uses deterministic API fixtures. It checks layout, routing,
state and controls; it does not approve or roll back real media. Playback still
uses original native streams, so browser codec limitations and Download fallback
remain. No backend processing, verification policy or database schema changed.

## Validation

- Frontend checks: zero Svelte/TypeScript errors or warnings, all eight translations
  audited, 28 unit tests passed. Production build passed with the existing large
  main-chunk advisory.
- Full Chromium suite: 141 tests passed with two workers. An initial run alongside
  WebKit and backend compilation hit unrelated dashboard timing limits; the full
  lower-concurrency rerun passed without changes to those tests.
- Safari/WebKit: 42 Quarantine, Queue, Preview and Calibration tests passed.
- Backend: warnings-as-errors build passed; all 2,019 tests passed.
- Visual review: desktop dark/light, 375px phone, landscape, long translated text,
  comparison widths, verification wrapping and reachable decisions. No overlapping
  panels or horizontal page overflow in the reviewed states.
