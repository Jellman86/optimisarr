<script lang="ts">
  import { tick } from 'svelte'
  import { api, type QuarantinedDuplicates, type Replacement, type ReplacementDetail, type VerificationCheck, type VerificationReport } from '../api'
  import { formatAbsoluteDateTime, formatDateTime, formatSize, formatSizeChangeBytes } from '../format'
  import { i18n, t, plural } from '../i18n/i18n.svelte'
  import { router } from '../stores/ui.svelte'
  import Banner from '../components/Banner.svelte'
  import PageHeader from '../components/PageHeader.svelte'
  import DataTable from '../components/DataTable.svelte'
  import Segmented from '../components/Segmented.svelte'
  import Icon from '../components/Icon.svelte'
  import InfoTip from '../components/InfoTip.svelte'
  import SizeChange from '../components/SizeChange.svelte'
  import Thumbnail from '../components/Thumbnail.svelte'
  import VerificationChecks from '../components/VerificationChecks.svelte'
  import AudioQualityReport from '../components/AudioQualityReport.svelte'
  import SoundtrackQualityReport from '../components/SoundtrackQualityReport.svelte'
  import ImagePerceptualReport from '../components/ImagePerceptualReport.svelte'
  import PerceptualSearchReport from '../components/PerceptualSearchReport.svelte'
  import MediaCompare from '../components/MediaCompare.svelte'
  import PosterGlow from '../components/PosterGlow.svelte'

  let replacements = $state<Replacement[]>([])
  let duplicates = $state<QuarantinedDuplicates[]>([])
  let chosenKind = $state<'all' | 'optimisation' | 'duplicate'>('all')
  // The filter only exists while both kinds do, so a choice cannot hide what is left.
  const kind = $derived(duplicates.length > 0 && replacements.length > 0 ? chosenKind : 'all')
  let busyDuplicateId = $state<string | null>(null)
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
        const target = (returnId !== null ? document.getElementById(`replacement-${returnId}`) : null)
          ?? document.querySelector<HTMLElement>('main [data-page-heading]')
        target?.focus({ preventScroll: true })
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
      const [result, spares] = await Promise.all([api.replacements(), api.quarantinedDuplicates()])
      if (request !== listRequest) return
      replacements = result
      duplicates = spares
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

  async function changeDuplicates(group: QuarantinedDuplicates, action: 'restore' | 'delete') {
    if (action === 'delete' && !confirm(i18n.m.quarantine.duplicate_confirm_delete)) return
    error = null
    busyDuplicateId = group.id
    try {
      if (action === 'restore') await api.restoreQuarantinedDuplicates(group.id)
      else await api.deleteQuarantinedDuplicates(group.id)
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.quarantine.duplicate_error
    }
    await load()
    busyDuplicateId = null
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

  function searchReport(detail: ReplacementDetail | null | undefined) {
    try { return detail?.verificationReportJson ? (JSON.parse(detail.verificationReportJson) as VerificationReport).perceptualSearch ?? null : null }
    catch { return null }
  }
  function audioReport(detail: ReplacementDetail | null | undefined) {
    try { return detail?.verificationReportJson ? (JSON.parse(detail.verificationReportJson) as VerificationReport).audioQuality ?? null : null }
    catch { return null }
  }
  function imageReport(detail: ReplacementDetail | null | undefined) {
    try { return detail?.verificationReportJson ? (JSON.parse(detail.verificationReportJson) as VerificationReport).imagePerceptualQuality ?? null : null }
    catch { return null }
  }
  function soundtrackReport(detail: ReplacementDetail | null | undefined) {
    try { return detail?.verificationReportJson ? (JSON.parse(detail.verificationReportJson) as VerificationReport).soundtrackQuality ?? null : null }
    catch { return null }
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
    <nav class="flex flex-wrap items-center gap-2 text-sm text-ink-3" aria-label={i18n.m.libraryWorkflow.breadcrumb}>
      <a class="focus-ring hit-target rounded px-1 hover:text-accent" href="#/quarantine">{i18n.m.nav.quarantine}</a>
      <span aria-hidden="true">/</span><span aria-current="page" class="text-ink">{i18n.m.quarantine.review_title}</span>
    </nav>
    <header class="card relative flex items-center gap-5 overflow-hidden p-5 sm:p-6">
      {#if selected}<PosterGlow mediaFileId={selected.mediaFileId} /><div class="relative"><Thumbnail mediaFileId={selected.mediaFileId} size="lg" /></div>{/if}
      <div class="relative min-w-0 flex-1">
        <h1 class="page-title break-words outline-none" tabindex="-1" data-page-heading bind:this={heading}>{selected ? fileName(selected.finalPath) : i18n.m.quarantine.review_title}</h1>
        {#if selected}
          <div class="mt-3 flex flex-wrap items-center gap-3 text-xs text-ink-3">
            <span class="inline-flex items-center gap-4"><span class="badge {selected.status === 'Replaced' ? 'tone-ok' : 'tone-neutral'}">{selected.status === 'Replaced' ? i18n.m.quarantine.status_replaced : selected.status === 'Purged' ? i18n.m.quarantine.status_purged : i18n.m.quarantine.status_rolled_back}</span>{#if selected.status === 'Purged'}<InfoTip label={t(i18n.m.common.about_information, { label: i18n.m.quarantine.status_purged })} text={i18n.m.quarantine.status_purged_title} />{/if}</span>
            <time datetime={selected.replacedAt} title={formatAbsoluteDateTime(selected.replacedAt, i18n.locale)}>{formatDateTime(selected.replacedAt, new Date(), i18n.locale)}</time>
            {#if selected.crossFilesystem}<span class="inline-flex items-center gap-4"><span class="badge tone-warn">{i18n.m.quarantine.copied}</span><InfoTip label={t(i18n.m.common.about_information, { label: i18n.m.quarantine.copied })} text={i18n.m.quarantine.copied_title} /></span>{/if}
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
        <div class="card p-3 sm:p-5"><p class="min-h-10 break-words text-xs text-ink-3 sm:min-h-0">{i18n.m.quarantine.col_size_change}</p><p class="mt-2 text-base font-semibold tabular-nums sm:text-xl">{formatSizeChangeBytes(r.originalSizeBytes, r.newSizeBytes)} <span class="whitespace-nowrap text-sm">(<SizeChange sourceSizeBytes={r.originalSizeBytes} outputSizeBytes={r.newSizeBytes} />)</span></p></div>
      </div>
      {#if r.status === 'Replaced'}
        <section class="card p-4 sm:p-6" aria-labelledby="quarantine-compare">
          <h2 id="quarantine-compare" class="mb-4 text-base font-semibold">{i18n.m.quarantine.compare_title}</h2>
          {#key r.id}
            <MediaCompare mediaKind={r.mediaKind} poster={api.jobArtworkUrl(r.jobId)}
              left={{ label: i18n.m.quarantine.original_quarantined, url: api.replacementOriginalContentUrl(r.id), sizeBytes: r.originalSizeBytes }}
              right={{ label: i18n.m.quarantine.replacement_in_place, url: api.replacementReplacementContentUrl(r.id), sizeBytes: r.newSizeBytes }} />
          {/key}
          <details class="hairline-t mt-5 pt-4">
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
          {#if r.acceptedBelowTargetAt}<span class="badge tone-warn" data-accepted-below-target>{i18n.m.quarantine.accepted_below_target}</span>
          {:else if r.verificationPassed !== null}<span class="badge {r.verificationPassed ? 'tone-ok' : 'tone-bad'}">{r.verificationPassed ? i18n.m.quarantine.passed : i18n.m.quarantine.failed}</span>{/if}
        </div>
        <!-- The checks lead, because they decided the outcome; each measure follows as a section. -->
        {#if checks}<VerificationChecks {checks} />{:else}<p class="text-body text-ink-3">{i18n.m.quarantine.no_report}</p>{/if}
        <AudioQualityReport report={audioReport(detail)} />
        <SoundtrackQualityReport report={soundtrackReport(detail)} />
        <PerceptualSearchReport report={searchReport(detail)} />
        <ImagePerceptualReport report={imageReport(detail)} />
      </section>
      {#if r.status === 'Replaced'}
        <!-- The decision follows the evidence and stays pinned to the bottom of the view while the
             comparison and the checks scroll by, so the choice and what it costs are always in sight. -->
        <section class="card sticky-actions flex flex-wrap items-center gap-x-6 gap-y-3 px-4 pt-4 sm:px-6" aria-labelledby="quarantine-decision" data-decision-bar>
          <div class="min-w-0 flex-1 basis-64">
            <h2 id="quarantine-decision" class="text-label font-semibold text-ink">{i18n.m.quarantine.decision_title}</h2>
            <p class="mt-1 max-w-3xl text-caption text-ink-3">{i18n.m.quarantine.action_note}</p>
          </div>
          {#if error}<div class="w-full" role="alert"><Banner kind="error">{error}</Banner></div>{/if}
          <div class="flex w-full flex-wrap gap-3 sm:w-auto">
            <button class="btn btn-lg btn-primary max-sm:flex-auto" onclick={() => decide(r, 'approve')} disabled={busy}><Icon name="check" />{busyId === r.id ? i18n.m.quarantine.working : i18n.m.quarantine.approve_free_space}</button>
            <button class="btn btn-lg btn-danger max-sm:flex-auto" onclick={() => decide(r, 'reject')} disabled={busy}><Icon name="rotate" />{i18n.m.quarantine.reject_roll_back}</button>
          </div>
        </section>
      {/if}
    {:else}<div class="card p-8 text-sm text-ink-3">{i18n.m.quarantine.unavailable}</div>{/if}
    <a class="btn" href="#/quarantine"><Icon name="arrow-left" />{i18n.m.nav.quarantine}</a>
  </div>
{:else}
<PageHeader title={i18n.m.nav.quarantine}>
  {#snippet description()}
    <p class="page-subtitle">
      {i18n.m.quarantine.subtitle_1}<strong>{i18n.m.quarantine.approve_word}</strong>{i18n.m.quarantine.subtitle_2}<strong>{i18n.m.quarantine.reject_word}</strong>{i18n.m.quarantine.subtitle_3}
      {#if activeCount > 0}<span class="text-ink-3">{t(i18n.m.quarantine.count_suffix, { count: activeCount })}</span>{/if}
    </p>
  {/snippet}
</PageHeader>

{#if loadError}<Banner kind="error" class="mb-4">{loadError}<button class="btn ml-3" onclick={load}>{i18n.m.setup.retry}</button></Banner>{/if}

{#if duplicates.length > 0 && replacements.length > 0}
  <Segmented class="mb-4" label={i18n.m.quarantine.kind_label} value={kind} onselect={(value) => { chosenKind = value }} items={[
    { value: 'all', label: i18n.m.quarantine.kind_all, count: replacements.length + duplicates.length },
    { value: 'optimisation', label: i18n.m.quarantine.kind_optimisation, count: replacements.length },
    { value: 'duplicate', label: i18n.m.quarantine.kind_duplicate, count: duplicates.length },
  ]} />
{/if}

{#if duplicates.length > 0 && kind !== 'optimisation'}
  <section class="card mb-4 overflow-hidden" aria-labelledby="quarantine-duplicates" data-testid="quarantined-duplicates">
    <div class="p-4 sm:px-5">
      <h2 id="quarantine-duplicates" class="text-base font-semibold">{i18n.m.quarantine.duplicate_heading}</h2>
      <p class="mt-1 text-xs text-ink-3">{i18n.m.quarantine.duplicate_note}</p>
    </div>
    <ul class="divide-y divide-line-soft">
      {#each duplicates as group (group.id)}
        <li class="flex flex-wrap items-start gap-3 px-4 py-3 sm:px-5" data-testid="quarantined-duplicate">
          <div class="min-w-0 flex-1 basis-64">
            <span class="badge tone-neutral">{i18n.m.quarantine.kind_duplicate}</span>
            {#if group.state === 'NeedsAttention'}<span class="badge ml-1 tone-warn">{i18n.m.quarantine.duplicate_attention}</span>{/if}
            {#if group.state === 'Purging'}<span class="badge ml-1 tone-muted">{i18n.m.quarantine.duplicate_deleting}</span>{/if}
            {#each group.copies as copy (copy.quarantinePath)}
              <div class="mt-2 break-all text-sm font-medium text-ink">{fileName(copy.path)}</div>
              <div class="break-all font-mono text-caption text-ink-3" title={copy.quarantinePath}>{t(i18n.m.quarantine.duplicate_from, { path: copy.path })} · {formatSize(copy.sizeBytes)}</div>
            {/each}
            {#each group.keptPaths as path (path)}
              <div class="mt-1 break-all font-mono text-caption text-ink-3">{t(i18n.m.quarantine.duplicate_kept, { path })}</div>
            {/each}
            {#if group.attentionReason}<p class="mt-1 text-xs text-warn">{group.attentionReason}</p>{/if}
            <time class="mt-1 block text-caption text-ink-3" datetime={group.heldSince} title={formatAbsoluteDateTime(group.heldSince, i18n.locale)}>{formatDateTime(group.heldSince, new Date(), i18n.locale)}</time>
          </div>
          <div class="flex flex-wrap gap-2">
            {#if group.state !== 'Purging'}<button class="btn" disabled={busyDuplicateId !== null} onclick={() => changeDuplicates(group, 'restore')}><Icon name="rotate" class="h-4 w-4" />{i18n.m.quarantine.duplicate_restore}</button>{/if}
            {#if group.state !== 'NeedsAttention'}<button class="btn btn-danger" disabled={busyDuplicateId !== null} onclick={() => changeDuplicates(group, 'delete')}><Icon name="trash" class="h-4 w-4" />{i18n.m.quarantine.duplicate_delete}</button>{/if}
          </div>
        </li>
      {/each}
    </ul>
  </section>
{/if}

{#if kind !== 'duplicate' && (activeCount > 0 || spentCount > 0)}
  <div class="mb-4 flex flex-wrap items-center gap-2">
    {#if activeCount > 0}
      <button class="btn btn-primary" onclick={approveAll} disabled={busy}>
        <Icon name="check" class="h-4 w-4" />
        {bulkAction === 'approve' ? i18n.m.quarantine.approving_all : t(i18n.m.quarantine.approve_all, { count: activeCount })}
      </button>
      <button class="btn btn-danger" onclick={rejectAll} disabled={busy}>
        <Icon name="rotate" class="h-4 w-4" />
        {bulkAction === 'reject' ? i18n.m.quarantine.rolling_back_all : t(i18n.m.quarantine.reject_all, { count: activeCount })}
      </button>
    {/if}
    {#if spentCount > 0}
      <button class="btn btn-ghost" onclick={clearSpent} disabled={busy}>
        <Icon name="trash" class="h-4 w-4" />
        {clearing ? i18n.m.quarantine.clearing : t(i18n.m.quarantine.clear_finished, { count: spentCount })}
      </button>
    {/if}
    <span class="text-xs text-ink-3">{i18n.m.quarantine.bulk_note}</span>
  </div>
{/if}

{#if loading || (kind !== 'duplicate' && (replacements.length > 0 || duplicates.length === 0))}
  <DataTable rowCount={replacements.length} {loading} error={loadError} label={i18n.m.nav.quarantine} density="compact" scroll="contained" bind:scroller={tableScrollEl} data-quarantine-list data-keyboard-list class="quarantine-table" footer={replacements.length > 0 ? replacementCount : undefined}>
    {#snippet head()}
      <tr>
        <th scope="col" class="w-28 sm:w-32">{i18n.m.quarantine.col_status}</th>
        <th scope="col" class="data-table-primary">{i18n.m.quarantine.col_replaced_file}</th>
        <th scope="col" class="w-48 lg:w-52">{i18n.m.quarantine.col_size_change}</th>
        <th scope="col" class="hidden w-44 md:table-cell">{i18n.m.quarantine.col_replaced}</th>
      </tr>
    {/snippet}
    {#each replacements as r (r.id)}
      <tr>
        <!-- Every row here is an optimisation; the word only tells it apart from the spare duplicates
             above, so a phone leaves it out rather than spend its one line of status on it. -->
        <td class="max-sm:flex max-sm:flex-wrap max-sm:items-center max-sm:gap-1">
          {#if duplicates.length > 0}<span class="badge tone-neutral max-sm:hidden sm:mb-1">{i18n.m.quarantine.kind_optimisation}</span>{/if}
          {#if r.status === 'Replaced'}
            <span class="badge tone-ok">{i18n.m.quarantine.status_replaced}</span>
          {:else if r.status === 'Purged'}
            <span class="badge tone-muted">{i18n.m.quarantine.status_purged}</span>
          {:else}
            <span class="badge tone-muted">{i18n.m.quarantine.status_rolled_back}</span>
          {/if}
          {#if r.crossFilesystem}
            <span class="badge tone-warn sm:ml-1">{i18n.m.quarantine.copied}</span>
          {/if}
        </td>
        <!-- On a phone the whole of the artwork, name and path opens the review, as a row does in Queue. -->
        <td class="data-table-primary max-sm:relative">
          <div class="flex items-center gap-3">
          <Thumbnail mediaFileId={r.mediaFileId} alt="" />
          <div class="min-w-0">
          <a id={`replacement-${r.id}`} data-row-primary class="focus-ring flex hit-target items-center gap-2 rounded text-sm font-medium text-ink hover:text-accent max-sm:text-label max-sm:after:absolute max-sm:after:inset-0" href={`#/quarantine/${r.id}`} onclick={() => rememberList(r.id)}><span class="min-w-0 truncate" title={r.finalPath}>{fileName(r.finalPath)}</span><Icon name="arrow-right" class="h-4 w-4 shrink-0 text-ink-3" /></a>
          {#if r.status === 'Purged'}
            <div class="text-caption text-ink-3">{i18n.m.quarantine.original_purged}</div>
          {:else if r.status === 'Replaced'}
            <div class="max-w-md truncate font-mono text-caption text-ink-3" title={r.quarantinePath}>{t(i18n.m.quarantine.original_in, { path: r.quarantinePath })}</div>
          {/if}
          </div>
          </div>
        </td>
        <td class="text-caption tabular-nums">
          <span class="whitespace-nowrap">{formatSize(r.originalSizeBytes)} → {formatSize(r.newSizeBytes)}</span>
          <span class="whitespace-nowrap">(<SizeChange sourceSizeBytes={r.originalSizeBytes} outputSizeBytes={r.newSizeBytes} />)</span>
        </td>
        <td class="hidden text-caption text-ink-3 md:table-cell"><time datetime={r.replacedAt} title={formatAbsoluteDateTime(r.replacedAt, i18n.locale)}>{formatDateTime(r.replacedAt, new Date(), i18n.locale)}</time></td>
      </tr>
    {/each}
    {#snippet empty()}{i18n.m.quarantine.empty}{/snippet}
  </DataTable>
{/if}

{#snippet replacementCount()}<span>{t(i18n.m.quarantine.replacements_count, { count: replacements.length.toLocaleString() })}</span>{/snippet}


{/if}

<style>
  [data-quarantine-review] :global(.card) { transition: box-shadow 180ms ease; }
  [data-quarantine-review] :global(.card:hover), [data-quarantine-review] :global(.card:focus-within) { box-shadow: var(--lift-3), inset 0 1px 0 var(--edge); }
  @media (prefers-reduced-motion: reduce) { [data-quarantine-review] :global(.card) { transition: none; } }
</style>
