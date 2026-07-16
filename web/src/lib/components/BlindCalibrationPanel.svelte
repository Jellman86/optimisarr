<script lang="ts">
  import { onDestroy, onMount, tick } from 'svelte'
  import { api, type CalibrationSession, type CalibrationSlot, type CalibrationSource } from '../api'
  import { formatDuration } from '../format'
  import { i18n, t } from '../i18n/i18n.svelte'
  import Banner from './Banner.svelte'
  import Icon from './Icon.svelte'

  let { libraryId, libraryName, onClose, onApplied }: {
    libraryId: number
    libraryName: string
    onClose: () => void
    onApplied: (quality: number) => void
  } = $props()

  let sources = $state<CalibrationSource[]>([])
  let selectedSource = $state<number | null>(null)
  let session = $state<CalibrationSession | null>(null)
  let loading = $state(true)
  let busy = $state(false)
  let error = $state<string | null>(null)
  let timer: ReturnType<typeof setTimeout> | null = null
  let activeSlot = $state<'A' | 'B' | 'X'>('A')
  let players = $state<Partial<Record<'A' | 'B' | 'X', HTMLMediaElement>>>({})
  let playbackError = $state(false)
  let playbackPosition = $state(0)
  let playing = $state(false)
  let switching = $state(false)
  let switchVersion = 0
  let hdrDisplaySupported = $state(false)
  let hdrViewingConfirmed = $state(false)
  let imageZoom = $state(1)
  let imagePanX = $state(0)
  let imagePanY = $state(0)
  let imageDrag = $state<{ x: number, y: number, panX: number, panY: number } | null>(null)
  let minimized = $state(false)
  let closed = false
  let dialog = $state<HTMLDivElement | null>(null)
  const returnFocus = typeof document === 'undefined' ? null : document.activeElement as HTMLElement | null
  let previousOverflow = ''
  const selected = $derived(sources.find((source) => source.mediaFileId === selectedSource) ?? null)
  const hdrReady = $derived(!selected?.isHdr || hdrDisplaySupported && hdrViewingConfirmed)
  const minimizedStatus = $derived(
    error
      ? i18n.m.shared.status_error
      : loading
        ? i18n.m.common.loading_short
        : busy && !session
          ? i18n.m.calibration.starting
          : !session
            ? selected?.mediaKind === 'Audio'
              ? i18n.m.calibration.choose_audio
              : selected?.mediaKind === 'Image'
                ? i18n.m.calibration.choose_image
                : i18n.m.calibration.choose_source
            : session.status === 'Preparing'
              ? session.preparationState === 'Waiting'
                ? i18n.m.calibration.waiting
                : `${i18n.m.calibration.preparing} · ${Math.round(session.preparationProgress * 100)}%`
              : session.status === 'Failed'
                ? i18n.m.calibration.failed
                : session.status === 'Screening'
                  ? i18n.m.calibration.screening
                  : session.status === 'Confirming'
                    ? i18n.m.calibration.confirming
                    : session.status === 'Complete'
                      ? i18n.m.calibration.complete
                      : session.status === 'Applied'
                        ? i18n.m.calibration.applied
                        : i18n.m.calibration.result_title,
  )

  loadSources()
  onMount(() => {
    previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    dialog?.focus()
    hdrDisplaySupported = window.matchMedia('(video-dynamic-range: high)').matches
      || window.matchMedia('(dynamic-range: high)').matches
  })

  async function loadSources() {
    try {
      sources = await api.calibrationSources(libraryId)
      selectedSource = sources[0]?.mediaFileId ?? null
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
      schedulePoll()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.calibration.start_error
    } finally {
      busy = false
    }
  }

  function schedulePoll() {
    if (closed || !session || session.status !== 'Preparing') return
    if (timer) clearTimeout(timer)
    timer = setTimeout(poll, 1500)
  }

  async function poll() {
    if (closed || !session) return
    try {
      session = await api.calibration(session.id)
      if (session.status === 'Preparing') schedulePoll()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.calibration.load_error
    }
  }

  function slot(name: 'A' | 'B' | 'X'): CalibrationSlot | null {
    if (!session?.trial) return null
    return name === 'A' ? session.trial.a : name === 'B' ? session.trial.b : session.trial.x
  }

  function sourceLabel(source: CalibrationSource): string {
    if (source.mediaKind === 'Audio') return `${source.relativePath} · ${formatDuration(source.durationSeconds)}`
    const resolution = source.width && source.height ? ` · ${source.width}×${source.height}` : ''
    if (source.mediaKind === 'Image') return `${source.relativePath}${resolution}`
    const range = source.isHdr ? ' · HDR' : ' · SDR'
    return `${source.relativePath}${resolution}${range} · ${formatDuration(source.durationSeconds)}`
  }

  function preparePlayer(name: 'A' | 'B' | 'X', event: Event) {
    const player = event.currentTarget as HTMLMediaElement
    const source = slot(name)
    if (source && Number.isFinite(player.duration)) {
      player.currentTime = source.startSeconds + playbackPosition
      player.volume = Math.min(1, Math.pow(10, source.gainDb / 20))
    }
  }

  function stopAtSampleEnd(name: 'A' | 'B' | 'X', event: Event) {
    const player = event.currentTarget as HTMLMediaElement
    const source = slot(name)
    if (source && name === activeSlot) {
      playbackPosition = Math.max(0, Math.min(
        session?.trial?.durationSeconds ?? 0,
        player.currentTime - source.startSeconds,
      ))
    }
    if (source && session?.trial && player.currentTime >= source.startSeconds + session.trial.durationSeconds) {
      player.pause()
      player.currentTime = source.startSeconds
      if (name === activeSlot) {
        playbackPosition = 0
        playing = false
      }
    }
  }

  function togglePlayback() {
    const player = players[activeSlot]
    const source = slot(activeSlot)
    if (!player || !source || !session?.trial) return
    if (!player.paused) {
      player.pause()
      playing = false
      return
    }
    if (playbackPosition >= session.trial.durationSeconds - 0.05) {
      playbackPosition = 0
      player.currentTime = source.startSeconds
    }
    void player.play().catch(() => {
      playing = false
      playbackError = true
    })
  }

  function seekPlayback(event: Event) {
    const position = Number((event.currentTarget as HTMLInputElement).value)
    const player = players[activeSlot]
    const source = slot(activeSlot)
    playbackPosition = position
    if (player && source) player.currentTime = source.startSeconds + position
  }

  function sampleTime(seconds: number): string {
    const wholeSeconds = Math.max(0, Math.floor(seconds))
    return `${Math.floor(wholeSeconds / 60)}:${String(wholeSeconds % 60).padStart(2, '0')}`
  }

  function seekToMatchingFrame(player: HTMLMediaElement, targetSeconds: number): Promise<void> {
    if (Math.abs(player.currentTime - targetSeconds) <= 0.02) return Promise.resolve()
    return new Promise((resolve) => {
      let timeout: ReturnType<typeof setTimeout> | null = null
      const finish = () => {
        player.removeEventListener('seeked', finish)
        if (timeout) clearTimeout(timeout)
        resolve()
      }
      player.addEventListener('seeked', finish)
      player.currentTime = targetSeconds
      if (!player.seeking && Math.abs(player.currentTime - targetSeconds) <= 0.05) {
        queueMicrotask(finish)
      } else {
        timeout = setTimeout(finish, 2_000)
      }
    })
  }

  async function switchTo(next: 'A' | 'B' | 'X') {
    if (session?.mediaKind === 'Image') {
      activeSlot = next
      return
    }
    if (next === activeSlot) {
      if (switching) {
        switchVersion++
        switching = false
      }
      return
    }
    const version = ++switchVersion
    const currentPlayer = players[activeSlot]
    const currentSource = slot(activeSlot)
    const nextPlayer = players[next]
    const nextSource = slot(next)
    const wasPlaying = currentPlayer ? !currentPlayer.paused : false
    const relativeTime = currentPlayer && currentSource
      ? Math.max(0, Math.min(
          session?.trial?.durationSeconds ?? 0,
          currentPlayer.currentTime - currentSource.startSeconds,
        ))
      : playbackPosition
    for (const player of Object.values(players)) player?.pause()
    playbackPosition = relativeTime
    playing = false
    if (nextPlayer && nextSource) {
      switching = true
      await seekToMatchingFrame(nextPlayer, nextSource.startSeconds + relativeTime)
      if (version !== switchVersion) return
      activeSlot = next
      switching = false
      if (wasPlaying) {
        await nextPlayer.play().catch(() => {
          playing = false
          playbackError = true
        })
      }
    } else {
      activeSlot = next
    }
  }

  function setImageZoom(next: number) {
    imageZoom = Math.max(1, Math.min(4, next))
    if (imageZoom === 1) {
      imagePanX = 0
      imagePanY = 0
    }
  }

  function startImagePan(event: PointerEvent) {
    if (imageZoom === 1) return
    const target = event.currentTarget as HTMLElement
    target.setPointerCapture(event.pointerId)
    imageDrag = { x: event.clientX, y: event.clientY, panX: imagePanX, panY: imagePanY }
  }

  function moveImagePan(event: PointerEvent) {
    if (!imageDrag) return
    imagePanX = imageDrag.panX + event.clientX - imageDrag.x
    imagePanY = imageDrag.panY + event.clientY - imageDrag.y
  }

  function endImagePan() {
    imageDrag = null
  }

  async function answer(choice: 'A' | 'B') {
    if (!session?.trial || busy) return
    busy = true
    error = null
    switchVersion++
    switching = false
    for (const player of Object.values(players)) player?.pause()
    try {
      session = await api.answerCalibration(session.id, session.trial.id, choice)
      activeSlot = 'A'
      players = {}
      playbackPosition = 0
      playing = false
      imageZoom = 1
      imagePanX = 0
      imagePanY = 0
      playbackError = false
      if (session.status === 'Complete') {
        try {
          session = await api.revealCalibration(session.id)
        } catch (err) {
          error = err instanceof Error ? err.message : i18n.m.calibration.reveal_error
        }
      }
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.calibration.answer_error
    } finally {
      busy = false
    }
  }

  async function reveal() {
    if (!session || busy) return
    busy = true
    try {
      session = await api.revealCalibration(session.id)
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.calibration.reveal_error
    } finally {
      busy = false
    }
  }

  async function apply() {
    if (!session || busy) return
    busy = true
    try {
      session = await api.applyCalibration(session.id)
      if (session.result?.recommendedQuality != null) onApplied(session.result.recommendedQuality)
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.calibration.apply_error
    } finally {
      busy = false
    }
  }

  function resultOutcome(outcome: string): string {
    return outcome === 'NoReliableDifference'
      ? i18n.m.calibration.no_difference_result
      : i18n.m.calibration.no_transparent_result
  }

  function minimise() {
    if (minimized) return
    const player = players[activeSlot]
    const source = slot(activeSlot)
    if (player && source && session?.trial) {
      playbackPosition = Math.max(0, Math.min(
        session.trial.durationSeconds,
        player.currentTime - source.startSeconds,
      ))
    }
    for (const candidate of Object.values(players)) candidate?.pause()
    switchVersion++
    switching = false
    playing = false
    minimized = true
    document.body.style.overflow = previousOverflow
  }

  function restore() {
    if (!minimized) return
    minimized = false
    document.body.style.overflow = 'hidden'
    void tick().then(() => dialog?.focus())
  }

  function handleKeydown(event: KeyboardEvent) {
    if (event.key === 'Escape') {
      if (!minimized) {
        event.preventDefault()
        minimise()
      }
      return
    }
    if (minimized) return
    if (event.key === 'Tab') {
      if (!dialog) return
      const focusable = [...dialog.querySelectorAll<HTMLElement>(
        'button:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])',
      )].filter((element) => element.offsetParent !== null)
      if (focusable.length === 0) return
      const first = focusable[0]
      const last = focusable[focusable.length - 1]
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault()
        last.focus()
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault()
        first.focus()
      }
      return
    }
    if (!session?.trial || event.metaKey || event.ctrlKey || event.altKey) return
    const key = event.key.toUpperCase()
    if (key === 'A' || key === 'B' || key === 'X') {
      event.preventDefault()
      switchTo(key)
    }
  }

  async function close() {
    if (closed) return
    closed = true
    if (timer) clearTimeout(timer)
    if (session) await api.deleteCalibration(session.id).catch(() => {})
    onClose()
  }

  onDestroy(() => {
    document.body.style.overflow = previousOverflow
    returnFocus?.focus()
    const shouldDelete = !closed
    closed = true
    if (timer) clearTimeout(timer)
    if (shouldDelete && session) void api.deleteCalibration(session.id).catch(() => {})
  })
</script>

<svelte:window onkeydown={handleKeydown} />

{#if minimized}
  <section
    class="fixed bottom-4 left-4 right-4 z-50 rounded-lg border border-slate-200 bg-white p-3 shadow-xl sm:left-auto sm:w-80 dark:border-slate-700 dark:bg-slate-900"
    aria-label={i18n.m.calibration.title}
  >
    <div class="flex items-center gap-2">
      <div class="min-w-0 flex-1">
        <p class="truncate text-xs font-semibold text-slate-700 dark:text-slate-200" title={libraryName}>{libraryName}</p>
        <p class="truncate text-[11px] text-slate-500 dark:text-slate-400" aria-live="polite">{minimizedStatus}</p>
      </div>
      <button class="btn btn-ghost min-h-11 min-w-11 flex-shrink-0 p-2" type="button" onclick={restore} title={i18n.m.shared.expand} aria-label={i18n.m.shared.expand}>
        <Icon name="chevron" class="h-4 w-4 rotate-180" />
      </button>
      <button class="btn btn-ghost min-h-11 min-w-11 flex-shrink-0 p-2 text-red-600 dark:text-red-400" type="button" onclick={close} title={i18n.m.common.close} aria-label={i18n.m.common.close}>
        <Icon name="x" class="h-4 w-4" />
      </button>
    </div>
    {#if session?.status === 'Preparing'}
      <div
        class="progress-track mt-2"
        role="progressbar"
        aria-label={i18n.m.calibration.preparing}
        aria-valuemin={session.preparationState === 'Waiting' ? undefined : 0}
        aria-valuemax={session.preparationState === 'Waiting' ? undefined : 100}
        aria-valuenow={session.preparationState === 'Waiting' ? undefined : Math.round(session.preparationProgress * 100)}
      >
        {#if session.preparationState === 'Waiting'}
          <div class="progress-indeterminate"></div>
        {:else}
          <div class="progress-fill" style={`width: ${Math.round(session.preparationProgress * 100)}%`}></div>
        {/if}
      </div>
    {/if}
    {#if error}<p class="mt-2 text-[11px] text-red-600 dark:text-red-400">{error}</p>{/if}
  </section>
{:else}
<div class="fixed inset-0 z-50 flex items-center justify-center bg-slate-950/70 p-2 backdrop-blur-sm sm:p-4" role="presentation">
  <div
    bind:this={dialog}
    class="card flex max-h-[96dvh] w-full max-w-5xl flex-col overflow-hidden"
    role="dialog"
    tabindex="-1"
    aria-modal="true"
    aria-labelledby="calibration-title"
  >
    <header class="flex items-start justify-between gap-4 border-b border-slate-200 p-4 dark:border-slate-700 sm:p-5">
      <div>
        <p class="text-xs font-semibold uppercase tracking-wide text-cyan-600 dark:text-cyan-400">{i18n.m.calibration.eyebrow}</p>
        <h2 id="calibration-title" class="text-lg font-semibold text-slate-900 dark:text-slate-100">{i18n.m.calibration.title}</h2>
        <p class="mt-1 text-sm text-slate-500 dark:text-slate-400">{libraryName}</p>
      </div>
      <div class="flex flex-shrink-0 items-center gap-1">
        <button class="btn btn-ghost min-h-11 min-w-11 p-2" type="button" onclick={minimise} title={i18n.m.shared.minimise} aria-label={i18n.m.shared.minimise}>
          <Icon name="minus" class="h-5 w-5" />
        </button>
        <button class="btn btn-ghost min-h-11 min-w-11 p-2" type="button" onclick={close} title={i18n.m.common.close} aria-label={i18n.m.common.close}>
          <Icon name="x" class="h-5 w-5" />
        </button>
      </div>
    </header>

    <div class="min-h-0 flex-1 overflow-y-auto p-4 sm:p-5">
      {#if error}<Banner kind="error" class="mb-4">{error}</Banner>{/if}

      {#if loading}
        <div class="py-12 text-center text-sm text-slate-500">{i18n.m.common.loading_short}</div>
      {:else if !session}
        <div class="mx-auto max-w-2xl">
          <h3 class="font-semibold text-slate-900 dark:text-slate-100">{selected?.mediaKind === 'Audio' ? i18n.m.calibration.choose_audio : selected?.mediaKind === 'Image' ? i18n.m.calibration.choose_image : i18n.m.calibration.choose_source}</h3>
          <p class="mt-1 text-sm leading-relaxed text-slate-500 dark:text-slate-400">{i18n.m.calibration.intro}</p>
          {#if sources.length === 0}
            <div class="mt-5 rounded-lg border border-dashed border-slate-300 p-6 text-center dark:border-slate-700">
              <p class="font-medium">{i18n.m.calibration.no_sources}</p>
              <p class="mt-1 text-sm text-slate-500">{i18n.m.calibration.no_sources_hint}</p>
            </div>
          {:else}
            <label class="label mt-5" for="calibration-source">{selected?.mediaKind === 'Audio' ? i18n.m.calibration.source_audio : selected?.mediaKind === 'Image' ? i18n.m.calibration.source_image : i18n.m.calibration.source_label}</label>
            <select id="calibration-source" class="input" bind:value={selectedSource}>
              {#each sources as source}
                <option value={source.mediaFileId}>{sourceLabel(source)}</option>
              {/each}
            </select>
            {#if selected?.isHdr}
              <div class="mt-4 rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-950 dark:border-amber-900 dark:bg-amber-950/30 dark:text-amber-100">
                <p class="font-medium">{i18n.m.calibration.hdr_title}</p>
                {#if hdrDisplaySupported}
                  <p class="mt-1 text-xs leading-relaxed">{i18n.m.calibration.hdr_body}</p>
                  <label class="mt-3 flex min-h-11 cursor-pointer items-start gap-3">
                    <input class="checkbox mt-0.5" type="checkbox" bind:checked={hdrViewingConfirmed} />
                    <span class="text-sm">{i18n.m.calibration.hdr_confirm}</span>
                  </label>
                {:else}
                  <p class="mt-1 text-xs leading-relaxed">{i18n.m.calibration.hdr_unsupported}</p>
                {/if}
              </div>
            {/if}
            <div class="mt-4 rounded-lg border border-cyan-200 bg-cyan-50 p-3 text-sm text-cyan-950 dark:border-cyan-900 dark:bg-cyan-950/30 dark:text-cyan-100">
              <p class="font-medium">{i18n.m.calibration.safe_title}</p>
              <p class="mt-1 text-xs leading-relaxed">{i18n.m.calibration.safe_body}</p>
            </div>
            <button class="btn btn-primary mt-5 min-h-11" type="button" disabled={busy || !hdrReady} onclick={start}>
              {busy ? i18n.m.calibration.starting : i18n.m.calibration.start}
            </button>
          {/if}
        </div>
      {:else if session.status === 'Preparing'}
        <div class="mx-auto max-w-xl py-10 text-center" aria-live="polite">
          <h3 class="font-semibold text-slate-900 dark:text-slate-100">{i18n.m.calibration.preparing}</h3>
          <p class="mt-2 text-sm text-slate-500 dark:text-slate-400">{session.preparationState === 'Waiting' ? i18n.m.calibration.waiting_hint : session.mediaKind === 'Audio' ? i18n.m.calibration.preparing_audio_hint : session.mediaKind === 'Image' ? i18n.m.calibration.preparing_image_hint : i18n.m.calibration.preparing_hint}</p>
          <div
            class="progress-track mx-auto mt-6 max-w-md"
            role="progressbar"
            aria-label={i18n.m.calibration.preparing}
            aria-valuemin={session.preparationState === 'Waiting' ? undefined : 0}
            aria-valuemax={session.preparationState === 'Waiting' ? undefined : 100}
            aria-valuenow={session.preparationState === 'Waiting' ? undefined : Math.round(session.preparationProgress * 100)}
          >
            {#if session.preparationState === 'Waiting'}
              <div class="progress-indeterminate"></div>
            {:else}
              <div class="progress-fill" style={`width: ${Math.round(session.preparationProgress * 100)}%`}></div>
            {/if}
          </div>
          <p class="mt-2 text-sm tabular-nums text-slate-500">{session.preparationState === 'Waiting' ? i18n.m.calibration.waiting : `${Math.round(session.preparationProgress * 100)}%`}</p>
        </div>
      {:else if session.status === 'Failed'}
        <div class="mx-auto max-w-xl py-10 text-center">
          <h3 class="font-semibold text-red-700 dark:text-red-300">{i18n.m.calibration.failed}</h3>
          <p class="mt-2 text-sm text-slate-500">{session.error}</p>
        </div>
      {:else if session.trial}
        {@const trial = session.trial}
        {#key trial.id}
        <div class="mx-auto max-w-3xl">
          <div class="mb-3 flex flex-wrap items-center justify-between gap-2 text-sm">
            <span class="font-medium">{trial.phase === 'Screening' ? i18n.m.calibration.screening : i18n.m.calibration.confirming}</span>
            <span class="text-slate-500">{t(i18n.m.calibration.trial_progress, { number: trial.number, maximum: trial.maximumNumber })} · {t(i18n.m.calibration.sample_progress, { current: trial.sampleNumber, total: trial.sampleCount })}</span>
          </div>
          <div
            role="group"
            aria-label={session.mediaKind === 'Image' ? i18n.m.calibration.image_viewport : i18n.m.calibration.switch_label}
            class="relative {session.mediaKind === 'Audio' ? 'min-h-36' : 'aspect-video'} overflow-hidden rounded-lg bg-black"
            class:cursor-grab={session.mediaKind === 'Image' && imageZoom > 1 && !imageDrag}
            class:cursor-grabbing={session.mediaKind === 'Image' && imageDrag !== null}
            style:touch-action={session.mediaKind === 'Image' ? 'none' : undefined}
            onpointerdown={session.mediaKind === 'Image' ? startImagePan : undefined}
            onpointermove={session.mediaKind === 'Image' ? moveImagePan : undefined}
            onpointerup={session.mediaKind === 'Image' ? endImagePan : undefined}
            onpointercancel={session.mediaKind === 'Image' ? endImagePan : undefined}
          >
            {#if session.mediaKind === 'Image'}
              {@const imageSource = slot(activeSlot)}
              {#each ['A', 'B', 'X'] as name}
                {@const preload = slot(name as 'A' | 'B' | 'X')}
                {#if preload}<img class="hidden" src={preload.url} alt="" onerror={() => { playbackError = true }} />{/if}
              {/each}
              {#if imageSource}
                <img
                  class="h-full w-full select-none object-contain will-change-transform"
                  src={imageSource.url}
                  alt={t(i18n.m.calibration.image_alt, { slot: activeSlot })}
                  draggable="false"
                  style={`transform: translate(${imagePanX}px, ${imagePanY}px) scale(${imageZoom})`}
                  onerror={() => { playbackError = true }}
                />
              {/if}
            {:else}
              {#each ['A', 'B', 'X'] as name}
                {@const typedName = name as 'A' | 'B' | 'X'}
                {@const source = slot(typedName)}
                {#if source && session.mediaKind === 'Audio'}
                  <audio
                    bind:this={players[typedName]}
                    class="absolute inset-x-6 top-1/2 w-[calc(100%-3rem)] -translate-y-1/2 {activeSlot === typedName ? 'block' : 'hidden'}"
                    src={source.url}
                    preload="auto"
                    onloadedmetadata={(event) => preparePlayer(typedName, event)}
                    ontimeupdate={(event) => stopAtSampleEnd(typedName, event)}
                    onplay={() => { if (activeSlot === typedName) playing = true }}
                    onpause={() => { if (activeSlot === typedName) playing = false }}
                    onerror={() => { playbackError = true }}
                  ></audio>
                {:else if source}
                  <video
                  bind:this={players[typedName]}
                  class="h-full w-full object-contain {activeSlot === typedName ? 'block' : 'hidden'}"
                  src={source.url}
                  muted
                  playsinline
                  preload="auto"
                  onloadedmetadata={(event) => preparePlayer(typedName, event)}
                  ontimeupdate={(event) => stopAtSampleEnd(typedName, event)}
                  onplay={() => { if (activeSlot === typedName) playing = true }}
                  onpause={() => { if (activeSlot === typedName) playing = false }}
                  onerror={() => { playbackError = true }}
                  ><track kind="captions" /></video>
                {/if}
              {/each}
            {/if}
            <span class="absolute left-3 top-3 rounded bg-black/75 px-3 py-1 text-sm font-bold text-white">{activeSlot}</span>
          </div>
          {#if session.mediaKind !== 'Image'}
            <div class="mt-3 flex items-center gap-2 sm:gap-3">
              <button
                class="btn min-h-11 min-w-11 p-2"
                type="button"
                disabled={playbackError || switching || !players[activeSlot]}
                aria-label={playing ? i18n.m.calibration.pause_sample : i18n.m.calibration.play_sample}
                aria-pressed={playing}
                onclick={togglePlayback}
              ><Icon name={playing ? 'pause' : 'play'} class="h-5 w-5" /></button>
              <input
                class="min-h-11 min-w-0 flex-1 cursor-pointer accent-cyan-600"
                type="range"
                min="0"
                max={trial.durationSeconds}
                step="0.01"
                value={playbackPosition}
                disabled={playbackError || switching || !players[activeSlot]}
                aria-label={i18n.m.calibration.sample_position}
                oninput={seekPlayback}
              />
              <span class="min-w-20 text-right text-xs tabular-nums text-slate-500 dark:text-slate-400">
                {sampleTime(playbackPosition)} / {sampleTime(trial.durationSeconds)}
              </span>
            </div>
          {/if}
          {#if session.mediaKind === 'Image'}
            <div class="mt-3 flex flex-wrap justify-center gap-2" aria-label={i18n.m.calibration.image_zoom_controls}>
              <button class="btn min-h-11 min-w-11" type="button" disabled={imageZoom <= 1} onclick={() => setImageZoom(imageZoom - 0.5)} aria-label={i18n.m.calibration.zoom_out}>−</button>
              <span class="flex min-h-11 min-w-20 items-center justify-center text-sm tabular-nums text-slate-500">{Math.round(imageZoom * 100)}%</span>
              <button class="btn min-h-11 min-w-11" type="button" disabled={imageZoom >= 4} onclick={() => setImageZoom(imageZoom + 0.5)} aria-label={i18n.m.calibration.zoom_in}>+</button>
              <button class="btn min-h-11" type="button" disabled={imageZoom === 1 && imagePanX === 0 && imagePanY === 0} onclick={() => setImageZoom(1)}>{i18n.m.calibration.zoom_reset}</button>
            </div>
          {/if}
          {#if playbackError}
            <Banner kind="error" class="mt-3">{i18n.m.calibration.playback_error}</Banner>
          {/if}
          <div class="mt-4 grid grid-cols-3 gap-2" aria-label={i18n.m.calibration.switch_label}>
            {#each ['A', 'B', 'X'] as name}
              <button
                class="btn min-h-11 {activeSlot === name ? 'border-cyan-500 bg-cyan-50 text-cyan-800 dark:bg-cyan-950 dark:text-cyan-200' : ''}"
                type="button"
                onclick={() => switchTo(name as 'A' | 'B' | 'X')}
                aria-pressed={activeSlot === name}
              >{name}</button>
            {/each}
          </div>
          <p class="mt-2 text-center text-xs text-slate-400">{i18n.m.calibration.keyboard_hint}</p>
          <div class="mt-6 border-t border-slate-200 pt-5 text-center dark:border-slate-700">
            <p class="font-semibold text-slate-900 dark:text-slate-100">{i18n.m.calibration.question}</p>
            <div class="mt-3 flex flex-col justify-center gap-2 sm:flex-row">
              <button class="btn btn-primary min-h-11 min-w-40" type="button" disabled={busy || playbackError || switching} onclick={() => answer('A')}>{i18n.m.calibration.matches_a}</button>
              <button class="btn btn-primary min-h-11 min-w-40" type="button" disabled={busy || playbackError || switching} onclick={() => answer('B')}>{i18n.m.calibration.matches_b}</button>
            </div>
          </div>
        </div>
        {/key}
      {:else if session.status === 'Complete'}
        <div class="mx-auto max-w-xl py-10 text-center">
          <h3 class="text-lg font-semibold text-slate-900 dark:text-slate-100">{i18n.m.calibration.complete}</h3>
          <p class="mt-2 text-sm text-slate-500">{i18n.m.calibration.complete_hint}</p>
          <button class="btn btn-primary mt-5 min-h-11" type="button" disabled={busy} onclick={reveal}>{i18n.m.calibration.reveal}</button>
        </div>
      {:else if session.result}
        <div class="mx-auto max-w-2xl">
          <h3 class="text-lg font-semibold text-slate-900 dark:text-slate-100">{i18n.m.calibration.result_title}</h3>
          <p class="mt-2 text-sm leading-relaxed text-slate-600 dark:text-slate-300">{resultOutcome(session.result.outcome)}</p>
          <dl class="mt-5 grid gap-3 rounded-lg border border-slate-200 p-4 text-sm sm:grid-cols-2 dark:border-slate-700">
            <div><dt class="text-slate-500">{i18n.m.calibration.answers}</dt><dd class="mt-1 font-semibold">{session.result.correctAnswers}/{session.result.totalAnswers}</dd></div>
            <div><dt class="text-slate-500">{i18n.m.calibration.recommendation}</dt><dd class="mt-1 font-semibold">{session.result.recommendedQuality != null ? (session.mediaKind === 'Audio' ? `${session.result.recommendedQuality} kbps` : session.mediaKind === 'Image' ? `${i18n.m.calibration.image_quality} ${session.result.recommendedQuality}` : `CRF/CQ ${session.result.recommendedQuality}`) : i18n.m.calibration.keep_current}</dd></div>
            {#if session.result.encoder}<div><dt class="text-slate-500">{i18n.m.calibration.encoder}</dt><dd class="mt-1 font-semibold">{session.result.encoder} · {session.result.qualityMode} {session.result.effectiveQuality}</dd></div>{/if}
            {#if session.result.estimatedSavingPercent != null}<div><dt class="text-slate-500">{i18n.m.calibration.estimated_saving}</dt><dd class="mt-1 font-semibold">≈ {session.result.estimatedSavingPercent}%</dd></div>{/if}
          </dl>
          <p class="mt-3 text-xs leading-relaxed text-slate-500">{session.mediaKind === 'Audio' ? i18n.m.calibration.result_caveat_audio : session.mediaKind === 'Image' ? i18n.m.calibration.result_caveat_image : i18n.m.calibration.result_caveat}</p>
          <div class="mt-5 flex flex-wrap gap-2">
            {#if session.result.recommendedQuality != null && !session.result.applied}
              <button class="btn btn-primary min-h-11" type="button" disabled={busy} onclick={apply}>{i18n.m.calibration.apply}</button>
            {:else if session.result.applied}
              <span class="badge bg-emerald-100 px-3 py-2 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300">{i18n.m.calibration.applied}</span>
            {/if}
            <button class="btn min-h-11" type="button" onclick={close}>{i18n.m.common.close}</button>
          </div>
        </div>
      {/if}
    </div>
  </div>
</div>
{/if}
