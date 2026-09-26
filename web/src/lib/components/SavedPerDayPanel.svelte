<script lang="ts">
  import type { DailySaving } from '../api'
  import { formatSize } from '../format'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { savingsBars } from '../savings-chart'

  let { days }: { days: DailySaving[] | null } = $props()

  let chart = $derived(days ? savingsBars(days) : null)
  // The day under the pointer, read out in the header rather than in a tooltip: a browser tooltip
  // takes a second to appear and covers the bars beside the one being read.
  let hovered = $state<number | null>(null)
  let hoveredBar = $derived(chart && hovered !== null ? (chart.bars[hovered] ?? null) : null)

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

  // Which day the pointer is over, from where it is across the plot: every day is an equal slice.
  function pointAt(event: PointerEvent) {
    if (!chart) return
    const plot = (event.currentTarget as HTMLElement).getBoundingClientRect()
    const index = Math.floor(((event.clientX - plot.left) / plot.width) * chart.bars.length)
    hovered = Math.min(chart.bars.length - 1, Math.max(0, index))
  }

  // A mark's label sits centred under its day, except today's, which ends at the chart's edge.
  function markPosition(index: number, count: number): string {
    return index === count - 1 ? 'right: 0' : `left: ${((index + 0.5) / count) * 100}%; transform: translateX(-50%)`
  }
</script>

<section class="card flex h-full flex-col" aria-labelledby="saved-per-day-heading">
  <div class="flex flex-wrap items-center gap-3 border-b border-line px-4 py-2.5">
    <h2 id="saved-per-day-heading" class="label mb-0">{i18n.m.dashboard.saved_per_day}</h2>
    {#if chart && chart.totalBytes > 0}
      <span class="ml-auto font-mono text-xs {hoveredBar ? 'text-ink' : 'text-ink-3'}" aria-hidden={hoveredBar ? 'true' : undefined}>
        {hoveredBar ? dayDetail(hoveredBar) : summary}
      </span>
    {/if}
  </div>

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

      <!-- One figure for assistive tech; the bars themselves are drawing. -->
      <div class="saved-plot" role="img" aria-label={summary} onpointermove={pointAt} onpointerleave={() => (hovered = null)}>
        {#each chart.ticks as tick (tick)}
          <span class="saved-grid" class:saved-grid-base={tick === 0} style="bottom: {(tick / chart.scaleBytes) * 100}%" aria-hidden="true"></span>
        {/each}

        <div class="saved-bars">
          {#each chart.bars as bar, index (bar.date)}
            <div
              class="saved-day"
              class:saved-day-dim={hovered !== null && hovered !== index}
              title={dayDetail(bar)}
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
    font-family: var(--font-mono);
    font-size: 10.5px;
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
    transition: opacity 120ms ease;
  }
  .saved-day-dim {
    opacity: 0.45;
  }
  .saved-day-bar {
    width: 100%;
    min-height: 3px;
    border-radius: 4px 4px 1px 1px;
    background: linear-gradient(to top, color-mix(in oklab, var(--ok) 50%, transparent), var(--ok));
    box-shadow: inset 0 1px 0 color-mix(in oklab, var(--ok-strong) 60%, transparent);
  }
  .saved-day:hover .saved-day-bar {
    background: var(--ok);
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
