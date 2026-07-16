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
  import { router } from '../stores/ui.svelte'
  import Banner from '../components/Banner.svelte'
  import Icon from '../components/Icon.svelte'

  type Rating = CalibrationClassification | null

  const match = router.path.match(/^\/libraries\/(\d+)\/quality-check$/)
  const libraryId = Number(match?.[1] ?? 0)
  const ratingOptions: CalibrationClassification[] = ['Indistinguishable', 'Acceptable', 'VisiblyWorse']

  let library = $state<Library | null>(null)
  let sources = $state<CalibrationSource[]>([])
  let selectedSource = $state<number | null>(null)
  let session = $state<CalibrationSession | null>(null)
  let ratings = $state<Record<string, Rating>>({})
  let activeName = $state('A')
  let activeScene = $state(0)
  let loading = $state(true)
  let busy = $state(false)
  let error = $state<string | null>(null)
  let playbackError = $state(false)
  let playing = $state(false)
  let playbackPosition = $state(0)
  let switching = $state(false)
  let players: Record<string, HTMLMediaElement> = {}
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
  const activeVariant = $derived(variants.find((variant) => variant.name === activeName) ?? variants[0] ?? null)
  const activeSample = $derived(activeVariant?.samples[activeScene] ?? null)
  const classifiedCount = $derived(Object.values(ratings).filter(Boolean).length)
  const canReveal = $derived(!playbackError && variants.length > 0 && classifiedCount === variants.length)
  const revealed = $derived(session?.status === 'Revealed' || session?.status === 'Applied')
  const hdrReady = $derived(!selected?.isHdr || hdrDisplaySupported && hdrViewingConfirmed)

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
      session = await api.startCalibration(libraryId, selectedSource, selected?.isHdr === true && hdrReady)
      activeName = 'A'
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

  function sampleFor(name: string): CalibrationSample | null {
    return variants.find((variant) => variant.name === name)?.samples[activeScene] ?? null
  }

  function registerPlayer(name: string, event: Event) {
    const player = event.currentTarget as HTMLMediaElement
    players[name] = player
    preparePlayer(name, player)
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

  async function chooseVariant(name: string) {
    if (name === activeName || switching) return
    if (session?.mediaKind === 'Image') {
      activeName = name
      return
    }
    const current = players[activeName]
    const currentSample = sampleFor(activeName)
    if (current && currentSample) {
      playbackPosition = Math.max(0, current.currentTime - currentSample.startSeconds)
      playing = !current.paused
      current.pause()
    }
    const target = players[name]
    const targetSample = sampleFor(name)
    if (!target || !targetSample) {
      activeName = name
      return
    }
    switching = true
    target.currentTime = targetSample.startSeconds + playbackPosition
    target.volume = Math.min(1, Math.pow(10, targetSample.gainDb / 20))
    const frameReady = await waitForSeek(target)
    if (!frameReady) {
      playbackError = true
      switching = false
      return
    }
    activeName = name
    if (playing) {
      await target.play().catch(() => {
        playing = false
        playbackError = true
      })
    }
    switching = false
  }

  async function chooseScene(index: number) {
    activeScene = index
    playbackPosition = 0
    playing = false
    players = {}
    imageZoom = 1
    imagePanX = 0
    imagePanY = 0
    await tick()
  }

  function updatePosition(event: Event) {
    const player = event.currentTarget as HTMLMediaElement
    const sample = sampleFor(activeName)
    if (!sample || player !== players[activeName]) return
    playbackPosition = Math.max(0, Math.min(sample.durationSeconds, player.currentTime - sample.startSeconds))
    if (player.currentTime >= sample.startSeconds + sample.durationSeconds) {
      player.pause()
      player.currentTime = sample.startSeconds
      playbackPosition = 0
      playing = false
    }
  }

  async function togglePlayback() {
    const player = players[activeName]
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
    if (revealed || playbackError) return
    ratings = { ...ratings, [name]: rating }
  }

  function dropRating(rating: CalibrationClassification, event: DragEvent) {
    event.preventDefault()
    const name = event.dataTransfer?.getData('text/plain')
    if (name && variants.some((variant) => variant.name === name)) classify(name, rating)
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
    if (!session?.result?.recommendedQuality) return
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
    return `${mode} ${result.effectiveQuality ?? result.quality ?? '—'}`
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
        {t(i18n.m.calibration.sorted_progress, { current: classifiedCount, total: variants.length })}
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
    </section>
  {:else if session.status === 'Failed'}
    <Banner kind="error">{session.error ?? i18n.m.calibration.failed}</Banner>
  {:else}
    <div class="grid gap-5 xl:grid-cols-[minmax(0,1fr)_22rem]">
      <div class="min-w-0 space-y-5">
        <section class="overflow-hidden rounded-2xl border border-slate-800 bg-slate-950 shadow-xl shadow-slate-950/10" bind:this={viewer}>
          <div class="flex items-center justify-between gap-3 border-b border-slate-800 px-4 py-3 text-slate-200">
            <div><span class="text-xs uppercase tracking-[0.18em] text-slate-500">{i18n.m.calibration.sample_label}</span><strong class="ml-2 text-lg">{activeName}</strong></div>
            <div class="flex items-center gap-2">
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
                  <img src={variant.samples[activeScene]?.url} alt={t(i18n.m.calibration.image_alt, { slot: variant.name })} draggable="false" class="absolute inset-0 h-full w-full select-none object-contain" class:invisible={variant.name !== activeName} style:transform={`translate(${imagePanX}px, ${imagePanY}px) scale(${imageZoom})`} onerror={() => (playbackError = true)} />
                {/each}
              {:else}
                {#each variants as variant}
                  <svelte:element this={session.mediaKind === 'Audio' ? 'audio' : 'video'} src={variant.samples[activeScene]?.url} preload="auto" playsinline class="absolute inset-0 h-full w-full object-contain" class:invisible={variant.name !== activeName} onloadedmetadata={(event: Event) => registerPlayer(variant.name, event)} ontimeupdate={updatePosition} onplay={() => variant.name === activeName && (playing = true)} onpause={() => variant.name === activeName && (playing = false)} onerror={() => (playbackError = true)} />
                {/each}
                {#if session.mediaKind === 'Audio'}<div class="pointer-events-none flex flex-col items-center text-slate-300"><svg class="h-20 w-20 text-cyan-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="1.2"><path stroke-linecap="round" stroke-linejoin="round" d="M9 18V5l10-2v13M9 18a3 3 0 11-6 0 3 3 0 016 0zm10-2a3 3 0 11-6 0 3 3 0 016 0z" /></svg><span class="mt-3 text-sm">{i18n.m.calibration.audio_listening}</span></div>{/if}
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
            {:else if activeSample}
              <div class="flex items-center gap-3"><button class="inline-flex min-h-11 min-w-11 items-center justify-center rounded-full bg-cyan-500 text-slate-950 hover:bg-cyan-400" aria-label={playing ? i18n.m.calibration.pause_sample : i18n.m.calibration.play_sample} onclick={togglePlayback}><Icon name={playing ? 'pause' : 'play'} class="h-5 w-5" /></button><input class="min-h-11 min-w-0 flex-1 accent-cyan-400" type="range" min="0" max={activeSample.durationSeconds} step="0.05" value={playbackPosition} aria-label={i18n.m.calibration.sample_position} oninput={(event) => seek(Number(event.currentTarget.value))} /><span class="w-20 text-right font-mono text-xs text-slate-400">{playbackPosition.toFixed(1)} / {activeSample.durationSeconds}s</span></div>
            {/if}
          </div>
        </section>

        {#if activeSample && activeSample.sampleCount > 1}
          <nav class="flex gap-2" aria-label={i18n.m.calibration.scenes}>
            {#each activeVariant?.samples ?? [] as sample, index}<button class="btn min-h-11 flex-1" class:border-cyan-500={activeScene === index} class:text-cyan-700={activeScene === index} onclick={() => chooseScene(index)}>{t(i18n.m.calibration.scene_button, { number: sample.sampleNumber })}</button>{/each}
          </nav>
        {/if}

        <section class="card p-4 sm:p-5">
          <div class="flex flex-col justify-between gap-2 sm:flex-row sm:items-end"><div><h2 class="font-semibold text-slate-900 dark:text-white">{i18n.m.calibration.sample_deck}</h2><p class="mt-1 text-sm text-slate-500 dark:text-slate-400">{i18n.m.calibration.sample_deck_hint}</p></div><span class="text-xs text-slate-400">{i18n.m.calibration.keyboard_drag_hint}</span></div>
          <div class="mt-4 grid grid-cols-3 gap-2 sm:grid-cols-6">
            {#each variants as variant}
              {@const resultVariant = session.result?.variants.find((item) => item.name === variant.name)}
              <button class="group relative min-h-20 rounded-xl border-2 bg-white px-3 py-3 text-left transition-colors dark:bg-slate-900" class:border-cyan-500={activeName === variant.name} class:border-slate-200={activeName !== variant.name} class:dark:border-slate-700={activeName !== variant.name} draggable={!revealed} ondragstart={(event) => event.dataTransfer?.setData('text/plain', variant.name)} onclick={() => chooseVariant(variant.name)} aria-pressed={activeName === variant.name}>
                <span class="text-xl font-bold text-slate-900 dark:text-white">{variant.name}</span>
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
            <div class="mt-4 rounded-xl bg-slate-100 p-3 text-center dark:bg-slate-800"><span class="text-xs uppercase tracking-wide text-slate-500">{i18n.m.calibration.sample_label}</span><strong class="ml-2 text-2xl text-slate-950 dark:text-white">{activeName}</strong></div>
            <div class="mt-3 space-y-2">
              {#each ratingOptions as rating}
                <button class="min-h-12 w-full rounded-xl border px-3 py-2 text-left text-sm font-semibold transition-colors" class:border-cyan-500={ratings[activeName] === rating} class:bg-cyan-50={ratings[activeName] === rating} class:text-cyan-800={ratings[activeName] === rating} class:border-slate-200={ratings[activeName] !== rating} class:dark:border-slate-700={ratings[activeName] !== rating} class:dark:bg-cyan-950={ratings[activeName] === rating} class:dark:text-cyan-200={ratings[activeName] === rating} disabled={playbackError} onclick={() => classify(activeName, rating)} ondragover={(event) => event.preventDefault()} ondrop={(event) => dropRating(rating, event)}>{ratingLabel(rating)}<span class="mt-0.5 block text-xs font-normal opacity-70">{ratingHint(rating)}</span></button>
              {/each}
            </div>
            <button class="btn btn-primary mt-5 min-h-12 w-full" disabled={!canReveal || busy} onclick={revealResults}>{busy ? i18n.m.common.loading_short : i18n.m.calibration.reveal_lineup}</button>
          </section>
        {:else if session.result}
          <section class="card overflow-hidden">
            <div class="border-b border-slate-200 p-5 dark:border-slate-700"><p class="text-xs font-semibold uppercase tracking-[0.16em] text-cyan-700 dark:text-cyan-300">{i18n.m.calibration.result_title}</p><h2 class="mt-1 text-xl font-bold text-slate-950 dark:text-white">{session.result.recommendedQuality !== null ? i18n.m.calibration.result_preference : i18n.m.calibration.result_none}</h2></div>
            <div class="divide-y divide-slate-100 dark:divide-slate-800">
              {#each session.result.variants as variant}<div class="flex items-center gap-3 px-5 py-3" class:bg-emerald-50={variant.recommended} class:dark:bg-emerald-950={variant.recommended}><strong class="w-6 text-slate-900 dark:text-white">{variant.name}</strong><span class="min-w-0 flex-1 text-sm text-slate-600 dark:text-slate-300">{revealedLabel(variant.name)}</span><span class="text-xs text-slate-500">{ratingLabel(variant.classification)}</span></div>{/each}
            </div>
            <div class="p-5">
              {#if session.result.recommendedQuality !== null}
                <p class="text-sm text-slate-600 dark:text-slate-400">{i18n.m.calibration.recommendation}: <strong class="text-slate-900 dark:text-white">{session.mediaKind === 'Audio' ? `${session.result.recommendedQuality} kbps` : session.mediaKind === 'Image' ? t(i18n.m.calibration.image_quality_value, { quality: session.result.recommendedQuality }) : `${session.result.qualityMode ?? 'CRF'} ${session.result.effectiveQuality ?? session.result.recommendedQuality}`}</strong></p>
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
