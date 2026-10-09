<script lang="ts">
  import { onMount } from 'svelte'
  import { api, type DiagnosticCapture } from '../api'
  import { i18n } from '../i18n/i18n.svelte'
  import ConfigSection from './ConfigSection.svelte'

  let captures = $state<DiagnosticCapture[]>([])
  let selection = $state('')
  let participants = $state('')
  let persistAcrossRestart = $state(false)
  let retentionDays = $state(7)
  let failureRetentionDays = $state(30)
  let maximumMiB = $state(4)
  let fromUtc = $state('')
  let toUtc = $state('')
  let capture = $state<DiagnosticCapture | null>(null)
  let activeCapture = $derived(captures.find(item => item.status === 'Recording') ?? null)
  let statusCapture = $derived(activeCapture ?? capture)
  let duration = $state('24')
  let scopedJob = $state('')
  let exportJob = $state('')
  let includePaths = $state(false)
  let busy = $state(false)
  let loading = $state(true)
  let collecting = $state(false)
  let error = $state<string | null>(null)
  let refreshGeneration = 0

  onMount(() => {
    void refresh()
    const update = () => { if (!busy) void refresh(true) }
    const timer = window.setInterval(update, 30000)
    window.addEventListener('diagnostic-capture-changed', update)
    return () => { refreshGeneration++; window.clearInterval(timer); window.removeEventListener('diagnostic-capture-changed', update) }
  })

  function selectCapture(next: DiagnosticCapture | null, reset = false) {
    if (reset || next?.id !== capture?.id) {
      exportJob = next?.scopedJobId ? String(next.scopedJobId) : ''
      fromUtc = ''; toUtc = ''
    }
    capture = next; selection = next?.id ?? ''
  }

  async function refresh(background = false) {
    const generation = ++refreshGeneration
    if (!background) loading = true
    try {
      const [latest, retained, workers] = await Promise.all([api.diagnosticCapture(), api.diagnosticCaptures(), api.workers()])
      if (generation !== refreshGeneration) return
      captures = retained
      selectCapture(retained.find(item => item.id === selection) ?? latest)
      participants = workers.filter(worker => worker.online).map(worker => `${worker.name} (${worker.protocolVersion >= 10 ? worker.operatingSystem : i18n.m.settings.diagnostics_update_required})`).join(', ')
      if (!background) error = null
    } catch (cause) {
      if (generation === refreshGeneration && !background) error = cause instanceof Error ? cause.message : String(cause)
    } finally {
      if (generation === refreshGeneration) loading = false
    }
  }

  async function start() {
    error = null
    const job = String(scopedJob ?? '').trim() ? Number(scopedJob) : null
    if (job !== null && (!Number.isSafeInteger(job) || job < 1)) {
      error = i18n.m.settings.diagnostics_job_invalid; return
    }
    const routine = Number(retentionDays), failure = Number(failureRetentionDays), storage = Number(maximumMiB)
    if (!Number.isSafeInteger(routine) || routine < 1 || routine > 30 || !Number.isSafeInteger(failure)
      || failure < routine || failure > 90 || !Number.isSafeInteger(storage) || storage < 1 || storage > 16) {
      error = i18n.m.settings.diagnostics_limits_invalid; return
    }
    busy = true; refreshGeneration++
    try {
      const started = await api.startDiagnosticCapture({
        durationHours: duration === 'until' ? null : Number(duration), scopedJobId: job,
        includePaths, persistAcrossRestart, retentionDays: routine, failureRetentionDays: failure, maximumBytes: storage * 1024 * 1024,
      })
      captures = [started, ...captures.filter(item => item.id !== started.id)]
      selectCapture(started)
      window.dispatchEvent(new Event('diagnostic-capture-changed'))
    } catch (cause) { error = cause instanceof Error ? cause.message : String(cause) }
    finally { busy = false }
  }

  async function stop(collect = false) {
    if (!activeCapture) return
    const id = activeCapture.id
    busy = true; error = null; refreshGeneration++
    try {
      const stopped = await api.stopDiagnosticCapture(id)
      captures = captures.map(item => item.id === stopped.id ? stopped : item)
      if (capture?.id === id) selectCapture(stopped)
      window.dispatchEvent(new Event('diagnostic-capture-changed'))
      if (collect) {
        collecting = true
        await api.waitForDiagnosticUploads(id)
        await saveBundle(undefined, false, id)
      }
    } catch (cause) { error = cause instanceof Error ? cause.message : String(cause) }
    finally { busy = false; collecting = false }
  }

  async function download() {
    if (!capture) return
    error = null
    const jobId = Number(exportJob)
    if (String(exportJob ?? '').trim() && (!Number.isSafeInteger(jobId) || jobId < 1)) {
      error = i18n.m.settings.diagnostics_job_invalid; return
    }
    if (!String(exportJob ?? '').trim() && fromUtc && toUtc && new Date(fromUtc) > new Date(toUtc)) {
      error = i18n.m.settings.diagnostics_range_invalid; return
    }
    busy = true
    try { await saveBundle(String(exportJob ?? '').trim() ? jobId : undefined) }
    catch (cause) { error = cause instanceof Error ? cause.message : String(cause) }
    finally { busy = false }
  }

  async function saveBundle(jobId?: number, applyTimeRange = true, id = capture?.id) {
    if (!id) return
    const blob = await api.diagnosticBundle(id, jobId, undefined,
      applyTimeRange && fromUtc ? new Date(fromUtc).toISOString() : undefined, applyTimeRange && toUtc ? new Date(toUtc).toISOString() : undefined)
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url; link.download = `optimisarr-diagnostics-${jobId ?? 'session'}-${id}.json`; link.click()
    window.setTimeout(() => URL.revokeObjectURL(url), 1000)
  }
  async function pin() {
    if (!capture) return
    busy = true; error = null; refreshGeneration++
    try { await api.pinDiagnosticCapture(capture.id, !capture.pinned); await refresh(true) }
    catch (cause) { error = cause instanceof Error ? cause.message : String(cause) }
    finally { busy = false }
  }
  async function remove() {
    if (!capture || !confirm(i18n.m.settings.diagnostics_delete_confirm)) return
    const id = capture.id
    busy = true; error = null; refreshGeneration++
    try {
      await api.deleteDiagnosticCapture(id)
      captures = captures.filter(item => item.id !== id)
      selectCapture(captures[0] ?? null, true)
      await refresh(true)
    } catch (cause) { error = cause instanceof Error ? cause.message : String(cause) }
    finally { busy = false }
  }

  const date = (value: string) => new Date(value).toLocaleString()
  const status = (value: string) => value === 'Recording' ? i18n.m.settings.diagnostics_recording
    : value === 'Expired' ? i18n.m.settings.diagnostics_expired : i18n.m.settings.diagnostics_stopped
</script>

<ConfigSection id="diagnostic-capture" title={i18n.m.settings.diagnostics_title} description={i18n.m.settings.diagnostics_desc}>
  <div class="grid min-w-0 gap-5 xl:grid-cols-[minmax(0,1.3fr)_minmax(17rem,.7fr)]">
    <div class="min-w-0 space-y-4">
      <div class="flex min-w-0 flex-wrap items-center gap-2">
        <span class:recording={!!activeCapture} class="capture-light" aria-hidden="true"></span>
        <strong class="text-sm text-ink">
          {loading ? i18n.m.common.loading_short : activeCapture ? i18n.m.settings.diagnostics_recording : i18n.m.settings.diagnostics_off}
        </strong>
        {#if statusCapture}
          <span class="text-xs text-ink-3">{statusCapture.eventsStored.toLocaleString()} / {statusCapture.maximumEvents.toLocaleString()} {i18n.m.settings.diagnostics_events}</span>
        {/if}
        <button class="btn btn-ghost ml-auto min-h-10 text-xs" disabled={busy || loading} onclick={() => refresh()}>{i18n.m.settings.diagnostics_refresh}</button>
      </div>

      {#if activeCapture}
        <div class="rounded-lg border border-accent/25 bg-accent/5 p-4">
          <p class="text-sm text-ink-2">
            {i18n.m.settings.diagnostics_started} {date(activeCapture.startedAt)} ·
            {activeCapture.expiresAt ? `${i18n.m.settings.diagnostics_expires}: ${date(activeCapture.expiresAt)}` : i18n.m.settings.diagnostics_until_stopped}
          </p>
          <p class="mt-1 text-xs text-ink-3">
            {activeCapture.scopedJobId ? `${i18n.m.settings.diagnostics_job} #${activeCapture.scopedJobId}` : i18n.m.settings.diagnostics_all_jobs}
          </p>
          <button class="btn mt-4 min-h-11" disabled={busy} onclick={() => stop(false)}>{i18n.m.settings.diagnostics_stop}</button>
          <button class="btn mt-4 ml-2 min-h-11" disabled={busy} onclick={() => stop(true)}>{i18n.m.settings.diagnostics_stop_collect}</button>
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
        <label class="flex cursor-pointer items-start gap-3 text-sm text-ink-2">
          <input type="checkbox" class="mt-1" bind:checked={persistAcrossRestart} />
          <span>{i18n.m.settings.diagnostics_restart}</span>
        </label>
        <div class="grid min-w-0 gap-3 sm:grid-cols-3">
          <label class="label">{i18n.m.settings.diagnostics_retention_days}<input class="input mt-2 w-full" type="number" min="1" max="30" bind:value={retentionDays} /></label>
          <label class="label">{i18n.m.settings.diagnostics_failure_days}<input class="input mt-2 w-full" type="number" min={retentionDays} max="90" bind:value={failureRetentionDays} /></label>
          <label class="label">{i18n.m.settings.diagnostics_storage}<input class="input mt-2 w-full" type="number" min="1" max="16" bind:value={maximumMiB} /></label>
        </div>
        <p class="text-xs text-ink-3">{i18n.m.settings.diagnostics_participants}: {participants || i18n.m.settings.diagnostics_no_workers}</p>
        <button class="btn btn-primary min-h-11" disabled={busy || loading} onclick={start}>{i18n.m.settings.diagnostics_start}</button>
      {/if}

      {#if collecting}<p class="text-sm text-ink-2" role="status">{i18n.m.settings.diagnostics_collecting}</p>{/if}
      {#if capture}
        <label class="label" for="diagnostic-saved">{i18n.m.settings.diagnostics_saved}</label>
        <select id="diagnostic-saved" class="input w-full" disabled={busy} bind:value={selection} onchange={() => selectCapture(captures.find(item => item.id === selection) ?? null, true)}>
          {#each captures as item (item.id)}<option value={item.id}>{date(item.startedAt)} · {status(item.status)} · {item.eventsStored.toLocaleString()} {i18n.m.settings.diagnostics_events}</option>{/each}
        </select>
        <p class="text-xs text-ink-3">{capture.pinned ? i18n.m.settings.diagnostics_pinned : capture.retainUntil ? `${capture.status === 'Recording' ? i18n.m.settings.diagnostics_retention_estimate : i18n.m.settings.diagnostics_retain_until}: ${date(capture.retainUntil)}` : ''}{capture.pinned || capture.retainUntil ? ' · ' : ''} {Math.ceil((capture.bytesStored ?? 0) / 1024)} KiB / {Math.round((capture.maximumBytes ?? 4194304) / 1024 / 1024)} MiB</p>
        {#if capture.eventLimitReached}<p class="text-sm text-warn" role="status">{i18n.m.settings.diagnostics_cap_reached}</p>{/if}
        <div class="flex flex-wrap gap-2">
          <button class="btn min-h-11" disabled={busy} onclick={pin}>{capture.pinned ? i18n.m.settings.diagnostics_unpin : i18n.m.settings.diagnostics_pin}</button>
          {#if capture.status !== 'Recording' && !capture.pinned}<button class="btn min-h-11" disabled={busy} onclick={remove}>{i18n.m.settings.diagnostics_delete}</button>{/if}
        </div>
        {#if capture.pinned}<p class="text-xs text-ink-3">{i18n.m.settings.diagnostics_pinned_delete_hint}</p>{/if}
        <div class="grid min-w-0 gap-3 sm:grid-cols-2">
          <label class="label">{i18n.m.settings.diagnostics_from}<input class="input mt-2 w-full" type="datetime-local" disabled={busy || !!String(exportJob ?? '').trim()} bind:value={fromUtc} /></label>
          <label class="label">{i18n.m.settings.diagnostics_to}<input class="input mt-2 w-full" type="datetime-local" disabled={busy || !!String(exportJob ?? '').trim()} bind:value={toUtc} /></label>
        </div>
        {#if String(exportJob ?? '').trim()}<p class="text-xs text-ink-3">{i18n.m.settings.diagnostics_job_range}</p>{/if}
        <div class="flex min-w-0 flex-wrap items-end gap-3 border-t pt-4" style="border-color: var(--edge)">
          <div class="min-w-40 flex-1">
            <label class="label" for="diagnostic-export-job">{i18n.m.settings.diagnostics_export_job} ({i18n.m.settings.diagnostics_optional})</label>
            <input id="diagnostic-export-job" class="input w-full" disabled={busy} type="number" min="1" step="1" bind:value={exportJob} />
          </div>
          <button class="btn min-h-11" disabled={busy} onclick={download}>{i18n.m.settings.diagnostics_download}</button>
        </div>
      {/if}
      {#if error}<p class="text-sm text-bad" role="alert">{error}</p>{/if}
    </div>

    <aside class="evidence-note min-w-0 rounded-lg p-4 text-sm leading-relaxed text-ink-3">
      <span class="mb-2 block text-xs font-semibold text-accent">{i18n.m.settings.diagnostics_evidence}</span>
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
