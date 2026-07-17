<script lang="ts">
  import { onDestroy, onMount, tick } from 'svelte'
  import {
    api,
    type CalibrationClassification,
    type CalibrationSample,
    type CalibrationSession,
    type CalibrationSource,
    type Library,
  } from '../api'
  import { formatDuration } from '../format'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { activity } from '../stores/activity.svelte'
  import { router } from '../stores/ui.svelte'
  import Banner from '../components/Banner.svelte'
  import Icon from '../components/Icon.svelte'
  import UsageGraph from '../components/UsageGraph.svelte'

  type Rating = CalibrationClassification | null

  const match = router.path.match(/^\/libraries\/(\d+)\/quality-check$/)
  const libraryId = Number(match?.[1] ?? 0)
  const ratingOptions: CalibrationClassification[] = ['Indistinguishable', 'Acceptable', 'VisiblyWorse']

  let library = $state<Library | null>(null)
  let sources = $state<CalibrationSource[]>([])
  let selectedSource = $state<number | null>(null)
  let session = $state<CalibrationSession | null>(null)
  let ratings = $state<Record<string, Rating>>({})
  let activeName = $state('ORIGINAL')
  let activeScene = $state(0)
  let loading = $state(true)
  let busy = $state(false)
  let error = $state<string | null>(null)
  let playbackError = $state(false)
  let playing = $state(false)
  let playbackPosition = $state(0)
  let switching = $state(false)
  let switchSequence = $state(0)
  let switchShouldResume = $state(false)
  let switchPosition = $state(0)
  let players: Record<string, HTMLMediaElement> = {}
  let videoPlayer = $state<HTMLVideoElement | null>(null)
  let pendingName = $state<string | null>(null)
  let browserStreamUrl = $state('')
  let diagnosticsEnabled = $state(false)
  let ignoreActiveStreams = $state(false)
  let viewer = $state<HTMLElement | null>(null)
  let fullscreen = $state(false)
  let hdrDisplaySupported = $state(false)
  let hdrViewingConfirmed = $state(false)
  let imageZoom = $state(1)
  let imagePanX = $state(0)
  let imagePanY = $state(0)
  let imageDrag = $state<{ x: number, y: number, panX: number, panY: number } | null>(null)
  let pollTimer: ReturnType<typeof setTimeout> | null = null
  let closed = false

  const selected = $derived(sources.find((source) => source.mediaFileId === selectedSource) ?? null)
  const variants = $derived(session?.variants ?? [])
  const candidateVariants = $derived(variants.filter((variant) => !variant.isOriginal))
  const activeVariant = $derived(variants.find((variant) => variant.name === activeName) ?? variants[0] ?? null)
  const activeSample = $derived(activeVariant?.samples[activeScene] ?? null)
  const videoName = $derived(pendingName ?? activeName)
  const videoVariant = $derived(variants.find((variant) => variant.name === videoName) ?? variants[0] ?? null)
  const videoSample = $derived(videoVariant?.samples[activeScene] ?? null)
  const activeDiagnostics = $derived(activeVariant?.diagnostics ?? null)
  const classifiedCount = $derived(Object.values(ratings).filter(Boolean).length)
  const canReveal = $derived(!playbackError && candidateVariants.length > 0 && classifiedCount === candidateVariants.length)
  const revealed = $derived(session?.status === 'Revealed' || session?.status === 'Applied')
  const hdrReady = $derived(!selected?.isHdr || hdrDisplaySupported && hdrViewingConfirmed)
  const gpuUnavailable = $derived(
    activity.metrics && !activity.metrics.gpuSupported ? i18n.m.dashboard.gpu_unavailable : null,
  )

  onMount(() => {
    hdrDisplaySupported = window.matchMedia('(video-dynamic-range: high)').matches
      || window.matchMedia('(dynamic-range: high)').matches
    const onFullscreen = () => (fullscreen = document.fullscreenElement === viewer)
    document.addEventListener('fullscreenchange', onFullscreen)
    void load()
    return () => document.removeEventListener('fullscreenchange', onFullscreen)
  })

  onDestroy(() => {
    closed = true
    if (pollTimer) clearTimeout(pollTimer)
    if (session) void api.deleteCalibration(session.id).catch(() => undefined)
  })

  async function load() {
    try {
      const [allLibraries, availableSources] = await Promise.all([
        api.libraries(),
        api.calibrationSources(libraryId),
      ])
      library = allLibraries.find((candidate) => candidate.id === libraryId) ?? null
      sources = availableSources
      selectedSource = sources[0]?.mediaFileId ?? null
      if (!library) error = i18n.m.calibration.library_missing
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.calibration.load_error
    } finally {
      loading = false
    }
  }

  async function start() {
    if (selectedSource === null) return
    busy = true
    error = null
    try {
      session = await api.startCalibration(
        libraryId,
        selectedSource,
        selected?.isHdr === true && hdrReady,
        diagnosticsEnabled,
        ignoreActiveStreams,
      )
      activeName = 'ORIGINAL'
      activeScene = 0
      ratings = {}
      schedulePoll()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.calibration.start_error
    } finally {
      busy = false
    }
  }

  function schedulePoll() {
    if (closed || session?.status !== 'Preparing') return
    if (pollTimer) clearTimeout(pollTimer)
    pollTimer = setTimeout(poll, 1500)
  }

  async function poll() {
    if (closed || !session) return
    try {
      session = await api.calibration(session.id)
      if (session.variants.length > 0 && !session.variants.some((variant) => variant.name === activeName)) {
        activeName = session.variants[0].name
      }
      schedulePoll()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.calibration.load_error
    }
  }

  function sourceLabel(source: CalibrationSource): string {
    const resolution = source.width && source.height ? ` · ${source.width}×${source.height}` : ''
    if (source.mediaKind === 'Image') return `${source.relativePath}${resolution}`
    const range = source.mediaKind === 'Video' ? ` · ${source.isHdr ? 'HDR' : 'SDR'}` : ''
    return `${source.relativePath}${resolution}${range} · ${formatDuration(source.durationSeconds)}`
  }

  function vmafScore(value: number | null): string {
    return value === null ? '–' : value.toFixed(1)
  }

  function sampleFor(name: string): CalibrationSample | null {
    return variants.find((variant) => variant.name === name)?.samples[activeScene] ?? null
  }

  function registerAudioPlayer(name: string, event: Event) {
    const player = event.currentTarget as HTMLMediaElement
    players[name] = player
    preparePlayer(name, player)
    if (name === activeName) browserStreamUrl = player.currentSrc || player.getAttribute('src') || ''
  }

  function registerVideoPlayer(name: string, event: Event) {
    const player = event.currentTarget as HTMLVideoElement
    videoPlayer = player
    preparePlayer(name, player)
    if (!switching && name === activeName) browserStreamUrl = player.currentSrc
  }

  function preparePlayer(name: string, player: HTMLMediaElement) {
    const sample = sampleFor(name)
    if (!sample || !Number.isFinite(player.duration)) return
    player.currentTime = sample.startSeconds + playbackPosition
    player.volume = Math.min(1, Math.pow(10, sample.gainDb / 20))
  }

  function waitForSeek(player: HTMLMediaElement): Promise<boolean> {
    if (!player.seeking) return Promise.resolve(true)
    return new Promise((resolve) => {
      const timeout = window.setTimeout(() => finish(!player.seeking), 3000)
      function finish(ready: boolean) {
        window.clearTimeout(timeout)
        player.removeEventListener('seeked', onSeeked)
        resolve(ready)
      }
      const onSeeked = () => finish(true)
      player.addEventListener('seeked', onSeeked, { once: true })
    })
  }

  function waitForMetadata(player: HTMLMediaElement): Promise<boolean> {
    if (player.readyState >= HTMLMediaElement.HAVE_METADATA) return Promise.resolve(true)
    return new Promise((resolve) => {
      const timeout = window.setTimeout(() => finish(false), 3000)
      function finish(ready: boolean) {
        window.clearTimeout(timeout)
        player.removeEventListener('loadedmetadata', onLoaded)
        player.removeEventListener('error', onError)
        resolve(ready)
      }
      const onLoaded = () => finish(true)
      const onError = () => finish(false)
      player.addEventListener('loadedmetadata', onLoaded, { once: true })
      player.addEventListener('error', onError, { once: true })
    })
  }

  async function waitForPlayer(name: string, sequence: number): Promise<HTMLMediaElement | null> {
    const deadline = performance.now() + 3000
    while (sequence === switchSequence && performance.now() < deadline) {
      if (players[name]) return players[name]
      await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()))
    }
    return players[name] ?? null
  }

  async function waitForVideoPlayer(name: string, sequence: number): Promise<HTMLVideoElement | null> {
    const expectedSource = sampleFor(name)?.url
    const deadline = performance.now() + 3000
    while (sequence === switchSequence && performance.now() < deadline) {
      if (videoPlayer?.getAttribute('src') === expectedSource) return videoPlayer
      await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()))
    }
    return videoPlayer?.getAttribute('src') === expectedSource ? videoPlayer : null
  }

  async function chooseVariant(name: string) {
    if (name === activeName && !switching) return
    if (!switching) {
      const activePlayer = session?.mediaKind === 'Video' ? videoPlayer : players[activeName]
      switchShouldResume = playing || activePlayer != null && !activePlayer.paused
      const currentSample = sampleFor(activeName)
      switchPosition = activePlayer && currentSample
        ? Math.max(0, activePlayer.currentTime - currentSample.startSeconds)
        : playbackPosition
    }
    const sequence = ++switchSequence
    switching = true
    try {
      if (session?.mediaKind === 'Video') await switchVideoVariant(name, sequence)
      else await switchVariant(name, sequence)
    } finally {
      if (sequence === switchSequence) {
        if (pendingName !== null) pendingName = null
        switching = false
        switchShouldResume = false
      }
    }
  }

  async function switchVideoVariant(name: string, sequence: number) {
    const targetSample = sampleFor(name)
    if (!targetSample) return
    videoPlayer?.pause()
    pendingName = name
    browserStreamUrl = ''
    await tick()
    const target = await waitForVideoPlayer(name, sequence)
    if (sequence !== switchSequence) return
    if (!target || !await waitForMetadata(target)) {
      playbackError = true
      return
    }
    target.currentTime = targetSample.startSeconds + switchPosition
    const frameReady = await waitForSeek(target)
    if (sequence !== switchSequence) return
    if (!frameReady) {
      playbackError = true
      return
    }
    activeName = name
    pendingName = null
    playbackPosition = switchPosition
    browserStreamUrl = target.currentSrc
    if (switchShouldResume) {
      await target.play().catch(() => {
        playing = false
        playbackError = true
      })
    }
  }

  async function switchVariant(name: string, sequence: number) {
    if (session?.mediaKind === 'Image') {
      if (sequence !== switchSequence) return
      activeName = name
      browserStreamUrl = sampleFor(name)?.url ?? ''
      return
    }
    const targetSample = sampleFor(name)
    if (!targetSample) return
    const target = await waitForPlayer(name, sequence)
    if (sequence !== switchSequence) return
    if (!target) {
      playbackError = true
      return
    }
    const current = players[activeName]
    const currentSample = sampleFor(activeName)
    if (current && currentSample) {
      playbackPosition = Math.max(0, current.currentTime - currentSample.startSeconds)
      current.pause()
    }
    target.currentTime = targetSample.startSeconds + playbackPosition
    target.volume = Math.min(1, Math.pow(10, targetSample.gainDb / 20))
    const frameReady = await waitForSeek(target)
    if (sequence !== switchSequence) return
    if (!frameReady) {
      playbackError = true
      return
    }
    activeName = name
    browserStreamUrl = target.currentSrc || target.getAttribute('src') || targetSample.url
    if (switchShouldResume) {
      await target.play().catch(() => {
        playing = false
        playbackError = true
      })
    }
  }

  async function chooseScene(index: number) {
    const switchingVideo = session?.mediaKind === 'Video'
    const sequence = switchingVideo ? ++switchSequence : switchSequence
    if (switchingVideo) switching = true
    const shouldResume = playing || videoPlayer != null && !videoPlayer.paused
    videoPlayer?.pause()
    activeScene = index
    playbackPosition = 0
    playing = false
    players = {}
    videoPlayer = null
    browserStreamUrl = ''
    imageZoom = 1
    imagePanX = 0
    imagePanY = 0
    await tick()
    if (!switchingVideo || sequence !== switchSequence) return
    const player = await waitForVideoPlayer(activeName, sequence)
    const sample = sampleFor(activeName)
    if (!player || !sample || !await waitForMetadata(player)) {
      playbackError = true
      switching = false
      return
    }
    player.currentTime = sample.startSeconds
    const frameReady = await waitForSeek(player)
    if (!frameReady) playbackError = true
    browserStreamUrl = player.currentSrc
    switching = false
    if (frameReady && shouldResume) await player.play().catch(() => (playbackError = true))
  }

  function updatePosition(event: Event) {
    const player = event.currentTarget as HTMLMediaElement
    const sample = sampleFor(activeName)
    const activePlayer = session?.mediaKind === 'Video' ? videoPlayer : players[activeName]
    if (!sample || pendingName !== null || player !== activePlayer) return
    playbackPosition = Math.max(0, Math.min(sample.durationSeconds, player.currentTime - sample.startSeconds))
    if (player.currentTime >= sample.startSeconds + sample.durationSeconds) {
      player.pause()
      player.currentTime = sample.startSeconds
      playbackPosition = 0
      playing = false
    }
  }

  async function togglePlayback() {
    const player = session?.mediaKind === 'Video' ? videoPlayer : players[activeName]
    const sample = sampleFor(activeName)
    if (!player || !sample) return
    if (!player.paused) {
      player.pause()
      playing = false
      return
    }
    if (playbackPosition >= sample.durationSeconds - 0.05) {
      playbackPosition = 0
      player.currentTime = sample.startSeconds
    }
    await player.play().then(() => (playing = true)).catch(() => {
      playing = false
      playbackError = true
    })
  }

  async function seek(position: number) {
    playbackPosition = position
    if (session?.mediaKind === 'Video') {
      const sample = sampleFor(activeName)
      if (!videoPlayer || !sample) return
      videoPlayer.currentTime = sample.startSeconds + position
      if (!await waitForSeek(videoPlayer)) playbackError = true
      return
    }
    const waits: Promise<boolean>[] = []
    for (const variant of variants) {
      const player = players[variant.name]
      const sample = sampleFor(variant.name)
      if (!player || !sample) continue
      player.currentTime = sample.startSeconds + position
      waits.push(waitForSeek(player))
    }
    const ready = await Promise.all(waits)
    if (ready.some((value) => !value)) playbackError = true
  }

  function classify(name: string, rating: CalibrationClassification) {
    if (revealed || playbackError || variants.find((variant) => variant.name === name)?.isOriginal) return
    ratings = { ...ratings, [name]: rating }
  }

  function variantLabel(name: string): string {
    return variants.find((variant) => variant.name === name)?.isOriginal
      ? i18n.m.calibration.original
      : name
  }

  async function revealResults() {
    if (!session || !canReveal) return
    busy = true
    error = null
    try {
      session = await api.classifyCalibration(session.id, ratings as Record<string, CalibrationClassification>)
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.calibration.reveal_error
    } finally {
      busy = false
    }
  }

  async function applyResult() {
    if (session?.result?.recommendedQuality == null) return
    busy = true
    error = null
    try {
      session = await api.applyCalibration(session.id)
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.calibration.apply_error
    } finally {
      busy = false
    }
  }

  async function toggleFullscreen() {
    if (!viewer) return
    if (document.fullscreenElement) await document.exitFullscreen()
    else await viewer.requestFullscreen()
  }

  function zoom(delta: number) {
    imageZoom = Math.max(1, Math.min(4, imageZoom + delta))
    if (imageZoom === 1) {
      imagePanX = 0
      imagePanY = 0
    }
  }

  function startImageDrag(event: PointerEvent) {
    if (imageZoom <= 1) return
    imageDrag = { x: event.clientX, y: event.clientY, panX: imagePanX, panY: imagePanY }
    ;(event.currentTarget as HTMLElement).setPointerCapture(event.pointerId)
  }

  function moveImage(event: PointerEvent) {
    if (!imageDrag) return
    imagePanX = imageDrag.panX + event.clientX - imageDrag.x
    imagePanY = imageDrag.panY + event.clientY - imageDrag.y
  }

  function ratingLabel(rating: CalibrationClassification): string {
    if (rating === 'Indistinguishable') return i18n.m.calibration.indistinguishable
    if (rating === 'Acceptable') return i18n.m.calibration.acceptable
    return i18n.m.calibration.visibly_worse
  }

  function ratingHint(rating: CalibrationClassification): string {
    if (rating === 'Indistinguishable') return i18n.m.calibration.indistinguishable_hint
    if (rating === 'Acceptable') return i18n.m.calibration.acceptable_hint
    return i18n.m.calibration.visibly_worse_hint
  }

  function revealedLabel(name: string): string {
    const result = session?.result?.variants.find((variant) => variant.name === name)
    if (!result) return ''
    if (result.isOriginal) return i18n.m.calibration.original
    if (session?.mediaKind === 'Audio') return `${result.quality} kbps`
    if (session?.mediaKind === 'Image') return t(i18n.m.calibration.image_quality_value, { quality: result.quality ?? '—' })
    const mode = result.qualityMode ?? 'CRF'
    return [result.profile, result.codec, result.container, `${mode} ${result.effectiveQuality ?? result.quality ?? '—'}`]
      .filter(Boolean)
      .join(' · ')
  }

  function diagnosticSummary(): string {
    if (!activeDiagnostics) return ''
    const identity = activeVariant?.isOriginal ? 'ORIGINAL' : activeDiagnostics.profile ?? activeName
    const quality = activeDiagnostics.qualityMode && activeDiagnostics.effectiveQuality != null
      ? `${activeDiagnostics.qualityMode} ${activeDiagnostics.effectiveQuality}`
      : activeDiagnostics.requestedQuality != null
        ? `quality ${activeDiagnostics.requestedQuality}`
        : null
    return [identity, activeDiagnostics.codec, activeDiagnostics.container, quality].filter(Boolean).join(' · ')
  }
</script>

<svelte:head>
  <title>{i18n.m.calibration.lab_title} · Optimisarr</title>
</svelte:head>

<div class="quality-lab min-h-full">
  <header class="mb-5 flex flex-col gap-4 border-b border-slate-200 pb-5 sm:flex-row sm:items-end sm:justify-between dark:border-slate-800">
    <div>
      <button class="btn btn-ghost -ml-3 mb-2 min-h-11" onclick={() => router.go(`/libraries/${libraryId}/configure`)}>
        <Icon name="arrow-left" class="h-4 w-4" />
        {i18n.m.calibration.back_to_library}
      </button>
      <p class="text-xs font-semibold uppercase tracking-[0.18em] text-cyan-700 dark:text-cyan-300">{i18n.m.calibration.eyebrow}</p>
      <h1 class="mt-1 text-2xl font-bold tracking-tight text-slate-950 sm:text-3xl dark:text-white">{i18n.m.calibration.lab_title}</h1>
      <p class="mt-2 max-w-3xl text-sm leading-relaxed text-slate-600 dark:text-slate-400">
        {library?.name ? `${library.name} · ` : ''}{i18n.m.calibration.lab_intro}
      </p>
    </div>
    {#if session?.status === 'Comparing'}
      <div class="rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-semibold text-slate-700 shadow-sm dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200" aria-live="polite">
        {t(i18n.m.calibration.sorted_progress, { current: classifiedCount, total: candidateVariants.length })}
      </div>
    {/if}
  </header>

  {#if error}<Banner kind="error" class="mb-5">{error}</Banner>{/if}
  {#if playbackError}<Banner kind="error" class="mb-5">{i18n.m.calibration.playback_error}</Banner>{/if}

  {#if loading}
    <div class="card flex min-h-72 items-center justify-center text-sm text-slate-500">{i18n.m.common.loading}</div>
  {:else if !session}
    <div class="grid gap-5 lg:grid-cols-[minmax(0,1fr)_22rem]">
      <section class="card p-5 sm:p-6">
        <h2 class="text-lg font-semibold text-slate-900 dark:text-white">
          {selected?.mediaKind === 'Audio' ? i18n.m.calibration.choose_audio : selected?.mediaKind === 'Image' ? i18n.m.calibration.choose_image : i18n.m.calibration.choose_source}
        </h2>
        <p class="mt-2 text-sm leading-relaxed text-slate-600 dark:text-slate-400">{i18n.m.calibration.intro}</p>
        {#if sources.length === 0}
          <div class="mt-6 rounded-xl border border-dashed border-slate-300 p-8 text-center dark:border-slate-700">
            <p class="font-semibold text-slate-800 dark:text-slate-200">{i18n.m.calibration.no_sources}</p>
            <p class="mt-2 text-sm text-slate-500 dark:text-slate-400">{i18n.m.calibration.no_sources_hint}</p>
          </div>
        {:else}
          <label class="label mt-6" for="quality-source">{selected?.mediaKind === 'Audio' ? i18n.m.calibration.source_audio : selected?.mediaKind === 'Image' ? i18n.m.calibration.source_image : i18n.m.calibration.source_label}</label>
          <select id="quality-source" class="input" bind:value={selectedSource}>
            {#each sources as source}<option value={source.mediaFileId}>{sourceLabel(source)}</option>{/each}
          </select>
          {#if selected?.isHdr}
            <div class="mt-5 rounded-xl border border-amber-300 bg-amber-50 p-4 text-sm text-amber-900 dark:border-amber-800 dark:bg-amber-950/30 dark:text-amber-200">
              <p class="font-semibold">{i18n.m.calibration.hdr_title}</p>
              {#if hdrDisplaySupported}
                <p class="mt-1 leading-relaxed">{i18n.m.calibration.hdr_body}</p>
                <label class="mt-3 flex min-h-11 items-start gap-3"><input class="mt-1" type="checkbox" bind:checked={hdrViewingConfirmed} /><span>{i18n.m.calibration.hdr_confirm}</span></label>
              {:else}<p class="mt-1 leading-relaxed">{i18n.m.calibration.hdr_unsupported}</p>{/if}
            </div>
          {/if}
          <label class="mt-5 flex min-h-11 cursor-pointer items-start gap-3 rounded-xl border border-amber-300 bg-amber-50 p-3 text-sm text-amber-950 dark:border-amber-800 dark:bg-amber-950/30 dark:text-amber-100">
            <input class="mt-1" type="checkbox" bind:checked={diagnosticsEnabled} />
            <span><strong class="block">Temporary stream verification</strong><span class="mt-0.5 block text-xs opacity-80">Uses one native video player and exposes its exact media resource, so the comparison will not be blind.</span></span>
          </label>
          <label class="mt-3 flex min-h-11 cursor-pointer items-start gap-3 rounded-xl border border-slate-300 bg-slate-50 p-3 text-sm text-slate-900 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100">
            <input class="mt-1" type="checkbox" bind:checked={ignoreActiveStreams} />
            <span><strong class="block">Ignore active media streams for this check</strong><span class="mt-0.5 block text-xs text-slate-600 dark:text-slate-400">Allows these disposable samples to run while Plex, Jellyfin, or Emby is playing. Normal optimisation work stays paused.</span></span>
          </label>
          <button class="btn btn-primary mt-6 min-h-11" disabled={busy || !hdrReady} onclick={start}>
            {busy ? i18n.m.calibration.starting : i18n.m.calibration.start}
          </button>
        {/if}
      </section>
      <aside class="card p-5">
        <p class="font-semibold text-slate-900 dark:text-white">{i18n.m.calibration.safe_title}</p>
        <p class="mt-2 text-sm leading-relaxed text-slate-600 dark:text-slate-400">{i18n.m.calibration.safe_body}</p>
      </aside>
    </div>
  {:else if session.status === 'Preparing'}
    <section class="card mx-auto max-w-2xl p-6 sm:p-8">
      <div class="flex items-center justify-between gap-3"><h2 class="text-lg font-semibold text-slate-900 dark:text-white">{session.preparationState === 'Waiting' ? i18n.m.calibration.waiting : i18n.m.calibration.preparing}</h2><span class="font-mono text-sm font-semibold text-cyan-700 dark:text-cyan-300">{Math.round(session.preparationProgress * 100)}%</span></div>
      <div class="mt-5 h-2 overflow-hidden rounded-full bg-slate-200 dark:bg-slate-800"><div class="h-full rounded-full bg-cyan-500 transition-[width] duration-300" style:width={`${Math.max(1, session.preparationProgress * 100)}%`}></div></div>
      <p class="mt-4 text-sm leading-relaxed text-slate-600 dark:text-slate-400">{session.preparationState === 'Waiting' ? i18n.m.calibration.waiting_hint : session.mediaKind === 'Audio' ? i18n.m.calibration.preparing_audio_hint : session.mediaKind === 'Image' ? i18n.m.calibration.preparing_image_hint : i18n.m.calibration.preparing_hint}</p>
      <div class="mt-6 border-t border-slate-200 pt-5 dark:border-slate-700">
        <div class="mb-3 flex items-center gap-2">
          <Icon name="gpu" class="h-4 w-4 text-slate-400" />
          <div class="label">{i18n.m.dashboard.live_usage}</div>
        </div>
        <div class="grid gap-3 sm:grid-cols-2">
          <UsageGraph label="CPU" data={activity.cpuHistory} current={activity.metrics?.cpuPercent ?? null} color="rgb(56,189,248)" />
          <UsageGraph
            label="GPU"
            data={activity.gpuHistory}
            current={activity.metrics?.gpuPercent ?? null}
            color="rgb(34,197,94)"
            unavailable={gpuUnavailable}
            detail={activity.metrics?.gpuEngine}
          />
        </div>
      </div>
    </section>
  {:else if session.status === 'Failed'}
    <Banner kind="error">{session.error ?? i18n.m.calibration.failed}</Banner>
  {:else}
    <div class="grid gap-5 xl:grid-cols-[minmax(0,1fr)_22rem]">
      <div class="min-w-0 space-y-5">
        <section class="overflow-hidden rounded-2xl border border-slate-800 bg-slate-950 shadow-xl shadow-slate-950/10" bind:this={viewer} aria-busy={switching}>
          <div class="flex items-center justify-between gap-3 border-b border-slate-800 px-4 py-3 text-slate-200">
            <div><span class="text-xs uppercase tracking-[0.18em] text-slate-500">{activeVariant?.isOriginal ? i18n.m.calibration.reference_label : i18n.m.calibration.sample_label}</span><strong class="ml-2 text-lg">{variantLabel(activeName)}</strong></div>
            <div class="flex items-center gap-2">
              {#if switching}<span class="text-xs font-medium text-cyan-300" aria-live="polite">{i18n.m.common.loading_short}</span>{/if}
              {#if activeSample && activeSample.sampleCount > 1}<span class="text-xs text-slate-400">{t(i18n.m.calibration.scene_label, { current: activeScene + 1, total: activeSample.sampleCount })}</span>{/if}
              {#if session.mediaKind === 'Video'}
                <button class="inline-flex min-h-11 min-w-11 items-center justify-center rounded-lg text-slate-300 hover:bg-slate-800 hover:text-white focus-visible:outline-2 focus-visible:outline-cyan-400" aria-label={fullscreen ? i18n.m.calibration.exit_fullscreen : i18n.m.calibration.fullscreen} title={fullscreen ? i18n.m.calibration.exit_fullscreen : i18n.m.calibration.fullscreen} onclick={toggleFullscreen}>
                  <svg class="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d={fullscreen ? 'M9 4v5H4M15 4v5h5M9 20v-5H4M15 20v-5h5' : 'M9 4H4v5M15 4h5v5M9 20H4v-5M15 20h5v-5'} /></svg>
                </button>
              {/if}
            </div>
          </div>
          <div class="relative flex aspect-video min-h-64 items-center justify-center overflow-hidden bg-black" class:cursor-grab={session.mediaKind === 'Image' && imageZoom > 1} class:cursor-grabbing={imageDrag !== null} role="group" aria-label={session.mediaKind === 'Image' ? i18n.m.calibration.image_viewport : i18n.m.calibration.switch_label} onpointerdown={startImageDrag} onpointermove={moveImage} onpointerup={() => (imageDrag = null)} onpointercancel={() => (imageDrag = null)}>
            {#key activeScene}
              {#if session.mediaKind === 'Image'}
                {#each variants as variant}
                  <img src={variant.samples[activeScene]?.url} alt={variant.isOriginal ? i18n.m.calibration.original_reference : t(i18n.m.calibration.image_alt, { slot: variant.name })} draggable="false" class="absolute inset-0 h-full w-full select-none object-contain" class:invisible={variant.name !== activeName} style:transform={`translate(${imagePanX}px, ${imagePanY}px) scale(${imageZoom})`} onerror={() => (playbackError = true)} />
                {/each}
              {:else if session.mediaKind === 'Video' && videoSample}
                {#key videoSample.url}
                  <!-- svelte-ignore a11y_media_has_caption disposable calibration clips have no generated caption track -->
                  <video
                    bind:this={videoPlayer}
                    src={videoSample.url}
                    preload="auto"
                    playsinline
                    controls={diagnosticsEnabled}
                    class="absolute inset-0 h-full w-full object-contain"
                    class:invisible={switching}
                    onloadedmetadata={(event: Event) => registerVideoPlayer(videoName, event)}
                    ontimeupdate={updatePosition}
                    onplay={() => pendingName === null && (playing = true)}
                    onpause={() => pendingName === null && (playing = false)}
                    onerror={() => (playbackError = true)}
                  ></video>
                {/key}
                {#if switching}<div class="absolute inset-0 flex items-center justify-center bg-black text-sm font-medium text-cyan-200" aria-live="polite">Loading exact video resource…</div>{/if}
              {:else}
                {#each variants as variant}
                  <audio src={variant.samples[activeScene]?.url} preload="auto" class="absolute inset-0 h-full w-full object-contain" class:invisible={variant.name !== activeName} onloadedmetadata={(event: Event) => registerAudioPlayer(variant.name, event)} ontimeupdate={updatePosition} onplay={() => variant.name === activeName && (playing = true)} onpause={() => variant.name === activeName && (playing = false)} onerror={() => (playbackError = true)}></audio>
                {/each}
                <div class="pointer-events-none flex flex-col items-center text-slate-300"><svg class="h-20 w-20 text-cyan-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="1.2"><path stroke-linecap="round" stroke-linejoin="round" d="M9 18V5l10-2v13M9 18a3 3 0 11-6 0 3 3 0 016 0zm10-2a3 3 0 11-6 0 3 3 0 016 0z" /></svg><span class="mt-3 text-sm">{i18n.m.calibration.audio_listening}</span></div>
              {/if}
            {/key}
          </div>
          <div class="border-t border-slate-800 px-4 py-3">
            {#if session.mediaKind === 'Image'}
              <div class="flex justify-center gap-2" aria-label={i18n.m.calibration.image_zoom_controls}>
                <button class="btn min-h-11 border-slate-700 bg-slate-900 text-slate-200" aria-label={i18n.m.calibration.zoom_out} onclick={() => zoom(-0.25)}>−</button>
                <button class="btn min-h-11 border-slate-700 bg-slate-900 text-slate-200" onclick={() => { imageZoom = 1; imagePanX = 0; imagePanY = 0 }}>{Math.round(imageZoom * 100)}%</button>
                <button class="btn min-h-11 border-slate-700 bg-slate-900 text-slate-200" aria-label={i18n.m.calibration.zoom_in} onclick={() => zoom(0.25)}>+</button>
              </div>
            {:else if activeSample && !(session.mediaKind === 'Video' && diagnosticsEnabled)}
              <div class="flex items-center gap-3"><button class="inline-flex min-h-11 min-w-11 items-center justify-center rounded-full bg-cyan-500 text-slate-950 hover:bg-cyan-400" aria-label={playing ? i18n.m.calibration.pause_sample : i18n.m.calibration.play_sample} onclick={togglePlayback}><Icon name={playing ? 'pause' : 'play'} class="h-5 w-5" /></button><input class="min-h-11 min-w-0 flex-1 accent-cyan-400" type="range" min="0" max={activeSample.durationSeconds} step="0.05" value={playbackPosition} aria-label={i18n.m.calibration.sample_position} oninput={(event) => seek(Number(event.currentTarget.value))} /><span class="w-20 text-right font-mono text-xs text-slate-400">{playbackPosition.toFixed(1)} / {activeSample.durationSeconds}s</span></div>
            {:else if activeSample}
              <p class="text-center text-xs text-slate-400">Playback is controlled by the browser’s native video controls above.</p>
            {/if}
          </div>
          {#if activeDiagnostics && activeSample}
            <div class="border-t border-amber-800/70 bg-amber-950/40 px-4 py-3 text-amber-100" aria-live="polite">
              <div class="flex flex-wrap items-center justify-between gap-2">
                <strong class="text-xs uppercase tracking-[0.16em] text-amber-300">Stream diagnostics</strong>
                <span class="font-mono text-xs">{diagnosticSummary()}</span>
              </div>
              <dl class="mt-2 grid gap-x-4 gap-y-1 text-[11px] sm:grid-cols-[7rem_minmax(0,1fr)]">
                <dt class="text-amber-300/80">Requested route</dt><dd class="break-all font-mono">{activeSample.url}</dd>
                <dt class="text-amber-300/80">video.currentSrc</dt><dd class="break-all font-mono">{browserStreamUrl || 'Waiting for the video element…'}</dd>
                <dt class="text-amber-300/80">Playback time</dt><dd class="font-mono">{playbackPosition.toFixed(3)}s · scene {activeScene + 1}</dd>
              </dl>
              {#if session.mediaKind === 'Video' && browserStreamUrl}
                <a class="mt-3 inline-flex min-h-11 items-center rounded-lg border border-amber-600/70 px-3 py-2 text-xs font-semibold text-amber-100 hover:bg-amber-900/60 focus-visible:outline-2 focus-visible:outline-amber-300" href={browserStreamUrl} target="_blank" rel="noopener noreferrer" aria-label="Open exact video resource">Open exact resource in browser</a>
              {/if}
            </div>
          {/if}
        </section>

        {#if activeSample && activeSample.sampleCount > 1}
          <nav class="flex gap-2" aria-label={i18n.m.calibration.scenes}>
            {#each activeVariant?.samples ?? [] as sample, index}<button class="btn min-h-11 flex-1" class:border-cyan-500={activeScene === index} class:text-cyan-700={activeScene === index} onclick={() => chooseScene(index)}>{t(i18n.m.calibration.scene_button, { number: sample.sampleNumber })}</button>{/each}
          </nav>
        {/if}

        <section class="card p-4 sm:p-5">
          <div class="flex flex-col justify-between gap-2 sm:flex-row sm:items-end"><div><h2 class="font-semibold text-slate-900 dark:text-white">{i18n.m.calibration.sample_deck}</h2><p class="mt-1 text-sm text-slate-500 dark:text-slate-400">{i18n.m.calibration.sample_deck_hint}</p></div><span class="text-xs text-slate-400">Select a sample, then rate it</span></div>
          <div class="mt-4 grid grid-cols-3 gap-2 sm:grid-cols-6">
            {#each variants as variant}
              {@const resultVariant = session.result?.variants.find((item) => item.name === variant.name)}
              <button class="group relative min-h-20 cursor-pointer touch-manipulation rounded-xl border-2 bg-white px-3 py-3 text-left transition-colors active:bg-slate-100 dark:bg-slate-900 dark:active:bg-slate-800" class:border-cyan-500={activeName === variant.name} class:border-slate-200={activeName !== variant.name} class:dark:border-slate-700={activeName !== variant.name} class:bg-cyan-50={variant.isOriginal} class:dark:bg-slate-800={variant.isOriginal} onclick={() => chooseVariant(variant.name)} aria-label={variant.isOriginal ? i18n.m.calibration.original_reference : variant.name} aria-pressed={activeName === variant.name}>
                <span class="text-xl font-bold text-slate-900 dark:text-white">{variantLabel(variant.name)}</span>
                {#if variant.isOriginal}<span class="mt-1 block text-[11px] font-semibold uppercase tracking-wide text-cyan-700 dark:text-cyan-300">{i18n.m.calibration.reference_label}</span>{/if}
                {#if ratings[variant.name]}<span class="mt-1 block truncate text-[11px] font-semibold text-cyan-700 dark:text-cyan-300">{ratingLabel(ratings[variant.name]!)}</span>{/if}
                {#if revealed}<span class="mt-1 block text-xs text-slate-500 dark:text-slate-400">{revealedLabel(variant.name)}</span>{/if}
                {#if resultVariant?.recommended}<span class="absolute right-2 top-2 h-2.5 w-2.5 rounded-full bg-emerald-500" title={i18n.m.calibration.recommended_sample}></span>{/if}
              </button>
            {/each}
          </div>
        </section>
      </div>

      <aside class="min-w-0 space-y-5">
        {#if !revealed}
          <section class="card p-5 xl:sticky xl:top-0">
            <h2 class="font-semibold text-slate-900 dark:text-white">{i18n.m.calibration.classification_title}</h2>
            <p class="mt-1 text-sm leading-relaxed text-slate-500 dark:text-slate-400">{i18n.m.calibration.classification_hint}</p>
            <div class="mt-4 rounded-xl bg-slate-100 p-3 text-center dark:bg-slate-800"><span class="text-xs uppercase tracking-wide text-slate-500">{activeVariant?.isOriginal ? i18n.m.calibration.reference_label : i18n.m.calibration.sample_label}</span><strong class="ml-2 text-2xl text-slate-950 dark:text-white">{variantLabel(activeName)}</strong></div>
            {#if activeVariant?.isOriginal}
              <p class="mt-3 rounded-xl border border-cyan-200 bg-cyan-50 p-4 text-sm leading-relaxed text-cyan-900 dark:border-cyan-900 dark:bg-cyan-950/40 dark:text-cyan-200">{i18n.m.calibration.reference_hint}</p>
            {:else}
              <div class="mt-3 space-y-2">
                {#each ratingOptions as rating}
                  <button class="min-h-12 w-full rounded-xl border px-3 py-2 text-left text-sm font-semibold transition-colors" class:border-cyan-500={ratings[activeName] === rating} class:bg-cyan-50={ratings[activeName] === rating} class:text-cyan-800={ratings[activeName] === rating} class:border-slate-200={ratings[activeName] !== rating} class:dark:border-slate-700={ratings[activeName] !== rating} class:dark:bg-cyan-950={ratings[activeName] === rating} class:dark:text-cyan-200={ratings[activeName] === rating} disabled={playbackError} onclick={() => classify(activeName, rating)}>{ratingLabel(rating)}<span class="mt-0.5 block text-xs font-normal opacity-70">{ratingHint(rating)}</span></button>
                {/each}
              </div>
            {/if}
            <button class="btn btn-primary mt-5 min-h-12 w-full" disabled={!canReveal || busy} onclick={revealResults}>{busy ? i18n.m.common.loading_short : i18n.m.calibration.reveal_lineup}</button>
          </section>
        {:else if session.result}
          <section class="card overflow-hidden">
            <div class="border-b border-slate-200 p-5 dark:border-slate-700"><p class="text-xs font-semibold uppercase tracking-[0.16em] text-cyan-700 dark:text-cyan-300">{i18n.m.calibration.result_title}</p><h2 class="mt-1 text-xl font-bold text-slate-950 dark:text-white">{session.result.recommendedQuality !== null ? i18n.m.calibration.result_preference : i18n.m.calibration.result_none}</h2></div>
            <div class="divide-y divide-slate-100 dark:divide-slate-800">
              {#each session.result.variants as variant}
                <div class="px-5 py-3" class:bg-emerald-50={variant.recommended} class:dark:bg-emerald-950={variant.recommended}>
                  <div class="flex items-center gap-3"><strong class="w-16 text-slate-900 dark:text-white">{variant.isOriginal ? i18n.m.calibration.original : variant.name}</strong><span class="min-w-0 flex-1 text-sm text-slate-600 dark:text-slate-300">{revealedLabel(variant.name)}</span>{#if !variant.isOriginal}<span class="text-xs text-slate-500">{ratingLabel(variant.classification)}</span>{/if}</div>
                  {#if variant.vmaf}
                    <div class="mt-2 grid grid-cols-3 gap-px overflow-hidden rounded-lg border border-slate-200 bg-slate-200 font-mono text-[11px] tabular-nums dark:border-slate-700 dark:bg-slate-700" title={[variant.vmaf.modelVersion, variant.vmaf.preprocessing, `${variant.vmaf.measuredSamples}/${variant.vmaf.totalSamples} scenes measured`].filter(Boolean).join(' · ')}>
                      <span class="bg-slate-50 px-2 py-1.5 text-cyan-700 dark:bg-slate-900 dark:text-cyan-300">VMAF H {vmafScore(variant.vmaf.harmonicMean)}</span>
                      <span class="bg-slate-50 px-2 py-1.5 text-slate-600 dark:bg-slate-900 dark:text-slate-300">P5 {vmafScore(variant.vmaf.fifthPercentile)}</span>
                      <span class="bg-slate-50 px-2 py-1.5 text-slate-600 dark:bg-slate-900 dark:text-slate-300">min {vmafScore(variant.vmaf.minimum)}</span>
                    </div>
                  {/if}
                </div>
              {/each}
            </div>
            <div class="p-5">
              {#if session.result.recommendedQuality !== null}
                <p class="text-sm text-slate-600 dark:text-slate-400">{i18n.m.calibration.recommendation}: <strong class="text-slate-900 dark:text-white">{session.mediaKind === 'Audio' ? `${session.result.recommendedQuality} kbps` : session.mediaKind === 'Image' ? t(i18n.m.calibration.image_quality_value, { quality: session.result.recommendedQuality }) : [session.result.recommendedProfile, session.result.encoder, `${session.result.qualityMode ?? 'CRF'} ${session.result.effectiveQuality ?? session.result.recommendedQuality}`].filter(Boolean).join(' · ')}</strong></p>
                {#if session.result.estimatedSavingPercent !== null}<p class="mt-1 text-sm text-slate-600 dark:text-slate-400">{i18n.m.calibration.estimated_saving}: <strong>{session.result.estimatedSavingPercent}%</strong></p>{/if}
                <button class="btn btn-primary mt-5 min-h-12 w-full" disabled={busy || session.status === 'Applied'} onclick={applyResult}>{session.status === 'Applied' ? i18n.m.calibration.applied : i18n.m.calibration.apply}</button>
              {:else}<p class="text-sm leading-relaxed text-slate-600 dark:text-slate-400">{i18n.m.calibration.keep_current}</p>{/if}
            </div>
          </section>
        {/if}
      </aside>
    </div>
  {/if}
</div>

<style>
  .quality-lab :global(:fullscreen) { width: 100vw; height: 100vh; border-radius: 0; }
  .quality-lab :global(:fullscreen > div:nth-child(2)) { height: calc(100vh - 7.5rem); min-height: 0; aspect-ratio: auto; }
  @media (prefers-reduced-motion: reduce) { .quality-lab * { scroll-behavior: auto !important; transition-duration: 0.01ms !important; } }
</style>
