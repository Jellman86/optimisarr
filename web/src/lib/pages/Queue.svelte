<script lang="ts">
  import { api, type Job } from '../api'

  let jobs = $state<Job[]>([])
  let error = $state<string | null>(null)
  let loading = $state(true)
  let cancellingId = $state<number | null>(null)

  // Active jobs change quickly, so poll while this page is mounted.
  $effect(() => {
    void load()
    const timer = setInterval(load, 2000)
    return () => clearInterval(timer)
  })

  async function load() {
    try {
      jobs = await api.jobs()
      error = null
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to load jobs'
    } finally {
      loading = false
    }
  }

  async function cancel(job: Job) {
    cancellingId = job.id
    try {
      await api.cancelJob(job.id)
      await load()
    } catch (err) {
      error = err instanceof Error ? err.message : 'Cancel failed'
    } finally {
      cancellingId = null
    }
  }

  const ACTIVE = ['Queued', 'Probing', 'Transcoding', 'Verifying', 'ReadyToReplace']
  function isActive(status: string) {
    return ACTIVE.includes(status)
  }

  function badgeClass(status: string): string {
    switch (status) {
      case 'Transcoding':
      case 'Probing':
      case 'Verifying':
        return 'bg-sky-100 text-sky-700 dark:bg-sky-950 dark:text-sky-300'
      case 'ReadyToReplace':
        return 'bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400'
      case 'Completed':
        return 'bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400'
      case 'Failed':
        return 'bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300'
      case 'Cancelled':
        return 'bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400'
      default:
        return 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300'
    }
  }

  let activeCount = $derived(jobs.filter((j) => isActive(j.status)).length)
</script>

<header class="mb-6">
  <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">Queue</h1>
  <p class="text-sm text-slate-500 dark:text-slate-400">
    Transcode jobs. Outputs are written to the work directory — your originals are never touched.
    {#if activeCount > 0}<span class="text-slate-400"> · {activeCount} active</span>{/if}
  </p>
</header>

{#if error}
  <div class="card mb-4 border-red-300 p-3 text-sm text-red-700 dark:border-red-800 dark:text-red-400">{error}</div>
{/if}

{#if loading}
  <div class="card p-8 text-center text-slate-400">Loading…</div>
{:else if jobs.length > 0}
  <div class="card overflow-x-auto">
    <table class="w-full text-sm">
      <thead class="border-b border-slate-200 text-left text-xs uppercase text-slate-500 dark:border-slate-700 dark:text-slate-400">
        <tr>
          <th class="px-4 py-3">Status</th>
          <th class="px-4 py-3">File</th>
          <th class="px-4 py-3 w-48">Progress</th>
          <th class="px-4 py-3">Priority</th>
          <th class="px-4 py-3"></th>
        </tr>
      </thead>
      <tbody class="divide-y divide-slate-100 dark:divide-slate-800">
        {#each jobs as job (job.id)}
          <tr class="text-slate-700 dark:text-slate-300">
            <td class="px-4 py-2"><span class="badge {badgeClass(job.status)}">{job.status}</span></td>
            <td class="max-w-xs truncate px-4 py-2 font-mono text-xs" title={job.relativePath ?? ''}>{job.relativePath ?? '—'}</td>
            <td class="px-4 py-2">
              {#if job.status === 'Transcoding'}
                <div class="flex items-center gap-2">
                  <div class="h-1.5 flex-1 overflow-hidden rounded-full bg-slate-200 dark:bg-slate-700">
                    <div class="h-full rounded-full bg-emerald-600 transition-all" style="width: {Math.round(job.progress * 100)}%"></div>
                  </div>
                  <span class="w-9 text-right text-xs tabular-nums text-slate-500">{Math.round(job.progress * 100)}%</span>
                </div>
              {:else if job.status === 'Failed' && job.errorMessage}
                <span class="text-xs text-red-600" title={job.errorMessage}>error</span>
              {:else}
                <span class="text-xs text-slate-400">—</span>
              {/if}
            </td>
            <td class="px-4 py-2 text-xs">{job.priority}</td>
            <td class="px-4 py-2 text-right">
              {#if isActive(job.status)}
                <button class="btn btn-danger px-3 py-1 text-xs" onclick={() => cancel(job)} disabled={cancellingId === job.id}>
                  {cancellingId === job.id ? 'Cancelling' : 'Cancel'}
                </button>
              {/if}
            </td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
  <p class="mt-2 text-xs text-slate-400">{jobs.length.toLocaleString()} jobs</p>
{:else}
  <div class="card p-8 text-center text-slate-500 dark:text-slate-400">
    The queue is empty. Enqueue a library's eligible files from the Libraries page.
  </div>
{/if}
