<script lang="ts">
  import { api, type Job, type VerificationCheck, type VerificationReport } from '../api'
  import { formatSize } from '../format'
  import { router } from '../stores/ui.svelte'
  import Banner from '../components/Banner.svelte'
  import BottomSheet from '../components/BottomSheet.svelte'
  import VerificationChecks from '../components/VerificationChecks.svelte'

  let jobs = $state<Job[]>([])
  let error = $state<string | null>(null)
  let loading = $state(true)
  let filter = $state<'all' | 'passed' | 'failed'>('all')

  // Detail bottom sheet — the same slide-up + table-shrink pattern as Inventory and Queue,
  // replacing the old in-table row expansion.
  let selectedId = $state<number | null>(null)
  let sheetExpanded = $state(true)
  let sheetHeight = $state(0)
  let tableScrollEl = $state<HTMLElement | null>(null)
  let tableMaxHeight = $state('65vh')

  $effect(() => {
    void load()
  })

  async function load() {
    loading = true
    error = null
    try {
      jobs = await api.jobs()
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to load jobs'
    } finally {
      loading = false
    }
  }

  function parseChecks(job: Job): VerificationCheck[] | null {
    if (!job.verificationReportJson) return null
    try {
      const report = JSON.parse(job.verificationReportJson) as VerificationReport
      return report.checks ?? null
    } catch {
      return null
    }
  }

  // Only jobs that have actually been through the Verifying step.
  let verifiedJobs = $derived(jobs.filter((j) => j.verificationPassed !== null))
  let passedJobs = $derived(verifiedJobs.filter((j) => j.verificationPassed === true))
  let failedJobs = $derived(verifiedJobs.filter((j) => j.verificationPassed === false))

  // Find the most commonly failing check name across all failed jobs.
  let topFailure = $derived.by(() => {
    const counts = new Map<string, number>()
    for (const job of failedJobs) {
      const checks = parseChecks(job)
      if (!checks) continue
      for (const c of checks) {
        if (c.outcome === 'Failed') counts.set(c.name, (counts.get(c.name) ?? 0) + 1)
      }
    }
    let max = 0
    let name: string | null = null
    for (const [k, v] of counts) {
      if (v > max) { max = v; name = k }
    }
    return name && max > 0 ? { name, count: max } : null
  })

  let visibleJobs = $derived(
    filter === 'passed' ? passedJobs : filter === 'failed' ? failedJobs : verifiedJobs,
  )

  let selectedJob = $derived(jobs.find((j) => j.id === selectedId) ?? null)

  function selectRow(id: number) {
    if (selectedId === id) {
      selectedId = null
    } else {
      selectedId = id
      sheetExpanded = true
    }
  }

  function passRate(): number {
    return verifiedJobs.length > 0 ? Math.round((passedJobs.length / verifiedJobs.length) * 100) : 0
  }

  // The table fills the space below the page chrome and shrinks when the sheet opens, so rows
  // stay reachable above the panel. The chrome above it (stats, filters) is measured, not assumed.
  $effect(() => {
    void sheetHeight
    void selectedId
    void filter
    void visibleJobs.length
    void loading
    const el = tableScrollEl
    if (!el) return
    const measure = () => {
      const top = el.getBoundingClientRect().top
      const sheetSub = selectedJob ? sheetHeight : 0
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
  <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">Verification</h1>
  <p class="text-sm text-slate-500 dark:text-slate-400">
    Quality assurance results for all processed files. Configure gates in
    <button
      class="text-cyan-600 hover:underline dark:text-cyan-400"
      onclick={() => router.go('/settings')}
    >Settings</button>.
  </p>
</header>

{#if error}
  <Banner kind="error" class="mb-4">{error}</Banner>
{/if}

{#if loading}
  <div class="card p-8 text-center text-slate-400">Loading…</div>
{:else if verifiedJobs.length === 0}
  <div class="card p-8 text-center text-slate-500 dark:text-slate-400">
    No verification results yet. They appear here once jobs complete the Verifying step.
  </div>
{:else}
  <!-- Summary stats -->
  <div class="mb-5 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
    <div class="card p-4">
      <div class="text-xs font-medium uppercase tracking-wide text-slate-400">Total verified</div>
      <div class="mt-1 text-2xl font-bold text-slate-700 dark:text-slate-200">
        {verifiedJobs.length.toLocaleString()}
      </div>
    </div>
    <div class="card p-4">
      <div class="text-xs font-medium uppercase tracking-wide text-slate-400">Passed</div>
      <div class="mt-1 text-2xl font-bold text-emerald-600 dark:text-emerald-400">
        {passedJobs.length.toLocaleString()}
      </div>
      <div class="mt-0.5 text-xs text-slate-400">{passRate()}% of total</div>
    </div>
    <div class="card p-4">
      <div class="text-xs font-medium uppercase tracking-wide text-slate-400">Failed</div>
      <div
        class="mt-1 text-2xl font-bold {failedJobs.length > 0
          ? 'text-red-600 dark:text-red-400'
          : 'text-slate-400'}"
      >
        {failedJobs.length.toLocaleString()}
      </div>
      {#if failedJobs.length > 0}
        <div class="mt-0.5 text-xs text-slate-400">
          {100 - passRate()}% of total
        </div>
      {/if}
    </div>
    <div class="card p-4">
      <div class="text-xs font-medium uppercase tracking-wide text-slate-400">Most common failure</div>
      {#if topFailure}
        <div class="mt-1 text-sm font-medium text-red-600 dark:text-red-400">{topFailure.name}</div>
        <div class="mt-0.5 text-xs text-slate-400">
          {topFailure.count} job{topFailure.count !== 1 ? 's' : ''}
        </div>
      {:else}
        <div class="mt-1 text-sm text-slate-400">None — all passed</div>
      {/if}
    </div>
  </div>

  <!-- Filter tabs -->
  <div class="mb-3 flex flex-wrap gap-2">
    <button
      class="btn px-3 py-1 text-xs"
      class:btn-primary={filter === 'all'}
      onclick={() => { filter = 'all'; selectedId = null }}
    >All ({verifiedJobs.length.toLocaleString()})</button>
    <button
      class="btn px-3 py-1 text-xs"
      class:btn-primary={filter === 'passed'}
      onclick={() => { filter = 'passed'; selectedId = null }}
    >Passed ({passedJobs.length.toLocaleString()})</button>
    <button
      class="btn px-3 py-1 text-xs"
      class:btn-primary={filter === 'failed'}
      onclick={() => { filter = 'failed'; selectedId = null }}
    >Failed ({failedJobs.length.toLocaleString()})</button>
  </div>

  <!-- Results table: scrolls internally and shrinks when the detail sheet opens. -->
  <div class="card overflow-hidden">
    <div
      bind:this={tableScrollEl}
      class="overflow-auto"
      style="max-height: {tableMaxHeight}; transition: max-height 0.3s ease-out;"
    >
      <table class="w-full text-sm">
        <thead
          class="sticky top-0 z-10 border-b border-slate-200 bg-white text-left text-xs uppercase text-slate-500 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-400"
        >
          <tr>
            <th class="px-4 py-3">Result</th>
            <th class="px-4 py-3">File</th>
            <th class="hidden px-4 py-3 sm:table-cell">Output size</th>
            <th class="hidden px-4 py-3 md:table-cell">Encoder</th>
            <th class="hidden px-4 py-3 lg:table-cell">Verified at</th>
          </tr>
        </thead>
        <tbody class="divide-y divide-slate-100 dark:divide-slate-800">
          {#each visibleJobs as job (job.id)}
            <tr
              class="cursor-pointer text-slate-700 hover:bg-slate-50 dark:text-slate-300 dark:hover:bg-slate-800/50 {selectedId === job.id ? 'bg-slate-50 dark:bg-slate-900/40' : ''}"
              onclick={() => selectRow(job.id)}
            >
              <td class="px-4 py-2">
                {#if job.verificationPassed}
                  <span
                    class="badge bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400"
                    >✓ Passed</span
                  >
                {:else}
                  <span class="badge bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300"
                    >✗ Failed</span
                  >
                {/if}
              </td>
              <td class="max-w-[40vw] px-4 py-2 sm:max-w-xs">
                <div class="truncate font-mono text-xs" title={job.relativePath ?? ''}>
                  {job.relativePath ?? '—'}
                </div>
              </td>
              <td class="hidden px-4 py-2 sm:table-cell">
                {job.outputSizeBytes ? formatSize(job.outputSizeBytes) : '—'}
              </td>
              <td class="hidden px-4 py-2 font-mono text-xs md:table-cell">
                {job.videoEncoder ?? '—'}
              </td>
              <td class="hidden px-4 py-2 text-xs text-slate-500 lg:table-cell dark:text-slate-400">
                {job.verifiedAt ? new Date(job.verifiedAt).toLocaleString() : '—'}
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  </div>
  <p class="mt-2 text-xs text-slate-400">{verifiedJobs.length.toLocaleString()} verified jobs</p>
{/if}

<!-- Detail bottom sheet: the full gate report for the selected job. -->
<BottomSheet open={selectedJob !== null} bind:expanded={sheetExpanded} bind:height={sheetHeight} onclose={() => (selectedId = null)}>
  {#snippet header()}
    {#if selectedJob}
      <div class="flex min-w-0 items-center gap-2">
        {#if selectedJob.verificationPassed}
          <span class="badge flex-shrink-0 bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400">✓ Passed</span>
        {:else}
          <span class="badge flex-shrink-0 bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300">✗ Failed</span>
        {/if}
        <p class="min-w-0 flex-1 truncate font-mono text-xs text-slate-700 dark:text-slate-200" title={selectedJob.relativePath ?? ''}>
          {selectedJob.relativePath ?? '—'}
        </p>
      </div>
    {/if}
  {/snippet}
  {#snippet children()}
    {#if selectedJob}
      {@const checks = parseChecks(selectedJob)}
      <dl class="mb-4 grid gap-x-8 gap-y-3 text-sm sm:grid-cols-2 lg:grid-cols-4">
        <div class="flex justify-between gap-4"><dt class="text-slate-500">Output size</dt><dd>{selectedJob.outputSizeBytes ? formatSize(selectedJob.outputSizeBytes) : '—'}</dd></div>
        <div class="flex justify-between gap-4"><dt class="text-slate-500">Encoder</dt><dd class="text-right font-mono text-xs">{selectedJob.videoEncoder ?? '—'}</dd></div>
        <div class="flex justify-between gap-4"><dt class="text-slate-500">Verified</dt><dd class="text-right">{selectedJob.verifiedAt ? new Date(selectedJob.verifiedAt).toLocaleString() : '—'}</dd></div>
      </dl>
      {#if checks}
        <VerificationChecks {checks} />
      {:else}
        <p class="text-sm text-slate-400">No detailed verification report was recorded for this job.</p>
      {/if}
    {/if}
  {/snippet}
</BottomSheet>
