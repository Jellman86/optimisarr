# Library workflow — 17 September 2026

## Direction and hierarchy

The selected proposal is the processing workflow. It uses the existing Libraries
surface gradients, typography, cyan accents and elevation tokens. The four stages
represent real decisions: choose files, encode, verify, then schedule and replace.
They are independent navigation destinations, not a wizard that forces a particular
editing order. The overview summarises the current draft; stage links and breadcrumbs
preserve it. Specialist settings have explicit pages rather than one expanding drawer.

| Home | Everyday controls | Advanced controls |
| --- | --- | --- |
| Choose files | Name, folder, media type, enabled state, priority, resolution eligibility, minimum size, excluded paths, hardlink protection | Same-codec threshold, efficient-source exception, worker placement, source-codec exclusions |
| Encode | Processing mode and named preset choices | Custom opens advanced video settings directly |
| Encode / Video settings | HDR, resolution reduction, frame-rate cap, black-bar removal, Dolby Vision opt-in | Codec/container overrides, encoder effort, CRF, content tune, bitrate ceiling/floor and adaptive quantisation |
| Encode / Video quality path | Fixed/adaptive strategy and VMAF policy | Custom thresholds link to advanced verification |
| Encode / Audio & subtitles | Audio conversion, retained languages and stereo/surround choice | Explicit bitrates and lossy-source re-encoding |
| Encode / Images | Format and resize choices | Exact quality and lossy-source re-encoding |
| Verify | Required track/size/metadata checks and optional quality gates | Duration tolerance, loudness/peak limits, SSIM floor, custom VMAF floors, clip and frame sampling |
| Schedule & replace | Automatic queueing, time window, automatic replacement, output destination and overwrite behaviour | No additional hidden page; the consequences remain beside the controls |

Only controls applicable to the library's media type and processing mode are exposed.
Remux keeps container and track decisions but hides video re-encoding controls.
Track cleanup leads to language selection. Mixed libraries retain their audio-only
and image controls. Settings remain stored when a different stage is opened.

## Controls and help

- Named radio choices replace the video preset slider. Queue priority uses a named
  select; genuine ranges such as image quality retain sliders and numeric readouts.
- Existing `InfoTip` behaviour supports hover, keyboard focus, tap and Escape.
  Previously bare labels now explain name/type changes, sampling, thresholds, schedules,
  output destinations and the required verification gates. Tooltips have specific
  accessible names; fields have independent names so help text does not pollute them.
- Links keep the shared card hover lift and deeper shadow. Form cards deepen their
  shadow on hover or focus without transforming their tooltip-containing blocks.
  Reduced motion removes transitions. The application theme owns all colours.
- English and all eight translated locales include the navigation and added help.
  Preset descriptions no longer refer to the removed video slider.

## Comparative and second-order review

The old editor mixed identity, everyday policy and specialist controls in one long
form; eligibility and output transformations shared an Advanced drawer. The new
hierarchy keeps source selection separate from output changes, and gives quality
strategy its own focused page so Encode does not become another long form.

The review checked consequences beyond the initial overview:

- App page keys identify the library editor, not its child route. Breadcrumbs,
  stage links and browser history retain the same draft. Deep links reload their
  destination. Navigation out of the editor still protects unsaved changes.
- Custom settings are counted on their containing navigation cards, including the
  overview. A preset's own audio bitrate is not reported as a custom override.
- Invalid hidden fields disable Save and have links back to the relevant page.
  Required folder/name validation applies across stages, including new libraries.
- Save operates on the whole draft. While its request is pending, controls are
  disabled to prevent edits being lost and duplicate submissions. Stage navigation
  still works while the request finishes.
- The embedded Setup editor uses local navigation and the same fields without
  changing the setup route. Calibration remains reachable from Encode.
- Visual inspection found that API `Tv` and option `TV` could leave the media-type
  select blank. The editor now matches the server's available value case-insensitively.
- No form binding was lost in the extraction. Existing payload conversion, defaults,
  backend verification and replacement logic remain in place. No migration is needed.

## Final width and theme polish

All fourteen workflow rooms retain the shared 1,152px content width on wide
screens and the same theme-token card gradients in light and dark mode. Regression
checks exercise both themes, hover shadow changes and horizontal containment.
Form labels and 44px controls now match Settings; card textures and hover lift
remain intact. The advanced encoding introduction is shorter, and every locale
clarifies that changes take effect when saved.

The final review also corrected Candidates/Excluded headings that retained a
settings-room title, and removed the duplicate audio navigation card in track
cleanup mode. Mobile and WebKit checks cover the larger control targets.

## Validation

- Backend: warnings-as-errors build passed with zero warnings/errors; 2,019 tests passed.
- Frontend: Svelte/TypeScript checks, eight-locale audit and 24 unit tests.
- Chromium: **125 tests passed** in the full application suite, including workflow/history/draft/save tests,
  mode/media scoping, hidden validation, custom and legacy values, tooltips and reflow.
- WebKit: **43 tests passed** in the Library and Setup suites, including short landscape and translated tooltips.
- Visual inspection: dark/light desktop, resting/hovered cards, video and advanced
  controls, readable settled tooltips, mobile reflow, and keyboard navigation.
- Production build retains the pre-existing advisory about the main chunk exceeding
  500 kB. No backend policy changes or live media processing were needed for this UI work.
