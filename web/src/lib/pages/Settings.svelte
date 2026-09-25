<script lang="ts">
  import { tick } from 'svelte'
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
  import { brand } from '../stores/brand.svelte'
  import { parseBrandStyle } from '../brand-style'
  import { router } from '../stores/ui.svelte'
  import { setup } from '../stores/setup.svelte'
  import Toggle from '../components/Toggle.svelte'
  import InfoTip from '../components/InfoTip.svelte'
  import Icon from '../components/Icon.svelte'
  import Banner from '../components/Banner.svelte'
  import ConfigSection from '../components/ConfigSection.svelte'
  import ToolsPanel from '../components/ToolsPanel.svelte'
  import WorkersPanel from '../components/WorkersPanel.svelte'
  import DiagnosticCapturePanel from '../components/DiagnosticCapturePanel.svelte'

  // Settings is a set of rooms rather than a strip of tabs. The landing page is a grid of
  // cards, one per room, and each card reports what that room is currently set to — so
  // "is Plex still connected?" and "is anything reclaimable?" are answered without opening
  // anything. Opening a room gives that section the page to itself.
  //
  // A tab strip could not do the reporting, and its numbered sections implied a sequence
  // that never existed: nobody configures their encoder before their notifications because
  // it happens to be numbered lower.
  type RoomKey = 'encoding' | 'files' | 'servers' | 'downloads' | 'notifications' | 'workers' | 'system'

  const ROOM_ICONS: Record<RoomKey, string> = {
    encoding: 'gpu', files: 'shield-check', servers: 'tv', downloads: 'download',
    notifications: 'bell', workers: 'server', system: 'sliders',
  }

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

  let focusAfterNavigation = $state<string | null>(null)

  $effect(() => {
    const room = openRoom
    const target = focusAfterNavigation
    if (!target || (target === 'room-heading' ? !room : room !== null)) return
    void tick().then(() => {
      document.getElementById(target)?.focus()
      focusAfterNavigation = null
    })
  })

  function openRoomAt(key: RoomKey) {
    focusAfterNavigation = 'room-heading'
    router.go(`/settings/${ROOM_PATHS[key]}`)
  }

  function closeRoom() {
    focusAfterNavigation = openRoom ? `settings-room-${openRoom}` : null
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
    workerVerificationRequired: true,
    remoteWorkersAvailable: false,
    workloadConcurrencyMode: 'Automatic',
    nonVideoSlots: 0,
    evidenceValidationSlots: 1,
    automaticNonVideoSlots: 0,
    automaticEvidenceValidationSlots: 1,
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
    encoding: ['maxConcurrentJobs', 'workloadConcurrencyMode', 'nonVideoSlots', 'evidenceValidationSlots', 'encoderMode', 'cpuThreadLimit', 'libraryScanIntervalHours', 'hardwareDecode', 'hdrToneMapMode'],
    files: ['dryRunMode', 'remoteWorkersEnabled', 'workerVerificationRequired', 'replacementAllowCrossFilesystem', 'replacementQuarantineRetentionDays'],
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
    },
    {
      key: 'servers' as RoomKey,
      title: i18n.m.settings.room_servers,
      description: i18n.m.settings.room_servers_desc,
      state: watchers.length
        ? watchers.map((w) => w.name).join(', ')
        : i18n.m.settings.room_none_connected,
    },
    {
      key: 'downloads' as RoomKey,
      title: i18n.m.settings.room_downloads,
      description: i18n.m.settings.room_downloads_desc,
      state: arrs.length ? arrs.map((c) => c.name).join(', ') : i18n.m.settings.room_none_connected,
    },
    {
      key: 'notifications' as RoomKey,
      title: i18n.m.settings.room_notifications,
      description: i18n.m.settings.room_notifications_desc,
      state: targets.length
        ? targets.map((n) => n.name).join(', ')
        : i18n.m.settings.room_none_configured,
    },
    // Only once opted in, and only where the server offers the preview at all: a default
    // single-container install should not have to wonder what a remote worker is.
    ...(settings.remoteWorkersAvailable && settings.remoteWorkersEnabled
      ? [{
          key: 'workers' as RoomKey,
          title: i18n.m.settings.room_workers,
          description: i18n.m.settings.room_workers_desc,
          state: i18n.m.settings.room_workers_state,
        }]
      : []),
    {
      key: 'system' as RoomKey,
      title: i18n.m.settings.room_system,
      description: i18n.m.settings.room_system_desc,
      state: i18n.m.settings.room_system_state,
    },
  ])

  const roomGroups = $derived([
    { id: 'processing', title: i18n.m.settings.group_processing, rooms: rooms.filter(r => r.key === 'encoding' || r.key === 'files') },
    { id: 'connections', title: i18n.m.settings.group_connections, rooms: rooms.filter(r => r.key !== 'encoding' && r.key !== 'files') },
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
        nonVideoSlots: Math.min(4, Math.max(0, Number(settings.nonVideoSlots) || 0)),
        evidenceValidationSlots: Math.min(4, Math.max(1, Number(settings.evidenceValidationSlots) || 1)),
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
    <span class="mt-1 block font-mono text-[10.5px] font-normal normal-case tracking-normal text-accent">
      {tr(i18n.m.settings.was_value, { value: previous })}
      <button type="button" class="underline underline-offset-2 hover:no-underline" onclick={() => revert(field)}>
        {i18n.m.settings.put_back}
      </button>
    </span>
  {/if}
{/snippet}

<div class="settings-layout">
<header class="settings-page-header">
  <div class="min-w-0">
    <h1 class="page-title">{i18n.m.nav.settings}</h1>
    <p class="page-subtitle">{i18n.m.settings.subtitle}</p>
  </div>
</header>

{#if error}
  <Banner kind="error" class="mb-4">{error}</Banner>
{/if}

{#if loading}
  <div class="card p-8 text-center text-ink-4">{i18n.m.common.loading_short}</div>
{:else}
  {#if !openRoom}
    <div class="settings-overview">
      {#each roomGroups as group (group.id)}
        <section aria-labelledby={`settings-${group.id}-heading`}>
          <h2 id={`settings-${group.id}-heading`} class="settings-group-title">{group.title}</h2>
          <div class="settings-room-grid">
            {#each group.rooms as room (room.key)}
              <button
                type="button"
                id={`settings-room-${room.key}`}
                class="settings-room card card-interactive focus-ring"
                onclick={() => openRoomAt(room.key)}
              >
                <span class="settings-room-symbols" aria-hidden="true">
                  <Icon name={ROOM_ICONS[room.key]} class="h-5 w-5 text-accent" />
                  <Icon name="arrow-up-right" class="h-4 w-4 text-ink-4" />
                </span>
                <span class="flex items-start justify-between gap-3">
                  <span class="settings-room-title">{room.title}</span>
                  {#if roomChangedCount(room.key) > 0}
                    <span class="badge tone-accent font-mono" data-room-changes title={i18n.m.settings.unsaved_here}>
                      {roomChangedCount(room.key)}
                    </span>
                  {/if}
                </span>
                <span class="settings-room-description">{room.description}</span>
                <span class="settings-room-state">{room.state}</span>
              </button>
            {/each}
          </div>
        </section>
      {/each}
    </div>
  {:else}
    <div class="settings-room-heading">
      <button type="button" class="btn btn-ghost -ml-2 mb-5 px-2 text-xs" onclick={closeRoom}>
        <Icon name="arrow-left" /> {i18n.m.settings.all_settings}
      </button>
      <div class="flex items-start gap-3.5">
        <span class="settings-heading-icon" aria-hidden="true"><Icon name={ROOM_ICONS[openRoom]} class="h-5 w-5" /></span>
        <div class="min-w-0">
          <h2 id="room-heading" tabindex="-1" class="text-xl font-semibold tracking-tight text-ink outline-none">
            {currentRoom?.title ?? i18n.m.nav.settings}
          </h2>
          {#if currentRoom?.description}
            <p class="mt-1 max-w-2xl text-sm leading-relaxed text-ink-3">{currentRoom.description}</p>
          {/if}
        </div>
      </div>
    </div>
  {/if}

  <div class="settings-detail" class:settings-detail-open={openRoom !== null}>

  {#if openRoom === 'encoding'}
  <div class="min-w-0 space-y-5">
  <ConfigSection
    id="global-workload"
    title={i18n.m.nav.queue}
    description={i18n.m.settings.queue_desc}
  >
    <div class="settings-fields">
      <div class="settings-field {isChanged('maxConcurrentJobs') ? 'settings-field-changed' : ''}">
        <div class="settings-field-label"><label class="label" for="max-jobs">{i18n.m.settings.max_jobs} <InfoTip text={i18n.m.settings.max_jobs_tip} /></label><p>{i18n.m.settings.concurrency_hint}</p></div>
        <input id="max-jobs" class="input" type="number" min="1" bind:value={settings.maxConcurrentJobs} />
        {@render wasChanged('maxConcurrentJobs', String(savedSettings?.maxConcurrentJobs ?? ''))}
      </div>

      <details class="workload-details">
        <summary class="focus-ring">{i18n.m.settings.workload_advanced}</summary>
        <p class="workload-intro">{i18n.m.settings.workload_intro}</p>
        <div class="settings-field {isChanged('workloadConcurrencyMode') ? 'settings-field-changed' : ''}">
          <div class="settings-field-label"><label class="label" for="workload-mode">{i18n.m.settings.workload_mode} <InfoTip text={i18n.m.settings.workload_mode_tip} /></label><p>{i18n.m.settings.workload_mode_hint}</p></div>
          <select id="workload-mode" class="input" bind:value={settings.workloadConcurrencyMode}>
            <option value="Automatic">{i18n.m.settings.workload_automatic}</option>
            <option value="Manual">{i18n.m.settings.workload_manual}</option>
          </select>
          {@render wasChanged('workloadConcurrencyMode', String(savedSettings?.workloadConcurrencyMode ?? ''))}
        </div>
        {#if settings.workloadConcurrencyMode === 'Manual'}
          <div class="settings-field {isChanged('nonVideoSlots') ? 'settings-field-changed' : ''}">
            <div class="settings-field-label"><label class="label" for="non-video-slots">{i18n.m.settings.workload_nonvideo} <InfoTip text={i18n.m.settings.workload_nonvideo_tip} /></label><p>{i18n.m.settings.workload_nonvideo_hint}</p></div>
            <input id="non-video-slots" class="input" type="number" min="0" max="4" step="1" bind:value={settings.nonVideoSlots} />
            {@render wasChanged('nonVideoSlots', String(savedSettings?.nonVideoSlots ?? ''))}
          </div>
          <div class="settings-field {isChanged('evidenceValidationSlots') ? 'settings-field-changed' : ''}">
            <div class="settings-field-label"><label class="label" for="evidence-slots">{i18n.m.settings.workload_evidence} <InfoTip text={i18n.m.settings.workload_evidence_tip} /></label><p>{i18n.m.settings.workload_evidence_hint}</p></div>
            <input id="evidence-slots" class="input" type="number" min="1" max="4" step="1" bind:value={settings.evidenceValidationSlots} />
            {@render wasChanged('evidenceValidationSlots', String(savedSettings?.evidenceValidationSlots ?? ''))}
          </div>
        {/if}
        <div class="workload-preview" aria-live="polite">
          <span>{i18n.m.settings.workload_effective}</span>
          <strong>{tr(i18n.m.settings.workload_preview, { video: Math.max(1, Number(settings.maxConcurrentJobs) || 1), nonvideo: settings.workloadConcurrencyMode === 'Automatic' ? settings.automaticNonVideoSlots : Math.min(4, Math.max(0, Number(settings.nonVideoSlots) || 0)), evidence: settings.workloadConcurrencyMode === 'Automatic' ? settings.automaticEvidenceValidationSlots : Math.min(4, Math.max(1, Number(settings.evidenceValidationSlots) || 1)) })}</strong>
          <small>{i18n.m.settings.workload_preview_note}</small>
        </div>
      </details>

      <div class="settings-field {isChanged('encoderMode') ? 'settings-field-changed' : ''}">
        <div class="settings-field-label"><label class="label" for="encoder-mode">{i18n.m.settings.encoder_mode} <InfoTip text={i18n.m.settings.encoder_mode_tip} /></label><p>{i18n.m.settings.encoder_hint}</p></div>
        <select id="encoder-mode" class="input" bind:value={settings.encoderMode}>
          <option value="Auto">Auto</option>
          <option value="Cpu">CPU</option>
          <option value="NvidiaNvenc">NVIDIA NVENC</option>
          <option value="IntelQsv">Intel QSV</option>
          <option value="Vaapi">VAAPI</option>
        </select>
        {@render wasChanged('encoderMode', String(savedSettings?.encoderMode ?? ''))}
      </div>

      <div class="settings-field {isChanged('cpuThreadLimit') ? 'settings-field-changed' : ''}">
        <div class="settings-field-label"><label class="label" for="cpu-threads">{i18n.m.settings.cpu_threads} <InfoTip text={i18n.m.settings.cpu_threads_tip} /></label><p>{i18n.m.settings.threads_hint}</p></div>
        <input id="cpu-threads" class="input" type="number" min="0" bind:value={settings.cpuThreadLimit} />
        {@render wasChanged('cpuThreadLimit', String(savedSettings?.cpuThreadLimit ?? ''))}
      </div>

      <div class="settings-field {isChanged('libraryScanIntervalHours') ? 'settings-field-changed' : ''}">
        <div class="settings-field-label"><label class="label" for="scan-interval">{i18n.m.settings.scan_interval} <InfoTip text={i18n.m.settings.scan_interval_tip} /></label><p>{i18n.m.settings.scan_hint}</p></div>
        <div class="flex min-w-0 items-center gap-2">
          <input id="scan-interval" class="input min-w-0 flex-1" type="number" min="1" step="1" bind:value={settings.libraryScanIntervalHours} />
          <span class="flex-none text-sm text-ink-3">{i18n.m.settings.hours}</span>
        </div>
        {@render wasChanged('libraryScanIntervalHours', String(savedSettings?.libraryScanIntervalHours ?? ''))}
      </div>

    </div>

    <div class="settings-video-fields">
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
      <p class="text-xs text-ink-3">
        {i18n.m.settings.auto_run_before}<button class="text-accent hover:underline" onclick={() => router.go('/libraries')}>{i18n.m.nav.libraries}</button>{i18n.m.settings.auto_run_after}
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
    <div>
      <Toggle
        bind:checked={settings.dryRunMode}
        label={i18n.m.settings.dry_run}
        hint={i18n.m.settings.dry_run_hint}
      />
    </div>
    {#if settings.remoteWorkersAvailable}
      <!-- Groundwork, not a feature: the server shows this only under the experimental flag. -->
      <div class="mt-5 border-t border-line pt-5">
        <Toggle
          bind:checked={settings.remoteWorkersEnabled}
          label={i18n.m.settings.remote_workers}
          hint={i18n.m.settings.remote_workers_hint}
        />
        {#if settings.remoteWorkersEnabled}
          <div class="mt-5 border-t border-line pt-5">
            <Toggle
              bind:checked={settings.workerVerificationRequired}
              label={i18n.m.settings.worker_verification}
              hint={i18n.m.settings.worker_verification_hint}
            />
          </div>
        {/if}
      </div>
    {/if}
    <div class="mt-5 border-t border-line pt-5">
      <Toggle
        bind:checked={settings.replacementAllowCrossFilesystem}
        label={i18n.m.settings.cross_fs}
        hint={i18n.m.settings.cross_fs_hint}
      />
    </div>
    <div class="mt-5 border-t border-line pt-5">
      <div class="-m-2 max-w-[16rem] rounded-lg p-2 transition-colors {isChanged('minFreeDiskBytes') ? 'settings-field-changed' : ''}">
        <label class="label" for="free-disk">{i18n.m.settings.free_disk} <InfoTip text={tr(i18n.m.settings.free_disk_tip, { size: formatSize(gibToBytes(minFreeDiskGiB)) })} /></label>
        <div class="flex min-w-0 items-center gap-2">
          <input id="free-disk" class="input min-w-0 flex-1" type="number" min="0" step="1" bind:value={minFreeDiskGiB} />
          <span class="flex-none text-sm text-ink-3">{i18n.m.settings.gib}</span>
        </div>
        {@render wasChanged('minFreeDiskBytes', savedMinFreeDiskGiB)}
      </div>
    </div>
    <div class="mt-5 border-t border-line pt-5">
      <label class="label" for="cleanup-retention">{i18n.m.settings.cleanup_retention} <InfoTip text={i18n.m.settings.cleanup_retention_tip} /></label>
      <div class="flex max-w-[16rem] min-w-0 items-center gap-2">
        <input id="cleanup-retention" class="input min-w-0 flex-1" type="number" min="0" step="1" bind:value={settings.replacementQuarantineRetentionDays} />
        <span class="flex-none text-sm text-ink-3">{i18n.m.settings.days}</span>
      </div>

      <div class="mt-3 rounded-lg border border-line bg-sunken p-3" aria-live="polite">
        <div class="flex flex-wrap items-center justify-between gap-3">
          <div class="min-w-0">
            <p class="text-xs font-medium text-ink-3">{i18n.m.settings.cleanup_reclaimable}</p>
            {#if cleanupLoading}
              <p class="mt-1 text-sm text-ink-3">{i18n.m.settings.cleanup_calculating}</p>
            {:else if cleanupPreview}
              <p class="mt-0.5 text-xl font-semibold tabular-nums text-ink">{formatSize(cleanupPreview.totalBytes)}</p>
              <p class="mt-1 text-xs text-ink-3">
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
          <p class="mt-2 text-xs text-ink-3">{i18n.m.settings.cleanup_indefinite}</p>
        {:else if cleanupPreview && cleanupPreview.totalCount === 0}
          <p class="mt-2 text-xs text-ink-3">{i18n.m.settings.cleanup_none}</p>
        {/if}
        {#if cleanupPolicyHasUnsavedChanges()}
          <p class="mt-2 text-xs text-warn">{i18n.m.settings.cleanup_save_first}</p>
        {/if}
        {#if cleanupPreview?.dryRunMode}
          <p class="mt-2 text-xs text-ink-3">{i18n.m.settings.cleanup_dry_run}</p>
        {/if}
        {#if cleanupError}<p class="mt-2 text-xs text-bad">{cleanupError}</p>{/if}
        {#if cleanupMessage}<p class="mt-2 text-xs text-ok">{cleanupMessage}</p>{/if}
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
      <p class="mb-4 max-w-4xl text-sm leading-relaxed text-ink-3">
        {i18n.m.settings.media_servers_desc}
      </p>

      {#if watcherError}
        <div class="callout tone-bad mb-3">{watcherError}</div>
      {/if}

      {#if watchers.length > 0}
        <ul class="mb-4 divide-y divide-line-soft">
          {#each watchers as w (w.id)}
            <li class="flex flex-wrap items-center gap-x-3 gap-y-2 py-2">
              <span class="badge tone-neutral">{w.type}</span>
              <div class="min-w-0 flex-1">
                <div class="truncate text-sm font-medium text-ink-2">{w.name}</div>
                <div class="truncate font-mono text-[11px] text-ink-4" title={w.baseUrl}>{w.baseUrl}</div>
              </div>
              <div class="flex flex-wrap items-center gap-2">
                {#if !w.enabled}<span class="badge tone-muted">{i18n.m.settings.disabled}</span>{/if}
                {#if w.refreshOnReplace}<span class="badge tone-ok" title={i18n.m.settings.badge_refresh_title}>{i18n.m.settings.badge_refresh}</span>{/if}
                {#if !w.hasToken}<span class="badge tone-warn" title={i18n.m.settings.badge_no_token_title}>{i18n.m.settings.badge_no_token}</span>{/if}
                <button class="btn btn-ghost min-h-11 px-2 py-1 text-xs sm:min-h-0" onclick={() => startEdit(w)}>{i18n.m.settings.edit}</button>
                <button class="btn btn-ghost min-h-11 px-2 py-1 text-xs text-bad sm:min-h-0" onclick={() => deleteWatcher(w)}>{i18n.m.settings.remove}</button>
              </div>
            </li>
          {/each}
        </ul>
      {:else}
        <p class="mb-4 text-sm text-ink-4">{i18n.m.settings.media_servers_empty}</p>
      {/if}

      <div class="rounded-lg border border-line p-4">
        <h3 class="mb-3 text-sm font-semibold text-ink-2">
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
              <p class="mt-2 text-xs text-ink-3">{i18n.m.settings.plex_pick_hint}</p>
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
              <p class="mt-2 text-xs text-ink-3">{connectMessage}</p>
            {/if}
            {#if jellyfinCode}
              <p class="mt-1 font-mono text-lg tracking-widest text-accent">{jellyfinCode}</p>
            {/if}
            {#if plexServers && plexServers.length}
              <ul class="mt-2 divide-y divide-line-soft rounded-md border border-line divide-line">
                {#each plexServers as server}
                  <li>
                    <button
                      class="flex w-full items-center justify-between gap-3 px-3 py-2 text-left text-sm hover:bg-lit"
                      onclick={() => selectPlexServer(server)}
                    >
                      <span class="min-w-0">
                        <span class="font-medium text-ink-2">{server.name}</span>
                        <span class="block truncate font-mono text-[11px] text-ink-4">{server.uri}</span>
                      </span>
                      <span class="badge flex-shrink-0 {server.local ? 'tone-ok' : 'bg-raised text-ink-3'}">
                        {server.local ? i18n.m.settings.badge_local : i18n.m.settings.badge_remote}
                      </span>
                    </button>
                  </li>
                {/each}
              </ul>
            {/if}
          </div>
        </div>
        <div class="mt-3 grid gap-3">
          <Toggle bind:checked={watcherDraft.enabled} label={i18n.m.settings.pause_streaming} hint={i18n.m.settings.pause_streaming_hint} />
          <Toggle bind:checked={watcherDraft.refreshOnReplace} label={i18n.m.settings.refresh_replace} hint={i18n.m.settings.refresh_replace_hint} />
        </div>
        {#if testResult}
          <p class="mt-3 text-sm {testResult.ok ? 'text-ok' : 'text-bad'}">
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
      <p class="mb-4 max-w-4xl text-sm leading-relaxed text-ink-3">
        {i18n.m.settings.download_managers_desc}
      </p>

      {#if arrError}
        <div class="callout tone-bad mb-3">{arrError}</div>
      {/if}

      {#if arrs.length > 0}
        <ul class="mb-4 divide-y divide-line-soft">
          {#each arrs as c (c.id)}
            <li class="flex flex-wrap items-center gap-x-3 gap-y-2 py-2">
              <span class="badge tone-neutral">{c.type}</span>
              <div class="min-w-0 flex-1">
                <div class="truncate text-sm font-medium text-ink-2">{c.name}</div>
                <div class="truncate font-mono text-[11px] text-ink-4" title={c.baseUrl}>{c.baseUrl}</div>
              </div>
              <div class="flex flex-wrap items-center gap-2">
                {#if !c.enabled}<span class="badge tone-muted">{i18n.m.settings.disabled}</span>{/if}
                {#if !c.hasApiKey}<span class="badge tone-warn" title={i18n.m.settings.badge_no_key_title}>{i18n.m.settings.badge_no_key}</span>{/if}
                <button class="btn btn-ghost min-h-11 px-2 py-1 text-xs sm:min-h-0" onclick={() => startEditArr(c)}>{i18n.m.settings.edit}</button>
                <button class="btn btn-ghost min-h-11 px-2 py-1 text-xs text-bad sm:min-h-0" onclick={() => deleteArr(c)}>{i18n.m.settings.remove}</button>
              </div>
            </li>
          {/each}
        </ul>
      {:else}
        <p class="mb-4 text-sm text-ink-4">{i18n.m.settings.download_managers_empty}</p>
      {/if}

      <div class="rounded-lg border border-line p-4">
        <h3 class="mb-3 text-sm font-semibold text-ink-2">
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
      <div class="callout tone-bad mb-3">{targetError}</div>
    {/if}
    {#if targetMessage}
      <div class="callout tone-ok mb-3" aria-live="polite">{targetMessage}</div>
    {/if}

    {#if targets.length > 0}
      <ul class="mb-4 divide-y divide-line-soft">
        {#each targets as t (t.id)}
          <li class="flex flex-wrap items-center gap-x-3 gap-y-2 py-2">
            <span class="badge tone-neutral">{t.type}</span>
            <div class="min-w-0 flex-1">
              <div class="truncate text-sm font-medium text-ink-2">{t.name}</div>
              <div class="truncate font-mono text-[11px] text-ink-4" title={t.url}>{t.url}</div>
            </div>
            <div class="flex flex-wrap items-center gap-2">
              {#if !t.enabled}<span class="badge tone-muted">{i18n.m.settings.disabled}</span>{/if}
              {#if t.type === 'Telegram' && !t.hasToken}<span class="badge tone-warn">{i18n.m.settings.badge_no_token}</span>{/if}
              {#if t.notifyOnReplacement}<span class="badge tone-ok">{i18n.m.settings.badge_replaced}</span>{/if}
              {#if t.notifyOnFailure}<span class="badge tone-warn">{i18n.m.settings.badge_failed}</span>{/if}
              <button class="btn btn-ghost min-h-11 px-2 py-1 text-xs sm:min-h-0" onclick={() => testTarget(t)} disabled={testingTargetId !== null}>
                {testingTargetId === t.id ? i18n.m.settings.testing : i18n.m.settings.send_test}
              </button>
              <button class="btn btn-ghost min-h-11 px-2 py-1 text-xs sm:min-h-0" onclick={() => startEditTarget(t)}>{i18n.m.settings.edit}</button>
              <button class="btn btn-ghost min-h-11 px-2 py-1 text-xs text-bad sm:min-h-0" onclick={() => deleteTarget(t)}>{i18n.m.settings.remove}</button>
            </div>
          </li>
        {/each}
      </ul>
    {:else}
      <p class="mb-4 text-sm text-ink-4">{i18n.m.settings.targets_empty}</p>
    {/if}

    <div class="rounded-lg border border-line p-4">
      <h3 class="mb-3 text-sm font-semibold text-ink-2">
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
            <p class="mt-1 text-[11px] text-ink-4">{i18n.m.settings.discord_hint}</p>
          {:else if targetDraft.type === 'Telegram'}
            <p class="mt-1 text-[11px] text-ink-4">{i18n.m.settings.telegram_hint}</p>
          {/if}
        </div>
        <div>
          <label class="label" for="target-token">
            {targetDraft.type === 'Telegram' ? i18n.m.settings.bot_token : i18n.m.settings.token}
            {#if targetDraft.type !== 'Telegram'}<span class="text-ink-4">{i18n.m.settings.optional}</span>{/if}
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
      <div class="mt-3 grid gap-3">
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
    <ConfigSection id="appearance" title={i18n.m.settings.appearance_title} description={i18n.m.settings.appearance_desc}>
      <div class="max-w-sm">
        <label for="brand-style" class="label">{i18n.m.settings.brand_style}</label>
        <select id="brand-style" class="input" value={brand.style} onchange={(event) => brand.set(parseBrandStyle(event.currentTarget.value))}>
          <option value="precession">{i18n.m.settings.brand_precession}</option>
          <option value="stellar">{i18n.m.settings.brand_stellar}</option>
        </select>
      </div>
    </ConfigSection>
    <DiagnosticCapturePanel />
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
    <p class="mb-4 max-w-4xl text-sm leading-relaxed text-ink-3">
      {i18n.m.settings.backup_desc}
    </p>

    {#if backupError}
      <div class="callout tone-bad mb-3">{backupError}</div>
    {/if}
    {#if backupMessage}
      <div class="callout tone-ok mb-3">{backupMessage}</div>
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

  </div>

  {#if changedCount > 0 || message}
    <div
      class="settings-savebar sticky bottom-0 z-10 mt-6 flex flex-wrap items-center gap-3 p-4"
      data-settings-actions
    >
      {#if changedCount > 0}
        <span class="text-sm font-semibold text-ink">
          {plural(changedCount, i18n.m.settings.unsaved_changes_one, i18n.m.settings.unsaved_changes_other)}
        </span>
      {/if}
      {#if message}<span class="text-sm text-ok" role="status">{message}</span>{/if}
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

</div>

<style>
  .settings-layout { width: 100%; min-width: 0; }
  .settings-page-header { margin-bottom: 2rem; }
  .settings-overview { display: grid; gap: 1.75rem; }
  .settings-group-title {
    margin-bottom: .875rem; color: var(--ink-3); font-size: .6875rem; font-weight: 600;
    letter-spacing: .11em; text-transform: uppercase;
  }
  .settings-room-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 1rem; }
  .settings-room {
    display: flex; min-width: 0; min-height: 11rem; flex-direction: column; gap: .5rem;
    padding: 1.375rem; text-align: left;
  }
  .settings-room-symbols { display: flex; align-items: center; justify-content: space-between; margin-bottom: .65rem; }
  .settings-room-title { color: var(--ink); font-size: .9375rem; font-weight: 600; letter-spacing: -.015em; }
  .settings-room-description { color: var(--ink-3); font-size: .8125rem; line-height: 1.55; }
  .settings-room-state { margin-top: auto; padding-top: .875rem; color: var(--ink-2); font-size: .75rem; line-height: 1.6; overflow-wrap: anywhere; }
  .settings-room-heading { margin-bottom: 1.75rem; }
  .settings-heading-icon {
    display: flex; flex: none; align-items: center; justify-content: center;
    width: 2.75rem; height: 2.75rem; border-radius: .75rem; color: var(--accent); background: var(--sunken);
  }
  .settings-detail-open { display: grid; grid-template-columns: minmax(0, 1fr); gap: 1.25rem; width: 100%; }
  .settings-fields { display: grid; }
  .settings-field {
    display: grid; grid-template-columns: minmax(0, 1fr) minmax(10rem, .65fr); align-items: center;
    min-width: 0; column-gap: 1.5rem; padding: 1rem 0; border-bottom: 1px solid var(--divide-soft);
  }
  .settings-field:first-child { padding-top: 0; }
  .settings-field:last-child { border-bottom: 0; padding-bottom: 0; }
  .settings-field > span { grid-column: 2; }
  .settings-field-label p { margin-top: .3rem; color: var(--ink-3); font-size: .75rem; line-height: 1.5; }
  .settings-detail :global(.label) { text-transform: none; letter-spacing: 0; font-size: .8125rem; font-weight: 500; color: var(--ink-2); }
  .settings-detail :global(.input) { min-height: 2.75rem; }

  .settings-field .input { min-height: 2.75rem; }
  .settings-field :global(.label) { margin-bottom: 0; }
  .settings-field-changed { background: color-mix(in srgb, var(--accent) 8%, transparent); }
  .settings-video-fields {
    display: grid; gap: 1.5rem;
    margin-top: 1.5rem; padding-top: 1.5rem; border-top: 1px solid var(--divide);
  }
  .workload-details { border-top: 1px solid var(--divide-soft); margin-top: 1rem; padding-top: 1rem; }
  .workload-details summary { width: fit-content; cursor: pointer; color: var(--accent); font-size: .8125rem; font-weight: 600; border-radius: .375rem; padding: .5rem; margin-left: -.5rem; }
  .workload-details summary:hover { background: var(--lit); }
  .workload-intro { color: var(--ink-3); font-size: .75rem; line-height: 1.6; margin: .5rem 0 1rem; }
  .workload-preview { display: grid; gap: .35rem; margin-top: 1rem; padding: 1rem; border: 1px solid var(--divide-soft); border-radius: .75rem; background: var(--sunken); }
  .workload-preview span, .workload-preview small { color: var(--ink-3); font-size: .75rem; }
  .workload-preview strong { color: var(--ink); font-size: .875rem; font-weight: 600; }
  .settings-video-fields > div { display: grid; grid-template-columns: minmax(0, 1fr) minmax(10rem, .65fr); gap: 1.5rem; align-items: center; }
  .settings-savebar {
    border-radius: .875rem; background: var(--raised); box-shadow: var(--lift-3), inset 0 1px 0 var(--edge);
  }
  @media (max-width: 639px) {
    .settings-page-header { margin-bottom: 1.5rem; }
    .settings-room-grid, .settings-field, .settings-video-fields > div { grid-template-columns: minmax(0, 1fr); gap: .75rem; }
    .settings-field > span { grid-column: 1; }
    .settings-room { min-height: 10rem; padding: 1.125rem; }
    .settings-room-heading { margin-bottom: 1.25rem; }
  }
  @media (prefers-reduced-motion: reduce) { .settings-room { transition: none; } }
</style>
