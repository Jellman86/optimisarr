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
  import { actualPixelsZoom, nextUnratedName, sampleForKey, settingChange, type CalibrationSetting } from '../calibration-presentation'
  import { ruleProfileLabel } from '../rule-profile-label'
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
  let compareVideoPresets = $state(false)
  let viewer = $state<HTMLElement | null>(null)
  let fullscreen = $state(false)
  let hdrDisplaySupported = $state(false)
  let hdrViewingConfirmed = $state(false)
  let viewZoom = $state(1)
  let panX = $state(0)
  let panY = $state(0)
  let panDrag = $state<{ x: number, y: number, panX: number, panY: number } | null>(null)
  let comparisonStage = $state<HTMLElement | null>(null)
  let pictureSize = $state<{ width: number, height: number } | null>(null)
  let pollTimer: ReturnType<typeof setTimeout> | null = null
  let closed = false
  let focusRoot = $state<HTMLElement | null>(null)

  async function focusStage(previousFocus: Element | null, target?: 'rating') {
    await tick()
    if (closed) return
    const currentFocus = document.activeElement
    // Only hand off focus when the previous control disappeared. A background poll must not
    // pull someone away from navigation or another control they deliberately focused.
    if (currentFocus !== document.body && currentFocus !== previousFocus) return
    if (previousFocus !== document.body && previousFocus?.isConnected) return
    const stage = target ?? (!session ? 'source' : session.status === 'Preparing' ? 'preparing'
      : session.status === 'Failed' ? 'failed' : session.status === 'Comparing' ? 'compare' : 'result'
    )
    focusRoot?.querySelector<HTMLElement>(`[data-quality-focus="${stage}"]`)?.focus()
  }

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
  // A rating always names the sample on screen: while a switch is pending, the stage still shows
  // the previous sample, so ratings wait rather than land on the wrong one.
  const ratingLocked = $derived(switching || playbackError)
  const nextUnrated = $derived(nextUnratedName(candidateVariants.map((variant) => variant.name), ratings, activeName))
  const mediaKind = $derived(session?.mediaKind ?? selected?.mediaKind ?? 'Video')
  const change = $derived(session?.result ? settingChange(session.result) : null)
  const steps = $derived([
    i18n.m.calibration.step_source,
    i18n.m.calibration.step_prepare,
    i18n.m.calibration.step_compare,
    i18n.m.calibration.step_result,
  ])
  const currentStep = $derived(
    !session ? 0
      : session.status === 'Preparing' || session.status === 'Failed' ? 1
        : session.status === 'Comparing' ? 2
          : 3,
  )
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
    const previousFocus = document.activeElement
    busy = true
    error = null
    try {
      session = await api.startCalibration(
        libraryId,
        selectedSource,
        selected?.isHdr === true && hdrReady,
        diagnosticsEnabled,
        ignoreActiveStreams,
        selected?.mediaKind === 'Video' && compareVideoPresets,
      )
      activeName = 'ORIGINAL'
      activeScene = 0
      ratings = {}
      schedulePoll()
      await focusStage(previousFocus)
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
    const previousStatus = session.status
    const previousFocus = document.activeElement
    try {
      session = await api.calibration(session.id)
      if (session.variants.length > 0 && !session.variants.some((variant) => variant.name === activeName)) {
        activeName = session.variants[0].name
      }
      schedulePoll()
      if (session.status !== previousStatus) await focusStage(previousFocus)
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
    if (variants.find((variant) => variant.name === name)?.isOriginal && player.videoWidth > 0) {
      pictureSize = { width: player.videoWidth, height: player.videoHeight }
    }
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

  // A browser that cannot seek a resource (no range support, data not yet seekable) clamps the
  // position instead of failing, so a finished seek only counts if it landed where it was sent.
  // Landing on the end is allowed: an encode may be a frame shorter than its scene.
  async function seekTo(player: HTMLMediaElement, time: number): Promise<boolean> {
    player.currentTime = time
    const reachable = Number.isFinite(player.duration) ? Math.min(time, player.duration) : time
    return await waitForSeek(player) && Math.abs(player.currentTime - reachable) < 0.05
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
    const previousFocus = document.activeElement
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
        await focusStage(previousFocus, 'rating')
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
    const frameReady = await seekTo(target, targetSample.startSeconds + switchPosition)
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
    target.volume = Math.min(1, Math.pow(10, targetSample.gainDb / 20))
    const frameReady = await seekTo(target, targetSample.startSeconds + playbackPosition)
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
    viewZoom = 1
    panX = 0
    panY = 0
    await tick()
    if (!switchingVideo || sequence !== switchSequence) return
    const player = await waitForVideoPlayer(activeName, sequence)
    const sample = sampleFor(activeName)
    if (!player || !sample || !await waitForMetadata(player)) {
      playbackError = true
      switching = false
      return
    }
    const frameReady = await seekTo(player, sample.startSeconds)
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
      // The picture left on screen at pause can run a frame ahead of currentTime. Seeking to
      // that time shows the frame every other sample will seek to, so stills compare alike.
      if (session?.mediaKind === 'Video' && !await seekTo(player, player.currentTime)) playbackError = true
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
      if (!await seekTo(videoPlayer, sample.startSeconds + position)) playbackError = true
      return
    }
    const waits: Promise<boolean>[] = []
    for (const variant of variants) {
      const player = players[variant.name]
      const sample = sampleFor(variant.name)
      if (!player || !sample) continue
      waits.push(seekTo(player, sample.startSeconds + position))
    }
    const ready = await Promise.all(waits)
    if (ready.some((value) => !value)) playbackError = true
  }

  function classify(name: string, rating: CalibrationClassification) {
    if (revealed || ratingLocked || variants.find((variant) => variant.name === name)?.isOriginal) return
    ratings = { ...ratings, [name]: rating }
  }

  function variantLabel(name: string): string {
    return variants.find((variant) => variant.name === name)?.isOriginal
      ? i18n.m.calibration.original
      : name
  }

  async function revealResults() {
    if (!session || !canReveal) return
    const previousFocus = document.activeElement
    busy = true
    error = null
    try {
      session = await api.classifyCalibration(session.id, ratings as Record<string, CalibrationClassification>)
      await focusStage(previousFocus)
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

  async function checkAnother() {
    const previousFocus = document.activeElement
    if (pollTimer) clearTimeout(pollTimer)
    const finished = session
    session = null
    ratings = {}
    activeName = 'ORIGINAL'
    activeScene = 0
    playbackError = false
    playing = false
    playbackPosition = 0
    players = {}
    videoPlayer = null
    error = null
    pictureSize = null
    fitView()
    await focusStage(previousFocus)
    if (finished) await api.deleteCalibration(finished.id).catch(() => undefined)
  }

  async function toggleFullscreen() {
    if (!viewer) return
    if (document.fullscreenElement) await document.exitFullscreen()
    else await viewer.requestFullscreen()
  }

  function zoom(delta: number) {
    const actual = actualPixelsScale() ?? 1
    viewZoom = Math.max(Math.min(1, actual), Math.min(Math.max(4, actual), viewZoom + delta))
    if (viewZoom === 1) {
      panX = 0
      panY = 0
    }
  }

  function fitView() {
    viewZoom = 1
    panX = 0
    panY = 0
  }

  function actualPixelsScale(): number | null {
    if (!pictureSize || !comparisonStage) return null
    return actualPixelsZoom(pictureSize.width, pictureSize.height, comparisonStage.clientWidth, comparisonStage.clientHeight)
  }

  function showActualPixels() {
    const actual = actualPixelsScale()
    if (actual === null) return
    viewZoom = actual
    panX = 0
    panY = 0
  }

  function recordImageSize(isOriginal: boolean, event: Event) {
    const image = event.currentTarget as HTMLImageElement
    if (isOriginal) pictureSize = { width: image.naturalWidth, height: image.naturalHeight }
  }

  function startPan(event: PointerEvent) {
    if (viewZoom <= 1) return
    panDrag = { x: event.clientX, y: event.clientY, panX, panY }
    ;(event.currentTarget as HTMLElement).setPointerCapture(event.pointerId)
  }

  function movePan(event: PointerEvent) {
    if (!panDrag) return
    panX = panDrag.panX + event.clientX - panDrag.x
    panY = panDrag.panY + event.clientY - panDrag.y
  }

  // Switching by key keeps the eyes on the picture, including in full screen where the sample
  // deck is out of view. Keys typed into a form field stay with that field.
  function switchSampleByKey(event: KeyboardEvent) {
    if (!session || session.status === 'Preparing' || session.status === 'Failed') return
    if (event.repeat || event.altKey || event.ctrlKey || event.metaKey || typingInto(event.target)) return
    const name = sampleForKey(event.key, variants.map((variant) => variant.name))
    if (!name) return
    event.preventDefault()
    void chooseVariant(name)
  }

  function typingInto(target: EventTarget | null): boolean {
    return target instanceof HTMLElement && (target.isContentEditable
      || target.matches('textarea, select, input:not([type="range"], [type="checkbox"], [type="radio"], [type="button"])'))
  }

  function ratingLabel(rating: CalibrationClassification): string {
    if (rating === 'Indistinguishable') return i18n.m.calibration.indistinguishable
    if (rating === 'Acceptable') return i18n.m.calibration.acceptable
    return mediaKind === 'Audio' ? i18n.m.calibration.audibly_worse : i18n.m.calibration.visibly_worse
  }

  function ratingHint(rating: CalibrationClassification): string {
    if (rating === 'Indistinguishable') {
      return mediaKind === 'Audio' ? i18n.m.calibration.indistinguishable_hint_audio : i18n.m.calibration.indistinguishable_hint
    }
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

  function settingLabel(setting: CalibrationSetting): string {
    if (mediaKind === 'Audio') return `${setting.quality} kbps`
    if (mediaKind === 'Image') return t(i18n.m.calibration.image_quality_value, { quality: setting.quality })
    return [setting.profile ? ruleProfileLabel(setting.profile, i18n.m.libraries) : null, `CRF ${setting.quality}`]
      .filter(Boolean)
      .join(' · ')
  }

  function sceneLabel(current: number, total: number): string {
    return t(mediaKind === 'Audio' ? i18n.m.calibration.excerpt_label : i18n.m.calibration.scene_label, { current, total })
  }

  function sceneButton(number: number): string {
    return t(mediaKind === 'Audio' ? i18n.m.calibration.excerpt_button : i18n.m.calibration.scene_button, { number })
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

<svelte:window onkeydown={switchSampleByKey} />

<svelte:head>
  <title>{i18n.m.calibration.eyebrow} · Optimisarr</title>
</svelte:head>

<div class="quality-lab min-h-full" bind:this={focusRoot}>
  <header class="mb-5 flex flex-col gap-4 border-b border-line pb-5 sm:flex-row sm:items-end sm:justify-between">
    <div>
      <button class="btn btn-ghost -ml-3 mb-2 min-h-11" onclick={() => router.go(`/libraries/${libraryId}/configure`)}>
        <Icon name="arrow-left" class="h-4 w-4" />
        {i18n.m.calibration.back_to_library}
      </button>
      <!-- One name for the feature: the button that navigates here says "Personal quality check",
           so the page it lands on says the same. A separate eyebrow would only repeat it. -->
      <h1 class="text-2xl font-bold tracking-tight text-ink sm:text-3xl">{i18n.m.calibration.eyebrow}</h1>
      <p class="mt-2 max-w-3xl text-sm leading-relaxed text-ink-2">
        {library?.name ? `${library.name} · ` : ''}{i18n.m.calibration.lab_intro}
      </p>
    </div>
    {#if session?.status === 'Comparing'}
      <div class="rounded-full border border-line bg-panel px-4 py-2 text-sm font-semibold text-ink-2 shadow-sm" aria-live="polite">
        {t(i18n.m.calibration.sorted_progress, { current: classifiedCount, total: candidateVariants.length })}
      </div>
    {/if}
  </header>

  {#if !loading && library}
    <!-- Narrow screens name only the current step; the others stay as numbered markers. -->
    <ol class="mb-5 flex flex-wrap gap-2" aria-label={i18n.m.calibration.steps}>
      {#each steps as step, index}
        <li class="flex min-h-11 items-center gap-2 rounded-xl border px-3 py-2 text-sm sm:flex-1 {index === currentStep ? 'flex-1 border-accent-line bg-accent-soft font-semibold text-ink' : 'border-line text-ink-3'}" aria-current={index === currentStep ? 'step' : undefined}>
          <span class="inline-flex h-6 w-6 shrink-0 items-center justify-center rounded-full text-xs font-bold {index < currentStep ? 'bg-ok-soft text-ok' : index === currentStep ? 'bg-accent-fill text-accent-on' : 'bg-sunken text-ink-3'}" aria-hidden="true">
            {#if index < currentStep}<Icon name="check" class="h-3.5 w-3.5" />{:else}{index + 1}{/if}
          </span>
          <span class="min-w-0 {index === currentStep ? '' : 'max-sm:sr-only'}">{step}</span>
        </li>
      {/each}
    </ol>
  {/if}

  {#if error}<div role="alert"><Banner kind="error" class="mb-5">{error}</Banner></div>{/if}
  {#if playbackError}
    <div role="alert"><Banner kind="error" class="mb-5">{i18n.m.calibration.playback_error}</Banner></div>
    {#if !revealed}<button class="btn mb-5 min-h-11" onclick={checkAnother}>{i18n.m.calibration.check_another}</button>{/if}
  {/if}

  {#if loading}
    <div class="card flex min-h-72 items-center justify-center text-sm text-ink-3">{i18n.m.common.loading}</div>
  {:else if !session}
    <div class="quality-columns">
      <section class="card p-5 sm:p-6">
        <h2 class="text-lg font-semibold text-ink" tabindex="-1" data-quality-focus="source">
          {selected?.mediaKind === 'Audio' ? i18n.m.calibration.choose_audio : selected?.mediaKind === 'Image' ? i18n.m.calibration.choose_image : i18n.m.calibration.choose_source}
        </h2>
        <p class="mt-2 text-sm leading-relaxed text-ink-2">{i18n.m.calibration.intro}</p>
        {#if sources.length === 0}
          <div class="mt-6 rounded-xl border border-dashed border-line p-8 text-center">
            <p class="font-semibold text-ink">{i18n.m.calibration.no_sources}</p>
            <p class="mt-2 text-sm text-ink-3">{i18n.m.calibration.no_sources_hint}</p>
          </div>
        {:else}
          <label class="label mt-6" for="quality-source">{selected?.mediaKind === 'Audio' ? i18n.m.calibration.source_audio : selected?.mediaKind === 'Image' ? i18n.m.calibration.source_image : i18n.m.calibration.source_label}</label>
          <select id="quality-source" class="input" bind:value={selectedSource}>
            {#each sources as source}<option value={source.mediaFileId}>{sourceLabel(source)}</option>{/each}
          </select>
          {#if selected?.mediaKind === 'Video'}
            <p class="mt-4 text-sm text-ink-2">{i18n.m.calibration.selected_settings_hint}</p>
            <label class="mt-3 flex min-h-11 cursor-pointer items-start gap-3 rounded-xl border border-line p-3 text-sm text-ink">
              <input class="mt-1" type="checkbox" bind:checked={compareVideoPresets} />
              <span><strong class="block">{i18n.m.calibration.compare_presets}</strong><span class="mt-0.5 block text-xs text-ink-2">{i18n.m.calibration.compare_presets_hint}</span></span>
            </label>
          {/if}
          {#if selected?.isHdr}
            <div class="callout tone-warn mt-5 rounded-xl p-4">
              <p class="font-semibold">{i18n.m.calibration.hdr_title}</p>
              {#if hdrDisplaySupported}
                <p class="mt-1 leading-relaxed">{i18n.m.calibration.hdr_body}</p>
                <label class="mt-3 flex min-h-11 items-start gap-3"><input class="mt-1" type="checkbox" bind:checked={hdrViewingConfirmed} /><span>{i18n.m.calibration.hdr_confirm}</span></label>
              {:else}<p class="mt-1 leading-relaxed">{i18n.m.calibration.hdr_unsupported}</p>{/if}
            </div>
          {/if}
          <label class="mt-5 flex min-h-11 cursor-pointer items-start gap-3 rounded-xl border border-line bg-sunken p-3 text-sm text-ink">
            <input class="mt-1" type="checkbox" bind:checked={ignoreActiveStreams} />
            <span><strong class="block">{i18n.m.calibration.ignore_streams_label}</strong><span class="mt-0.5 block text-xs text-ink-2">{i18n.m.calibration.ignore_streams_hint}</span></span>
          </label>
          <!-- A debugging aid that breaks blindness; kept out of the way unless already chosen. -->
          <details class="mt-3 text-sm" open={diagnosticsEnabled}>
            <summary class="focus-ring cursor-pointer py-2 text-ink-3">{i18n.m.calibration.troubleshooting}</summary>
            <label class="mt-2 flex min-h-11 cursor-pointer items-start gap-3 rounded-xl p-3 {diagnosticsEnabled ? 'callout tone-warn' : 'border border-line'}">
              <input class="mt-1" type="checkbox" bind:checked={diagnosticsEnabled} />
              <span><strong class="block">{i18n.m.calibration.diag_verify_label}</strong><span class="mt-0.5 block text-xs opacity-80">{i18n.m.calibration.diag_verify_hint}</span></span>
            </label>
          </details>
          <button class="btn btn-primary mt-6 min-h-11" disabled={busy || !hdrReady} onclick={start}>
            {busy ? i18n.m.calibration.starting : i18n.m.calibration.start}
          </button>
        {/if}
      </section>
      <aside class="card self-start p-5">
        <p class="flex items-center gap-2 font-semibold text-ink"><Icon name="shield-check" class="h-4 w-4 text-ok" />{i18n.m.calibration.safe_title}</p>
        <p class="mt-2 text-sm leading-relaxed text-ink-2">{mediaKind === 'Audio' ? i18n.m.calibration.safe_body_audio : mediaKind === 'Image' ? i18n.m.calibration.safe_body_image : i18n.m.calibration.safe_body}</p>
      </aside>
    </div>
  {:else if session.status === 'Preparing'}
    <section class="card mx-auto max-w-2xl p-6 sm:p-8">
      <div class="flex items-center justify-between gap-3"><h2 class="text-lg font-semibold text-ink" tabindex="-1" data-quality-focus="preparing">{session.preparationState === 'Waiting' ? i18n.m.calibration.waiting : i18n.m.calibration.preparing}</h2><span class="font-mono text-sm font-semibold text-accent">{Math.round(session.preparationProgress * 100)}%</span></div>
      <div class="mt-5 h-2 overflow-hidden rounded-full bg-sunken" role="progressbar" aria-label={i18n.m.calibration.preparing} aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(session.preparationProgress * 100)}><div class="h-full rounded-full bg-accent-fill transition-[width] duration-300" style:width={`${Math.max(1, session.preparationProgress * 100)}%`}></div></div>
      <p class="mt-4 text-sm leading-relaxed text-ink-2">{session.preparationState === 'Waiting' ? i18n.m.calibration.waiting_hint : session.mediaKind === 'Audio' ? i18n.m.calibration.preparing_audio_hint : session.mediaKind === 'Image' ? i18n.m.calibration.preparing_image_hint : compareVideoPresets ? i18n.m.calibration.preparing_hint : i18n.m.calibration.preparing_quality_hint}</p>
      <div class="mt-6 border-t border-line pt-5">
        <div class="mb-3 flex items-center gap-2">
          <Icon name="gpu" class="h-4 w-4 text-ink-4" />
          <div class="label">{i18n.m.dashboard.live_usage}</div>
        </div>
        <div class="grid gap-3 sm:grid-cols-2">
          <UsageGraph label="CPU" data={activity.cpuHistory} current={activity.metrics?.cpuPercent ?? null} color="var(--accent)" />
          <UsageGraph
            label="GPU"
            data={activity.gpuHistory}
            current={activity.metrics?.gpuPercent ?? null}
            color="var(--ink-3)"
            unavailable={gpuUnavailable}
            detail={activity.metrics?.gpuEngine}
          />
        </div>
      </div>
      {#if error}<button class="btn mt-5 min-h-11" onclick={checkAnother}>{i18n.m.calibration.check_another}</button>{/if}
    </section>
  {:else if session.status === 'Failed'}
    <section class="card mx-auto max-w-2xl space-y-4 p-6 sm:p-8">
      <div role="alert" tabindex="-1" data-quality-focus="failed"><Banner kind="error">{session.error ?? i18n.m.calibration.failed}</Banner></div>
      <button class="btn min-h-11" onclick={checkAnother}>{i18n.m.calibration.check_another}</button>
    </section>
  {:else}
    <div class="quality-columns">
      <div class="min-w-0 space-y-5">
        <section class="scheme-dark overflow-hidden rounded-2xl bg-black text-ink shadow-xl ring-1 ring-white/10" bind:this={viewer} aria-busy={switching}>
          <div class="flex min-h-16 items-center justify-between gap-3 border-b border-white/10 px-3 py-2.5 text-white/85 sm:px-4 sm:py-3">
            <div class="min-w-0 flex-1">
              <span class="block text-xs font-semibold text-ink-3">{activeVariant?.isOriginal ? i18n.m.calibration.reference_label : i18n.m.calibration.sample_label}</span>
              <h2 class="block truncate text-lg font-bold leading-tight" tabindex="-1" data-quality-focus="compare">{variantLabel(activeName)}</h2>
            </div>
            <div class="flex shrink-0 items-center gap-1 sm:gap-2">
              {#if switching}<span class="text-xs font-medium text-accent" aria-live="polite">{i18n.m.common.loading_short}</span>{/if}
              {#if activeSample && activeSample.sampleCount > 1}<span class="hidden text-xs text-ink-4 min-[360px]:inline">{sceneLabel(activeScene + 1, activeSample.sampleCount)}</span>{/if}
              {#if session.mediaKind === 'Video'}
                <button class="inline-flex min-h-11 min-w-11 items-center justify-center rounded-lg text-white/70 hover:bg-white/10 hover:text-white focus-visible:outline-2 focus-visible:outline-accent" aria-label={fullscreen ? i18n.m.calibration.exit_fullscreen : i18n.m.calibration.fullscreen} title={fullscreen ? i18n.m.calibration.exit_fullscreen : i18n.m.calibration.fullscreen} onclick={toggleFullscreen}>
                  <svg class="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2"><path stroke-linecap="round" stroke-linejoin="round" d={fullscreen ? 'M9 4v5H4M15 4v5h5M9 20v-5H4M15 20v-5h5' : 'M9 4H4v5M15 4h5v5M9 20H4v-5M15 20h5v-5'} /></svg>
                </button>
              {/if}
            </div>
          </div>
          <div class="comparison-stage relative flex w-full items-center justify-center overflow-hidden bg-black" class:aspect-video={session.mediaKind !== 'Audio'} class:cursor-grab={session.mediaKind !== 'Audio' && viewZoom > 1} class:cursor-grabbing={panDrag !== null} class:touch-none={viewZoom > 1} role="group" aria-label={session.mediaKind === 'Image' ? i18n.m.calibration.image_viewport : i18n.m.calibration.switch_label} bind:this={comparisonStage} onpointerdown={startPan} onpointermove={movePan} onpointerup={() => (panDrag = null)} onpointercancel={() => (panDrag = null)}>
            {#key activeScene}
              {#if session.mediaKind === 'Image'}
                {#each variants as variant}
                  <img src={variant.samples[activeScene]?.url} alt={variant.isOriginal ? i18n.m.calibration.original_reference : t(i18n.m.calibration.image_alt, { slot: variant.name })} draggable="false" class="absolute inset-0 h-full w-full select-none object-contain" class:invisible={variant.name !== activeName} style:transform={`translate(${panX}px, ${panY}px) scale(${viewZoom})`} onload={(event: Event) => recordImageSize(variant.isOriginal, event)} onerror={() => (playbackError = true)} />
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
                    style:transform={`translate(${panX}px, ${panY}px) scale(${viewZoom})`}
                    onloadedmetadata={(event: Event) => registerVideoPlayer(videoName, event)}
                    ontimeupdate={updatePosition}
                    onplay={() => pendingName === null && (playing = true)}
                    onpause={() => pendingName === null && (playing = false)}
                    onerror={() => (playbackError = true)}
                  ></video>
                {/key}
                {#if switching}<div class="absolute inset-0 flex items-center justify-center bg-black text-sm font-medium text-accent" aria-live="polite">{i18n.m.calibration.loading_resource}</div>{/if}
              {:else}
                {#each variants as variant}
                  <audio src={variant.samples[activeScene]?.url} preload="auto" class="hidden" onloadedmetadata={(event: Event) => registerAudioPlayer(variant.name, event)} ontimeupdate={updatePosition} onplay={() => variant.name === activeName && (playing = true)} onpause={() => variant.name === activeName && (playing = false)} onerror={() => (playbackError = true)}></audio>
                {/each}
                <!-- Music needs no picture: a compact listening header keeps the switcher and rating close. -->
                <div class="flex w-full items-center gap-4 px-4 py-5 text-white/80 sm:px-5">
                  <Icon name="waveform" class="h-8 w-8 shrink-0 text-accent" />
                  <div class="min-w-0">
                    <p class="text-sm font-semibold text-white">{i18n.m.calibration.audio_listening}</p>
                    <p class="mt-0.5 text-xs leading-relaxed text-white/65">{i18n.m.calibration.audio_listening_hint}</p>
                  </div>
                </div>
              {/if}
            {/key}
          </div>
          <div class="space-y-3 border-t border-slate-800 px-4 py-3">
            {#if session.mediaKind === 'Image'}
              {@render zoomControls(i18n.m.calibration.image_zoom_controls, i18n.m.calibration.fit_view_label, i18n.m.calibration.pan_hint)}
            {:else if activeSample && !(session.mediaKind === 'Video' && diagnosticsEnabled)}
              <div class="flex items-center gap-3"><button class="inline-flex min-h-11 min-w-11 items-center justify-center rounded-full bg-accent-fill text-accent-on hover:brightness-110" aria-label={playing ? i18n.m.calibration.pause_sample : i18n.m.calibration.play_sample} onclick={togglePlayback}><Icon name={playing ? 'pause' : 'play'} class="h-5 w-5" /></button><input class="min-h-11 min-w-0 flex-1 accent-accent" type="range" min="0" max={activeSample.durationSeconds} step="0.05" value={playbackPosition} aria-label={i18n.m.calibration.sample_position} oninput={(event) => seek(Number(event.currentTarget.value))} /><span class="w-20 text-right font-mono text-xs text-ink-4">{playbackPosition.toFixed(1)} / {activeSample.durationSeconds}s</span></div>
              {#if session.mediaKind === 'Video'}
                {@render zoomControls(i18n.m.calibration.video_zoom_controls, i18n.m.calibration.fit_picture_label, i18n.m.calibration.video_pan_hint)}
              {/if}
            {:else if activeSample}
              <p class="text-center text-xs text-ink-4">{i18n.m.calibration.native_controls_note}</p>
            {/if}
            {#if fullscreen}
              <!-- Full screen hides the sample deck, so the viewer carries its own switcher. -->
              <div class="flex flex-wrap items-center justify-center gap-x-4 gap-y-2">
                <div class="flex flex-wrap items-center justify-center gap-1.5" role="group" aria-label={i18n.m.calibration.switch_sample}>
                  {#each variants as variant}
                    <button class="min-h-11 min-w-11 rounded-full px-3 text-sm font-bold focus-visible:outline-2 focus-visible:outline-accent {activeName === variant.name ? 'bg-accent-fill text-accent-on' : 'bg-white/10 text-white hover:bg-white/20'}" aria-pressed={activeName === variant.name} aria-label={variant.isOriginal ? i18n.m.calibration.show_original : t(i18n.m.calibration.show_sample, { name: variant.name })} aria-keyshortcuts={variant.isOriginal ? 'O' : variant.name} onclick={() => chooseVariant(variant.name)}>{variant.isOriginal ? 'O' : variant.name}</button>
                  {/each}
                </div>
                <span class="text-xs text-white/60">{i18n.m.calibration.shortcut_hint}</span>
              </div>
            {/if}
          </div>
          {#if activeDiagnostics && activeSample}
            <div class="border-t border-warn-line bg-warn-soft px-4 py-3 text-warn-strong" aria-live="polite">
              <div class="flex flex-wrap items-center justify-between gap-2">
                <strong class="text-xs font-semibold text-warn">{i18n.m.calibration.stream_diagnostics}</strong>
                <span class="font-mono text-xs">{diagnosticSummary()}</span>
              </div>
              <dl class="mt-2 grid gap-x-4 gap-y-1 text-[11px] sm:grid-cols-[7rem_minmax(0,1fr)]">
                <dt class="text-warn">{i18n.m.calibration.requested_route}</dt><dd class="break-all font-mono">{activeSample.url}</dd>
                <dt class="text-warn">video.currentSrc</dt><dd class="break-all font-mono">{browserStreamUrl || 'Waiting for the video element…'}</dd>
                <dt class="text-warn">{i18n.m.calibration.playback_time}</dt><dd class="font-mono">{playbackPosition.toFixed(3)}s · {sceneButton(activeScene + 1)}</dd>
              </dl>
              {#if session.mediaKind === 'Video' && browserStreamUrl}
                <a class="mt-3 inline-flex min-h-11 items-center rounded-lg border border-warn-line px-3 py-2 text-xs font-semibold text-warn-strong hover:bg-warn-soft focus-visible:outline-2 focus-visible:outline-warn" href={browserStreamUrl} target="_blank" rel="noopener noreferrer" aria-label="Open exact video resource">{i18n.m.calibration.open_exact_resource}</a>
              {/if}
            </div>
          {/if}
        </section>

        {#if activeSample && activeSample.sampleCount > 1}
          <nav class="flex gap-2" aria-label={session.mediaKind === 'Audio' ? i18n.m.calibration.excerpts : i18n.m.calibration.scenes}>
            {#each activeVariant?.samples ?? [] as sample, index}<button class="btn min-h-11 flex-1" class:btn-primary={activeScene === index} aria-current={activeScene === index ? 'true' : undefined} onclick={() => chooseScene(index)}>{sceneButton(sample.sampleNumber)}</button>{/each}
          </nav>
        {/if}

        <section class="card p-4 sm:p-5">
          <div class="flex flex-col justify-between gap-2 sm:flex-row sm:items-end"><div><h2 class="font-semibold text-ink">{i18n.m.calibration.sample_deck}</h2><p class="mt-1 text-sm text-ink-3">{i18n.m.calibration.sample_deck_hint}</p><p class="mt-1 text-xs text-ink-4">{i18n.m.calibration.shortcut_hint}</p></div><span class="text-xs text-ink-4">{i18n.m.calibration.select_then_rate}</span></div>
          <div class="sample-deck mt-4">
            {#each variants as variant}
              {@const resultVariant = session.result?.variants.find((item) => item.name === variant.name)}
              <button class="choice group relative min-h-20 touch-manipulation rounded-xl px-3 py-3 text-left" class:choice-selected={activeName === variant.name} onclick={() => chooseVariant(variant.name)} aria-label={variant.isOriginal ? i18n.m.calibration.original_reference : variant.name} aria-pressed={activeName === variant.name}>
                <span class="text-xl font-bold text-ink">{variantLabel(variant.name)}</span>
                {#if variant.isOriginal}<span class="mt-1 block text-[11px] font-semibold text-accent">{i18n.m.calibration.reference_label}</span>
                {:else if ratings[variant.name]}<span class="mt-1 flex items-center gap-1 truncate text-[11px] font-semibold text-accent"><Icon name="check" class="h-3 w-3 shrink-0" />{ratingLabel(ratings[variant.name]!)}</span>
                {:else if !revealed}<span class="mt-1 block truncate text-[11px] text-ink-4">{i18n.m.calibration.not_rated}</span>{/if}
                {#if revealed}<span class="mt-1 block text-xs text-ink-3">{revealedLabel(variant.name)}</span>{/if}
                {#if resultVariant?.recommended}<span class="absolute right-2 top-2 h-2.5 w-2.5 rounded-full bg-ok" title={i18n.m.calibration.recommended_sample}></span>{/if}
              </button>
            {/each}
          </div>
        </section>
      </div>

      <aside class="min-w-0 space-y-5">
        {#if !revealed}
          <section class="card p-5 xl:sticky xl:top-0">
            <h2 class="font-semibold text-ink">{session.mediaKind === 'Audio' ? i18n.m.calibration.classification_title_audio : i18n.m.calibration.classification_title}</h2>
            <p class="mt-1 text-sm leading-relaxed text-ink-3">{i18n.m.calibration.classification_hint}</p>
            <div class="mt-4 rounded-xl bg-raised p-3 text-center"><span class="text-xs text-ink-3">{activeVariant?.isOriginal ? i18n.m.calibration.reference_label : i18n.m.calibration.sample_label}</span><strong class="ml-2 text-2xl text-ink">{variantLabel(activeName)}</strong></div>
            {#if activeVariant?.isOriginal}
              <p class="callout tone-accent mt-3 rounded-xl p-4">{i18n.m.calibration.reference_hint}</p>
            {:else}
              <div class="mt-3 space-y-2" role="group" tabindex="-1" data-quality-focus="rating" aria-label={t(i18n.m.calibration.rate_sample, { name: activeName })}>
                {#each ratingOptions as rating}
                  <button class="choice min-h-12 w-full rounded-xl px-3 py-2 text-left text-sm font-semibold" class:choice-selected={ratings[activeName] === rating} aria-pressed={ratings[activeName] === rating} disabled={ratingLocked} onclick={() => classify(activeName, rating)}>{ratingLabel(rating)}<span class="mt-0.5 block text-xs font-normal opacity-70">{ratingHint(rating)}</span></button>
                {/each}
              </div>
              {#if switching}<p class="mt-2 text-xs text-ink-3" aria-live="polite">{i18n.m.calibration.rating_locked_loading}</p>{/if}
            {/if}
            {#if nextUnrated && (activeVariant?.isOriginal || ratings[activeName])}
              <button class="btn mt-3 min-h-11 w-full" disabled={switching} onclick={() => chooseVariant(nextUnrated!)}>
                {t(i18n.m.calibration.next_unrated, { name: nextUnrated })}
                <Icon name="arrow-right" class="h-4 w-4" />
              </button>
            {:else if canReveal}
              <p class="mt-3 text-xs leading-relaxed text-ink-3">{i18n.m.calibration.all_rated}</p>
            {/if}
            <button class="btn btn-primary mt-5 min-h-12 w-full" disabled={!canReveal || busy} onclick={revealResults}>{busy ? i18n.m.common.loading_short : i18n.m.calibration.reveal_lineup}</button>
          </section>
        {:else if session.result}
          <section class="card overflow-hidden">
            <div class="border-b border-line p-5">
              <p class="text-xs font-semibold text-accent">{i18n.m.calibration.result_title}</p>
              <h2 class="mt-1 text-xl font-bold text-ink" tabindex="-1" data-quality-focus="result">{change ? i18n.m.calibration.result_preference : i18n.m.calibration.result_none}</h2>
              <p class="mt-2 text-sm leading-relaxed text-ink-2">{change ? i18n.m.calibration.result_basis : i18n.m.calibration.result_none_basis}</p>
            </div>
            {#if change}
              <div class="border-b border-line p-5">
                <h3 class="text-sm font-semibold text-ink">{i18n.m.calibration.setting_change}</h3>
                <dl class="mt-3 grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1.5 text-sm">
                  <dt class="text-ink-3">{i18n.m.calibration.setting_now}</dt><dd class="text-ink-2">{settingLabel(change.now)}</dd>
                  <dt class="text-ink-3">{i18n.m.calibration.setting_after}</dt><dd class="font-semibold text-ink">{settingLabel(change.after)}</dd>
                </dl>
                {#if change.unchanged}<p class="mt-2 text-xs text-ink-3">{i18n.m.calibration.setting_unchanged}</p>{/if}
                {#if session.result.estimatedSavingPercent !== null}
                  <p class="mt-3 text-sm text-ink-2">{i18n.m.calibration.estimated_saving}: <strong class="text-ink">{session.result.estimatedSavingPercent}%</strong></p>
                  <p class="mt-0.5 text-xs text-ink-3">{i18n.m.calibration.estimate_note}</p>
                {/if}
                <p class="mt-3 flex gap-2 text-xs leading-relaxed text-ink-2"><Icon name="shield-check" class="mt-0.5 h-3.5 w-3.5 shrink-0 text-ok" /><span>{i18n.m.calibration.apply_scope}{#if change.after.profile}{' '}{i18n.m.calibration.apply_scope_presets}{/if}</span></p>
                <button class="btn btn-primary mt-4 min-h-12 w-full" disabled={busy || session.status === 'Applied'} onclick={applyResult}>{session.status === 'Applied' ? i18n.m.calibration.applied : i18n.m.calibration.apply}</button>
                <span class="sr-only" role="status">{session.status === 'Applied' ? i18n.m.calibration.applied : ''}</span>
              </div>
            {/if}
            <div class="divide-y divide-line-soft">
              {#each session.result.variants as variant}
                <div class="px-5 py-3" class:bg-ok-soft={variant.recommended}>
                  <div class="flex items-center gap-3"><strong class="w-16 text-ink">{variant.isOriginal ? i18n.m.calibration.original : variant.name}</strong><span class="min-w-0 flex-1 text-sm text-ink-2">{revealedLabel(variant.name)}</span>{#if !variant.isOriginal}<span class="text-xs text-ink-3">{ratingLabel(variant.classification)}</span>{/if}</div>
                  {#if variant.vmaf}
                    <div class="mt-2 grid grid-cols-3 gap-px overflow-hidden rounded-lg border border-line bg-sunken font-mono text-[11px] tabular-nums" title={[variant.vmaf.modelVersion, variant.vmaf.preprocessing, `${variant.vmaf.measuredSamples}/${variant.vmaf.totalSamples} scenes measured`].filter(Boolean).join(' · ')}>
                      <span class="bg-sunken px-2 py-1.5 text-accent">VMAF H {vmafScore(variant.vmaf.harmonicMean)}</span>
                      <span class="bg-sunken px-2 py-1.5 text-ink-2">P5 {vmafScore(variant.vmaf.fifthPercentile)}</span>
                      <span class="bg-sunken px-2 py-1.5 text-ink-2">min {vmafScore(variant.vmaf.minimum)}</span>
                    </div>
                  {/if}
                </div>
              {/each}
            </div>
            <div class="space-y-3 border-t border-line p-5">
              {#if session.result.variants.some((variant) => variant.vmaf)}<p class="text-xs leading-relaxed text-ink-3">{i18n.m.calibration.vmaf_note}</p>{/if}
              <p class="text-xs leading-relaxed text-ink-3">{session.mediaKind === 'Audio' ? i18n.m.calibration.result_caveat_audio : session.mediaKind === 'Image' ? i18n.m.calibration.result_caveat_image : i18n.m.calibration.result_caveat}</p>
              <button class="btn min-h-11 w-full" disabled={busy} onclick={checkAnother}>{i18n.m.calibration.check_another}</button>
            </div>
          </section>
        {/if}
      </aside>
    </div>
  {/if}
</div>

{#snippet zoomControls(label: string, fitLabel: string, panHint: string)}
  <div class="flex flex-wrap items-center justify-center gap-2" role="group" aria-label={label}>
    <button class="btn min-h-11" aria-label={fitLabel} aria-pressed={viewZoom === 1} onclick={fitView}>{i18n.m.calibration.fit_view}</button>
    <button class="btn min-h-11 min-w-11" aria-label={i18n.m.calibration.zoom_out} onclick={() => zoom(-0.25)}>−</button>
    <span class="min-w-14 text-center font-mono text-xs text-white/75" aria-live="polite">{t(i18n.m.calibration.zoom_level, { percent: Math.round(viewZoom * 100) })}</span>
    <button class="btn min-h-11 min-w-11" aria-label={i18n.m.calibration.zoom_in} onclick={() => zoom(0.25)}>+</button>
    <button class="btn min-h-11" aria-label={i18n.m.calibration.actual_pixels_label} disabled={!pictureSize} onclick={showActualPixels}>{i18n.m.calibration.actual_pixels}</button>
  </div>
  {#if viewZoom > 1}<p class="text-center text-xs text-white/60">{panHint}</p>{/if}
{/snippet}

<style>
  .quality-lab { container-type: inline-size; }
  .quality-lab .btn-primary:hover { background: color-mix(in srgb, var(--accent-fill) 92%, black); }
  .sample-deck { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 7rem), 1fr)); gap: 0.5rem; }
  .quality-columns { display: grid; gap: 1.25rem; }
  @container (min-width: 60rem) {
    .quality-columns { grid-template-columns: minmax(0, 1fr) minmax(0, 22rem); }
  }
  .quality-lab :global(:fullscreen) { display: flex; flex-direction: column; width: 100vw; height: 100vh; border-radius: 0; }
  .quality-lab :global(:fullscreen .comparison-stage) { flex: 1 1 auto; height: auto; min-height: 0; aspect-ratio: auto; }
  @media (prefers-reduced-motion: reduce) { .quality-lab * { scroll-behavior: auto !important; transition-duration: 0.01ms !important; } }
</style>
