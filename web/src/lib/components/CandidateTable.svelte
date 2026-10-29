<script lang="ts">
  // The eligible/skipped candidate table shared by the fleet-wide Candidates page and the
  // per-library Candidates tab in the Libraries workspace, so both show the same reasons,
  // badges, and responsive columns. Read-only: enqueue stays a library-level action.
  // Rows can be picked and excluded in bulk; the exclusion is the one per-file action the
  // library offers, and it is what the operator does with a set of unwanted files.
  import type { BatchFailure, Candidate } from '../api'
  import { api } from '../api'
  import { formatSize } from '../format'
  import { createSelection } from '../selection.svelte'
  import { i18n, plural, t } from '../i18n/i18n.svelte'
  import BatchResult from './BatchResult.svelte'
  import ConfirmDialog from './ConfirmDialog.svelte'
  import PreviewCompare from './PreviewCompare.svelte'
  import SelectionCheckbox from './SelectionCheckbox.svelte'
  import SelectionToolbar from './SelectionToolbar.svelte'
  import Thumbnail from './Thumbnail.svelte'

  let { candidates, scoped = false, onchanged }: {
    candidates: Candidate[]
    scoped?: boolean
    /**
     * Called with the files a bulk action excluded, so the owner can drop them from the list it
     * keeps. The owner is not asked to reload the whole list: an excluded file is never offered
     * again, so the two lists agree once the excluded rows are gone.
     */
    onchanged?: (excluded: number[]) => void
  } = $props()

  // The file currently open in the original-vs-encoded preview, if any.
  let previewing = $state<Candidate | null>(null)

  let show = $state<'all' | 'eligible' | 'skipped'>('all')

  // Files this table has just excluded. They leave the list at once, because an excluded file
  // is never offered for optimisation again — which is exactly what the server reports when
  // the list is reloaded.
  let excludedIds = $state<number[]>([])
  let selection = createSelection()
  let confirmOpen = $state(false)
  let busy = $state(false)
  let error = $state<string | null>(null)
  let outcome = $state<{ succeeded: number; failures: BatchFailure[] } | null>(null)

  let eligibleCount = $derived(candidates.filter((c) => c.eligible).length)
  let skippedCount = $derived(candidates.length - eligibleCount)
  let visible = $derived(
    show === 'eligible'
      ? candidates.filter((c) => c.eligible)
      : show === 'skipped'
        ? candidates.filter((c) => !c.eligible)
        : candidates,
  )
  // A file excluded in this session is no longer a candidate, whatever the chips say.
  let offered = $derived(visible.filter((c) => !excludedIds.includes(c.mediaFileId)))

  // Render one page at a time so a large library (thousands of probed files) stays responsive; the
  // chips above still count the whole set. The page is clamped, so it stays valid as the data or
  // filter changes. Mirrors the Queue table's client-side paging.
  const CANDIDATE_PAGE_SIZE = 100
  let page = $state(1)
  let pageCount = $derived(Math.max(1, Math.ceil(offered.length / CANDIDATE_PAGE_SIZE)))
  let pageStart = $derived((Math.min(page, pageCount) - 1) * CANDIDATE_PAGE_SIZE)
  let pagedVisible = $derived(offered.slice(pageStart, pageStart + CANDIDATE_PAGE_SIZE))

  // The bulk actions work on the rows the operator picked. A range is chosen over the rows on
  // screen; "select all" covers every row the current chip shows, pages included.
  let visibleIds = $derived(offered.map((c) => c.mediaFileId))
  let pagedIds = $derived(pagedVisible.map((c) => c.mediaFileId))

  function selectShow(key: typeof show) {
    show = key
    page = 1
  }

  function goToPage(next: number) {
    page = Math.max(1, Math.min(next, pageCount))
  }

  function toggleSelection(mediaFileId: number, extend: boolean) {
    selection.toggle(mediaFileId, pagedIds, extend)
  }

  function toggleSelectAll() {
    if (selection.isEverySelected(visibleIds)) selection.remove(visibleIds)
    else selection.selectAll(visibleIds)
  }

  async function excludeSelected() {
    if (busy || selection.isEmpty) return
    busy = true
    error = null
    try {
      const result = await api.batchExcludeFiles(selection.ids)
      outcome = { succeeded: result.succeeded.length, failures: result.failures }
      // The files that were excluded leave the list; the ones that were refused stay picked,
      // so the operator can act on them again.
      excludedIds = [...excludedIds, ...result.succeeded]
      selection.remove(result.succeeded)
      onchanged?.(result.succeeded)
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.shared.status_error
    } finally {
      busy = false
      confirmOpen = false
    }
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

  {#if error}<div class="card tone-bad mb-4 p-3 text-sm text-bad" role="alert">{error}</div>{/if}
  {#if outcome}
    <BatchResult
      title={i18n.m.shared.batch_exclude}
      succeeded={outcome.succeeded}
      failures={outcome.failures}
      ondismiss={() => (outcome = null)}
    />
  {/if}
  {#if !selection.isEmpty}
    <SelectionToolbar
      count={selection.count}
      total={candidates.length}
      limited={selection.isFull}
      onclear={() => selection.clear()}
    >
      {#snippet actions()}
        <button class="btn btn-ghost" onclick={() => (confirmOpen = true)} disabled={busy} title={i18n.m.shared.batch_exclude_title}>
          {i18n.m.shared.batch_exclude}
        </button>
      {/snippet}
    </SelectionToolbar>
  {/if}

  <div class="card overflow-x-auto">
    <table class="w-full text-sm">
      <thead class="border-b border-line text-left text-xs font-semibold text-ink-3">
        <tr>
          <th class="w-10 px-4 py-3">
            <SelectionCheckbox
              checked={selection.isEverySelected(visibleIds)}
              indeterminate={selection.isSomeSelected(visibleIds) && !selection.isEverySelected(visibleIds)}
              label={i18n.m.selection.select_all}
              onchange={toggleSelectAll}
            />
          </th>
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
      <tbody class="divide-y divide-line-soft">
        {#each pagedVisible as candidate (candidate.mediaFileId)}
          <tr class="text-ink-2">
            <td class="px-4 py-2">
              <SelectionCheckbox
                checked={selection.isSelected(candidate.mediaFileId)}
                label={t(i18n.m.selection.select_row, { name: candidate.relativePath })}
                onchange={(extend) => toggleSelection(candidate.mediaFileId, extend)}
              />
            </td>
            <td class="px-4 py-2">
              {#if candidate.eligible}
                <span class="badge tone-ok">{i18n.m.shared.eligible}</span>
              {:else}
                <span class="badge tone-muted">{i18n.m.shared.skipped}</span>
              {/if}
            </td>
            <td class="px-4 py-2">
              <div class="flex items-center gap-3">
                <Thumbnail mediaFileId={candidate.mediaFileId} alt={candidate.relativePath} />
                <span class="max-w-[44vw] truncate font-mono text-xs sm:max-w-xs" title={candidate.relativePath}>
                  {#if candidate.mediaKind === 'Audio' || candidate.mediaKind === 'Image'}
                    <span class="badge mr-1 tone-muted">{candidate.mediaKind === 'Audio' ? i18n.m.shared.media_audio : i18n.m.shared.media_image}</span>
                  {/if}{candidate.relativePath}
                </span>
              </div>
            </td>
            <td class="hidden px-4 py-2 sm:table-cell">{formatSize(candidate.sizeBytes)}</td>
            <td class="hidden px-4 py-2 md:table-cell">
              {candidate.codec ?? '—'}{#if candidate.isHdr}<span class="badge ml-1 tone-warn">HDR</span>{/if}
            </td>
            <!-- The rule profile is a video preset; it is meaningless for audio/image files,
                 which are governed by their own audio/image rules. -->
            {#if !scoped}
              <td class="hidden px-4 py-2 text-xs lg:table-cell">{candidate.mediaKind === 'Audio' || candidate.mediaKind === 'Image' ? '—' : candidate.profile}</td>
            {/if}
            <td class="px-4 py-2 text-xs text-ink-3">{candidate.reason}</td>
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

  <ConfirmDialog
    bind:open={confirmOpen}
    title={plural(selection.count, i18n.m.shared.batch_confirm_exclude_title_one, i18n.m.shared.batch_confirm_exclude_title_other, selection.count.toLocaleString())}
    confirmLabel={i18n.m.shared.batch_exclude}
    danger={true}
    busy={busy}
    onconfirm={excludeSelected}
    oncancel={() => (confirmOpen = false)}
  >
    {#snippet body()}
      <p>{i18n.m.shared.batch_confirm_exclude_body}</p>
    {/snippet}
  </ConfirmDialog>

  {#if previewing}
    <PreviewCompare
      mediaFileId={previewing.mediaFileId}
      mediaKind={previewing.mediaKind}
      relativePath={previewing.relativePath}
      onClose={() => (previewing = null)}
    />
  {/if}
  <div class="mt-2 flex items-center justify-between text-xs text-ink-4">
    <span>
      {#if offered.length > 0}
        {offered.length === candidates.length
          ? t(i18n.m.shared.candidate_range_all, { start: (pageStart + 1).toLocaleString(), end: Math.min(pageStart + CANDIDATE_PAGE_SIZE, offered.length).toLocaleString(), total: offered.length.toLocaleString() })
          : t(i18n.m.shared.candidate_range_filtered, { start: (pageStart + 1).toLocaleString(), end: Math.min(pageStart + CANDIDATE_PAGE_SIZE, offered.length).toLocaleString(), visible: offered.length.toLocaleString(), total: candidates.length.toLocaleString() })}
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
  <div class="card p-8 text-center text-ink-3">
    {i18n.m.shared.candidates_empty}
  </div>
{/if}
