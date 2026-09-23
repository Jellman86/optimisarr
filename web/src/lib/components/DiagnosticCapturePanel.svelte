<script lang="ts">
  import { onMount } from 'svelte'
  import { api, type DiagnosticCapture } from '../api'
  import { i18n } from '../i18n/i18n.svelte'
  import ConfigSection from './ConfigSection.svelte'

  let capture = $state<DiagnosticCapture | null>(null)
  let duration = $state('24')
  let scopedJob = $state('')
  let exportJob = $state('')
  let includePaths = $state(false)
  let busy = $state(false)
  let loading = $state(true)
  let error = $state<string | null>(null)

  onMount(() => { void refresh() })

  async function refresh() {
    loading = true
    try {
      capture = await api.diagnosticCapture()
      if (capture?.scopedJobId) exportJob = String(capture.scopedJobId)
      error = null
    } catch (cause) {
      error = cause instanceof Error ? cause.message : String(cause)
    } finally {
      loading = false
    }
  }

  async function start() {
    // Svelte's number input binding may yield a number even when the state began as a string.
    const job = String(scopedJob).trim() ? Number(scopedJob) : null
    if (job !== null && (!Number.isSafeInteger(job) || job < 1)) {
      error = i18n.m.settings.diagnostics_job_invalid
      return
    }
    busy = true
    error = null
    try {
      capture = await api.startDiagnosticCapture({
        durationHours: duration === 'until' ? null : Number(duration),
        scopedJobId: job,
        includePaths,
      })
      if (job !== null) exportJob = String(job)
    } catch (cause) {
      error = cause instanceof Error ? cause.message : String(cause)
    } finally {
      busy = false
    }
  }

  async function stop() {
    if (!capture) return
    busy = true
    error = null
    try {
      capture = await api.stopDiagnosticCapture(capture.id)
    } catch (cause) {
      error = cause instanceof Error ? cause.message : String(cause)
    } finally {
      busy = false
    }
  }

  async function download() {
    if (!capture) return
    const jobId = Number(exportJob)
    if (!Number.isSafeInteger(jobId) || jobId < 1) {
      error = i18n.m.settings.diagnostics_job_invalid
      return
    }
    busy = true
    error = null
    try {
      const blob = await api.diagnosticBundle(capture.id, jobId)
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = `optimisarr-diagnostics-${jobId}-${capture.id}.json`
      link.click()
      window.setTimeout(() => URL.revokeObjectURL(url), 1000)
    } catch (cause) {
      error = cause instanceof Error ? cause.message : String(cause)
    } finally {
      busy = false
    }
  }

  const date = (value: string) => new Date(value).toLocaleString()
</script>

<ConfigSection id="diagnostic-capture" title={i18n.m.settings.diagnostics_title} description={i18n.m.settings.diagnostics_desc}>
  <div class="grid min-w-0 gap-5 xl:grid-cols-[minmax(0,1.3fr)_minmax(17rem,.7fr)]">
    <div class="min-w-0 space-y-4">
      <div class="flex min-w-0 flex-wrap items-center gap-2">
        <span class:recording={capture?.status === 'Recording'} class="capture-light" aria-hidden="true"></span>
        <strong class="text-sm text-ink">
          {loading ? i18n.m.common.loading_short : capture?.status === 'Recording' ? i18n.m.settings.diagnostics_recording : i18n.m.settings.diagnostics_off}
        </strong>
        {#if capture}
          <span class="text-xs text-ink-3">{capture.eventsStored} / {capture.maximumEvents} {i18n.m.settings.diagnostics_events}</span>
        {/if}
        <button class="btn btn-ghost ml-auto min-h-10 text-xs" disabled={loading} onclick={refresh}>{i18n.m.settings.diagnostics_refresh}</button>
      </div>

      {#if capture?.status === 'Recording'}
        <div class="rounded-lg border border-accent/25 bg-accent/5 p-4">
          <p class="text-sm text-ink-2">
            {i18n.m.settings.diagnostics_started} {date(capture.startedAt)} ·
            {capture.expiresAt ? date(capture.expiresAt) : i18n.m.settings.diagnostics_until_stopped}
          </p>
          <p class="mt-1 text-xs text-ink-3">
            {capture.scopedJobId ? `${i18n.m.settings.diagnostics_job} #${capture.scopedJobId}` : i18n.m.settings.diagnostics_all_jobs}
          </p>
          <button class="btn mt-4 min-h-11" disabled={busy} onclick={stop}>{i18n.m.settings.diagnostics_stop}</button>
        </div>
      {:else}
        <div class="grid min-w-0 gap-3 sm:grid-cols-2">
          <div>
            <label class="label" for="diagnostic-duration">{i18n.m.settings.diagnostics_duration}</label>
            <select id="diagnostic-duration" class="input w-full" bind:value={duration}>
              <option value="1">{i18n.m.settings.diagnostics_1h}</option>
              <option value="24">{i18n.m.settings.diagnostics_24h}</option>
              <option value="168">{i18n.m.settings.diagnostics_7d}</option>
              <option value="until">{i18n.m.settings.diagnostics_until_stopped}</option>
            </select>
          </div>
          <div>
            <label class="label" for="diagnostic-scope">{i18n.m.settings.diagnostics_job_optional}</label>
            <input id="diagnostic-scope" class="input w-full" type="number" min="1" step="1" bind:value={scopedJob} placeholder={i18n.m.settings.diagnostics_all_jobs} />
          </div>
        </div>
        <label class="flex cursor-pointer items-start gap-3 text-sm text-ink-2">
          <input type="checkbox" class="mt-1" bind:checked={includePaths} />
          <span>{i18n.m.settings.diagnostics_paths}</span>
        </label>
        <button class="btn btn-primary min-h-11" disabled={busy || loading} onclick={start}>{i18n.m.settings.diagnostics_start}</button>
      {/if}

      {#if capture}
        <div class="flex min-w-0 flex-wrap items-end gap-3 border-t pt-4" style="border-color: var(--edge)">
          <div class="min-w-40 flex-1">
            <label class="label" for="diagnostic-export-job">{i18n.m.settings.diagnostics_export_job}</label>
            <input id="diagnostic-export-job" class="input w-full" type="number" min="1" step="1" bind:value={exportJob} />
          </div>
          <button class="btn min-h-11" disabled={busy} onclick={download}>{i18n.m.settings.diagnostics_download}</button>
        </div>
      {/if}
      {#if error}<p class="text-sm text-bad" role="alert">{error}</p>{/if}
    </div>

    <aside class="evidence-note min-w-0 rounded-lg p-4 text-sm leading-relaxed text-ink-3">
      <span class="mb-2 block text-xs font-semibold uppercase tracking-widest text-accent">{i18n.m.settings.diagnostics_evidence}</span>
      <p>{i18n.m.settings.diagnostics_disclosure}</p>
      <p class="mt-3">{i18n.m.settings.diagnostics_limit}</p>
      <p class="mt-3">{i18n.m.settings.diagnostics_retention}</p>
    </aside>
  </div>
</ConfigSection>

<style>
  :global(#diagnostic-capture) { transition: box-shadow 180ms ease; }
  :global(#diagnostic-capture:hover), :global(#diagnostic-capture:focus-within) { box-shadow: var(--lift-3), inset 0 1px 0 var(--edge); }
  .capture-light { width: .55rem; height: .55rem; flex: none; border-radius: 50%; background: var(--ink-4); }
  .capture-light.recording { background: var(--accent); box-shadow: 0 0 0 .25rem color-mix(in srgb, var(--accent) 14%, transparent), 0 0 1rem var(--accent); }
  .evidence-note { background: var(--raised); box-shadow: inset 0 1px 0 var(--edge); }
  @media (prefers-reduced-motion: reduce) { :global(#diagnostic-capture) { transition: none; } }
</style>
