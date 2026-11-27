# Optimisarr: Fundamental UI Improvement Program

**Date:** 2026-11-25
**Baseline:** measured from `web/src/` and layout captures on `dev` (42 arbitrary font sizes, 89 `border-line` utilities, 165 `min-h-11` button overrides, 6 table implementations, sub-3:1 non-text control edges/switches/focus rings, and 0 visible Queue rows above the fold at 1440x900).
**Goal:** bring the UI to cohesive operational craft while staying 100% faithful to Optimisarr's calm, honest, safety-first ethos (`CLAUDE.md` §5).

The colour token system, safety copy, and automated test discipline are already stronger than most operational tools. What holds the UI back is drift, not taste: individual pages re-implement their own tables, tabs, buttons, and type sizes, non-text control contrast drops below WCAG 3:1, and the busiest screens bury the actual work beneath summary chrome.

---

## 1. Honest baseline

### Protect these (already strong)

- **Token architecture:** ink, surface, and tone tokens in `web/src/app.css`, swapping cleanly between light and dark schemes. `web/src/lib/theme-tokens.test.ts` enforces zero hardcoded palette hues and colour-blind separation (Machado 2009 OKLab ΔE >= 8).
- **Stated visual grammar:** "a surface is a fill and an elevation, never a stroke" and "state is a lit fill, never a coloured edge." The rules are right; the implementation needs to obey them everywhere.
- **Safety copy and defaults:** Quarantine's explicit irreversibility copy, `ConfirmDialog` focusing Cancel, `SettingRow` requiring a plain sentence, and retention warning tags.
- **Library overview:** "What this library does" as generated plain-English sentences is the strongest screen in the app.
- **Engineering guardrails:** role-based Playwright e2e suite, 30-route layout audit with axe, nine locales with completeness checks, native `<dialog>` focus handling, request-generation guards, and non-shifting artwork.

### Structural gaps

1. **No scale tokens, only colour tokens.** Arbitrary font sizes (`text-[11px]` x32, plus 10, 10.5, 11.5, 13, 17, 18px, and scoped `.6875rem`/`.8125rem`/`.9375rem`), ~12 distinct radii, 165 `min-h-11` button overrides on top of a 36px `.btn`, `.label` overridden in 3 pages, `.input` overridden in 2, and 32 files carrying scoped `<style>` blocks.
2. **Primitive drift.** Six table implementations (shared `.table-head` used by one), five tab/filter looks (`.segmented` used once), three chip styles, two setting-row patterns, two focus-ring implementations, and dead code (`BottomSheet.svelte`, Sidebar's "coming soon" branch).
3. **Border rule broken in practice.** 149 `border` utilities (89 `border-line`). Quality reports nest three surface levels deep; `Banner.svelte` renders every message as a full `--lift-2` card.
4. **"Passed" and "clickable" share the same blue.** `--ok` and `--accent` are both `#0062cc` in light mode and 5.4 ΔE apart in dark. Links, Replace buttons, "Healthy", "Eligible", saved percentages, and done vs current stages in `JobStages.svelte` all wear the same blue. Because green/teal fails the protanopia floor against `--bad`, the fix is form, not hue.
5. **Non-text contrast below WCAG 3:1 (unchecked by axe).**
   - Off switch track on panel: 1.68:1 light, 1.73:1 dark (target >= 3:1)
   - Input edge on panel: 1.42:1 light, 1.72:1 dark (target >= 3:1)
   - Focus ring (light): 2.41:1 on ground, 2.64:1 on panel (target >= 3:1)
6. **Queue buries the queue.** At 1440x900, zero job rows appear above the fold. The running job repeats three times (strip, hero, sidebar `NowEncoding`), and lane counts duplicate the strip.
7. **Navigation depth in configuration.** Library Video stacks five navigation layers and paints the active pill with the primary-action fill. Settings uses seven accent tile cards for a simple list and repeats headings three times inside each room. Libraries cards give every card a primary Scan button.
8. **Evidence is the smallest thing on the page.** `VerificationChecks.svelte` renders core proof at `text-xs` with typed check/cross characters; the four quality reports use inconsistent bordered cards and mix mono and sans for numbers.
9. **Interaction craft.** No global shortcuts, row navigation, or sortable columns; Inventory lacks a search field even though `/api/inventory` accepts `search`; Dashboard and Queue rely on 10 to 15s polling; static `document.title` and no focus move on navigation; 124 `title=` attributes hiding essential text; 14 raw `toLocaleString()` dates with seconds.
10. **Wide and narrow extremes.** At 2560px, overview sentences span ~1,500px with Edit across the screen. On phones, strip and title take a third of the viewport, In-flight leaves orphan values, and Diagnostics exposes native date inputs.
11. **Cohesion and regression visibility.** `Sidecar.svelte` duplicates `.status-strip` and `.job-stages`; `PosterGlow` tints four surfaces; eager page loading triggers the 500 kB chunk advisory; dark-only docs captures.

---

## 2. North-star design grammar

1. **Three surface tiers max:** ground, panel, and one inner tier (sunken well or raised key). A panel inside a panel becomes a hairline-separated section.
2. **No strokes around containers:** `--divide-soft` divides rows inside a surface; strokes appear only on inputs and artwork (`border-line` budget -> 0).
3. **4px spacing rhythm:** named steps 4, 8, 12, 16, 24, 32. Panel padding is 16 (compact) or 24 (form); gap between panels is 16; page header to content is 24.
4. **Six type roles, zero arbitrary sizes:**
   - Display (max one per page): 28/32 bold
   - Title: 17/24 semibold
   - Body: 14/20 regular
   - Label: 13/18 semibold
   - Caption: 12/16 regular
   - Micro (chips and axes only): 11/14 regular
   - Tabular numerals for metrics; monospace only for machine strings (paths, encoder IDs, commands), never quantities.
5. **Tone discipline:** blue text means clickable; passed is ink text beside a filled ok glyph (or a `tone-ok` chip); live is accent with motion or progress; amber means look; pink means failed or destructive; graphite means fact. Never colour without a glyph or word.
6. **One accent fill per view:** reserved for the single action that advances the work. Active tabs, selected rows, and "on" states use lit fills.
7. **Controls:** three heights (28 inline in a row, 36 default, 44 in forms/dialogs and automatically on `pointer: coarse`); four radii (pill for buttons/chips, 8 for fields, 14 for panels, 18 for dialogs).
8. **Disclosure ladder:** always-visible sentence -> warning tag -> inline "More about this" -> child page. Dialogs inspect one item; confirmations only for destructive or bulk actions; no bottom sheets.
9. **Motion:** three tokens, `--motion-fast` 120ms (hover/press), `--motion-base` 180ms (layout/disclosure) and `--motion-slow` 300ms (entering surfaces), with standard, enter and exit curves. Colour never fades on availability changes; nothing loops except live indicators; reduced motion keeps state without movement.
10. **Data:** relative dates under 24 hours, absolute without seconds after; sizes always show before, after, and percent together; missing values use an accessible "not available" dash.

---

## 3. Phased improvement program

### Phase 0: System and guardrails
- **0.1 Drift budgets and visual baselines (P0):** ratcheting budgets in `theme-tokens.test.ts` (arbitrary `text-[...]`, `border-line`, `min-h-11` button overrides, scoped CSS lines); optional CI screenshot baselines for key routes.
- **0.2 Scale tokens and control sizes (P0):** `@theme` type, radius, and spacing tokens; `.btn-sm`, `.btn`, `.btn-lg` + coarse-pointer 44px rule; single `.input` height and single `.label` definition.
- **0.3 Tone semantics and non-text contrast (P0):** separate passed/verified from clickable blue using ink + filled ok glyph across `JobStages`, `SavingsPanel`, `Inventory`, `RecentResultsPanel`, `Sidecar`; raise `--field-edge`, off-switch track, and `--focus` to >= 3:1 in both schemes; unify `focus-ring`.
- **0.4 Shared primitives (P0, 3 PRs):**
  - (a) `Segmented`, `FilterBar`, `Chip`
  - (b) `DataTable` (sticky header, density, selection column, `aria-sort`, empty/loading/error slots)
  - (c) `DetailDialog`, `PanelHeader`, flat `Banner`, `SettingRow` everywhere, remove unused `BottomSheet.svelte`.

### Phase 1: Shell and operational views
- **1.1 Shell and status strip (P0):** shared `PageHeader`, per-route `document.title`, focus moved to `h1` on route change, `StatusStrip` as sole global live readout with visible wrapping detail, hide `NowEncoding` on Queue and Dashboard.
- **1.2 Queue, list first (P0):** collapsible one-line lane chips, compact two-row active-job strip (48px poster, progress, stage tracker), sticky filter/bulk bar, at least 6 job rows visible at 1440x900.
- **1.3 Dashboard hierarchy (P1):** "Needs you" leads when non-empty, Savings as the sole Display figure, labelled In-flight values, Fleet free-space header, keyboard day stepping and text summary on the chart.
- **1.4 Inventory and Quarantine lists (P1):** search field wired to `/api/inventory?search=`, sortable size and verdict, ~52px compact density, humane dates, sticky decision bar on Quarantine review.

### Phase 2: Configuration and inspection
- **2.1 Library editor navigation (P1):** flatten 5 layers to 3 (breadcrumb -> Settings/Candidates/Excluded tabs -> sticky left stage/section rail with lit fills), keep "What this library does" overview, cap row measure near 72ch with Edit adjacent, sticky dirty save bar.
- **2.2 Settings as a list (P1):** two grouped lists replacing 7 tile cards, body-sized state summaries, single room heading, `SettingRow` everywhere.
- **2.3 Evidence and job detail (P1):** unified `EvidenceList` and `MeasureReport` shell across all four quality reports; outcome-first job detail dialog with commands/diagnostics behind disclosures.
- **2.4 Libraries list and sidecar alignment (P2):** single primary action on Libraries list (Scan becomes default button); align `Sidecar.svelte` onto shared strip, stage, and chip primitives.

### Phase 3: Interaction, motion, and responsive excellence
- **3.1 Keyboard (P1):** `g`+letter navigation, `/` search focus, `j`/`k` row movement, `x` select, Enter open, `?` shortcuts sheet (all 9 locales), optional non-destructive route/library palette.
- **3.2 Realtime continuity (P1):** SignalR-driven list updates with polling fallback, keyed 180ms row transitions, quiet staleness/reconnecting indicator, sized skeletons.
- **3.3 Phone and accessibility completion (P1):** `DataTable` two-line phone rows, single-line phone status strip, sticky safe-area bottom bars, replace essential `title=` with visible text or `InfoTip`, 40%-expansion pseudo-locale audit.
- **3.4 Motion, brand, and bundle weight (P2):** motion tokens, restrict `PosterGlow` to detail headers, lazy-load pages to clear the 500 kB chunk advisory, regenerate the docs captures (dark; light on request).

---

## 4. Guardrails

- Never add a fourth surface tier, gradients, glass blur, or a new hue; solve hierarchy with spacing and type.
- Never trade operational density for empty space without an explicit reason.
- Never soften destructive copy or animate/optimistically render a verification result or replacement.
- Never let artwork carry state or shift layout.
- Never use blue text for anything that cannot be pressed.
- Never duplicate a shared primitive into a page `<style>` block.
- Never ship copy changes without updating all nine locales, or restyle an e2e-targeted class without updating the locator in the same PR.
- Never put destructive actions in a command palette or behind single-key shortcuts.
