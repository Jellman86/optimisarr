<script lang="ts">
  // Remote transcoding sidecars: pair one with a PIN, see what is paired, revoke it.
  // Loads its own data so it can be dropped into the Settings "Workers" tab without the
  // host wiring anything up, the same way ToolsPanel does.
  import { api, type Worker, type WorkerJob, type WorkerPairingCode } from '../api'
  import { formatSize } from '../format'
  import Banner from './Banner.svelte'
  import ConfigSection from './ConfigSection.svelte'
  import { i18n, t } from '../i18n/i18n.svelte'

  let workers = $state<Worker[]>([])
  let pairing = $state<WorkerPairingCode | null>(null)
  let error = $state<string | null>(null)
  let loading = $state(true)
  let busy = $state(false)
  // Drives the countdown. Held as state rather than read from Date inside the template so
  // the displayed number actually changes.
  let nowMs = $state(Date.now())

  // Seconds left on the displayed PIN. Zero means it has lapsed and is no longer usable,
  // which is exactly when the server would start refusing it too.
  let secondsLeft = $derived(
    pairing ? Math.max(0, Math.ceil((new Date(pairing.expiresUtc).getTime() - nowMs) / 1000)) : 0,
  )

  // The address the operator types into the sidecar. Read from the browser because only it
  // knows how this instance was actually reached — a reverse proxy hostname is what the
  // sidecar needs, not the container's own idea of its address.
  let serverAddress = $derived(typeof location === 'undefined' ? '' : location.origin)

  $effect(() => {
    void load()
  })

  // One ticker for the whole panel: the PIN countdown every second while a code is on screen,
  // and otherwise a slower beat that keeps "last seen" honest and refreshes the cards, since
  // a worker's stage and progress arrive only through its renewals.
  $effect(() => {
    const handle = setInterval(() => {
      nowMs = Date.now()
    }, pairing ? 1000 : 15000)
    return () => clearInterval(handle)
  })

  // Both the list and the code live on the server, and the one event that ends a pairing —
  // the sidecar redeeming the code — happens on another machine. So while a code is on screen
  // the page asks often, and it keeps asking even when the list is empty: a first worker can
  // appear at any moment, and the page is the only thing that will tell the operator.
  // A code still within its time. Derived so the effect below only re-arms when this flips,
  // not on every tick of the countdown.
  let codeShowing = $derived(pairing !== null && secondsLeft > 0)

  $effect(() => {
    if (loading) return
    const handle = setInterval(() => void refresh(), codeShowing ? 2000 : 15000)
    return () => clearInterval(handle)
  })

  async function refresh() {
    try {
      const askCode = codeShowing
      const [list, code] = await Promise.all([
        api.workers(),
        askCode ? api.activeWorkerPairingCode() : Promise.resolve(null),
      ])
      workers = list
      // A code the server no longer has while it should still be live was redeemed or cancelled
      // from elsewhere, so the panel stops showing it. A code still live brings its attempts
      // left. A lapsed code is left alone so the expiry notice stays on screen.
      if (askCode) pairing = code
    } catch {
      // A missed refresh is not worth an error banner; the next one will try again.
    }
  }

  async function load() {
    loading = true
    error = null
    try {
      workers = await api.workers()
      // A code may already be live from another tab or before a reload, so resume it rather
      // than silently showing none. Null simply means none is on screen.
      pairing = await api.activeWorkerPairingCode()
      nowMs = Date.now()
    } catch (e) {
      error = e instanceof Error ? e.message : String(e)
    } finally {
      loading = false
    }
  }

  async function issue() {
    busy = true
    error = null
    try {
      pairing = await api.issueWorkerPairingCode()
      nowMs = Date.now()
    } catch (e) {
      error = e instanceof Error ? e.message : String(e)
    } finally {
      busy = false
    }
  }

  async function cancelCode() {
    busy = true
    try {
      await api.cancelWorkerPairingCode()
      pairing = null
    } catch (e) {
      error = e instanceof Error ? e.message : String(e)
    } finally {
      busy = false
    }
  }

  async function forget(worker: Worker) {
    if (!confirm(t(i18n.m.workers.forget_confirm, { name: worker.name }))) return
    busy = true
    error = null
    try {
      await api.forgetWorker(worker.id)
      workers = await api.workers()
    } catch (e) {
      error = e instanceof Error ? e.message : String(e)
    } finally {
      busy = false
    }
  }

  async function revoke(worker: Worker) {
    if (!confirm(t(i18n.m.workers.revoke_confirm, { name: worker.name }))) return
    busy = true
    error = null
    try {
      await api.revokeWorker(worker.id)
      // A revoked worker is refetched rather than patched locally, so the row reflects what
      // the server actually recorded.
      workers = await api.workers()
    } catch (e) {
      error = e instanceof Error ? e.message : String(e)
    } finally {
      busy = false
    }
  }

  // Drain and resume patch the row from the server's answer rather than flipping a flag
  // locally, so what is shown is what the server actually recorded.
  async function drain(worker: Worker) {
    busy = true
    error = null
    try {
      replace(await api.drainWorker(worker.id))
    } catch (e) {
      error = e instanceof Error ? e.message : String(e)
    } finally {
      busy = false
    }
  }

  async function resume(worker: Worker) {
    busy = true
    error = null
    try {
      replace(await api.resumeWorker(worker.id))
    } catch (e) {
      error = e instanceof Error ? e.message : String(e)
    } finally {
      busy = false
    }
  }

  function replace(updated: Worker) {
    workers = workers.map((w) => (w.id === updated.id ? updated : w))
  }

  const amber = 'tone-warn'
  const grey = 'bg-raised text-ink-2'
  const green = 'bg-ok-soft text-ok-strong'

  function status(worker: Worker): { label: string; classes: string } {
    if (worker.revokedAt) return { label: i18n.m.workers.status_revoked, classes: grey }
    // Reachability first: a worker that has stopped checking in cannot take work whatever its
    // configured concurrency says, so showing "drained" there would be misleading.
    if (!worker.online) return { label: i18n.m.workers.status_offline, classes: grey }
    // An operator's drain is amber because it needs a person to end it. Draining while work is
    // still held, drained once it has all been delivered.
    if (worker.drainRequestedAt) {
      return {
        label: worker.heldLeases > 0 ? i18n.m.workers.status_draining : i18n.m.workers.status_drained,
        classes: amber,
      }
    }
    if (worker.maxConcurrency <= 0) return { label: i18n.m.workers.status_drained, classes: amber }
    return { label: i18n.m.workers.status_online, classes: green }
  }

  function paired(worker: Worker): string {
    return new Date(worker.pairedAt).toLocaleDateString()
  }

  function ago(iso: string | null): string {
    if (!iso) return i18n.m.workers.never_seen
    const seconds = Math.max(0, Math.round((nowMs - new Date(iso).getTime()) / 1000))
    if (seconds < 60) return t(i18n.m.workers.ago_seconds, { n: seconds })
    if (seconds < 3600) return t(i18n.m.workers.ago_minutes, { n: Math.round(seconds / 60) })
    if (seconds < 86400) return t(i18n.m.workers.ago_hours, { n: Math.round(seconds / 3600) })
    return t(i18n.m.workers.ago_days, { n: Math.round(seconds / 86400) })
  }

  function stageLabel(job: WorkerJob): string {
    switch (job.stage) {
      case 'FetchingSource': return i18n.m.workers.stage_fetchingsource
      case 'Encoding': return i18n.m.workers.stage_encoding
      case 'Delivering': return i18n.m.workers.stage_delivering
      case 'Measuring': return i18n.m.workers.stage_measuring
      default: return i18n.m.workers.stage_claimed
    }
  }

  function fileName(path: string | null, jobId: number): string {
    if (!path) return `#${jobId}`
    const slash = path.lastIndexOf('/')
    return slash >= 0 ? path.slice(slash + 1) : path
  }
</script>

<div class="space-y-5">
  {#if error}
    <Banner kind="error">{error}</Banner>
  {/if}

  <ConfigSection
    step={1}
    id="workers-pairing"
    title={i18n.m.workers.title}
    description={i18n.m.workers.subtitle}
  >
    <div class="space-y-4 px-4 py-4 sm:px-6">
      <!-- Stated plainly so nobody pairs a machine expecting a finished feature. -->
      <Banner kind="info">{i18n.m.workers.preview_note}</Banner>

      {#if pairing && secondsLeft > 0}
        <div class="rounded-xl border border-accent bg-accent-soft p-4">
          <h3 class="text-sm font-semibold text-ink">
            {i18n.m.workers.pairing_title}
          </h3>
          <p class="mt-1 text-sm text-ink-2">{i18n.m.workers.pairing_hint}</p>

          <dl class="mt-3 space-y-3">
            <div>
              <dt class="text-xs font-medium uppercase tracking-wide text-ink-3">
                {i18n.m.workers.server_address}
              </dt>
              <dd class="mt-1 break-all font-mono text-sm text-ink">
                {serverAddress}
              </dd>
            </div>
            <div>
              <dt class="text-xs font-medium uppercase tracking-wide text-ink-3">
                {i18n.m.workers.pairing_code}
              </dt>
              <!-- Grouped for reading aloud; the server ignores the spacing on the way back. -->
              <dd class="mt-1 font-mono text-3xl font-bold tracking-[0.2em] text-ink">
                {pairing.code.slice(0, 4)} {pairing.code.slice(4)}
              </dd>
            </div>
          </dl>

          <div class="mt-3 flex flex-wrap items-center gap-3 text-xs text-ink-2">
            <span>{t(i18n.m.workers.expires_in, { seconds: secondsLeft })}</span>
            <span>{t(i18n.m.workers.attempts_left, { count: pairing.attemptsRemaining })}</span>
            <button class="btn btn-ghost ml-auto" disabled={busy} onclick={cancelCode}>
              {i18n.m.workers.cancel_code}
            </button>
          </div>
        </div>
      {:else}
        {#if pairing}
          <Banner kind="error">{i18n.m.workers.code_expired}</Banner>
        {/if}
        <button class="btn btn-primary" disabled={busy} onclick={issue}>
          {i18n.m.workers.pair}
        </button>
      {/if}
    </div>
  </ConfigSection>

  <section>
    {#if loading}
      <p class="px-1 py-6 text-sm text-ink-3">{i18n.m.common.loading_short}</p>
    {:else if workers.length === 0}
      <div class="card px-4 py-6">
        <p class="text-sm font-medium text-ink-2">{i18n.m.workers.empty}</p>
        <p class="mt-1 text-sm text-ink-3">{i18n.m.workers.empty_hint}</p>
      </div>
    {:else}
      <div class="grid gap-4 lg:grid-cols-2" data-testid="worker-cards">
        {#each workers as worker (worker.id)}
          <article class="worker-card card flex flex-col gap-3 p-4" data-testid="worker-card">
            <div class="flex items-start justify-between gap-3">
              <div class="min-w-0">
                <h3 class="truncate text-base font-semibold text-ink">{worker.name}</h3>
                <p class="text-xs text-ink-3">
                  {t(i18n.m.workers.platform_line, { os: worker.operatingSystem, arch: worker.architecture, version: worker.protocolVersion })}
                  <!-- The build the machine is actually running, which the protocol version above
                       does not tell you. Shown even when absent: a worker that reports no version
                       is running something older than this, and that is worth seeing. -->
                  · {worker.sidecarVersion
                    ? t(i18n.m.workers.sidecar_version, { version: worker.sidecarVersion })
                    : i18n.m.workers.sidecar_version_unknown}
                  · {t(i18n.m.workers.paired_on, { date: paired(worker) })}
                </p>
              </div>
              <span class="badge flex-shrink-0 {status(worker).classes}">{status(worker).label}</span>
            </div>

            <!-- What it proved, not what its platform implies: the same list the claim route matches on. -->
            <div class="flex flex-wrap gap-1.5 text-xs">
              {#each worker.videoEncoders as encoder (encoder)}
                <span class="rounded-md border border-line px-2 py-0.5 font-mono text-ink-2">{encoder}</span>
              {/each}
              <!-- Audio matters as much as video: a library set to Opus or MP3 is only offered to
                   a worker whose FFmpeg carries that encoder. -->
              {#each worker.audioEncoders ?? [] as encoder (encoder)}
                <span class="rounded-md border border-line px-2 py-0.5 font-mono text-ink-3">{encoder}</span>
              {/each}
              {#if worker.hardwareDecoders.length > 0}
                <span class="rounded-md border border-line px-2 py-0.5 text-ink-2">{t(i18n.m.workers.hw_decode, { list: worker.hardwareDecoders.join(', ') })}</span>
              {/if}
              {#if worker.vmaf !== 'None'}
                <span class="rounded-md border border-line px-2 py-0.5 text-ink-2">{i18n.m.workers.vmaf_on_worker}</span>
              {/if}
            </div>

            <!-- How busy the machine says it is. Only shown when it has actually said: a worker
                 that reports nothing shows nothing, rather than a 0% that would read as an idle
                 Mac and invite someone to send it more work. -->
            {#if worker.cpuBusyFraction != null || worker.gpuBusyFraction != null}
              <div class="flex flex-wrap items-center gap-3 text-xs text-ink-2" data-testid="worker-load">
                {#if worker.cpuBusyFraction != null}
                  <span class="flex items-center gap-1.5">
                    <span class="text-ink-3">{i18n.m.workers.cpu_label}</span>
                    <span class="h-1.5 w-16 overflow-hidden rounded-full bg-sunken">
                      <span class="block h-full rounded-full bg-sky-500" style="width: {Math.round(worker.cpuBusyFraction * 100)}%"></span>
                    </span>
                    <span class="font-mono tabular-nums">{Math.round(worker.cpuBusyFraction * 100)}%</span>
                  </span>
                {/if}
                {#if worker.gpuBusyFraction != null}
                  <!-- Titled, not footnoted: a hardware encode on Apple silicon runs on a media
                       engine that is not the GPU's shader cores and is not measurable, so this
                       reads low while the Mac is flat out. Better to say so than to let someone
                       conclude the accelerator is idle. -->
                  <span class="flex items-center gap-1.5" title={i18n.m.workers.gpu_hint}>
                    <span class="text-ink-3">{i18n.m.workers.gpu_label}</span>
                    <span class="h-1.5 w-16 overflow-hidden rounded-full bg-sunken">
                      <span class="block h-full rounded-full bg-violet-500" style="width: {Math.round(worker.gpuBusyFraction * 100)}%"></span>
                    </span>
                    <span class="font-mono tabular-nums">{Math.round(worker.gpuBusyFraction * 100)}%</span>
                  </span>
                {/if}
              </div>
            {/if}

            <dl class="worker-details grid gap-x-3 gap-y-1.5 text-sm">
              <dt class="text-ink-3">{i18n.m.workers.working_on}</dt>
              <dd class="min-w-0 text-ink">
                {#if worker.activeJobs.length === 0}
                  {worker.drainRequestedAt ? i18n.m.workers.idle_draining : i18n.m.workers.idle}
                {:else}
                  <ul class="space-y-2">
                    {#each worker.activeJobs as job (job.jobId)}
                      <li>
                        <div class="truncate font-mono text-xs" title={job.relativePath ?? ''}>{fileName(job.relativePath, job.jobId)}</div>
                        <div class="mt-1 flex items-center gap-2">
                          <div class="progress-track h-1.5 flex-1"><div class="progress-fill" style="width: {Math.round(job.progress * 100)}%"></div></div>
                          <span class="flex-shrink-0 whitespace-nowrap text-xs tabular-nums text-ink-3">
                            {stageLabel(job)}{job.stage === 'Encoding' ? ` ${Math.round(job.progress * 100)}%` : ''}
                          </span>
                        </div>
                      </li>
                    {/each}
                  </ul>
                {/if}
              </dd>

              <dt class="text-ink-3">{i18n.m.workers.load}</dt>
              <dd class="min-w-0 break-words text-ink">
                {t(i18n.m.workers.load_value, { held: worker.heldLeases, max: worker.maxConcurrency, scratch: formatSize(worker.freeScratchBytes) })}
              </dd>

              <dt class="text-ink-3">{i18n.m.workers.last_seen}</dt>
              <dd class="min-w-0 break-words text-ink">{ago(worker.lastSeenAt)}</dd>

              <dt class="text-ink-3">{i18n.m.workers.last_problem}</dt>
              <dd class="min-w-0 text-ink">
                {#if worker.lastProblem}
                  <span class="text-xs text-warn">{ago(worker.lastProblemAt)} · {worker.lastProblem}</span>
                {:else}
                  <span class="font-mono text-xs text-ink-3">{i18n.m.workers.no_problem}</span>
                {/if}
              </dd>
            </dl>

            {#if !worker.revokedAt}
              <div class="mt-auto flex flex-wrap gap-2 pt-1">
                {#if worker.drainRequestedAt}
                  <button class="btn btn-primary" disabled={busy} onclick={() => resume(worker)}>
                    {i18n.m.workers.resume}
                  </button>
                {:else}
                  <button class="btn btn-ghost" disabled={busy} onclick={() => drain(worker)}>
                    {worker.heldLeases > 0 ? i18n.m.workers.drain : i18n.m.workers.stop_taking}
                  </button>
                {/if}
                <button class="btn btn-ghost text-bad" disabled={busy} onclick={() => revoke(worker)}>
                  {i18n.m.workers.revoke}
                </button>
                <!-- Only for a sidecar that is not coming back. A live one should be revoked, which
                     stops it working while keeping the record of what it did. -->
                {#if worker.revokedAt || !worker.online}
                  <button class="btn btn-ghost text-ink-3" disabled={busy} onclick={() => forget(worker)}>
                    {i18n.m.workers.forget}
                  </button>
                {/if}
              </div>
            {/if}
          </article>
        {/each}
      </div>
    {/if}
  </section>
</div>

<style>
  .worker-card { container-type: inline-size; }
  .worker-details { grid-template-columns: minmax(0, 1fr); }
  .worker-details dd { margin-bottom: 0.5rem; }
  @container (min-width: 28rem) {
    .worker-details { grid-template-columns: minmax(0, 7rem) minmax(0, 1fr); }
    .worker-details dd { margin-bottom: 0; }
  }
</style>
