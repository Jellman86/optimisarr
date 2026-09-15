<script lang="ts">
  import {
    api,
    type Settings,
    type TimedCleanupPreview,
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
  import { i18n, plural, t as tr } from '../i18n/i18n.svelte'
  import { router } from '../stores/ui.svelte'
  import { setup } from '../stores/setup.svelte'
  import Toggle from '../components/Toggle.svelte'
  import InfoTip from '../components/InfoTip.svelte'
  import Icon from '../components/Icon.svelte'
  import Banner from '../components/Banner.svelte'
  import ConfigSection from '../components/ConfigSection.svelte'
  import ToolsPanel from '../components/ToolsPanel.svelte'
  import WorkersPanel from '../components/WorkersPanel.svelte'

  // Settings is a set of rooms rather than a strip of tabs. The landing page is a grid of
  // cards, one per room, and each card reports what that room is currently set to — so
  // "is Plex still connected?" and "is anything reclaimable?" are answered without opening
  // anything. Opening a room gives that section the page to itself.
  //
  // A tab strip could not do the reporting, and its numbered sections implied a sequence
  // that never existed: nobody configures their encoder before their notifications because
  // it happens to be numbered lower.
  type RoomKey = 'encoding' | 'files' | 'servers' | 'downloads' | 'notifications' | 'workers' | 'system'

  const ROOM_PATHS: Record<RoomKey, string> = {
    encoding: 'encoding',
    files: 'files',
    servers: 'media-servers',
    downloads: 'download-managers',
    notifications: 'notifications',
    workers: 'workers',
    system: 'system',
  }

  function roomFromPath(path: string): RoomKey | null {
    // The old /tools route, and anything linking to it, lands in the room that absorbed it.
    if (path.startsWith('/tools')) return 'system'
    const tail = path.replace(/^\/settings\/?/, '')
    if (!tail) return null
    const match = (Object.entries(ROOM_PATHS) as [RoomKey, string][]).find(([, slug]) => slug === tail)
    return match ? match[0] : null
  }

  let openRoom = $derived(roomFromPath(router.path))

  function openRoomAt(key: RoomKey) {
    router.go(`/settings/${ROOM_PATHS[key]}`)
    requestAnimationFrame(() => document.getElementById('room-heading')?.focus())
  }

  function closeRoom() {
    router.go('/settings')
  }

  const notificationTypes: NotificationType[] = ['Webhook', 'Discord', 'Telegram', 'Ntfy', 'Apprise']
  const emptyTarget = (): SaveNotificationTarget => ({
    name: '', type: 'Webhook', url: '', token: '', enabled: true, notifyOnReplacement: true, notifyOnFailure: true,
  })

  let targets = $state<NotificationTarget[]>([])
  let targetError = $state<string | null>(null)
  let targetMessage = $state<string | null>(null)
  let editingTargetId = $state<number | null>(null)
  let targetDraft = $state<SaveNotificationTarget>(emptyTarget())
  let savingTarget = $state(false)
  let testingTargetId = $state<number | null>(null)
  let restartingSetup = $state(false)
  let hasStoredTelegramToken = $derived(editingTargetId !== null && targets.some(
    (target) => target.id === editingTargetId && target.type === 'Telegram' && target.hasToken,
  ))
  let telegramTokenRequired = $derived(targetDraft.type === 'Telegram' && !hasStoredTelegramToken)

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
    targetMessage = null
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

  async function testTarget(t: NotificationTarget) {
    testingTargetId = t.id
    targetError = null
    targetMessage = null
    try {
      const result = await api.testNotificationTarget(t.id)
      if (result.ok) targetMessage = i18n.m.settings.notification_test_success
      else targetError = result.error ?? i18n.m.settings.notification_test_failed
    } catch (err) {
      targetError = err instanceof Error ? err.message : i18n.m.settings.notification_test_failed
    } finally {
      testingTargetId = null
    }
  }

  async function restartSetup() {
    if (!confirm(i18n.m.settings.restart_setup_confirm)) return
    restartingSetup = true
    try {
      await setup.restart()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.settings.error_save
    } finally {
      restartingSetup = false
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
    hdrToneMapMode: 'Software',
    replacementAllowCrossFilesystem: false,
    dryRunMode: false,
    remoteWorkersEnabled: false,
    remoteWorkersAvailable: false,
    replacementQuarantineRetentionDays: 0,
  })

  // The values as the server last confirmed them. Everything the form binds to is a draft
  // over the top of this, which is what lets a row say "was 14 days", lets one field be put
  // back on its own, and lets the save bar count what it is about to write.
  //
  // It has to be a separate snapshot rather than a re-fetch: walking from Encoding to Files
  // and back must not lose an edit, and re-reading the server to find out what changed would
  // do exactly that.
  let savedSettings = $state<Settings | null>(null)
  let savedMinFreeDiskGiB = $state('10')
  let minFreeDiskGiB = $state('10')

  /** Which settings belong to which room, so a card can count its own unsaved edits. */
  const ROOM_FIELDS: Partial<Record<RoomKey, (keyof Settings)[]>> = {
    encoding: ['maxConcurrentJobs', 'encoderMode', 'cpuThreadLimit', 'libraryScanIntervalHours', 'hardwareDecode', 'hdrToneMapMode'],
    files: ['dryRunMode', 'remoteWorkersEnabled', 'replacementAllowCrossFilesystem', 'replacementQuarantineRetentionDays'],
  }

  function sameValue(a: unknown, b: unknown): boolean {
    // Number inputs hand back strings, so 5 and '5' are the same answer typed twice.
    if (typeof a === 'number' || typeof b === 'number') return Number(a) === Number(b)
    return a === b
  }

  let changedFields = $derived.by(() => {
    if (!savedSettings) return new Set<string>()
    const out = new Set<string>()
    for (const key of Object.keys(settings) as (keyof Settings)[]) {
      if (!sameValue(settings[key], savedSettings[key])) out.add(key)
    }
    // Free disk is edited in GiB and stored in bytes, so it is compared in the unit it is typed in.
    if (minFreeDiskGiB !== savedMinFreeDiskGiB) out.add('minFreeDiskBytes')
    return out
  })

  let changedCount = $derived(changedFields.size)

  function roomChangedCount(key: RoomKey): number {
    const fields = ROOM_FIELDS[key]
    if (!fields) return 0
    let n = fields.filter((f) => changedFields.has(f)).length
    if (key === 'files' && changedFields.has('minFreeDiskBytes')) n += 1
    return n
  }

  /** True while this field is holding an unsaved edit — the row lights up and offers a way back. */
  function isChanged(field: keyof Settings | 'minFreeDiskBytes'): boolean {
    return changedFields.has(field)
  }

  function revert(field: keyof Settings | 'minFreeDiskBytes') {
    if (!savedSettings) return
    if (field === 'minFreeDiskBytes') {
      minFreeDiskGiB = savedMinFreeDiskGiB
      return
    }
    settings = { ...settings, [field]: savedSettings[field] }
  }

  function discardAll() {
    if (!savedSettings) return
    settings = { ...savedSettings }
    minFreeDiskGiB = savedMinFreeDiskGiB
    message = null
    error = null
  }

  /** What each card says about its own section without being opened. */
  let rooms = $derived([
    {
      key: 'encoding' as RoomKey,
      title: i18n.m.settings.room_encoding,
      description: i18n.m.settings.room_encoding_desc,
      state: tr(i18n.m.settings.room_encoding_state, {
        jobs: settings.maxConcurrentJobs,
        encoder: settings.encoderMode,
        hours: settings.libraryScanIntervalHours,
      }),
      healthy: true,
    },
    {
      key: 'files' as RoomKey,
      title: i18n.m.settings.room_files,
      description: i18n.m.settings.room_files_desc,
      state: settings.dryRunMode
        ? i18n.m.settings.room_files_state_dry_run
        : tr(i18n.m.settings.room_files_state, {
            size: formatSize(gibToBytes(minFreeDiskGiB)),
            days: Math.max(0, Math.floor(Number(settings.replacementQuarantineRetentionDays) || 0)),
          }),
      healthy: !settings.dryRunMode,
    },
    {
      key: 'servers' as RoomKey,
      title: i18n.m.settings.room_servers,
      description: i18n.m.settings.room_servers_desc,
      state: watchers.length
        ? watchers.map((w) => w.name).join(', ')
        : i18n.m.settings.room_none_connected,
      healthy: watchers.length > 0,
    },
    {
      key: 'downloads' as RoomKey,
      title: i18n.m.settings.room_downloads,
      description: i18n.m.settings.room_downloads_desc,
      state: arrs.length ? arrs.map((c) => c.name).join(', ') : i18n.m.settings.room_none_connected,
      healthy: arrs.length > 0,
    },
    {
      key: 'notifications' as RoomKey,
      title: i18n.m.settings.room_notifications,
      description: i18n.m.settings.room_notifications_desc,
      state: targets.length
        ? targets.map((n) => n.name).join(', ')
        : i18n.m.settings.room_none_configured,
      healthy: targets.length > 0,
    },
    // Only once opted in, and only where the server offers the preview at all: a default
    // single-container install should not have to wonder what a remote worker is.
    ...(settings.remoteWorkersAvailable && settings.remoteWorkersEnabled
      ? [{
          key: 'workers' as RoomKey,
          title: i18n.m.settings.room_workers,
          description: i18n.m.settings.room_workers_desc,
          state: i18n.m.settings.room_workers_state,
          healthy: true,
        }]
      : []),
    {
      key: 'system' as RoomKey,
      title: i18n.m.settings.room_system,
      description: i18n.m.settings.room_system_desc,
      state: i18n.m.settings.room_system_state,
      healthy: true,
      readOnly: true,
    },
  ])

  let currentRoom = $derived(openRoom ? rooms.find((r) => r.key === openRoom) ?? null : null)

  let loading = $state(true)
  let saving = $state(false)
  let error = $state<string | null>(null)
  let message = $state<string | null>(null)
  let cleanupPreview = $state<TimedCleanupPreview | null>(null)
  let cleanupLoading = $state(false)
  let cleaning = $state(false)
  let cleanupError = $state<string | null>(null)
  let cleanupMessage = $state<string | null>(null)

  $effect(() => {
    void load()
    void loadWatchers()
    void loadTargets()
    void loadArrs()
  })

  // Rooms are only safe because the draft outlives them. That holds while you stay inside
  // Settings — but leaving for another page unmounts this component, so an unsaved edit would
  // vanish without a word. The guard asks first.
  //
  // It has to let Settings' own rooms through: all in-app navigation funnels through the hash,
  // so walking from Encoding to Files looks exactly like leaving unless the destination is
  // checked. By the time a guard runs the hash already holds where we are going.
  function confirmLeavingUnsaved(): boolean {
    if (changedCount === 0) return true
    const destination = window.location.hash.replace(/^#/, '')
    if (destination.startsWith('/settings')) return true
    return confirm(i18n.m.settings.confirm_discard)
  }

  $effect(() => router.guardLeave(confirmLeavingUnsaved))

  async function load() {
    loading = true
    error = null
    try {
      // Merged over the current values rather than replacing them outright. A response that omits
      // a field — an older server, a partial payload — would otherwise leave a boolean undefined,
      // and `bind:checked={undefined}` throws hard enough to take the whole page down with it.
      //
      // The merge must happen *after* the await. Spreading `settings` inline in the same
      // expression reads it synchronously, which makes the calling $effect depend on it, so
      // assigning it here would retrigger the effect and loop forever on "Loading…".
      const loaded = await api.settings()
      settings = { ...settings, ...loaded }
      minFreeDiskGiB = bytesToGiB(settings.minFreeDiskBytes)
      savedSettings = { ...settings }
      savedMinFreeDiskGiB = minFreeDiskGiB
      await loadCleanupPreview()
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
      const saved = await api.saveSettings({
        ...settings,
        maxConcurrentJobs: Number(settings.maxConcurrentJobs) || 1,
        cpuThreadLimit: Math.max(0, Number(settings.cpuThreadLimit) || 0),
        libraryScanIntervalHours: Math.max(1, Number(settings.libraryScanIntervalHours) || 1),
        replacementQuarantineRetentionDays: Math.max(0, Math.floor(Number(settings.replacementQuarantineRetentionDays) || 0)),
        minFreeDiskBytes: gibToBytes(minFreeDiskGiB),
      })
      settings = { ...settings, ...saved }
      minFreeDiskGiB = bytesToGiB(settings.minFreeDiskBytes)
      savedSettings = { ...settings }
      savedMinFreeDiskGiB = minFreeDiskGiB
      message = i18n.m.settings.saved
      await loadCleanupPreview()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.settings.error_save
    } finally {
      saving = false
    }
  }

  async function loadCleanupPreview() {
    cleanupLoading = true
    cleanupError = null
    try {
      cleanupPreview = await api.timedCleanupPreview()
    } catch (err) {
      cleanupPreview = null
      cleanupError = err instanceof Error ? err.message : i18n.m.settings.cleanup_error_load
    } finally {
      cleanupLoading = false
    }
  }

  function cleanupPolicyHasUnsavedChanges() {
    if (!cleanupPreview) return false
    return Math.max(0, Math.floor(Number(settings.replacementQuarantineRetentionDays) || 0)) !== cleanupPreview.retentionDays
      || settings.dryRunMode !== cleanupPreview.dryRunMode
  }

  async function cleanUpNow() {
    if (!cleanupPreview || cleanupPreview.totalCount === 0 || cleanupPolicyHasUnsavedChanges()) return

    const confirmCopy = cleanupPreview.totalCount === 1
      ? i18n.m.settings.cleanup_confirm_one
      : i18n.m.settings.cleanup_confirm_other
    const confirmed = confirm(tr(confirmCopy, {
      space: formatSize(cleanupPreview.totalBytes),
      count: cleanupPreview.totalCount,
      failedCount: cleanupPreview.failedOutputCount,
      failedSpace: formatSize(cleanupPreview.failedOutputBytes),
      quarantineCount: cleanupPreview.quarantinedOriginalCount,
      quarantineSpace: formatSize(cleanupPreview.quarantinedOriginalBytes),
    }))
    if (!confirmed) return

    cleaning = true
    cleanupError = null
    cleanupMessage = null
    try {
      const result = await api.runTimedCleanup(cleanupPreview)
      const completeCopy = result.cleanedCount === 1
        ? i18n.m.settings.cleanup_complete_one
        : i18n.m.settings.cleanup_complete_other
      cleanupMessage = tr(completeCopy, {
        count: result.cleanedCount,
        space: formatSize(result.reclaimedBytes),
      })
      await loadCleanupPreview()
    } catch (err) {
      const failure = err instanceof Error ? err.message : i18n.m.settings.cleanup_error_run
      await loadCleanupPreview()
      cleanupError = failure
    } finally {
      cleaning = false
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

{#snippet wasChanged(field: keyof Settings | 'minFreeDiskBytes', previous: string)}
  {#if isChanged(field)}
    <span class="mt-1 block font-mono text-[10.5px] font-normal normal-case tracking-normal text-cyan-700 dark:text-cyan-300">
      {tr(i18n.m.settings.was_value, { value: previous })}
      <button type="button" class="underline underline-offset-2 hover:no-underline" onclick={() => revert(field)}>
        {i18n.m.settings.put_back}
      </button>
    </span>
  {/if}
{/snippet}

<header class="mb-6">
  <div class="min-w-0">
    <h1 class="text-2xl font-bold text-slate-800 dark:text-slate-100">{i18n.m.nav.settings}</h1>
    <p class="text-sm text-slate-500 dark:text-slate-400">{i18n.m.settings.subtitle}</p>
  </div>
</header>

{#if error}
  <Banner kind="error" class="mb-4">{error}</Banner>
{/if}

{#if loading}
  <div class="card p-8 text-center text-slate-400">{i18n.m.common.loading_short}</div>
{:else}
  {#if !openRoom}
    <!-- The landing page. Each card reports what its room is set to, so the common
         questions are answered without opening anything. -->
    <div class="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
      {#each rooms as room (room.key)}
        <button
          type="button"
          class="card card-interactive flex min-h-[7.5rem] flex-col gap-2 p-4 text-left"
          onclick={() => openRoomAt(room.key)}
        >
          <span class="flex items-start justify-between gap-2">
            <span class="text-sm font-semibold text-slate-900 dark:text-slate-100">{room.title}</span>
            {#if roomChangedCount(room.key) > 0}
              <span
                class="badge flex-none bg-cyan-600 font-mono text-[10px] text-white"
                title={i18n.m.settings.unsaved_here}
              >{roomChangedCount(room.key)}</span>
            {:else if room.readOnly}
              <span class="badge flex-none bg-slate-200 font-mono text-[10px] font-medium uppercase tracking-wide text-slate-500 dark:bg-slate-700 dark:text-slate-300">{i18n.m.settings.read_only}</span>
            {/if}
          </span>
          <span class="text-xs leading-relaxed text-slate-500 dark:text-slate-400">{room.description}</span>
          <span class="mt-auto flex items-center gap-2 pt-2 font-mono text-[11px] text-slate-600 hairline-t dark:text-slate-300">
            <span
              class="h-1.5 w-1.5 flex-none rounded-full {room.healthy ? 'bg-emerald-500' : 'bg-slate-400 dark:bg-slate-500'}"
              aria-hidden="true"
            ></span>
            <span class="truncate">{room.state}</span>
          </span>
        </button>
      {/each}
    </div>
  {:else}
    <!-- The room says its own name. Without this the first thing you see after opening
         "Encoding" is a section headed "Queue", and the room you asked for has vanished. -->
    <div class="mb-5">
      <button type="button" class="btn btn-ghost -ml-2 mb-2 px-2 text-xs" onclick={closeRoom}>
        &larr; {i18n.m.settings.all_settings}
      </button>
      <h2
        id="room-heading"
        tabindex="-1"
        class="text-lg font-semibold text-slate-900 outline-none dark:text-slate-100"
      >{currentRoom?.title ?? i18n.m.nav.settings}</h2>
      {#if currentRoom?.description}
        <p class="mt-0.5 max-w-3xl text-sm text-slate-500 dark:text-slate-400">{currentRoom.description}</p>
      {/if}
    </div>
  {/if}

  {#if openRoom === 'encoding'}
  <div class="min-w-0 space-y-5">
  <ConfigSection
    id="global-workload"
    title={i18n.m.nav.queue}
    description={i18n.m.settings.queue_desc}
  >
    <div class="grid gap-5 sm:grid-cols-2">
      <div class="-m-2 rounded-lg p-2 transition-colors {isChanged('maxConcurrentJobs') ? 'bg-cyan-500/10' : ''}">
        <label class="label" for="max-jobs">{i18n.m.settings.max_jobs} <InfoTip text={i18n.m.settings.max_jobs_tip} /></label>
        <input id="max-jobs" class="input" type="number" min="1" bind:value={settings.maxConcurrentJobs} />
        {@render wasChanged('maxConcurrentJobs', String(savedSettings?.maxConcurrentJobs ?? ''))}
      </div>

      <div class="-m-2 rounded-lg p-2 transition-colors {isChanged('encoderMode') ? 'bg-cyan-500/10' : ''}">
        <label class="label" for="encoder-mode">{i18n.m.settings.encoder_mode} <InfoTip text={i18n.m.settings.encoder_mode_tip} /></label>
        <select id="encoder-mode" class="input" bind:value={settings.encoderMode}>
          <option value="Auto">Auto</option>
          <option value="Cpu">CPU</option>
          <option value="NvidiaNvenc">NVIDIA NVENC</option>
          <option value="IntelQsv">Intel QSV</option>
          <option value="Vaapi">VAAPI</option>
        </select>
        {@render wasChanged('encoderMode', String(savedSettings?.encoderMode ?? ''))}
      </div>

      <div class="-m-2 rounded-lg p-2 transition-colors {isChanged('cpuThreadLimit') ? 'bg-cyan-500/10' : ''}">
        <label class="label" for="cpu-threads">{i18n.m.settings.cpu_threads} <InfoTip text={i18n.m.settings.cpu_threads_tip} /></label>
        <input id="cpu-threads" class="input" type="number" min="0" bind:value={settings.cpuThreadLimit} />
        {@render wasChanged('cpuThreadLimit', String(savedSettings?.cpuThreadLimit ?? ''))}
      </div>

      <div class="-m-2 rounded-lg p-2 transition-colors {isChanged('libraryScanIntervalHours') ? 'bg-cyan-500/10' : ''}">
        <label class="label" for="scan-interval">{i18n.m.settings.scan_interval} <InfoTip text={i18n.m.settings.scan_interval_tip} /></label>
        <div class="flex min-w-0 items-center gap-2">
          <input id="scan-interval" class="input min-w-0 flex-1" type="number" min="1" step="1" bind:value={settings.libraryScanIntervalHours} />
          <span class="flex-none text-sm text-slate-500 dark:text-slate-400">{i18n.m.settings.hours}</span>
        </div>
        {@render wasChanged('libraryScanIntervalHours', String(savedSettings?.libraryScanIntervalHours ?? ''))}
      </div>

    </div>

    <div class="mt-5 grid gap-5 border-t border-line pt-5 sm:grid-cols-2">
      <Toggle
        bind:checked={settings.hardwareDecode}
        label={i18n.m.settings.hardware_decode}
        hint={i18n.m.settings.hardware_decode_hint}
      />
      <div>
        <label class="label" for="hdr-tone-map-mode">
          {i18n.m.settings.hdr_tone_map_mode}
          <InfoTip text={i18n.m.settings.hdr_tone_map_mode_tip} />
        </label>
        <select id="hdr-tone-map-mode" class="input" bind:value={settings.hdrToneMapMode}>
          <option value="Software">{i18n.m.settings.hdr_tone_map_software}</option>
          <option value="Hardware">{i18n.m.settings.hdr_tone_map_hardware}</option>
        </select>
      </div>
      <p class="text-xs text-slate-500 dark:text-slate-400 sm:col-span-2">
        {i18n.m.settings.auto_run_before}<button class="text-cyan-600 hover:underline dark:text-cyan-400" onclick={() => router.go('/libraries')}>{i18n.m.nav.libraries}</button>{i18n.m.settings.auto_run_after}
      </p>
    </div>
  </ConfigSection>
  </div>
  {/if}

  {#if openRoom === 'files'}
  <div class="min-w-0 space-y-5">
  <ConfigSection
    id="global-replacement"
    title={i18n.m.settings.replacement_title}
    description={i18n.m.settings.replacement_desc}
  >
    <div class="max-w-2xl">
      <Toggle
        bind:checked={settings.dryRunMode}
        label={i18n.m.settings.dry_run}
        hint={i18n.m.settings.dry_run_hint}
      />
    </div>
    {#if settings.remoteWorkersAvailable}
      <!-- Groundwork, not a feature: the server shows this only under the experimental flag. -->
      <div class="mt-5 max-w-2xl border-t border-line pt-5">
        <Toggle
          bind:checked={settings.remoteWorkersEnabled}
          label={i18n.m.settings.remote_workers}
          hint={i18n.m.settings.remote_workers_hint}
        />
      </div>
    {/if}
    <div class="mt-5 max-w-2xl border-t border-line pt-5">
      <Toggle
        bind:checked={settings.replacementAllowCrossFilesystem}
        label={i18n.m.settings.cross_fs}
        hint={i18n.m.settings.cross_fs_hint}
      />
    </div>
    <div class="mt-5 max-w-2xl border-t border-line pt-5">
      <div class="-m-2 max-w-[16rem] rounded-lg p-2 transition-colors {isChanged('minFreeDiskBytes') ? 'bg-cyan-500/10' : ''}">
        <label class="label" for="free-disk">{i18n.m.settings.free_disk} <InfoTip text={tr(i18n.m.settings.free_disk_tip, { size: formatSize(gibToBytes(minFreeDiskGiB)) })} /></label>
        <div class="flex min-w-0 items-center gap-2">
          <input id="free-disk" class="input min-w-0 flex-1" type="number" min="0" step="1" bind:value={minFreeDiskGiB} />
          <span class="flex-none text-sm text-slate-500 dark:text-slate-400">{i18n.m.settings.gib}</span>
        </div>
        {@render wasChanged('minFreeDiskBytes', savedMinFreeDiskGiB)}
      </div>
    </div>
    <div class="mt-5 max-w-2xl border-t border-line pt-5">
      <label class="label" for="cleanup-retention">{i18n.m.settings.cleanup_retention} <InfoTip text={i18n.m.settings.cleanup_retention_tip} /></label>
      <div class="flex max-w-[16rem] min-w-0 items-center gap-2">
        <input id="cleanup-retention" class="input min-w-0 flex-1" type="number" min="0" step="1" bind:value={settings.replacementQuarantineRetentionDays} />
        <span class="flex-none text-sm text-slate-500 dark:text-slate-400">{i18n.m.settings.days}</span>
      </div>

      <div class="mt-3 rounded-lg border border-line bg-slate-50 p-3 dark:bg-slate-900/50" aria-live="polite">
        <div class="flex flex-wrap items-center justify-between gap-3">
          <div class="min-w-0">
            <p class="text-xs font-medium text-slate-500 dark:text-slate-400">{i18n.m.settings.cleanup_reclaimable}</p>
            {#if cleanupLoading}
              <p class="mt-1 text-sm text-slate-500 dark:text-slate-400">{i18n.m.settings.cleanup_calculating}</p>
            {:else if cleanupPreview}
              <p class="mt-0.5 text-xl font-semibold tabular-nums text-slate-800 dark:text-slate-100">{formatSize(cleanupPreview.totalBytes)}</p>
              <p class="mt-1 text-xs text-slate-500 dark:text-slate-400">
                {tr(i18n.m.settings.cleanup_breakdown, {
                  failedCount: cleanupPreview.failedOutputCount,
                  failedSpace: formatSize(cleanupPreview.failedOutputBytes),
                  quarantineCount: cleanupPreview.quarantinedOriginalCount,
                  quarantineSpace: formatSize(cleanupPreview.quarantinedOriginalBytes),
                })}
              </p>
            {/if}
          </div>
          <button
            class="btn btn-danger min-h-11"
            onclick={cleanUpNow}
            disabled={cleanupLoading || cleaning || !cleanupPreview || cleanupPreview.totalCount === 0 || cleanupPolicyHasUnsavedChanges()}
          >
            {cleaning ? i18n.m.settings.cleanup_running : i18n.m.settings.cleanup_now}
          </button>
        </div>

        {#if cleanupPreview?.retentionDays === 0}
          <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">{i18n.m.settings.cleanup_indefinite}</p>
        {:else if cleanupPreview && cleanupPreview.totalCount === 0}
          <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">{i18n.m.settings.cleanup_none}</p>
        {/if}
        {#if cleanupPolicyHasUnsavedChanges()}
          <p class="mt-2 text-xs text-amber-700 dark:text-amber-300">{i18n.m.settings.cleanup_save_first}</p>
        {/if}
        {#if cleanupPreview?.dryRunMode}
          <p class="mt-2 text-xs text-slate-500 dark:text-slate-400">{i18n.m.settings.cleanup_dry_run}</p>
        {/if}
        {#if cleanupError}<p class="mt-2 text-xs text-red-600 dark:text-red-400">{cleanupError}</p>{/if}
        {#if cleanupMessage}<p class="mt-2 text-xs text-emerald-600 dark:text-emerald-400">{cleanupMessage}</p>{/if}
      </div>
    </div>
  </ConfigSection>

  </div>
  {/if}

  {#if openRoom === 'servers'}
  <div class="min-w-0 space-y-5">
    <!-- Media servers (Plex/Jellyfin/Emby): playback-aware pause + post-replacement re-scan. -->
    <ConfigSection
      id="global-media-servers"
      title={i18n.m.settings.media_servers}
      description={i18n.m.settings.media_servers_summary}
    >
      <p class="mb-4 max-w-4xl text-sm leading-relaxed text-slate-500 dark:text-slate-400">
        {i18n.m.settings.media_servers_desc}
      </p>

      {#if watcherError}
        <div class="mb-3 rounded border border-red-300 p-2 text-sm text-red-700 dark:border-red-800 dark:text-red-400">{watcherError}</div>
      {/if}

      {#if watchers.length > 0}
        <ul class="mb-4 divide-y divide-line-soft">
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
                <button class="btn btn-ghost min-h-11 px-2 py-1 text-xs sm:min-h-0" onclick={() => startEdit(w)}>{i18n.m.settings.edit}</button>
                <button class="btn btn-ghost min-h-11 px-2 py-1 text-xs text-red-600 sm:min-h-0 dark:text-red-400" onclick={() => deleteWatcher(w)}>{i18n.m.settings.remove}</button>
              </div>
            </li>
          {/each}
        </ul>
      {:else}
        <p class="mb-4 text-sm text-slate-400">{i18n.m.settings.media_servers_empty}</p>
      {/if}

      <div class="rounded-lg border border-line p-4">
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
            <div class="flex min-w-0 flex-col items-stretch gap-2 sm:flex-row sm:items-center">
              <input
                id="watcher-token"
                class="input"
                type="password"
                placeholder={editingId === null ? '' : i18n.m.settings.keep_current}
                bind:value={watcherDraft.apiToken}
              />
              {#if watcherDraft.type !== 'Emby'}
                {#if connecting}
                  <button class="btn btn-ghost min-h-11 whitespace-nowrap px-3 py-1 text-xs sm:min-h-0" onclick={resetConnect}>{i18n.m.common.cancel}</button>
                {:else}
                  <button class="btn min-h-11 whitespace-nowrap px-3 py-1 text-xs sm:min-h-0" onclick={connect}>
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
              <ul class="mt-2 divide-y divide-line-soft rounded-md border border-line divide-line">
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
        <div class="mt-4 flex flex-wrap items-center gap-2">
          <button class="btn btn-primary min-h-11 px-3 py-1 text-sm sm:min-h-0" onclick={saveWatcher} disabled={savingWatcher}>
            {savingWatcher ? i18n.m.settings.saving : editingId === null ? i18n.m.settings.add_media_server_btn : i18n.m.settings.save_changes}
          </button>
          <button
            class="btn min-h-11 px-3 py-1 text-sm sm:min-h-0"
            onclick={testConnection}
            disabled={testing || (!watcherDraft.baseUrl.trim())}
            title={i18n.m.settings.test_connection_title}
          >
            {testing ? i18n.m.settings.testing : i18n.m.settings.test_connection}
          </button>
          {#if editingId !== null}
            <button class="btn btn-ghost min-h-11 px-3 py-1 text-sm sm:min-h-0" onclick={startAdd} disabled={savingWatcher}>{i18n.m.common.cancel}</button>
          {/if}
        </div>
      </div>
    </ConfigSection>
  </div>
  {/if}

  {#if openRoom === 'downloads'}
  <div class="min-w-0 space-y-5">
    <!-- Download managers (Sonarr/Radarr): hold files back while an import is in progress. -->
    <ConfigSection
      id="global-download-managers"
      title={i18n.m.settings.download_managers}
      description={i18n.m.settings.download_managers_summary}
    >
      <p class="mb-4 max-w-4xl text-sm leading-relaxed text-slate-500 dark:text-slate-400">
        {i18n.m.settings.download_managers_desc}
      </p>

      {#if arrError}
        <div class="mb-3 rounded border border-red-300 p-2 text-sm text-red-700 dark:border-red-800 dark:text-red-400">{arrError}</div>
      {/if}

      {#if arrs.length > 0}
        <ul class="mb-4 divide-y divide-line-soft">
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
                <button class="btn btn-ghost min-h-11 px-2 py-1 text-xs sm:min-h-0" onclick={() => startEditArr(c)}>{i18n.m.settings.edit}</button>
                <button class="btn btn-ghost min-h-11 px-2 py-1 text-xs text-red-600 sm:min-h-0 dark:text-red-400" onclick={() => deleteArr(c)}>{i18n.m.settings.remove}</button>
              </div>
            </li>
          {/each}
        </ul>
      {:else}
        <p class="mb-4 text-sm text-slate-400">{i18n.m.settings.download_managers_empty}</p>
      {/if}

      <div class="rounded-lg border border-line p-4">
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
        <div class="mt-4 flex flex-wrap items-center gap-2">
          <button class="btn btn-primary min-h-11 px-3 py-1 text-sm sm:min-h-0" onclick={saveArr} disabled={savingArr}>
            {savingArr ? i18n.m.settings.saving : editingArrId === null ? i18n.m.settings.add_download_manager_btn : i18n.m.settings.save_changes}
          </button>
          {#if editingArrId !== null}
            <button class="btn btn-ghost min-h-11 px-3 py-1 text-sm sm:min-h-0" onclick={startAddArr} disabled={savingArr}>{i18n.m.common.cancel}</button>
          {/if}
        </div>
      </div>
    </ConfigSection>
  </div>
  {/if}

  {#if openRoom === 'notifications'}
  <div
    class="min-w-0"
  >
  <ConfigSection
    id="global-notifications"
    title={i18n.m.settings.room_notifications}
    description={i18n.m.settings.notifications_desc}
  >

    {#if targetError}
      <div class="mb-3 rounded border border-red-300 p-2 text-sm text-red-700 dark:border-red-800 dark:text-red-400">{targetError}</div>
    {/if}
    {#if targetMessage}
      <div class="mb-3 rounded border border-emerald-300 p-2 text-sm text-emerald-700 dark:border-emerald-800 dark:text-emerald-400" aria-live="polite">{targetMessage}</div>
    {/if}

    {#if targets.length > 0}
      <ul class="mb-4 divide-y divide-line-soft">
        {#each targets as t (t.id)}
          <li class="flex flex-wrap items-center gap-x-3 gap-y-2 py-2">
            <span class="badge bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300">{t.type}</span>
            <div class="min-w-0 flex-1">
              <div class="truncate text-sm font-medium text-slate-700 dark:text-slate-200">{t.name}</div>
              <div class="truncate font-mono text-[11px] text-slate-400" title={t.url}>{t.url}</div>
            </div>
            <div class="flex flex-wrap items-center gap-2">
              {#if !t.enabled}<span class="badge bg-slate-100 text-slate-400 dark:bg-slate-800 dark:text-slate-500">{i18n.m.settings.disabled}</span>{/if}
              {#if t.type === 'Telegram' && !t.hasToken}<span class="badge bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300">{i18n.m.settings.badge_no_token}</span>{/if}
              {#if t.notifyOnReplacement}<span class="badge bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-400">{i18n.m.settings.badge_replaced}</span>{/if}
              {#if t.notifyOnFailure}<span class="badge bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300">{i18n.m.settings.badge_failed}</span>{/if}
              <button class="btn btn-ghost min-h-11 px-2 py-1 text-xs sm:min-h-0" onclick={() => testTarget(t)} disabled={testingTargetId !== null}>
                {testingTargetId === t.id ? i18n.m.settings.testing : i18n.m.settings.send_test}
              </button>
              <button class="btn btn-ghost min-h-11 px-2 py-1 text-xs sm:min-h-0" onclick={() => startEditTarget(t)}>{i18n.m.settings.edit}</button>
              <button class="btn btn-ghost min-h-11 px-2 py-1 text-xs text-red-600 sm:min-h-0 dark:text-red-400" onclick={() => deleteTarget(t)}>{i18n.m.settings.remove}</button>
            </div>
          </li>
        {/each}
      </ul>
    {:else}
      <p class="mb-4 text-sm text-slate-400">{i18n.m.settings.targets_empty}</p>
    {/if}

    <div class="rounded-lg border border-line p-4">
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
          <label class="label" for="target-url">{targetDraft.type === 'Telegram' ? i18n.m.settings.chat_id : i18n.m.settings.url}</label>
          <input
            id="target-url"
            class="input"
            placeholder={targetDraft.type === 'Telegram'
              ? i18n.m.settings.telegram_chat_id_ph
              : targetDraft.type === 'Discord'
                ? i18n.m.settings.discord_url_ph
                : i18n.m.settings.ntfy_url_ph}
            bind:value={targetDraft.url}
          />
          {#if targetDraft.type === 'Discord'}
            <p class="mt-1 text-[11px] text-slate-400">{i18n.m.settings.discord_hint}</p>
          {:else if targetDraft.type === 'Telegram'}
            <p class="mt-1 text-[11px] text-slate-400">{i18n.m.settings.telegram_hint}</p>
          {/if}
        </div>
        <div>
          <label class="label" for="target-token">
            {targetDraft.type === 'Telegram' ? i18n.m.settings.bot_token : i18n.m.settings.token}
            {#if targetDraft.type !== 'Telegram'}<span class="text-slate-400">{i18n.m.settings.optional}</span>{/if}
          </label>
          <input
            id="target-token"
            class="input"
            type="password"
            required={telegramTokenRequired}
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
      <div class="mt-4 flex flex-wrap items-center gap-2">
        <button class="btn btn-primary min-h-11 px-3 py-1 text-sm sm:min-h-0" onclick={saveTarget} disabled={savingTarget || (telegramTokenRequired && !targetDraft.token.trim())}>
          {savingTarget ? i18n.m.settings.saving : editingTargetId === null ? i18n.m.settings.add_target_btn : i18n.m.settings.save_changes}
        </button>
        {#if editingTargetId !== null}
          <button class="btn btn-ghost min-h-11 px-3 py-1 text-sm sm:min-h-0" onclick={startAddTarget} disabled={savingTarget}>{i18n.m.common.cancel}</button>
        {/if}
      </div>
    </div>
  </ConfigSection>
  </div>
  {/if}

  {#if openRoom === 'system'}
    <div
        class="min-w-0"
    >
      <ToolsPanel />
    </div>
  {/if}

  {#if openRoom === 'workers'}
    <div
        class="min-w-0"
    >
      <WorkersPanel />
    </div>
  {/if}

  {#if openRoom === 'system'}
  <div
    class="min-w-0 space-y-5"
  >
  <ConfigSection
    id="global-backup"
    title={i18n.m.settings.backup_title}
    description={i18n.m.settings.backup_summary}
  >
    <p class="mb-4 max-w-4xl text-sm leading-relaxed text-slate-500 dark:text-slate-400">
      {i18n.m.settings.backup_desc}
    </p>

    {#if backupError}
      <div class="mb-3 rounded border border-red-300 p-2 text-sm text-red-700 dark:border-red-800 dark:text-red-400">{backupError}</div>
    {/if}
    {#if backupMessage}
      <div class="mb-3 rounded border border-emerald-300 p-2 text-sm text-emerald-700 dark:border-emerald-800 dark:text-emerald-400">{backupMessage}</div>
    {/if}

    <div class="flex flex-wrap items-center gap-3">
      <button class="btn" onclick={exportConfig}>{i18n.m.settings.export_config}</button>
      <button class="btn" onclick={() => fileInput?.click()} disabled={importing}>
        {importing ? i18n.m.settings.importing : i18n.m.settings.import_config}
      </button>
      <input bind:this={fileInput} type="file" accept="application/json,.json" class="hidden" onchange={importConfig} />
    </div>

  </ConfigSection>

  <ConfigSection
    id="global-first-run"
    title={i18n.m.settings.restart_setup_title}
    description={i18n.m.settings.restart_setup_desc}
  >
    <button class="btn min-h-11 w-full sm:w-auto" onclick={restartSetup} disabled={restartingSetup}>
      <Icon name="retry" class="h-4 w-4 {restartingSetup ? 'animate-spin' : ''}" />
      {restartingSetup ? i18n.m.settings.restarting_setup : i18n.m.settings.restart_setup}
    </button>
  </ConfigSection>
  </div>
  {/if}

  {#if changedCount > 0 || message}
    <div
      class="card sticky bottom-0 z-10 mt-5 flex flex-wrap items-center gap-3 p-4"
      data-settings-actions
    >
      {#if changedCount > 0}
        <span class="text-sm font-semibold text-slate-800 dark:text-slate-100">
          {plural(changedCount, i18n.m.settings.unsaved_changes_one, i18n.m.settings.unsaved_changes_other)}
        </span>
      {/if}
      {#if message}<span class="text-sm text-emerald-600 dark:text-emerald-400">{message}</span>{/if}
      <span class="flex-1"></span>
      {#if changedCount > 0}
        <button class="btn btn-ghost min-h-11" onclick={discardAll} disabled={saving}>
          {i18n.m.settings.discard}
        </button>
        <button class="btn btn-primary min-h-11" onclick={save} disabled={saving}>
          {saving ? i18n.m.settings.saving : i18n.m.settings.save_settings}
        </button>
      {/if}
    </div>
  {/if}
{/if}
