<script lang="ts">
  // The eligible/skipped candidate table shared by the fleet-wide Candidates page and the
  // per-library Candidates tab in the Libraries workspace, so both show the same reasons,
  // badges, and responsive columns. Read-only: enqueue stays a library-level action.
  import type { Candidate } from '../api'
  import { formatSize } from '../format'
  import PreviewCompare from './PreviewCompare.svelte'
  import Thumbnail from './Thumbnail.svelte'
  import { i18n, t } from '../i18n/i18n.svelte'

  let { candidates, scoped = false }: { candidates: Candidate[]; scoped?: boolean } = $props()

  // The file currently open in the original-vs-encoded preview, if any.
  let previewing = $state<Candidate | null>(null)

  let show = $state<'all' | 'eligible' | 'skipped'>('all')

  let eligibleCount = $derived(candidates.filter((c) => c.eligible).length)
  let skippedCount = $derived(candidates.length - eligibleCount)
  let visible = $derived(
    show === 'eligible'
      ? candidates.filter((c) => c.eligible)
      : show === 'skipped'
        ? candidates.filter((c) => !c.eligible)
        : candidates,
  )

  // Render one page at a time so a large library (thousands of probed files) stays responsive; the
  // chips above still count the whole set. The page is clamped, so it stays valid as the data or
  // filter changes. Mirrors the Queue table's client-side paging.
  const CANDIDATE_PAGE_SIZE = 100
  let page = $state(1)
  let pageCount = $derived(Math.max(1, Math.ceil(visible.length / CANDIDATE_PAGE_SIZE)))
  let pageStart = $derived((Math.min(page, pageCount) - 1) * CANDIDATE_PAGE_SIZE)
  let pagedVisible = $derived(visible.slice(pageStart, pageStart + CANDIDATE_PAGE_SIZE))

  function selectShow(key: typeof show) {
    show = key
    page = 1
  }

  function goToPage(next: number) {
    page = Math.max(1, Math.min(next, pageCount))
  }
</script>

{#if candidates.length > 0}
  <div class="mb-4 flex flex-wrap gap-2">
    <button class="btn px-3 py-1 text-xs" class:btn-primary={show === 'all'} onclick={() => selectShow('all')}>
      {t(i18n.m.shared.all_count, { count: candidates.length })}
    </button>
    <button class="btn px-3 py-1 text-xs" class:btn-primary={show === 'eligible'} onclick={() => selectShow('eligible')}>
      {t(i18n.m.shared.eligible_count, { count: eligibleCount })}
    </button>
    <button class="btn px-3 py-1 text-xs" class:btn-primary={show === 'skipped'} onclick={() => selectShow('skipped')}>
      {t(i18n.m.shared.skipped_count, { count: skippedCount })}
    </button>
  </div>

  <div class="card overflow-x-auto">
    <table class="w-full text-sm">
      <thead class="border-b border-slate-200 text-left text-xs uppercase text-slate-500 dark:border-slate-700 dark:text-slate-400">
        <tr>
          <th class="px-4 py-3">{i18n.m.shared.col_status}</th>
          <th class="px-4 py-3">{i18n.m.shared.col_file}</th>
          <th class="hidden px-4 py-3 sm:table-cell">{i18n.m.shared.col_size}</th>
          <th class="hidden px-4 py-3 md:table-cell">{i18n.m.shared.col_codec}</th>
          <!-- The rule profile is constant within one library, so the column is redundant when scoped. -->
          {#if !scoped}<th class="hidden px-4 py-3 lg:table-cell">{i18n.m.shared.col_profile}</th>{/if}
          <th class="px-4 py-3">{i18n.m.shared.col_reason}</th>
          <th class="px-4 py-3"></th>
        </tr>
      </thead>
      <tbody class="divide-y divide-slate-100 dark:divide-slate-800">
        {#each pagedVisible as candidate (candidate.mediaFileId)}
          <tr class="text-slate-700 dark:text-slate-300">
            <td class="px-4 py-2">
              {#if candidate.eligible}
                <span class="badge bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400">{i18n.m.shared.eligible}</span>
              {:else}
                <span class="badge bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400">{i18n.m.shared.skipped}</span>
              {/if}
            </td>
            <td class="px-4 py-2">
              <div class="flex items-center gap-3">
                <Thumbnail mediaFileId={candidate.mediaFileId} alt={candidate.relativePath} />
                <span class="max-w-[44vw] truncate font-mono text-xs sm:max-w-xs" title={candidate.relativePath}>
                  {#if candidate.mediaKind === 'Audio' || candidate.mediaKind === 'Image'}
                    <span class="badge mr-1 bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400">{candidate.mediaKind === 'Audio' ? i18n.m.shared.media_audio : i18n.m.shared.media_image}</span>
                  {/if}{candidate.relativePath}
                </span>
              </div>
            </td>
            <td class="hidden px-4 py-2 sm:table-cell">{formatSize(candidate.sizeBytes)}</td>
            <td class="hidden px-4 py-2 md:table-cell">
              {candidate.codec ?? '—'}{#if candidate.isHdr}<span class="badge ml-1 bg-amber-100 text-amber-700 dark:bg-amber-900/40 dark:text-amber-400">HDR</span>{/if}
            </td>
            <!-- The rule profile is a video preset; it is meaningless for audio/image files,
                 which are governed by their own audio/image rules. -->
            {#if !scoped}
              <td class="hidden px-4 py-2 text-xs lg:table-cell">{candidate.mediaKind === 'Audio' || candidate.mediaKind === 'Image' ? '—' : candidate.profile}</td>
            {/if}
            <td class="px-4 py-2 text-xs text-slate-500 dark:text-slate-400">{candidate.reason}</td>
            <td class="px-4 py-2 text-right">
              {#if candidate.eligible}
                <button class="btn px-3 py-1 text-xs" onclick={() => (previewing = candidate)}>{i18n.m.shared.preview}</button>
              {/if}
            </td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>

  {#if previewing}
    <PreviewCompare
      mediaFileId={previewing.mediaFileId}
      mediaKind={previewing.mediaKind}
      relativePath={previewing.relativePath}
      onClose={() => (previewing = null)}
    />
  {/if}
  <div class="mt-2 flex items-center justify-between text-xs text-slate-400">
    <span>
      {#if visible.length > 0}
        {visible.length === candidates.length
          ? t(i18n.m.shared.candidate_range_all, { start: (pageStart + 1).toLocaleString(), end: Math.min(pageStart + CANDIDATE_PAGE_SIZE, visible.length).toLocaleString(), total: visible.length.toLocaleString() })
          : t(i18n.m.shared.candidate_range_filtered, { start: (pageStart + 1).toLocaleString(), end: Math.min(pageStart + CANDIDATE_PAGE_SIZE, visible.length).toLocaleString(), visible: visible.length.toLocaleString(), total: candidates.length.toLocaleString() })}
      {:else}
        0
      {/if}
    </span>
    {#if pageCount > 1}
      <span class="flex items-center gap-2">
        <button class="btn px-2 py-1 text-xs" onclick={() => goToPage(page - 1)} disabled={page <= 1} aria-label={i18n.m.shared.previous_page}>‹</button>
        <span>{t(i18n.m.shared.page_of, { page: Math.min(page, pageCount).toLocaleString(), count: pageCount.toLocaleString() })}</span>
        <button class="btn px-2 py-1 text-xs" onclick={() => goToPage(page + 1)} disabled={page >= pageCount} aria-label={i18n.m.shared.next_page}>›</button>
      </span>
    {/if}
  </div>
{:else}
  <div class="card p-8 text-center text-slate-500 dark:text-slate-400">
    {i18n.m.shared.candidates_empty}
  </div>
{/if}
