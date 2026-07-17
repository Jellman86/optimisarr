<script lang="ts">
  // The Queue page's "Failures" tab: failed jobs grouped by their classified reason, with a per-job
  // drill-in to the captured ffmpeg log. Reads the diagnostics endpoints so "why did this fail?" is
  // answerable here, not only by reading container logs.
  import { api, type FailureGroup } from '../api'
  import Banner from './Banner.svelte'
  import EmptyState from './EmptyState.svelte'
  import Icon from './Icon.svelte'
  import { i18n, plural, t } from '../i18n/i18n.svelte'
  import { jobFailureDescription } from '../i18n/jobErrors'

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
      error = err instanceof Error ? err.message : i18n.m.shared.failures_load_error
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
      {plural(totalFailures, i18n.m.shared.failures_summary_one, i18n.m.shared.failures_summary_other, totalFailures.toLocaleString())}
    {:else}
      {i18n.m.shared.failures_intro}
    {/if}
  </p>
  <button class="btn btn-ghost inline-flex items-center gap-1 px-3 py-1 text-xs" onclick={load} disabled={loading}>
    <Icon name="retry" class="h-4 w-4" />
    {i18n.m.shared.refresh}
  </button>
</div>

{#if error}
  <Banner kind="error" class="mb-4">{error}</Banner>
{/if}

{#if loading}
  <div class="card p-8 text-center text-slate-400">{i18n.m.common.loading_short}</div>
{:else if groups.length === 0}
  <EmptyState icon="check" title={i18n.m.shared.failures_empty_title} hint={i18n.m.shared.failures_empty_hint} />
{:else}
  <div class="space-y-4">
    {#each groups as group (group.category)}
      <div class="card overflow-hidden">
        <div class="flex items-start gap-3 border-b border-slate-100 p-4 dark:border-slate-800">
          <Icon name="warning" class="mt-0.5 h-5 w-5 flex-shrink-0 text-red-500" />
          <div class="min-w-0 flex-1">
            <div class="flex items-center gap-2">
              <h3 class="font-medium text-slate-800 dark:text-slate-100">{jobFailureDescription(group.category, i18n.m, group.description)}</h3>
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
                  {#if sample.jobType !== 'Normal'}
                    <span class="badge mt-1 bg-amber-100 text-amber-800 dark:bg-amber-950 dark:text-amber-300">
                      {sample.jobType === 'Calibration' ? 'Personal quality check' : 'Preview comparison'}
                    </span>
                  {/if}
                  {#if sample.errorMessage}
                    <details class="mt-1 text-xs text-slate-500 dark:text-slate-400">
                      <summary class="cursor-pointer">{i18n.m.queue.technical_error}</summary>
                      <p class="mt-1 whitespace-pre-line break-words font-mono text-[11px] text-red-600 dark:text-red-400">{sample.errorMessage}</p>
                    </details>
                  {/if}
                  {#if sample.verificationChecks.length > 0}
                    <dl class="mt-2 space-y-2 rounded-lg border border-red-200 bg-red-50 p-3 text-xs dark:border-red-900 dark:bg-red-950/30">
                      {#each sample.verificationChecks as check}
                        <div>
                          <dt class="font-semibold text-red-800 dark:text-red-300">{check.name}</dt>
                          <dd class="mt-0.5 break-words text-red-700 dark:text-red-400">{check.detail}</dd>
                        </div>
                      {/each}
                    </dl>
                  {/if}
                </div>
                <button
                  class="btn btn-ghost inline-flex flex-shrink-0 items-center gap-1 px-2 py-1 text-xs"
                  onclick={() => toggleLog(sample.jobId)}
                >
                  <Icon name="chevron" class="h-3.5 w-3.5 transition-transform {openLogJobId === sample.jobId ? 'rotate-180' : ''}" />
                  {openLogJobId === sample.jobId ? i18n.m.shared.hide_log : i18n.m.shared.view_log}
                </button>
              </div>

              {#if openLogJobId === sample.jobId}
                <div class="mt-2">
                  {#if logLoadingId === sample.jobId}
                    <p class="text-xs text-slate-400">{i18n.m.shared.loading_log}</p>
                  {:else if logs[sample.jobId]}
                    <pre class="max-h-64 overflow-auto whitespace-pre-wrap break-all rounded-md bg-slate-50 p-3 font-mono text-[11px] leading-relaxed text-slate-600 dark:bg-slate-900/60 dark:text-slate-300">{logs[sample.jobId]}</pre>
                  {:else}
                    <p class="text-xs text-slate-400">{i18n.m.shared.no_log}</p>
                  {/if}
                </div>
              {/if}
            </li>
          {/each}
        </ul>

        {#if group.count > group.samples.length}
          <div class="border-t border-slate-100 px-4 py-2 text-xs text-slate-400 dark:border-slate-800 dark:text-slate-500">
            {t(i18n.m.shared.failures_showing, { shown: group.samples.length, total: group.count })}
          </div>
        {/if}
      </div>
    {/each}
  </div>
{/if}
