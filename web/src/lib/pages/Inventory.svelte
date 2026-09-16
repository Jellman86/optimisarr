<script lang="ts">
  import { onDestroy, tick } from 'svelte'
  import { api, type InventoryCounts, type InventoryFilter, type InventoryRow, type Library, type MediaFile } from '../api'
  import { formatSize } from '../format'
  import { i18n, t } from '../i18n/i18n.svelte'
  import Banner from '../components/Banner.svelte'
  import Icon from '../components/Icon.svelte'
  import InventoryDetail from '../components/InventoryDetail.svelte'
  import PreviewCompare from '../components/PreviewCompare.svelte'
  import Thumbnail from '../components/Thumbnail.svelte'

  let libraries = $state<Library[]>([])
  let rows = $state<InventoryRow[]>([])
  let total = $state(0)
  let counts = $state<InventoryCounts>({ all: 0, eligible: 0, skipped: 0, unprobed: 0 })
  let selectedLibrary = $state<number | 'all'>('all')
  let show = $state<InventoryFilter>('all')
  let page = $state(1)
  const pageSize = 50
  let selected = $state<InventoryRow | null>(null)
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

  $effect(() => { void loadLibraries() })
  $effect(() => { void loadInventory(selectedLibrary, show, page) })
  onDestroy(() => { disposed = true; requestId++ })

  async function loadLibraries() {
    try { libraries = await api.libraries() }
    catch (err) { if (!disposed) libraryError = err instanceof Error ? err.message : i18n.m.inventory.error_load_libraries }
  }

  async function loadInventory(library: number | 'all', filter: InventoryFilter, pageNumber: number) {
    // A slow response from a previous filter must never replace the current selection.
    const request = ++requestId
    loading = true
    error = null
    try {
      const result = await api.inventory({ libraryId: library === 'all' ? undefined : library, show: filter, page: pageNumber, pageSize })
      if (request !== requestId || disposed) return
      const lastPage = Math.max(1, Math.ceil(result.total / pageSize))
      if (pageNumber > lastPage) { page = lastPage; return }
      rows = result.items
      total = result.total
      counts = result.counts
      if (selected) selected = result.items.find(row => row.file.id === selected?.file.id) ?? null
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
      await loadInventory(selectedLibrary, show, page)
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

  function fileName(path: string) { return path.split(/[\\/]/).pop() || path }
  function libraryName(file: MediaFile) { return libraries.find(l => l.id === file.libraryId)?.name ?? i18n.m.inventory.library_label }
  function selectLibrary(event: Event) {
    const value = (event.currentTarget as HTMLSelectElement).value
    selectedLibrary = value === 'all' ? 'all' : Number(value)
    page = 1
    selected = null
  }
  function selectFilter(value: InventoryFilter) { show = value; page = 1; selected = null }
  function selectRow(row: InventoryRow) { selected = row; probeError = null; probeMessage = null }
  async function closeDetails() {
    const id = selected?.file.id
    selected = null
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
  function goToPage(next: number) { page = Math.max(1, Math.min(next, pageCount)); selected = null }
  let pageCount = $derived(Math.max(1, Math.ceil(total / pageSize)))
  let pageStart = $derived((Math.min(page, pageCount) - 1) * pageSize)
  let filters = $derived([
    { key: 'all' as InventoryFilter, label: t(i18n.m.inventory.filter_all, { count: counts.all.toLocaleString() }) },
    { key: 'eligible' as InventoryFilter, label: t(i18n.m.inventory.filter_eligible, { count: counts.eligible.toLocaleString() }) },
    { key: 'skipped' as InventoryFilter, label: t(i18n.m.inventory.filter_skipped, { count: counts.skipped.toLocaleString() }) },
    { key: 'unprobed' as InventoryFilter, label: t(i18n.m.inventory.filter_unprobed, { count: counts.unprobed.toLocaleString() }) },
  ])
</script>

<div class="inventory-layout">
  <header class="inventory-heading">
    <div><h1 class="page-title">{i18n.m.nav.inventory}</h1><p class="page-subtitle">{i18n.m.inventory.subtitle}</p></div>
    <div class="inventory-library"><label class="label" for="lib-filter">{i18n.m.inventory.library_label}</label><select id="lib-filter" class="input" value={selectedLibrary} onchange={selectLibrary}><option value="all">{i18n.m.inventory.all_libraries}</option>{#each libraries as library}<option value={library.id}>{library.name}</option>{/each}</select></div>
  </header>
  {#if libraryError}<Banner kind="error" class="mb-4">{libraryError}</Banner>{/if}
  {#if error}<Banner kind="error" class="mb-4">{error}<button class="btn ml-3" onclick={() => loadInventory(selectedLibrary, show, page)}>{i18n.m.setup.retry}</button></Banner>{/if}
  {#if probeError && !selected}<Banner kind="error" class="mb-4">{probeError}</Banner>{/if}
  {#if probeMessage && !selected}<p class="callout tone-ok mb-4" role="status">{probeMessage}</p>{/if}
  <div class="inventory-toolbar">
    <div class="inventory-filters">{#each filters as filter}<button class="focus-ring" class:filter-active={show === filter.key} aria-pressed={show === filter.key} onclick={() => selectFilter(filter.key)}>{filter.label}</button>{/each}</div>
    <span class="inventory-loading" role="status">{loading ? i18n.m.common.loading_short : ''}</span>
  </div>
  <div class="inventory-surface" aria-busy={loading}>
    {#if rows.length}
      <table class="inventory-table">
        <thead><tr><th scope="col">{i18n.m.inventory.col_file}</th><th scope="col" class="size-column">{i18n.m.inventory.col_size}</th><th scope="col" class="format-column">{i18n.m.inventory.col_format}</th><th scope="col">{i18n.m.inventory.rule_verdict}</th></tr></thead>
        <tbody>{#each rows as row (row.file.id)}{@const file = row.file}
          <tr class:selected-row={selected?.file.id === file.id}>
            <td><button id={`inventory-file-${file.id}`} class="inventory-file focus-ring" onclick={() => selectRow(row)}>
              <Thumbnail mediaFileId={file.id} />
              <span class="inventory-file-name"><span class="inventory-file-title">{fileName(file.relativePath)}</span><span class="inventory-file-meta">{libraryName(file)}{#if file.mediaKind && file.mediaKind !== 'Unknown'} · {file.mediaKind}{/if}<span class="mobile-size"> · {formatSize(file.sizeBytes)}</span></span></span>
            </button></td>
            <td class="size-column inventory-size">{formatSize(file.sizeBytes)}</td>
            <td class="format-column"><span class="inventory-codec">{file.mediaKind === 'Audio' ? file.audioCodecs ?? '—' : file.videoCodec ?? '—'}</span><span class="inventory-resolution">{file.width && file.height ? `${file.width} × ${file.height}` : file.container ?? '—'}</span></td>
            <td><span class="inventory-verdict" class:verdict-eligible={row.eligible === true} class:verdict-unprobed={row.eligible === null}>{row.eligible === null ? i18n.m.inventory.badge_unprobed : row.eligible ? i18n.m.inventory.badge_eligible : i18n.m.inventory.badge_skipped}</span></td>
          </tr>
        {/each}</tbody>
      </table>
    {:else if !loading && !error}
      <div class="inventory-empty"><Icon name="film" class="h-7 w-7 text-ink-4" /><p>{counts.all > 0 ? i18n.m.inventory.empty_filter : i18n.m.inventory.empty}</p></div>
    {:else}<div class="inventory-empty">{loading ? i18n.m.common.loading_short : i18n.m.inventory.error_load}</div>{/if}
  </div>
  {#if total > 0}
    <footer class="inventory-pagination">
      <span>{t(i18n.m.inventory.range, { start: (pageStart + 1).toLocaleString(), end: Math.min(pageStart + pageSize, total).toLocaleString(), total: total.toLocaleString() })}</span>
      <div><button class="btn btn-ghost" onclick={() => goToPage(page - 1)} disabled={page <= 1 || loading} aria-label={i18n.m.inventory.prev_page}><Icon name="arrow-left" /></button><span>{t(i18n.m.inventory.page_of, { page: Math.min(page, pageCount), count: pageCount })}</span><button class="btn btn-ghost" onclick={() => goToPage(page + 1)} disabled={page >= pageCount || loading} aria-label={i18n.m.inventory.next_page}><Icon name="arrow-right" /></button></div>
    </footer>
  {/if}
</div>

{#if selected}
  {#key selected.file.id}
    <InventoryDetail row={selected} libraryName={libraryName(selected.file)} probing={probingId === selected.file.id} error={probeError} onclose={closeDetails} onprobe={() => { if (selected) void probe(selected.file) }} onpreview={startPreview} />
  {/key}
{/if}
{#if previewing}
  {#key previewing.id}
    <PreviewCompare mediaFileId={previewing.id} mediaKind={previewing.mediaKind ?? 'Video'} relativePath={previewing.relativePath} onClose={closePreview} />
  {/key}
{/if}

<style>
  .inventory-layout { max-width: 72rem; margin-inline: auto; }
  .inventory-heading { display: flex; flex-wrap: wrap; align-items: flex-end; justify-content: space-between; gap: 1.5rem; margin-bottom: 2rem; }
  .inventory-heading > div:first-child { flex: 1; min-width: 16rem; }.inventory-library { min-width: 12rem; }.inventory-library .label { text-transform: none; letter-spacing: 0; font-size: .75rem; }
  .inventory-toolbar { display: flex; align-items: center; flex-wrap: wrap; justify-content: space-between; gap: .75rem; margin-bottom: 1rem; }.inventory-filters { display: flex; flex-wrap: wrap; gap: .375rem; }.inventory-filters button { padding: .625rem .875rem; border-radius: .5rem; color: var(--ink-3); font-size: .75rem; }.inventory-filters button:hover { color: var(--ink); background: var(--lit); }.inventory-filters button.filter-active { color: var(--ink); background: var(--raised); box-shadow: var(--lift-1); }.inventory-loading { font-size: .75rem; color: var(--ink-3); }
  .inventory-surface { background: var(--panel); border-radius: .875rem; box-shadow: var(--lift-1), inset 0 1px 0 var(--edge); overflow: clip; }.inventory-table { width: 100%; table-layout: fixed; border-collapse: collapse; text-align: left; }.inventory-table th { background: var(--raised); color: var(--ink-3); padding: .875rem 1rem; font-size: .6875rem; font-weight: 500; }.inventory-table th:first-child { width: 47%; }.inventory-table th:nth-child(2) { width: 13%; }.inventory-table th:nth-child(3) { width: 21%; }.inventory-table th:last-child { width: 19%; }.inventory-table td { padding: .875rem 1rem; border-top: 1px solid var(--divide-soft); font-size: .8125rem; color: var(--ink-2); vertical-align: middle; }.inventory-table tr:hover td, .inventory-table tr.selected-row td { background: var(--lit); }
  .inventory-file { display: flex; align-items: center; gap: .875rem; text-align: left; width: 100%; min-width: 0; border-radius: .375rem; }.inventory-file-name { min-width: 0; }.inventory-file-title { display: -webkit-box; -webkit-line-clamp: 2; line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; overflow-wrap: anywhere; color: var(--ink); font-size: .8125rem; font-weight: 500; line-height: 1.5; }.inventory-file:hover .inventory-file-title { color: var(--accent); }.inventory-file-meta { display: block; margin-top: .375rem; color: var(--ink-3); font-size: .6875rem; }.inventory-size { font-variant-numeric: tabular-nums; white-space: nowrap; }.inventory-codec { overflow-wrap: anywhere; }.inventory-resolution { display: block; color: var(--ink-3); font-size: .6875rem; margin-top: .25rem; }.inventory-verdict { display: inline-flex; align-items: center; gap: .5rem; color: var(--ink-3); font-size: .75rem; }.inventory-verdict::before { content: ''; display: block; flex-shrink: 0; width: .3rem; height: .3rem; border-radius: 50%; background: currentColor; }.verdict-eligible { color: var(--ok); }.verdict-unprobed { color: var(--warn); }.mobile-size { display: none; }
  .inventory-empty { padding: 3rem 1.5rem; text-align: center; color: var(--ink-3); font-size: .875rem; display: grid; justify-items: center; gap: 1rem; }.inventory-pagination { margin-top: 1rem; display: flex; align-items: center; justify-content: space-between; gap: 1rem; color: var(--ink-3); font-size: .75rem; }.inventory-pagination > div { display: flex; align-items: center; gap: .625rem; }.inventory-pagination .btn { min-width: 2.5rem; min-height: 2.5rem; }
  @media (max-width: 639px) { .inventory-heading { margin-bottom: 1.25rem; }.inventory-library { width: 100%; }.inventory-filters { gap: .125rem; }.inventory-filters button { padding: .75rem .625rem; }.format-column,.size-column { display: none; }.inventory-table th:first-child { width: 72%; }.inventory-table th:last-child { width: 28%; }.inventory-table td,.inventory-table th { padding: .875rem .75rem; }.inventory-file { gap: .625rem; }.inventory-file-title { font-size: .75rem; }.inventory-verdict { font-size: .6875rem; gap: .375rem; }.mobile-size { display: inline; } }
</style>
