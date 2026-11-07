<script lang="ts">
  import { onMount, onDestroy } from 'svelte'
  import { api, type DuplicateCleanupPreview, type DuplicateCleanupRefusal, type DuplicateCleanupStatus, type Library, type ExactDuplicateStatus } from '../api'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { formatSize } from '../format'
  import Banner from '../components/Banner.svelte'
  import Icon from '../components/Icon.svelte'
  import ExactCopyGroup from '../components/ExactCopyGroup.svelte'
  import { router } from '../stores/ui.svelte'

  const routeLibraryId = Number(router.path.match(/^\/libraries\/(\d+)\/duplicates$/)?.[1]) || null

  let libraries = $state<Library[]>([])
  let libraryId = $state<number | null>(null)
  let report = $state<ExactDuplicateStatus | null>(null)
  let error = $state<string | null>(null)
  let loading = $state(false)
  let acting = $state(false)
  let forceRefresh = $state(false)
  let compareIds = $state<number[]>([])
  let restoreScope = false
  // Comparing across libraries is the common case, so the choice starts visible.
  let scopeExpanded = $state(true)
  let disposed = false
  let revision = 0
  let loadRequest = 0
  let timer: ReturnType<typeof setTimeout> | undefined
  let running = $derived(report?.status === 'Queued' || report?.status === 'Running')
  let scopeChanged = $derived(!!report?.libraries?.length && JSON.stringify(report.libraries.map(l => l.id).sort((a,b) => a-b)) !== JSON.stringify([libraryId, ...compareIds].sort((a,b) => (a ?? 0)-(b ?? 0))))
  let scopeLibraries = $derived([...libraries.map(l => ({ id:l.id, name:l.name, available:true })),
    ...(report?.libraries ?? []).filter(l => !libraries.some(current => current.id === l.id)).map(l => ({ id:l.id, name:l.name, available:false }))])
  let statusLabel = $derived(report ? i18n.m.duplicates[report.status] : i18n.m.duplicates.NotStarted)
  // Whole-file counts stall for minutes on one large film, so the bar follows settled bytes when the
  // server reports them. It never claims 100% before the server says the scan is complete.
  let scanPercent = $derived.by(() => {
    if (!report || report.status !== 'Running') return null
    const { checked, skipped, total, totalBytes = 0, processedBytes = 0 } = report.progress
    const fraction = totalBytes > 0 ? processedBytes / totalBytes : total > 0 ? (checked + skipped) / total : null
    return fraction === null ? null : Math.min(99, Math.max(0, Math.floor(fraction * 100)))
  })
  let progressText = $derived(report ? t(i18n.m.duplicates.progress, { checked: report.progress.checked, total: report.progress.total, skipped: report.progress.skipped, bytes: formatSize(report.progress.bytesRead) }) : '')
  let groupCount = $derived(report?.result ? report.result.totalGroups ?? report.result.groups.length : 0)
  let cleanupPreview = $state<DuplicateCleanupPreview | null>(null)
  let cleanup = $state<DuplicateCleanupStatus | null>(null)
  let cleanupError = $state<string | null>(null)
  let startingCleanup = $state(false)
  let cleanupTimer: ReturnType<typeof setTimeout> | undefined
  let cleanupRequest = 0
  let cleanable = $derived(report?.status === 'Completed' && !report.isStale && !scopeChanged && groupCount > 0)
  const leftAloneReasons = $derived.by((): [DuplicateCleanupRefusal, string][] => [
    ['ManagerUnavailable', i18n.m.duplicates.left_manager],
    ['PossiblyTracked', i18n.m.duplicates.left_possibly_tracked],
    ['NamesDiffer', i18n.m.duplicates.left_names],
    ['SeparateLibraries', i18n.m.duplicates.left_libraries],
    ['SharedExtra', i18n.m.duplicates.left_extra],
    ['LibraryType', i18n.m.duplicates.left_type],
    ['NothingToMove', i18n.m.duplicates.left_nothing],
    ['Ignored', i18n.m.duplicates.left_ignored],
  ])
  // Asks Sonarr and Radarr what they track, so only once for each finished report.
  let cleanupKey = $derived(cleanable && libraryId !== null ? `${libraryId}:${report?.finishedAt}` : null)
  $effect(() => {
    const key = cleanupKey, id = libraryId
    cleanupPreview = null
    startingCleanup = false
    cleanupError = null
    if (key !== null && id !== null) void loadCleanup(id)
    return () => { cleanupRequest++; clearTimeout(cleanupTimer) }
  })
  async function loadCleanup(id: number) {
    const request = ++cleanupRequest
    try {
      const [preview, status] = await Promise.all([api.duplicateCleanupPreview(id), api.duplicateCleanup(id)])
      if (disposed || request !== cleanupRequest) return
      cleanupPreview = preview
      cleanup = status.total > 0 ? status : null
      if (status.running) cleanupTimer = setTimeout(() => void followCleanup(id, request), 1500)
    } catch (err) { if (!disposed && request === cleanupRequest) cleanupError = err instanceof Error ? err.message : i18n.m.duplicates.clean_error }
  }
  async function followCleanup(id: number, request: number) {
    try {
      const status = await api.duplicateCleanup(id)
      if (disposed || request !== cleanupRequest) return
      cleanup = status
      if (status.running) { cleanupTimer = setTimeout(() => void followCleanup(id, request), 1500); return }
      // What could move has changed, and the report may look the same if its first page did not.
      cleanupPreview = null
      await load(id)
      if (!disposed && request === cleanupRequest && cleanable) await loadCleanup(id)
    } catch (err) { if (!disposed && request === cleanupRequest) cleanupError = err instanceof Error ? err.message : i18n.m.duplicates.clean_error }
  }
  async function startCleanup() {
    if (libraryId === null) return
    const id = libraryId, request = cleanupRequest
    cleanupError = null
    startingCleanup = true
    try {
      const status = await api.startDuplicateCleanup(id)
      if (disposed || request !== cleanupRequest) return
      cleanup = status
      cleanupTimer = setTimeout(() => void followCleanup(id, request), 1500)
    } catch (err) { if (!disposed && request === cleanupRequest) cleanupError = err instanceof Error ? err.message : i18n.m.duplicates.clean_error }
    finally { startingCleanup = false }
  }
  async function stopCleanup() {
    if (libraryId === null) return
    try { await api.cancelDuplicateCleanup(libraryId) }
    catch (err) { if (!disposed) cleanupError = err instanceof Error ? err.message : i18n.m.duplicates.clean_error }
  }
  onMount(() => { void loadLibraries() })
  onDestroy(() => { disposed = true; revision++; clearTimeout(timer); clearTimeout(cleanupTimer) })

  async function loadLibraries() {
    try {
      const next = await api.libraries()
      if (disposed) return
      libraries = next
      if (routeLibraryId !== null) {
        libraryId = routeLibraryId; restoreScope = true; loading = true
        await load(routeLibraryId)
      }
    }
    catch (err) { if (!disposed) error = err instanceof Error ? err.message : i18n.m.inventory.error_load_libraries }
  }
  async function load(id: number, request = revision, page = 0) {
    const sequence = ++loadRequest
    try {
      const next = await api.exactDuplicates(id, page)
      if (disposed || request !== revision || sequence !== loadRequest) return
      report = next; error = null
      if (restoreScope) { compareIds = (next.libraries ?? []).map(l => l.id).filter(id => id !== libraryId); restoreScope = false }
      clearTimeout(timer)
      if (next.status === 'Running' || next.status === 'Queued') timer = setTimeout(() => void load(id, request), 1500)
    } catch (err) { if (!disposed && request === revision && sequence === loadRequest) error = err instanceof Error ? err.message : i18n.m.inventory.error_load }
    finally { if (!disposed && request === revision && sequence === loadRequest) loading = false }
  }
  function select(event: Event) {
    const value = (event.currentTarget as HTMLSelectElement).value
    if (routeLibraryId !== null) { router.go(value ? `/libraries/${Number(value)}/duplicates` : '/inventory/duplicates'); return }
    revision++; clearTimeout(timer); report = null; error = null
    forceRefresh = false; compareIds = []; restoreScope = true; scopeExpanded = true
    libraryId = value ? Number(value) : null
    if (libraryId !== null) { loading = true; void load(libraryId) }
  }
  async function act(cancel = false) {
    if (libraryId === null || acting) return
    const id = libraryId, request = revision
    loadRequest++; clearTimeout(timer)
    acting = true; error = null
    try {
      if (cancel) await api.cancelExactDuplicates(id)
      else await api.scanExactDuplicates(id, forceRefresh, [id, ...compareIds])
      await load(id, request)
    } catch (err) { if (!disposed && request === revision) error = err instanceof Error ? err.message : i18n.m.inventory.error_load }
    finally { if (!disposed) acting = false }
  }
  function toggleLibrary(id: number, checked: boolean) {
    compareIds = checked ? [...compareIds, id] : compareIds.filter(value => value !== id)
  }
  async function refreshReport(id: number) {
    if (libraryId !== id || disposed) return
    loading = true
    await load(id, revision, report?.result?.page ?? 0)
  }
  function changePage(delta: number) {
    if (libraryId === null || loading || acting) return
    loading = true
    void load(libraryId, revision, Math.max(0, (report?.result?.page ?? 0) + delta))
  }
</script>

<div class="duplicate-view">
  <nav class="flex flex-wrap items-center gap-2 text-sm text-ink-3" aria-label={i18n.m.libraryWorkflow.breadcrumb}>
    {#if routeLibraryId !== null}
      <a class="focus-ring hover:text-accent" href="#/libraries">{i18n.m.nav.libraries}</a><Icon name="chevron" class="h-4 w-4 -rotate-90" />
      <a class="focus-ring hover:text-accent" href={`#/libraries/${routeLibraryId}/configure`}>{libraries.find(l => l.id === routeLibraryId)?.name ?? i18n.m.inventory.library_label}</a><Icon name="chevron" class="h-4 w-4 -rotate-90" />
    {:else}<a class="focus-ring hover:text-accent" href="#/inventory">{i18n.m.nav.inventory}</a><Icon name="chevron" class="h-4 w-4 -rotate-90" />{/if}<span aria-current="page">{i18n.m.duplicates.title}</span>
  </nav>
  <header><h1 class="page-title">{i18n.m.duplicates.title}</h1><p class="page-subtitle">{i18n.m.duplicates.subtitle}</p></header>
  <section class="card p-5 sm:p-6" aria-label={i18n.m.duplicates.title}>
    <div class="duplicate-controls">
      <div class="min-w-0 flex-1"><label class="label" for="duplicate-library">{i18n.m.inventory.library_label}</label><select class="input" id="duplicate-library" value={libraryId ?? ''} onchange={select} disabled={acting}><option value="">{i18n.m.duplicates.choose}</option>{#each libraries as library}<option value={library.id}>{library.name}</option>{/each}</select></div>
      {#if running}<button class="btn" disabled={acting} onclick={() => act(true)}>{i18n.m.duplicates.cancel}</button>
      {:else}<button class="btn btn-primary" disabled={libraryId === null || loading || acting} onclick={() => act()}>{i18n.m.duplicates.scan}</button>{/if}
    </div>
    {#if libraryId !== null && scopeLibraries.length > 1}
      <details class="scope-picker mt-4 rounded-xl border border-line p-4" bind:open={scopeExpanded}>
        <summary class="focus-ring cursor-pointer text-sm font-semibold text-ink">{i18n.m.duplicates.compareLibraries}{compareIds.length ? ` (${compareIds.length})` : ''}</summary>
        <p class="mt-3 text-sm leading-relaxed text-ink-3" id="duplicate-scope-hint">{i18n.m.duplicates.scopeHint}</p>
        <fieldset class="scope-grid mt-3" aria-describedby="duplicate-scope-hint" disabled={running || acting || loading}>
          <legend class="sr-only">{i18n.m.duplicates.compareLibraries}</legend>
          {#each scopeLibraries.filter(l => l.id !== libraryId) as library (library.id)}
            <label class="scope-library"><input type="checkbox" class="h-4 w-4 shrink-0 accent-accent" checked={compareIds.includes(library.id)} disabled={(compareIds.length >= 15 || !library.available) && !compareIds.includes(library.id)} onchange={event => toggleLibrary(library.id,event.currentTarget.checked)} />
              <span class="min-w-0 break-words text-sm text-ink">{library.name}{#if !library.available}<span class="mt-1 block text-xs text-ink-3">{i18n.m.duplicates.libraryUnavailable}</span>{/if}</span>
            </label>
          {/each}
        </fieldset>
      </details>
    {/if}
    <label class="mt-4 flex min-h-11 items-center gap-3 text-sm text-ink">
      <input type="checkbox" class="h-4 w-4 accent-accent" bind:checked={forceRefresh} disabled={running || acting || loading} aria-describedby="duplicate-refresh-hint" />
      {i18n.m.duplicates.refresh}
    </label>
    <details class="mt-3 text-sm text-ink-3"><summary class="focus-ring cursor-pointer">{i18n.m.duplicates.scanDetails}</summary>
      <p class="mt-3 text-xs leading-relaxed" id="duplicate-refresh-hint">{i18n.m.duplicates.refreshHint}</p>
      <p class="mt-3 leading-relaxed">{i18n.m.duplicates.cost}</p>
      <p class="mt-3 text-xs leading-relaxed">{i18n.m.duplicates.snapshot}</p>
    </details>
    <p class="mt-2 flex min-w-0 items-center gap-1.5 text-sm font-medium text-ink-2"><Icon name="check" class="h-4 w-4 shrink-0 text-ok" /><span class="min-w-0 break-words">{i18n.m.duplicates.safe}</span></p>
  </section>
  {#if error}<Banner kind="error">{error}<button class="btn ml-3" onclick={() => libraryId === null ? loadLibraries() : load(libraryId)}>{i18n.m.setup.retry}</button></Banner>{/if}
  {#if report}
    <section class="card p-5 sm:p-6" aria-busy={loading || running}>
      <div class="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
        <h2 class="text-base font-semibold text-ink" aria-live="polite">{loading ? i18n.m.common.loading_short : statusLabel}</h2>
        {#if report.finishedAt}<span class="text-xs tabular-nums text-ink-3">{new Date(report.finishedAt).toLocaleString()}</span>{/if}
      </div>
      {#if running}
        <div class="scan-progress mt-4">
          <div class="progress-track" role="progressbar" aria-label={i18n.m.duplicates.Running} aria-valuemin="0" aria-valuemax="100" aria-valuenow={scanPercent ?? undefined} aria-valuetext={scanPercent === null ? statusLabel : `${scanPercent}% · ${progressText}`}>
            {#if scanPercent !== null}<div class="progress-fill" style:width={`${scanPercent}%`}></div>
            {:else}<div class="progress-indeterminate"></div>{/if}
          </div>
          {#if scanPercent !== null}<strong class="scan-percent">{scanPercent}%</strong>{/if}
        </div>
      {/if}
      {#if report.result && report.status === 'Completed' && groupCount > 0}<p class="mt-3 text-sm font-medium text-ink">{t(i18n.m.duplicates.found, { count: groupCount })}</p>{/if}
      {#if report.status !== 'NotStarted' && report.status !== 'Queued'}<p class="mt-2 text-sm tabular-nums text-ink-3">{progressText}</p>{/if}
      {#if report.progress.reused}<p class="mt-1 text-sm text-ink-3">{t(i18n.m.duplicates.reused, { count: report.progress.reused })}</p>{/if}
      {#if report.error}<p class="mt-3 text-sm text-bad" role="alert">{report.error}</p>{/if}
      {#if report.result?.groups.length === 0}<p class="mt-3 text-sm text-ink">{i18n.m.duplicates.empty}</p>{/if}
      {#if report.libraries?.length}<div class="mt-4 border-t border-line pt-4"><h3 class="text-xs font-semibold text-ink-2">{i18n.m.duplicates.reportScope}</h3><ul class="mt-2 flex flex-wrap gap-2" aria-label={i18n.m.duplicates.reportScope}>{#each report.libraries as library (library.id)}<li class="badge tone-neutral" title={library.root}>{library.name}</li>{/each}</ul></div>{/if}
    </section>
    {#if cleanupPreview || cleanup || cleanupError}
      <section class="card p-5 sm:p-6" aria-labelledby="duplicate-cleanup" data-testid="duplicate-cleanup">
        <h2 id="duplicate-cleanup" class="text-base font-semibold text-ink">{i18n.m.duplicates.clean_title}</h2>
        {#if cleanupError}<p class="mt-2 text-sm text-bad" role="alert">{cleanupError}</p>{/if}
        {#if cleanup?.running}
          <p class="mt-2 text-sm text-ink-2" role="status">{t(i18n.m.duplicates.clean_running, { done: cleanup.done, total: cleanup.total, moved: cleanup.moved })}</p>
          <button class="btn mt-3 min-h-11" onclick={stopCleanup}>{i18n.m.duplicates.cancel}</button>
        {:else}
          {#if cleanup}
            <p class="mt-2 text-sm text-ink-2" role="status">{t(i18n.m.duplicates.clean_finished, { moved: cleanup.moved, size: formatSize(cleanup.bytesHeld) })} <a class="focus-ring underline" href="#/quarantine">{i18n.m.nav.quarantine}</a></p>
            {#if cleanup.failures.length}
              <ul class="mt-2 space-y-1 text-xs text-bad">{#each cleanup.failures as failure (failure.path + failure.reason)}<li class="break-all">{failure.path}: {failure.reason}</li>{/each}</ul>
            {/if}
          {/if}
          {#if cleanupPreview && cleanupPreview.copies > 0}
            <p class="mt-2 text-sm text-ink-2">{t(i18n.m.duplicates.clean_summary, { copies: cleanupPreview.copies, groups: cleanupPreview.groups, size: formatSize(cleanupPreview.bytesToFree) })}</p>
            <button class="btn btn-primary mt-3 min-h-11" disabled={startingCleanup} onclick={startCleanup}>{i18n.m.duplicates.clean_start}</button>
          {:else if cleanupPreview && !cleanup}
            <p class="mt-2 text-sm text-ink-3">{i18n.m.duplicates.clean_none}</p>
          {/if}
          {#if cleanupPreview && Object.keys(cleanupPreview.leftAlone ?? {}).length > 0}
            <h3 class="mt-4 text-xs font-semibold text-ink-2">{i18n.m.duplicates.left_title}</h3>
            <ul class="mt-1 space-y-1 text-xs text-ink-3">
              {#each leftAloneReasons as [reason, text] (reason)}
                {#if cleanupPreview.leftAlone?.[reason]}<li>{text} ({cleanupPreview.leftAlone[reason]})</li>{/if}
              {/each}
            </ul>
          {/if}
        {/if}
      </section>
    {/if}
    {#if report.isStale}<Banner kind="info">{i18n.m.duplicates.stale}</Banner>{/if}
    {#if report.result?.truncated}<Banner kind="info">{i18n.m.duplicates.truncated}</Banner>{/if}
    {#if scopeChanged}<Banner kind="info">{i18n.m.duplicates.scopeChanged}</Banner>{/if}
    {#each report.result?.groups ?? [] as group (group.sha256 + group.sizeBytes)}
      {#if libraryId !== null}<ExactCopyGroup {group} {libraryId} disabled={scopeChanged || loading || acting || running} onrefresh={refreshReport} />{/if}
    {/each}
    {#if report.result && ((report.result.page ?? 0) > 0 || report.result.hasMore)}
      <nav class="duplicate-pagination card flex flex-wrap items-center justify-between gap-3 p-4" aria-label={i18n.m.duplicates.pages}>
        <button class="btn" disabled={loading || acting || (report.result.page ?? 0) === 0} onclick={() => changePage(-1)}>{i18n.m.duplicates.previous}</button>
        <span class="text-sm text-ink-3" aria-live="polite">{t(i18n.m.duplicates.page, { page: (report.result.page ?? 0) + 1, count: report.result.totalGroups ?? 0 })}</span>
        <button class="btn" disabled={loading || acting || !report.result.hasMore} onclick={() => changePage(1)}>{i18n.m.duplicates.next}</button>
      </nav>
    {/if}
  {:else if loading}<p class="text-sm text-ink-3" role="status">{i18n.m.common.loading_short}</p>{/if}
</div>

<style>
  .duplicate-view { display:grid; gap:1.25rem; width:100%; min-width:0; container-type:inline-size; overflow-wrap:anywhere; }
  .duplicate-view h2 { min-width:0; overflow-wrap:anywhere; }
  .duplicate-controls { display:flex; align-items:end; flex-wrap:wrap; gap:1rem; }
  .duplicate-controls > div { flex-basis:18rem; }
  .duplicate-controls button, .duplicate-pagination button { min-width:0; max-width:100%; min-height:2.75rem; overflow-wrap:anywhere; }
  .scope-grid { display:grid; min-width:0; grid-template-columns:repeat(auto-fit,minmax(min(100%,16rem),1fr)); gap:.5rem; }
  .scope-library { display:flex; align-items:center; gap:.75rem; min-height:2.75rem; padding:.5rem .75rem; border:1px solid var(--line); border-radius:8px; cursor:pointer; }
  .scope-library:has(input:checked) { background:var(--accent-soft); border-color:var(--accent-line); }
  .duplicate-view .badge { min-width:0; max-width:100%; }
  .scan-progress { display:flex; align-items:center; gap:.75rem; }
  .scan-progress .progress-track { height:.5rem; }
  .scan-percent { min-width:3ch; font-size:1.125rem; font-weight:600; color:var(--ink); font-variant-numeric:tabular-nums; text-align:right; }
  @container (max-width:20rem) {
    .duplicate-view > section, .scope-picker { padding:.5rem; }
    .scope-library { flex-direction:column; align-items:start; gap:.25rem; padding:.5rem; }
    .scope-library > span { width:100%; }
  }
  @media (max-width:639px) { .duplicate-controls button { width:100%; } }
</style>
