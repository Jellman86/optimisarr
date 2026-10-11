<script lang="ts">
  import { onMount, onDestroy } from 'svelte'
  import { api, type Library, type ExactDuplicateStatus } from '../api'
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
  let scopeExpanded = $state(false)
  let disposed = false
  let revision = 0
  let loadRequest = 0
  let timer: ReturnType<typeof setTimeout> | undefined
  let running = $derived(report?.status === 'Queued' || report?.status === 'Running')
  let scopeChanged = $derived(!!report?.libraries?.length && JSON.stringify(report.libraries.map(l => l.id).sort((a,b) => a-b)) !== JSON.stringify([libraryId, ...compareIds].sort((a,b) => (a ?? 0)-(b ?? 0))))
  let scopeLibraries = $derived([...libraries.map(l => ({ id:l.id, name:l.name, available:true })),
    ...(report?.libraries ?? []).filter(l => !libraries.some(current => current.id === l.id)).map(l => ({ id:l.id, name:l.name, available:false }))])
  let statusLabel = $derived(report ? i18n.m.duplicates[report.status] : i18n.m.duplicates.NotStarted)
  onMount(() => { void loadLibraries() })
  onDestroy(() => { disposed = true; revision++; clearTimeout(timer) })

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
    forceRefresh = false; compareIds = []; restoreScope = true; scopeExpanded = false
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
      <div class="flex flex-wrap items-center justify-between gap-3"><h2 class="text-base font-semibold text-ink" aria-live="polite">{loading ? i18n.m.common.loading_short : statusLabel}</h2>{#if report.finishedAt}<span class="text-xs text-ink-3">{new Date(report.finishedAt).toLocaleString()}</span>{/if}</div>
      {#if report.status !== 'NotStarted'}<p class="mt-3 text-sm text-ink-3">{t(i18n.m.duplicates.progress, { checked: report.progress.checked, total: report.progress.total, skipped: report.progress.skipped, bytes: formatSize(report.progress.bytesRead) })}</p>{/if}
      {#if report.progress.reused}<p class="mt-2 text-sm text-ink-2">{t(i18n.m.duplicates.reused, { count: report.progress.reused })}</p>{/if}
      {#if running}<progress class="mt-4 w-full accent-accent" max={Math.max(1,report.progress.total)} value={report.progress.checked + report.progress.skipped} aria-label={i18n.m.duplicates.Running}></progress>{/if}
      {#if report.error}<p class="mt-3 text-sm text-bad" role="alert">{report.error}</p>{/if}
      {#if report.result?.groups.length === 0}<p class="mt-4 text-sm text-ink">{i18n.m.duplicates.empty}</p>{/if}
      {#if report.libraries?.length}<div class="mt-4 border-t border-line pt-4"><h3 class="text-xs font-semibold text-ink-2">{i18n.m.duplicates.reportScope}</h3><ul class="mt-2 flex flex-wrap gap-2" aria-label={i18n.m.duplicates.reportScope}>{#each report.libraries as library (library.id)}<li class="badge bg-raised text-ink-2" title={library.root}>{library.name}</li>{/each}</ul></div>{/if}
    </section>
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
  @container (max-width:20rem) {
    .duplicate-view > section, .scope-picker { padding:.5rem; }
    .scope-library { flex-direction:column; align-items:start; gap:.25rem; padding:.5rem; }
    .scope-library > span { width:100%; }
  }
  @media (max-width:639px) { .duplicate-controls button { width:100%; } }
</style>
