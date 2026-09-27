<script lang="ts">
  import { onMount } from 'svelte'
  import { formatSize as bytes } from './lib/format'

  type Snapshot = {
    name: string; state: string; serverAddress: string | null; scratchPath: string;
    storage: { kind: string; freeBytes: number; totalBytes: number }; concurrency: number;
    capabilities: { videoEncoders: string[]; hardwareDecoders: string[]; vmaf: string } | null;
    jobs: { jobId: number; title: string; encoder: string; stage: string; encodedSeconds: number | null;
      sourceBytes: number | null; outputExtension: string | null; hardwareDecoder: string | null; previewRevision: number;
      sourceMedia: { videoCodec: string | null; width: number | null; height: number | null; durationSeconds: number | null;
        audioCodecs: string | null; pixelFormat: string | null } | null }[];
    metrics: { cpuPercent: number | null; gpuPercent: number | null; gpuEngine: string | null; sampledAt: string } | null;
    lastOutcome: string | null; version: string;
  }
  let status = $state<Snapshot | null>(null)
  let error = $state(false)
  let loading = $state(true)
  let dark = $state(false)
  let request: AbortController | null = null
  let timer: ReturnType<typeof setTimeout>
  let disposed = false
  const percent = (value: number | null | undefined) => value != null && Number.isFinite(value) ? `${Math.round(Math.max(0, Math.min(100, value)))}%` : '—'
  const duration = (seconds: number | null | undefined) => {
    if (seconds == null || !Number.isFinite(seconds) || seconds < 0) return 'Unknown duration'
    const total = Math.floor(seconds)
    return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`
  }
  const stages: Record<string, string> = { FetchingSource: 'Receiving source', Encoding: 'Encoding', Measuring: 'Quality & verification', Delivering: 'Returning candidate' }
  const states: Record<string, string> = {
    Draining: 'Finishing current work', Starting: 'Starting', Unpaired: 'Pairing required', Connected: 'Connected', Working: 'Working',
    Unreachable: 'Server unreachable', Faulted: 'Needs attention', Stopped: 'Worker stopped',
  }
  const server = $derived.by(() => {
    try {
      const url = new URL(status?.serverAddress ?? '')
      return ['https:', 'http:'].includes(url.protocol) && !url.username && !url.password ? url.href : null
    } catch { return null }
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
      if (!disposed) { status = next; error = false }
    } catch { if (!disposed) error = true }
    finally {
      clearTimeout(timeout)
      loading = false
      if (!disposed) timer = setTimeout(refresh, 2000)
    }
  }
  function toggleTheme() {
    dark = !dark
    document.documentElement.classList.toggle('dark', dark)
    localStorage.setItem('sidecar-theme', dark ? 'dark' : 'light')
  }
  onMount(() => {
    dark = (localStorage.getItem('sidecar-theme') ?? (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')) === 'dark'
    document.documentElement.classList.toggle('dark', dark)
    void refresh()
    return () => { disposed = true; clearTimeout(timer); request?.abort() }
  })
</script>

<svelte:head><title>Optimisarr · Linux sidecar</title></svelte:head>

<div class="shell">
  <header>
    <div class="identity"><img src="/favicon-192.png" alt="" width="44" height="44" /><div><strong>Optimisarr</strong><p class="eyebrow">Linux sidecar</p></div></div>
    <button class="theme" onclick={toggleTheme} aria-label={dark ? 'Use light theme' : 'Use dark theme'}>{dark ? 'Light theme' : 'Dark theme'}</button>
  </header>
  <main>
    <div class="heading"><div><p class="eyebrow">Worker overview</p><h1>{status?.name ?? 'Connecting to sidecar'}</h1><p class="muted">Encoding and verification for your Optimisarr server.</p></div>
      {#if server}<a class="action" href={server}>Open main server <span aria-hidden="true">↗</span></a>{/if}
    </div>
    {#if error}
      <section class="card problem" role="alert"><h2>Cannot reach this sidecar</h2><p>Status may be out of date. Check the container in Dockhand, or try again.</p><button class="action" onclick={refresh}>Retry</button></section>
    {:else if loading}
      <section class="card" role="status">Loading worker status…</section>
    {:else if status}
      <section class="load-grid" aria-label="Live utilization">
        <div class="card load"><p class="eyebrow">Host CPU</p><p class="load-value">{percent(status.metrics?.cpuPercent)}</p><p class="muted">{status.metrics?.cpuPercent != null ? 'Across all host cores' : 'Waiting for a CPU sample'}</p></div>
        <div class="card load"><p class="eyebrow">GPU use</p><p class="load-value">{percent(status.metrics?.gpuPercent)}</p><p class="muted">{status.metrics?.gpuPercent != null ? `${status.metrics.gpuEngine ?? 'GPU'} engine` : status.jobs.length ? 'Waiting for a readable GPU sample' : 'No active GPU sample'}</p></div>
        <div class="card load"><p class="eyebrow">Working space</p><p class="load-value">{bytes(status.storage.freeBytes)}</p><p class="muted">Free in {status.storage.kind.toLowerCase()} · {status.concurrency} job {status.concurrency === 1 ? 'slot' : 'slots'}</p></div>
      </section>
      <section class="card activity">
        <div class="section-title"><h2>Current work</h2><span class:healthy={['Connected', 'Working'].includes(status.state)} class="badge">{states[status.state] ?? status.state}</span></div>
        {#each status.jobs as job (job.jobId)}
          <article class="job">
            <div class="preview">
              {#if job.previewRevision > 0}<img src={`/api/sidecar/jobs/${job.jobId}/preview?v=${job.previewRevision}`} alt={`Preview of ${job.title}`} />{:else}<span>{job.stage === 'FetchingSource' ? 'Receiving media' : 'Waiting for frame'}</span>{/if}
            </div>
            <div class="job-body"><h3>{job.title}</h3><p class="muted">{stages[job.stage] ?? job.stage} · {job.encoder}</p>
              <div class="media-facts">
                {#if job.sourceMedia?.videoCodec}<span>{job.sourceMedia.videoCodec.toUpperCase()} source</span>{/if}
                {#if job.sourceMedia?.width && job.sourceMedia.height}<span>{job.sourceMedia.width} × {job.sourceMedia.height}</span>{/if}
                {#if job.sourceMedia?.durationSeconds}<span>{duration(job.sourceMedia.durationSeconds)}</span>{/if}
                {#if job.sourceBytes}<span>{bytes(job.sourceBytes)}</span>{/if}
                {#if job.sourceMedia?.pixelFormat}<span>{job.sourceMedia.pixelFormat}</span>{/if}
                {#if job.sourceMedia?.audioCodecs}<span>Audio: {job.sourceMedia.audioCodecs}</span>{/if}
                {#if job.outputExtension}<span>Output: {job.outputExtension.toUpperCase()}</span>{/if}
                <span>{job.hardwareDecoder ? `GPU decode · ${job.hardwareDecoder}` : 'Software decode'}</span>
              </div>
              {#if job.stage === 'Encoding' && job.encodedSeconds != null}
                {#if job.sourceMedia?.durationSeconds && job.sourceMedia.durationSeconds > 0}
                  {@const progress = Math.max(0, Math.min(100, job.encodedSeconds / job.sourceMedia.durationSeconds * 100))}
                  <progress max="100" value={progress} aria-label={`Encoding progress for ${job.title}`}></progress>
                  <p class="muted">{Math.floor(progress)}% encoded <span aria-hidden="true">·</span> {duration(job.encodedSeconds)} / {duration(job.sourceMedia.durationSeconds)}</p>
                {:else}<p class="muted">{Math.floor(job.encodedSeconds)}s encoded</p>{/if}
              {/if}
              <p class="job-id muted">Job {job.jobId}</p>
            </div>
          </article>
        {:else}
          <div class="empty"><h3>{status.state === 'Connected' ? 'Ready for work' : states[status.state] ?? status.state}</h3><p class="muted">{status.state === 'Connected' ? 'No active jobs. Scheduling and pause controls are on the main server.' : status.state === 'Draining' ? 'This worker is taking no new jobs. Resume it on the main server when ready.' : status.state === 'Starting' ? 'Checking the media tools and hardware before accepting jobs.' : 'Check this worker on the main server and review its container logs in Dockhand.'}</p></div>
        {/each}
        {#if status.lastOutcome}<p class="outcome muted">{status.lastOutcome}</p>{/if}
      </section>
      <div class="grid">
        <section class="card"><p class="eyebrow">Working storage</p><h2>{status.storage.kind} working storage</h2><p class="metric">{bytes(status.storage.freeBytes)} <span>free</span></p>
          <meter min="0" max={status.storage.totalBytes || 1} value={status.storage.totalBytes - status.storage.freeBytes} aria-label="Working storage used"></meter>
          <p class="muted">{bytes(status.storage.totalBytes)} total · {status.scratchPath}</p><p class="note">{status.storage.kind === 'RAM' ? 'Source and candidate files stay in RAM while this worker processes a job.' : 'Source and candidate files use disk storage.'} Originals remain on the main server.</p>
        </section>
        <section class="card"><p class="eyebrow">Media hardware</p><h2>Proved capabilities</h2>
          {#if status.capabilities}
            <dl><dt>Video encoders</dt><dd>{status.capabilities.videoEncoders.join(', ') || 'None available'}</dd><dt>Hardware decoders</dt><dd>{status.capabilities.hardwareDecoders.join(', ') || 'Software decoding'}</dd><dt>Quality verification</dt><dd>{status.capabilities.vmaf === 'Cpu' ? 'CPU VMAF' : status.capabilities.vmaf === 'Cuda' ? 'CUDA VMAF' : '—'}</dd></dl>
            <p class="note">GPU decoding is used for compatible jobs when enabled on the main server. RAM working storage is independent of GPU frame memory.</p>
          {:else}<p class="muted">Running real encoding and decoding probes…</p>{/if}
        </section>
      </div>
      <footer><span>{status.concurrency} concurrent {status.concurrency === 1 ? 'job' : 'jobs'} · v{status.version}</span><span>Refreshes every 2 seconds</span></footer>
    {/if}
  </main>
</div>

<style>
  :global(body) { margin: 0; min-width: 320px; background: var(--ground-wash); color: var(--ink); }
  .shell { max-width: 1080px; margin: auto; padding: 0 28px 32px; }
  header, .identity, .heading, .section-title, .job, footer { display: flex; align-items: center; justify-content: space-between; gap: 20px; }
  header { padding: 24px 0; border-bottom: 1px solid var(--divide); }
  .identity { justify-content: flex-start; gap: 12px; }
  .identity strong { font-size: 20px; letter-spacing: -.03em; }
  p { margin: 0; line-height: 1.6; }
  .eyebrow { font-size: 12px; font-weight: 650; color: var(--ink-3); letter-spacing: .08em; text-transform: uppercase; }
  .heading { margin: 36px 0 28px; }
  h1 { font-size: clamp(28px, 5vw, 38px); font-weight: 650; letter-spacing: -.04em; margin: 4px 0 8px; }
  h2 { font-size: 18px; font-weight: 650; margin: 4px 0 14px; }
  h3 { font-size: 17px; font-weight: 600; margin: 0 0 6px; overflow-wrap: anywhere; }
  .muted, footer { color: var(--ink-3); font-size: 14px; }
  .card { background: linear-gradient(180deg, var(--panel-hi), var(--panel)); box-shadow: var(--lift-2), inset 0 1px var(--edge); border-radius: 18px; padding: 24px; }
  .load-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 22px; margin-bottom: 22px; }
  .load { padding: 20px 24px; }
  .load-value { font-size: clamp(22px, 3vw, 30px); letter-spacing: -.04em; font-weight: 600; margin: 8px 0 4px; }
  .preview { aspect-ratio: 16 / 9; width: 220px; flex-shrink: 0; background: var(--sunken); border-radius: 10px; display: grid; place-items: center; overflow: hidden; color: var(--ink-3); font-size: 13px; }
  .preview img { width: 100%; height: 100%; object-fit: contain; }
  .job-body { flex: 1; min-width: 0; }
  .media-facts { display: flex; flex-wrap: wrap; gap: 6px 16px; margin: 16px 0; color: var(--ink-2); font-size: 13px; }
  .job-id { margin-top: 12px; font-size: 12px; }
  progress { width: 100%; height: 6px; border: 0; border-radius: 8px; background: var(--sunken); accent-color: var(--accent); margin: 4px 0 10px; overflow: hidden; }
  progress::-webkit-progress-bar { background: var(--sunken); }
  progress::-webkit-progress-value { background: var(--accent); }
  progress::-moz-progress-bar { background: var(--accent); }
  .activity { margin-bottom: 22px; }
  .section-title h2 { margin: 0; }
  .badge { border-radius: 20px; background: var(--warn-soft); color: var(--warn-strong); padding: 5px 12px; font-size: 13px; font-weight: 600; }
  .badge.healthy { background: var(--ok-soft); color: var(--ok-strong); }
  .empty { padding: 30px 0 16px; }
  .job { padding: 24px 0 4px; align-items: flex-start; }
  .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 22px; }
  .metric { font-size: 30px; font-weight: 600; letter-spacing: -.04em; margin: 20px 0 8px; }
  .metric span { font-size: 15px; font-weight: 400; color: var(--ink-3); letter-spacing: normal; }
  meter { display: block; width: 100%; height: 12px; margin: 14px 0; accent-color: var(--accent); }
  .note { font-size: 13px; color: var(--ink-3); margin-top: 20px; }
  dl { margin: 20px 0 0; font-size: 14px; }
  dt { color: var(--ink-3); margin-top: 14px; }
  dd { margin: 3px 0 0; overflow-wrap: anywhere; }
  .action, .theme { display: inline-flex; align-items: center; justify-content: center; gap: 12px; min-height: 44px; border-radius: 10px; padding: 10px 16px; cursor: pointer; font-size: 14px; font-weight: 600; transition: background .15s; }
  .action { background: var(--accent-soft); color: var(--accent-strong); text-decoration: none; white-space: nowrap; }
  .theme { color: var(--ink-2); background: var(--raised); }
  .action:hover, .theme:hover { background: var(--lit); }
  .action:focus-visible, .theme:focus-visible { outline: 3px solid var(--focus); outline-offset: 3px; }
  .problem p { margin-bottom: 16px; }
  .problem h2 { color: var(--bad); }
  .outcome { margin-top: 20px; }
  footer { padding: 24px 2px 0; font-size: 12px; flex-wrap: wrap; }
  @media (max-width: 680px) { .shell { padding: 0 16px 24px; } .heading { align-items: flex-start; flex-direction: column; margin-top: 26px; } .grid { grid-template-columns: 1fr; } .load-grid { gap: 8px; } .load.card { padding: 16px 10px; } .load .eyebrow { font-size: 12px; } .load .muted { font-size: 12px; } .load-value { font-size: 20px; white-space: nowrap; } .preview { width: 100%; } .card { padding: 20px; } .job { flex-direction: column; gap: 8px; } .theme { padding: 10px; } }
  @media (prefers-reduced-motion: reduce) { .action, .theme { transition: none; } }
</style>
