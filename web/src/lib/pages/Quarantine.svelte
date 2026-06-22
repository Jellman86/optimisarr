<script lang="ts">
  import { api, type Replacement, type ReplacementDetail, type VerificationCheck, type VerificationReport } from '../api'
  import { formatSize } from '../format'
  import Banner from '../components/Banner.svelte'
  import Icon from '../components/Icon.svelte'
  import BottomSheet from '../components/BottomSheet.svelte'
  import VerificationChecks from '../components/VerificationChecks.svelte'
  import MediaCompare from '../components/MediaCompare.svelte'

  let replacements = $state<Replacement[]>([])
  let error = $state<string | null>(null)
  let loading = $state(true)
  let busyId = $state<number | null>(null)
  let bulkAction = $state<'approve' | 'reject' | null>(null)
  let clearing = $state(false)

  // Compare-to-approve opens in the shared bottom sheet (same as Inventory/Queue): the table
  // shrinks to stay reachable above it. Details (incl. the verification report) are fetched lazily
  // and cached, so the list stays lean.
  let selectedId = $state<number | null>(null)
  let sheetExpanded = $state(true)
  let sheetHeight = $state(0)
  let details = $state<Record<number, ReplacementDetail>>({})
  let detailError = $state<string | null>(null)
  let detailLoading = $state(false)

  $effect(() => {
    void load()
  })

  async function load() {
    try {
      replacements = await api.replacements()
      error = null
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to load replacements'
    } finally {
      loading = false
    }
  }

  let selected = $derived(replacements.find((r) => r.id === selectedId) ?? null)

  // Only "Replaced" entries have a quarantined original to compare/act on; spent rows are read-only.
  async function selectRow(r: Replacement) {
    if (r.status !== 'Replaced') return
    if (selectedId === r.id) {
      selectedId = null
      return
    }
    selectedId = r.id
    sheetExpanded = true
    detailError = null
    if (!details[r.id]) {
      detailLoading = true
      try {
        details[r.id] = await api.replacement(r.id)
      } catch (err) {
        detailError = err instanceof Error ? err.message : 'Unable to load the comparison'
      } finally {
        detailLoading = false
      }
    }
  }

  async function approve(r: Replacement) {
    if (
      !confirm(
        `Approve this replacement?\n\nThis permanently deletes the quarantined original:\n${r.quarantinePath}\n\nThe replacement is kept, but it can no longer be rolled back.`,
      )
    ) {
      return
    }
    busyId = r.id
    try {
      await api.approveReplacement(r.id)
      delete details[r.id]
      selectedId = null
      await load()
    } catch (err) {
      error = err instanceof Error ? err.message : 'Approve failed'
    } finally {
      busyId = null
    }
  }

  async function reject(r: Replacement) {
    if (!confirm(`Roll back — restore the original and remove the replacement for:\n\n${r.originalPath}`)) {
      return
    }
    busyId = r.id
    try {
      await api.rollbackReplacement(r.id)
      delete details[r.id]
      selectedId = null
      await load()
    } catch (err) {
      error = err instanceof Error ? err.message : 'Rollback failed'
    } finally {
      busyId = null
    }
  }

  async function approveAll() {
    if (!confirm(`Approve all ${activeCount} quarantined replacements?\n\nThis permanently deletes every quarantined original shown as Replaced. None of these replacements can be rolled back afterward.`)) return
    bulkAction = 'approve'
    let failed = 0
    for (const replacement of replacements.filter((r) => r.status === 'Replaced')) {
      try {
        await api.approveReplacement(replacement.id)
      } catch {
        failed++
      }
    }
    if (failed > 0) error = `${failed} replacement${failed === 1 ? '' : 's'} could not be approved. Review the remaining rows.`
    selectedId = null
    await load()
    bulkAction = null
  }

  async function rejectAll() {
    if (!confirm(`Roll back all ${activeCount} quarantined replacements?\n\nEach original will be restored and its replacement removed.`)) return
    bulkAction = 'reject'
    let failed = 0
    for (const replacement of replacements.filter((r) => r.status === 'Replaced')) {
      try {
        await api.rollbackReplacement(replacement.id)
      } catch {
        failed++
      }
    }
    if (failed > 0) error = `${failed} replacement${failed === 1 ? '' : 's'} could not be rolled back. Review the remaining rows.`
    selectedId = null
    await load()
    bulkAction = null
  }

  async function clearSpent() {
    if (!confirm(`Clear ${spentCount} finished quarantine ${spentCount === 1 ? 'entry' : 'entries'} (rolled back + purged) from the list?\n\nThese are history with no rollback left — no files are touched.`)) return
    clearing = true
    try {
      await api.clearReplacements()
      await load()
    } catch (err) {
      error = err instanceof Error ? err.message : 'Clear failed'
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

  // The table fills the space below the page chrome and shrinks when the sheet is open, so rows
  // stay reachable above it (same behaviour as Inventory/Queue).
  let tableScrollEl = $state<HTMLElement | null>(null)
  let tableMaxHeight = $state('65vh')

  $effect(() => {
    void sheetHeight
    void selectedId
    void replacements.length
    void loading
    void error
    const el = tableScrollEl
    if (!el) return
    const measure = () => {
      const top = el.getBoundingClientRect().top
      const sheetSub = selected ? sheetHeight : 0
      const available = window.innerHeight - top - 48 - sheetSub
      tableMaxHeight = `${Math.max(200, Math.round(available))}px`
    }
    const raf = requestAnimationFrame(measure)
    window.addEventListener('resize', measure)
    return () => {
      cancelAnimationFrame(raf)
      window.removeEventListener('resize', measure)
    }
  })
</script>

<header class="mb-6">
  <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">Quarantine</h1>
  <p class="text-sm text-slate-500 dark:text-slate-400">
    Replaced originals are kept here, not deleted. Compare a replacement against its original, then
    <strong>approve</strong> to keep it and free the space now, or <strong>reject</strong> to roll back and restore the original.
    Once a retention window is set in Settings, originals past it are purged automatically and can no longer be rolled back.
    {#if activeCount > 0}<span class="text-slate-400"> · {activeCount} in quarantine</span>{/if}
  </p>
</header>

{#if error}
  <Banner kind="error" class="mb-4">{error}</Banner>
{/if}

{#if activeCount > 0 || spentCount > 0}
  <div class="mb-4 flex flex-wrap items-center gap-2">
    {#if activeCount > 0}
      <button class="btn btn-primary px-3 py-1.5 text-sm" onclick={approveAll} disabled={bulkAction !== null}>
        <Icon name="check" class="h-4 w-4" />
        {bulkAction === 'approve' ? 'Approving all…' : `Approve all (${activeCount})`}
      </button>
      <button class="btn btn-danger px-3 py-1.5 text-sm" onclick={rejectAll} disabled={bulkAction !== null}>
        <Icon name="rotate" class="h-4 w-4" />
        {bulkAction === 'reject' ? 'Rolling back all…' : `Reject all (${activeCount})`}
      </button>
    {/if}
    {#if spentCount > 0}
      <button class="btn btn-ghost px-3 py-1.5 text-sm" onclick={clearSpent} disabled={clearing}>
        <Icon name="trash" class="h-4 w-4" />
        {clearing ? 'Clearing…' : `Clear finished (${spentCount})`}
      </button>
    {/if}
    <span class="text-xs text-slate-400">Bulk actions affect only active quarantine entries and require confirmation.</span>
  </div>
{/if}

{#if loading}
  <div class="card p-8 text-center text-slate-400">Loading…</div>
{:else if replacements.length > 0}
  <div class="card overflow-hidden">
    <div bind:this={tableScrollEl} class="overflow-auto" style="max-height: {tableMaxHeight}; transition: max-height 0.3s ease-out;">
      <table class="w-full text-sm">
        <thead class="sticky top-0 z-10 border-b border-slate-200 bg-white text-left text-xs uppercase text-slate-500 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-400">
          <tr>
            <th class="px-4 py-3">Status</th>
            <th class="px-4 py-3">Replaced file</th>
            <th class="hidden px-4 py-3 sm:table-cell">Saving</th>
            <th class="hidden px-4 py-3 md:table-cell">Replaced</th>
          </tr>
        </thead>
        <tbody class="divide-y divide-slate-100 dark:divide-slate-800">
          {#each replacements as r (r.id)}
            <tr
              class="text-slate-700 dark:text-slate-300 {r.status === 'Replaced' ? 'cursor-pointer hover:bg-slate-50 dark:hover:bg-slate-800/50' : ''} {selectedId === r.id ? 'bg-cyan-50 dark:bg-cyan-900/20' : ''}"
              onclick={() => selectRow(r)}
            >
              <td class="px-4 py-2">
                {#if r.status === 'Replaced'}
                  <span class="badge bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400">Replaced</span>
                {:else if r.status === 'Purged'}
                  <span class="badge bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400" title="The quarantined original was deleted (retention expired or approved). This replacement can no longer be rolled back.">Purged</span>
                {:else}
                  <span class="badge bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400">Rolled back</span>
                {/if}
                {#if r.crossFilesystem}
                  <span class="badge ml-1 bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300" title="Different filesystem: a verified copy-plus-delete was used instead of an atomic move.">copied</span>
                {/if}
              </td>
              <td class="px-4 py-2">
                <div class="max-w-md truncate font-mono text-xs" title={r.finalPath}>{r.finalPath}</div>
                {#if r.status === 'Purged'}
                  <div class="text-[11px] text-slate-400">↳ original purged</div>
                {:else if r.status === 'Replaced'}
                  <div class="max-w-md truncate font-mono text-[11px] text-slate-400" title={r.quarantinePath}>↳ original in {r.quarantinePath}</div>
                {/if}
              </td>
              <td class="hidden px-4 py-2 text-xs tabular-nums sm:table-cell">
                {formatSize(r.originalSizeBytes)} → {formatSize(r.newSizeBytes)}
                <span class="text-emerald-600 dark:text-emerald-400"> (−{savingPercent(r)}%)</span>
              </td>
              <td class="hidden px-4 py-2 text-xs text-slate-500 md:table-cell">{new Date(r.replacedAt).toLocaleString()}</td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  </div>
  <p class="mt-2 text-xs text-slate-400">{replacements.length.toLocaleString()} replacements · click a Replaced row to compare and approve</p>
{:else}
  <div class="card p-8 text-center text-slate-500 dark:text-slate-400">
    Nothing in quarantine. When you replace a verified job from the Queue, its original is kept here for review.
  </div>
{/if}

<BottomSheet open={selected !== null} bind:expanded={sheetExpanded} bind:height={sheetHeight} onclose={() => (selectedId = null)}>
  {#snippet header()}
    {#if selected}
      <div class="flex min-w-0 items-center gap-2">
        <span class="badge flex-shrink-0 bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400">Replaced</span>
        <p class="min-w-0 flex-1 truncate font-mono text-xs text-slate-700 dark:text-slate-200" title={selected.finalPath}>
          {selected.finalPath}
        </p>
      </div>
    {/if}
  {/snippet}
  {#snippet children()}
    {#if selected}
      {@const r = selected}
      {#if detailError}
        <Banner kind="error" class="mb-3">{detailError}</Banner>
      {/if}
      {#if detailLoading && !details[r.id]}
        <div class="text-center text-sm text-slate-400">Loading comparison…</div>
      {:else}
        {@const detail = details[r.id]}
        {@const checks = parseChecks(detail)}
        <div class="grid gap-4 sm:grid-cols-2">
          <div>
            <div class="text-xs font-semibold uppercase text-slate-500 dark:text-slate-400">Original (quarantined)</div>
            <div class="mt-1 font-mono text-xs text-slate-600 dark:text-slate-300">{formatSize(r.originalSizeBytes)}</div>
            <div class="mt-1 break-all font-mono text-[11px] text-slate-400" title={r.quarantinePath}>{r.quarantinePath}</div>
          </div>
          <div>
            <div class="text-xs font-semibold uppercase text-slate-500 dark:text-slate-400">Replacement (in place)</div>
            <div class="mt-1 font-mono text-xs text-slate-600 dark:text-slate-300">
              {formatSize(r.newSizeBytes)}
              <span class="text-emerald-600 dark:text-emerald-400">(−{savingPercent(r)}%, saved {formatSize(r.originalSizeBytes - r.newSizeBytes)})</span>
            </div>
            <div class="mt-1 break-all font-mono text-[11px] text-slate-400" title={r.finalPath}>{r.finalPath}</div>
          </div>
        </div>

        <!-- Visual original-vs-replacement: both files exist on disk (quarantined original
             + in-place replacement), so the operator can see/hear the difference. -->
        {#if detail}
          <div class="mt-4">
            <MediaCompare
              mediaKind={detail.mediaKind}
              left={{ label: 'Original (quarantined)', url: api.replacementOriginalContentUrl(r.id), sizeBytes: r.originalSizeBytes }}
              right={{ label: 'Replacement (in place)', url: api.replacementReplacementContentUrl(r.id), sizeBytes: r.newSizeBytes }}
            />
          </div>
        {/if}

        <div class="mt-4">
          <div class="mb-1.5 flex items-center gap-2">
            <span class="text-xs font-semibold uppercase text-slate-500 dark:text-slate-400">Verification</span>
            {#if detail?.verificationPassed === true}
              <span class="badge bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400">Passed</span>
            {:else if detail?.verificationPassed === false}
              <span class="badge bg-red-100 text-red-700 dark:bg-red-900/40 dark:text-red-400">Failed</span>
            {/if}
          </div>
          {#if checks}
            <VerificationChecks {checks} />
          {:else}
            <p class="text-xs text-slate-400">No verification report was recorded for this replacement.</p>
          {/if}
        </div>

        <div class="mt-4 flex flex-wrap items-center gap-2">
          <button class="btn btn-primary px-3 py-1.5 text-sm" onclick={() => approve(r)} disabled={busyId === r.id}>
            <Icon name="check" class="h-4 w-4" />
            {busyId === r.id ? 'Working' : 'Approve & free space'}
          </button>
          <button class="btn btn-danger px-3 py-1.5 text-sm" onclick={() => reject(r)} disabled={busyId === r.id}>
            <Icon name="rotate" class="h-4 w-4" />
            Reject (roll back)
          </button>
          <span class="text-xs text-slate-400">Approve deletes the quarantined original now; rolling back is then no longer possible.</span>
        </div>
      {/if}
    {/if}
  {/snippet}
</BottomSheet>
