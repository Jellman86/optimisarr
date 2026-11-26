<script lang="ts">
  import { onDestroy, tick } from 'svelte'
  import { api, type InventoryCounts, type InventoryFilter, type InventoryRow, type InventorySort, type Library, type MediaFile, type MediaProbeBatch } from '../api'
  import { formatSize } from '../format'
  import { i18n, plural, t } from '../i18n/i18n.svelte'
  import Banner from '../components/Banner.svelte'
  import PageHeader from '../components/PageHeader.svelte'
  import BatchResult from '../components/BatchResult.svelte'
  import ConfirmDialog from '../components/ConfirmDialog.svelte'
  import DataTable, { type SortState } from '../components/DataTable.svelte'
  import FilterBar, { type FilterItem } from '../components/FilterBar.svelte'
  import Icon from '../components/Icon.svelte'
  import InventoryDetail from '../components/InventoryDetail.svelte'
  import OkMark from '../components/OkMark.svelte'
  import PreviewCompare from '../components/PreviewCompare.svelte'
  import SelectionCheckbox from '../components/SelectionCheckbox.svelte'
  import SelectionToolbar from '../components/SelectionToolbar.svelte'
  import Thumbnail from '../components/Thumbnail.svelte'
  import { createSelection } from '../selection.svelte'

  let libraries = $state<Library[]>([])
  let rows = $state<InventoryRow[]>([])
  let total = $state(0)
  let counts = $state<InventoryCounts>({ all: 0, eligible: 0, skipped: 0, unprobed: 0 })
  let selectedLibrary = $state<number | 'all'>('all')
  let show = $state<InventoryFilter>('all')
  // What is typed shows at once; the search it asks for waits for a pause in typing, so each
  // keystroke does not refetch the library.
  let searchText = $state('')
  let search = $state('')
  let searchTimer: ReturnType<typeof setTimeout> | undefined
  let searchField: HTMLInputElement | undefined = $state()
  let sort = $state<InventorySort | null>(null)
  let page = $state(1)
  const pageSize = 50
  let selected = $state<InventoryRow | null>(null)
  let retriedFileId = $state<number | null>(null)
  let previewing = $state<MediaFile | null>(null)
  let previewSource = $state<InventoryRow | null>(null)
  let error = $state<string | null>(null)
  let libraryError = $state<string | null>(null)
  let probeError = $state<string | null>(null)
  let probeMessage = $state<string | null>(null)
  let probingId = $state<number | null>(null)
  let loading = $state(true)
  let requestId = 0
  let disposed = false

  // The bulk selection covers the rows on the page being shown. The header checkbox picks exactly
  // those, and a change of filter, library, or page drops the selection, so a pick made under one
  // filter is never carried into another.
  const selection = createSelection()
  let bulkBusy = $state(false)
  let bulkAction = $state<'exclude' | 'reprobe' | null>(null)
  let bulkOutcomeKind = $state<'exclude' | 'reprobe'>('exclude')
  let bulkOutcome = $state<{ succeeded: number; skipped: number; failures: { id: number; code: string; message: string }[] } | null>(null)
  let probeBatch = $state<MediaProbeBatch | null>(null)
  let probeBatchId = $state<number | null>(null)
  let probeActive = $derived(probeBatch !== null && ['Queued', 'Running'].includes(probeBatch.state))
  let rowIds = $derived(rows.map((row) => row.file.id))
  let bulkOutcomeTitle = $derived(bulkOutcomeKind === 'exclude' ? i18n.m.shared.batch_exclude : i18n.m.inventory.batch_reprobe_result)

  $effect(() => { void loadLibraries() })
  $effect(() => { void loadInventory(selectedLibrary, show, page, search, sort) })
  onDestroy(() => { disposed = true; requestId++; clearTimeout(searchTimer) })

  function reload() { return loadInventory(selectedLibrary, show, page, search, sort) }

  async function loadLibraries() {
    try { libraries = await api.libraries() }
    catch (err) { if (!disposed) libraryError = err instanceof Error ? err.message : i18n.m.inventory.error_load_libraries }
  }

  async function loadInventory(library: number | 'all', filter: InventoryFilter, pageNumber: number, query: string, order: InventorySort | null) {
    // A slow response from a previous filter must never replace the current selection.
    const request = ++requestId
    loading = true
    error = null
    try {
      const result = await api.inventory({ libraryId: library === 'all' ? undefined : library, show: filter, search: query || undefined, sort: order ?? undefined, page: pageNumber, pageSize })
      if (request !== requestId || disposed) return
      const lastPage = Math.max(1, Math.ceil(result.total / pageSize))
      if (pageNumber > lastPage) { page = lastPage; return }
      rows = result.items
      total = result.total
      counts = result.counts
      // A file just tried again can leave the Skipped filter; its details stay open to say it was queued.
      if (selected) selected = result.items.find(row => row.file.id === selected?.file.id) ?? (selected.file.id === retriedFileId ? selected : null)
    } catch (err) {
      if (request === requestId && !disposed) error = err instanceof Error ? err.message : i18n.m.inventory.error_load
    } finally {
      if (request === requestId && !disposed) loading = false
    }
  }

  async function probe(file: MediaFile) {
    if (probingId !== null) return
    probingId = file.id
    probeError = null
    probeMessage = null
    try {
      const updated = await api.probe(file.id)
      if (selected?.file.id === file.id) selected = { ...selected, file: updated }
      if (disposed) return
      await reload()
      if (!disposed) {
        if (error) probeError = error
        else probeMessage = i18n.m.inventory.probe_complete
      }
    } catch (err) {
      if (!disposed) probeError = err instanceof Error ? err.message : i18n.m.inventory.error_probe
    } finally {
      if (!disposed) probingId = null
    }
  }

  // --- Bulk actions on the rows shown on this page ---

  function toggleRowSelection(id: number, extend: boolean) {
    selection.toggle(id, rowIds, extend)
  }

  function toggleSelectAllRows() {
    if (selection.isEverySelected(rowIds)) selection.clear()
    else selection.selectAll(rowIds)
  }

  function askBulkAction(action: 'exclude' | 'reprobe') {
    if (selection.count === 0 || bulkBusy || probeActive) return
    bulkAction = action
    bulkOutcome = null
  }

  async function runBulkExclude() {
    if (selection.count === 0 || bulkBusy || probeActive) return
    const ids = [...selection.ids]
    bulkBusy = true
    probeError = null
    probeMessage = null
    try {
      const result = await api.batchExcludeFiles(ids)
      const excluded = new Set(result.succeeded)
      // The excluded files leave the list; the ones that failed stay picked, so they can be tried
      // again without being picked a second time.
      selection.remove([...excluded])
      bulkOutcomeKind = 'exclude'
      bulkOutcome = { succeeded: result.succeeded.length, skipped: 0, failures: result.failures }
      await reload()
    } catch (err) {
      if (!disposed) probeError = err instanceof Error ? err.message : i18n.m.inventory.error_probe
    } finally {
      bulkBusy = false
      bulkAction = null
    }
  }

  async function runBulkReprobe() {
    if (selection.count === 0 || bulkBusy || probeActive) return
    const ids = [...selection.ids]
    bulkBusy = true
    probeError = null
    probeMessage = null
    try {
      const started = await api.batchProbeFiles(ids)
      probeBatchId = started.id
      probeBatch = started
      bulkAction = null
      await followProbeBatch(started.id)
    } catch (err) {
      if (!disposed) probeError = err instanceof Error ? err.message : i18n.m.inventory.error_probe
    } finally {
      bulkBusy = false
      bulkAction = null
    }
  }

  /**
   * Follows a probe batch until it is done. The batch is worked on in the background, so the page
   * polls it and shows how far it has got rather than holding a request open.
   */
  async function followProbeBatch(batchId: number) {
    for (;;) {
      if (disposed) return
      let batch: MediaProbeBatch
      try {
        batch = await api.probeBatch(batchId)
      } catch (err) {
        if (!disposed) probeError = err instanceof Error ? err.message : i18n.m.inventory.error_probe
        return
      }
      probeBatch = batch
      if (batch.state === 'Completed' || batch.state === 'Cancelled' || batch.state === 'Failed') break
      await new Promise((resolve) => setTimeout(resolve, 1000))
    }

    const batch = probeBatch
    if (!batch || disposed) return
    const failures = batch.items
      .filter((item) => item.outcome === 'Failed')
      .map((item) => ({
        id: item.mediaFileId,
        code: 'media.probeFailed',
        message: item.error ?? i18n.m.inventory.error_probe,
      }))
    bulkOutcomeKind = 'reprobe'
    // The rows that were inspected now carry their new metadata, so the list is read again.
    bulkOutcome = batch.completed > 0 ? { succeeded: batch.succeeded, skipped: batch.skipped, failures } : null
    selection.remove(batch.items.filter(item => item.outcome === 'Succeeded').map(item => item.mediaFileId))
    if (batch.state === 'Failed') probeError = i18n.m.inventory.batch_reprobe_failed
    await reload()
    if (batch.state === 'Cancelled' && !disposed) probeMessage = i18n.m.inventory.batch_reprobe_cancelled
  }

  async function resumeProbeBatch() {
    if (bulkBusy || probeBatchId === null) return
    bulkBusy = true
    probeError = null
    try { await followProbeBatch(probeBatchId) } finally { bulkBusy = false }
  }

  async function cancelProbeBatch() {
    if (probeBatchId === null) return
    try {
      await api.cancelProbeBatch(probeBatchId)
      if (!bulkBusy) await resumeProbeBatch()
    } catch (err) {
      if (!disposed) probeError = err instanceof Error ? err.message : i18n.m.inventory.error_probe
    }
  }

  function fileName(path: string) { return path.split(/[\\/]/).pop() || path }
  function libraryName(file: MediaFile) { return libraries.find(l => l.id === file.libraryId)?.name ?? i18n.m.inventory.library_label }

  function selectLibrary(event: Event) {
    const value = (event.currentTarget as HTMLSelectElement).value
    selectedLibrary = value === 'all' ? 'all' : Number(value)
    page = 1
    selected = null
    // Another library shows different files, so the picks made under the old one are dropped.
    selection.clear()
  }
  function selectFilter(value: InventoryFilter) { show = value; page = 1; selected = null; selection.clear() }

  // A new search or order shows a different set of rows, so it starts from the first page and
  // drops the picks made among the old rows.
  function startOver() { page = 1; selected = null; selection.clear() }
  function applySearch(value: string) {
    clearTimeout(searchTimer)
    const query = value.trim()
    if (query === search) return
    search = query
    startOver()
  }
  function typeSearch(event: Event) {
    searchText = (event.currentTarget as HTMLInputElement).value
    clearTimeout(searchTimer)
    searchTimer = setTimeout(() => applySearch(searchText), 250)
  }
  function submitSearch(event: SubmitEvent) {
    event.preventDefault()
    applySearch(searchText)
  }
  function clearSearch() {
    searchText = ''
    applySearch('')
    searchField?.focus()
  }
  function searchKey(event: KeyboardEvent) {
    if (event.key !== 'Escape' || searchText === '') return
    event.preventDefault()
    clearSearch()
  }

  type SortColumn = 'name' | 'size' | 'verdict'
  function sortState(column: SortColumn): SortState {
    if (!sort?.startsWith(`${column}_`)) return 'none'
    return sort.endsWith('_asc') ? 'ascending' : 'descending'
  }
  // Size starts with the largest file, the one most worth a look; names and verdicts start from
  // the top of their order. A third click returns to the plain path order.
  function cycleSort(column: SortColumn) {
    const first = column === 'size' ? 'desc' : 'asc'
    const second = first === 'asc' ? 'desc' : 'asc'
    const current = sort?.startsWith(`${column}_`) ? sort.slice(column.length + 1) : null
    const next = current === null ? first : current === first ? second : null
    sort = next ? (`${column}_${next}` as InventorySort) : null
    startOver()
  }
  function selectRow(row: InventoryRow) { selected = row; retriedFileId = null; probeError = null; probeMessage = null }
  async function closeDetails() {
    const id = selected?.file.id
    selected = null
    retriedFileId = null
    await tick()
    if (id !== undefined) document.getElementById(`inventory-file-${id}`)?.focus()
  }
  function startPreview() {
    if (!selected) return
    previewSource = selected
    previewing = selected.file
    selected = null
  }
  function closePreview() {
    previewing = null
    if (!selected && previewSource) selected = rows.find(row => row.file.id === previewSource?.file.id) ?? null
    previewSource = null
  }
  function goToPage(next: number) { page = Math.max(1, Math.min(next, pageCount)); selected = null; selection.clear() }
  let pageCount = $derived(Math.max(1, Math.ceil(total / pageSize)))
  let pageStart = $derived((Math.min(page, pageCount) - 1) * pageSize)
  let filters: FilterItem<InventoryFilter>[] = $derived([
    { value: 'all', label: t(i18n.m.inventory.filter_all, { count: counts.all.toLocaleString() }) },
    { value: 'eligible', label: t(i18n.m.inventory.filter_eligible, { count: counts.eligible.toLocaleString() }) },
    { value: 'skipped', label: t(i18n.m.inventory.filter_skipped, { count: counts.skipped.toLocaleString() }) },
    { value: 'unprobed', label: t(i18n.m.inventory.filter_unprobed, { count: counts.unprobed.toLocaleString() }) },
  ])
</script>

<!-- A container, so the table drops its Format column by the room it has rather than by the
     window: beside an open sidebar a tablet leaves it phone-width. -->
<div class="@container inventory-layout">
  <PageHeader title={i18n.m.nav.inventory} subtitle={i18n.m.inventory.subtitle} actionsAlign="end">
    {#snippet actions()}
      <div class="inventory-library"><label class="label" for="lib-filter">{i18n.m.inventory.library_label}</label><select id="lib-filter" class="input" value={selectedLibrary} onchange={selectLibrary}><option value="all">{i18n.m.inventory.all_libraries}</option>{#each libraries as library}<option value={library.id}>{library.name}</option>{/each}</select></div>
    {/snippet}
  </PageHeader>
  {#if libraryError}<Banner kind="error" class="mb-4">{libraryError}</Banner>{/if}
  {#if error}<Banner kind="error" class="mb-4">{error}<button class="btn ml-3" onclick={reload}>{i18n.m.setup.retry}</button></Banner>{/if}
  {#if probeError && !selected}<div role="alert"><Banner kind="error" class="mb-4">{probeError}</Banner></div>{/if}
  {#if probeMessage && !selected}<p class="callout tone-ok mb-4" role="status">{probeMessage}</p>{/if}
  <div class="inventory-toolbar">
    <FilterBar label={i18n.m.inventory.filter_label} items={filters} value={show} onselect={selectFilter} />
    <form role="search" class="relative w-full min-w-0 sm:w-72" onsubmit={submitSearch}>
      <label class="sr-only" for="inventory-search">{i18n.m.inventory.search_label}</label>
      <Icon name="search" class="pointer-events-none absolute top-1/2 left-3 h-4 w-4 -translate-y-1/2 text-ink-4" />
      <input id="inventory-search" bind:this={searchField} type="search" class={['input pl-9 [&::-webkit-search-cancel-button]:appearance-none', searchText && 'pr-12']} placeholder={i18n.m.inventory.search_placeholder} value={searchText} oninput={typeSearch} onkeydown={searchKey} autocomplete="off" spellcheck="false" />
      {#if searchText}<button type="button" class="btn btn-ghost btn-icon btn-sm absolute top-1/2 right-1 -translate-y-1/2" aria-label={i18n.m.inventory.clear_search} onclick={clearSearch}><Icon name="x" class="h-4 w-4" /></button>{/if}
    </form>
    <div class={['flex flex-wrap items-center', loading && rows.length > 0 && 'gap-3']}><span class="inventory-loading" role="status">{loading && rows.length > 0 ? i18n.m.common.loading_short : ''}</span><a class="btn btn-ghost" href="#/inventory/duplicates">{i18n.m.duplicates.title}<Icon name="arrow-right" /></a></div>
  </div>
  {#if bulkOutcome}
    <BatchResult
      succeeded={bulkOutcome.succeeded}
      skipped={bulkOutcome.skipped}
      failures={bulkOutcome.failures}
      title={bulkOutcomeTitle}
      ondismiss={() => (bulkOutcome = null)}
    />
  {/if}
  {#if selection.count > 0}
    <SelectionToolbar count={selection.count} total={rowIds.length} onclear={() => selection.clear()}>
      {#snippet actions()}
        <button type="button" class="btn" disabled={bulkBusy || probeActive} onclick={() => askBulkAction('exclude')}>{i18n.m.shared.batch_exclude}</button>
        <button type="button" class="btn" disabled={bulkBusy || probeActive} onclick={() => askBulkAction('reprobe')}>{i18n.m.inventory.batch_reprobe}</button>
      {/snippet}
    </SelectionToolbar>
  {/if}
  {#if probeBatch && probeBatch.state !== 'Completed' && probeBatch.state !== 'Cancelled' && probeBatch.state !== 'Failed'}
    <p class="callout mb-4" role="status">
      {t(i18n.m.inventory.batch_reprobe_running, { done: probeBatch.completed.toLocaleString(), total: probeBatch.total.toLocaleString() })}
      <button type="button" class="btn ml-2" onclick={cancelProbeBatch}>{i18n.m.common.cancel}</button>
      {#if !bulkBusy}<button type="button" class="btn ml-2" onclick={resumeProbeBatch}>{i18n.m.setup.retry}</button>{/if}
    </p>
  {/if}
  <DataTable data-keyboard-list density="compact" rowCount={rows.length} {loading} {error} label={i18n.m.nav.inventory} footer={total > 0 ? pagination : undefined}>
    {#snippet head()}
      <tr>
        <th scope="col" class="selection-column"><SelectionCheckbox checked={selection.isEverySelected(rowIds)} indeterminate={!selection.isEverySelected(rowIds) && selection.isSomeSelected(rowIds)} label={i18n.m.inventory.select_all_page} onchange={toggleSelectAllRows} /></th>
        <th scope="col" class="data-table-primary" aria-sort={sortState('name')}><button type="button" class="table-sort" onclick={() => cycleSort('name')}>{i18n.m.inventory.col_file}</button></th>
        <th scope="col" aria-sort={sortState('size')} class="w-[13%] sm:w-[max(13%,7.25rem)]"><button type="button" class="table-sort" onclick={() => cycleSort('size')}>{i18n.m.inventory.col_size}</button></th>
        <th scope="col" class="hidden w-[21%] sm:@min-[36rem]:table-cell">{i18n.m.inventory.col_format}</th>
        <th scope="col" aria-sort={sortState('verdict')} class="w-[28%] sm:w-[max(19%,8.25rem)]"><button type="button" class="table-sort" onclick={() => cycleSort('verdict')}>{i18n.m.inventory.rule_verdict}</button></th>
      </tr>
    {/snippet}
    {#each rows as row (row.file.id)}{@const file = row.file}
      <tr class:selected-row={selected?.file.id === file.id}>
        <td class="selection-column"><SelectionCheckbox checked={selection.isSelected(file.id)} label={t(i18n.m.selection.select_row, { name: fileName(file.relativePath) })} onchange={(extend) => toggleRowSelection(file.id, extend)} /></td>
        <td class="data-table-primary"><button id={`inventory-file-${file.id}`} data-row-primary class="group flex w-full min-w-0 items-center gap-2.5 rounded-md text-left focus-ring max-sm:min-h-(--control-lg) sm:gap-3.5" onclick={() => selectRow(row)}>
          <Thumbnail mediaFileId={file.id} size="xs" />
          <span class="min-w-0"><span class="line-clamp-2 text-label font-medium text-ink group-hover:text-accent sm:line-clamp-1">{fileName(file.relativePath)}</span><span class="mt-1 block text-micro text-ink-3">{libraryName(file)}{#if file.mediaKind && file.mediaKind !== 'Unknown'}{` · ${file.mediaKind}`}{/if}</span></span>
        </button></td>
        <td class="whitespace-nowrap tabular-nums max-sm:text-caption">{formatSize(file.sizeBytes)}</td>
        <td class="hidden sm:@min-[36rem]:table-cell"><span>{file.mediaKind === 'Audio' ? file.audioCodecs ?? '—' : file.videoCodec ?? '—'}</span><span class="mt-1 block text-micro text-ink-3">{file.width && file.height ? `${file.width} × ${file.height}` : file.container ?? '—'}</span></td>
        <td><span class="inventory-verdict" class:verdict-eligible={row.eligible === true} class:verdict-unprobed={row.eligible === null}>{#if row.eligible === true}<OkMark class="h-3 w-3" />{/if}<span class="min-w-0">{row.eligible === null ? i18n.m.inventory.badge_unprobed : row.eligible ? i18n.m.inventory.badge_eligible : i18n.m.inventory.badge_skipped}</span></span>{#if row.eligible === false && row.reason}<span class="mt-1 line-clamp-2 text-micro text-ink-4 sm:line-clamp-1" title={row.reason}>{row.reason}</span>{/if}</td>
      </tr>
    {/each}
    {#snippet empty()}<Icon name="film" class="h-7 w-7 text-ink-4" /><p>{search ? t(i18n.m.inventory.empty_search, { query: search }) : counts.all > 0 ? i18n.m.inventory.empty_filter : i18n.m.inventory.empty}</p>{#if search}<button type="button" class="btn" onclick={clearSearch}>{i18n.m.inventory.clear_search}</button>{/if}{/snippet}
  </DataTable>
</div>

{#snippet pagination()}
  <span>{t(i18n.m.inventory.range, { start: (pageStart + 1).toLocaleString(), end: Math.min(pageStart + pageSize, total).toLocaleString(), total: total.toLocaleString() })}</span>
  <div class="data-table-pages"><button class="btn btn-ghost btn-icon" onclick={() => goToPage(page - 1)} disabled={page <= 1 || loading} aria-label={i18n.m.inventory.prev_page}><Icon name="arrow-left" /></button><span>{t(i18n.m.inventory.page_of, { page: Math.min(page, pageCount), count: pageCount })}</span><button class="btn btn-ghost btn-icon" onclick={() => goToPage(page + 1)} disabled={page >= pageCount || loading} aria-label={i18n.m.inventory.next_page}><Icon name="arrow-right" /></button></div>
{/snippet}

{#if selected}
  {#key selected.file.id}
    <InventoryDetail row={selected} libraryName={libraryName(selected.file)} probing={probingId === selected.file.id} error={probeError} onclose={closeDetails} onprobe={() => { if (selected) void probe(selected.file) }} onpreview={startPreview} onretried={() => { retriedFileId = selected?.file.id ?? null; void reload() }} />
  {/key}
{/if}
{#if previewing}
  {#key previewing.id}
    <PreviewCompare mediaFileId={previewing.id} mediaKind={previewing.mediaKind ?? 'Video'} relativePath={previewing.relativePath} onClose={closePreview} />
  {/key}
{/if}

<ConfirmDialog
  open={bulkAction === 'exclude'}
  title={plural(selection.count, i18n.m.shared.batch_confirm_exclude_title_one, i18n.m.shared.batch_confirm_exclude_title_other, selection.count.toLocaleString())}
  confirmLabel={i18n.m.shared.batch_exclude}
  busy={bulkBusy}
  onconfirm={runBulkExclude}
  oncancel={() => { bulkAction = null }}
>
  {#snippet body()}
    <p>{i18n.m.shared.batch_confirm_exclude_body}</p>
      <p class="mt-2">{i18n.m.shared.batch_confirm_exclude_existing}</p>
  {/snippet}
</ConfirmDialog>

<ConfirmDialog
  open={bulkAction === 'reprobe'}
  title={plural(selection.count, i18n.m.inventory.batch_reprobe_title_one, i18n.m.inventory.batch_reprobe_title_other, selection.count.toLocaleString())}
  confirmLabel={i18n.m.inventory.batch_reprobe}
  busy={bulkBusy}
  onconfirm={runBulkReprobe}
  oncancel={() => { bulkAction = null }}
>
  {#snippet body()}
    <p>{i18n.m.inventory.batch_reprobe_body}</p>
  {/snippet}
</ConfirmDialog>

<style>
  .inventory-library { min-width: 12rem; }
  .inventory-toolbar { display: flex; align-items: center; flex-wrap: wrap; justify-content: space-between; gap: .75rem; margin-bottom: 1rem; }.inventory-loading { font-size: .75rem; color: var(--ink-3); }
  .inventory-verdict { display: inline-flex; align-items: center; gap: .5rem; color: var(--ink-3); font-size: .75rem; min-width: 0; max-width: 100%; overflow-wrap: break-word; }.inventory-verdict:not(.verdict-eligible)::before { content: ''; display: block; flex-shrink: 0; width: .3rem; height: .3rem; border-radius: 50%; background: currentColor; }.verdict-eligible { color: var(--ink-2); }.verdict-unprobed { color: var(--warn); }
  @media (max-width: 639px) { .inventory-library { width: 100%; }.inventory-verdict { gap: .375rem; } }
</style>
