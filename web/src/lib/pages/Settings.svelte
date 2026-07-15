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
  // `t` is aliased to `tr` here because this component already uses `t`/`c`/`w` as local
  // names for notification-target, connection, and watcher records.
  import { i18n, t as tr } from '../i18n/i18n.svelte'
  import { router } from '../stores/ui.svelte'
  import Toggle from '../components/Toggle.svelte'
  import InfoTip from '../components/InfoTip.svelte'
  import Banner from '../components/Banner.svelte'
  import ToolsPanel from '../components/ToolsPanel.svelte'

  // Settings is split into tabs so each concern is found quickly and Tools lives here
  // rather than in its own sidebar entry. The General tab holds the core settings the
  // single "Save settings" button persists together; the rest manage their own records.
  type TabKey = 'general' | 'connections' | 'notifications' | 'tools' | 'backup'
  let tabs: { key: TabKey; label: string }[] = $derived([
    { key: 'general', label: i18n.m.settings.tab_general },
    { key: 'connections', label: i18n.m.settings.tab_connections },
    { key: 'notifications', label: i18n.m.settings.tab_notifications },
    { key: 'tools', label: i18n.m.settings.tab_tools },
    { key: 'backup', label: i18n.m.settings.tab_backup },
  ])
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
      targetError = err instanceof Error ? err.message : i18n.m.settings.error_load_targets
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
      targetError = err instanceof Error ? err.message : i18n.m.settings.error_save_target
    } finally {
      savingTarget = false
    }
  }

  async function deleteTarget(t: NotificationTarget) {
    if (!confirm(tr(i18n.m.settings.confirm_remove_target, { name: t.name }))) return
    targetError = null
    try {
      await api.deleteNotificationTarget(t.id)
      if (editingTargetId === t.id) startAddTarget()
      await loadTargets()
    } catch (err) {
      targetError = err instanceof Error ? err.message : i18n.m.settings.error_remove_target
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
      arrError = err instanceof Error ? err.message : i18n.m.settings.error_load_arrs
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
      arrError = err instanceof Error ? err.message : i18n.m.settings.error_save_arr
    } finally {
      savingArr = false
    }
  }

  async function deleteArr(c: ArrConnection) {
    if (!confirm(tr(i18n.m.settings.confirm_remove_arr, { type: c.type, name: c.name }))) return
    arrError = null
    try {
      await api.deleteArrConnection(c.id)
      if (editingArrId === c.id) startAddArr()
      await loadArrs()
    } catch (err) {
      arrError = err instanceof Error ? err.message : i18n.m.settings.error_remove_arr
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
    if (!connectCancelled) connectMessage = i18n.m.settings.timed_out
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
    connectMessage = i18n.m.settings.connect_plex_opening
    try {
      const start = await api.plexConnectStart()
      window.open(start.authUrl, '_blank', 'noopener')
      connectMessage = i18n.m.settings.connect_plex_approve
      const token = await pollForToken(() => api.plexConnectPoll(start.id))
      if (token) {
        watcherDraft.apiToken = token
        connectMessage = i18n.m.settings.connect_plex_finding
        try {
          plexServers = await api.plexServers(token)
          connectMessage = plexServers.length
            ? i18n.m.settings.connect_plex_pick
            : i18n.m.settings.connect_plex_none
        } catch {
          connectMessage = i18n.m.settings.connect_plex_manual
        }
      }
    } catch (err) {
      watcherError = err instanceof Error ? err.message : i18n.m.settings.error_plex
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
    connectMessage = tr(i18n.m.settings.connect_plex_selected, { name: server.name })
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
      testResult = { ok: false, serverName: null, version: null, error: err instanceof Error ? err.message : i18n.m.settings.error_test }
    } finally {
      testing = false
    }
  }

  async function connectJellyfin() {
    const baseUrl = watcherDraft.baseUrl.trim()
    if (!baseUrl) {
      watcherError = i18n.m.settings.error_jellyfin_url
      return
    }
    connecting = true
    connectMessage = i18n.m.settings.connect_jellyfin_starting
    try {
      const start = await api.jellyfinConnectStart(baseUrl)
      jellyfinCode = start.code
      connectMessage = i18n.m.settings.connect_jellyfin_code
      const token = await pollForToken(() => api.jellyfinConnectPoll(baseUrl, start.secret))
      if (token) {
        watcherDraft.apiToken = token
        jellyfinCode = null
        connectMessage = i18n.m.settings.connect_jellyfin_done
      }
    } catch (err) {
      watcherError = err instanceof Error ? err.message : i18n.m.settings.error_quick_connect
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
      watcherError = err instanceof Error ? err.message : i18n.m.settings.error_load_watchers
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
      watcherError = err instanceof Error ? err.message : i18n.m.settings.error_save_watcher
    } finally {
      savingWatcher = false
    }
  }

  async function deleteWatcher(w: ActivityWatcher) {
    if (!confirm(tr(i18n.m.settings.confirm_remove_watcher, { name: w.name }))) return
    watcherError = null
    try {
      await api.deleteActivityWatcher(w.id)
      if (editingId === w.id) startAdd()
      await loadWatchers()
    } catch (err) {
      watcherError = err instanceof Error ? err.message : i18n.m.settings.error_remove_watcher
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
    verificationAudioLoudnessGateEnabled: false,
    verificationMaxLoudnessDriftLufs: 1,
    verificationAudioClippingGateEnabled: false,
    verificationMaxTruePeakDbtp: 0,
    verificationImageQualityGateEnabled: true,
    verificationMinimumImageSsim: 0.95,
    verificationImageMetadataGateEnabled: true,
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
      error = err instanceof Error ? err.message : i18n.m.settings.error_load
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
        verificationMaxLoudnessDriftLufs: Math.max(0, Number(settings.verificationMaxLoudnessDriftLufs) || 0),
        verificationMaxTruePeakDbtp: Number(settings.verificationMaxTruePeakDbtp) || 0,
        verificationMinimumImageSsim: Math.min(1, Math.max(0, Number(settings.verificationMinimumImageSsim) || 0)),
        minFreeDiskBytes: gibToBytes(minFreeDiskGiB),
      })
      minFreeDiskGiB = bytesToGiB(settings.minFreeDiskBytes)
      message = i18n.m.settings.saved
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.settings.error_save
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
      backupMessage = i18n.m.settings.export_done
    } catch (err) {
      backupError = err instanceof Error ? err.message : i18n.m.settings.error_export
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
      backupMessage = tr(i18n.m.settings.import_done, {
        libraries: result.librariesCreated + result.librariesUpdated,
        watchers: result.watchersCreated + result.watchersUpdated,
        targets: result.targetsCreated + result.targetsUpdated,
        arrs: result.arrConnectionsCreated + result.arrConnectionsUpdated,
        settings: result.settingsApplied,
      })
      await load()
      await loadWatchers()
      await loadTargets()
      await loadArrs()
    } catch (err) {
      backupError = err instanceof Error ? err.message : i18n.m.settings.error_import
    } finally {
      importing = false
    }
  }
</script>

<header class="mb-6">
  <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">{i18n.m.nav.settings}</h1>
  <p class="text-sm text-slate-500 dark:text-slate-400">{i18n.m.settings.subtitle}</p>
</header>

{#if error}
  <Banner kind="error" class="mb-4">{error}</Banner>
{/if}

{#if loading}
  <div class="card p-8 text-center text-slate-400">{i18n.m.common.loading_short}</div>
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
    <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">{i18n.m.nav.queue}</h2>
    <p class="mb-4 text-xs text-slate-500 dark:text-slate-400">
      {i18n.m.settings.queue_desc}
    </p>
    <div class="grid gap-5 sm:grid-cols-2 lg:grid-cols-4">
      <div>
        <label class="label" for="max-jobs">{i18n.m.settings.max_jobs} <InfoTip text={i18n.m.settings.max_jobs_tip} /></label>
        <input id="max-jobs" class="input" type="number" min="1" bind:value={settings.maxConcurrentJobs} />
      </div>

      <div>
        <label class="label" for="encoder-mode">{i18n.m.settings.encoder_mode} <InfoTip text={i18n.m.settings.encoder_mode_tip} /></label>
        <select id="encoder-mode" class="input" bind:value={settings.encoderMode}>
          <option value="Auto">Auto</option>
          <option value="Cpu">CPU</option>
          <option value="NvidiaNvenc">NVIDIA NVENC</option>
          <option value="IntelQsv">Intel QSV</option>
          <option value="Vaapi">VAAPI</option>
        </select>
      </div>

      <div>
        <label class="label" for="cpu-threads">{i18n.m.settings.cpu_threads} <InfoTip text={i18n.m.settings.cpu_threads_tip} /></label>
        <input id="cpu-threads" class="input" type="number" min="0" bind:value={settings.cpuThreadLimit} />
      </div>

      <div>
        <label class="label" for="scan-interval">{i18n.m.settings.scan_interval} <InfoTip text={i18n.m.settings.scan_interval_tip} /></label>
        <div class="flex items-center gap-2">
          <input id="scan-interval" class="input" type="number" min="1" step="1" bind:value={settings.libraryScanIntervalHours} />
          <span class="text-sm text-slate-500 dark:text-slate-400">{i18n.m.settings.hours}</span>
        </div>
      </div>

      <div>
        <label class="label" for="free-disk">{i18n.m.settings.free_disk} <InfoTip text={tr(i18n.m.settings.free_disk_tip, { size: formatSize(gibToBytes(minFreeDiskGiB)) })} /></label>
        <div class="flex items-center gap-2">
          <input id="free-disk" class="input" type="number" min="0" step="1" bind:value={minFreeDiskGiB} />
          <span class="text-sm text-slate-500 dark:text-slate-400">{i18n.m.settings.gib}</span>
        </div>
      </div>
    </div>

    <div class="mt-5 border-t border-slate-200 pt-5 dark:border-slate-800">
      <Toggle
        bind:checked={settings.hardwareDecode}
        label={i18n.m.settings.hardware_decode}
        hint={i18n.m.settings.hardware_decode_hint}
      />
      <p class="mt-3 text-xs text-slate-500 dark:text-slate-400">
        {i18n.m.settings.auto_run_before}<button class="text-cyan-600 hover:underline dark:text-cyan-400" onclick={() => router.go('/libraries')}>{i18n.m.nav.libraries}</button>{i18n.m.settings.auto_run_after}
      </p>
    </div>
  </div>

  <div class="card p-5">
    <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">{i18n.m.settings.gates_title}</h2>
    <p class="mb-4 text-xs text-slate-500 dark:text-slate-400">
      {i18n.m.settings.gates_desc}
    </p>

    <div class="grid gap-4 lg:grid-cols-2">
      <div class="rounded-lg border border-slate-200 p-4 dark:border-slate-800">
        <h3 class="mb-3 text-sm font-semibold text-slate-700 dark:text-slate-200">{i18n.m.settings.always_on}</h3>
        <div class="max-w-[16rem]">
          <label class="label" for="duration-tolerance">{i18n.m.settings.duration_tolerance} <InfoTip text={i18n.m.settings.duration_tolerance_tip} /></label>
          <div class="flex items-center gap-2">
            <input id="duration-tolerance" class="input" type="number" min="0" step="0.1" bind:value={settings.verificationDurationTolerancePercent} />
            <span class="text-sm text-slate-500 dark:text-slate-400">%</span>
          </div>
        </div>
        <div class="mt-4 grid gap-3 border-t border-slate-200 pt-4 dark:border-slate-800">
          <Toggle bind:checked={settings.verificationRequireAudioRetained} label={i18n.m.settings.require_audio} />
          <Toggle bind:checked={settings.verificationRequireSubtitlesRetained} label={i18n.m.settings.require_subtitles} />
          <Toggle bind:checked={settings.verificationRequireSizeReduction} label={i18n.m.settings.require_smaller} />
        </div>
      </div>

      <div class="rounded-lg border border-slate-200 p-4 dark:border-slate-800">
        <Toggle
          bind:checked={settings.verificationAudioLoudnessGateEnabled}
          label={i18n.m.settings.loudness_label}
          hint={i18n.m.settings.loudness_hint}
        />
        <div class="mt-4 max-w-[16rem]" class:opacity-50={!settings.verificationAudioLoudnessGateEnabled}>
          <label class="label" for="loudness-drift">{i18n.m.settings.loudness_max}</label>
          <div class="flex items-center gap-2">
            <input id="loudness-drift" class="input" type="number" min="0" step="0.1" bind:value={settings.verificationMaxLoudnessDriftLufs} disabled={!settings.verificationAudioLoudnessGateEnabled} />
            <span class="text-sm text-slate-500 dark:text-slate-400">{i18n.m.settings.lu}</span>
          </div>
        </div>
      </div>

      <div class="rounded-lg border border-slate-200 p-4 dark:border-slate-800">
        <Toggle
          bind:checked={settings.verificationAudioClippingGateEnabled}
          label={i18n.m.settings.clipping_label}
          hint={i18n.m.settings.clipping_hint}
        />
        <div class="mt-4 max-w-[16rem]" class:opacity-50={!settings.verificationAudioClippingGateEnabled}>
          <label class="label" for="true-peak-ceiling">{i18n.m.settings.true_peak} <InfoTip text={i18n.m.settings.true_peak_tip} /></label>
          <div class="flex items-center gap-2">
            <input id="true-peak-ceiling" class="input" type="number" step="0.1" bind:value={settings.verificationMaxTruePeakDbtp} disabled={!settings.verificationAudioClippingGateEnabled} />
            <span class="text-sm text-slate-500 dark:text-slate-400">{i18n.m.settings.dbtp}</span>
          </div>
        </div>
      </div>

      <div class="rounded-lg border border-slate-200 p-4 dark:border-slate-800">
        <Toggle
          bind:checked={settings.verificationImageQualityGateEnabled}
          label={i18n.m.settings.ssim_label}
          hint={i18n.m.settings.ssim_hint}
        />
        <div class="mt-4 max-w-[16rem]" class:opacity-50={!settings.verificationImageQualityGateEnabled}>
          <label class="label" for="image-ssim-floor">{i18n.m.settings.ssim_min} <InfoTip text={i18n.m.settings.ssim_min_tip} /></label>
          <input id="image-ssim-floor" class="input" type="number" step="0.01" min="0" max="1" bind:value={settings.verificationMinimumImageSsim} disabled={!settings.verificationImageQualityGateEnabled} />
        </div>
      </div>

      <div class="rounded-lg border border-slate-200 p-4 dark:border-slate-800">
        <Toggle
          bind:checked={settings.verificationImageMetadataGateEnabled}
          label={i18n.m.settings.exif_label}
          hint={i18n.m.settings.exif_hint}
        />
        <p class="mt-3 text-xs text-slate-400">{i18n.m.settings.exif_note}</p>
      </div>
    </div>
  </div>

  <div class="card p-5">
    <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">{i18n.m.settings.replacement_title}</h2>
    <p class="mb-4 text-xs text-slate-500 dark:text-slate-400">
      {i18n.m.settings.replacement_desc}
    </p>
    <div class="max-w-2xl">
      <Toggle
        bind:checked={settings.dryRunMode}
        label={i18n.m.settings.dry_run}
        hint={i18n.m.settings.dry_run_hint}
      />
    </div>
    <div class="mt-5 max-w-2xl border-t border-slate-200 pt-5 dark:border-slate-800">
      <Toggle
        bind:checked={settings.replacementAllowCrossFilesystem}
        label={i18n.m.settings.cross_fs}
        hint={i18n.m.settings.cross_fs_hint}
      />
    </div>
    <div class="mt-5 max-w-[16rem] border-t border-slate-200 pt-5 dark:border-slate-800">
      <label class="label" for="quarantine-retention">{i18n.m.settings.quarantine_retention} <InfoTip text={i18n.m.settings.quarantine_retention_tip} /></label>
      <div class="flex items-center gap-2">
        <input id="quarantine-retention" class="input" type="number" min="0" step="1" bind:value={settings.replacementQuarantineRetentionDays} />
        <span class="text-sm text-slate-500 dark:text-slate-400">{i18n.m.settings.days}</span>
      </div>
    </div>
  </div>

  <div class="flex items-center gap-3">
    <button class="btn btn-primary" onclick={save} disabled={saving}>{saving ? i18n.m.settings.saving : i18n.m.settings.save_settings}</button>
    {#if message}<span class="text-sm text-emerald-600 dark:text-emerald-400">{message}</span>{/if}
    <span class="text-xs text-slate-400">{i18n.m.settings.save_note}</span>
  </div>
  </div>
  {/if}

  {#if activeTab === 'connections'}
  <div class="space-y-5">
    <!-- Media servers (Plex/Jellyfin/Emby): playback-aware pause + post-replacement re-scan. -->
    <div class="card p-5">
      <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">{i18n.m.settings.media_servers}</h2>
      <p class="mb-4 text-xs text-slate-500 dark:text-slate-400">
        {i18n.m.settings.media_servers_desc}
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
                {#if !w.enabled}<span class="badge bg-slate-100 text-slate-400 dark:bg-slate-800 dark:text-slate-500">{i18n.m.settings.disabled}</span>{/if}
                {#if w.refreshOnReplace}<span class="badge bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400" title={i18n.m.settings.badge_refresh_title}>{i18n.m.settings.badge_refresh}</span>{/if}
                {#if !w.hasToken}<span class="badge bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300" title={i18n.m.settings.badge_no_token_title}>{i18n.m.settings.badge_no_token}</span>{/if}
                <button class="btn btn-ghost px-2 py-1 text-xs" onclick={() => startEdit(w)}>{i18n.m.settings.edit}</button>
                <button class="btn btn-ghost px-2 py-1 text-xs text-red-600 dark:text-red-400" onclick={() => deleteWatcher(w)}>{i18n.m.settings.remove}</button>
              </div>
            </li>
          {/each}
        </ul>
      {:else}
        <p class="mb-4 text-sm text-slate-400">{i18n.m.settings.media_servers_empty}</p>
      {/if}

      <div class="rounded-lg border border-slate-200 p-4 dark:border-slate-800">
        <h3 class="mb-3 text-sm font-semibold text-slate-700 dark:text-slate-200">
          {editingId === null ? i18n.m.settings.add_media_server : i18n.m.settings.edit_media_server}
        </h3>
        <div class="grid gap-3 sm:grid-cols-2">
          <div>
            <label class="label" for="watcher-name">{i18n.m.settings.name}</label>
            <input id="watcher-name" class="input" placeholder={i18n.m.settings.media_server_name_ph} bind:value={watcherDraft.name} />
          </div>
          <div>
            <label class="label" for="watcher-type">{i18n.m.settings.type}</label>
            <select id="watcher-type" class="input" bind:value={watcherDraft.type}>
              {#each watcherTypes as t}<option value={t}>{t}</option>{/each}
            </select>
          </div>
          <div>
            <label class="label" for="watcher-url">{i18n.m.settings.base_url}</label>
            <input id="watcher-url" class="input" placeholder="http://192.168.1.10:32400" bind:value={watcherDraft.baseUrl} />
            {#if watcherDraft.type === 'Plex'}
              <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">{i18n.m.settings.plex_pick_hint}</p>
            {/if}
          </div>
          <div>
            <label class="label" for="watcher-token">
              {watcherDraft.type === 'Plex' ? i18n.m.settings.plex_token : i18n.m.settings.api_key}
            </label>
            <div class="flex items-center gap-2">
              <input
                id="watcher-token"
                class="input"
                type="password"
                placeholder={editingId === null ? '' : i18n.m.settings.keep_current}
                bind:value={watcherDraft.apiToken}
              />
              {#if watcherDraft.type !== 'Emby'}
                {#if connecting}
                  <button class="btn btn-ghost whitespace-nowrap px-3 py-1 text-xs" onclick={resetConnect}>{i18n.m.common.cancel}</button>
                {:else}
                  <button class="btn whitespace-nowrap px-3 py-1 text-xs" onclick={connect}>
                    {watcherDraft.type === 'Plex' ? i18n.m.settings.sign_in_plex : i18n.m.settings.quick_connect}
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
                        {server.local ? i18n.m.settings.badge_local : i18n.m.settings.badge_remote}
                      </span>
                    </button>
                  </li>
                {/each}
              </ul>
            {/if}
          </div>
        </div>
        <div class="mt-3 grid max-w-2xl gap-3">
          <Toggle bind:checked={watcherDraft.enabled} label={i18n.m.settings.pause_streaming} hint={i18n.m.settings.pause_streaming_hint} />
          <Toggle bind:checked={watcherDraft.refreshOnReplace} label={i18n.m.settings.refresh_replace} hint={i18n.m.settings.refresh_replace_hint} />
        </div>
        {#if testResult}
          <p class="mt-3 text-sm {testResult.ok ? 'text-emerald-600 dark:text-emerald-400' : 'text-red-600 dark:text-red-400'}">
            {#if testResult.ok}
              {tr(i18n.m.settings.test_ok, { name: testResult.serverName ?? '' })}{testResult.version ? tr(i18n.m.settings.test_ok_version, { version: testResult.version }) : ''}
            {:else}
              {tr(i18n.m.settings.test_fail, { error: testResult.error ?? '' })}
            {/if}
          </p>
        {/if}
        <div class="mt-4 flex items-center gap-2">
          <button class="btn btn-primary px-3 py-1 text-sm" onclick={saveWatcher} disabled={savingWatcher}>
            {savingWatcher ? i18n.m.settings.saving : editingId === null ? i18n.m.settings.add_media_server_btn : i18n.m.settings.save_changes}
          </button>
          <button
            class="btn px-3 py-1 text-sm"
            onclick={testConnection}
            disabled={testing || (!watcherDraft.baseUrl.trim())}
            title={i18n.m.settings.test_connection_title}
          >
            {testing ? i18n.m.settings.testing : i18n.m.settings.test_connection}
          </button>
          {#if editingId !== null}
            <button class="btn btn-ghost px-3 py-1 text-sm" onclick={startAdd} disabled={savingWatcher}>{i18n.m.common.cancel}</button>
          {/if}
        </div>
      </div>
    </div>

    <!-- Download managers (Sonarr/Radarr): hold files back while an import is in progress. -->
    <div class="card p-5">
      <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">{i18n.m.settings.download_managers}</h2>
      <p class="mb-4 text-xs text-slate-500 dark:text-slate-400">
        {i18n.m.settings.download_managers_desc}
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
                {#if !c.enabled}<span class="badge bg-slate-100 text-slate-400 dark:bg-slate-800 dark:text-slate-500">{i18n.m.settings.disabled}</span>{/if}
                {#if !c.hasApiKey}<span class="badge bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300" title={i18n.m.settings.badge_no_key_title}>{i18n.m.settings.badge_no_key}</span>{/if}
                <button class="btn btn-ghost px-2 py-1 text-xs" onclick={() => startEditArr(c)}>{i18n.m.settings.edit}</button>
                <button class="btn btn-ghost px-2 py-1 text-xs text-red-600 dark:text-red-400" onclick={() => deleteArr(c)}>{i18n.m.settings.remove}</button>
              </div>
            </li>
          {/each}
        </ul>
      {:else}
        <p class="mb-4 text-sm text-slate-400">{i18n.m.settings.download_managers_empty}</p>
      {/if}

      <div class="rounded-lg border border-slate-200 p-4 dark:border-slate-800">
        <h3 class="mb-3 text-sm font-semibold text-slate-700 dark:text-slate-200">
          {editingArrId === null ? i18n.m.settings.add_download_manager : i18n.m.settings.edit_download_manager}
        </h3>
        <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <div>
            <label class="label" for="arr-name">{i18n.m.settings.name}</label>
            <input id="arr-name" class="input" placeholder={i18n.m.settings.arr_name_ph} bind:value={arrDraft.name} />
          </div>
          <div>
            <label class="label" for="arr-type">{i18n.m.settings.type}</label>
            <select id="arr-type" class="input" bind:value={arrDraft.type}>
              {#each arrTypes as t}<option value={t}>{t}</option>{/each}
            </select>
          </div>
          <div>
            <label class="label" for="arr-url">{i18n.m.settings.base_url}</label>
            <input id="arr-url" class="input" placeholder="http://192.168.1.10:8989" bind:value={arrDraft.baseUrl} />
          </div>
          <div>
            <label class="label" for="arr-key">{i18n.m.settings.api_key}</label>
            <input
              id="arr-key"
              class="input"
              type="password"
              placeholder={editingArrId === null ? '' : i18n.m.settings.keep_current}
              bind:value={arrDraft.apiKey}
            />
          </div>
        </div>
        <div class="mt-3">
          <Toggle bind:checked={arrDraft.enabled} label={i18n.m.settings.enabled} hint={i18n.m.settings.arr_enabled_hint} />
        </div>
        <div class="mt-4 flex items-center gap-2">
          <button class="btn btn-primary px-3 py-1 text-sm" onclick={saveArr} disabled={savingArr}>
            {savingArr ? i18n.m.settings.saving : editingArrId === null ? i18n.m.settings.add_download_manager_btn : i18n.m.settings.save_changes}
          </button>
          {#if editingArrId !== null}
            <button class="btn btn-ghost px-3 py-1 text-sm" onclick={startAddArr} disabled={savingArr}>{i18n.m.common.cancel}</button>
          {/if}
        </div>
      </div>
    </div>
  </div>
  {/if}

  {#if activeTab === 'notifications'}
  <div class="card p-5">
    <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">{i18n.m.settings.tab_notifications}</h2>
    <p class="mb-4 text-xs text-slate-500 dark:text-slate-400">
      {i18n.m.settings.notifications_desc}
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
              {#if !t.enabled}<span class="badge bg-slate-100 text-slate-400 dark:bg-slate-800 dark:text-slate-500">{i18n.m.settings.disabled}</span>{/if}
              {#if t.notifyOnReplacement}<span class="badge bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400">{i18n.m.settings.badge_replaced}</span>{/if}
              {#if t.notifyOnFailure}<span class="badge bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300">{i18n.m.settings.badge_failed}</span>{/if}
              <button class="btn btn-ghost px-2 py-1 text-xs" onclick={() => startEditTarget(t)}>{i18n.m.settings.edit}</button>
              <button class="btn btn-ghost px-2 py-1 text-xs text-red-600 dark:text-red-400" onclick={() => deleteTarget(t)}>{i18n.m.settings.remove}</button>
            </div>
          </li>
        {/each}
      </ul>
    {:else}
      <p class="mb-4 text-sm text-slate-400">{i18n.m.settings.targets_empty}</p>
    {/if}

    <div class="rounded-lg border border-slate-200 p-4 dark:border-slate-800">
      <h3 class="mb-3 text-sm font-semibold text-slate-700 dark:text-slate-200">
        {editingTargetId === null ? i18n.m.settings.add_target : i18n.m.settings.edit_target}
      </h3>
      <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <div>
          <label class="label" for="target-name">{i18n.m.settings.name}</label>
          <input id="target-name" class="input" placeholder={i18n.m.settings.target_name_ph} bind:value={targetDraft.name} />
        </div>
        <div>
          <label class="label" for="target-type">{i18n.m.settings.type}</label>
          <select id="target-type" class="input" bind:value={targetDraft.type}>
            {#each notificationTypes as t}<option value={t}>{t}</option>{/each}
          </select>
        </div>
        <div>
          <label class="label" for="target-url">{i18n.m.settings.url}</label>
          <input
            id="target-url"
            class="input"
            placeholder={targetDraft.type === 'Discord' ? i18n.m.settings.discord_url_ph : i18n.m.settings.ntfy_url_ph}
            bind:value={targetDraft.url}
          />
          {#if targetDraft.type === 'Discord'}
            <p class="mt-1 text-[11px] text-slate-400">{i18n.m.settings.discord_hint}</p>
          {/if}
        </div>
        <div>
          <label class="label" for="target-token">{i18n.m.settings.token} <span class="text-slate-400">{i18n.m.settings.optional}</span></label>
          <input
            id="target-token"
            class="input"
            type="password"
            placeholder={editingTargetId === null ? '' : i18n.m.settings.keep_current}
            bind:value={targetDraft.token}
          />
        </div>
      </div>
      <div class="mt-3 grid max-w-2xl gap-3">
        <Toggle bind:checked={targetDraft.enabled} label={i18n.m.settings.enabled} />
        <Toggle bind:checked={targetDraft.notifyOnReplacement} label={i18n.m.settings.notify_replaced} />
        <Toggle bind:checked={targetDraft.notifyOnFailure} label={i18n.m.settings.notify_failed} />
      </div>
      <div class="mt-4 flex items-center gap-2">
        <button class="btn btn-primary px-3 py-1 text-sm" onclick={saveTarget} disabled={savingTarget}>
          {savingTarget ? i18n.m.settings.saving : editingTargetId === null ? i18n.m.settings.add_target_btn : i18n.m.settings.save_changes}
        </button>
        {#if editingTargetId !== null}
          <button class="btn btn-ghost px-3 py-1 text-sm" onclick={startAddTarget} disabled={savingTarget}>{i18n.m.common.cancel}</button>
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
    <h2 class="mb-1 font-semibold text-slate-800 dark:text-slate-100">{i18n.m.settings.backup_title}</h2>
    <p class="mb-4 max-w-3xl text-xs text-slate-500 dark:text-slate-400">
      {i18n.m.settings.backup_desc}
    </p>

    {#if backupError}
      <div class="mb-3 rounded border border-red-300 p-2 text-sm text-red-700 dark:border-red-800 dark:text-red-400">{backupError}</div>
    {/if}
    {#if backupMessage}
      <div class="mb-3 rounded border border-emerald-300 p-2 text-sm text-emerald-700 dark:border-emerald-800 dark:text-emerald-400">{backupMessage}</div>
    {/if}

    <div class="flex items-center gap-3">
      <button class="btn" onclick={exportConfig}>{i18n.m.settings.export_config}</button>
      <button class="btn" onclick={() => fileInput?.click()} disabled={importing}>
        {importing ? i18n.m.settings.importing : i18n.m.settings.import_config}
      </button>
      <input bind:this={fileInput} type="file" accept="application/json,.json" class="hidden" onchange={importConfig} />
    </div>
  </div>
  {/if}
{/if}
