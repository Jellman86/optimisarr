<script lang="ts">
  import { tick } from 'svelte'
  import { api, type Replacement, type ReplacementDetail, type VerificationCheck, type VerificationReport } from '../api'
  import { formatSize } from '../format'
  import { i18n, t, plural } from '../i18n/i18n.svelte'
  import { router } from '../stores/ui.svelte'
  import Banner from '../components/Banner.svelte'
  import Icon from '../components/Icon.svelte'
  import Thumbnail from '../components/Thumbnail.svelte'
  import VerificationChecks from '../components/VerificationChecks.svelte'
  import MediaCompare from '../components/MediaCompare.svelte'

  let replacements = $state<Replacement[]>([])
  let error = $state<string | null>(null)
  let loadError = $state<string | null>(null)
  let loading = $state(true)
  let busyId = $state<number | null>(null)
  let bulkAction = $state<'approve' | 'reject' | null>(null)
  let clearing = $state(false)
  const busy = $derived(busyId !== null || bulkAction !== null || clearing)
  const reviewOpen = $derived(router.path !== '/quarantine')
  const selectedId = $derived(Number(router.path.match(/^\/quarantine\/(\d+)$/)?.[1]) || null)
  let detail = $state<ReplacementDetail | null>(null)
  let detailError = $state<string | null>(null)
  let detailLoading = $state(false)
  let detailRequest = 0
  let listRequest = 0
  let heading: HTMLHeadingElement | undefined = $state()
  let tableScrollEl: HTMLDivElement | undefined = $state()
  let returnId: number | null = null
  let listScrollTop = 0
  let mainScrollTop = 0
  let wasReviewOpen = false
  const selected = $derived(detail?.id === selectedId ? detail : null)
  const fileName = (path: string) => path.split(/[\\/]/).pop() || path

  $effect(() => { void load(); return () => { listRequest++ } })
  $effect(() => {
    const id = selectedId
    detail = null
    detailError = null
    detailLoading = id !== null
    error = null
    if (id !== null) void loadDetail(id)
    return () => { detailRequest++ }
  })
  $effect(() => {
    const open = reviewOpen
    const id = selectedId
    const restore = wasReviewOpen && !open
    wasReviewOpen = open
    void tick().then(() => {
      if (open !== reviewOpen || id !== selectedId) return
      if (open) {
        heading?.focus({ preventScroll: true })
        document.querySelector('main')?.scrollTo({ top: 0 })
      } else if (restore) {
        if (tableScrollEl) tableScrollEl.scrollTop = listScrollTop
        document.querySelector('main')?.scrollTo({ top: mainScrollTop })
        document.getElementById(`replacement-${returnId}`)?.focus({ preventScroll: true })
      }
    })
  })

  function rememberList(id: number) {
    returnId = id
    listScrollTop = tableScrollEl?.scrollTop ?? 0
    mainScrollTop = document.querySelector('main')?.scrollTop ?? 0
  }
  async function load() {
    const request = ++listRequest
    try {
      const result = await api.replacements()
      if (request !== listRequest) return
      replacements = result
      loadError = null
    } catch (err) {
      if (request === listRequest) loadError = err instanceof Error ? err.message : i18n.m.quarantine.error_load
    } finally {
      if (request === listRequest) loading = false
    }
  }
  async function loadDetail(id: number) {
    const request = ++detailRequest
    detailLoading = true
    detailError = null
    try {
      const result = await api.replacement(id)
      if (request !== detailRequest) return
      detail = result
      await tick()
      if (request === detailRequest) heading?.focus({ preventScroll: true })
    } catch (err) {
      if (request === detailRequest) detailError = err instanceof Error ? err.message : i18n.m.quarantine.error_comparison
    } finally {
      if (request === detailRequest) detailLoading = false
    }
  }
  async function decide(r: Replacement, action: 'approve' | 'reject') {
    if (busy || r.status !== 'Replaced') return
    const confirmation = action === 'approve'
      ? t(i18n.m.quarantine.confirm_approve, { path: r.quarantinePath })
      : t(i18n.m.quarantine.confirm_reject, { path: r.originalPath })
    if (!confirm(confirmation)) return
    busyId = r.id
    error = null
    try {
      if (action === 'approve') await api.approveReplacement(r.id)
      else await api.rollbackReplacement(r.id)
      await load()
      if (selectedId === r.id) router.go('/quarantine')
    } catch (err) {
      error = err instanceof Error ? err.message : action === 'approve' ? i18n.m.quarantine.error_approve : i18n.m.quarantine.error_rollback
    } finally { busyId = null }
  }
  async function approveAll() {
    if (!confirm(t(i18n.m.quarantine.confirm_approve_all, { count: activeCount }))) return
    error = null
    bulkAction = 'approve'
    let failed = 0
    for (const replacement of replacements.filter((r) => r.status === 'Replaced')) {
      try {
        await api.approveReplacement(replacement.id)
      } catch {
        failed++
      }
    }
    if (failed > 0)
      error = plural(failed, i18n.m.quarantine.bulk_approve_failed_one, i18n.m.quarantine.bulk_approve_failed_other)
    await load()
    bulkAction = null
  }

  async function rejectAll() {
    if (!confirm(t(i18n.m.quarantine.confirm_reject_all, { count: activeCount }))) return
    error = null
    bulkAction = 'reject'
    let failed = 0
    for (const replacement of replacements.filter((r) => r.status === 'Replaced')) {
      try {
        await api.rollbackReplacement(replacement.id)
      } catch {
        failed++
      }
    }
    if (failed > 0)
      error = plural(failed, i18n.m.quarantine.bulk_reject_failed_one, i18n.m.quarantine.bulk_reject_failed_other)
    await load()
    bulkAction = null
  }

  async function clearSpent() {
    if (
      !confirm(
        plural(spentCount, i18n.m.quarantine.confirm_clear_one, i18n.m.quarantine.confirm_clear_other),
      )
    )
      return
    error = null
    clearing = true
    try {
      await api.clearReplacements()
      await load()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.quarantine.error_clear
    } finally {
      clearing = false
    }
  }

  function savingPercent(r: { originalSizeBytes: number; newSizeBytes: number }): number {
    if (r.originalSizeBytes <= 0) return 0
    return Math.round((1 - r.newSizeBytes / r.originalSizeBytes) * 100)
  }

  function parseChecks(detail: ReplacementDetail | undefined): VerificationCheck[] | null {
    if (!detail?.verificationReportJson) return null
    try {
      return (JSON.parse(detail.verificationReportJson) as VerificationReport).checks ?? null
    } catch {
      return null
    }
  }

  let activeCount = $derived(replacements.filter((r) => r.status === 'Replaced').length)
  let spentCount = $derived(replacements.filter((r) => r.status === 'Purged' || r.status === 'RolledBack').length)

</script>

{#if error && !reviewOpen}<Banner kind="error" class="mb-4">{error}</Banner>{/if}
{#if reviewOpen}
  <div data-quarantine-review class="space-y-5">
    <nav class="flex min-h-11 flex-wrap items-center gap-2 text-sm text-ink-3" aria-label={i18n.m.libraryWorkflow.breadcrumb}>
      <a class="focus-ring inline-flex min-h-11 items-center rounded px-1 hover:text-accent" href="#/quarantine">{i18n.m.nav.quarantine}</a>
      <span aria-hidden="true">/</span><span aria-current="page" class="text-ink">{i18n.m.quarantine.review_title}</span>
    </nav>
    <header class="card flex items-center gap-5 p-5 sm:p-6">
      {#if selected}<Thumbnail mediaFileId={selected.mediaFileId} size="md" />{/if}
      <div class="min-w-0 flex-1">
        <h1 class="page-title break-words outline-none" tabindex="-1" bind:this={heading}>{selected ? fileName(selected.finalPath) : i18n.m.quarantine.review_title}</h1>
        {#if selected}
          <div class="mt-3 flex flex-wrap items-center gap-3 text-xs text-ink-3">
            <span class="badge {selected.status === 'Replaced' ? 'tone-ok' : 'tone-neutral'}">{selected.status === 'Replaced' ? i18n.m.quarantine.status_replaced : selected.status === 'Purged' ? i18n.m.quarantine.status_purged : i18n.m.quarantine.status_rolled_back}</span>
            <span>{new Date(selected.replacedAt).toLocaleString()}</span>
            {#if selected.crossFilesystem}<span class="badge tone-warn" title={i18n.m.quarantine.copied_title}>{i18n.m.quarantine.copied}</span>{/if}
          </div>
        {/if}
      </div>
    </header>
    {#if detailLoading}
      <div class="card p-8 text-center text-ink-3" role="status">{i18n.m.quarantine.loading_comparison}</div>
    {:else if detailError}
      <Banner kind="error">{detailError}<button class="btn ml-3" onclick={() => selectedId && loadDetail(selectedId)}>{i18n.m.setup.retry}</button></Banner>
    {:else if selected}
      {@const r = selected}
      {@const checks = parseChecks(r)}
      <div class="review-metrics grid grid-cols-3 gap-2 sm:gap-4">
        <div class="card p-3 sm:p-5"><p class="min-h-10 text-xs text-ink-3 sm:min-h-0">{r.status === 'Replaced' ? i18n.m.quarantine.original_quarantined : i18n.m.shared.original}</p><p class="mt-2 text-base font-semibold tabular-nums sm:text-xl">{formatSize(r.originalSizeBytes)}</p></div>
        <div class="card p-3 sm:p-5"><p class="min-h-10 text-xs text-ink-3 sm:min-h-0">{r.status === 'Replaced' ? i18n.m.quarantine.replacement_in_place : i18n.m.shared.encoded}</p><p class="mt-2 text-base font-semibold tabular-nums sm:text-xl">{formatSize(r.newSizeBytes)}</p></div>
        <div class="card p-3 sm:p-5"><p class="min-h-10 text-xs text-ink-3 sm:min-h-0">{i18n.m.quarantine.col_saving}</p><p class="mt-2 text-base font-semibold tabular-nums sm:text-xl text-accent">{formatSize(r.originalSizeBytes - r.newSizeBytes)} <span class="text-sm text-ink-3">({savingPercent(r)}%)</span></p></div>
      </div>
      {#if r.status === 'Replaced'}
        <section class="card p-4 sm:p-6" aria-labelledby="quarantine-compare">
          <h2 id="quarantine-compare" class="mb-4 text-base font-semibold">{i18n.m.quarantine.compare_title}</h2>
          {#key r.id}
            <MediaCompare mediaKind={r.mediaKind}
              left={{ label: i18n.m.quarantine.original_quarantined, url: api.replacementOriginalContentUrl(r.id), sizeBytes: r.originalSizeBytes }}
              right={{ label: i18n.m.quarantine.replacement_in_place, url: api.replacementReplacementContentUrl(r.id), sizeBytes: r.newSizeBytes }} />
          {/key}
          <details class="mt-5 border-t border-line pt-4">
            <summary class="focus-ring cursor-pointer rounded py-2 text-sm text-ink-2">{i18n.m.quarantine.file_locations}</summary>
            <dl class="mt-3 grid gap-4 text-xs sm:grid-cols-2">
              <div class="min-w-0"><dt class="text-ink-3">{i18n.m.quarantine.original_quarantined}</dt><dd class="mt-2 break-all font-mono text-ink-2">{r.quarantinePath}</dd></div>
              <div class="min-w-0"><dt class="text-ink-3">{i18n.m.quarantine.replacement_in_place}</dt><dd class="mt-2 break-all font-mono text-ink-2">{r.finalPath}</dd></div>
            </dl>
          </details>
        </section>
      {:else}<div class="card p-5 text-sm leading-relaxed text-ink-3">{i18n.m.quarantine.finished_note}</div>{/if}
      <section class="card p-5 sm:p-6" aria-labelledby="quarantine-verification">
        <div class="mb-4 flex flex-wrap items-center gap-3"><h2 id="quarantine-verification" class="text-base font-semibold">{i18n.m.quarantine.verification}</h2>
          {#if r.verificationPassed !== null}<span class="badge {r.verificationPassed ? 'tone-ok' : 'tone-bad'}">{r.verificationPassed ? i18n.m.quarantine.passed : i18n.m.quarantine.failed}</span>{/if}
        </div>
        {#if checks}<VerificationChecks {checks} />{:else}<p class="text-sm text-ink-3">{i18n.m.quarantine.no_report}</p>{/if}
      </section>
      {#if r.status === 'Replaced'}
        <section class="card p-5 sm:p-6" aria-labelledby="quarantine-decision">
          <h2 id="quarantine-decision" class="text-base font-semibold">{i18n.m.quarantine.decision_title}</h2>
          <p class="mt-2 max-w-3xl text-sm leading-relaxed text-ink-3">{i18n.m.quarantine.action_note}</p>
          {#if error}<div role="alert"><Banner kind="error" class="mt-4">{error}</Banner></div>{/if}
          <div class="mt-5 flex flex-wrap gap-3">
            <button class="btn btn-primary min-h-11" onclick={() => decide(r, 'approve')} disabled={busy}><Icon name="check" />{busyId === r.id ? i18n.m.quarantine.working : i18n.m.quarantine.approve_free_space}</button>
            <button class="btn btn-danger min-h-11" onclick={() => decide(r, 'reject')} disabled={busy}><Icon name="rotate" />{i18n.m.quarantine.reject_roll_back}</button>
          </div>
        </section>
      {/if}
    {:else}<div class="card p-8 text-sm text-ink-3">{i18n.m.quarantine.unavailable}</div>{/if}
    <a class="btn min-h-11" href="#/quarantine"><Icon name="arrow-left" />{i18n.m.nav.quarantine}</a>
  </div>
{:else}
<header class="mb-6">
  <h1 class="page-title">{i18n.m.nav.quarantine}</h1>
  <p class="text-sm text-ink-3">
    {i18n.m.quarantine.subtitle_1}<strong>{i18n.m.quarantine.approve_word}</strong>{i18n.m.quarantine.subtitle_2}<strong>{i18n.m.quarantine.reject_word}</strong>{i18n.m.quarantine.subtitle_3}
    {#if activeCount > 0}<span class="text-ink-4">{t(i18n.m.quarantine.count_suffix, { count: activeCount })}</span>{/if}
  </p>
</header>

{#if loadError}<Banner kind="error" class="mb-4">{loadError}<button class="btn ml-3" onclick={load}>{i18n.m.setup.retry}</button></Banner>{/if}

{#if activeCount > 0 || spentCount > 0}
  <div class="mb-4 flex flex-wrap items-center gap-2">
    {#if activeCount > 0}
      <button class="btn btn-primary px-3 py-1.5 text-sm" onclick={approveAll} disabled={busy}>
        <Icon name="check" class="h-4 w-4" />
        {bulkAction === 'approve' ? i18n.m.quarantine.approving_all : t(i18n.m.quarantine.approve_all, { count: activeCount })}
      </button>
      <button class="btn btn-danger px-3 py-1.5 text-sm" onclick={rejectAll} disabled={busy}>
        <Icon name="rotate" class="h-4 w-4" />
        {bulkAction === 'reject' ? i18n.m.quarantine.rolling_back_all : t(i18n.m.quarantine.reject_all, { count: activeCount })}
      </button>
    {/if}
    {#if spentCount > 0}
      <button class="btn btn-ghost px-3 py-1.5 text-sm" onclick={clearSpent} disabled={busy}>
        <Icon name="trash" class="h-4 w-4" />
        {clearing ? i18n.m.quarantine.clearing : t(i18n.m.quarantine.clear_finished, { count: spentCount })}
      </button>
    {/if}
    <span class="text-xs text-ink-4">{i18n.m.quarantine.bulk_note}</span>
  </div>
{/if}

{#if loading}
  <div class="card p-8 text-center text-ink-4">{i18n.m.common.loading_short}</div>
{:else if replacements.length > 0}
  <div class="card overflow-hidden">
    <div bind:this={tableScrollEl} data-quarantine-list class="max-h-[65vh] overflow-auto">
      <table class="quarantine-table w-full table-fixed text-sm">
        <thead class="table-head">
          <tr>
            <th class="px-4 py-3">{i18n.m.quarantine.col_status}</th>
            <th class="px-4 py-3">{i18n.m.quarantine.col_replaced_file}</th>
            <th class="hidden px-4 py-3 sm:table-cell">{i18n.m.quarantine.col_saving}</th>
            <th class="hidden px-4 py-3 md:table-cell">{i18n.m.quarantine.col_replaced}</th>
          </tr>
        </thead>
        <tbody class="divide-y divide-line-soft">
          {#each replacements as r (r.id)}
            <tr
              class="text-ink-2 hover:bg-lit"
            >
              <td class="px-4 py-2">
                {#if r.status === 'Replaced'}
                  <span class="badge tone-ok">{i18n.m.quarantine.status_replaced}</span>
                {:else if r.status === 'Purged'}
                  <span class="badge tone-muted" title={i18n.m.quarantine.status_purged_title}>{i18n.m.quarantine.status_purged}</span>
                {:else}
                  <span class="badge tone-muted">{i18n.m.quarantine.status_rolled_back}</span>
                {/if}
                {#if r.crossFilesystem}
                  <span class="badge ml-1 tone-warn" title={i18n.m.quarantine.copied_title}>{i18n.m.quarantine.copied}</span>
                {/if}
              </td>
              <td class="px-4 py-2">
                <a id={`replacement-${r.id}`} class="focus-ring flex min-h-11 items-center gap-2 rounded text-sm font-medium text-ink hover:text-accent" href={`#/quarantine/${r.id}`} onclick={() => rememberList(r.id)}><span class="min-w-0 truncate" title={r.finalPath}>{fileName(r.finalPath)}</span><Icon name="arrow-right" class="h-4 w-4 shrink-0 text-ink-3" /></a>
                {#if r.status === 'Purged'}
                  <div class="text-[11px] text-ink-4">{i18n.m.quarantine.original_purged}</div>
                {:else if r.status === 'Replaced'}
                  <div class="max-w-md truncate font-mono text-[11px] text-ink-4" title={r.quarantinePath}>{t(i18n.m.quarantine.original_in, { path: r.quarantinePath })}</div>
                {/if}
              </td>
              <td class="hidden px-4 py-2 text-xs tabular-nums sm:table-cell">
                {formatSize(r.originalSizeBytes)} → {formatSize(r.newSizeBytes)}
                <span class="text-ok"> (−{savingPercent(r)}%)</span>
              </td>
              <td class="hidden px-4 py-2 text-xs text-ink-3 md:table-cell">{new Date(r.replacedAt).toLocaleString()}</td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  </div>
  <p class="mt-2 text-xs text-ink-4">{t(i18n.m.quarantine.replacements_count, { count: replacements.length.toLocaleString() })}</p>
{:else}
  <div class="card p-8 text-center text-ink-3">
    {i18n.m.quarantine.empty}
  </div>
{/if}


{/if}

<style>
  .quarantine-table th:first-child { width: 8rem; }
  .quarantine-table th:nth-child(3) { width: 11rem; }
  .quarantine-table th:nth-child(4) { width: 11rem; }
  [data-quarantine-review] :global(.card) { transition: box-shadow 180ms ease; }
  [data-quarantine-review] :global(.card:hover), [data-quarantine-review] :global(.card:focus-within) { box-shadow: var(--lift-3), inset 0 1px 0 var(--edge); }
  @media (max-width: 639px) { .quarantine-table th:first-child { width: 7rem; } }
  @media (prefers-reduced-motion: reduce) { [data-quarantine-review] :global(.card) { transition: none; } }
</style>
