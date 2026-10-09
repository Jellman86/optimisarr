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
  import Icon from './lib/components/Icon.svelte'
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

<div class="sidecar-shell">
  <header class="sidecar-header">
    <div class="flex min-w-0 items-center gap-3">
      <BrandCanvas {style} {working} dark={theme.isDark} class="h-11 w-11 flex-none" />
      <div class="min-w-0">
        <p class="text-[17px] font-semibold leading-tight tracking-tight text-ink">Optimisarr</p>
        <p class="label mb-0 mt-0.5 whitespace-nowrap">Linux sidecar</p>
      </div>
    </div>
    <div class="flex items-center gap-2">
      {#if server}
        <a class="btn whitespace-nowrap" href={server}><span class="hidden sm:inline">Open</span> Optimisarr<Icon name="arrow-up-right" class="h-3.5 w-3.5" /></a>
      {/if}
      <button class="btn btn-ghost h-10 w-10 p-0" onclick={() => theme.toggle()} aria-label={theme.isDark ? 'Use light theme' : 'Use dark theme'} title={theme.isDark ? 'Light theme' : 'Dark theme'}>
        <Icon name={theme.isDark ? 'sun' : 'moon'} class="h-4 w-4" />
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
      <!-- The main app's status strip: one state, why, and the few numbers that explain it. -->
      <section class="status-strip card" aria-label="Worker state">
        <div class="status-strip-state">
          <span class="h-2 w-2 flex-none rounded-full {view.tone === 'live' ? 'animate-pulse bg-accent' : view.tone === 'ok' ? 'bg-ok' : view.tone === 'warn' ? 'bg-warn' : view.tone === 'bad' ? 'bg-bad' : 'bg-ink-5'}" aria-hidden="true"></span>
          <span class="label mb-0">State</span>
          <span class="font-mono text-sm font-medium {view.tone === 'live' ? 'text-accent' : view.tone === 'ok' ? 'text-ok' : view.tone === 'warn' ? 'text-warn' : view.tone === 'bad' ? 'text-bad' : 'text-ink-2'}">{view.label}</span>
          <span class="status-strip-reason" title={view.detail}>{view.detail}</span>
        </div>
        <div class="status-strip-fact"><span class="label mb-0">Slots</span><span class="fact-value">{status.jobs.length} / {status.concurrency}</span></div>
        <div class="status-strip-fact"><span class="label mb-0">CPU</span><span class="fact-value">{cpu != null ? `${cpu}%` : '—'}</span></div>
        <div class="status-strip-fact" title={status.metrics?.gpuEngine ? `${status.metrics.gpuEngine} engine` : 'No GPU reading'}><span class="label mb-0">GPU</span><span class="fact-value">{gpu != null ? `${gpu}%` : '—'}</span></div>
        <div class="status-strip-fact status-strip-optional"><span class="label mb-0">Free in {status.storage.kind.toLowerCase()}</span><span class="fact-value">{formatSize(status.storage.freeBytes)}</span></div>
      </section>

      {#if status.update}
        <p class="callout tone-info flex flex-wrap items-center gap-x-3 gap-y-1">
          <Icon name="download" class="h-4 w-4" />
          <span>Optimisarr {status.update.version} is available. This worker runs {status.version}; pull the new sidecar image to stay in step with the server.</span>
          <a class="font-semibold underline" href={status.update.releaseUrl} rel="noreferrer">Release notes</a>
        </p>
      {/if}

      {#if status.pairing.required}
        <div class="pair-grid">
          <section class="card p-5 sm:p-6" aria-labelledby="pair-title">
            <p class="label">Pairing</p>
            <h1 id="pair-title" class="text-2xl font-semibold tracking-tight text-ink">Pair {status.name}</h1>
            <p class="mt-1 text-sm text-ink-2">Connect this worker to your Optimisarr server so it can take encodes the server hands out.</p>
            <ol class="pair-steps">
              <li><span class="step-number">1</span><span>In Optimisarr, open <strong>Settings → Workers</strong> and choose <strong>Pair a sidecar</strong>. Remote workers must be turned on there.</span></li>
              <li><span class="step-number">2</span><span>Enter the server address and the eight-digit code it shows. A code lasts five minutes.</span></li>
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
            <ul class="promise-list">
              <li><Icon name="shield-check" class="h-4 w-4 flex-none text-ok" /><span>This worker only ever receives a copy. It never replaces, moves or deletes a file.</span></li>
              <li><Icon name="check" class="h-4 w-4 flex-none text-ok" /><span>Every encode it returns is verified on the server before anything changes.</span></li>
              <li><Icon name="server" class="h-4 w-4 flex-none text-accent" /><span>Pause, drain and revoke it from Settings → Workers.</span></li>
            </ul>
            <p class="label mt-5">What this machine proved</p>
            {#if status.capabilities}
              <div class="flex flex-wrap gap-1.5">{#each status.capabilities.videoEncoders as encoder}<span class="badge tone-neutral font-mono">{encoder}</span>{:else}<span class="text-sm text-ink-3">No working video encoder was found.</span>{/each}</div>
            {:else}
              <p class="flex items-center gap-2 text-sm text-ink-3"><span class="h-1.5 w-1.5 animate-pulse rounded-full bg-accent" aria-hidden="true"></span>Running real test encodes…</p>
            {/if}
          </section>
        </div>
      {:else}
        <section class="card" aria-labelledby="work-title">
          <div class="card-head">
            <h1 id="work-title" class="label mb-0">In flight</h1>
            <span class="ml-auto font-mono text-xs text-ink-3">{status.jobs.length} of {status.concurrency} {status.concurrency === 1 ? 'slot' : 'slots'}{host ? ` · for ${host}` : ''}</span>
          </div>
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
                  <span class="text-[11px] font-medium text-accent">{STAGES[step].active}</span>
                  <span class="badge tone-neutral font-mono">{job.encoder}</span>
                  <span class="badge font-mono {job.hardwareDecoder ? 'tone-ok' : 'tone-muted'}">{job.hardwareDecoder ? `GPU decode · ${job.hardwareDecoder}` : 'Software decode'}</span>
                  {#if job.startedAt}<span class="ml-auto font-mono text-[11px] text-ink-3" title="Time on this worker">{sinceLabel(job.startedAt, now)}</span>{/if}
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
                <ol class="job-stages" aria-label="Stages on this worker">
                  {#each STAGES as stage, index}
                    <li class:stage-done={index < step} class:stage-current={index === step} aria-current={index === step ? 'step' : undefined}>{#if index < step}<span class="mr-1" aria-hidden="true">✓</span>{/if}{stage.label}</li>
                  {/each}
                </ol>
                <div class="facts">
                  {#if job.sourceMedia?.videoCodec}<span>{job.sourceMedia.videoCodec.toUpperCase()}</span>{/if}
                  {#if job.sourceMedia?.width && job.sourceMedia.height}<span>{job.sourceMedia.width} × {job.sourceMedia.height}</span>{/if}
                  {#if duration}<span>{timecode(duration)}</span>{/if}
                  {#if job.sourceBytes}<span>{formatSize(job.sourceBytes)}</span>{/if}
                  {#if job.sourceMedia?.pixelFormat}<span>{job.sourceMedia.pixelFormat}</span>{/if}
                  {#if job.sourceMedia?.audioCodecs}<span>Audio {job.sourceMedia.audioCodecs}</span>{/if}
                  {#if job.outputExtension}<span>→ {job.outputExtension.toUpperCase()}</span>{/if}
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
        <div class="card-head"><h2 id="flow-title" class="label mb-0">How this worker helps</h2></div>
        <ol class="flow">
          <li class="flow-server"><Icon name="server" class="h-4 w-4" /><span><strong>Server</strong> picks a job and keeps the original</span></li>
          {#each STAGES as stage, index}
            {@const active = status.jobs.some((job) => stageIndex(job.stage) === index)}
            <li class:flow-active={active}><Icon name={index === 0 ? 'download' : index === 1 ? 'film' : index === 2 ? 'search' : 'upload'} class="h-4 w-4" /><span><strong>{stage.label}</strong> {index === 0 ? 'a copy here' : index === 1 ? 'on this hardware' : index === 2 ? 'requested quality and verification checks' : 'the candidate'}</span></li>
          {/each}
          <li class="flow-server"><Icon name="shield-check" class="h-4 w-4" /><span><strong>Server</strong> validates results, then replaces</span></li>
        </ol>
      </section>

      <div class="detail-grid">
        <section class="card p-5" aria-labelledby="storage-title">
          <p class="label">Working storage</p>
          <h2 id="storage-title" class="text-base font-semibold text-ink">{status.storage.kind === 'RAM' ? 'In memory' : 'On disk'}</h2>
          <p class="mt-3 font-mono text-2xl font-semibold tabular-nums tracking-tight text-ink">{formatSize(status.storage.freeBytes)} <span class="text-sm font-normal text-ink-3">free</span></p>
          <div class="progress-track mt-3" role="meter" aria-label="Working storage used" aria-valuemin="0" aria-valuemax="100" aria-valuenow={storagePercent}><div class="progress-fill" style:width={`${storagePercent}%`}></div></div>
          <p class="mt-2 font-mono text-xs text-ink-3">{formatSize(usedStorage)} used of {formatSize(status.storage.totalBytes)} · {status.scratchPath}</p>
          <p class="mt-3 text-xs leading-relaxed text-ink-3">{status.storage.kind === 'RAM' ? 'Source copies and candidates live in RAM while a job runs, and vanish with it.' : 'Source copies and candidates are written here while a job runs, then removed.'}</p>
        </section>

        <section class="card p-5" aria-labelledby="hardware-title">
          <p class="label">Media hardware</p>
          <h2 id="hardware-title" class="text-base font-semibold text-ink">Proved on this machine</h2>
          {#if status.capabilities}
            <p class="label mt-3">Video encoders</p>
            <div class="flex flex-wrap gap-1.5">{#each status.capabilities.videoEncoders as encoder}<span class="badge tone-neutral font-mono">{encoder}</span>{:else}<span class="text-sm text-ink-3">None available</span>{/each}</div>
            <p class="eyebrow mt-4 mb-2">Audio encoders</p>
            <div class="flex flex-wrap gap-1.5">{#each status.capabilities.audioEncoders ?? [] as encoder}<span class="badge tone-neutral font-mono">{encoder}</span>{:else}<span class="text-sm text-ink-3">None reported</span>{/each}</div>
            <p class="label mt-3">Hardware decode</p>
            <div class="flex flex-wrap gap-1.5">{#each status.capabilities.hardwareDecoders as decoder}<span class="badge tone-ok font-mono">{decoder}</span>{:else}<span class="text-sm text-ink-3">Software only</span>{/each}</div>
            <p class="label mt-3">Quality measurement</p>
            <span class="badge tone-neutral font-mono">{status.capabilities.vmaf === 'Cuda' ? 'VMAF · CUDA' : status.capabilities.vmaf === 'Cpu' ? 'VMAF · CPU' : 'Unavailable'}</span>
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
                  <Icon name={item.delivered ? 'check' : 'warning'} class="mt-0.5 h-4 w-4 flex-none {item.delivered ? 'text-ok' : 'text-warn'}" />
                  <span class="min-w-0 flex-1"><span class="line-clamp-2 break-words text-ink">{item.title}</span><span class="text-xs text-ink-3">{item.delivered ? 'Returned to the server' : 'Stopped here; the server has the details'}</span></span>
                  <span class="font-mono text-[11px] text-ink-3">{sinceLabel(item.finishedAt, now)} ago</span>
                </li>
              {/each}
            </ul>
          {:else}
            <p class="mt-3 text-sm text-ink-3">Nothing finished yet. Results are judged on the server, which keeps the full history.</p>
          {/if}
        </section>
      </div>

      <footer class="sidecar-footer">
        <a class="btn btn-ghost min-h-11" href="/api/sidecar/diagnostics" download="optimisarr-sidecar-diagnostics.json">Export local diagnostics</a>
        <span class="font-mono">{status.name} · v{status.version}</span>
        <span>This page is read-only apart from pairing. Pause, drain and scheduling live on the server.</span>
      </footer>
    {/if}
  </main>
</div>

<style>
  :global(body) { margin: 0; min-width: 320px; }
  .sidecar-shell { max-width: 1200px; margin: 0 auto; padding: 0 1.5rem 2rem; }
  .sidecar-header { display: flex; align-items: center; justify-content: space-between; gap: 1rem; padding: 1.25rem 0 1rem; }
  .status-strip { display: flex; flex-wrap: wrap; align-items: center; gap: .25rem 1.5rem; padding: .6rem .9rem .6rem 1rem; }
  .status-strip-state { display: flex; min-width: 0; flex: 1 1 18rem; align-items: center; gap: .6rem; }
  .status-strip-reason { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: .8125rem; color: var(--ink-2); }
  .status-strip-fact { display: flex; align-items: center; gap: .6rem; }
  .fact-value { font-size: .875rem; font-weight: 600; font-variant-numeric: tabular-nums; color: var(--ink); }
  .card-head { display: flex; flex-wrap: wrap; align-items: center; gap: .75rem; padding: .7rem 1rem; box-shadow: inset 0 -1px 0 var(--divide-soft); }
  .pair-grid { display: grid; grid-template-columns: minmax(0, 1.4fr) minmax(0, 1fr); gap: 1rem; }
  .pair-steps { display: flex; flex-direction: column; gap: .6rem; margin-top: 1.1rem; font-size: .875rem; color: var(--ink-2); }
  .pair-steps li { display: flex; gap: .7rem; align-items: flex-start; }
  .step-number { flex: none; display: grid; place-items: center; width: 1.4rem; height: 1.4rem; border-radius: 999px; background: var(--sunken); box-shadow: var(--inset-1); font: 600 .72rem ui-monospace, monospace; color: var(--accent); }
  .code-input { font-size: 1.35rem; letter-spacing: .18em; padding-block: .6rem; max-width: 16rem; }
  .promise-list { display: flex; flex-direction: column; gap: .7rem; margin-top: .9rem; font-size: .875rem; color: var(--ink-2); }
  .promise-list li { display: flex; gap: .6rem; align-items: flex-start; }
  .promise-list :global(svg) { margin-top: .15rem; }
  .job { display: grid; grid-template-columns: 7rem minmax(0, 1fr) minmax(0, 17rem); gap: 1.25rem 1.5rem; padding: 1.25rem 1.25rem 1.4rem; }
  .job + .job { box-shadow: inset 0 1px 0 var(--divide-soft); }
  .poster { width: 7rem; aspect-ratio: 2 / 3; border-radius: .5rem; overflow: hidden; display: grid; place-items: center; background: var(--raised); box-shadow: var(--lift-2); align-self: start; }
  .poster img { width: 100%; height: 100%; object-fit: cover; }
  .job-title { margin: .5rem 0 .9rem; font-size: 1.3rem; line-height: 1.3; letter-spacing: -.025em; font-weight: 600; color: var(--ink); overflow-wrap: anywhere; }
  .readout { display: flex; flex-wrap: wrap; align-items: baseline; gap: .4rem 1rem; margin: .6rem 0 .25rem; font-size: .75rem; color: var(--ink-3); font-variant-numeric: tabular-nums; }
  .readout strong { font-size: 1rem; color: var(--ink); font-weight: 600; }
  .job-stages { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: .5rem; margin-top: 1rem; }
  .job-stages li { padding-top: .55rem; border-top: 2px solid var(--edge); font-size: .6875rem; color: var(--ink-3); }
  .job-stages .stage-done { color: var(--ok); border-color: var(--ok); }
  .job-stages .stage-current { color: var(--accent); border-color: var(--accent); }
  .facts { display: flex; flex-wrap: wrap; gap: .35rem 1rem; margin-top: 1rem; font: .72rem var(--font-mono); color: var(--ink-3); }
  .frame { position: relative; margin: 0; aspect-ratio: 16 / 9; border-radius: .6rem; overflow: hidden; display: grid; place-items: center; background: var(--sunken); box-shadow: var(--inset-1); align-self: start; }
  .spectrum-caption { grid-column: 2 / -1; }
  .audio-spectrum { aspect-ratio: 10 / 3; }
  .frame img { width: 100%; height: 100%; object-fit: contain; background: #000; }
  .frame figcaption { position: absolute; left: .5rem; top: .5rem; display: flex; align-items: center; gap: .35rem; padding: .15rem .45rem; border-radius: 999px; background: rgba(0, 0, 0, .6); color: #fff; font: 600 .625rem ui-monospace, monospace; letter-spacing: .08em; text-transform: uppercase; }
  .flow { display: grid; grid-template-columns: repeat(6, minmax(0, 1fr)); gap: .5rem; padding: 1rem; }
  .flow li { display: flex; flex-direction: column; gap: .45rem; padding: .7rem .75rem; border-radius: .6rem; font-size: .75rem; line-height: 1.35; color: var(--ink-3); background: var(--sunken); box-shadow: var(--inset-1); }
  .flow li strong { display: block; font-size: .8125rem; color: var(--ink-2); }
  .flow li :global(svg) { color: var(--ink-4); }
  .flow .flow-server { background: none; box-shadow: inset 0 0 0 1px var(--divide-soft); }
  .flow .flow-server :global(svg) { color: var(--ink-3); }
  .flow .flow-active { background: var(--raised); box-shadow: var(--lift-1), inset 0 1px 0 var(--edge); }
  .flow .flow-active strong, .flow .flow-active :global(svg) { color: var(--accent); }
  .detail-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 1rem; }
  .sidecar-footer { display: flex; flex-wrap: wrap; justify-content: space-between; gap: .5rem 1.5rem; padding: .5rem .25rem 0; font-size: .75rem; color: var(--ink-3); }
  @media (max-width: 1023px) {
    .job { grid-template-columns: 6rem minmax(0, 1fr); }
    .poster { width: 6rem; }
    .spectrum-caption { grid-column: 1 / -1; }
    .frame { grid-column: 1 / -1; max-width: 26rem; }
    .flow { grid-template-columns: repeat(3, minmax(0, 1fr)); }
    .detail-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  }
  @media (max-width: 767px) {
    .pair-grid { grid-template-columns: minmax(0, 1fr); }
    .detail-grid { grid-template-columns: minmax(0, 1fr); }
  }
  @media (max-width: 639px) {
    .sidecar-shell { padding: 0 1rem 1.5rem; }
    .status-strip-optional { display: none; }
    .status-strip-reason { white-space: normal; flex-basis: 100%; }
    .frame-empty { display: none; }
    .job { grid-template-columns: 3.75rem minmax(0, 1fr); padding: 1rem; gap: 1rem; }
    .poster { width: 3.75rem; }
    .job-title { font-size: 1.1rem; }
    .flow { grid-template-columns: repeat(2, minmax(0, 1fr)); }
    .job-stages { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  }
</style>
