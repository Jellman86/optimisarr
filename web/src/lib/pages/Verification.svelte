<script lang="ts">
  import { api, type Job, type VerificationCheck, type VerificationReport } from '../api'
  import { formatSize } from '../format'
  import { router } from '../stores/ui.svelte'
  import Banner from '../components/Banner.svelte'
  import VerificationChecks from '../components/VerificationChecks.svelte'

  let jobs = $state<Job[]>([])
  let error = $state<string | null>(null)
  let loading = $state(true)
  let filter = $state<'all' | 'passed' | 'failed'>('all')
  let expandedId = $state<number | null>(null)

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

  function toggle(job: Job) {
    expandedId = expandedId === job.id ? null : job.id
  }

  function passRate(): number {
    return verifiedJobs.length > 0 ? Math.round((passedJobs.length / verifiedJobs.length) * 100) : 0
  }
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
      onclick={() => { filter = 'all'; expandedId = null }}
    >All ({verifiedJobs.length.toLocaleString()})</button>
    <button
      class="btn px-3 py-1 text-xs"
      class:btn-primary={filter === 'passed'}
      onclick={() => { filter = 'passed'; expandedId = null }}
    >Passed ({passedJobs.length.toLocaleString()})</button>
    <button
      class="btn px-3 py-1 text-xs"
      class:btn-primary={filter === 'failed'}
      onclick={() => { filter = 'failed'; expandedId = null }}
    >Failed ({failedJobs.length.toLocaleString()})</button>
  </div>

  <!-- Results table -->
  <div class="card overflow-hidden">
    <div class="overflow-x-auto">
      <table class="w-full text-sm">
        <thead
          class="border-b border-slate-200 text-left text-xs uppercase text-slate-500 dark:border-slate-700 dark:text-slate-400"
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
            {@const checks = parseChecks(job)}
            <tr
              class="text-slate-700 dark:text-slate-300 {checks
                ? 'cursor-pointer hover:bg-slate-50 dark:hover:bg-slate-800/50'
                : ''} {expandedId === job.id ? 'bg-slate-50 dark:bg-slate-900/40' : ''}"
              onclick={() => checks && toggle(job)}
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
                {#if checks}
                  <span class="text-[11px] text-slate-400"
                    >{expandedId === job.id ? '▾ hide' : '▸ details'}</span
                  >
                {/if}
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
            {#if expandedId === job.id && checks}
              <tr class="bg-slate-50 dark:bg-slate-900/40">
                <td colspan="5" class="px-4 py-3">
                  <VerificationChecks {checks} />
                </td>
              </tr>
            {/if}
          {/each}
        </tbody>
      </table>
    </div>
  </div>
  <p class="mt-2 text-xs text-slate-400">{verifiedJobs.length.toLocaleString()} verified jobs</p>
{/if}
