<script lang="ts">
  // "Optimise this file…": what would happen to one file, before anything is queued. The plan is
  // read-only and comes from the server, which works it out with the same rules the queue uses.
  // Settings are the library's; changing them for one file is a later slice.
  import { onDestroy } from 'svelte'
  import { api, type Library, type LibraryOptions, type MediaPlan, type MediaPlanTrack, type Settings } from '../api'
  import { filesChoice } from '../files-choice'
  import { containerLabel, formatDuration, formatSize } from '../format'
  import { i18n, plural, t } from '../i18n/i18n.svelte'
  import { codecName, summariseLibrary, vmafLevelName } from '../library-summary'
  import { router } from '../stores/ui.svelte'
  import Banner from '../components/Banner.svelte'
  import Icon from '../components/Icon.svelte'
  import PreviewCompare from '../components/PreviewCompare.svelte'
  import Thumbnail from '../components/Thumbnail.svelte'

  const mediaFileId = $derived(Number(router.path.match(/^\/inventory\/optimise\/(\d+)/)?.[1] ?? 0))

  let plan = $state<MediaPlan | null>(null)
  let library = $state<Library | null>(null)
  let options = $state<LibraryOptions | null>(null)
  let settings = $state<Settings | null>(null)
  let error = $state<string | null>(null)
  let loading = $state(true)
  let queueing = $state(false)
  let queued = $state<'added' | 'already' | null>(null)
  let queueError = $state<string | null>(null)
  let previewing = $state(false)
  let disposed = false
  onDestroy(() => { disposed = true })

  $effect(() => { void load(mediaFileId) })

  async function load(id: number) {
    loading = true
    error = null
    plan = null
    queued = null
    queueing = false
    queueError = null
    try {
      const [loadedPlan, libraries, loadedOptions, loadedSettings] = await Promise.all([
        api.mediaPlan(id),
        api.libraries(),
        // The preset's values and the two server-wide settings only add detail; the plan stands without them.
        api.libraryOptions().catch(() => null),
        api.settings().catch(() => null),
      ])
      if (disposed || id !== mediaFileId) return
      plan = loadedPlan
      library = libraries.find((candidate) => candidate.id === loadedPlan.libraryId) ?? null
      options = loadedOptions
      settings = loadedSettings
    } catch (err) {
      if (!disposed) error = err instanceof Error ? err.message : i18n.m.onePlan.error_load
    } finally {
      if (!disposed) loading = false
    }
  }

  async function queue() {
    if (!plan || queueing) return
    const id = plan.mediaFileId
    // An answer that arrives after another file was opened belongs to the file it was asked for.
    const stillShown = () => !disposed && plan?.mediaFileId === id
    queueing = true
    queueError = null
    try {
      const { alreadyQueued } = await api.enqueueFile(id)
      if (stillShown()) queued = alreadyQueued ? 'already' : 'added'
    } catch (err) {
      if (stillShown()) queueError = err instanceof Error ? err.message : i18n.m.inventory.error_queue
    } finally {
      if (stillShown()) queueing = false
    }
  }

  const fileName = $derived((plan?.source.relativePath ?? '').split(/[\\/]/).pop() ?? '')
  const title = $derived(fileName.replace(/\.[^.]+$/, '').replace(/[._]+/g, ' '))
  const libraryName = $derived(library?.name ?? i18n.m.inventory.library_label)
  const choice = $derived(library ? filesChoice(library) : 'ask')
  const preset = $derived(options?.ruleProfileSpecs.find((spec) => spec.profile === library?.ruleProfile) ?? null)
  // The checks sentence is the library's own, from the same summary its overview shows.
  const checksSentence = $derived(library
    ? summariseLibrary(library, {
      m: i18n.m, locale: i18n.locale, preset,
      retentionDays: settings?.replacementQuarantineRetentionDays ?? null,
      dryRun: settings?.dryRunMode ?? false,
    }).find((line) => line.topic === 'checks')?.text ?? null
    : null)

  const pictureChanges = $derived.by(() => {
    const picture = plan?.picture
    if (!picture) return []
    const changes: string[] = []
    if (picture.width && picture.height) changes.push(t(i18n.m.onePlan.picture_shrunk, { width: picture.width, height: picture.height }))
    if (picture.frameRateCap) changes.push(t(i18n.m.onePlan.picture_fps, { fps: picture.frameRateCap }))
    if (picture.tonemapToSdr) changes.push(i18n.m.librarySummary.video_tonemap)
    if (picture.deinterlace) changes.push(i18n.m.onePlan.picture_deinterlaced)
    if (picture.cropBlackBars) changes.push(i18n.m.onePlan.picture_cropped)
    if (picture.noiseReduction !== 'Off') {
      changes.push(t(i18n.m.librarySummary.video_noise, { level: picture.noiseReduction === 'Strong' ? i18n.m.libraries.noise_strong : i18n.m.libraries.noise_light }))
    }
    if (library?.superResolutionEnabled) changes.push(i18n.m.librarySummary.video_upscale)
    return changes
  })

  const qualitySentence = $derived.by(() => {
    const picture = plan?.picture
    if (!picture || !library) return null
    const crf = picture.crf ?? ''
    const level = vmafLevelName(library, i18n.m)
    const score = library.minVmafHarmonicMean ?? ''
    const target = level ? t(i18n.m.librarySummary.target_named, { score, level }) : String(score)
    if (library.vmafQualityGateEnabled !== true) return t(i18n.m.onePlan.quality_fixed_unchecked, { crf })
    return library.videoQualityStrategy === 'AdaptiveVmaf'
      ? t(i18n.m.onePlan.quality_target, { target })
      : t(i18n.m.onePlan.quality_fixed_checked, { crf, target })
  })

  function trackNames(tracks: MediaPlanTrack[]): string {
    return new Intl.ListFormat(i18n.locale === 'en' ? 'en-GB' : i18n.locale, { style: 'long', type: 'conjunction' })
      .format(tracks.map((track) => (track.language ? track.language.toUpperCase() : i18n.m.inventory.unknown_language)))
  }
  const removed = (tracks: MediaPlanTrack[] | null) => (tracks ?? []).filter((track) => !track.kept)
  const kept = (tracks: MediaPlanTrack[] | null) => (tracks ?? []).filter((track) => track.kept)
</script>

<header class="mb-6">
  <nav class="mb-4 flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-ink-3" aria-label={i18n.m.libraryWorkflow.breadcrumb}>
    <a class="focus-ring inline-flex min-h-11 min-w-11 items-center rounded px-1 hover:text-accent" href="#/inventory">{i18n.m.nav.inventory}</a>
    <span aria-hidden="true">/</span>
    <span aria-current="page" class="break-words text-ink">{title || i18n.m.onePlan.title}</span>
  </nav>
  <h1 class="page-title">{i18n.m.onePlan.title}</h1>
  <p class="page-subtitle">{t(i18n.m.onePlan.subtitle, { library: libraryName })}</p>
</header>

{#if error}
  <Banner kind="error" class="mb-4">{error}<button class="btn ml-3 min-h-11" onclick={() => load(mediaFileId)}>{i18n.m.setup.retry}</button></Banner>
{:else if loading || !plan}
  <div class="card p-8 text-center text-ink-4" role="status">{i18n.m.common.loading_short}</div>
{:else}
  {@const source = plan.source}
  {@const container = containerLabel(source.container, source.relativePath)}
  <div class="space-y-5" data-one-file-plan>
    <section class="card flex flex-wrap items-center gap-4 p-4 sm:p-6" aria-label={title}>
      <Thumbnail mediaFileId={plan.mediaFileId} size="poster" shape={plan.kind === 'Audio' || plan.kind === 'Image' ? 'square' : 'portrait'} />
      <div class="min-w-0 flex-1">
        <p class="text-xs font-semibold text-ink-3">{libraryName}</p>
        <h2 class="mt-1 break-words text-lg font-semibold text-ink">{title}</h2>
        <p class="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-ink-2">
          <span>{formatSize(source.sizeBytes)}</span>
          {#if container}<span>{container}</span>{/if}
          {#if source.durationSeconds !== null}<span>{formatDuration(source.durationSeconds)}</span>{/if}
        </p>
      </div>
    </section>

    <section class="card p-4 sm:p-6" aria-labelledby="plan-verdict-heading" data-plan-verdict={plan.eligible ? 'yes' : 'no'}>
      <div class="flex flex-wrap items-center justify-between gap-2">
        <h2 id="plan-verdict-heading" class="text-base font-semibold text-ink">{i18n.m.onePlan.can_it}</h2>
        <span class="badge {plan.eligible ? 'tone-ok' : 'tone-muted'}">{plan.eligible ? i18n.m.onePlan.yes : i18n.m.onePlan.no}</span>
      </div>
      <p class="mt-2 text-sm leading-relaxed text-ink-2">{plan.reason}</p>
      {#if !plan.eligible && library}
        <p class="mt-3 text-sm leading-relaxed text-ink-3">
          {t(i18n.m.onePlan.skipped_where, { library: libraryName })}
          <a class="focus-ring inline-flex min-h-11 items-center rounded px-1 font-medium text-accent underline" href={`#/libraries/${library.id}/configure/files`}>{libraryName} → {i18n.m.librarySummary.topic_files}</a>
        </p>
      {/if}
    </section>

    <section class="card overflow-hidden" aria-labelledby="plan-heading">
      <header class="px-4 py-4 hairline-b sm:px-6">
        <h2 id="plan-heading" class="text-base font-semibold text-ink">{i18n.m.onePlan.what_title}</h2>
        <p class="mt-0.5 text-sm leading-relaxed text-ink-3">{t(i18n.m.onePlan.what_intro, { library: libraryName })}</p>
      </header>
      <div class="plan-table px-4 sm:px-6" role="table" aria-labelledby="plan-heading">
        <div class="plan-row plan-head" role="row">
          <span role="columnheader"><span class="sr-only">{i18n.m.onePlan.col_part}</span></span>
          <span role="columnheader">{i18n.m.onePlan.col_now}</span>
          <span role="columnheader">{i18n.m.onePlan.col_after}</span>
        </div>

        {#if plan.kind === 'Video'}
          <div class="plan-row" role="row" data-plan-row="video">
            <span role="rowheader">{i18n.m.librarySummary.topic_video}</span>
            <span role="cell" data-label={i18n.m.onePlan.col_now}>
              {codecName(source.videoCodec) || '—'}{#if source.width && source.height}{` · ${source.width} × ${source.height}`}{/if}
              {#if source.isHdr}{' · HDR'}{/if}{#if source.isDolbyVision}{' · Dolby Vision'}{/if}{#if source.interlaced}{` · ${i18n.m.onePlan.interlaced}`}{/if}
            </span>
            <span role="cell" data-label={i18n.m.onePlan.col_after}>
              {#if plan.picture}
                {t(i18n.m.librarySummary.video_encode, { codec: codecName(plan.picture.codec) })}
                {#if qualitySentence}{' '}{qualitySentence}{/if}
                {' '}{pictureChanges.length > 0 ? pictureChanges.join(' ') : i18n.m.librarySummary.video_picture_kept}
                {#if pictureChanges.length > 0}<span class="badge tone-warn mt-2" data-plan-tag>{i18n.m.librarySummary.tag_changes_picture}</span>{/if}
              {:else}
                {i18n.m.onePlan.picture_copied}
              {/if}
            </span>
          </div>
        {/if}

        {#if plan.sound}
          <div class="plan-row" role="row" data-plan-row="audio">
            <span role="rowheader">{i18n.m.libraries.audio}</span>
            <span role="cell" data-label={i18n.m.onePlan.col_now}>
              {source.audioCodecs ?? '—'}{#if source.audioTrackCount !== null}{plural(source.audioTrackCount, i18n.m.inventory.audio_tracks_one, i18n.m.inventory.audio_tracks, source.audioTrackCount.toLocaleString())}{/if}
              {#if plan.audioTracks && plan.audioTracks.length > 0}{' · '}{trackNames(plan.audioTracks)}{/if}
            </span>
            <span role="cell" data-label={i18n.m.onePlan.col_after}>
              {plan.sound.codec
                ? t(i18n.m.onePlan.sound_converted, { codec: codecName(plan.sound.codec), kbps: plan.sound.bitrateKbps ?? '' })
                : i18n.m.onePlan.sound_copied}
              {#if plan.sound.downmixToStereo}{' '}{i18n.m.librarySummary.audio_downmix}{/if}
              {#if plan.kind === 'Video'}
                {#if removed(plan.audioTracks).length > 0}
                  {' '}{t(i18n.m.onePlan.tracks_removed, { tracks: trackNames(removed(plan.audioTracks)) })}
                  <span class="badge tone-warn mt-2" data-plan-tag>{i18n.m.librarySummary.tag_removes_tracks}</span>
                {:else if plan.audioTracks === null && library?.keepAudioLanguages}
                  {' '}{i18n.m.onePlan.tracks_unknown}
                {:else}
                  {' '}{i18n.m.onePlan.tracks_all_kept}
                {/if}
              {/if}
            </span>
          </div>
        {/if}

        {#if plan.kind === 'Video'}
          <div class="plan-row" role="row" data-plan-row="subtitles">
            <span role="rowheader">{i18n.m.inventory.detail_subtitles}</span>
            <span role="cell" data-label={i18n.m.onePlan.col_now}>
              {#if (source.subtitleTrackCount ?? 0) === 0}{i18n.m.onePlan.none}
              {:else}{source.subtitleTrackCount}{#if plan.subtitleTracks && plan.subtitleTracks.length > 0}{' · '}{trackNames(plan.subtitleTracks)}{/if}{/if}
            </span>
            <span role="cell" data-label={i18n.m.onePlan.col_after}>
              {#if (source.subtitleTrackCount ?? 0) === 0}{i18n.m.onePlan.none}
              {:else if removed(plan.subtitleTracks).length > 0}
                {kept(plan.subtitleTracks).length === 0
                  ? i18n.m.onePlan.subtitles_none_left
                  : t(i18n.m.onePlan.tracks_removed, { tracks: trackNames(removed(plan.subtitleTracks)) })}
                <span class="badge tone-warn mt-2" data-plan-tag>{i18n.m.librarySummary.tag_removes_tracks}</span>
              {:else if plan.subtitleTracks === null && library?.keepSubtitleLanguages}{i18n.m.onePlan.tracks_unknown}
              {:else}{i18n.m.onePlan.tracks_all_kept}{/if}
            </span>
          </div>
        {/if}

        <div class="plan-row" role="row" data-plan-row="file">
          <span role="rowheader">{i18n.m.onePlan.row_file}</span>
          <span role="cell" data-label={i18n.m.onePlan.col_now}><span class="break-all font-mono text-xs">{fileName}</span></span>
          <span role="cell" data-label={i18n.m.onePlan.col_after}>
            {#if plan.file.renamed}
              {t(i18n.m.onePlan.file_renamed, { name: plan.file.fileName })}
              <span class="badge tone-warn mt-2" data-plan-tag>{i18n.m.librarySummary.tag_renames}</span>
            {:else}
              {i18n.m.onePlan.file_kept}
            {/if}
            {#if plan.file.mayBecomeMatroska}{' '}{i18n.m.onePlan.file_may_change}{/if}
          </span>
        </div>

        <div class="plan-row" role="row" data-plan-row="size">
          <span role="rowheader">{i18n.m.inventory.detail_size}</span>
          <span role="cell" data-label={i18n.m.onePlan.col_now}>{formatSize(source.sizeBytes)}</span>
          <span role="cell" data-label={i18n.m.onePlan.col_after}>
            {i18n.m.onePlan.size_unknown}
            {#if library?.requireSizeReduction}{' '}{i18n.m.onePlan.size_must_shrink}{/if}
            {' '}{i18n.m.onePlan.size_preview}
          </span>
        </div>

        {#if checksSentence}
          <div class="plan-row" role="row" data-plan-row="checks">
            <span role="rowheader">{i18n.m.librarySummary.topic_checks}</span>
            <span role="cell" data-label={i18n.m.onePlan.col_now}>—</span>
            <span role="cell" data-label={i18n.m.onePlan.col_after}>{checksSentence}</span>
          </div>
        {/if}
      </div>
    </section>

    <section class="card p-4 sm:p-6" aria-labelledby="plan-original-heading" data-plan-original={choice}>
      <h2 id="plan-original-heading" class="text-base font-semibold text-ink">{i18n.m.onePlan.original_title}</h2>
      <p class="mt-2 text-sm leading-relaxed text-ink-2">
        {#if choice === 'elsewhere'}
          {library?.targetFolder ? t(i18n.m.librarySummary.after_elsewhere, { folder: library.targetFolder }) : i18n.m.librarySummary.after_elsewhere_unset}
          {' '}{i18n.m.filesChoice.elsewhere_desc}
        {:else}
          {choice === 'auto' ? i18n.m.librarySummary.after_auto : i18n.m.librarySummary.after_ask}
          {' '}{i18n.m.filesChoice.original_quarantine}
        {/if}
      </p>
      {#if choice !== 'elsewhere' && settings?.dryRunMode}
        <p class="callout tone-warn mt-3 text-sm leading-relaxed" data-plan-dry-run>{i18n.m.onePlan.dry_run}</p>
      {/if}
      {#if library}
        <p class="mt-3 text-sm leading-relaxed text-ink-3">
          {t(i18n.m.onePlan.settings_from, { library: libraryName })}
          <a class="focus-ring inline-flex min-h-11 items-center rounded px-1 font-medium text-accent underline" href={`#/libraries/${library.id}/configure`}>{t(i18n.m.onePlan.open_settings, { library: libraryName })}</a>
        </p>
      {/if}
    </section>

    <section class="card p-4 sm:p-6" aria-label={i18n.m.onePlan.actions}>
      {#if queueError}<p class="callout tone-bad mb-3" role="alert">{queueError}</p>{/if}
      {#if queued}
        <p class="callout tone-ok mb-3" role="status" data-plan-queued>
          {queued === 'already' ? i18n.m.inventory.queue_already : i18n.m.inventory.queue_added}
          <a class="focus-ring inline-flex min-h-11 items-center rounded px-1 font-medium underline" href="#/queue">{i18n.m.onePlan.open_queue}</a>
        </p>
      {/if}
      <div class="flex flex-wrap items-center gap-2">
        <button class="btn btn-primary min-h-11" onclick={queue} disabled={!plan.eligible || queueing || queued !== null} aria-describedby="plan-action-note">
          <Icon name="plus" class="h-4 w-4" />{queueing ? i18n.m.inventory.queueing : i18n.m.onePlan.queue}
        </button>
        <button class="btn min-h-11" onclick={() => (previewing = true)} disabled={!plan.eligible}>
          <Icon name="play" class="h-4 w-4" />{i18n.m.inventory.preview}
        </button>
      </div>
      <p id="plan-action-note" class="mt-3 text-xs leading-relaxed text-ink-3">
        {plan.eligible ? i18n.m.onePlan.queue_note : i18n.m.onePlan.queue_disabled}
      </p>
    </section>
  </div>
{/if}

{#if previewing && plan}
  <PreviewCompare mediaFileId={plan.mediaFileId} mediaKind={plan.kind} relativePath={plan.source.relativePath} onClose={() => (previewing = false)} />
{/if}

<style>
  .plan-row {
    display: grid;
    grid-template-columns: 8rem minmax(0, 1fr) minmax(0, 1.4fr);
    gap: 0.5rem 1.5rem;
    padding-block: 0.875rem;
    border-top: 1px solid var(--divide-soft);
    font-size: 0.875rem;
    line-height: 1.6;
    color: var(--ink-2);
  }
  .plan-row:first-child { border-top: 0; }
  .plan-head {
    font-size: 0.75rem;
    font-weight: 600;
    color: var(--ink-3);
    padding-block: 0.625rem;
  }
  .plan-row [role='rowheader'] {
    font-weight: 600;
    color: var(--ink);
  }
  .plan-row :global(.badge) { display: inline-flex; margin-left: 0.25rem; }
  /* On a phone the three columns become a stack, and each cell says which side it is. */
  @media (max-width: 639px) {
    .plan-head { display: none; }
    .plan-row { grid-template-columns: minmax(0, 1fr); gap: 0.25rem; }
    .plan-row [role='cell']::before {
      content: attr(data-label);
      display: block;
      font-size: 0.75rem;
      font-weight: 600;
      color: var(--ink-3);
    }
  }
</style>
