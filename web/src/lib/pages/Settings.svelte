<script lang="ts">
  import {
    api,
    type Settings,
    type ActivityWatcher,
    type ActivityWatcherType,
    type SaveActivityWatcher,
    type NotificationTarget,
    type NotificationType,
    type SaveNotificationTarget,
    type ArrConnection,
    type ArrConnectionType,
    type SaveArrConnection,
  } from '../api'
  import { formatSize } from '../format'
  import Toggle from '../components/Toggle.svelte'

  const notificationTypes: NotificationType[] = ['Webhook', 'Ntfy', 'Apprise']
  const emptyTarget = (): SaveNotificationTarget => ({
    name: '', type: 'Webhook', url: '', token: '', enabled: true, notifyOnReplacement: true, notifyOnFailure: true,
  })

  let targets = $state<NotificationTarget[]>([])
  let targetError = $state<string | null>(null)
  let editingTargetId = $state<number | null>(null)
  let targetDraft = $state<SaveNotificationTarget>(emptyTarget())
  let savingTarget = $state(false)

  async function loadTargets() {
    try {
      targets = await api.notificationTargets()
      targetError = null
    } catch (err) {
      targetError = err instanceof Error ? err.message : 'Unable to load notification targets'
    }
  }

  function startAddTarget() {
    editingTargetId = null
    targetDraft = emptyTarget()
  }

  function startEditTarget(t: NotificationTarget) {
    editingTargetId = t.id
    targetDraft = {
      name: t.name, type: t.type, url: t.url, token: '',
      enabled: t.enabled, notifyOnReplacement: t.notifyOnReplacement, notifyOnFailure: t.notifyOnFailure,
    }
  }

  async function saveTarget() {
    savingTarget = true
    targetError = null
    try {
      if (editingTargetId === null) await api.createNotificationTarget(targetDraft)
      else await api.updateNotificationTarget(editingTargetId, targetDraft)
      targetDraft = emptyTarget()
      editingTargetId = null
      await loadTargets()
    } catch (err) {
      targetError = err instanceof Error ? err.message : 'Unable to save notification target'
    } finally {
      savingTarget = false
    }
  }

  async function deleteTarget(t: NotificationTarget) {
    if (!confirm(`Remove the notification target "${t.name}"?`)) return
    targetError = null
    try {
      await api.deleteNotificationTarget(t.id)
      if (editingTargetId === t.id) startAddTarget()
      await loadTargets()
    } catch (err) {
      targetError = err instanceof Error ? err.message : 'Unable to remove target'
    }
  }

  const arrTypes: ArrConnectionType[] = ['Sonarr', 'Radarr']
  const emptyArr = (): SaveArrConnection => ({ name: '', type: 'Sonarr', baseUrl: '', apiKey: '', enabled: true })

  let arrs = $state<ArrConnection[]>([])
  let arrError = $state<string | null>(null)
  let editingArrId = $state<number | null>(null)
  let arrDraft = $state<SaveArrConnection>(emptyArr())
  let savingArr = $state(false)

  async function loadArrs() {
    try {
      arrs = await api.arrConnections()
      arrError = null
    } catch (err) {
      arrError = err instanceof Error ? err.message : 'Unable to load Sonarr/Radarr connections'
    }
  }

  function startAddArr() {
    editingArrId = null
    arrDraft = emptyArr()
  }

  function startEditArr(c: ArrConnection) {
    editingArrId = c.id
    arrDraft = { name: c.name, type: c.type, baseUrl: c.baseUrl, apiKey: '', enabled: c.enabled }
  }

  async function saveArr() {
    savingArr = true
    arrError = null
    try {
      if (editingArrId === null) await api.createArrConnection(arrDraft)
      else await api.updateArrConnection(editingArrId, arrDraft)
      arrDraft = emptyArr()
      editingArrId = null
      await loadArrs()
    } catch (err) {
      arrError = err instanceof Error ? err.message : 'Unable to save connection'
    } finally {
      savingArr = false
    }
  }

  async function deleteArr(c: ArrConnection) {
    if (!confirm(`Remove the ${c.type} connection "${c.name}"?`)) return
    arrError = null
    try {
      await api.deleteArrConnection(c.id)
      if (editingArrId === c.id) startAddArr()
      await loadArrs()
    } catch (err) {
      arrError = err instanceof Error ? err.message : 'Unable to remove connection'
    }
  }

  const watcherTypes: ActivityWatcherType[] = ['Plex', 'Jellyfin', 'Emby']
  const emptyWatcher = (): SaveActivityWatcher => ({ name: '', type: 'Plex', baseUrl: '', apiToken: '', enabled: true, refreshOnReplace: true })

  let watchers = $state<ActivityWatcher[]>([])
  let watcherError = $state<string | null>(null)
  let editingId = $state<number | null>(null)
  let watcherDraft = $state<SaveActivityWatcher>(emptyWatcher())
  let savingWatcher = $state(false)

  // Interactive sign-in (Plex OAuth/PIN, Jellyfin Quick Connect).
  let connecting = $state(false)
  let connectMessage = $state<string | null>(null)
  let jellyfinCode = $state<string | null>(null)
  let connectCancelled = false

  const delay = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

  function resetConnect() {
    connectCancelled = true
    connecting = false
    connectMessage = null
    jellyfinCode = null
  }

  async function pollForToken(check: () => Promise<{ authorized: boolean; token: string | null }>) {
    connectCancelled = false
    for (let i = 0; i < 60 && !connectCancelled; i++) {
      await delay(2000)
      if (connectCancelled) return null
      const result = await check()
      if (result.authorized && result.token) return result.token
    }
    if (!connectCancelled) connectMessage = 'Timed out waiting for approval. Try again.'
    return null
  }

  async function connect() {
    watcherError = null
    if (watcherDraft.type === 'Plex') return connectPlex()
    if (watcherDraft.type === 'Jellyfin') return connectJellyfin()
  }

  async function connectPlex() {
    connecting = true
    jellyfinCode = null
    connectMessage = 'Opening the Plex sign-in page…'
    try {
      const start = await api.plexConnectStart()
      window.open(start.authUrl, '_blank', 'noopener')
      connectMessage = 'Approve Optimisarr in the opened Plex tab, then come back here…'
      const token = await pollForToken(() => api.plexConnectPoll(start.id))
      if (token) {
        watcherDraft.apiToken = token
        connectMessage = 'Connected to Plex — token filled in. Save the watcher to keep it.'
      }
    } catch (err) {
      watcherError = err instanceof Error ? err.message : 'Plex sign-in failed'
      connectMessage = null
    } finally {
      connecting = false
    }
  }

  async function connectJellyfin() {
    const baseUrl = watcherDraft.baseUrl.trim()
    if (!baseUrl) {
      watcherError = 'Enter the Jellyfin base URL first.'
      return
    }
    connecting = true
    connectMessage = 'Starting Quick Connect…'
    try {
      const start = await api.jellyfinConnectStart(baseUrl)
      jellyfinCode = start.code
      connectMessage = 'In Jellyfin, open Quick Connect and enter this code, then keep this open…'
      const token = await pollForToken(() => api.jellyfinConnectPoll(baseUrl, start.secret))
      if (token) {
        watcherDraft.apiToken = token
        jellyfinCode = null
        connectMessage = 'Connected to Jellyfin — token filled in. Save the watcher to keep it.'
      }
    } catch (err) {
      watcherError = err instanceof Error ? err.message : 'Quick Connect failed'
      connectMessage = null
      jellyfinCode = null
    } finally {
      connecting = false
    }
  }

  async function loadWatchers() {
    try {
      watchers = await api.activityWatchers()
      watcherError = null
    } catch (err) {
      watcherError = err instanceof Error ? err.message : 'Unable to load watchers'
    }
  }

  function startAdd() {
    editingId = null
    watcherDraft = emptyWatcher()
    resetConnect()
  }

  function startEdit(w: ActivityWatcher) {
    editingId = w.id
    // Token is write-only; leave blank to keep the stored secret.
    watcherDraft = { name: w.name, type: w.type, baseUrl: w.baseUrl, apiToken: '', enabled: w.enabled, refreshOnReplace: w.refreshOnReplace }
    resetConnect()
  }

  async function saveWatcher() {
    savingWatcher = true
    watcherError = null
    try {
      if (editingId === null) {
        await api.createActivityWatcher(watcherDraft)
      } else {
        await api.updateActivityWatcher(editingId, watcherDraft)
      }
      watcherDraft = emptyWatcher()
      editingId = null
      resetConnect()
      await loadWatchers()
    } catch (err) {
      watcherError = err instanceof Error ? err.message : 'Unable to save watcher'
    } finally {
      savingWatcher = false
    }
  }

  async function deleteWatcher(w: ActivityWatcher) {
    if (!confirm(`Remove the activity watcher "${w.name}"?`)) return
    watcherError = null
    try {
      await api.deleteActivityWatcher(w.id)
      if (editingId === w.id) startAdd()
      await loadWatchers()
    } catch (err) {
      watcherError = err instanceof Error ? err.message : 'Unable to remove watcher'
    }
  }

  let settings = $state<Settings>({
    maxConcurrentJobs: 1,
    scheduleEnabled: false,
    scheduleWindowStart: '00:00',
    scheduleWindowEnd: '00:00',
    minFreeDiskBytes: 10 * 1024 * 1024 * 1024,
    cpuThreadLimit: 0,
    encoderMode: 'Auto',
    verificationDurationTolerancePercent: 1,
    verificationRequireAudioRetained: true,
    verificationRequireSubtitlesRetained: false,
    verificationRequireSizeReduction: true,
    verificationQualityGateEnabled: false,
    verificationMinimumVmafHarmonicMean: 93,
    verificationMinimumVmafMin: 80,
    verificationAudioLoudnessGateEnabled: false,
    verificationMaxLoudnessDriftLufs: 1,
    verificationAudioClippingGateEnabled: false,
    verificationMaxTruePeakDbtp: 0,
    replacementAllowCrossFilesystem: false,
    replacementQuarantineRetentionDays: 0,
  })
  let minFreeDiskGiB = $state('10')
  let loading = $state(true)
  let saving = $state(false)
  let error = $state<string | null>(null)
  let message = $state<string | null>(null)

  $effect(() => {
    void load()
    void loadWatchers()
    void loadTargets()
    void loadArrs()
  })

  async function load() {
    loading = true
    error = null
    try {
      settings = await api.settings()
      minFreeDiskGiB = bytesToGiB(settings.minFreeDiskBytes)
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to load settings'
    } finally {
      loading = false
    }
  }

  async function save() {
    saving = true
    error = null
    message = null
    try {
      settings = await api.saveSettings({
        ...settings,
        maxConcurrentJobs: Number(settings.maxConcurrentJobs) || 1,
        cpuThreadLimit: Math.max(0, Number(settings.cpuThreadLimit) || 0),
        verificationDurationTolerancePercent: Math.max(0, Number(settings.verificationDurationTolerancePercent) || 0),
        verificationMinimumVmafHarmonicMean: clamp01to100(settings.verificationMinimumVmafHarmonicMean),
        verificationMinimumVmafMin: clamp01to100(settings.verificationMinimumVmafMin),
        verificationMaxLoudnessDriftLufs: Math.max(0, Number(settings.verificationMaxLoudnessDriftLufs) || 0),
        verificationMaxTruePeakDbtp: Number(settings.verificationMaxTruePeakDbtp) || 0,
        minFreeDiskBytes: gibToBytes(minFreeDiskGiB),
      })
      minFreeDiskGiB = bytesToGiB(settings.minFreeDiskBytes)
      message = 'Settings saved.'
    } catch (err) {
      error = err instanceof Error ? err.message : 'Unable to save settings'
    } finally {
      saving = false
    }
  }

  function gibToBytes(value: string) {
    const parsed = Number(value)
    if (!Number.isFinite(parsed) || parsed < 0) return 0
    return Math.round(parsed * 1024 * 1024 * 1024)
  }

  function bytesToGiB(value: number) {
    return (value / 1024 / 1024 / 1024).toString()
  }

  function clamp01to100(value: number) {
    return Math.min(100, Math.max(0, Number(value) || 0))
  }

  // Backup & restore: export a secret-free config snapshot, or import one.
  let importing = $state(false)
  let backupError = $state<string | null>(null)
  let backupMessage = $state<string | null>(null)
  let fileInput = $state<HTMLInputElement>()

  async function exportConfig() {
    backupError = null
    backupMessage = null
    try {
      const snapshot = await api.exportSettings()
      const blob = new Blob([JSON.stringify(snapshot, null, 2)], { type: 'application/json' })
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = `optimisarr-config-${new Date().toISOString().slice(0, 10)}.json`
      link.click()
      URL.revokeObjectURL(url)
      backupMessage = 'Config exported. Tokens are not included — re-enter them after importing.'
    } catch (err) {
      backupError = err instanceof Error ? err.message : 'Unable to export config'
    }
  }

  async function importConfig(event: Event) {
    const input = event.currentTarget as HTMLInputElement
    const file = input.files?.[0]
    input.value = '' // let the same file be re-selected later
    if (!file) return
    backupError = null
    backupMessage = null
    importing = true
    try {
      const snapshot = JSON.parse(await file.text())
      const result = await api.importSettings(snapshot)
      backupMessage =
        `Imported ${result.librariesCreated + result.librariesUpdated} libraries, ` +
        `${result.watchersCreated + result.watchersUpdated} watchers, ` +
        `${result.targetsCreated + result.targetsUpdated} targets, and ` +
        `${result.settingsApplied} settings. Re-enter any provider tokens.`
      await load()
      await loadWatchers()
      await loadTargets()
    } catch (err) {
      backupError = err instanceof Error ? err.message : 'Unable to import config'
    } finally {
      importing = false
    }
  }
</script>

<header class="mb-6">
  <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">Settings</h1>
  <p class="text-sm text-slate-500 dark:text-slate-400">Global options that apply across every library.</p>
</header>

{#if error}
  <div class="card mb-4 border-red-300 p-3 text-sm text-red-700 dark:border-red-800 dark:text-red-400">{error}</div>
{/if}

{#if loading}
  <div class="card p-8 text-center text-slate-400">Loading…</div>
{:else}
  <div class="card max-w-2xl p-5">
    <h2 class="mb-4 font-semibold text-slate-800 dark:text-slate-100">Queue</h2>
    <div class="grid gap-5 sm:grid-cols-2">
      <div>
      <label class="label" for="max-jobs">Max concurrent jobs</label>
      <input id="max-jobs" class="input" type="number" min="1" bind:value={settings.maxConcurrentJobs} />
      <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">
        How many transcodes may run at once across all libraries. Start at 1 and raise it only if your CPU/GPU and disk can keep up.
      </p>
      </div>

      <div>
        <label class="label" for="encoder-mode">Encoder mode</label>
        <select id="encoder-mode" class="input" bind:value={settings.encoderMode}>
          <option value="Auto">Auto</option>
          <option value="Cpu">CPU</option>
          <option value="NvidiaNvenc">NVIDIA NVENC</option>
          <option value="IntelQsv">Intel QSV</option>
          <option value="Vaapi">VAAPI</option>
        </select>
        <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">
          Auto prefers hardware encoders when available, then falls back to CPU.
        </p>
      </div>

      <div>
        <label class="label" for="cpu-threads">CPU thread limit</label>
        <input id="cpu-threads" class="input" type="number" min="0" bind:value={settings.cpuThreadLimit} />
        <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">
          Passed to FFmpeg as <code>-threads</code> for each job. Set 0 to let FFmpeg decide.
        </p>
      </div>

      <div>
        <label class="label" for="free-disk">Minimum free work disk</label>
        <div class="flex items-center gap-2">
          <input id="free-disk" class="input" type="number" min="0" step="1" bind:value={minFreeDiskGiB} />
          <span class="text-sm text-slate-500 dark:text-slate-400">GiB</span>
        </div>
        <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">
          New jobs pause below this free space threshold. Set 0 to disable. Current setting: {formatSize(gibToBytes(minFreeDiskGiB))}.
        </p>
      </div>
    </div>

    <div class="mt-6 border-t border-slate-200 pt-5 dark:border-slate-800">
      <Toggle
        bind:checked={settings.scheduleEnabled}
        label="Restrict new jobs to a processing window"
        hint="Outside the window, running jobs finish but no new ones start."
      />
      <div class="mt-4 grid gap-4 sm:grid-cols-2">
        <div>
          <label class="label" for="window-start">Window start</label>
          <input id="window-start" class="input" type="time" bind:value={settings.scheduleWindowStart} disabled={!settings.scheduleEnabled} />
        </div>
        <div>
          <label class="label" for="window-end">Window end</label>
          <input id="window-end" class="input" type="time" bind:value={settings.scheduleWindowEnd} disabled={!settings.scheduleEnabled} />
        </div>
      </div>
      <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">
        A start and end of 00:00 means all day. Overnight windows such as 22:00 to 06:00 are supported.
      </p>
    </div>
  </div>

  <div class="card mt-5 max-w-2xl p-5">
    <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">Verification gates</h2>
    <p class="mb-4 text-xs text-slate-500 dark:text-slate-400">
      Decode health, output readability, and a video stream are always required. These add extra gates.
    </p>
    <div class="max-w-xs">
      <label class="label" for="duration-tolerance">Duration tolerance</label>
      <div class="flex items-center gap-2">
        <input
          id="duration-tolerance"
          class="input"
          type="number"
          min="0"
          step="0.1"
          bind:value={settings.verificationDurationTolerancePercent}
        />
        <span class="text-sm text-slate-500 dark:text-slate-400">%</span>
      </div>
    </div>

    <div class="mt-5 grid gap-4 border-t border-slate-200 pt-5 dark:border-slate-800">
      <Toggle bind:checked={settings.verificationRequireAudioRetained} label="Require all audio tracks to be retained" />
      <Toggle bind:checked={settings.verificationRequireSubtitlesRetained} label="Require all subtitle tracks to be retained" />
      <Toggle bind:checked={settings.verificationRequireSizeReduction} label="Require output to be smaller than the original" />
    </div>

    <div class="mt-5 border-t border-slate-200 pt-5 dark:border-slate-800">
      <Toggle
        bind:checked={settings.verificationQualityGateEnabled}
        label="Measure perceptual quality (VMAF) and gate on it"
        hint="Compares the output to the original with FFmpeg's libvmaf. Needs an ffmpeg built with libvmaf and roughly doubles verification time, so it is off by default. A score of ~95 means visually indistinguishable from the source."
      />
      <div class="mt-4 grid gap-4 sm:grid-cols-2" class:opacity-50={!settings.verificationQualityGateEnabled}>
        <div>
          <label class="label" for="vmaf-harmonic">Minimum VMAF (harmonic mean)</label>
          <input
            id="vmaf-harmonic"
            class="input"
            type="number"
            min="0"
            max="100"
            step="0.5"
            bind:value={settings.verificationMinimumVmafHarmonicMean}
            disabled={!settings.verificationQualityGateEnabled}
          />
          <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">Overall quality floor. The harmonic mean penalises bad frames more than a plain average.</p>
        </div>
        <div>
          <label class="label" for="vmaf-min">Minimum VMAF (worst frame)</label>
          <input
            id="vmaf-min"
            class="input"
            type="number"
            min="0"
            max="100"
            step="0.5"
            bind:value={settings.verificationMinimumVmafMin}
            disabled={!settings.verificationQualityGateEnabled}
          />
          <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">Catches short artifact bursts a healthy average would hide. If quality can't be measured, the gate fails closed.</p>
        </div>
      </div>
    </div>

    <div class="mt-5 border-t border-slate-200 pt-5 dark:border-slate-800">
      <Toggle
        bind:checked={settings.verificationAudioLoudnessGateEnabled}
        label="Check audio loudness drift (EBU R128)"
        hint="Measures integrated loudness of the original and output with FFmpeg's ebur128 filter and fails the job if they differ too much. Adds a decode pass per file, so it is off by default; most useful when a profile re-encodes audio."
      />
      <div class="mt-4 max-w-xs" class:opacity-50={!settings.verificationAudioLoudnessGateEnabled}>
        <label class="label" for="loudness-drift">Maximum loudness drift</label>
        <div class="flex items-center gap-2">
          <input
            id="loudness-drift"
            class="input"
            type="number"
            min="0"
            step="0.1"
            bind:value={settings.verificationMaxLoudnessDriftLufs}
            disabled={!settings.verificationAudioLoudnessGateEnabled}
          />
          <span class="text-sm text-slate-500 dark:text-slate-400">LU</span>
        </div>
        <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">If loudness can't be measured, the gate fails closed.</p>
      </div>
    </div>

    <div class="mt-5 border-t border-slate-200 pt-5 dark:border-slate-800">
      <Toggle
        bind:checked={settings.verificationAudioClippingGateEnabled}
        label="Check for introduced audio clipping (true peak)"
        hint="Reads the output's true peak from the same ebur128 pass and fails the job only when the re-encode pushes the peak above the ceiling while the original sat below it — a source that was already hot is not blamed on the re-encode."
      />
      <div class="mt-4 max-w-xs" class:opacity-50={!settings.verificationAudioClippingGateEnabled}>
        <label class="label" for="true-peak-ceiling">True-peak ceiling</label>
        <div class="flex items-center gap-2">
          <input
            id="true-peak-ceiling"
            class="input"
            type="number"
            step="0.1"
            bind:value={settings.verificationMaxTruePeakDbtp}
            disabled={!settings.verificationAudioClippingGateEnabled}
          />
          <span class="text-sm text-slate-500 dark:text-slate-400">dBTP</span>
        </div>
        <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">0 dBTP is full scale; set a margin like −1 to be stricter. If the true peak can't be measured, the gate fails closed.</p>
      </div>
    </div>
  </div>

  <div class="card mt-5 max-w-2xl p-5">
    <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">Replacement</h2>
    <p class="mb-4 text-xs text-slate-500 dark:text-slate-400">
      How verified outputs take the place of your originals.
    </p>
    <Toggle
      bind:checked={settings.replacementAllowCrossFilesystem}
      label="Allow cross-filesystem replacement"
      hint="Falls back to copy-plus-delete instead of an atomic move. Off is safer; enable only for intentional split-mount layouts."
    />
    <div class="mt-5 max-w-xs border-t border-slate-200 pt-5 dark:border-slate-800">
      <label class="label" for="quarantine-retention">Quarantine retention</label>
      <div class="flex items-center gap-2">
        <input
          id="quarantine-retention"
          class="input"
          type="number"
          min="0"
          step="1"
          bind:value={settings.replacementQuarantineRetentionDays}
        />
        <span class="text-sm text-slate-500 dark:text-slate-400">days</span>
      </div>
      <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">How long quarantined originals are kept. Set 0 to keep them indefinitely.</p>
    </div>
  </div>

  <div class="mt-5 flex max-w-2xl items-center gap-3">
    <button class="btn btn-primary" onclick={save} disabled={saving}>{saving ? 'Saving…' : 'Save settings'}</button>
    {#if message}<span class="text-sm text-emerald-600 dark:text-emerald-400">{message}</span>{/if}
  </div>

  <div class="card mt-5 max-w-2xl p-5">
    <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">Service activity</h2>
    <p class="mb-4 text-xs text-slate-500 dark:text-slate-400">
      While any enabled media server is streaming, new jobs pause so transcodes never compete with playback.
      Running jobs are never interrupted. An unreachable server does not pause the queue.
    </p>

    {#if watcherError}
      <div class="mb-3 rounded border border-red-300 p-2 text-sm text-red-700 dark:border-red-800 dark:text-red-400">{watcherError}</div>
    {/if}

    {#if watchers.length > 0}
      <ul class="mb-4 divide-y divide-slate-100 dark:divide-slate-800">
        {#each watchers as w (w.id)}
          <li class="flex items-center gap-3 py-2">
            <span class="badge bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300">{w.type}</span>
            <div class="min-w-0 flex-1">
              <div class="truncate text-sm font-medium text-slate-700 dark:text-slate-200">{w.name}</div>
              <div class="truncate font-mono text-[11px] text-slate-400" title={w.baseUrl}>{w.baseUrl}</div>
            </div>
            {#if !w.enabled}<span class="badge bg-slate-100 text-slate-400 dark:bg-slate-800 dark:text-slate-500">disabled</span>{/if}
            {#if w.refreshOnReplace}<span class="badge bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400" title="Re-scans this server after a verified replacement.">refresh</span>{/if}
            {#if !w.hasToken}<span class="badge bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300" title="No token set — Optimisarr cannot query this server.">no token</span>{/if}
            <button class="btn btn-ghost px-2 py-1 text-xs" onclick={() => startEdit(w)}>Edit</button>
            <button class="btn btn-ghost px-2 py-1 text-xs text-red-600 dark:text-red-400" onclick={() => deleteWatcher(w)}>Remove</button>
          </li>
        {/each}
      </ul>
    {:else}
      <p class="mb-4 text-sm text-slate-400">No watchers yet. Add one to pause processing while you stream.</p>
    {/if}

    <div class="rounded-lg border border-slate-200 p-4 dark:border-slate-800">
      <h3 class="mb-3 text-sm font-semibold text-slate-700 dark:text-slate-200">
        {editingId === null ? 'Add a watcher' : 'Edit watcher'}
      </h3>
      <div class="grid gap-3 sm:grid-cols-2">
        <div>
          <label class="label" for="watcher-name">Name</label>
          <input id="watcher-name" class="input" placeholder="Living room Plex" bind:value={watcherDraft.name} />
        </div>
        <div>
          <label class="label" for="watcher-type">Type</label>
          <select id="watcher-type" class="input" bind:value={watcherDraft.type}>
            {#each watcherTypes as t}<option value={t}>{t}</option>{/each}
          </select>
        </div>
        <div>
          <label class="label" for="watcher-url">Base URL</label>
          <input id="watcher-url" class="input" placeholder="http://192.168.1.10:32400" bind:value={watcherDraft.baseUrl} />
        </div>
        <div>
          <label class="label" for="watcher-token">
            {watcherDraft.type === 'Plex' ? 'Plex token' : 'API key'}
          </label>
          <div class="flex items-center gap-2">
            <input
              id="watcher-token"
              class="input"
              type="password"
              placeholder={editingId === null ? '' : 'Leave blank to keep current'}
              bind:value={watcherDraft.apiToken}
            />
            {#if watcherDraft.type !== 'Emby'}
              {#if connecting}
                <button class="btn btn-ghost whitespace-nowrap px-3 py-1 text-xs" onclick={resetConnect}>Cancel</button>
              {:else}
                <button class="btn whitespace-nowrap px-3 py-1 text-xs" onclick={connect}>
                  {watcherDraft.type === 'Plex' ? 'Sign in with Plex' : 'Quick Connect'}
                </button>
              {/if}
            {/if}
          </div>
          {#if connectMessage}
            <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">{connectMessage}</p>
          {/if}
          {#if jellyfinCode}
            <p class="mt-1 font-mono text-lg tracking-widest text-cyan-600 dark:text-cyan-400">{jellyfinCode}</p>
          {/if}
        </div>
      </div>
      <div class="mt-3 grid gap-3">
        <Toggle bind:checked={watcherDraft.enabled} label="Enabled" hint="Watch this server for active playback to pause processing." />
        <Toggle bind:checked={watcherDraft.refreshOnReplace} label="Refresh after replacements" hint="Tell this server to re-scan the title after a verified replacement or rollback." />
      </div>
      <div class="mt-4 flex items-center gap-2">
        <button class="btn btn-primary px-3 py-1 text-sm" onclick={saveWatcher} disabled={savingWatcher}>
          {savingWatcher ? 'Saving…' : editingId === null ? 'Add watcher' : 'Save changes'}
        </button>
        {#if editingId !== null}
          <button class="btn btn-ghost px-3 py-1 text-sm" onclick={startAdd} disabled={savingWatcher}>Cancel</button>
        {/if}
      </div>
    </div>
  </div>

  <div class="card mt-5 max-w-2xl p-5">
    <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">Notifications</h2>
    <p class="mb-4 text-xs text-slate-500 dark:text-slate-400">
      POST to a webhook, ntfy topic, or Apprise endpoint when a file is replaced or a job fails.
      Delivery is best-effort and never affects processing.
    </p>

    {#if targetError}
      <div class="mb-3 rounded border border-red-300 p-2 text-sm text-red-700 dark:border-red-800 dark:text-red-400">{targetError}</div>
    {/if}

    {#if targets.length > 0}
      <ul class="mb-4 divide-y divide-slate-100 dark:divide-slate-800">
        {#each targets as t (t.id)}
          <li class="flex items-center gap-3 py-2">
            <span class="badge bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300">{t.type}</span>
            <div class="min-w-0 flex-1">
              <div class="truncate text-sm font-medium text-slate-700 dark:text-slate-200">{t.name}</div>
              <div class="truncate font-mono text-[11px] text-slate-400" title={t.url}>{t.url}</div>
            </div>
            {#if !t.enabled}<span class="badge bg-slate-100 text-slate-400 dark:bg-slate-800 dark:text-slate-500">disabled</span>{/if}
            {#if t.notifyOnReplacement}<span class="badge bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400">replaced</span>{/if}
            {#if t.notifyOnFailure}<span class="badge bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300">failed</span>{/if}
            <button class="btn btn-ghost px-2 py-1 text-xs" onclick={() => startEditTarget(t)}>Edit</button>
            <button class="btn btn-ghost px-2 py-1 text-xs text-red-600 dark:text-red-400" onclick={() => deleteTarget(t)}>Remove</button>
          </li>
        {/each}
      </ul>
    {:else}
      <p class="mb-4 text-sm text-slate-400">No notification targets yet.</p>
    {/if}

    <div class="rounded-lg border border-slate-200 p-4 dark:border-slate-800">
      <h3 class="mb-3 text-sm font-semibold text-slate-700 dark:text-slate-200">
        {editingTargetId === null ? 'Add a target' : 'Edit target'}
      </h3>
      <div class="grid gap-3 sm:grid-cols-2">
        <div>
          <label class="label" for="target-name">Name</label>
          <input id="target-name" class="input" placeholder="ntfy alerts" bind:value={targetDraft.name} />
        </div>
        <div>
          <label class="label" for="target-type">Type</label>
          <select id="target-type" class="input" bind:value={targetDraft.type}>
            {#each notificationTypes as t}<option value={t}>{t}</option>{/each}
          </select>
        </div>
        <div>
          <label class="label" for="target-url">URL</label>
          <input id="target-url" class="input" placeholder="https://ntfy.sh/my-topic" bind:value={targetDraft.url} />
        </div>
        <div>
          <label class="label" for="target-token">Token <span class="text-slate-400">(optional)</span></label>
          <input
            id="target-token"
            class="input"
            type="password"
            placeholder={editingTargetId === null ? '' : 'Leave blank to keep current'}
            bind:value={targetDraft.token}
          />
        </div>
      </div>
      <div class="mt-3 grid gap-3">
        <Toggle bind:checked={targetDraft.enabled} label="Enabled" />
        <Toggle bind:checked={targetDraft.notifyOnReplacement} label="Notify when a file is replaced" />
        <Toggle bind:checked={targetDraft.notifyOnFailure} label="Notify when a job fails" />
      </div>
      <div class="mt-4 flex items-center gap-2">
        <button class="btn btn-primary px-3 py-1 text-sm" onclick={saveTarget} disabled={savingTarget}>
          {savingTarget ? 'Saving…' : editingTargetId === null ? 'Add target' : 'Save changes'}
        </button>
        {#if editingTargetId !== null}
          <button class="btn btn-ghost px-3 py-1 text-sm" onclick={startAddTarget} disabled={savingTarget}>Cancel</button>
        {/if}
      </div>
    </div>
  </div>

  <div class="card mt-5 max-w-2xl p-5">
    <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">Sonarr / Radarr</h2>
    <p class="mb-4 text-xs text-slate-500 dark:text-slate-400">
      While a connected manager is importing into a title's folder, files in that folder are held back from
      queueing so a transcode never fights an import. They become eligible again on the next enqueue once the
      import settles. An unreachable manager never blocks the queue.
    </p>

    {#if arrError}
      <div class="mb-3 rounded border border-red-300 p-2 text-sm text-red-700 dark:border-red-800 dark:text-red-400">{arrError}</div>
    {/if}

    {#if arrs.length > 0}
      <ul class="mb-4 divide-y divide-slate-100 dark:divide-slate-800">
        {#each arrs as c (c.id)}
          <li class="flex items-center gap-3 py-2">
            <span class="badge bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300">{c.type}</span>
            <div class="min-w-0 flex-1">
              <div class="truncate text-sm font-medium text-slate-700 dark:text-slate-200">{c.name}</div>
              <div class="truncate font-mono text-[11px] text-slate-400" title={c.baseUrl}>{c.baseUrl}</div>
            </div>
            {#if !c.enabled}<span class="badge bg-slate-100 text-slate-400 dark:bg-slate-800 dark:text-slate-500">disabled</span>{/if}
            {#if !c.hasApiKey}<span class="badge bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300" title="No API key set — Optimisarr cannot query this manager.">no key</span>{/if}
            <button class="btn btn-ghost px-2 py-1 text-xs" onclick={() => startEditArr(c)}>Edit</button>
            <button class="btn btn-ghost px-2 py-1 text-xs text-red-600 dark:text-red-400" onclick={() => deleteArr(c)}>Remove</button>
          </li>
        {/each}
      </ul>
    {:else}
      <p class="mb-4 text-sm text-slate-400">No connections yet. Add Sonarr or Radarr to avoid optimising files mid-import.</p>
    {/if}

    <div class="rounded-lg border border-slate-200 p-4 dark:border-slate-800">
      <h3 class="mb-3 text-sm font-semibold text-slate-700 dark:text-slate-200">
        {editingArrId === null ? 'Add a connection' : 'Edit connection'}
      </h3>
      <div class="grid gap-3 sm:grid-cols-2">
        <div>
          <label class="label" for="arr-name">Name</label>
          <input id="arr-name" class="input" placeholder="Sonarr" bind:value={arrDraft.name} />
        </div>
        <div>
          <label class="label" for="arr-type">Type</label>
          <select id="arr-type" class="input" bind:value={arrDraft.type}>
            {#each arrTypes as t}<option value={t}>{t}</option>{/each}
          </select>
        </div>
        <div>
          <label class="label" for="arr-url">Base URL</label>
          <input id="arr-url" class="input" placeholder="http://192.168.1.10:8989" bind:value={arrDraft.baseUrl} />
        </div>
        <div>
          <label class="label" for="arr-key">API key</label>
          <input
            id="arr-key"
            class="input"
            type="password"
            placeholder={editingArrId === null ? '' : 'Leave blank to keep current'}
            bind:value={arrDraft.apiKey}
          />
        </div>
      </div>
      <div class="mt-3 grid gap-3">
        <Toggle bind:checked={arrDraft.enabled} label="Enabled" hint="Query this manager for in-progress imports before queueing." />
      </div>
      <div class="mt-4 flex items-center gap-2">
        <button class="btn btn-primary px-3 py-1 text-sm" onclick={saveArr} disabled={savingArr}>
          {savingArr ? 'Saving…' : editingArrId === null ? 'Add connection' : 'Save changes'}
        </button>
        {#if editingArrId !== null}
          <button class="btn btn-ghost px-3 py-1 text-sm" onclick={startAddArr} disabled={savingArr}>Cancel</button>
        {/if}
      </div>
    </div>
  </div>

  <div class="card mt-5 max-w-2xl p-5">
    <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">Backup &amp; restore</h2>
    <p class="mb-4 text-xs text-slate-500 dark:text-slate-400">
      Export your settings, libraries, watchers, and notification targets to a JSON file, or import one.
      For your security, provider tokens are never exported — re-enter them after importing. Importing merges
      into your current config (matching libraries by path and watchers/targets by name) and never deletes anything.
    </p>

    {#if backupError}
      <div class="mb-3 rounded border border-red-300 p-2 text-sm text-red-700 dark:border-red-800 dark:text-red-400">{backupError}</div>
    {/if}
    {#if backupMessage}
      <div class="mb-3 rounded border border-emerald-300 p-2 text-sm text-emerald-700 dark:border-emerald-800 dark:text-emerald-400">{backupMessage}</div>
    {/if}

    <div class="flex items-center gap-3">
      <button class="btn" onclick={exportConfig}>Export config</button>
      <button class="btn" onclick={() => fileInput?.click()} disabled={importing}>
        {importing ? 'Importing…' : 'Import config'}
      </button>
      <input bind:this={fileInput} type="file" accept="application/json,.json" class="hidden" onchange={importConfig} />
    </div>
  </div>
{/if}
