<script lang="ts">
  import type { OptimisationResult } from '../api'
  import { formatAbsoluteDateTime, formatDateTime, formatSize, mediaTitle, sizeBarShares, sizeChangeStatus } from '../format'
  import { i18n } from '../i18n/i18n.svelte'
  import { router } from '../stores/ui.svelte'
  import DataTable from './DataTable.svelte'
  import PanelHeader from './PanelHeader.svelte'
  import SizeChange from './SizeChange.svelte'
  import Thumbnail from './Thumbnail.svelte'

  let { results }: { results: OptimisationResult[] | null } = $props()

  let now = $state(new Date())
  $effect(() => {
    // Relative times ("5 minutes ago") must age while the page is open.
    const timer = setInterval(() => (now = new Date()), 60_000)
    return () => clearInterval(timer)
  })
</script>

<section class="results card @container flex h-full flex-col overflow-clip" aria-labelledby="recent-results-heading">
  <PanelHeader as="h2" id="recent-results-heading" title={i18n.m.dashboard.recent_results} hint={i18n.m.dashboard.recent_results_hint} hintClass="hidden @min-[88rem]:inline">
    {#snippet trailing()}
      <button class="rounded text-xs font-semibold text-accent hover:underline focus-ring" onclick={() => router.go('/queue')}>
        {i18n.m.dashboard.queue_history}
      </button>
    {/snippet}
  </PanelHeader>

  <DataTable bare density="compact" rowCount={results?.length ?? 0} loading={results === null} labelledby="recent-results-heading">
    {#snippet head()}
      <tr>
        <th scope="col">{i18n.m.dashboard.col_title}</th>
        <th scope="col" class="hidden w-[12%] @min-[64rem]:table-cell">{i18n.m.dashboard.col_library}</th>
        <th scope="col" class="w-[30%] @min-[44rem]:w-[26%] @min-[64rem]:w-1/5">{i18n.m.dashboard.col_sizes}</th>
        <th scope="col" class="w-22 @min-[44rem]:w-24">{i18n.m.dashboard.col_size_change}</th>
        <th scope="col" class="hidden w-20 @min-[64rem]:table-cell">{i18n.m.dashboard.col_vmaf}</th>
        <th scope="col" class="hidden w-[10%] @min-[88rem]:table-cell">{i18n.m.dashboard.col_encoder}</th>
        <th scope="col" class="hidden w-[10%] @min-[88rem]:table-cell">{i18n.m.dashboard.col_where}</th>
        <th scope="col" class="hidden w-[14%] @min-[44rem]:table-cell @min-[64rem]:w-[11%]">{i18n.m.dashboard.col_finished}</th>
      </tr>
    {/snippet}
    {#each results ?? [] as result (result.jobId)}
      {@const title = mediaTitle(result.relativePath)}
      {@const status = sizeChangeStatus(result.sourceSizeBytes, result.outputSizeBytes)}
      {@const shares = sizeBarShares(result.sourceSizeBytes, result.outputSizeBytes)}
      <tr>
        <td>
          <div class="flex min-w-0 items-center gap-3">
            <Thumbnail mediaFileId={result.mediaFileId} />
            <span class="min-w-0">
              <span class="block truncate font-semibold text-ink">{title.primary ?? i18n.m.dashboard.unnamed_file}</span>
              {#if title.episode || title.secondary}
                <span class="block truncate text-xs text-ink-3">{#if title.episode}<span class="tabular-nums">{title.episode}</span>{' '}{/if}{title.secondary ?? ''}</span>
              {/if}
            </span>
          </div>
        </td>
        <td class="hidden truncate @min-[64rem]:table-cell">{result.libraryName ?? '—'}</td>
        <td>
          <div class="flex min-w-0 flex-col gap-1.5">
            <span class="truncate text-xs tabular-nums">{formatSize(result.sourceSizeBytes)} → {formatSize(result.outputSizeBytes)}</span>
            <!-- The bar is measured against whichever of the two files is the larger one, so a
                 file that grew shows the part that overflows the source instead of a full bar
                 that reads as a saving. -->
            <span class="block h-1.5 overflow-hidden rounded-full bg-[var(--raised)]" aria-hidden="true">
              {#if shares !== null && status === 'grew'}
                <span class="flex h-full">
                  <span class="block h-full rounded-l-full bg-ink-4" style="width: {shares.source}%"></span>
                  <span class="block h-full rounded-r-full bg-warn" style="width: {shares.output - shares.source}%"></span>
                </span>
              {:else if shares !== null}
                <span class="block h-full rounded-full {status === 'saved' ? 'bg-ok' : 'bg-ink-4'}" style="width: {shares.output}%"></span>
              {/if}
            </span>
          </div>
        </td>
        <td class="whitespace-nowrap font-semibold tabular-nums">
          <SizeChange sourceSizeBytes={result.sourceSizeBytes} outputSizeBytes={result.outputSizeBytes} />
        </td>
        <td class="hidden whitespace-nowrap tabular-nums @min-[64rem]:table-cell">{result.vmafHarmonicMean != null ? result.vmafHarmonicMean.toFixed(1) : '—'}</td>
        <td class="hidden truncate font-mono text-xs @min-[88rem]:table-cell">{result.videoEncoder ?? '—'}</td>
        <td class="hidden truncate text-ink-3 @min-[88rem]:table-cell">{result.workerName ?? i18n.m.dashboard.this_server}</td>
        <td class="hidden truncate text-ink-3 @min-[44rem]:table-cell"><time datetime={result.finishedAt} title={formatAbsoluteDateTime(result.finishedAt, i18n.locale)}>{formatDateTime(result.finishedAt, now, i18n.locale)}</time></td>
      </tr>
    {/each}
    {#snippet loadingState()}<div class="h-40 w-full animate-pulse rounded-lg bg-[var(--lit)]" aria-hidden="true"></div><span class="sr-only">{i18n.m.common.loading_short}</span>{/snippet}
    {#snippet empty()}{i18n.m.dashboard.recent_results_empty}{/snippet}
  </DataTable>
</section>

<style>
  /* The poster is a recognition aid; on a narrow card it gives way to the title. Columns
     appear as the panel widens, not as the window does: the same panel is narrow in a laptop's
     two-column grid and wide across a 2560 px screen. */
  @container (min-width: 44rem) {
    .results :global([data-thumbnail]) { width: 2.75rem; height: 4rem; }
  }
</style>
