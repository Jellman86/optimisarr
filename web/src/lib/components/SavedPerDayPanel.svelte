<script lang="ts">
  import type { DailySaving } from '../api'
  import { formatSize } from '../format'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { savingsBars } from '../savings-chart'

  let { days }: { days: DailySaving[] | null } = $props()

  let chart = $derived(days ? savingsBars(days) : null)

  function shortDate(date: string): string {
    return new Date(`${date}T12:00:00`).toLocaleDateString(i18n.locale, { day: 'numeric', month: 'short' })
  }

  function barLabel(bar: DailySaving): string {
    return t(i18n.m.dashboard.saved_on_day, { date: shortDate(bar.date), size: formatSize(bar.bytesSaved) })
  }

  let summary = $derived(
    chart && days
      ? t(i18n.m.dashboard.saved_per_day_summary, {
          days: days.length,
          size: formatSize(chart.totalBytes),
        })
      : '',
  )
</script>

<section class="card flex h-full flex-col" aria-labelledby="saved-per-day-heading">
  <div class="flex flex-wrap items-center gap-3 border-b border-line px-4 py-2.5">
    <h2 id="saved-per-day-heading" class="label mb-0">{i18n.m.dashboard.saved_per_day}</h2>
    {#if chart && chart.totalBytes > 0}<span class="ml-auto font-mono text-xs text-ink-3">{summary}</span>{/if}
  </div>

  {#if !chart || !days}
    <div class="flex-1 p-4" aria-hidden="true"><div class="h-full min-h-32 animate-pulse rounded-lg bg-[var(--lit)]"></div></div>
  {:else if chart.totalBytes === 0}
    <p class="px-4 py-5 text-sm text-ink-3">{t(i18n.m.dashboard.saved_per_day_empty, { days: days.length })}</p>
  {:else}
    <div class="flex flex-1 gap-3 p-4">
      <div class="flex w-16 flex-none flex-col justify-between pb-6 text-right font-mono text-[10.5px] text-ink-3" aria-hidden="true">
        <span>{formatSize(chart.peakBytes)}</span>
        <span>0</span>
      </div>
      <div class="flex min-w-0 flex-1 flex-col gap-2">
        <!-- One figure for assistive tech; the bars themselves are drawing. -->
        <div class="flex min-h-32 flex-1 items-end gap-[3px] border-b border-line" role="img" aria-label={summary}>
          {#each chart.bars as bar (bar.date)}
            <div class="saved-day" title={bar.bytesSaved > 0 ? barLabel(bar) : shortDate(bar.date)}>
              {#if bar.fraction > 0}
                <div class="saved-day-bar" style="height: {bar.fraction * 100}%"></div>
              {/if}
            </div>
          {/each}
        </div>
        <div class="flex justify-between font-mono text-[10.5px] text-ink-3" aria-hidden="true">
          <span>{shortDate(chart.bars[0].date)}</span>
          <span>{i18n.m.dashboard.chart_today}</span>
        </div>
      </div>
    </div>
  {/if}
</section>

<style>
  .saved-day {
    display: flex;
    height: 100%;
    min-width: 0;
    flex: 1 1 0;
    align-items: flex-end;
    border-radius: 3px 3px 0 0;
  }
  .saved-day:hover {
    background: var(--lit);
  }
  .saved-day-bar {
    width: 100%;
    border-radius: 3px 3px 0 0;
    background: var(--ok);
    opacity: 0.85;
  }
</style>
