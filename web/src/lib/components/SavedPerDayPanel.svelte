<script lang="ts">
  import type { DailySaving } from '../api'
  import { formatSize } from '../format'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { savingsBars } from '../savings-chart'
  import PanelHeader from './PanelHeader.svelte'

  let { days }: { days: DailySaving[] | null } = $props()

  let chart = $derived(days ? savingsBars(days) : null)
  // The day under the pointer, or the day stepped to from the keyboard, read out beneath the chart
  // rather than in a tooltip: a browser tooltip takes a second to appear, covers the bars beside
  // the one being read, and never appears for a keyboard at all. The pointer wins while it is over
  // the plot, so moving the mouse never fights a day chosen with the arrows.
  let hovered = $state<number | null>(null)
  let selected = $state<number | null>(null)
  let active = $derived(hovered ?? selected)
  let activeBar = $derived(chart && active !== null ? (chart.bars[active] ?? null) : null)
  // Spoken only when a key moves the selection; the pointer and each 15-second refresh stay quiet.
  let announcement = $state('')

  function shortDate(date: string): string {
    return new Date(`${date}T12:00:00`).toLocaleDateString(i18n.locale, { day: 'numeric', month: 'short' })
  }

  function dayDetail(bar: DailySaving): string {
    if (bar.bytesSaved <= 0) return t(i18n.m.dashboard.saved_day_nothing, { date: shortDate(bar.date) })
    const template = bar.files === 1 ? i18n.m.dashboard.saved_day_files_one : i18n.m.dashboard.saved_day_files_other
    return t(template, { date: shortDate(bar.date), size: formatSize(bar.bytesSaved), count: bar.files.toLocaleString() })
  }

  let summary = $derived(
    chart && days
      ? t(i18n.m.dashboard.saved_per_day_summary, {
          days: days.length,
          size: formatSize(chart.totalBytes),
        })
      : '',
  )

  let peakBar = $derived.by(() => {
    const peak = chart?.peakBytes ?? 0
    return peak > 0 ? (chart?.bars.findLast((bar) => bar.bytesSaved === peak) ?? null) : null
  })

  // The first key lands on the latest day that saved something, since that is the day most
  // people are looking for; an all-quiet month starts on today.
  function startingDay(): number {
    if (!chart) return 0
    const latest = chart.bars.findLastIndex((bar) => bar.bytesSaved > 0)
    return latest >= 0 ? latest : chart.bars.length - 1
  }

  function stepDays(event: KeyboardEvent) {
    if (!chart || chart.bars.length === 0) return
    const last = chart.bars.length - 1
    const current = selected === null ? null : Math.min(last, selected)
    let next: number | null
    switch (event.key) {
      case 'ArrowLeft': next = current === null ? startingDay() : Math.max(0, current - 1); break
      case 'ArrowRight': next = current === null ? startingDay() : Math.min(last, current + 1); break
      case 'Home': next = 0; break
      case 'End': next = last; break
      case 'Escape':
        if (current === null) return
        next = null
        break
      default: return
    }
    event.preventDefault()
    hovered = null
    selected = next
    announcement = next === null ? '' : dayDetail(chart.bars[next])
  }

  function clearSelection() {
    selected = null
    announcement = ''
  }

  // Which day the pointer is over, from where it is across the plot: every day is an equal slice.
  function pointAt(event: PointerEvent) {
    if (!chart) return
    const plot = (event.currentTarget as HTMLElement).getBoundingClientRect()
    if (plot.width <= 0) return
    const index = Math.floor(((event.clientX - plot.left) / plot.width) * chart.bars.length)
    hovered = Math.min(chart.bars.length - 1, Math.max(0, index))
  }

  // A mark's label sits centred under its day, except today's, which ends at the chart's edge.
  function markPosition(index: number, count: number): string {
    return index === count - 1 ? 'right: 0' : `left: ${((index + 0.5) / count) * 100}%; transform: translateX(-50%)`
  }
</script>

<section class="card flex h-full flex-col" aria-labelledby="saved-per-day-heading">
  <PanelHeader as="h2" id="saved-per-day-heading" title={i18n.m.dashboard.saved_per_day} />

  {#if !chart || !days}
    <div class="flex-1 p-4" aria-hidden="true"><div class="h-full min-h-40 animate-pulse rounded-lg bg-[var(--lit)]"></div></div>
  {:else if chart.totalBytes === 0}
    <p class="px-4 py-5 text-sm text-ink-3">{t(i18n.m.dashboard.saved_per_day_empty, { days: days.length })}</p>
  {:else}
    <div class="saved-chart flex-1 px-4 pb-3 pt-4">
      <!-- The scale: a label per gridline, each at its line's height. -->
      <div class="saved-axis" aria-hidden="true">
        {#each chart.ticks as tick (tick)}
          <span class="saved-tick" style="bottom: {(tick / chart.scaleBytes) * 100}%">{tick === 0 ? '0' : formatSize(tick)}</span>
        {/each}
      </div>

      <!-- An interactive slider over the days so screen readers enter focus mode for Left/Right
           arrow stepping instead of swallowing arrows in browse mode. -->
      <div
        class="saved-plot rounded-sm focus-ring"
        role="slider"
        aria-label={summary}
        aria-describedby="saved-per-day-hint"
        aria-valuemin={1}
        aria-valuemax={chart.bars.length}
        aria-valuenow={Math.min(chart.bars.length, (selected ?? startingDay()) + 1)}
        aria-valuetext={activeBar ? dayDetail(activeBar) : summary}
        tabindex="0"
        onkeydown={stepDays}
        onblur={clearSelection}
        onpointermove={pointAt}
        onpointerleave={() => (hovered = null)}
      >
        {#each chart.ticks as tick (tick)}
          <span class="saved-grid" class:saved-grid-base={tick === 0} style="bottom: {(tick / chart.scaleBytes) * 100}%" aria-hidden="true"></span>
        {/each}

        <div class="saved-bars">
          {#each chart.bars as bar, index (bar.date)}
            <div
              class="saved-day"
              class:saved-day-dim={active !== null && active !== index}
              class:saved-day-active={active === index}
              class:saved-day-selected={selected === index}
            >
              {#if bar.fraction > 0}
                <div class="saved-day-bar" style="height: {bar.fraction * 100}%"></div>
              {:else}
                <div class="saved-day-none"></div>
              {/if}
            </div>
          {/each}
        </div>

        {#if chart.averageFraction > 0}
          <!-- The average per day, quiet days included, so one huge day reads as the outlier it is. -->
          <div class="saved-average" style="bottom: {chart.averageFraction * 100}%" aria-hidden="true">
            <span class="saved-average-label">{t(i18n.m.dashboard.saved_per_day_average, { size: formatSize(chart.averageBytes) })}</span>
          </div>
        {/if}
      </div>

      <div class="saved-dates" aria-hidden="true">
        {#each chart.weekMarks as index (index)}
          <span class="saved-date" style={markPosition(index, chart.bars.length)}>
            {index === chart.bars.length - 1 ? i18n.m.dashboard.chart_today : shortDate(chart.bars[index].date)}
          </span>
        {/each}
      </div>
    </div>

    <!-- The chart's figures in words, always on screen, so nothing here needs a pointer to be read. -->
    <div class="hairline-t flex flex-wrap items-baseline gap-x-4 gap-y-1 px-4 py-2.5 text-xs tabular-nums text-ink-3" data-saved-summary>
      <span class={activeBar ? 'font-medium text-ink' : 'text-ink-2'} data-saved-readout>{activeBar ? dayDetail(activeBar) : summary}</span>
      {#if chart.averageBytes > 0}<span>{t(i18n.m.dashboard.saved_per_day_average, { size: formatSize(chart.averageBytes) })}</span>{/if}
      {#if peakBar}<span>{t(i18n.m.dashboard.saved_per_day_peak, { date: shortDate(peakBar.date), size: formatSize(peakBar.bytesSaved) })}</span>{/if}
      <span id="saved-per-day-hint" class="sr-only">{i18n.m.dashboard.saved_per_day_step_hint}</span>
      <span class="sr-only" aria-live="polite" data-saved-announcement>{announcement}</span>
    </div>
  {/if}
</section>

<style>
  .saved-chart {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr);
    grid-template-rows: minmax(10rem, 1fr) auto;
    column-gap: 0.75rem;
    row-gap: 0.5rem;
  }
  .saved-axis {
    position: relative;
    min-width: 3.25rem;
  }
  .saved-tick,
  .saved-date,
  .saved-average-label {
    font-variant-numeric: tabular-nums;
    font-size: 0.6875rem;
    line-height: 1;
    color: var(--ink-3);
    white-space: nowrap;
  }
  .saved-tick {
    position: absolute;
    right: 0;
    transform: translateY(50%);
  }
  .saved-plot {
    position: relative;
    min-height: 10rem;
  }
  .saved-grid {
    position: absolute;
    left: 0;
    right: 0;
    border-top: 1px dashed var(--divide-soft);
  }
  .saved-grid-base {
    border-top: 1px solid var(--divide);
  }
  .saved-bars {
    position: absolute;
    inset: 0;
    display: flex;
    align-items: flex-end;
    gap: 3px;
  }
  .saved-day {
    display: flex;
    height: 100%;
    min-width: 0;
    flex: 1 1 0;
    align-items: flex-end;
    transition: opacity var(--motion-fast) var(--motion-ease-standard);
  }
  .saved-day-dim {
    opacity: 0.45;
  }
  /* The day chosen from the keyboard is a lit column, the same fill as any other selection, so it
     stays findable on a quiet day whose bar is only a hairline. */
  .saved-day-selected {
    border-radius: 4px;
    background: var(--lit);
  }
  .saved-day-active .saved-day-none {
    background: var(--ink-3);
  }
  .saved-day-bar {
    width: 100%;
    min-height: 3px;
    border-radius: 4px 4px 1px 1px;
    background: linear-gradient(to top, color-mix(in oklab, var(--ok) 50%, transparent), var(--ok));
    box-shadow: inset 0 1px 0 color-mix(in oklab, var(--ok-strong) 60%, transparent);
  }
  .saved-day:hover .saved-day-bar,
  .saved-day-active .saved-day-bar {
    background: var(--ok);
    box-shadow: inset 0 2px 0 var(--ok-strong);
  }
  /* A day that saved nothing still has its place on the axis, so gaps read as days, not as a gap in the data. */
  .saved-day-none {
    width: 100%;
    height: 2px;
    border-radius: 1px;
    background: var(--divide-soft);
  }
  .saved-average {
    position: absolute;
    left: 0;
    right: 0;
    border-top: 1px dashed var(--ink-4);
    pointer-events: none;
  }
  .saved-average-label {
    position: absolute;
    right: 0;
    bottom: 3px;
    padding: 0 0.25rem;
    border-radius: 3px;
    background: var(--panel, transparent);
    color: var(--ink-2);
  }
  .saved-dates {
    position: relative;
    grid-column: 2;
    height: 1em;
  }
  .saved-date {
    position: absolute;
    top: 0;
  }
</style>
