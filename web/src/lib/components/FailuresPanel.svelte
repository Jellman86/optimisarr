<script lang="ts">
  // The Queue page's "Failures" tab: failed jobs grouped by their classified reason, with a per-job
  // drill-in to the captured ffmpeg log. Reads the diagnostics endpoints so "why did this fail?" is
  // answerable here, not only by reading container logs.
  import { api, type FailureGroup } from '../api'
  import Banner from './Banner.svelte'
  import EmptyState from './EmptyState.svelte'
  import Icon from './Icon.svelte'
  import { i18n, plural, t } from '../i18n/i18n.svelte'
  import { jobFailureDescription, jobFailureStory } from '../i18n/jobErrors'
  import { mediaTitle } from '../format'
  import Thumbnail from './Thumbnail.svelte'
  import JobDiagnosticDownload from './JobDiagnosticDownload.svelte'

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

  const totalFailures = $derived(groups.reduce((sum, group) => sum + group.count, 0))
</script>

<div class="mb-4 flex items-center justify-between">
  <p class="text-sm text-ink-3">
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
  <div class="card p-8 text-center text-ink-4">{i18n.m.common.loading_short}</div>
{:else if groups.length === 0}
  <EmptyState icon="check" title={i18n.m.shared.failures_empty_title} hint={i18n.m.shared.failures_empty_hint} />
{:else}
  <div class="space-y-4">
    {#each groups as group (group.category)}
      <div class="card overflow-hidden">
        <div class="flex items-start gap-3 border-b border-line-soft p-4 border-line">
          <Icon name="warning" class="mt-0.5 h-5 w-5 flex-shrink-0 text-bad" />
          <div class="min-w-0 flex-1">
            <div class="flex items-center gap-2">
              <h3 class="font-medium text-ink">{jobFailureDescription(group.category, i18n.m, group.description)}</h3>
              <span class="badge tone-bad">{group.count}</span>
            </div>
          </div>
        </div>

        <ul class="divide-y divide-line-soft">
          {#each group.samples as sample (sample.jobId)}
            {@const title = mediaTitle(sample.relativePath)}
            {@const story = jobFailureStory(group.category, sample.errorMessage, sample.verificationChecks, i18n.m)}
            <li class="px-4 py-3">
              <div class="flex items-start justify-between gap-3">
                <Thumbnail mediaFileId={sample.mediaFileId} size="md" />
                <div class="min-w-0 flex-1">
                  <div class="truncate text-sm font-medium text-ink" title={sample.relativePath ?? ''}>
                    {title.primary ?? '—'}{#if title.episode}{' '}<span class="ml-1 font-normal text-ink-3">{[title.episode, title.secondary].filter(Boolean).join(' · ')}</span>{/if}
                  </div>
                  <p class="mt-0.5 text-xs font-medium text-bad">{story.headline}</p>
                  {#if story.hint}<p class="mt-0.5 text-xs text-ink-3">{story.hint}</p>{/if}
                  {#if sample.jobType !== 'Normal'}
                    <span class="badge mt-1 tone-warn">
                      {sample.jobType === 'Calibration' ? i18n.m.shared.failure_job_calibration : i18n.m.shared.failure_job_preview}
                    </span>
                  {/if}
                  {#if sample.errorMessage || sample.verificationChecks.length > 0}
                    <details class="mt-1 text-xs text-ink-3">
                      <summary class="cursor-pointer">{i18n.m.queue.technical_error}</summary>
                      {#if sample.verificationChecks.length > 0}
                        <dl class="mt-2 space-y-1.5">
                          {#each sample.verificationChecks as check}
                            <div>
                              <dt class="font-semibold text-ink-2">{check.name}</dt>
                              <dd class="mt-0.5 break-words">{check.detail}</dd>
                            </div>
                          {/each}
                        </dl>
                      {:else if sample.errorMessage}
                        <p class="mt-1 whitespace-pre-line break-words font-mono text-[11px]">{sample.errorMessage}</p>
                      {/if}
                    </details>
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

              <div class="mt-2"><JobDiagnosticDownload jobId={sample.jobId} /></div>

              {#if openLogJobId === sample.jobId}
                <div class="mt-2">
                  {#if logLoadingId === sample.jobId}
                    <p class="text-xs text-ink-4">{i18n.m.shared.loading_log}</p>
                  {:else if logs[sample.jobId]}
                    <pre class="max-h-64 overflow-auto whitespace-pre-wrap break-all rounded-md bg-sunken p-3 font-mono text-[11px] leading-relaxed text-ink-2">{logs[sample.jobId]}</pre>
                  {:else}
                    <p class="text-xs text-ink-4">{i18n.m.shared.no_log}</p>
                  {/if}
                </div>
              {/if}
            </li>
          {/each}
        </ul>

        {#if group.count > group.samples.length}
          <div class="border-t border-line-soft px-4 py-2 text-xs text-ink-4 border-line">
            {t(i18n.m.shared.failures_showing, { shown: group.samples.length, total: group.count })}
          </div>
        {/if}
      </div>
    {/each}
  </div>
{/if}
