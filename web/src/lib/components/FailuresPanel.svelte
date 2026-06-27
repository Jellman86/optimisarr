<script lang="ts">
  // The Queue page's "Failures" tab: failed jobs grouped by their classified reason, with a per-job
  // drill-in to the captured ffmpeg log. Reads the diagnostics endpoints so "why did this fail?" is
  // answerable here, not only by reading container logs.
  import { api, type FailureGroup } from '../api'
  import Banner from './Banner.svelte'
  import EmptyState from './EmptyState.svelte'
  import Icon from './Icon.svelte'

  let groups = $state<FailureGroup[]>([])
  let loading = $state(true)
  let error = $state<string | null>(null)

  // The job whose ffmpeg log is open, plus a small cache so re-opening one is instant.
  let openLogJobId = $state<number | null>(null)
  let logs = $state<Record<number, string | null>>({})
  let logLoadingId = $state<number | null>(null)

  $effect(() => {
    void load()
  })

  async function load() {
    loading = true
    try {
      groups = await api.jobFailures()
      error = null
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to load failures'
    } finally {
      loading = false
    }
  }

  async function toggleLog(jobId: number) {
    if (openLogJobId === jobId) {
      openLogJobId = null
      return
    }
    openLogJobId = jobId
    if (!(jobId in logs)) {
      logLoadingId = jobId
      try {
        logs[jobId] = await api.jobLog(jobId)
      } catch {
        logs[jobId] = null
      } finally {
        logLoadingId = null
      }
    }
  }

  // The leaf filename, so a long library path doesn't dominate the row.
  function fileName(path: string | null): string {
    if (!path) return '—'
    const parts = path.split('/')
    return parts[parts.length - 1] || path
  }

  const totalFailures = $derived(groups.reduce((sum, group) => sum + group.count, 0))
</script>

<div class="mb-4 flex items-center justify-between">
  <p class="text-sm text-slate-500 dark:text-slate-400">
    {#if !loading && totalFailures > 0}
      {totalFailures.toLocaleString()} failed job{totalFailures === 1 ? '' : 's'}, grouped by reason. Originals were never touched.
    {:else}
      Failed jobs grouped by reason, with the captured ffmpeg log for each.
    {/if}
  </p>
  <button class="btn btn-ghost inline-flex items-center gap-1 px-3 py-1 text-xs" onclick={load} disabled={loading}>
    <Icon name="retry" class="h-4 w-4" />
    Refresh
  </button>
</div>

{#if error}
  <Banner kind="error" class="mb-4">{error}</Banner>
{/if}

{#if loading}
  <div class="card p-8 text-center text-slate-400">Loading…</div>
{:else if groups.length === 0}
  <EmptyState icon="check" title="No failed jobs" hint="Every job so far has completed or is still in progress." />
{:else}
  <div class="space-y-4">
    {#each groups as group (group.category)}
      <div class="card overflow-hidden">
        <div class="flex items-start gap-3 border-b border-slate-100 p-4 dark:border-slate-800">
          <Icon name="warning" class="mt-0.5 h-5 w-5 flex-shrink-0 text-red-500" />
          <div class="min-w-0 flex-1">
            <div class="flex items-center gap-2">
              <h3 class="font-medium text-slate-800 dark:text-slate-100">{group.description}</h3>
              <span class="badge bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300">{group.count}</span>
            </div>
            <p class="text-xs text-slate-400 dark:text-slate-500">{group.category}</p>
          </div>
        </div>

        <ul class="divide-y divide-slate-100 dark:divide-slate-800">
          {#each group.samples as sample (sample.jobId)}
            <li class="px-4 py-3">
              <div class="flex items-start justify-between gap-3">
                <div class="min-w-0 flex-1">
                  <div class="truncate font-mono text-xs text-slate-700 dark:text-slate-200" title={sample.relativePath ?? ''}>
                    {fileName(sample.relativePath)}
                  </div>
                  {#if sample.errorMessage}
                    <p class="mt-0.5 line-clamp-2 whitespace-pre-line text-xs text-red-600 dark:text-red-400" title={sample.errorMessage}>
                      {sample.errorMessage}
                    </p>
                  {/if}
                </div>
                <button
                  class="btn btn-ghost inline-flex flex-shrink-0 items-center gap-1 px-2 py-1 text-xs"
                  onclick={() => toggleLog(sample.jobId)}
                >
                  <Icon name="chevron" class="h-3.5 w-3.5 transition-transform {openLogJobId === sample.jobId ? 'rotate-180' : ''}" />
                  {openLogJobId === sample.jobId ? 'Hide log' : 'View log'}
                </button>
              </div>

              {#if openLogJobId === sample.jobId}
                <div class="mt-2">
                  {#if logLoadingId === sample.jobId}
                    <p class="text-xs text-slate-400">Loading log…</p>
                  {:else if logs[sample.jobId]}
                    <pre class="max-h-64 overflow-auto whitespace-pre-wrap break-all rounded-md bg-slate-50 p-3 font-mono text-[11px] leading-relaxed text-slate-600 dark:bg-slate-900/60 dark:text-slate-300">{logs[sample.jobId]}</pre>
                  {:else}
                    <p class="text-xs text-slate-400">No ffmpeg log was captured for this job (it failed before or after the encode).</p>
                  {/if}
                </div>
              {/if}
            </li>
          {/each}
        </ul>

        {#if group.count > group.samples.length}
          <div class="border-t border-slate-100 px-4 py-2 text-xs text-slate-400 dark:border-slate-800 dark:text-slate-500">
            Showing {group.samples.length} of {group.count}. Use the Queue tab's “Failed” filter to see them all.
          </div>
        {/if}
      </div>
    {/each}
  </div>
{/if}
