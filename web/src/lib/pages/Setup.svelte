<script lang="ts">
  import { onMount, tick } from 'svelte'
  import { api, type HardwareCapability, type Library, type LibraryAccess, type Settings, type SetupPath, type SetupReadiness, type SetupStorageRelationship, type ToolCheck } from '../api'
  import { i18n, plural, t } from '../i18n/i18n.svelte'
  import { firstUnavailableLibrary, libraryPathsReady as allLibraryPathsReady } from '../setup-library-readiness'
  import { formatSize } from '../format'
  import { setup } from '../stores/setup.svelte'
  import BrandMark from '../components/BrandMark.svelte'
  import Icon from '../components/Icon.svelte'
  import Libraries from './Libraries.svelte'

  let viewStep = $state(setup.state?.currentStep ?? 1)
  let busy = $state(false)
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
  let settings = $state<Settings | null>(null)
  let libraryAccess = $state<Record<number, LibraryAccess | null>>({})
  let configuringLibraryId = $state<number | null>(null)
  let retesting = $state(false)
  let retestMessage = $state<string | null>(null)

  const requiredToolsReady = $derived(tools.length > 0 && tools.every((tool) => !tool.required || tool.available))
  const requiredPathsReady = $derived(paths.length > 0 && paths.every((path) => path.issue === 'none'))
  const libraryPathsReady = $derived(allLibraryPathsReady(libraries, libraryAccess))
  const stepNames = $derived([
    i18n.m.setup.step_welcome,
    i18n.m.setup.step_readiness,
    i18n.m.setup.step_library,
    i18n.m.setup.step_safety,
    i18n.m.setup.step_review,
  ])

  onMount(() => {
    void loadContext()
  })

  async function loadContext(announce = false) {
    if (announce) {
      retesting = true
      retestMessage = null
    }
    error = null
    try {
      const [readiness, hardwareResult, libraryResults, settingsResult] = await Promise.all([
        api.setupReadiness(),
        api.hardware().catch(() => null),
        api.libraries(),
        api.settings(),
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
      settings = settingsResult
      await setLibraries(libraryResults)
      if (announce) retestMessage = i18n.m.setup.retest_complete
    } catch (err) {
      await showError(err instanceof Error ? err.message : i18n.m.setup.error_load)
    } finally {
      retesting = false
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
  }

  async function librarySaved() {
    configuringLibraryId = null
    try {
      await setLibraries()
    } catch (err) {
      await showError(err instanceof Error ? err.message : i18n.m.setup.error_load)
    }
  }

  async function continueStep() {
    if (!setup.state) return
    error = null

    if (viewStep === 3) {
      try {
        await setLibraries()
      } catch (err) {
        await showError(err instanceof Error ? err.message : i18n.m.setup.error_load)
        return
      }
    }

    if (viewStep === 2 && (!databaseAvailable || !requiredToolsReady || !requiredPathsReady)) {
      await showError(i18n.m.setup.required_tools_error)
      return
    }
    if (viewStep === 3 && libraries.length === 0) {
      await showError(i18n.m.setup.library_required_error)
      return
    }
    if (viewStep === 3 && !libraryPathsReady) {
      const unavailable = firstUnavailableLibrary(libraries, libraryAccess)
      await showError(unavailable && libraryAccess[unavailable.id]
        ? libraryAccess[unavailable.id]!.message
        : i18n.m.setup.access_checking)
      return
    }
    if (viewStep === 4 && !settings) {
      await showError(i18n.m.setup.settings_required_error)
      return
    }

    busy = true
    try {
      if (viewStep === 4 && settings) {
        settings = await api.saveSettings(settings)
      }

      if (viewStep === 5) {
        await setup.complete()
        return
      }

      if (viewStep <= setup.state.completedStep) {
        viewStep += 1
      } else {
        const state = await setup.advance(viewStep)
        viewStep = state?.currentStep ?? viewStep + 1
      }
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
    if (path.issue === 'lowSpace') return i18n.m.setup.issue_low_space
    return i18n.m.setup.path_ready
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

  function relationshipMessage(relationship: SetupStorageRelationship): string {
    if (relationship.workAtomic === null || relationship.quarantineAtomic === null) return i18n.m.setup.atomic_unknown
    if (relationship.workAtomic && relationship.quarantineAtomic) return i18n.m.setup.atomic_ready
    if (!relationship.workAtomic && !relationship.quarantineAtomic) return i18n.m.setup.atomic_both_separate
    return relationship.workAtomic ? i18n.m.setup.atomic_quarantine_separate : i18n.m.setup.atomic_work_separate
  }

  function libraryRelationshipMessage(access: LibraryAccess): string {
    return relationshipMessage({
      libraryId: 0,
      libraryName: '',
      workAtomic: access.atomicWithWork,
      quarantineAtomic: access.atomicWithQuarantine,
    })
  }
</script>

<div class="min-h-dvh bg-slate-100 px-4 py-5 text-slate-800 sm:px-6 sm:py-8 dark:bg-slate-950 dark:text-slate-200">
  <div class="mx-auto mb-5 flex max-w-5xl items-center gap-3">
    <BrandMark sizes="36px" class="h-9 w-9" />
    <div>
      <div class="font-bold tracking-tight text-slate-900 dark:text-slate-100">Optimisarr</div>
      <div class="text-xs text-slate-500 dark:text-slate-400">{i18n.m.app.tagline}</div>
    </div>
  </div>

  <div class="mx-auto grid min-h-[min(43rem,calc(100dvh-8rem))] {configuringLibraryId !== null ? 'max-w-7xl' : 'max-w-5xl'} overflow-hidden rounded-2xl border border-slate-200 bg-white md:grid-cols-[15rem_minmax(0,1fr)] dark:border-slate-800 dark:bg-slate-900">
    <aside class="border-b border-slate-200 bg-slate-50 px-4 py-5 md:border-b-0 md:border-r md:px-5 md:py-7 dark:border-slate-800 dark:bg-slate-900/60">
      <p class="mb-4 text-xs font-semibold uppercase tracking-[0.18em] text-cyan-700 dark:text-cyan-400">
        {t(i18n.m.setup.step_of, { current: viewStep, total: setup.state?.stepCount ?? 5 })}
      </p>
      <ol class="grid grid-cols-5 gap-1 md:block" aria-label={i18n.m.setup.progress_label}>
        {#each stepNames as name, index}
          {@const number = index + 1}
          <li class="relative md:pb-6">
            {#if number < stepNames.length}
              <span class="absolute left-[0.9rem] top-8 hidden h-[calc(100%-1.25rem)] w-px bg-slate-200 md:block dark:bg-slate-700"></span>
            {/if}
            <div class="relative flex flex-col items-center text-center md:flex-row md:items-start md:gap-3 md:text-left" aria-current={number === viewStep ? 'step' : undefined} aria-label={`${name}: ${stepStatus(number)}`}>
              <span class="flex h-7 w-7 flex-none items-center justify-center rounded-full text-xs font-bold {number <= (setup.state?.completedStep ?? 0) ? 'bg-emerald-600 text-white' : number === viewStep ? 'bg-cyan-600 text-white' : 'bg-slate-200 text-slate-500 dark:bg-slate-700 dark:text-slate-300'}">
                {number <= (setup.state?.completedStep ?? 0) ? '✓' : number}
              </span>
              <span class="hidden min-w-0 pt-0.5 md:block">
                <span class="block text-sm font-semibold text-slate-700 dark:text-slate-200">{name}</span>
                <span class="block text-[11px] text-slate-400">{stepStatus(number)}</span>
              </span>
            </div>
          </li>
        {/each}
      </ol>
    </aside>

    <main class="flex min-w-0 flex-col px-5 py-6 sm:px-8 sm:py-8 md:px-10" id="setup-content">
      {#if configuringLibraryId !== null}
        <Libraries
          embeddedEditorId={configuringLibraryId}
          onEmbeddedClose={() => (configuringLibraryId = null)}
          onEmbeddedSaved={() => void librarySaved()}
        />
      {:else}
      {#if error}
        <div bind:this={errorSummary} class="mb-5 rounded-lg border border-red-300 bg-red-50 px-4 py-3 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/40 dark:text-red-200" role="alert" tabindex="-1">
          <div class="font-semibold">{i18n.m.setup.error_heading}</div>
          <div>{error}</div>
        </div>
      {/if}

      <div class="flex-1">
        {#if viewStep === 1}
          <p class="mb-2 text-xs font-semibold uppercase tracking-[0.16em] text-cyan-700 dark:text-cyan-400">{i18n.m.setup.welcome_eyebrow}</p>
          <h1 class="max-w-2xl text-3xl font-bold tracking-tight text-slate-900 sm:text-4xl dark:text-white">{i18n.m.setup.welcome_heading}</h1>
          <p class="mt-4 max-w-2xl text-base leading-7 text-slate-600 dark:text-slate-300">{i18n.m.setup.welcome_body}</p>

          <dl class="mt-8 max-w-2xl divide-y divide-slate-200 border-y border-slate-200 dark:divide-slate-800 dark:border-slate-800">
            <div class="grid gap-1 py-4 sm:grid-cols-[11rem_1fr] sm:gap-5">
              <dt class="font-semibold text-slate-800 dark:text-slate-100">{i18n.m.setup.safety_title}</dt>
              <dd class="text-sm leading-6 text-slate-600 dark:text-slate-400">{i18n.m.setup.safety_body}</dd>
            </div>
            <div class="grid gap-1 py-4 sm:grid-cols-[11rem_1fr] sm:gap-5">
              <dt class="font-semibold text-slate-800 dark:text-slate-100">{i18n.m.setup.network_title}</dt>
              <dd class="text-sm leading-6 text-slate-600 dark:text-slate-400">{i18n.m.setup.network_body}</dd>
            </div>
          </dl>
        {:else if viewStep === 2}
          <h1 class="text-2xl font-bold text-slate-900 dark:text-white">{i18n.m.setup.readiness_heading}</h1>
          <p class="mt-2 max-w-2xl text-sm leading-6 text-slate-600 dark:text-slate-400">{i18n.m.setup.readiness_body}</p>

          <section class="mt-7 max-w-3xl" aria-labelledby="storage-heading">
            <div class="flex flex-col gap-4 lg:flex-row lg:items-end lg:justify-between">
              <div>
                <h2 id="storage-heading" class="font-semibold text-slate-900 dark:text-slate-100">{i18n.m.setup.storage_heading}</h2>
                <p class="mt-1 max-w-2xl text-xs leading-5 text-slate-500 dark:text-slate-400">{i18n.m.setup.storage_body}</p>
              </div>
              <div class="flex-none">
                <div class="mb-1.5 text-xs font-medium text-slate-500 dark:text-slate-400">{i18n.m.setup.deployment_label}</div>
                <div class="grid grid-cols-2 gap-1 sm:flex sm:flex-wrap" role="group" aria-label={i18n.m.setup.deployment_label}>
                  {#each ['local', 'compose', 'unraid', 'truenas'] as platform}
                    <button
                      class="min-h-11 rounded-md border px-2.5 text-xs font-medium transition-colors {selectedPlatform === platform ? 'border-cyan-500 bg-cyan-50 text-cyan-800 dark:bg-cyan-950/50 dark:text-cyan-200' : 'border-slate-200 text-slate-600 hover:border-slate-300 hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800'}"
                      type="button"
                      aria-pressed={selectedPlatform === platform}
                      onclick={() => (selectedPlatform = platform as SetupReadiness['platform'])}
                    >
                      {platformLabel(platform as SetupReadiness['platform'])}
                      {#if detectedPlatform === platform}<span class="block text-[10px] font-normal opacity-70">{i18n.m.setup.platform_detected}</span>{/if}
                    </button>
                  {/each}
                </div>
              </div>
            </div>

            <div class="mt-4 divide-y divide-slate-200 border-y border-slate-200 dark:divide-slate-800 dark:border-slate-800">
            {#each paths as path}
              <article class="py-4">
                <div class="flex items-start gap-3">
                  <span class="mt-0.5 flex h-6 w-6 flex-none items-center justify-center rounded-full {path.issue === 'none' ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300' : 'bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300'}">
                    <Icon name={path.issue === 'none' ? 'check' : 'warning'} class="h-3.5 w-3.5" />
                  </span>
                  <div class="min-w-0 flex-1">
                    <div class="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
                      <div class="font-semibold text-slate-800 dark:text-slate-100">{path.role === 'library' ? `${i18n.m.setup.step_library}: ${path.name}` : path.name}</div>
                      <span class="text-xs font-medium {path.issue === 'none' ? 'text-emerald-700 dark:text-emerald-300' : 'text-red-700 dark:text-red-300'}">{path.issue === 'none' ? i18n.m.setup.path_ready : i18n.m.setup.path_unavailable}</span>
                    </div>
                    <div class="mt-0.5 break-all font-mono text-xs text-slate-500 dark:text-slate-400">{path.path}</div>
                    <div class="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-slate-500 dark:text-slate-400">
                      {#if path.availableBytes !== null && path.totalBytes !== null}
                        <span>{t(i18n.m.setup.free_space, { free: formatSize(path.availableBytes), total: formatSize(path.totalBytes) })}</span>
                      {:else if path.availableBytes !== null}
                        <span>{t(i18n.m.setup.free_space_only, { free: formatSize(path.availableBytes) })}</span>
                      {/if}
                      {#if path.fileSystemId && path.mountId}
                        <span class="break-all font-mono">{t(i18n.m.setup.mount_evidence, { type: path.fileSystemType ?? '—', filesystem: path.fileSystemId, mount: path.mountId })}</span>
                      {:else}
                        <span>{i18n.m.setup.mount_unknown}</span>
                      {/if}
                    </div>
                    {#if path.issue !== 'none'}
                      <div class="mt-3 border-l-2 border-red-300 pl-3 dark:border-red-800">
                        <div class="text-sm font-medium text-red-800 dark:text-red-200">{issueMessage(path)}</div>
                        {#if path.issue === 'lowSpace' && path.requiredFreeBytes !== null}
                          <p class="mt-1 text-xs leading-5 text-slate-600 dark:text-slate-400">{t(i18n.m.setup.space_requirement, { required: formatSize(path.requiredFreeBytes) })}</p>
                        {/if}
                        <div class="mt-2 text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{i18n.m.setup.recovery_title}</div>
                        <p class="mt-1 text-xs leading-5 text-slate-600 dark:text-slate-300">{recoverySteps()}</p>
                      </div>
                    {/if}
                  </div>
                </div>
              </article>
            {/each}
            </div>
          </section>

          {#if storageRelationships.length > 0}
            <section class="mt-7 max-w-3xl" aria-labelledby="atomic-heading">
              <h2 id="atomic-heading" class="font-semibold text-slate-900 dark:text-slate-100">{i18n.m.setup.atomic_heading}</h2>
              <p class="mt-1 text-xs leading-5 text-slate-500 dark:text-slate-400">{i18n.m.setup.atomic_body}</p>
              <div class="mt-3 divide-y divide-slate-200 border-y border-slate-200 dark:divide-slate-800 dark:border-slate-800">
                {#each storageRelationships as relationship (relationship.libraryId)}
                  {@const atomic = relationship.workAtomic === true && relationship.quarantineAtomic === true}
                  <div class="flex flex-col gap-2 py-3 sm:flex-row sm:items-center sm:justify-between">
                    <span class="font-medium text-slate-800 dark:text-slate-100">{relationship.libraryName}</span>
                    <span class="flex items-center gap-2 text-xs font-medium {atomic ? 'text-emerald-700 dark:text-emerald-300' : 'text-amber-700 dark:text-amber-300'}">
                      <Icon name={atomic ? 'check' : 'warning'} class="h-4 w-4" />
                      {relationshipMessage(relationship)}
                    </span>
                  </div>
                  {#if !atomic}
                    <div class="pb-4 text-xs leading-5 text-slate-600 dark:text-slate-300">
                      <p>{i18n.m.setup.atomic_fix}</p>
                      <p class="mt-1 font-medium">{settings?.replacementAllowCrossFilesystem ? i18n.m.setup.fallback_enabled : i18n.m.setup.fallback_disabled}</p>
                    </div>
                  {/if}
                {/each}
              </div>
            </section>
          {/if}

          <section class="mt-7 max-w-3xl" aria-labelledby="toolchain-heading">
            <h2 id="toolchain-heading" class="font-semibold text-slate-900 dark:text-slate-100">{i18n.m.setup.toolchain_heading}</h2>
            <p class="mt-1 text-xs leading-5 text-slate-500 dark:text-slate-400">{i18n.m.setup.toolchain_body}</p>
            <div class="mt-3 divide-y divide-slate-200 border-y border-slate-200 dark:divide-slate-800 dark:border-slate-800">
            {#each tools as tool}
              <div class="flex items-start gap-3 py-3">
                <span class="mt-0.5 flex h-6 w-6 flex-none items-center justify-center rounded-full {tool.available ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300' : tool.required ? 'bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300' : 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300'}">
                  <Icon name={tool.available ? 'check' : 'warning'} class="h-3.5 w-3.5" />
                </span>
                <div class="min-w-0 flex-1">
                  <div class="flex flex-wrap items-center gap-2">
                    <span class="font-semibold text-slate-800 dark:text-slate-100">{tool.name}</span>
                    <span class="text-xs text-slate-400">{tool.required ? i18n.m.setup.required : i18n.m.setup.optional}</span>
                  </div>
                  <div class="truncate font-mono text-xs text-slate-500 dark:text-slate-400">{tool.version ?? tool.error ?? tool.command}</div>
                </div>
                <span class="text-xs font-medium {tool.available ? 'text-emerald-700 dark:text-emerald-300' : 'text-slate-500 dark:text-slate-400'}">{tool.available ? i18n.m.setup.available : i18n.m.setup.unavailable}</span>
              </div>
            {/each}
            </div>
          </section>

          {#if hardware}
            <p class="mt-4 max-w-3xl text-xs text-slate-500 dark:text-slate-400">
              {t(i18n.m.setup.encoder_summary, { count: hardware.encoders.filter((encoder) => encoder.available).length })}
            </p>
          {/if}
          <div class="mt-5 flex flex-wrap items-center gap-3" aria-live="polite">
            <button class="btn min-h-11" onclick={() => void loadContext(true)} disabled={busy || retesting}>
              <Icon name="retry" class="h-4 w-4 motion-reduce:animate-none {retesting ? 'animate-spin' : ''}" />
              {retesting ? i18n.m.setup.retesting : i18n.m.setup.retest}
            </button>
            {#if retestMessage}<span class="text-sm text-emerald-700 dark:text-emerald-300">{retestMessage}</span>{/if}
          </div>
        {:else if viewStep === 3}
          <h1 class="text-2xl font-bold text-slate-900 dark:text-white">{i18n.m.setup.library_heading}</h1>
          <p class="mt-2 max-w-2xl text-sm leading-6 text-slate-600 dark:text-slate-400">{i18n.m.setup.library_body}</p>

          {#if libraries.length > 0}
            <div class="mt-7 max-w-2xl divide-y divide-slate-200 border-y border-slate-200 dark:divide-slate-800 dark:border-slate-800">
              {#each libraries as library (library.id)}
                {@const access = libraryAccess[library.id]}
                <article class="flex flex-col gap-3 py-4 sm:flex-row sm:items-center sm:justify-between" aria-label={library.name}>
                  <div class="min-w-0">
                    <div class="flex flex-wrap items-center gap-2">
                      <span class="font-semibold text-slate-900 dark:text-slate-100">{library.name}</span>
                      <span class="badge bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300">{library.mediaType}</span>
                    </div>
                    <div class="mt-1 truncate font-mono text-xs text-slate-500 dark:text-slate-400" title={library.path}>{library.path}</div>
                    <div class="mt-2 text-xs font-medium {access?.ok ? 'text-emerald-700 dark:text-emerald-300' : 'text-amber-700 dark:text-amber-300'}">
                      {access?.ok ? i18n.m.setup.access_ready : access?.message ?? i18n.m.setup.access_checking}
                    </div>
                    {#if access}
                      <div class="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-slate-500 dark:text-slate-400">
                        {#if access.availableBytes !== null}<span>{t(i18n.m.setup.free_space_only, { free: formatSize(access.availableBytes) })}</span>{/if}
                        {#if access.fileSystemId && access.mountId}<span class="font-mono">{t(i18n.m.setup.mount_evidence, { type: access.fileSystemType ?? '—', filesystem: access.fileSystemId, mount: access.mountId })}</span>{/if}
                      </div>
                      <div class="mt-2 flex items-center gap-1.5 text-xs font-medium {access.atomicWithWork === true && access.atomicWithQuarantine === true ? 'text-emerald-700 dark:text-emerald-300' : 'text-amber-700 dark:text-amber-300'}">
                        <Icon name={access.atomicWithWork === true && access.atomicWithQuarantine === true ? 'check' : 'warning'} class="h-3.5 w-3.5" />
                        {libraryRelationshipMessage(access)}
                      </div>
                      {#if !access.ok}
                        <p class="mt-2 border-l-2 border-amber-300 pl-2 text-xs leading-5 text-slate-600 dark:border-amber-800 dark:text-slate-300">{recoverySteps()}</p>
                      {/if}
                    {/if}
                  </div>
                  <button class="btn min-h-11 flex-none" onclick={() => (configuringLibraryId = library.id)}>
                    <Icon name="sliders" class="h-4 w-4" />
                    {i18n.m.libraries.configure}
                  </button>
                </article>
              {/each}
            </div>
          {/if}
          <p class="mt-4 max-w-2xl text-xs leading-5 text-slate-500 dark:text-slate-400">{i18n.m.setup.library_safe_defaults}</p>
          <button class="btn {libraries.length === 0 ? 'btn-primary' : ''} mt-4 min-h-11" onclick={() => (configuringLibraryId = 0)}>
            <Icon name="plus" class="h-4 w-4" />
            {libraries.length === 0 ? i18n.m.setup.create_library : i18n.m.setup.add_another_library}
          </button>
        {:else if viewStep === 4}
          <h1 class="text-2xl font-bold text-slate-900 dark:text-white">{i18n.m.setup.safety_heading}</h1>
          <p class="mt-2 max-w-2xl text-sm leading-6 text-slate-600 dark:text-slate-400">{i18n.m.setup.safety_step_body}</p>

          {#if settings}
            <div class="mt-7 max-w-2xl divide-y divide-slate-200 border-y border-slate-200 dark:divide-slate-800 dark:border-slate-800">
              <label class="flex cursor-pointer items-start gap-3 py-4">
                <input class="mt-1 h-4 w-4 accent-cyan-600" type="checkbox" bind:checked={settings.dryRunMode} />
                <span><span class="block font-semibold text-slate-800 dark:text-slate-100">{i18n.m.setup.dry_run_title}</span><span class="mt-1 block text-sm leading-6 text-slate-500 dark:text-slate-400">{i18n.m.setup.dry_run_body}</span></span>
              </label>
              <div class="grid gap-2 py-4 sm:grid-cols-[1fr_9rem] sm:items-center">
                <div><div class="font-semibold text-slate-800 dark:text-slate-100">{i18n.m.setup.concurrent_title}</div><div class="mt-1 text-sm text-slate-500 dark:text-slate-400">{i18n.m.setup.concurrent_body}</div></div>
                <input class="input" type="number" min="1" bind:value={settings.maxConcurrentJobs} aria-label={i18n.m.setup.concurrent_title} />
              </div>
              <div class="py-4">
                <div class="font-semibold text-slate-800 dark:text-slate-100">{i18n.m.setup.automation_title}</div>
                <div class="mt-1 text-sm leading-6 text-slate-500 dark:text-slate-400">{i18n.m.setup.automation_body}</div>
              </div>
            </div>
          {/if}
        {:else}
          <p class="mb-2 text-xs font-semibold uppercase tracking-[0.16em] text-emerald-700 dark:text-emerald-400">{i18n.m.setup.review_eyebrow}</p>
          <h1 class="text-2xl font-bold text-slate-900 dark:text-white">{i18n.m.setup.review_heading}</h1>
          <p class="mt-2 max-w-2xl text-sm leading-6 text-slate-600 dark:text-slate-400">{i18n.m.setup.review_body}</p>

          <dl class="mt-7 max-w-2xl divide-y divide-slate-200 border-y border-slate-200 dark:divide-slate-800 dark:border-slate-800">
            <div class="grid gap-1 py-4 sm:grid-cols-[10rem_1fr]"><dt class="text-sm font-semibold text-slate-500 dark:text-slate-400">{i18n.m.setup.review_system}</dt><dd class="text-sm text-slate-800 dark:text-slate-200">{databaseAvailable && requiredToolsReady && requiredPathsReady ? i18n.m.setup.review_ready : i18n.m.setup.review_attention}</dd></div>
            <div class="grid gap-1 py-4 sm:grid-cols-[10rem_1fr]"><dt class="text-sm font-semibold text-slate-500 dark:text-slate-400">{i18n.m.setup.review_library}</dt><dd class="text-sm text-slate-800 dark:text-slate-200">{libraries.length > 0 ? libraries.map((library) => library.name).join(', ') : '—'}</dd></div>
            <div class="grid gap-1 py-4 sm:grid-cols-[10rem_1fr]"><dt class="text-sm font-semibold text-slate-500 dark:text-slate-400">{i18n.m.setup.review_replacement}</dt><dd class="text-sm text-slate-800 dark:text-slate-200">{settings?.dryRunMode ? i18n.m.setup.review_dry_run : i18n.m.setup.review_live}</dd></div>
            <div class="grid gap-1 py-4 sm:grid-cols-[10rem_1fr]"><dt class="text-sm font-semibold text-slate-500 dark:text-slate-400">{i18n.m.setup.review_queue}</dt><dd class="text-sm text-slate-800 dark:text-slate-200">{plural(settings?.maxConcurrentJobs ?? 1, i18n.m.setup.review_jobs_one, i18n.m.setup.review_jobs_other)}</dd></div>
          </dl>
        {/if}
      </div>

      <div class="mt-8 flex flex-wrap items-center justify-between gap-3 border-t border-slate-200 pt-5 dark:border-slate-800">
        <button class="btn min-h-11" onclick={() => (viewStep = Math.max(1, viewStep - 1))} disabled={viewStep === 1 || busy}>{i18n.m.setup.back}</button>
        <button class="btn btn-primary min-h-11 px-5" onclick={continueStep} disabled={busy || viewStep === 3 && libraries.length === 0}>
          {busy ? i18n.m.setup.saving : viewStep === 5 ? i18n.m.setup.finish : i18n.m.common.continue}
        </button>
      </div>
      {/if}
    </main>
  </div>
</div>
