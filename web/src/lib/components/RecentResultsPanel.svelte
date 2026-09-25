<script lang="ts">
  import type { OptimisationResult } from '../api'
  import { formatRelative, formatSize, mediaTitle, savedPercent } from '../format'
  import { i18n } from '../i18n/i18n.svelte'
  import { router } from '../stores/ui.svelte'
  import Thumbnail from './Thumbnail.svelte'

  let { results }: { results: OptimisationResult[] | null } = $props()

  let now = $state(new Date())
  $effect(() => {
    // Relative times ("5 minutes ago") must age while the page is open.
    const timer = setInterval(() => (now = new Date()), 60_000)
    return () => clearInterval(timer)
  })
</script>

<section class="results card flex h-full flex-col" aria-labelledby="recent-results-heading">
  <div class="flex flex-wrap items-center gap-3 border-b border-line px-4 py-2.5">
    <h2 id="recent-results-heading" class="label mb-0">{i18n.m.dashboard.recent_results}</h2>
    <span class="results-hint text-xs text-ink-3">{i18n.m.dashboard.recent_results_hint}</span>
    <button class="ml-auto rounded text-xs font-semibold text-accent hover:underline focus-ring" onclick={() => router.go('/queue')}>
      {i18n.m.dashboard.queue_history}
    </button>
  </div>

  {#if results === null}
    <div class="p-4" aria-hidden="true"><div class="h-40 animate-pulse rounded-lg bg-[var(--lit)]"></div></div>
  {:else if results.length === 0}
    <p class="px-4 py-5 text-sm text-ink-3">{i18n.m.dashboard.recent_results_empty}</p>
  {:else}
    <div class="results-table" role="table" aria-labelledby="recent-results-heading">
      <div class="results-row results-head" role="row">
        <span role="columnheader">{i18n.m.dashboard.col_title}</span>
        <span role="columnheader" class="col-library">{i18n.m.dashboard.col_library}</span>
        <span role="columnheader">{i18n.m.dashboard.col_sizes}</span>
        <span role="columnheader">{i18n.m.dashboard.col_saved}</span>
        <span role="columnheader" class="col-vmaf">{i18n.m.dashboard.col_vmaf}</span>
        <span role="columnheader" class="col-encoder">{i18n.m.dashboard.col_encoder}</span>
        <span role="columnheader" class="col-where">{i18n.m.dashboard.col_where}</span>
        <span role="columnheader" class="col-finished">{i18n.m.dashboard.col_finished}</span>
      </div>
      {#each results as result (result.jobId)}
        {@const title = mediaTitle(result.relativePath)}
        {@const kept = result.sourceSizeBytes > 0 ? Math.min(100, (result.outputSizeBytes / result.sourceSizeBytes) * 100) : 100}
        <div class="results-row" role="row">
          <span role="cell" class="flex min-w-0 items-center gap-3">
            <Thumbnail mediaFileId={result.mediaFileId} size="sm" />
            <span class="min-w-0">
              <span class="block truncate text-sm font-semibold text-ink">{title.primary ?? i18n.m.dashboard.unnamed_file}</span>
              {#if title.episode || title.secondary}
                <span class="block truncate text-xs text-ink-3">{#if title.episode}<span class="font-mono">{title.episode}</span>{' '}{/if}{title.secondary ?? ''}</span>
              {/if}
            </span>
          </span>
          <span role="cell" class="col-library truncate text-sm text-ink-2">{result.libraryName ?? '—'}</span>
          <span role="cell" class="flex min-w-0 flex-col gap-1.5">
            <span class="truncate font-mono text-xs text-ink-2">{formatSize(result.sourceSizeBytes)} → {formatSize(result.outputSizeBytes)}</span>
            <span class="block h-1.5 overflow-hidden rounded-full bg-[var(--raised)]" aria-hidden="true"><span class="block h-full rounded-full bg-accent" style="width: {kept}%"></span></span>
          </span>
          <span role="cell" class="font-mono text-sm font-semibold text-ok">−{savedPercent(result.sourceSizeBytes, result.outputSizeBytes)}%</span>
          <span role="cell" class="col-vmaf font-mono text-sm text-ink-2">{result.vmafHarmonicMean != null ? result.vmafHarmonicMean.toFixed(1) : '—'}</span>
          <span role="cell" class="col-encoder truncate font-mono text-xs text-ink-2">{result.videoEncoder ?? '—'}</span>
          <span role="cell" class="col-where truncate text-sm text-ink-3">{result.workerName ?? i18n.m.dashboard.this_server}</span>
          <span role="cell" class="col-finished truncate text-sm text-ink-3" title={new Date(result.finishedAt).toLocaleString(i18n.locale)}>{formatRelative(result.finishedAt, now, i18n.locale)}</span>
        </div>
      {/each}
    </div>
  {/if}
</section>

<style>
  /* Columns appear as the panel widens, not as the window does: the same panel is narrow in a
     laptop's two-column grid and wide across a 2560 px screen. */
  .results { container-type: inline-size; }
  .results-row {
    display: grid;
    grid-template-columns: minmax(0, 2.2fr) minmax(0, 1.4fr) 4rem;
    align-items: center;
    gap: 1rem;
    padding: 0.55rem 1rem;
    border-top: 1px solid var(--divide-soft);
  }
  .results-head {
    border-top: 0;
    padding-block: 0.6rem;
    font-size: 10.5px;
    font-weight: 600;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--ink-3);
  }
  .col-library, .col-vmaf, .col-encoder, .col-where, .col-finished, .results-hint { display: none; }
  @container (min-width: 44rem) {
    .results-row { grid-template-columns: minmax(0, 2.2fr) minmax(0, 1.4fr) 4rem minmax(0, 0.8fr); }
    .col-finished { display: block; }
  }
  @container (min-width: 64rem) {
    .results-row { grid-template-columns: minmax(0, 2.2fr) minmax(0, 0.8fr) minmax(0, 1.4fr) 4rem 3.5rem minmax(0, 0.8fr); }
    .col-library, .col-vmaf { display: block; }
    .col-encoder { display: none; }
  }
  @container (min-width: 88rem) {
    .results-row { grid-template-columns: minmax(0, 2.4fr) minmax(0, 0.8fr) minmax(0, 1.5fr) 4rem 3.5rem minmax(0, 0.9fr) minmax(0, 0.9fr) minmax(0, 0.8fr); }
    .col-encoder, .col-where, .results-hint { display: block; }
  }
</style>
