# UI polish review — 16 September 2026

## Comparative assessment

The implementation follows the selected **Control rooms** settings direction and
**Index** inventory direction. The shared theme, raised sidebar and animated tesseract
connect the two views without introducing a separate visual system.

| Area | Selected direction / problem | Implemented result and tradeoff |
| --- | --- | --- |
| Settings | Control rooms, with clearer hierarchy | Processing and protection precede connections and system tools. Complete state summaries, room icons and draft badges support scanning. Encoding labels and explanations align consistently. Existing controls and save semantics remain. |
| System | Cards appeared to touch; encoder tiles squeezed | Explicit gaps separate every section; encoder cells reflow to available width. The System room no longer claims to be read-only when it contains actions. |
| Inventory | Index: dense, calm, file-first list | Four columns organise file identity, size, format and verdict. Artwork aids recognition, while library and media kind provide context. Mobile keeps identity and verdict visible and moves size into the file caption. |
| File popup | Richer media presentation | A large portrait poster, restrained artwork backdrop and prominent title introduce a structured metadata dialog. Audio and image artwork use square frames. The rule verdict remains the first operational detail, above specifications and full path. |
| Data honesty | Mockups use idealised media metadata | Production headings derive from actual filenames. No invented synopsis, ratings or catalogue title are shown. Artwork uses the existing authenticated backend proxy and silently falls back when unavailable. |
| Preview | Consistent, useful comparison | The comparison uses the same theme and a native dialog, retaining media players, statistics and verification evidence. Escape minimises; explicit Close discards the temporary preview. |
| Sidebar and icon | More breathing room; movement even at rest | Desktop keeps its top and bottom inset. Idle geometry takes 36 seconds per turn; working geometry takes six. The existing antialiased atlas avoids live rendering costs. |

The resulting hierarchy gives recognition to the poster and decision-making to the
verdict and actions. The Index remains more efficient for comparing files than the
alternative poster collections direction, while the dialog provides the richer media
presentation requested for individual files.

## Second-order review and resulting fixes

Review covered interactions between the changes, shared components and asynchronous
work, as well as their initial appearance.

- **Filter races:** an older request could replace a newer filter's results. A request
  generation guard now accepts only the current response. Empty filters retain their
  controls, counts and an explicit empty message.
- **Modal navigation:** native dialogs contain focus and make the background inert.
  Escape and backdrop dismissal restore the originating control. The file dialog keeps
  its actions visible while its metadata scrolls, including narrow and short viewports.
- **Preview lifetime:** closing before preview creation returned could leak a late job;
  close and destruction could also discard twice. Cleanup now consumes each job once,
  handles late creation and stops polling after destruction.
- **Browsing during preview:** minimising permits inspection of another file. Starting
  its preview now creates a new component and discards the previous job, preventing old
  comparison data from appearing under a new filename. Closing restores current inventory
  data rather than an old snapshot.
- **Shared artwork:** the sidebar reuses its thumbnail when the current job changes.
  Failure and loading state now reset with media identity, so a missing poster cannot
  suppress the next job's valid artwork.
- **Short sidebar:** the active-job card could push theme, language and collapse controls
  below a short window. The main rail content now scrolls while the footer stays reachable;
  the language popup remains outside that scrolling region.
- **Settings continuity:** draft preservation, leave-page confirmation, reset-to-original
  controls and return focus are covered by the settings tests. System spacing and language
  menu width are checked across expanded, collapsed and small viewport layouts.
- **Motion costs and preferences:** idle and working speeds are tested separately.
  Hidden/offscreen animation stops; reduced-motion sessions use a still and do not fetch
  the animation atlas. Existing missing-atlas fallback remains intact.

## Verification

- Backend build with warnings treated as errors: zero warnings or errors; **2,019 tests passed**.
- Svelte/TypeScript checks: zero errors or warnings. Locale completeness and placeholder
  audit passed for all eight translations; **20 frontend unit tests passed**.
- Full Chromium suite: **104 end-to-end tests passed**, including Settings, Sidebar,
  Inventory, Preview, Setup and shared media comparison regressions.
- Targeted WebKit suite: **12 tests passed** across Inventory and Preview, including keyboard dismissal, missing
  artwork, probe success/error, filter races, minimised browsing and late-job cleanup.
- Separate visual inspection covered desktop dark/light presentation, the poster dialog,
  missing artwork and long filenames on a 375px phone viewport, and a 667 × 375 landscape
  WebKit viewport. The reviewed states showed no horizontal page overflow, overlapping
  panels or inaccessible dialog actions.
- Production frontend build passed. The existing advisory about the main JavaScript
  chunk exceeding 500 kB remains; this work does not claim to resolve bundle splitting.

Browser checks use deterministic API and artwork fixtures. They verify presentation and
interaction, not the availability of a user's live artwork provider or a live transcode.
No backend, database schema, verification gates or original-media replacement policy changed.
