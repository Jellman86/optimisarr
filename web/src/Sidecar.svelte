<script lang="ts">
  // The Linux sidecar's own page. It says what this worker is doing for the server, in the main
  // app's language: the same mark (the one the server chose), the same status strip, the same
  // working-job card with artwork and a live frame. It can pair an unpaired worker and nothing
  // else; pausing, scheduling and revoking stay on the server.
  import { onMount } from 'svelte'
  import { formatSize } from './lib/format'
  import { theme } from './lib/stores/ui.svelte'
  import { brandAsset, parseBrandStyle } from './lib/brand-style'
  import BrandCanvas from './lib/components/BrandCanvas.svelte'
  import Chip from './lib/components/Chip.svelte'
  import Icon from './lib/components/Icon.svelte'
  import OkMark from './lib/components/OkMark.svelte'
  import PanelHeader from './lib/components/PanelHeader.svelte'
  import {
    STAGES, createSpeedTracker, encodedPercent, formatPairingCode, isCompletePairingCode,
    remainingSeconds, serverHost, sinceLabel, stageIndex, timecode, workerState,
  } from './lib/sidecar-view'

  type SourceMedia = { videoCodec: string | null; width: number | null; height: number | null; durationSeconds: number | null;
    audioCodecs: string | null; pixelFormat: string | null }
  type Job = { jobId: number; title: string; encoder: string; stage: string; encodedSeconds: number | null;
    kind?: string; sourceBytes: number | null; outputExtension: string | null; hardwareDecoder: string | null; previewRevision: number;
    hasArtwork: boolean; startedAt: string | null; sourceMedia: SourceMedia | null }
  type Snapshot = {
    name: string; state: string; serverAddress: string | null; scratchPath: string;
    storage: { kind: string; freeBytes: number; totalBytes: number }; concurrency: number;
    capabilities: { videoEncoders: string[]; audioEncoders?: string[]; hardwareDecoders: string[]; vmaf: string } | null;
    jobs: Job[]; recent: { jobId: number; title: string; delivered: boolean; finishedAt: string }[];
    metrics: { cpuPercent: number | null; gpuPercent: number | null; gpuEngine: string | null; sampledAt: string } | null;
    version: string; brandStyle: string;
    pairing: { required: boolean; configuredServer: string | null; inProgress: boolean; problem: string | null };
    update: { version: string; releaseUrl: string } | null;
  }

  let status = $state<Snapshot | null>(null)
  let error = $state(false)
  let loading = $state(true)
  let now = $state(Date.now())
  let speeds = $state<Record<number, number | null>>({})
  const tracker = createSpeedTracker()
  let request: AbortController | null = null
  let timer: ReturnType<typeof setTimeout>
  let disposed = false
  // The theme control names the setting it holds, not the scheme it would switch to: the page
  // follows the system, or it is pinned to one scheme.
  const themeLabel = $derived(theme.mode === 'system' ? 'Follow the system theme' : theme.isDark ? 'Dark theme' : 'Light theme')

  let serverInput = $state('')
  let codeInput = $state('')
  let pairing = $state(false)
  let pairProblem = $state<string | null>(null)

  const style = $derived(parseBrandStyle(status?.brandStyle ?? null))
  const working = $derived((status?.jobs.length ?? 0) > 0)
  const view = $derived(status ? workerState(status.state, status.jobs.length) : null)
  const host = $derived(serverHost(status?.serverAddress))
  const server = $derived.by(() => {
    try {
      const url = new URL(status?.serverAddress ?? '')
      return ['https:', 'http:'].includes(url.protocol) && !url.username && !url.password ? url.href : null
    } catch { return null }
  })
  const workersPage = $derived(server ? `${server.replace(/\/$/, '')}/#/settings/workers` : null)
  const usedStorage = $derived(status ? Math.max(0, status.storage.totalBytes - status.storage.freeBytes) : 0)
  const storagePercent = $derived(status && status.storage.totalBytes > 0 ? Math.round((usedStorage / status.storage.totalBytes) * 100) : 0)
  const configuredServer = $derived(status?.pairing.configuredServer ?? null)
  const canPair = $derived(!pairing && isCompletePairingCode(codeInput) && (configuredServer != null || /^https?:\/\/\S+$/i.test(serverInput.trim())))

  const percent = (value: number | null | undefined) => value != null && Number.isFinite(value) ? Math.round(Math.max(0, Math.min(100, value))) : null
  // Snippets do not keep the template's narrowing of status, so the header's figure is derived here.
  const slotSummary = $derived(status ? `${status.jobs.length} of ${status.concurrency} ${status.concurrency === 1 ? 'slot' : 'slots'}` : '')
  const cpu = $derived(percent(status?.metrics?.cpuPercent))
  const gpu = $derived(percent(status?.metrics?.gpuPercent))

  // The tab's icon follows the page: the server's mark, lit while work is running.
  $effect(() => {
    const href = brandAsset(style, theme.isDark, working, true)
    for (const link of document.querySelectorAll<HTMLLinkElement>('link[rel~="icon"]')) link.href = href
  })

  async function refresh() {
    clearTimeout(timer)
    if (document.hidden) { timer = setTimeout(refresh, 2000); return }
    request?.abort()
    const controller = new AbortController()
    request = controller
    const timeout = setTimeout(() => controller.abort(), 10000)
    try {
      const response = await fetch('/api/sidecar/status', { signal: controller.signal, cache: 'no-store' })
      if (!response.ok) throw new Error('Status unavailable')
      const next: Snapshot = await response.json()
      if (disposed) return
      const at = Date.now()
      const nextSpeeds: Record<number, number | null> = {}
      for (const job of next.jobs ?? []) nextSpeeds[job.jobId] = job.stage === 'Encoding' ? tracker.observe(job.jobId, job.encodedSeconds, at) : null
      tracker.forget((next.jobs ?? []).map((job) => job.jobId))
      status = next
      speeds = nextSpeeds
      now = at
      error = false
    } catch { if (!disposed) error = true }
    finally {
      clearTimeout(timeout)
      loading = false
      if (!disposed) timer = setTimeout(refresh, 2000)
    }
  }

  async function pair(event: SubmitEvent) {
    event.preventDefault()
    if (!canPair) return
    pairing = true
    pairProblem = null
    const controller = new AbortController()
    // The first pairing can wait for this machine's hardware check, which runs real encodes.
    const timeout = setTimeout(() => controller.abort(), 180000)
    try {
      const response = await fetch('/api/sidecar/pair', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, signal: controller.signal,
        body: JSON.stringify({ server: configuredServer ? null : serverInput.trim(), code: codeInput }),
      })
      if (!response.ok) {
        const body = await response.json().catch(() => null) as { error?: string } | null
        pairProblem = body?.error ?? 'Pairing did not complete. Try again.'
      } else {
        codeInput = ''
      }
    } catch {
      pairProblem = 'This page lost contact with the worker while pairing. Refresh to see whether it paired.'
    } finally {
      clearTimeout(timeout)
      pairing = false
      void refresh()
    }
  }

  onMount(() => {
    void refresh()
    const clock = setInterval(() => { now = Date.now() }, 1000)
    return () => { disposed = true; clearTimeout(timer); clearInterval(clock); request?.abort() }
  })
</script>

<svelte:head><title>{status ? `${status.name} · Optimisarr sidecar` : 'Optimisarr · Linux sidecar'}</title></svelte:head>

<div class="mx-auto max-w-300 px-6 pb-8 max-sm:px-4 max-sm:pb-6">
  <header class="flex items-center justify-between gap-4 pb-4 pt-5">
    <div class="flex min-w-0 items-center gap-3">
      <BrandCanvas {style} {working} dark={theme.isDark} class="h-11 w-11 flex-none" />
      <div class="min-w-0">
        <p class="text-title font-semibold leading-tight tracking-tight text-ink">Optimisarr</p>
        <p class="label mb-0 mt-0.5 whitespace-nowrap">Linux sidecar</p>
      </div>
    </div>
    <div class="flex items-center gap-2">
      {#if server}
        <a class="btn whitespace-nowrap" href={server}><span class="hidden sm:inline">Open</span> Optimisarr<Icon name="arrow-up-right" class="h-3.5 w-3.5" /></a>
      {/if}
      <button class="btn btn-ghost btn-icon" onclick={() => theme.toggle()} aria-label={themeLabel} title={themeLabel}>
        <Icon name={theme.icon} class="h-4 w-4" />
      </button>
    </div>
  </header>

  <main class="flex flex-col gap-4">
    {#if error}
      <section class="card p-5" role="alert">
        <h1 class="flex items-center gap-2 text-base font-semibold text-bad"><Icon name="warning" class="h-4 w-4" />Cannot reach this sidecar</h1>
        <p class="mt-1 text-sm text-ink-2">What was shown may be out of date, so it is hidden. Check that the container is running, then try again.</p>
        <button class="btn mt-4" onclick={refresh}><Icon name="retry" class="h-4 w-4" />Retry</button>
      </section>
    {:else if loading}
      <section class="card flex items-center gap-3 p-5 text-sm text-ink-2" role="status">
        <span class="h-2 w-2 animate-pulse rounded-full bg-accent" aria-hidden="true"></span>Loading worker status…
      </section>
    {:else if status && view}
      <!-- The main app's status strip: one state over why, and the few numbers that explain it. -->
      <section class="status-strip card" aria-label="Worker state">
        <div class="status-strip-state">
          <span class="h-2 w-2 flex-none rounded-full {view.tone === 'live' ? 'animate-pulse bg-accent' : view.tone === 'ok' ? 'bg-ok' : view.tone === 'warn' ? 'bg-warn' : view.tone === 'bad' ? 'bg-bad' : 'bg-ink-4'}" aria-hidden="true"></span>
          <div class="status-strip-text">
            <span class="sr-only">State:</span>
            <span class="status-strip-label text-sm font-semibold {view.tone === 'live' ? 'text-accent' : view.tone === 'warn' ? 'text-warn' : view.tone === 'bad' ? 'text-bad' : 'text-ink'}">{view.label}</span>
            <span class="status-strip-reason text-label">{view.detail}</span>
          </div>
        </div>
        <div class="status-strip-fact"><span class="label mb-0">Slots</span><span class="text-sm font-semibold tabular-nums text-ink">{status.jobs.length} / {status.concurrency}</span></div>
        <div class="status-strip-fact"><span class="label mb-0">CPU</span><span class="text-sm font-semibold tabular-nums text-ink">{cpu != null ? `${cpu}%` : '—'}</span></div>
        <div class="status-strip-fact"><span class="label mb-0">GPU</span><span class="text-sm font-semibold tabular-nums text-ink">{gpu != null ? `${gpu}%` : '—'}</span>{#if status.metrics?.gpuEngine}<span class="text-xs text-ink-3" data-gpu-engine>{status.metrics.gpuEngine} engine</span>{/if}</div>
        <div class="status-strip-fact status-strip-optional"><span class="label mb-0">Free in {status.storage.kind === 'RAM' ? 'RAM' : status.storage.kind.toLowerCase()}</span><span class="text-sm font-semibold tabular-nums text-ink">{formatSize(status.storage.freeBytes)}</span></div>
      </section>

      {#if status.update}
        <p class="callout tone-info flex flex-wrap items-center gap-x-3 gap-y-1">
          <Icon name="download" class="h-4 w-4" />
          <span>Optimisarr {status.update.version} is available. This worker runs {status.version}; pull the new sidecar image to stay in step with the server.</span>
          <a class="focus-ring hit-target inline-flex items-center rounded-sm font-semibold underline" href={status.update.releaseUrl} rel="noreferrer">Release notes</a>
        </p>
      {/if}

      {#if status.pairing.required}
        <div class="grid grid-cols-1 gap-4 md:grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)]">
          <section class="card p-5 sm:p-6" aria-labelledby="pair-title">
            <p class="label">Pairing</p>
            <h1 id="pair-title" class="text-2xl font-semibold tracking-tight text-ink">Pair {status.name}</h1>
            <p class="mt-1 text-sm text-ink-2">Connect this worker to your Optimisarr server so it can take encodes the server hands out.</p>
            <!-- Each step's number is an ordinal on a quiet well, not a link, so it reads in ink. -->
            <ol class="mt-4 flex flex-col gap-2.5 text-sm text-ink-2">
              <li class="flex items-start gap-3"><span class="grid h-5.5 w-5.5 flex-none place-items-center rounded-full bg-sunken text-micro font-semibold tabular-nums text-ink-2 shadow-[var(--inset-1)]">1</span><span>In Optimisarr, open <strong>Settings → Workers</strong> and choose <strong>Pair a sidecar</strong>. Remote workers must be turned on there.</span></li>
              <li class="flex items-start gap-3"><span class="grid h-5.5 w-5.5 flex-none place-items-center rounded-full bg-sunken text-micro font-semibold tabular-nums text-ink-2 shadow-[var(--inset-1)]">2</span><span>Enter the server address and the eight-digit code it shows. A code lasts five minutes.</span></li>
            </ol>
            <form class="mt-5 flex flex-col gap-4" onsubmit={pair}>
              <div>
                <label class="label" for="pair-server">Server address</label>
                {#if configuredServer}
                  <input id="pair-server" class="input font-mono" value={configuredServer} readonly aria-describedby="pair-server-note" />
                  <p id="pair-server-note" class="mt-1 text-xs text-ink-3">Set by this container's OPTIMISARR_SERVER.</p>
                {:else}
                  <input id="pair-server" class="input font-mono" type="url" inputmode="url" autocomplete="url" spellcheck="false"
                    placeholder="https://optimisarr.example.com" bind:value={serverInput} disabled={pairing} />
                {/if}
              </div>
              <div>
                <label class="label" for="pair-code">Pairing code</label>
                <input id="pair-code" class="input code-input font-mono" inputmode="numeric" autocomplete="one-time-code" spellcheck="false"
                  placeholder="0000 0000" maxlength="9" value={codeInput} disabled={pairing}
                  aria-invalid={pairProblem ? 'true' : undefined} aria-describedby={pairProblem || status.pairing.problem ? 'pair-problem' : undefined}
                  oninput={(event) => { codeInput = formatPairingCode(event.currentTarget.value); event.currentTarget.value = codeInput }} />
              </div>
              {#if pairProblem ?? status.pairing.problem}
                <p id="pair-problem" class="callout tone-bad" role="alert">{pairProblem ?? status.pairing.problem}</p>
              {/if}
              <div class="flex flex-wrap items-center gap-3">
                <button class="btn btn-primary" type="submit" disabled={!canPair} aria-busy={pairing}>
                  {#if pairing}<span class="h-2 w-2 animate-pulse rounded-full bg-white" aria-hidden="true"></span>Pairing…{:else}Pair this worker{/if}
                </button>
                {#if pairing && !status.capabilities}
                  <span class="text-xs text-ink-3" role="status">Finishing this machine's hardware check first. The first one can take a minute.</span>
                {/if}
              </div>
            </form>
          </section>

          <section class="card p-5 sm:p-6" aria-labelledby="pair-promise">
            <p class="label">Once paired</p>
            <h2 id="pair-promise" class="text-base font-semibold text-ink">Optimisarr stays in charge</h2>
            <!-- Promises, not states or links, so the glyphs stay quiet: only the verified one wears the ok mark. -->
            <ul class="mt-3.5 flex flex-col gap-3 text-sm text-ink-2">
              <li class="flex items-start gap-2.5"><Icon name="shield-check" class="mt-0.5 h-4 w-4 flex-none text-ink-3" /><span>This worker only ever receives a copy. It never replaces, moves or deletes a file.</span></li>
              <li class="flex items-start gap-2.5"><OkMark class="mt-0.5 h-4 w-4" /><span>Every encode it returns is verified on the server before anything changes.</span></li>
              <li class="flex items-start gap-2.5"><Icon name="server" class="mt-0.5 h-4 w-4 flex-none text-ink-3" /><span>Pause, drain and revoke it from Settings → Workers.</span></li>
            </ul>
            <p class="label mt-5">What this machine proved</p>
            {#if status.capabilities}
              <div class="flex flex-wrap gap-1.5">{#each status.capabilities.videoEncoders as encoder}<Chip mono>{encoder}</Chip>{:else}<span class="text-sm text-ink-3">No working video encoder was found.</span>{/each}</div>
            {:else}
              <p class="flex items-center gap-2 text-sm text-ink-3"><span class="h-1.5 w-1.5 animate-pulse rounded-full bg-accent" aria-hidden="true"></span>Running real test encodes…</p>
            {/if}
          </section>
        </div>
      {:else}
        <section class="card" aria-labelledby="work-title">
          <PanelHeader title="In flight" id="work-title" as="h1">
            {#snippet trailing()}
              <span class="text-xs tabular-nums text-ink-3">{slotSummary}{#if host}{' · for '}<span class="break-all font-mono">{host}</span>{/if}</span>
            {/snippet}
          </PanelHeader>
          {#each status.jobs as job (job.jobId)}
            {@const step = stageIndex(job.stage)}
            {@const duration = job.sourceMedia?.durationSeconds ?? null}
            {@const progress = job.stage === 'Encoding' ? encodedPercent(job.encodedSeconds, duration) : null}
            {@const speed = speeds[job.jobId] ?? null}
            {@const left = job.stage === 'Encoding' ? remainingSeconds(job.encodedSeconds, duration, speed) : null}
            <article class="job" aria-label={job.title}>
              <div class="poster" data-thumbnail>
                {#if job.hasArtwork}<img src={`/api/sidecar/jobs/${job.jobId}/artwork`} alt="" loading="lazy" />{:else}<Icon name={job.kind === 'Audio' ? 'waveform' : 'film'} class="h-5 w-5 text-ink-5" />{/if}
              </div>
              <div class="min-w-0">
                <div class="flex flex-wrap items-center gap-2">
                  <span class="inline-flex items-center gap-1.5 text-micro font-medium text-accent"><span class="h-1.5 w-1.5 animate-pulse rounded-full bg-accent" aria-hidden="true"></span>{STAGES[step].active}</span>
                  <Chip mono>{job.encoder}</Chip>
                  {#if job.hardwareDecoder}<Chip tone="ok">GPU decode · <span class="font-mono">{job.hardwareDecoder}</span></Chip>{:else}<Chip tone="muted">Software decode</Chip>{/if}
                  {#if job.startedAt}<span class="ml-auto text-micro tabular-nums text-ink-3">{sinceLabel(job.startedAt, now)} on this worker</span>{/if}
                </div>
                <h2 class="job-title">{job.title}</h2>
                <div class="progress-track" role="progressbar" aria-label={`Progress for ${job.title}`} aria-valuemin="0" aria-valuemax="100" aria-valuenow={progress != null ? Math.floor(progress) : undefined} aria-valuetext={progress == null ? STAGES[step].doing : undefined}>
                  {#if progress != null}<div class="progress-fill" style:width={`${progress}%`}></div>{:else}<div class="progress-indeterminate"></div>{/if}
                </div>
                <div class="readout">
                  {#if progress != null}
                    <strong>{Math.floor(progress)}% encoded</strong>
                    <span>{timecode(job.encodedSeconds)} / {timecode(duration)}</span>
                    {#if speed != null}<span>{speed.toFixed(speed < 10 ? 2 : 1)}×</span>{/if}
                    {#if left != null}<span class="ml-auto">{left < 60 ? `${Math.round(left)}s left` : `~${Math.round(left / 60)} min left`}</span>{/if}
                  {:else if job.stage === 'Encoding' && job.encodedSeconds != null}
                    <strong>{timecode(job.encodedSeconds)} encoded</strong>
                  {/if}
                </div>
                <p class="text-xs text-ink-3">{STAGES[step].doing}</p>
                <ol class="job-stages mt-4 max-sm:grid-cols-2" aria-label="Stages on this worker">
                  {#each STAGES as stage, index}
                    <li class:stage-done={index < step} class:stage-current={index === step} aria-current={index === step ? 'step' : undefined}>{#if index < step}<OkMark />{:else if index === step}<span class="stage-live" aria-hidden="true"></span>{:else}<span class="stage-pending" aria-hidden="true"></span>{/if}<span class="min-w-0">{stage.label}{#if index < step}<span class="sr-only"> (done)</span>{/if}</span></li>
                  {/each}
                </ol>
                <div class="mt-4 flex flex-wrap gap-x-4 gap-y-1.5 text-caption tabular-nums text-ink-3">
                  {#if job.sourceMedia?.videoCodec}<span class="font-mono">{job.sourceMedia.videoCodec.toUpperCase()}</span>{/if}
                  {#if job.sourceMedia?.width && job.sourceMedia.height}<span>{job.sourceMedia.width} × {job.sourceMedia.height}</span>{/if}
                  {#if duration}<span>{timecode(duration)}</span>{/if}
                  {#if job.sourceBytes}<span>{formatSize(job.sourceBytes)}</span>{/if}
                  {#if job.sourceMedia?.pixelFormat}<span class="font-mono">{job.sourceMedia.pixelFormat}</span>{/if}
                  {#if job.sourceMedia?.audioCodecs}<span>Audio <span class="font-mono">{job.sourceMedia.audioCodecs}</span></span>{/if}
                  {#if job.outputExtension}<span>→ <span class="font-mono">{job.outputExtension.toUpperCase()}</span></span>{/if}
                  <span>Job {job.jobId}</span>
                </div>
              </div>
              <figure class="frame" class:audio-spectrum={job.kind === 'Audio'} class:frame-empty={job.previewRevision === 0 && job.kind !== 'Audio'}>
                {#if job.previewRevision > 0}
                  <img src={`/api/sidecar/jobs/${job.jobId}/preview?v=${job.previewRevision}`} alt={job.kind === 'Audio' ? `Source audio spectrogram of ${job.title}` : `Latest frame of ${job.title}`} />
                  <figcaption><span class="h-1.5 w-1.5 animate-pulse rounded-full bg-bad" aria-hidden="true"></span>{job.kind === 'Audio' ? 'Source spectrum' : 'Live frame'}</figcaption>
                {:else}
                  <span class="text-xs text-ink-3">{job.kind === 'Audio' ? 'Spectrum appears once encoding starts' : job.stage === 'FetchingSource' ? 'Frames appear once encoding starts' : 'Waiting for a frame'}</span>
                {/if}
              </figure>
              {#if job.kind === 'Audio'}
                <p class="spectrum-caption text-xs text-ink-3">Source spectrogram · up to 3 s · frequency 0–24 kHz (log). Brighter colour means stronger signal; verification runs separately.</p>
              {/if}
            </article>
          {:else}
            <div class="px-5 py-8 text-center">
              <p class="text-base font-semibold text-ink">{status.state === 'Connected' ? 'Ready for work' : view.label}</p>
              <p class="mx-auto mt-1 max-w-xl text-sm text-ink-3">
                {#if status.state === 'Connected'}Nothing assigned right now. Jobs arrive when a library allows workers and this machine can run its encoder.
                {:else if status.state === 'Draining'}Taking no new jobs. Resume it from Settings → Workers on the server.
                {:else if status.state === 'Starting'}Checking the media tools and hardware before accepting jobs.
                {:else}Check this worker in Settings → Workers on the server, and its container log.{/if}
              </p>
              {#if workersPage}<a class="btn mt-4" href={workersPage}>Workers on {host}<Icon name="arrow-up-right" class="h-3.5 w-3.5" /></a>{/if}
            </div>
          {/each}
        </section>
      {/if}

      <!-- How the work is split, so it is clear what happens here and what never does. -->
      <section class="card" aria-labelledby="flow-title">
        <PanelHeader title="How this worker helps" id="flow-title" as="h2" />
        <ol class="flow">
          <li class="flow-server"><Icon name="server" class="h-4 w-4" /><span><strong>Server</strong> picks a job and keeps the original</span></li>
          {#each STAGES as stage, index}
            {@const active = status.jobs.some((job) => stageIndex(job.stage) === index)}
            <li class:flow-active={active}><Icon name={index === 0 ? 'download' : index === 1 ? 'film' : index === 2 ? 'search' : 'upload'} class="h-4 w-4" /><span><strong>{stage.label}</strong> {index === 0 ? 'a copy here' : index === 1 ? 'on this hardware' : index === 2 ? 'requested quality and verification checks' : 'the candidate'}</span></li>
          {/each}
          <li class="flow-server"><Icon name="shield-check" class="h-4 w-4" /><span><strong>Server</strong> validates results, then replaces</span></li>
        </ol>
      </section>

      <div class="grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-3">
        <section class="card p-5" aria-labelledby="storage-title">
          <p class="label">Working storage</p>
          <h2 id="storage-title" class="text-base font-semibold text-ink">{status.storage.kind === 'RAM' ? 'In memory' : 'On disk'}</h2>
          <p class="mt-3 text-2xl font-semibold tabular-nums tracking-tight text-ink" data-storage-free>{formatSize(status.storage.freeBytes)} <span class="text-sm font-normal text-ink-3">free</span></p>
          <div class="progress-track mt-3" role="meter" aria-label="Working storage used" aria-valuemin="0" aria-valuemax="100" aria-valuenow={storagePercent}><div class="progress-fill" style:width={`${storagePercent}%`}></div></div>
          <p class="mt-2 text-xs tabular-nums text-ink-3" data-storage-used>{formatSize(usedStorage)} used of {formatSize(status.storage.totalBytes)} · <span class="break-all font-mono">{status.scratchPath}</span></p>
          <p class="mt-3 text-xs leading-relaxed text-ink-3">{status.storage.kind === 'RAM' ? 'Source copies and candidates live in RAM while a job runs, and vanish with it.' : 'Source copies and candidates are written here while a job runs, then removed.'}</p>
        </section>

        <section class="card p-5" aria-labelledby="hardware-title">
          <p class="label">Media hardware</p>
          <h2 id="hardware-title" class="text-base font-semibold text-ink">Proved on this machine</h2>
          {#if status.capabilities}
            <p class="label mt-3">Video encoders</p>
            <div class="flex flex-wrap gap-1.5">{#each status.capabilities.videoEncoders as encoder}<Chip mono>{encoder}</Chip>{:else}<span class="text-sm text-ink-3">None available</span>{/each}</div>
            <p class="label mt-3">Audio encoders</p>
            <div class="flex flex-wrap gap-1.5">{#each status.capabilities.audioEncoders ?? [] as encoder}<Chip mono>{encoder}</Chip>{:else}<span class="text-sm text-ink-3">None reported</span>{/each}</div>
            <p class="label mt-3">Hardware decode</p>
            <div class="flex flex-wrap gap-1.5">{#each status.capabilities.hardwareDecoders as decoder}<Chip tone="ok" mono>{decoder}</Chip>{:else}<span class="text-sm text-ink-3">Software only</span>{/each}</div>
            <p class="label mt-3">Quality measurement</p>
            <Chip>{status.capabilities.vmaf === 'Cuda' ? 'VMAF · CUDA' : status.capabilities.vmaf === 'Cpu' ? 'VMAF · CPU' : 'Unavailable'}</Chip>
          {:else}
            <p class="mt-3 flex items-center gap-2 text-sm text-ink-3"><span class="h-1.5 w-1.5 animate-pulse rounded-full bg-accent" aria-hidden="true"></span>Running real test encodes…</p>
          {/if}
        </section>

        <section class="card p-5" aria-labelledby="recent-title">
          <p class="label">Recent</p>
          <h2 id="recent-title" class="text-base font-semibold text-ink">Since this worker started</h2>
          {#if status.recent.length}
            <ul class="mt-3 flex flex-col gap-2.5">
              {#each status.recent as item (item.jobId + item.finishedAt)}
                <li class="flex items-start gap-2 text-sm">
                  {#if item.delivered}<OkMark class="mt-0.5 h-4 w-4" />{:else}<Icon name="warning" class="mt-0.5 h-4 w-4 flex-none text-warn" />{/if}
                  <span class="min-w-0 flex-1"><span class="line-clamp-2 break-words text-ink">{item.title}</span><span class="text-xs text-ink-3">{item.delivered ? 'Returned to the server' : 'Stopped here; the server has the details'}</span></span>
                  <span class="whitespace-nowrap text-micro tabular-nums text-ink-3">{sinceLabel(item.finishedAt, now)} ago</span>
                </li>
              {/each}
            </ul>
          {:else}
            <p class="mt-3 text-sm text-ink-3">Nothing finished yet. Results are judged on the server, which keeps the full history.</p>
          {/if}
        </section>
      </div>

      <footer class="flex flex-wrap justify-between gap-x-6 gap-y-2 px-1 pt-2 text-xs text-ink-3">
        <a class="btn btn-ghost" href="/api/sidecar/diagnostics" download="optimisarr-sidecar-diagnostics.json">Export local diagnostics</a>
        <span class="font-mono">{status.name} · v{status.version}</span>
        <span>This page is read-only apart from pairing. Pause, drain and scheduling live on the server.</span>
      </footer>
    {/if}
  </main>
</div>

<style>
  :global(body) { margin: 0; min-width: 320px; }
  .code-input { font-size: 1.35rem; letter-spacing: .18em; padding-block: .6rem; max-width: 16rem; }
  .job { display: grid; grid-template-columns: 7rem minmax(0, 1fr) minmax(0, 17rem); gap: 1.25rem 1.5rem; padding: 1.25rem 1.25rem 1.4rem; }
  .job + .job { box-shadow: inset 0 1px 0 var(--divide-soft); }
  .poster { width: 7rem; aspect-ratio: 2 / 3; border-radius: .5rem; overflow: hidden; display: grid; place-items: center; background: var(--raised); box-shadow: var(--lift-2); align-self: start; }
  .poster img { width: 100%; height: 100%; object-fit: cover; }
  .job-title { margin: .5rem 0 .9rem; font-size: 1.3rem; line-height: 1.3; letter-spacing: -.025em; font-weight: 600; color: var(--ink); overflow-wrap: anywhere; }
  .readout { display: flex; flex-wrap: wrap; align-items: baseline; gap: .4rem 1rem; margin: .6rem 0 .25rem; font-size: .75rem; color: var(--ink-3); font-variant-numeric: tabular-nums; }
  .readout strong { font-size: 1rem; color: var(--ink); font-weight: 600; }
  .frame { position: relative; margin: 0; aspect-ratio: 16 / 9; border-radius: .6rem; overflow: hidden; display: grid; place-items: center; background: var(--sunken); box-shadow: var(--inset-1); align-self: start; }
  .spectrum-caption { grid-column: 2 / -1; }
  .audio-spectrum { aspect-ratio: 10 / 3; }
  .frame img { width: 100%; height: 100%; object-fit: contain; background: #000; }
  .frame figcaption { position: absolute; left: .5rem; top: .5rem; display: flex; align-items: center; gap: .35rem; padding: .15rem .45rem; border-radius: 999px; background: rgba(0, 0, 0, .6); color: #fff; font: 600 .625rem/1 var(--font-sans); letter-spacing: .08em; text-transform: uppercase; }
  .flow { display: grid; grid-template-columns: repeat(6, minmax(0, 1fr)); gap: .5rem; padding: 1rem; }
  .flow li { display: flex; flex-direction: column; gap: .45rem; padding: .7rem .75rem; border-radius: .6rem; font-size: .75rem; line-height: 1.35; color: var(--ink-3); background: var(--sunken); box-shadow: var(--inset-1); }
  .flow li strong { display: block; font-size: .8125rem; color: var(--ink-2); }
  .flow li :global(svg) { color: var(--ink-4); }
  .flow .flow-server { background: none; box-shadow: inset 0 0 0 1px var(--divide-soft); }
  .flow .flow-server :global(svg) { color: var(--ink-3); }
  .flow .flow-active { background: var(--accent-soft); box-shadow: none; }
  .flow .flow-active strong { color: var(--ink); } .flow .flow-active :global(svg) { color: var(--accent); }
  @media (max-width: 1023px) {
    .job { grid-template-columns: 6rem minmax(0, 1fr); }
    .poster { width: 6rem; }
    .spectrum-caption { grid-column: 1 / -1; }
    .frame { grid-column: 1 / -1; max-width: 26rem; }
    .flow { grid-template-columns: repeat(3, minmax(0, 1fr)); }
  }
  @media (max-width: 639px) {
    .frame-empty { display: none; }
    .job { grid-template-columns: 3.75rem minmax(0, 1fr); padding: 1rem; gap: 1rem; }
    .poster { width: 3.75rem; }
    .job-title { font-size: 1.1rem; }
    .flow { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  }
</style>
