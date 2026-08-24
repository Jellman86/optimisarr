<script lang="ts">
  // Remote transcoding sidecars: pair one with a PIN, see what is paired, revoke it.
  // Loads its own data so it can be dropped into the Settings "Workers" tab without the
  // host wiring anything up, the same way ToolsPanel does.
  import { api, type Worker, type WorkerPairingCode } from '../api'
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

  // One ticker for the whole panel, only while a code is on screen.
  $effect(() => {
    if (!pairing) return
    const handle = setInterval(() => (nowMs = Date.now()), 1000)
    return () => clearInterval(handle)
  })

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

  const vmafLabels = ['None', 'CPU', 'CUDA']

  function status(worker: Worker): { label: string; classes: string } {
    if (worker.revokedAt) {
      return {
        label: i18n.m.workers.status_revoked,
        classes: 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-400',
      }
    }
    // Reachability first: a worker that has stopped checking in cannot take work whatever its
    // configured concurrency says, so showing "drained" there would be misleading.
    if (!worker.online) {
      return {
        label: i18n.m.workers.status_offline,
        classes: 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-400',
      }
    }
    if (worker.maxConcurrency <= 0) {
      return {
        label: i18n.m.workers.status_drained,
        classes: 'bg-amber-100 text-amber-800 dark:bg-amber-950/50 dark:text-amber-300',
      }
    }
    return {
      label: i18n.m.workers.status_online,
      classes: 'bg-emerald-100 text-emerald-800 dark:bg-emerald-950/50 dark:text-emerald-300',
    }
  }

  function paired(worker: Worker): string {
    return new Date(worker.pairedAt).toLocaleString()
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
      <!-- Stated plainly so nobody pairs a machine expecting their queue to speed up today. -->
      <Banner kind="info">{i18n.m.workers.not_dispatching}</Banner>

      {#if pairing && secondsLeft > 0}
        <div class="rounded-xl border border-cyan-500 bg-cyan-50/70 p-4 dark:border-cyan-500 dark:bg-cyan-950/25">
          <h3 class="text-sm font-semibold text-slate-900 dark:text-slate-100">
            {i18n.m.workers.pairing_title}
          </h3>
          <p class="mt-1 text-sm text-slate-600 dark:text-slate-300">{i18n.m.workers.pairing_hint}</p>

          <dl class="mt-3 space-y-3">
            <div>
              <dt class="text-xs font-medium uppercase tracking-wide text-slate-500 dark:text-slate-400">
                {i18n.m.workers.server_address}
              </dt>
              <dd class="mt-1 break-all font-mono text-sm text-slate-900 dark:text-slate-100">
                {serverAddress}
              </dd>
            </div>
            <div>
              <dt class="text-xs font-medium uppercase tracking-wide text-slate-500 dark:text-slate-400">
                {i18n.m.workers.pairing_code}
              </dt>
              <!-- Grouped for reading aloud; the server ignores the spacing on the way back. -->
              <dd class="mt-1 font-mono text-3xl font-bold tracking-[0.2em] text-slate-900 dark:text-slate-100">
                {pairing.code.slice(0, 4)} {pairing.code.slice(4)}
              </dd>
            </div>
          </dl>

          <div class="mt-3 flex flex-wrap items-center gap-3 text-xs text-slate-600 dark:text-slate-300">
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

  <section class="card overflow-hidden">
    <div class="overflow-x-auto">
      {#if loading}
        <p class="px-4 py-6 text-sm text-slate-500 dark:text-slate-400">{i18n.m.common.loading_short}</p>
      {:else if workers.length === 0}
        <div class="px-4 py-6">
          <p class="text-sm font-medium text-slate-700 dark:text-slate-200">{i18n.m.workers.empty}</p>
          <p class="mt-1 text-sm text-slate-500 dark:text-slate-400">{i18n.m.workers.empty_hint}</p>
        </div>
      {:else}
        <table class="w-full text-left text-sm">
          <thead class="border-b border-slate-200 text-xs uppercase tracking-wide text-slate-500 dark:border-slate-700 dark:text-slate-400">
            <tr>
              <th class="px-4 py-3 font-medium">{i18n.m.workers.col_name}</th>
              <th class="px-4 py-3 font-medium">{i18n.m.workers.col_platform}</th>
              <th class="px-4 py-3 font-medium">{i18n.m.workers.col_encoders}</th>
              <th class="px-4 py-3 font-medium">{i18n.m.workers.col_paired}</th>
              <th class="px-4 py-3 font-medium">{i18n.m.workers.col_status}</th>
              <th class="px-4 py-3"><span class="sr-only">{i18n.m.workers.revoke}</span></th>
            </tr>
          </thead>
          <tbody class="divide-y divide-slate-200 dark:divide-slate-700">
            {#each workers as worker (worker.id)}
              <tr>
                <td class="px-4 py-3 font-medium text-slate-900 dark:text-slate-100">{worker.name}</td>
                <td class="px-4 py-3 text-slate-600 dark:text-slate-300">
                  {worker.operatingSystem}
                  {worker.architecture}
                  <span class="block text-xs text-slate-500 dark:text-slate-400">
                    {t(i18n.m.workers.vmaf, { mode: vmafLabels[worker.vmaf] ?? '—' })}
                  </span>
                </td>
                <td class="px-4 py-3 text-slate-600 dark:text-slate-300">
                  {worker.videoEncoders.length > 0 ? worker.videoEncoders.join(', ') : '—'}
                </td>
                <td class="px-4 py-3 text-slate-600 dark:text-slate-300">{paired(worker)}</td>
                <td class="px-4 py-3">
                  <span class="badge {status(worker).classes}">{status(worker).label}</span>
                </td>
                <td class="px-4 py-3 text-right">
                  {#if !worker.revokedAt}
                    <button class="btn btn-ghost" disabled={busy} onclick={() => revoke(worker)}>
                      {i18n.m.workers.revoke}
                    </button>
                  {/if}
                </td>
              </tr>
            {/each}
          </tbody>
        </table>
      {/if}
    </div>
  </section>
</div>
