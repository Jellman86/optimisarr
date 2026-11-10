<script lang="ts">
  // First run, as five steps with one thing in each: this machine, your library, your other apps,
  // your files, try one file. Nothing here encodes, replaces or deletes. A library is scanned when
  // it is saved, which only reads, so the last step has real files to offer.
  import { onMount, tick } from 'svelte'
  import { api, type HardwareCapability, type InventoryPage, type Library, type LibraryAccess, type LibraryOptions, type MediaFile, type SaveLibrary, type Settings, type SetupPath, type SetupReadiness, type SetupRecommendation, type SetupStorageRelationship, type ToolCheck } from '../api'
  import { i18n, mediaTypeLabel, plural, t } from '../i18n/i18n.svelte'
  import { firstUnavailableLibrary, libraryPathsReady as allLibraryPathsReady } from '../setup-library-readiness'
  import { filesChoice } from '../files-choice'
  import { containerLabel, formatDuration, formatSize } from '../format'
  import { newLibraryDefaults } from '../library-defaults'
  import { isVideoLibrary, presetName, summariseLibrary } from '../library-summary'
  import { setup } from '../stores/setup.svelte'
  import BrandMark from '../components/BrandMark.svelte'
  import FilesChoice from '../components/FilesChoice.svelte'
  import FolderPicker from '../components/FolderPicker.svelte'
  import Icon from '../components/Icon.svelte'
  import LanguageSelect from '../components/LanguageSelect.svelte'
  import LibrarySummary from '../components/LibrarySummary.svelte'
  import Thumbnail from '../components/Thumbnail.svelte'
  import Settings_ from './Settings.svelte'
  import { router, theme } from '../stores/ui.svelte'

  let viewStep = $state(setup.state?.currentStep ?? 1)
  let busy = $state(false)
  let loadingContext = $state(true)
  let contextReady = $state(false)
  let content = $state<HTMLElement>()
  let error = $state<string | null>(null)
  let errorSummary = $state<HTMLDivElement | null>(null)
  let tools = $state<ToolCheck[]>([])
  let hardware = $state<HardwareCapability | null>(null)
  let paths = $state<SetupPath[]>([])
  let storageRelationships = $state<SetupStorageRelationship[]>([])
  let selectedPlatform = $state<SetupReadiness['platform']>('compose')
  let detectedPlatform = $state<SetupReadiness['platform']>('compose')
  let platformInitialised = false
  let databaseAvailable = $state(false)
  let libraries = $state<Library[]>([])
  let options = $state<LibraryOptions | null>(null)
  let settings = $state<Settings | null>(null)
  let libraryAccess = $state<Record<number, LibraryAccess | null>>({})
  let retesting = $state(false)
  let retestMessage = $state<string | null>(null)
  let recommendation = $state<SetupRecommendation | null>(null)
  // The hardware encoder that passed its test is chosen unless the person picks the CPU.
  let encoderChoice = $state<'recommended' | 'cpu'>('recommended')
  let savedEncoderMode = 'Auto'
  let savedHardwareDecode = false
  const DRAFT_KEY = 'optimisarr.setup.plan.v2'
  type SetupDraft = { dryRunMode: boolean; retentionDays: number; encoderChoice: 'recommended' | 'cpu' }

  // Step 2: one small form. The full editor is in the library's settings after setup.
  let form = $state<SaveLibrary>(newLibraryDefaults())
  let editingId = $state<number | null>(null)
  let formOpen = $state(true)
  let formError = $state<string | null>(null)
  let savingLibrary = $state(false)
  let pickingFolder = $state(false)
  type Scan = { state: 'scanning' | 'done' | 'failed'; found: number; error: string | null }
  let scans = $state<Record<number, Scan>>({})

  // Step 3.
  let arrCount = $state(0)
  let serverCount = $state(0)
  let appsOpen = $state<'downloads' | 'servers' | null>(null)

  // Step 4: one answer per library, saved when the step is left.
  type FilesAnswer = { autoReplace: boolean; moveOnComplete: boolean; targetFolder: string | null; moveOverwrite: boolean; folderRefused: boolean }
  let filesAnswers = $state<Record<number, FilesAnswer>>({})
  let keepMode = $state<'approve' | 'days'>('approve')
  let keepDays = $state(30)

  // Step 5.
  let eligiblePage = $state<InventoryPage | null>(null)

  const w = $derived(i18n.m.wizard)
  const encoderNames: Record<SetupRecommendation['encoderMode'], string> = { Cpu: 'CPU', NvidiaNvenc: 'NVIDIA NVENC', IntelQsv: 'Intel Quick Sync', Vaapi: 'VAAPI' }
  const stepNames = $derived([w.step_machine, w.step_library, w.step_apps, w.step_files, w.step_try])
  const settingsPlace = $derived(`${i18n.m.nav.settings} → ${i18n.m.settings.room_files}`)
  const missingTools = $derived(tools.filter((tool) => tool.required && !tool.available).map((tool) => tool.name))
  const requiredToolsReady = $derived(tools.length > 0 && missingTools.length === 0)
  const requiredPathsReady = $derived(paths.length > 0 && paths.filter((path) => path.role !== 'library').every((path) => path.issue === 'none'))
  const machineReady = $derived(databaseAvailable && requiredToolsReady && requiredPathsReady)
  const libraryPathsReady = $derived(allLibraryPathsReady(libraries, libraryAccess))
  const hardwareOffered = $derived(recommendation !== null && recommendation.encoderMode !== 'Cpu')
  const retentionDays = $derived(keepMode === 'approve' ? 0 : Number(keepDays))
  const retentionError = $derived(keepMode === 'days' && (!Number.isInteger(retentionDays) || retentionDays < 1) ? i18n.m.setup.retention_invalid : null)
  const folderRefused = $derived(Object.values(filesAnswers).some((answer) => answer.moveOnComplete && (answer.folderRefused || !answer.targetFolder?.trim())))
  const automaticLibraries = $derived(libraries.filter((library) => library.enabled && library.autoEnqueueEnabled))
  // A scan can hand new files to a library that already optimises by itself, so "nothing else
  // will start" is said only when no library does.
  const startNote = $derived.by(() => {
    if (automaticLibraries.length === 0) return w.try_nothing_else
    return !settings?.dryRunMode && automaticLibraries.some((library) => library.autoReplace)
      ? `${i18n.m.setup.finish_automation} ${i18n.m.setup.finish_auto_replace}`
      : i18n.m.setup.finish_automation
  })

  type CheckState = 'ready' | 'fix' | 'fast' | 'slower' | 'tested' | 'missing'
  type Check = { key: string; title: string; text: string; state: CheckState; blocking: boolean; path?: SetupPath }
  const badge = $derived<Record<CheckState, { label: string; tone: string; icon: string }>>({
    ready: { label: w.badge_ready, tone: 'tone-ok', icon: 'check' },
    fast: { label: w.badge_fast, tone: 'tone-ok', icon: 'check' },
    tested: { label: w.badge_tested, tone: 'tone-ok', icon: 'check' },
    slower: { label: w.badge_slower, tone: 'tone-warn', icon: 'warning' },
    missing: { label: w.badge_missing, tone: 'tone-warn', icon: 'warning' },
    fix: { label: w.badge_fix, tone: 'tone-bad', icon: 'warning' },
  })

  function pathTitle(path: SetupPath): string {
    if (path.role === 'config') return w.path_config
    if (path.role === 'work') return w.path_work
    if (path.role === 'quarantine') return i18n.m.nav.quarantine
    return t(w.path_library, { name: path.name })
  }

  function pathText(path: SetupPath): string {
    if (path.issue !== 'none') return issueMessage(path)
    const readable = t(w.path_ok, { path: path.path })
    if (path.role === 'work') return `${readable} ${w.work_ok}`
    return path.availableBytes !== null && path.totalBytes !== null
      ? `${readable} ${t(i18n.m.setup.free_space, { free: formatSize(path.availableBytes), total: formatSize(path.totalBytes) })}.`
      : readable
  }

  function relationshipCheck(relationship: SetupStorageRelationship): Check {
    const title = t(w.replace_title, { name: relationship.libraryName })
    const key = `replace-${relationship.libraryId}`
    if (relationship.workAtomic === null || relationship.quarantineAtomic === null) return { key, title, text: w.replace_unknown, state: 'slower', blocking: false }
    if (relationship.workAtomic && relationship.quarantineAtomic) return { key, title, text: w.replace_fast, state: 'fast', blocking: false }
    return settings?.replacementAllowCrossFilesystem
      ? { key, title, text: w.replace_copy, state: 'slower', blocking: false }
      : { key, title, text: t(w.replace_blocked, { place: settingsPlace }), state: 'missing', blocking: false }
  }

  const checks = $derived.by(() => {
    const list: Check[] = []
    if (!databaseAvailable) list.push({ key: 'database', title: w.database_title, text: w.database_missing, state: 'fix', blocking: true })
    list.push(missingTools.length > 0
      ? { key: 'tools', title: w.tools_title, text: t(w.tools_missing, { tools: missingTools.join(', ') }), state: 'fix', blocking: true }
      : { key: 'tools', title: w.tools_title, text: w.tools_ready, state: 'ready', blocking: false })
    for (const path of paths) {
      list.push({ key: `path-${path.role}-${path.libraryId ?? 0}`, title: pathTitle(path), text: pathText(path), state: path.issue === 'none' ? 'ready' : 'fix', blocking: path.issue !== 'none' && path.role !== 'library', path })
    }
    for (const relationship of storageRelationships) list.push(relationshipCheck(relationship))
    return list
  })
  const hasProblem = $derived(checks.some((check) => check.state === 'fix'))
  const encoderText = $derived.by(() => {
    if (!recommendation) return ''
    const reason = recommendation.encoderReason === 'nvidia' ? i18n.m.setup.reason_encoder_nvidia
      : recommendation.encoderReason === 'intel' ? i18n.m.setup.reason_encoder_intel
        : recommendation.encoderReason === 'vaapi' ? i18n.m.setup.reason_encoder_vaapi
          : i18n.m.setup.reason_encoder_cpu
    return hardwareOffered ? `${reason} ${w.encoder_faster}` : reason
  })
  const qualityCheck = $derived.by<{ text: string; state: CheckState } | null>(() => {
    if (!recommendation) return null
    if (recommendation.vmafReason === 'unavailable') return { text: w.quality_missing, state: 'missing' }
    return recommendation.vmafReason === 'cuda-balanced' ? { text: w.quality_gpu, state: 'ready' } : { text: w.quality_cpu, state: 'slower' }
  })
  const chosenEncoderName = $derived(hardwareOffered && encoderChoice === 'recommended' && recommendation
    ? encoderNames[recommendation.encoderMode]
    : encoderChoice === 'cpu' || !hardwareOffered ? encoderNames.Cpu : savedEncoderMode)

  const kinds = $derived((options?.mediaTypes ?? ['Film', 'TV', 'Music', 'Photo']).filter((kind) => kind !== 'Other'))
  const presets = $derived([
    { profile: 'ConservativeHevc', info: i18n.m.presetInfo.balanced, recommended: true },
    { profile: 'CompatibilityH264', info: i18n.m.presetInfo.compatibility, recommended: false },
    { profile: 'ExperimentalAv1', info: i18n.m.presetInfo.efficiency, recommended: false },
  ])
  const formComplete = $derived(form.name.trim() !== '' && form.path.trim() !== '')
  // The summary is worked out from the values Save would send, so it cannot promise anything else.
  const summaryLines = $derived(summariseLibrary(form, {
    m: i18n.m, locale: i18n.locale,
    preset: options?.ruleProfileSpecs.find((spec) => spec.profile === form.ruleProfile) ?? null,
    retentionDays: settings ? retentionDays : null,
    dryRun: settings?.dryRunMode ?? false,
  }))

  // Why Continue is off, said beside it rather than left to be guessed.
  const blockedReason = $derived.by(() => {
    if (viewStep === 1 && contextReady && !machineReady) return w.blocked_machine
    if (viewStep === 2 && libraries.length === 0) return w.blocked_library_none
    if (viewStep === 2 && !libraryPathsReady) return w.blocked_library_path
    if (viewStep === 4 && retentionError) return retentionError
    if (viewStep === 4 && folderRefused) return w.blocked_folder
    return null
  })

  type Suggestion = { label: string; file: MediaFile }
  const suggestions = $derived.by(() => {
    const files = [...(eligiblePage?.items ?? [])].map((row) => row.file).sort((a, b) => a.sizeBytes - b.sizeBytes)
    if (files.length === 0) return []
    const picks: Suggestion[] = [{ label: w.pick_typical, file: files[Math.floor((files.length - 1) / 2)] }]
    if (files[0].id !== picks[0].file.id) picks.push({ label: w.pick_small, file: files[0] })
    const largest = files[files.length - 1]
    if (picks.every((pick) => pick.file.id !== largest.id)) picks.push({ label: w.pick_large, file: largest })
    return picks
  })

  onMount(() => {
    void loadContext()
  })

  async function loadContext(announce = false) {
    if (retesting || busy) return
    loadingContext = true
    contextReady = false
    if (announce) {
      retesting = true
      retestMessage = null
    }
    error = null
    try {
      const [readiness, hardwareResult, libraryResults, settingsResult, optionsResult] = await Promise.all([
        api.setupReadiness(),
        api.hardware().catch(() => null),
        api.libraries(),
        api.settings(),
        api.libraryOptions().catch(() => null),
      ])
      tools = readiness.tools
      paths = readiness.paths
      storageRelationships = readiness.storageRelationships
      detectedPlatform = readiness.platform
      if (!platformInitialised) {
        selectedPlatform = readiness.platform
        platformInitialised = true
      }
      databaseAvailable = readiness.databaseAvailable
      hardware = hardwareResult
      recommendation = readiness.recommendation
      options = optionsResult
      savedEncoderMode = settingsResult.encoderMode
      savedHardwareDecode = settingsResult.hardwareDecode
      if (!announce) {
        encoderChoice = settingsResult.encoderMode === 'Cpu' ? 'cpu' : 'recommended'
        keepMode = settingsResult.replacementQuarantineRetentionDays === 0 ? 'approve' : 'days'
        if (settingsResult.replacementQuarantineRetentionDays > 0) keepDays = settingsResult.replacementQuarantineRetentionDays
        restoreDraft(settingsResult)
        settings = settingsResult
      } else if (settings) {
        // A re-test refreshes what was measured and keeps what the person has chosen.
        settings = { ...settingsResult, dryRunMode: settings.dryRunMode }
      }
      await setLibraries(libraryResults)
      void loadApps()
      contextReady = true
      if (announce) retestMessage = w.tested
    } catch (err) {
      await showError(err instanceof Error ? err.message : i18n.m.setup.error_load)
    } finally {
      retesting = false
      loadingContext = false
    }
  }

  async function showError(message: string) {
    error = message
    await tick()
    errorSummary?.focus()
  }

  async function setLibraries(nextLibraries?: Library[]) {
    const results = nextLibraries ?? await api.libraries()
    const accessEntries = await Promise.all(results.map(async (library) => {
      try {
        return [library.id, await api.libraryAccess(library.id)] as const
      } catch {
        return [library.id, null] as const
      }
    }))
    libraries = results
    libraryAccess = Object.fromEntries(accessEntries)
    formOpen = results.length === 0 || editingId !== null
    for (const library of results) {
      filesAnswers[library.id] ??= { autoReplace: library.autoReplace, moveOnComplete: library.moveOnComplete, targetFolder: library.targetFolder, moveOverwrite: library.moveOverwrite, folderRefused: false }
    }
  }

  async function focusStep() {
    await tick()
    const heading = content?.querySelector<HTMLHeadingElement>('h1')
    heading?.focus({ preventScroll: true })
    heading?.scrollIntoView({ block: 'start' })
  }

  function changeStep(step: number) {
    error = null
    viewStep = step
    appsOpen = null
    void focusStep()
  }

  function startAdding() {
    form = newLibraryDefaults()
    editingId = null
    formError = null
    formOpen = true
  }

  function startEditing(library: Library) {
    form = { ...library }
    editingId = library.id
    formError = null
    formOpen = true
  }

  function cancelForm() {
    editingId = null
    formError = null
    formOpen = libraries.length === 0
    if (formOpen) form = newLibraryDefaults()
  }

  async function saveLibrary() {
    if (savingLibrary) return
    if (!formComplete) {
      formError = w.form_incomplete
      return
    }
    savingLibrary = true
    formError = null
    try {
      const body = { ...form, name: form.name.trim(), path: form.path.trim() }
      const saved = editingId === null ? await api.createLibrary(body) : await api.updateLibrary(editingId, body)
      editingId = null
      form = newLibraryDefaults()
      await setLibraries()
      // Reading the folder is all a scan does. A new library does not optimise by itself.
      void scanLibrary(saved.id)
    } catch (err) {
      formError = err instanceof Error ? err.message : i18n.m.libraries.error_save
    } finally {
      savingLibrary = false
    }
  }

  async function scanLibrary(id: number) {
    scans[id] = { state: 'scanning', found: 0, error: null }
    try {
      const summary = await api.scanLibrary(id)
      scans[id] = { state: 'done', found: summary.discovered, error: null }
    } catch (err) {
      scans[id] = { state: 'failed', found: 0, error: err instanceof Error ? err.message : i18n.m.setup.scan_error }
    }
  }

  function scanText(scan: Scan): string {
    if (scan.state === 'scanning') return w.scan_reading
    if (scan.state === 'failed') return t(w.scan_failed, { error: scan.error ?? '' })
    return scan.found === 0 ? w.scan_none : plural(scan.found, w.scan_found_one, w.scan_found_other, scan.found.toLocaleString())
  }

  async function loadApps() {
    const [arrs, watchers] = await Promise.all([api.arrConnections().catch(() => []), api.activityWatchers().catch(() => [])])
    arrCount = arrs.length
    serverCount = watchers.length
  }

  function toggleApps(room: 'downloads' | 'servers') {
    appsOpen = appsOpen === room ? null : room
    // The embedded forms save for themselves; closing them is when the counts can have changed.
    if (appsOpen === null) void loadApps()
  }

  async function saveFilesAnswers() {
    for (const library of libraries) {
      const answer = filesAnswers[library.id]
      if (!answer) continue
      const targetFolder = answer.moveOnComplete ? answer.targetFolder?.trim() || null : library.targetFolder
      if (answer.autoReplace === library.autoReplace && answer.moveOnComplete === library.moveOnComplete
        && targetFolder === library.targetFolder && answer.moveOverwrite === library.moveOverwrite) continue
      await api.updateLibrary(library.id, { ...library, autoReplace: answer.autoReplace && !answer.moveOnComplete, moveOnComplete: answer.moveOnComplete, targetFolder, moveOverwrite: answer.moveOverwrite })
    }
    await setLibraries()
  }

  async function loadEligible() {
    try {
      eligiblePage = await api.inventory({ show: 'eligible', page: 1, pageSize: 100 })
    } catch {
      /* The step still offers Finish; the suggestions are a convenience. */
    }
  }

  // Files are read in the background after a scan, so the suggestions fill in while the step is open.
  $effect(() => {
    if (viewStep !== 5 || !contextReady) return
    void loadEligible()
    const timer = setInterval(() => {
      if ((eligiblePage?.counts.unprobed ?? 1) > 0 || Object.values(scans).some((scan) => scan.state === 'scanning')) void loadEligible()
    }, 4000)
    return () => clearInterval(timer)
  })

  $effect(() => {
    if (viewStep === 3) void loadApps()
  })

  async function continueStep() {
    if (busy || loadingContext || retesting || !contextReady || !setup.state || blockedReason) return
    busy = true
    error = null
    try {
      if (viewStep === 2) {
        await setLibraries()
        if (!libraryPathsReady) {
          const unavailable = firstUnavailableLibrary(libraries, libraryAccess)
          await showError(unavailable && libraryAccess[unavailable.id] ? libraryAccess[unavailable.id]!.message : i18n.m.setup.access_checking)
          return
        }
      }
      if (viewStep === 4) await saveFilesAnswers()
      if (viewStep <= setup.state.completedStep) changeStep(viewStep + 1)
      else {
        const state = await setup.advance(viewStep)
        changeStep(state?.currentStep ?? viewStep + 1)
      }
    } catch (err) {
      await showError(err instanceof Error ? err.message : i18n.m.setup.error_save)
    } finally {
      busy = false
    }
  }

  // Finishing saves the two server-wide answers and marks setup complete. It queues nothing.
  async function finish(destination: string) {
    if (busy || !settings || !contextReady) return
    if (!machineReady) return void showError(w.blocked_machine)
    if (libraries.length === 0) return void showError(i18n.m.setup.library_required_error)
    if (retentionError) return void showError(retentionError)
    busy = true
    error = null
    try {
      const useRecommendedEncoder = hardwareOffered && encoderChoice === 'recommended'
      const plan: Settings = {
        ...settings,
        replacementQuarantineRetentionDays: retentionDays,
        encoderMode: encoderChoice === 'cpu' && hardwareOffered ? 'Cpu' : savedEncoderMode,
        hardwareDecode: encoderChoice === 'cpu' && hardwareOffered ? false : savedHardwareDecode,
      }
      const receipt = await api.applySetup({ settings: plan, useRecommendedEncoder, applyRecommendedVmaf: false, applyRecommendedSchedule: false })
      clearDraft()
      router.go(destination)
      setup.accept(receipt.state)
    } catch (err) {
      await showError(err instanceof Error ? err.message : i18n.m.setup.error_save)
    } finally {
      busy = false
    }
  }

  function stepStatus(step: number) {
    if (step === viewStep) return i18n.m.setup.status_current
    if (setup.state && step <= setup.state.completedStep) return i18n.m.setup.status_completed
    return i18n.m.setup.status_pending
  }

  function issueMessage(path: SetupPath): string {
    if (path.issue === 'missing') return i18n.m.setup.issue_missing
    if (path.issue === 'unreadable') return i18n.m.setup.issue_unreadable
    if (path.issue === 'unwritable') return i18n.m.setup.issue_unwritable
    return i18n.m.setup.issue_low_space
  }

  function platformLabel(platform: SetupReadiness['platform']): string {
    if (platform === 'local') return i18n.m.setup.platform_local
    if (platform === 'unraid') return i18n.m.setup.platform_unraid
    if (platform === 'truenas') return i18n.m.setup.platform_truenas
    return i18n.m.setup.platform_compose
  }

  function recoverySteps(): string {
    if (selectedPlatform === 'local') return i18n.m.setup.fix_local
    if (selectedPlatform === 'unraid') return i18n.m.setup.fix_unraid
    if (selectedPlatform === 'truenas') return i18n.m.setup.fix_truenas
    return i18n.m.setup.fix_compose
  }

  function filesSentence(library: Library): string {
    const choice = filesChoice(filesAnswers[library.id] ?? library)
    const after = choice === 'elsewhere'
      ? (filesAnswers[library.id]?.targetFolder ? t(i18n.m.librarySummary.after_elsewhere, { folder: filesAnswers[library.id].targetFolder ?? '' }) : i18n.m.librarySummary.after_elsewhere_unset)
      : choice === 'auto' ? i18n.m.librarySummary.after_auto : i18n.m.librarySummary.after_ask
    return `${library.name}: ${after}`
  }

  const keptSentence = $derived(retentionDays > 0
    ? plural(retentionDays, i18n.m.librarySummary.originals_days_one, i18n.m.librarySummary.originals_days_other)
    : i18n.m.librarySummary.originals_kept)

  function clearDraft() {
    try { localStorage.removeItem(DRAFT_KEY) } catch { /* Storage is optional in restricted browsers. */ }
  }

  function restoreDraft(target: Settings) {
    try {
      const raw = localStorage.getItem(DRAFT_KEY)
      if (!raw) return
      const draft = JSON.parse(raw) as Partial<SetupDraft>
      if (typeof draft.dryRunMode === 'boolean') target.dryRunMode = draft.dryRunMode
      if (typeof draft.retentionDays === 'number' && Number.isInteger(draft.retentionDays) && draft.retentionDays >= 0) {
        keepMode = draft.retentionDays === 0 ? 'approve' : 'days'
        if (draft.retentionDays > 0) keepDays = draft.retentionDays
      }
      if (draft.encoderChoice === 'cpu' || draft.encoderChoice === 'recommended') encoderChoice = draft.encoderChoice
    } catch {
      clearDraft()
    }
  }

  $effect(() => {
    if (!settings) return
    const draft: SetupDraft = { dryRunMode: settings.dryRunMode, retentionDays: Number.isInteger(retentionDays) ? retentionDays : 0, encoderChoice }
    try { localStorage.setItem(DRAFT_KEY, JSON.stringify(draft)) } catch { /* Keep the in-memory plan usable. */ }
  })
</script>

<div class="min-h-dvh bg-ground px-4 py-5 text-ink sm:px-6 sm:py-8">
  <header class="mx-auto mb-5 flex max-w-5xl flex-wrap items-center gap-3">
    <BrandMark class="h-9 w-9" />
    <div class="min-w-0 flex-1">
      <div class="font-bold tracking-tight text-ink">Optimisarr</div>
      <div class="text-xs text-ink-3">{i18n.m.app.tagline}</div>
    </div>
    <div class="flex items-center gap-2 [&_button]:min-h-11 [&_button]:min-w-11">
      <button type="button" class="btn btn-ghost min-h-11 min-w-11" onclick={() => theme.toggle()} aria-label={i18n.m.nav.toggle_theme} aria-describedby="setup-theme-mode">
        <Icon name={theme.icon} class="h-5 w-5" />
        <span id="setup-theme-mode" class="sr-only">{i18n.m.nav[`theme_${theme.mode}`]}</span>
      </button>
      <LanguageSelect compact />
    </div>
  </header>

  <div class="mx-auto grid grid-rows-[auto_1fr] md:grid-rows-1 min-h-[min(43rem,calc(100dvh-8rem))] max-w-5xl card overflow-hidden rounded-2xl md:grid-cols-[15rem_minmax(0,1fr)]">
    <aside class="border-b border-line bg-sunken px-4 py-5 md:border-b-0 md:border-r md:px-5 md:py-7">
      <p class="mb-4 text-xs font-semibold text-accent">
        {t(i18n.m.setup.step_of, { current: viewStep, total: setup.state?.stepCount ?? 5 })}
      </p>
      <ol class="grid grid-cols-5 gap-1 md:block" aria-label={i18n.m.setup.progress_label}>
        {#each stepNames as name, index}
          {@const number = index + 1}
          <li class="relative md:pb-6" aria-current={number === viewStep ? 'step' : undefined} aria-label={`${name}: ${stepStatus(number)}`}>
            {#if number < stepNames.length}
              <span class="absolute left-[0.9rem] top-8 hidden h-[calc(100%-1.25rem)] w-px bg-line md:block"></span>
            {/if}
            <div class="relative flex flex-col items-center text-center md:flex-row md:items-start md:gap-3 md:text-left" >
              <span class="flex h-7 w-7 flex-none items-center justify-center rounded-full text-xs font-bold {number <= (setup.state?.completedStep ?? 0) ? 'tone-ok' : number === viewStep ? 'tone-accent' : 'bg-sunken text-ink-3'}">
                {#if number <= (setup.state?.completedStep ?? 0)}<Icon name="check" class="h-4 w-4" />{:else}{number}{/if}
              </span>
              <span class="hidden min-w-0 pt-0.5 md:block">
                <span class="block text-sm font-semibold text-ink-2">{name}</span>
                <span class="block text-xs text-ink-3">{stepStatus(number)}</span>
              </span>
            </div>
          </li>
        {/each}
      </ol>
    </aside>

    <main class="flex min-w-0 flex-col [&_h1]:scroll-mt-6 px-5 py-6 sm:px-8 sm:py-8 md:px-10" id="setup-content" bind:this={content} aria-busy={loadingContext || busy}>
      {#if loadingContext}<p class="mb-4 text-sm text-ink-3" role="status">{i18n.m.setup.loading}</p>{/if}
      {#if error}
        <div bind:this={errorSummary} class="callout tone-bad mb-5 px-4 py-3" role="alert" tabindex="-1">
          <div class="font-semibold">{i18n.m.setup.error_heading}</div>
          <div>{error}</div>
          {#if !contextReady}<button class="btn mt-3 min-h-11" type="button" onclick={() => void loadContext()} disabled={loadingContext}>{i18n.m.setup.retry}</button>{/if}
        </div>
      {/if}

      <fieldset class="min-w-0 flex-1" disabled={busy || loadingContext}>
        {#if viewStep === 1}
          <h1 tabindex="-1" class="page-title">{w.machine_heading}</h1>
          <p class="mt-2 max-w-2xl text-sm leading-6 text-ink-2">{w.machine_body}</p>

          <div class="mt-7 max-w-3xl divide-y divide-line border-y border-line" data-setup-checks>
            {#each checks as check (check.key)}
              <div>
              {@render checkRow(check.key, check.title, check.text, check.state)}
              {#if check.state === 'fix' && check.path}
                <div class="callout tone-bad mb-4 ml-9" data-setup-fix>
                  {#if check.path.issue === 'lowSpace' && check.path.requiredFreeBytes !== null}
                    <p class="text-xs leading-5 text-ink-2">{t(i18n.m.setup.space_requirement, { required: formatSize(check.path.requiredFreeBytes) })}</p>
                  {/if}
                  <div class="text-xs font-semibold text-ink-3">{i18n.m.setup.recovery_title}</div>
                  <div class="mt-2 grid grid-cols-2 gap-1 sm:flex sm:flex-wrap" role="group" aria-label={i18n.m.setup.deployment_label}>
                    {#each ['local', 'compose', 'unraid', 'truenas'] as platform (platform)}
                      <button class="choice min-h-11 rounded-md px-2.5 text-xs font-medium {selectedPlatform === platform ? 'choice-selected' : 'text-ink-2'}" type="button"
                        aria-pressed={selectedPlatform === platform} onclick={() => (selectedPlatform = platform as SetupReadiness['platform'])}>
                        {platformLabel(platform as SetupReadiness['platform'])}
                        {#if detectedPlatform === platform}<span class="block text-xs font-normal opacity-70">{i18n.m.setup.platform_detected}</span>{/if}
                      </button>
                    {/each}
                  </div>
                  <p class="mt-2 text-xs leading-5 text-ink-2">{recoverySteps()}</p>
                </div>
              {/if}
              </div>
            {/each}
            {#if recommendation}
              <div>
              {@render checkRow('encoder', w.encoder_title, encoderText, hardwareOffered ? 'tested' : 'ready')}
              {#if hardwareOffered}
                <div class="mb-4 ml-9 flex flex-wrap gap-2" role="group" aria-label={w.encoder_title}>
                  <button class="choice min-h-11 rounded-md px-3 text-sm font-medium {encoderChoice === 'recommended' ? 'choice-selected' : 'text-ink-2'}" type="button"
                    aria-pressed={encoderChoice === 'recommended'} onclick={() => (encoderChoice = 'recommended')}>{t(w.encoder_recommended, { name: encoderNames[recommendation.encoderMode] })}</button>
                  <button class="choice min-h-11 rounded-md px-3 text-sm font-medium {encoderChoice === 'cpu' ? 'choice-selected' : 'text-ink-2'}" type="button"
                    aria-pressed={encoderChoice === 'cpu'} onclick={() => (encoderChoice = 'cpu')}>{w.encoder_cpu}</button>
                </div>
              {/if}
              </div>
              {#if qualityCheck}{@render checkRow('quality', w.quality_title, qualityCheck.text, qualityCheck.state)}{/if}
            {/if}
          </div>

          <!-- The proof behind each line. Closed when all is well, open when something needs fixing. -->
          <details class="mt-4 max-w-3xl text-xs text-ink-3" open={hasProblem} data-setup-details>
            <summary class="focus-ring inline-flex min-h-11 cursor-pointer items-center rounded px-1 font-medium">{w.details}</summary>
            <ul class="mt-2 space-y-1.5 font-mono leading-5">
              {#each tools as tool (tool.name)}
                <li class="break-words">{tool.name} · {tool.version ?? tool.error ?? tool.command} · {tool.required ? i18n.m.setup.required : i18n.m.setup.optional}</li>
              {/each}
              {#each paths as path (`${path.role}-${path.libraryId ?? 0}`)}
                <li class="break-all">{path.path} — {path.fileSystemId && path.mountId ? t(i18n.m.setup.mount_evidence, { type: path.fileSystemType ?? '—', filesystem: path.fileSystemId, mount: path.mountId }) : i18n.m.setup.mount_unknown}</li>
              {/each}
              {#if hardware}<li>{t(i18n.m.setup.encoder_summary, { count: hardware.encoders.filter((encoder) => encoder.available).length })}</li>{/if}
            </ul>
          </details>

          <p class="callout mt-4 max-w-3xl text-sm leading-6 text-ink-2">{w.private_note}</p>
          <div class="mt-5 flex flex-wrap items-center gap-3" aria-live="polite">
            <button class="btn min-h-11" type="button" onclick={() => void loadContext(true)} disabled={busy || retesting}>
              <Icon name="retry" class="h-4 w-4 motion-reduce:animate-none {retesting ? 'animate-spin' : ''}" />
              {retesting ? w.testing : w.test_again}
            </button>
            {#if retestMessage}<span class="text-sm text-ok">{retestMessage}</span>{/if}
          </div>
        {:else if viewStep === 2}
          <h1 tabindex="-1" class="page-title">{w.library_heading}</h1>
          <p class="mt-2 max-w-2xl text-sm leading-6 text-ink-2">{w.library_body}</p>

          {#if libraries.length > 0}
            <section class="mt-7 max-w-3xl" aria-labelledby="setup-libraries-heading">
              <h2 id="setup-libraries-heading" class="font-semibold text-ink">{w.saved_heading}</h2>
              <div class="mt-3 divide-y divide-line border-y border-line">
                {#each libraries as library (library.id)}
                  {@const access = libraryAccess[library.id]}
                  {@const scan = scans[library.id]}
                  <article class="flex flex-col gap-3 py-4 sm:flex-row sm:items-center sm:justify-between" aria-label={library.name} data-setup-library={library.id}>
                    <div class="min-w-0">
                      <div class="flex flex-wrap items-center gap-2">
                        <span class="font-semibold text-ink">{library.name}</span>
                        <span class="badge tone-neutral">{mediaTypeLabel(library.mediaType, i18n.m)}</span>
                        {#if isVideoLibrary(library.mediaType)}<span class="badge tone-neutral">{presetName(library.ruleProfile, i18n.m)}</span>{/if}
                      </div>
                      <div class="mt-1 break-all font-mono text-xs text-ink-3">{library.path}</div>
                      <div class="mt-2 text-xs font-medium {access?.ok ? 'text-ok' : 'text-warn'}">{access?.ok ? w.folder_ok : access?.message ?? i18n.m.setup.access_checking}</div>
                      {#if access && !access.ok}<p class="callout tone-warn mt-2 text-xs leading-5">{recoverySteps()}</p>{/if}
                      {#if scan}
                        <div class="mt-2 text-xs {scan.state === 'failed' ? 'text-bad' : 'text-ink-2'}" role="status" data-setup-scan={scan.state}>{scanText(scan)}</div>
                        {#if scan.state === 'failed'}<button class="btn mt-2 min-h-11" type="button" onclick={() => void scanLibrary(library.id)}>{w.scan_retry}</button>{/if}
                      {/if}
                    </div>
                    <button class="btn min-h-11 flex-none" type="button" onclick={() => startEditing(library)} aria-label={`${i18n.m.setup.change}: ${library.name}`}>{i18n.m.setup.change}</button>
                  </article>
                {/each}
              </div>
            </section>
          {/if}

          {#if formOpen}
            <form class="mt-7 max-w-3xl space-y-5" onsubmit={(event) => { event.preventDefault(); void saveLibrary() }} data-setup-library-form>
              <div>
                <label class="label" for="setup-library-name">{w.name_label}</label>
                <input id="setup-library-name" class="input max-w-md" bind:value={form.name} autocomplete="off" />
              </div>
              <div>
                <div class="label" id="setup-library-kind">{w.kind_label}</div>
                <div class="flex flex-wrap gap-2" role="group" aria-labelledby="setup-library-kind">
                  {#each kinds as kind (kind)}
                    <button class="choice min-h-11 rounded-md px-3 text-sm font-medium {form.mediaType === kind ? 'choice-selected' : 'text-ink-2'}" type="button"
                      aria-pressed={form.mediaType === kind} onclick={() => (form.mediaType = kind)}>{mediaTypeLabel(kind, i18n.m)}</button>
                  {/each}
                </div>
              </div>
              <div>
                <label class="label" for="setup-library-path">{w.folder_label}</label>
                <div class="flex max-w-xl flex-wrap gap-2">
                  <input id="setup-library-path" class="input min-w-0 flex-1 font-mono" bind:value={form.path} autocomplete="off" spellcheck="false" />
                  <button class="btn min-h-11" type="button" onclick={() => (pickingFolder = true)}>{w.browse}</button>
                </div>
              </div>
              {#if isVideoLibrary(form.mediaType)}
                <fieldset>
                  <legend class="label">{w.preset_legend}</legend>
                  <div class="grid gap-2">
                    {#each presets as preset (preset.profile)}
                      <label class="choice flex cursor-pointer items-start gap-3 rounded-lg p-3 {form.ruleProfile === preset.profile ? 'choice-selected' : ''}">
                        <input class="mt-1 h-4 w-4 accent-accent" type="radio" name="setup-preset" value={preset.profile} checked={form.ruleProfile === preset.profile}
                          onchange={() => (form.ruleProfile = preset.profile)} aria-describedby={`setup-preset-${preset.profile}`} />
                        <span class="min-w-0">
                          <span class="font-semibold text-ink">{presetName(preset.profile, i18n.m)}</span>
                          {#if preset.recommended}<span class="badge tone-ok ml-2">{w.preset_recommended}</span>{/if}
                          <span id={`setup-preset-${preset.profile}`} class="mt-1 block text-sm leading-6 text-ink-3">{preset.info}</span>
                        </span>
                      </label>
                    {/each}
                  </div>
                  <p class="mt-2 text-xs leading-5 text-ink-3">{w.preset_more}</p>
                </fieldset>
              {/if}
              <LibrarySummary lines={summaryLines} headingId="setup-summary-heading" intro={t(w.summary_heading, { name: form.name.trim() || w.summary_unnamed })} />
              {#if formError}<p class="callout tone-bad text-sm" role="alert">{formError}</p>{/if}
              <div class="flex flex-wrap gap-2">
                <button class="btn btn-primary min-h-11" type="submit" disabled={savingLibrary}>{savingLibrary ? i18n.m.setup.saving : editingId === null ? w.add : w.save_changes}</button>
                {#if libraries.length > 0}<button class="btn min-h-11" type="button" onclick={cancelForm} disabled={savingLibrary}>{i18n.m.common.cancel}</button>{/if}
              </div>
            </form>
          {:else}
            <button class="btn mt-5 min-h-11" type="button" onclick={startAdding}><Icon name="plus" class="h-4 w-4" />{w.add_another}</button>
          {/if}
        {:else if viewStep === 3}
          <h1 tabindex="-1" class="page-title">{w.apps_heading}</h1>
          <p class="mt-2 max-w-2xl text-sm leading-6 text-ink-2">{w.apps_body}</p>

          <div class="mt-7 max-w-3xl space-y-4">
            {#each [
              { room: 'downloads' as const, title: w.arr_title, count: arrCount, points: [w.arr_1, w.arr_2, w.arr_3] },
              { room: 'servers' as const, title: w.servers_title, count: serverCount, points: [w.servers_1, w.servers_2, w.servers_3] },
            ] as app (app.room)}
              <section class="card p-4 sm:p-5" aria-labelledby={`setup-app-${app.room}`} data-setup-app={app.room}>
                <div class="flex flex-wrap items-center justify-between gap-2">
                  <h2 id={`setup-app-${app.room}`} class="font-semibold text-ink">{app.title}</h2>
                  <span class="badge {app.count > 0 ? 'tone-ok' : 'tone-muted'}" data-setup-app-count>{app.count > 0 ? plural(app.count, w.connected_one, w.connected_other) : w.none_connected}</span>
                </div>
                <ul class="mt-3 list-disc space-y-1.5 pl-5 text-sm leading-6 text-ink-2">
                  {#each app.points as point (point)}<li>{point}</li>{/each}
                </ul>
                <button class="btn mt-3 min-h-11" type="button" aria-expanded={appsOpen === app.room} onclick={() => toggleApps(app.room)}>{appsOpen === app.room ? w.apps_close : w.apps_open}</button>
                {#if appsOpen === app.room}<div class="mt-4" data-setup-app-form><Settings_ embeddedRoom={app.room} /></div>{/if}
              </section>
            {/each}
          </div>
          <p class="mt-4 max-w-3xl text-xs leading-5 text-ink-3">{t(w.apps_later, { downloads: `${i18n.m.nav.settings} → ${i18n.m.settings.room_downloads}`, servers: `${i18n.m.nav.settings} → ${i18n.m.settings.room_servers}` })}</p>
        {:else if viewStep === 4}
          <h1 tabindex="-1" class="page-title">{w.files_heading}</h1>
          <p class="mt-2 max-w-2xl text-sm leading-6 text-ink-2">{w.files_body}</p>

          {#if settings}
            <div class="mt-7 max-w-3xl space-y-6">
              {#each libraries as library (library.id)}
                {@const answer = filesAnswers[library.id]}
                {#if answer}
                  <section aria-labelledby={`setup-files-${library.id}`} data-setup-files={library.id}>
                    {#if libraries.length > 1}<h2 id={`setup-files-${library.id}`} class="mb-2 font-semibold text-ink">{library.name}</h2>{:else}<h2 id={`setup-files-${library.id}`} class="sr-only">{library.name}</h2>{/if}
                    <FilesChoice bind:autoReplace={answer.autoReplace} bind:moveOnComplete={answer.moveOnComplete} bind:targetFolder={answer.targetFolder}
                      bind:moveOverwrite={answer.moveOverwrite} bind:folderRefused={answer.folderRefused}
                      libraryId={library.id} libraryName={library.name} libraryPath={library.path}
                      retentionDays={retentionError ? null : retentionDays} dryRun={settings.dryRunMode} offerAutomatic={false} />
                  </section>
                {/if}
              {/each}
              <p class="text-xs leading-5 text-ink-3">{w.files_auto_later}</p>

              <fieldset data-setup-keep>
                <legend class="font-semibold text-ink">{w.keep_legend}</legend>
                <div class="mt-2 grid gap-2">
                  <label class="choice flex min-h-11 cursor-pointer items-center gap-3 rounded-lg px-3 py-2 {keepMode === 'approve' ? 'choice-selected' : ''}">
                    <input class="h-4 w-4 accent-accent" type="radio" name="setup-keep" checked={keepMode === 'approve'} onchange={() => (keepMode = 'approve')} />
                    <span class="text-sm text-ink">{w.keep_until}</span>
                  </label>
                  <div class="choice flex flex-wrap items-center gap-3 rounded-lg px-3 py-2 {keepMode === 'days' ? 'choice-selected' : ''}">
                    <label class="flex min-h-11 cursor-pointer items-center gap-3">
                      <input class="h-4 w-4 accent-accent" type="radio" name="setup-keep" checked={keepMode === 'days'} onchange={() => (keepMode = 'days')} />
                      <span class="text-sm text-ink">{w.keep_days}</span>
                    </label>
                    {#if keepMode === 'days'}
                      <label class="flex items-center gap-2 text-sm text-ink-2">
                        <span>{w.days_label}</span>
                        <input id="setup-retention" class="input w-24" type="number" min="1" step="1" bind:value={keepDays} aria-invalid={retentionError ? 'true' : undefined} aria-describedby={retentionError ? 'setup-retention-error' : undefined} />
                      </label>
                      {#if retentionError}<p id="setup-retention-error" class="basis-full text-xs text-bad">{retentionError}</p>{/if}
                    {/if}
                  </div>
                </div>
                <p class="mt-2 text-xs leading-5 text-ink-3">{w.keep_warning}</p>
              </fieldset>

              <label class="flex cursor-pointer items-start gap-3 border-y border-line py-4" data-setup-lock>
                <input class="mt-1 h-4 w-4 accent-accent" type="checkbox" bind:checked={settings.dryRunMode} />
                <span><span class="block font-semibold text-ink">{w.lock_title}</span><span class="mt-1 block text-sm leading-6 text-ink-3">{w.lock_body}</span></span>
              </label>
            </div>
          {/if}
        {:else}
          <h1 tabindex="-1" class="page-title">{w.try_heading}</h1>
          <p class="mt-2 max-w-2xl text-sm leading-6 text-ink-2" data-setup-try-intro>
            {#if eligiblePage}{t(w.try_counts, { all: eligiblePage.counts.all.toLocaleString(), eligible: eligiblePage.counts.eligible.toLocaleString() })}{' '}{/if}{startNote}
          </p>

          {#if suggestions.length > 0}
            <div class="mt-6 max-w-3xl divide-y divide-line border-y border-line" data-setup-suggestions>
              {#each suggestions as suggestion (suggestion.file.id)}
                {@const file = suggestion.file}
                <article class="flex flex-wrap items-center gap-3 py-4" aria-label={suggestion.label}>
                  <Thumbnail mediaFileId={file.id} />
                  <div class="min-w-0 flex-1">
                    <div class="text-xs font-semibold text-ink-3">{suggestion.label}</div>
                    <div class="mt-0.5 break-words font-semibold text-ink">{file.relativePath.split(/[\\/]/).pop()}</div>
                    <div class="mt-1 flex flex-wrap gap-x-3 gap-y-1 text-xs text-ink-3">
                      <span>{formatSize(file.sizeBytes)}</span>
                      {#if containerLabel(file.container, file.relativePath)}<span>{containerLabel(file.container, file.relativePath)}</span>{/if}
                      {#if file.durationSeconds !== null}<span>{formatDuration(file.durationSeconds)}</span>{/if}
                    </div>
                  </div>
                  <button class="btn min-h-11 flex-none" type="button" disabled={busy} onclick={() => void finish(`/inventory/optimise/${file.id}`)}>{i18n.m.onePlan.open}</button>
                </article>
              {/each}
            </div>
            <p class="mt-3 max-w-3xl text-xs leading-5 text-ink-3">{w.choose_note}</p>
          {:else if eligiblePage && eligiblePage.counts.unprobed === 0 && !Object.values(scans).some((scan) => scan.state === 'scanning')}
            <p class="callout mt-6 max-w-3xl text-sm leading-6" data-setup-try-none>{w.try_none}</p>
          {:else}
            <p class="mt-6 max-w-3xl text-sm text-ink-3" role="status" data-setup-try-waiting>{w.try_waiting}</p>
          {/if}
          {#if eligiblePage && eligiblePage.counts.unprobed > 0}
            <p class="mt-3 max-w-3xl text-xs leading-5 text-ink-3" role="status">{plural(eligiblePage.counts.unprobed, w.still_reading_one, w.still_reading_other, eligiblePage.counts.unprobed.toLocaleString())}</p>
          {/if}

          <section class="mt-8 max-w-3xl" aria-labelledby="setup-summary-title" data-setup-summary>
            <h2 id="setup-summary-title" class="font-semibold text-ink">{w.setup_heading}</h2>
            <ul class="mt-3 divide-y divide-line border-y border-line text-sm text-ink-2">
              <li class="flex flex-wrap items-center justify-between gap-3 py-3"><span class="min-w-0 flex-1">{t(w.sum_encoder, { name: chosenEncoderName })}</span><button class="btn min-h-11" type="button" onclick={() => changeStep(1)}>{i18n.m.setup.change}</button></li>
              <li class="flex flex-wrap items-center justify-between gap-3 py-3">
                <span class="min-w-0 flex-1">{#each libraries as library (library.id)}<span class="block break-words">{library.name} · {isVideoLibrary(library.mediaType) ? presetName(library.ruleProfile, i18n.m) : mediaTypeLabel(library.mediaType, i18n.m)} · <span class="font-mono text-xs">{library.path}</span></span>{/each}</span>
                <button class="btn min-h-11" type="button" onclick={() => changeStep(2)}>{i18n.m.setup.change}</button>
              </li>
              <li class="flex flex-wrap items-center justify-between gap-3 py-3"><span class="min-w-0 flex-1">{arrCount + serverCount === 0 ? w.sum_apps_none : t(w.sum_apps, { arr: arrCount, servers: serverCount })}</span><button class="btn min-h-11" type="button" onclick={() => changeStep(3)}>{i18n.m.setup.change}</button></li>
              <li class="flex flex-wrap items-center justify-between gap-3 py-3">
                <span class="min-w-0 flex-1">{#each libraries as library (library.id)}<span class="block break-words">{filesSentence(library)}</span>{/each}<span class="block">{keptSentence}{#if settings?.dryRunMode}{' '}{i18n.m.librarySummary.dry_run}{/if}</span></span>
                <button class="btn min-h-11" type="button" onclick={() => changeStep(4)}>{i18n.m.setup.change}</button>
              </li>
              {#if automaticLibraries.length === 0}<li class="py-3">{w.sum_automatic_off}</li>{/if}
            </ul>
          </section>
        {/if}
      </fieldset>

      <div class="mt-8 border-t border-line pt-5">
        {#if blockedReason}<p id="setup-blocked" class="mb-3 text-sm text-warn" role="status" data-setup-blocked>{blockedReason}</p>{/if}
        <div class="flex flex-wrap items-center justify-between gap-3">
          <button class="btn min-h-11" type="button" onclick={() => changeStep(Math.max(1, viewStep - 1))} disabled={viewStep === 1 || busy || loadingContext}>{i18n.m.setup.back}</button>
          {#if viewStep === 5}
            <button class="btn btn-primary min-h-11 px-5" type="button" onclick={() => void finish('/')} disabled={busy || loadingContext || !contextReady}>{busy ? w.finishing : w.finish_dashboard}</button>
          {:else}
            <button class="btn btn-primary min-h-11 px-5" type="button" onclick={continueStep} disabled={busy || loadingContext || retesting || !contextReady || blockedReason !== null} aria-describedby={blockedReason ? 'setup-blocked' : undefined}>
              {loadingContext ? i18n.m.setup.loading : busy ? i18n.m.setup.saving : viewStep === 3 && arrCount + serverCount === 0 ? w.skip : i18n.m.common.continue}
            </button>
          {/if}
        </div>
      </div>
    </main>
  </div>
</div>

{#if pickingFolder}
  <FolderPicker initialPath={form.path} onSelect={(path) => { form.path = path; pickingFolder = false }} onClose={() => (pickingFolder = false)} />
{/if}

{#snippet checkRow(key: string, title: string, text: string, state: CheckState)}
  <div class="flex items-start gap-3 py-4" data-setup-check={key} data-setup-check-state={state}>
    <span class="mt-0.5 flex h-6 w-6 flex-none items-center justify-center rounded-full {badge[state].tone}"><Icon name={badge[state].icon} class="h-3.5 w-3.5" /></span>
    <div class="min-w-0 flex-1">
      <div class="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
        <div class="font-semibold text-ink">{title}</div>
        <span class="badge {badge[state].tone}">{badge[state].label}</span>
      </div>
      <p class="mt-1 break-words text-sm leading-6 text-ink-2">{text}</p>
    </div>
  </div>
{/snippet}
