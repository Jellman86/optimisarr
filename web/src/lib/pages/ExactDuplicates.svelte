<script lang="ts">
  import { onMount, onDestroy } from 'svelte'
  import { api, type Library, type ExactDuplicateStatus } from '../api'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { formatSize } from '../format'
  import Banner from '../components/Banner.svelte'
  import Icon from '../components/Icon.svelte'
  import Thumbnail from '../components/Thumbnail.svelte'

  let libraries = $state<Library[]>([])
  let libraryId = $state<number | null>(null)
  let report = $state<ExactDuplicateStatus | null>(null)
  let error = $state<string | null>(null)
  let loading = $state(false)
  let acting = $state(false)
  let disposed = false
  let revision = 0
  let loadRequest = 0
  let timer: ReturnType<typeof setTimeout> | undefined
  let running = $derived(report?.status === 'Queued' || report?.status === 'Running')
  let statusLabel = $derived(report ? i18n.m.duplicates[report.status] : i18n.m.duplicates.NotStarted)
  onMount(() => { void loadLibraries() })
  onDestroy(() => { disposed = true; revision++; clearTimeout(timer) })

  async function loadLibraries() {
    try { libraries = await api.libraries() }
    catch (err) { if (!disposed) error = err instanceof Error ? err.message : i18n.m.inventory.error_load_libraries }
  }
  async function load(id: number, request = revision) {
    const sequence = ++loadRequest
    try {
      const next = await api.exactDuplicates(id)
      if (disposed || request !== revision || sequence !== loadRequest) return
      report = next; error = null
      clearTimeout(timer)
      if (next.status === 'Running' || next.status === 'Queued') timer = setTimeout(() => void load(id, request), 1500)
    } catch (err) { if (!disposed && request === revision && sequence === loadRequest) error = err instanceof Error ? err.message : i18n.m.inventory.error_load }
    finally { if (!disposed && request === revision && sequence === loadRequest) loading = false }
  }
  function select(event: Event) {
    const value = (event.currentTarget as HTMLSelectElement).value
    revision++; clearTimeout(timer); report = null; error = null
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
      else await api.scanExactDuplicates(id)
      await load(id, request)
    } catch (err) { if (!disposed && request === revision) error = err instanceof Error ? err.message : i18n.m.inventory.error_load }
    finally { if (!disposed) acting = false }
  }
</script>

<div class="duplicate-view">
  <nav class="flex flex-wrap items-center gap-2 text-sm text-ink-3" aria-label={i18n.m.libraryWorkflow.breadcrumb}>
    <a class="focus-ring hover:text-accent" href="#/inventory">{i18n.m.nav.inventory}</a><Icon name="chevron" class="h-4 w-4 -rotate-90" /><span aria-current="page">{i18n.m.duplicates.title}</span>
  </nav>
  <header><h1 class="page-title">{i18n.m.duplicates.title}</h1><p class="page-subtitle">{i18n.m.duplicates.subtitle}</p></header>
  <section class="card p-5 sm:p-6" aria-label={i18n.m.duplicates.title}>
    <div class="duplicate-controls">
      <div class="min-w-0 flex-1"><label class="label" for="duplicate-library">{i18n.m.inventory.library_label}</label><select class="input" id="duplicate-library" value={libraryId ?? ''} onchange={select} disabled={acting}><option value="">{i18n.m.duplicates.choose}</option>{#each libraries as library}<option value={library.id}>{library.name}</option>{/each}</select></div>
      {#if running}<button class="btn" disabled={acting} onclick={() => act(true)}>{i18n.m.duplicates.cancel}</button>
      {:else}<button class="btn btn-primary" disabled={libraryId === null || loading || acting} onclick={() => act()}>{i18n.m.duplicates.scan}</button>{/if}
    </div>
    <p class="mt-4 text-sm leading-relaxed text-ink-3">{i18n.m.duplicates.cost}</p>
    <p class="mt-2 inline-flex items-center gap-1.5 text-sm font-medium text-ink-2"><Icon name="check" class="h-4 w-4 text-ok" />{i18n.m.duplicates.safe}</p>
  </section>
  {#if error}<Banner kind="error">{error}<button class="btn ml-3" onclick={() => libraryId === null ? loadLibraries() : load(libraryId)}>{i18n.m.setup.retry}</button></Banner>{/if}
  {#if report}
    <section class="card p-5 sm:p-6" aria-busy={loading || running}>
      <div class="flex flex-wrap items-center justify-between gap-3"><h2 class="text-base font-semibold text-ink" aria-live="polite">{loading ? i18n.m.common.loading_short : statusLabel}</h2>{#if report.finishedAt}<span class="text-xs text-ink-3">{new Date(report.finishedAt).toLocaleString()}</span>{/if}</div>
      {#if report.status !== 'NotStarted'}<p class="mt-3 text-sm text-ink-3">{t(i18n.m.duplicates.progress, { checked: report.progress.checked, total: report.progress.total, skipped: report.progress.skipped, bytes: formatSize(report.progress.bytesRead) })}</p>{/if}
      {#if running}<progress class="mt-4 w-full accent-accent" max={Math.max(1,report.progress.total)} value={report.progress.checked + report.progress.skipped} aria-label={i18n.m.duplicates.Running}></progress>{/if}
      {#if report.error}<p class="mt-3 text-sm text-bad" role="alert">{report.error}</p>{/if}
      {#if report.result?.groups.length === 0}<p class="mt-4 text-sm text-ink">{i18n.m.duplicates.empty}</p>{/if}
      <p class="mt-3 text-xs leading-relaxed text-ink-4">{i18n.m.duplicates.snapshot}</p>
    </section>
    {#if report.result?.truncated}<Banner kind="info">{i18n.m.duplicates.truncated}</Banner>{/if}
    {#each report.result?.groups ?? [] as group (group.sha256 + group.sizeBytes)}
      <details class="card card-interactive duplicate-group p-5 sm:p-6" open={group.copies.length <= 5}>
        <summary class="focus-ring flex min-w-0 items-start gap-4">
          <Thumbnail mediaFileId={group.copies[0].id} shape="square" size="md" />
          <div class="min-w-0 flex-1"><h2 class="text-base font-semibold text-ink">{t(i18n.m.duplicates.copies,{count:group.totalCopies || group.copies.length})}</h2><p class="mt-1 text-sm text-ink-3">{formatSize(group.sizeBytes)} · {i18n.m.duplicates.fullBytes}</p><code class="mt-2 block break-all text-xs text-ink-4" title={group.sha256}>SHA-256 {group.sha256.slice(0,16)}…</code></div>
          <Icon name="chevron" class="h-4 w-4 shrink-0 text-ink-3" />
        </summary>
        <ul class="duplicate-paths mt-5">{#each group.copies as copy (copy.id)}<li><span class="break-all font-mono text-sm text-ink">{copy.relativePath}</span><span class="mt-1 block text-xs text-ink-3">{copy.hardLinkCount !== null && copy.hardLinkCount > 1 ? t(i18n.m.duplicates.linked,{count:copy.hardLinkCount}) : copy.hardLinkCount === null ? i18n.m.duplicates.unknown : i18n.m.duplicates.independent}</span></li>{/each}</ul>
        <p class="mt-4 text-sm text-ink-3">{group.extraCopyBytes === null ? i18n.m.duplicates.spaceUnknown : t(i18n.m.duplicates.extra,{bytes:formatSize(group.extraCopyBytes)})}</p>
      </details>
    {/each}
  {:else if loading}<p class="text-sm text-ink-3" role="status">{i18n.m.common.loading_short}</p>{/if}
</div>

<style>
  .duplicate-view { display:grid; gap:1.25rem; width:100%; min-width:0; }
  .duplicate-controls { display:flex; align-items:end; flex-wrap:wrap; gap:1rem; }
  .duplicate-controls > div { flex-basis:18rem; }
  .duplicate-controls button { min-height:2.75rem; }
  .duplicate-paths { display:grid; gap:.75rem; list-style:none; padding:0; }
  .duplicate-paths li { border-left:2px solid var(--line); padding:.5rem .85rem; min-width:0; }
  summary { list-style:none; }
  summary::-webkit-details-marker { display:none; }
  details[open] summary > :global(svg:last-child) { transform:rotate(180deg); }
  @media (max-width:639px) { .duplicate-controls button { width:100%; } }
</style>
