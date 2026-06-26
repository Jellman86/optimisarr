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
    type ConnectionTestResult,
    type PlexDiscoveredServer,
  } from '../api'
  import { formatSize } from '../format'
  import { router } from '../stores/ui.svelte'
  import Toggle from '../components/Toggle.svelte'
  import InfoTip from '../components/InfoTip.svelte'
  import Banner from '../components/Banner.svelte'
  import ToolsPanel from '../components/ToolsPanel.svelte'

  // Settings is split into tabs so each concern is found quickly and Tools lives here
  // rather than in its own sidebar entry. The General tab holds the core settings the
  // single "Save settings" button persists together; the rest manage their own records.
  type TabKey = 'general' | 'connections' | 'notifications' | 'tools' | 'backup'
  const tabs: { key: TabKey; label: string }[] = [
    { key: 'general', label: 'General' },
    { key: 'connections', label: 'Connections' },
    { key: 'notifications', label: 'Notifications' },
    { key: 'tools', label: 'Tools' },
    { key: 'backup', label: 'Backup' },
  ]
  // A visit to the old /tools route lands on Settings with the Tools tab open.
  let activeTab = $state<TabKey>(router.path.startsWith('/tools') ? 'tools' : 'general')

  const notificationTypes: NotificationType[] = ['Webhook', 'Discord', 'Ntfy', 'Apprise']
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

  // Discovered Plex servers (after sign-in) and the last "Test connection" result.
  let plexServers = $state<PlexDiscoveredServer[] | null>(null)
  let testing = $state(false)
  let testResult = $state<ConnectionTestResult | null>(null)

  const delay = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

  function resetConnect() {
    connectCancelled = true
    connecting = false
    connectMessage = null
    jellyfinCode = null
    plexServers = null
    testResult = null
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
        connectMessage = 'Connected to Plex — finding your servers…'
        try {
          plexServers = await api.plexServers(token)
          connectMessage = plexServers.length
            ? 'Pick your server below, or save the token as-is.'
            : 'Connected — no servers found on this account. Enter the URL manually.'
        } catch {
          connectMessage = 'Connected to Plex — token filled in. Enter the server URL, then Test.'
        }
      }
    } catch (err) {
      watcherError = err instanceof Error ? err.message : 'Plex sign-in failed'
      connectMessage = null
    } finally {
      connecting = false
    }
  }

  // Fill the connection from a discovered Plex server (local URL preferred, its own token).
  function selectPlexServer(server: PlexDiscoveredServer) {
    watcherDraft.baseUrl = server.uri
    if (server.accessToken) watcherDraft.apiToken = server.accessToken
    if (!watcherDraft.name.trim()) watcherDraft.name = server.name
    plexServers = null
    testResult = null
    connectMessage = `Selected "${server.name}". Test or save the connection.`
  }

  async function testConnection() {
    testing = true
    testResult = null
    try {
      testResult = await api.testConnection({
        type: watcherDraft.type,
        baseUrl: watcherDraft.baseUrl.trim(),
        token: watcherDraft.apiToken || undefined,
        id: editingId ?? undefined,
      })
    } catch (err) {
      testResult = { ok: false, serverName: null, version: null, error: err instanceof Error ? err.message : 'Test failed' }
    } finally {
      testing = false
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
    minFreeDiskBytes: 10 * 1024 * 1024 * 1024,
    cpuThreadLimit: 0,
    libraryScanIntervalHours: 1,
    encoderMode: 'Auto',
    hardwareDecode: true,
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
    verificationImageQualityGateEnabled: false,
    verificationMinimumImageSsim: 0.95,
    verificationImageMetadataGateEnabled: false,
    replacementAllowCrossFilesystem: false,
    dryRunMode: false,
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
        libraryScanIntervalHours: Math.max(1, Number(settings.libraryScanIntervalHours) || 1),
        verificationDurationTolerancePercent: Math.max(0, Number(settings.verificationDurationTolerancePercent) || 0),
        verificationMinimumVmafHarmonicMean: clamp01to100(settings.verificationMinimumVmafHarmonicMean),
        verificationMinimumVmafMin: clamp01to100(settings.verificationMinimumVmafMin),
        verificationMaxLoudnessDriftLufs: Math.max(0, Number(settings.verificationMaxLoudnessDriftLufs) || 0),
        verificationMaxTruePeakDbtp: Number(settings.verificationMaxTruePeakDbtp) || 0,
        verificationMinimumImageSsim: Math.min(1, Math.max(0, Number(settings.verificationMinimumImageSsim) || 0)),
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

  // Backup & restore: export/import configuration including provider secrets.
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
      backupMessage = 'Config exported. It includes provider secrets; store the file securely and never commit or share it.'
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
        `${result.targetsCreated + result.targetsUpdated} targets, ` +
        `${result.arrConnectionsCreated + result.arrConnectionsUpdated} Sonarr/Radarr connections, and ` +
        `${result.settingsApplied} settings.`
      await load()
      await loadWatchers()
      await loadTargets()
      await loadArrs()
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
  <Banner kind="error" class="mb-4">{error}</Banner>
{/if}

{#if loading}
  <div class="card p-8 text-center text-slate-400">Loading…</div>
{:else}
  <div class="no-scrollbar mb-5 flex gap-1 overflow-x-auto border-b border-slate-200 dark:border-slate-700">
    {#each tabs as tab}
      <button
        class="-mb-px flex-shrink-0 whitespace-nowrap border-b-2 px-3 py-2 text-sm font-medium transition-colors {activeTab === tab.key
          ? 'border-cyan-500 text-cyan-700 dark:text-cyan-300'
          : 'border-transparent text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200'}"
        onclick={() => (activeTab = tab.key)}
      >
        {tab.label}
      </button>
    {/each}
  </div>

  {#if activeTab === 'general'}
  <div class="space-y-5">
  <div class="card p-5">
    <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">Queue</h2>
    <p class="mb-4 text-xs text-slate-500 dark:text-slate-400">
      How many jobs run, what does the encoding, and when the queue is allowed to start work.
    </p>
    <div class="grid gap-5 sm:grid-cols-2 lg:grid-cols-4">
      <div>
        <label class="label" for="max-jobs">Max concurrent jobs <InfoTip text="How many transcodes run at once across all libraries. Start at 1 and raise it only if your CPU/GPU and disk keep up." /></label>
        <input id="max-jobs" class="input" type="number" min="1" bind:value={settings.maxConcurrentJobs} />
      </div>

      <div>
        <label class="label" for="encoder-mode">Encoder mode <InfoTip text="Auto prefers a hardware encoder when available, then falls back to CPU. The Tools tab shows what your GPU supports." /></label>
        <select id="encoder-mode" class="input" bind:value={settings.encoderMode}>
          <option value="Auto">Auto</option>
          <option value="Cpu">CPU</option>
          <option value="NvidiaNvenc">NVIDIA NVENC</option>
          <option value="IntelQsv">Intel QSV</option>
          <option value="Vaapi">VAAPI</option>
        </select>
      </div>

      <div>
        <label class="label" for="cpu-threads">CPU thread limit <InfoTip text="Passed to FFmpeg as -threads for each job. 0 lets FFmpeg decide." /></label>
        <input id="cpu-threads" class="input" type="number" min="0" bind:value={settings.cpuThreadLimit} />
      </div>

      <div>
        <label class="label" for="scan-interval">Library scan interval <InfoTip text="How often every enabled library is rescanned for new or changed files (and they are reprobed). Scanning is cheap — it skips unchanged files. When each library's files are auto-enqueued and run is set per library (its auto-optimise window)." /></label>
        <div class="flex items-center gap-2">
          <input id="scan-interval" class="input" type="number" min="1" step="1" bind:value={settings.libraryScanIntervalHours} />
          <span class="text-sm text-slate-500 dark:text-slate-400">hours</span>
        </div>
      </div>

      <div>
        <label class="label" for="free-disk">Minimum free work disk <InfoTip text={`New jobs pause when the work disk falls below this. 0 disables the check. Currently ${formatSize(gibToBytes(minFreeDiskGiB))}.`} /></label>
        <div class="flex items-center gap-2">
          <input id="free-disk" class="input" type="number" min="0" step="1" bind:value={minFreeDiskGiB} />
          <span class="text-sm text-slate-500 dark:text-slate-400">GiB</span>
        </div>
      </div>
    </div>

    <div class="mt-5 border-t border-slate-200 pt-5 dark:border-slate-800">
      <Toggle
        bind:checked={settings.hardwareDecode}
        label="Hardware decoding"
        hint="When a hardware encoder is in use, decode the source on the GPU too, to cut CPU load. Falls back to CPU decoding automatically for sources the GPU can't decode. No effect on CPU encoding."
      />
      <p class="mt-3 text-xs text-slate-500 dark:text-slate-400">
        When jobs run is set per library, on the <button class="text-cyan-600 hover:underline dark:text-cyan-400" onclick={() => router.go('/libraries')}>Libraries</button> page: enable "Optimise automatically" and choose a window.
      </p>
    </div>
  </div>

  <div class="card p-5">
    <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">Verification gates</h2>
    <p class="mb-4 text-xs text-slate-500 dark:text-slate-400">
      Every job must already pass a decode-health check, be readable by ffprobe, and keep a video stream.
      These optional gates add stricter checks before an output may replace an original. Each fails closed —
      if a measurement can't be taken, the job fails rather than risk a bad replacement.
    </p>

    <div class="grid gap-4 lg:grid-cols-2">
      <div class="rounded-lg border border-slate-200 p-4 dark:border-slate-800">
        <h3 class="mb-3 text-sm font-semibold text-slate-700 dark:text-slate-200">Always-on checks</h3>
        <div class="max-w-[16rem]">
          <label class="label" for="duration-tolerance">Duration tolerance <InfoTip text="How far the output's runtime may drift from the original before the job fails." /></label>
          <div class="flex items-center gap-2">
            <input id="duration-tolerance" class="input" type="number" min="0" step="0.1" bind:value={settings.verificationDurationTolerancePercent} />
            <span class="text-sm text-slate-500 dark:text-slate-400">%</span>
          </div>
        </div>
        <div class="mt-4 grid gap-3 border-t border-slate-200 pt-4 dark:border-slate-800">
          <Toggle bind:checked={settings.verificationRequireAudioRetained} label="Require all audio tracks to be retained" />
          <Toggle bind:checked={settings.verificationRequireSubtitlesRetained} label="Require all subtitle tracks to be retained" />
          <Toggle bind:checked={settings.verificationRequireSizeReduction} label="Require output to be smaller than the original" />
        </div>
      </div>

      <div class="rounded-lg border border-slate-200 p-4 dark:border-slate-800">
        <Toggle
          bind:checked={settings.verificationQualityGateEnabled}
          label="Perceptual quality (VMAF)"
          hint="Compares output to original with FFmpeg's libvmaf. Needs an ffmpeg built with libvmaf and roughly doubles verification time, so it's off by default. ~95 is visually indistinguishable from the source."
        />
        <div class="mt-4 grid gap-4 sm:grid-cols-2" class:opacity-50={!settings.verificationQualityGateEnabled}>
          <div>
            <label class="label" for="vmaf-harmonic">Min VMAF (harmonic mean) <InfoTip text="Overall quality floor. The harmonic mean penalises bad frames more than a plain average." /></label>
            <input id="vmaf-harmonic" class="input" type="number" min="0" max="100" step="0.5" bind:value={settings.verificationMinimumVmafHarmonicMean} disabled={!settings.verificationQualityGateEnabled} />
          </div>
          <div>
            <label class="label" for="vmaf-min">Min VMAF (worst frame) <InfoTip text="Catches short artifact bursts a healthy average would hide." /></label>
            <input id="vmaf-min" class="input" type="number" min="0" max="100" step="0.5" bind:value={settings.verificationMinimumVmafMin} disabled={!settings.verificationQualityGateEnabled} />
          </div>
        </div>
      </div>

      <div class="rounded-lg border border-slate-200 p-4 dark:border-slate-800">
        <Toggle
          bind:checked={settings.verificationAudioLoudnessGateEnabled}
          label="Audio loudness drift (EBU R128)"
          hint="Measures integrated loudness of original and output with FFmpeg's ebur128 filter and fails the job if they differ too much. Adds a decode pass, so it's off by default; most useful when a profile re-encodes audio."
        />
        <div class="mt-4 max-w-[16rem]" class:opacity-50={!settings.verificationAudioLoudnessGateEnabled}>
          <label class="label" for="loudness-drift">Maximum loudness drift</label>
          <div class="flex items-center gap-2">
            <input id="loudness-drift" class="input" type="number" min="0" step="0.1" bind:value={settings.verificationMaxLoudnessDriftLufs} disabled={!settings.verificationAudioLoudnessGateEnabled} />
            <span class="text-sm text-slate-500 dark:text-slate-400">LU</span>
          </div>
        </div>
      </div>

      <div class="rounded-lg border border-slate-200 p-4 dark:border-slate-800">
        <Toggle
          bind:checked={settings.verificationAudioClippingGateEnabled}
          label="Introduced audio clipping (true peak)"
          hint="Reads the output's true peak from the same ebur128 pass and fails only when the re-encode pushes the peak above the ceiling while the original sat below it — a source that was already hot is not blamed on the re-encode."
        />
        <div class="mt-4 max-w-[16rem]" class:opacity-50={!settings.verificationAudioClippingGateEnabled}>
          <label class="label" for="true-peak-ceiling">True-peak ceiling <InfoTip text="0 dBTP is full scale; set a margin like −1 to be stricter. The job fails only if the re-encode pushes the peak above this while the original sat below it." /></label>
          <div class="flex items-center gap-2">
            <input id="true-peak-ceiling" class="input" type="number" step="0.1" bind:value={settings.verificationMaxTruePeakDbtp} disabled={!settings.verificationAudioClippingGateEnabled} />
            <span class="text-sm text-slate-500 dark:text-slate-400">dBTP</span>
          </div>
        </div>
      </div>

      <div class="rounded-lg border border-slate-200 p-4 dark:border-slate-800">
        <Toggle
          bind:checked={settings.verificationImageQualityGateEnabled}
          label="Image structural quality (SSIM)"
          hint="Photo/image jobs only: compares the re-encoded still to the original with FFmpeg's ssim filter and fails when structural similarity drops below the floor. Runs an extra pass, so it's off by default."
        />
        <div class="mt-4 max-w-[16rem]" class:opacity-50={!settings.verificationImageQualityGateEnabled}>
          <label class="label" for="image-ssim-floor">Minimum SSIM <InfoTip text="Structural similarity, 0–1 where 1 is identical. 0.95 is a conservative default." /></label>
          <input id="image-ssim-floor" class="input" type="number" step="0.01" min="0" max="1" bind:value={settings.verificationMinimumImageSsim} disabled={!settings.verificationImageQualityGateEnabled} />
        </div>
      </div>

      <div class="rounded-lg border border-slate-200 p-4 dark:border-slate-800">
        <Toggle
          bind:checked={settings.verificationImageMetadataGateEnabled}
          label="Preserve image EXIF/ICC metadata"
          hint="Photo/image jobs only: fails the job when the re-encode drops the original's embedded ICC colour profile or EXIF (reads both with exiftool). Only flags loss — an output may gain metadata."
        />
        <p class="mt-3 text-xs text-slate-400">No threshold — it simply requires the original's colour profile and EXIF to survive.</p>
      </div>
    </div>
  </div>

  <div class="card p-5">
    <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">Replacement</h2>
    <p class="mb-4 text-xs text-slate-500 dark:text-slate-400">
      How a verified output takes the place of your original, and how long the original is kept afterwards.
    </p>
    <div class="max-w-2xl">
      <Toggle
        bind:checked={settings.dryRunMode}
        label="Dry-run mode"
        hint="Scan, queue, transcode, and verify normally, but never replace originals or purge quarantined originals. Verified outputs stop at Ready to replace for review."
      />
    </div>
    <div class="mt-5 max-w-2xl border-t border-slate-200 pt-5 dark:border-slate-800">
      <Toggle
        bind:checked={settings.replacementAllowCrossFilesystem}
        label="Allow cross-filesystem replacement"
        hint="Falls back to copy-plus-delete instead of an atomic move. Off is safer; enable only for intentional split-mount layouts."
      />
    </div>
    <div class="mt-5 max-w-[16rem] border-t border-slate-200 pt-5 dark:border-slate-800">
      <label class="label" for="quarantine-retention">Quarantine retention <InfoTip text="How long quarantined originals are kept before they are purged to free space. 0 keeps them indefinitely (roll back any time)." /></label>
      <div class="flex items-center gap-2">
        <input id="quarantine-retention" class="input" type="number" min="0" step="1" bind:value={settings.replacementQuarantineRetentionDays} />
        <span class="text-sm text-slate-500 dark:text-slate-400">days</span>
      </div>
    </div>
  </div>

  <div class="flex items-center gap-3">
    <button class="btn btn-primary" onclick={save} disabled={saving}>{saving ? 'Saving…' : 'Save settings'}</button>
    {#if message}<span class="text-sm text-emerald-600 dark:text-emerald-400">{message}</span>{/if}
    <span class="text-xs text-slate-400">Saves every option on this tab. Connections and notifications save on their own.</span>
  </div>
  </div>
  {/if}

  {#if activeTab === 'connections'}
  <div class="space-y-5">
    <!-- Media servers (Plex/Jellyfin/Emby): playback-aware pause + post-replacement re-scan. -->
    <div class="card p-5">
      <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">Media servers</h2>
      <p class="mb-4 text-xs text-slate-500 dark:text-slate-400">
        Connect Plex, Jellyfin, or Emby. While an enabled server is streaming, new jobs pause so transcodes
        never compete with playback (running jobs are never interrupted, and an unreachable server never pauses
        the queue). Connected servers are also asked to re-scan a title after a verified replacement.
      </p>

      {#if watcherError}
        <div class="mb-3 rounded border border-red-300 p-2 text-sm text-red-700 dark:border-red-800 dark:text-red-400">{watcherError}</div>
      {/if}

      {#if watchers.length > 0}
        <ul class="mb-4 divide-y divide-slate-100 dark:divide-slate-800">
          {#each watchers as w (w.id)}
            <li class="flex flex-wrap items-center gap-x-3 gap-y-2 py-2">
              <span class="badge bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300">{w.type}</span>
              <div class="min-w-0 flex-1">
                <div class="truncate text-sm font-medium text-slate-700 dark:text-slate-200">{w.name}</div>
                <div class="truncate font-mono text-[11px] text-slate-400" title={w.baseUrl}>{w.baseUrl}</div>
              </div>
              <div class="flex flex-wrap items-center gap-2">
                {#if !w.enabled}<span class="badge bg-slate-100 text-slate-400 dark:bg-slate-800 dark:text-slate-500">disabled</span>{/if}
                {#if w.refreshOnReplace}<span class="badge bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400" title="Re-scans this server after a verified replacement.">refresh</span>{/if}
                {#if !w.hasToken}<span class="badge bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300" title="No token set — Optimisarr cannot query this server.">no token</span>{/if}
                <button class="btn btn-ghost px-2 py-1 text-xs" onclick={() => startEdit(w)}>Edit</button>
                <button class="btn btn-ghost px-2 py-1 text-xs text-red-600 dark:text-red-400" onclick={() => deleteWatcher(w)}>Remove</button>
              </div>
            </li>
          {/each}
        </ul>
      {:else}
        <p class="mb-4 text-sm text-slate-400">No media servers yet. Add one to pause while you stream and auto-refresh after replacements.</p>
      {/if}

      <div class="rounded-lg border border-slate-200 p-4 dark:border-slate-800">
        <h3 class="mb-3 text-sm font-semibold text-slate-700 dark:text-slate-200">
          {editingId === null ? 'Add a media server' : 'Edit media server'}
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
            {#if watcherDraft.type === 'Plex'}
              <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">Or sign in and pick a server below — it fills the URL for you.</p>
            {/if}
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
            {#if plexServers && plexServers.length}
              <ul class="mt-2 divide-y divide-slate-100 rounded-md border border-slate-200 dark:divide-slate-800 dark:border-slate-700">
                {#each plexServers as server}
                  <li>
                    <button
                      class="flex w-full items-center justify-between gap-3 px-3 py-2 text-left text-sm hover:bg-slate-50 dark:hover:bg-slate-800/50"
                      onclick={() => selectPlexServer(server)}
                    >
                      <span class="min-w-0">
                        <span class="font-medium text-slate-700 dark:text-slate-200">{server.name}</span>
                        <span class="block truncate font-mono text-[11px] text-slate-400">{server.uri}</span>
                      </span>
                      <span class="badge flex-shrink-0 {server.local ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400' : 'bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400'}">
                        {server.local ? 'local' : 'remote'}
                      </span>
                    </button>
                  </li>
                {/each}
              </ul>
            {/if}
          </div>
        </div>
        <div class="mt-3 grid max-w-2xl gap-3">
          <Toggle bind:checked={watcherDraft.enabled} label="Pause while streaming" hint="Hold new jobs while this server has active playback." />
          <Toggle bind:checked={watcherDraft.refreshOnReplace} label="Refresh after replacements" hint="Ask this server to re-scan the title after a verified replacement or rollback." />
        </div>
        {#if testResult}
          <p class="mt-3 text-sm {testResult.ok ? 'text-emerald-600 dark:text-emerald-400' : 'text-red-600 dark:text-red-400'}">
            {#if testResult.ok}
              ✓ Connected to {testResult.serverName}{testResult.version ? ` (v${testResult.version})` : ''}
            {:else}
              ✗ {testResult.error}
            {/if}
          </p>
        {/if}
        <div class="mt-4 flex items-center gap-2">
          <button class="btn btn-primary px-3 py-1 text-sm" onclick={saveWatcher} disabled={savingWatcher}>
            {savingWatcher ? 'Saving…' : editingId === null ? 'Add media server' : 'Save changes'}
          </button>
          <button
            class="btn px-3 py-1 text-sm"
            onclick={testConnection}
            disabled={testing || (!watcherDraft.baseUrl.trim())}
            title="Check the URL and token reach the server"
          >
            {testing ? 'Testing…' : 'Test connection'}
          </button>
          {#if editingId !== null}
            <button class="btn btn-ghost px-3 py-1 text-sm" onclick={startAdd} disabled={savingWatcher}>Cancel</button>
          {/if}
        </div>
      </div>
    </div>

    <!-- Download managers (Sonarr/Radarr): hold files back while an import is in progress. -->
    <div class="card p-5">
      <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">Download managers</h2>
      <p class="mb-4 text-xs text-slate-500 dark:text-slate-400">
        Connect Sonarr or Radarr. While one is importing into a title's folder, files there are held back from
        queueing so a transcode never fights an import; they become eligible again on the next enqueue once the
        import settles. An unreachable manager never blocks the queue.
      </p>

      {#if arrError}
        <div class="mb-3 rounded border border-red-300 p-2 text-sm text-red-700 dark:border-red-800 dark:text-red-400">{arrError}</div>
      {/if}

      {#if arrs.length > 0}
        <ul class="mb-4 divide-y divide-slate-100 dark:divide-slate-800">
          {#each arrs as c (c.id)}
            <li class="flex flex-wrap items-center gap-x-3 gap-y-2 py-2">
              <span class="badge bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300">{c.type}</span>
              <div class="min-w-0 flex-1">
                <div class="truncate text-sm font-medium text-slate-700 dark:text-slate-200">{c.name}</div>
                <div class="truncate font-mono text-[11px] text-slate-400" title={c.baseUrl}>{c.baseUrl}</div>
              </div>
              <div class="flex flex-wrap items-center gap-2">
                {#if !c.enabled}<span class="badge bg-slate-100 text-slate-400 dark:bg-slate-800 dark:text-slate-500">disabled</span>{/if}
                {#if !c.hasApiKey}<span class="badge bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300" title="No API key set — Optimisarr cannot query this manager.">no key</span>{/if}
                <button class="btn btn-ghost px-2 py-1 text-xs" onclick={() => startEditArr(c)}>Edit</button>
                <button class="btn btn-ghost px-2 py-1 text-xs text-red-600 dark:text-red-400" onclick={() => deleteArr(c)}>Remove</button>
              </div>
            </li>
          {/each}
        </ul>
      {:else}
        <p class="mb-4 text-sm text-slate-400">No download managers yet. Add Sonarr or Radarr to avoid optimising files mid-import.</p>
      {/if}

      <div class="rounded-lg border border-slate-200 p-4 dark:border-slate-800">
        <h3 class="mb-3 text-sm font-semibold text-slate-700 dark:text-slate-200">
          {editingArrId === null ? 'Add a download manager' : 'Edit download manager'}
        </h3>
        <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
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
        <div class="mt-3">
          <Toggle bind:checked={arrDraft.enabled} label="Enabled" hint="Query this manager for in-progress imports before queueing." />
        </div>
        <div class="mt-4 flex items-center gap-2">
          <button class="btn btn-primary px-3 py-1 text-sm" onclick={saveArr} disabled={savingArr}>
            {savingArr ? 'Saving…' : editingArrId === null ? 'Add download manager' : 'Save changes'}
          </button>
          {#if editingArrId !== null}
            <button class="btn btn-ghost px-3 py-1 text-sm" onclick={startAddArr} disabled={savingArr}>Cancel</button>
          {/if}
        </div>
      </div>
    </div>
  </div>
  {/if}

  {#if activeTab === 'notifications'}
  <div class="card p-5">
    <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">Notifications</h2>
    <p class="mb-4 text-xs text-slate-500 dark:text-slate-400">
      POST to a webhook, Discord channel, ntfy topic, or Apprise endpoint when a file is replaced or
      a job fails. Delivery is best-effort and never affects processing.
    </p>

    {#if targetError}
      <div class="mb-3 rounded border border-red-300 p-2 text-sm text-red-700 dark:border-red-800 dark:text-red-400">{targetError}</div>
    {/if}

    {#if targets.length > 0}
      <ul class="mb-4 divide-y divide-slate-100 dark:divide-slate-800">
        {#each targets as t (t.id)}
          <li class="flex flex-wrap items-center gap-x-3 gap-y-2 py-2">
            <span class="badge bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300">{t.type}</span>
            <div class="min-w-0 flex-1">
              <div class="truncate text-sm font-medium text-slate-700 dark:text-slate-200">{t.name}</div>
              <div class="truncate font-mono text-[11px] text-slate-400" title={t.url}>{t.url}</div>
            </div>
            <div class="flex flex-wrap items-center gap-2">
              {#if !t.enabled}<span class="badge bg-slate-100 text-slate-400 dark:bg-slate-800 dark:text-slate-500">disabled</span>{/if}
              {#if t.notifyOnReplacement}<span class="badge bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400">replaced</span>{/if}
              {#if t.notifyOnFailure}<span class="badge bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300">failed</span>{/if}
              <button class="btn btn-ghost px-2 py-1 text-xs" onclick={() => startEditTarget(t)}>Edit</button>
              <button class="btn btn-ghost px-2 py-1 text-xs text-red-600 dark:text-red-400" onclick={() => deleteTarget(t)}>Remove</button>
            </div>
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
      <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
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
          <input
            id="target-url"
            class="input"
            placeholder={targetDraft.type === 'Discord' ? 'https://discord.com/api/webhooks/…' : 'https://ntfy.sh/my-topic'}
            bind:value={targetDraft.url}
          />
          {#if targetDraft.type === 'Discord'}
            <p class="mt-1 text-[11px] text-slate-400">Discord channel → Edit → Integrations → Webhooks → Copy URL. No token needed — the URL carries the secret.</p>
          {/if}
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
      <div class="mt-3 grid max-w-2xl gap-3">
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
  {/if}

  {#if activeTab === 'tools'}
    <ToolsPanel />
  {/if}

  {#if activeTab === 'backup'}
  <div class="card p-5">
    <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">Backup &amp; restore</h2>
    <p class="mb-4 max-w-3xl text-xs text-slate-500 dark:text-slate-400">
      Export your settings, libraries, media-server and download-manager connections, and notification targets
      to a JSON file, or import one. The file includes provider tokens and API keys, so store it securely and never
      commit or share it. It does not include media, jobs, replacements, quarantine, or rollback history. Importing
      merges into your current config (matching libraries by path and connections/targets by name) and never deletes anything.
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
{/if}
