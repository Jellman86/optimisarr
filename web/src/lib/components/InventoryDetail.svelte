<script lang="ts">
  import type { InventoryRow } from '../api'
  import { formatDuration, formatSize } from '../format'
  import { i18n, t } from '../i18n/i18n.svelte'
  import { modal } from '../modal'
  import Thumbnail from './Thumbnail.svelte'
  import Icon from './Icon.svelte'

  let { row, libraryName, probing, error, onclose, onprobe, onpreview }: {
    row: InventoryRow; libraryName: string; probing: boolean; error: string | null
    onclose: () => void; onprobe: () => void; onpreview: () => void
  } = $props()
  let file = $derived(row.file)
  let title = $derived((file.relativePath.split(/[\\/]/).pop() ?? file.relativePath).replace(/\.[^.]+$/, '').replace(/[._]+/g, ' '))
  let artworkFailed = $state(false)
  let artworkLoaded = $state(false)
  let verdict = $derived(row.eligible === null ? i18n.m.inventory.badge_unprobed : row.eligible ? i18n.m.inventory.badge_eligible : i18n.m.inventory.badge_skipped)
</script>

<dialog class="app-modal inventory-dialog" use:modal={onclose} aria-labelledby="inventory-detail-title">
  <header class="detail-hero">
    {#if !artworkFailed}
      <img class="detail-backdrop" class:artwork-loaded={artworkLoaded} src="/api/media/{file.id}/thumbnail" alt="" loading="lazy" onload={() => (artworkLoaded = true)} onerror={() => (artworkFailed = true)} />
    {/if}
    <div class="detail-wash"></div>
    <div class="detail-poster">
      <Thumbnail mediaFileId={file.id} size="poster" shape={file.mediaKind === 'Audio' || file.mediaKind === 'Image' ? 'square' : 'portrait'} />
    </div>
    <div class="detail-heading">
      <p class="detail-library">{libraryName}{#if file.mediaKind && file.mediaKind !== 'Unknown'} · {file.mediaKind}{/if}</p>
      <h2 id="inventory-detail-title">{title}</h2>
      <div class="detail-meta"><span>{formatSize(file.sizeBytes)}</span>{#if file.container}<span>{file.container}</span>{/if}{#if file.durationSeconds !== null}<span>{formatDuration(file.durationSeconds)}</span>{/if}</div>
    </div>
    <button type="button" class="btn btn-ghost detail-close" onclick={onclose} aria-label={i18n.m.shared.close_detail}><Icon name="x" /></button>
  </header>

  <div class="detail-body">
    <section class="detail-verdict" aria-label={i18n.m.inventory.rule_verdict}>
      <div class="flex flex-wrap items-center justify-between gap-2"><h3>{i18n.m.inventory.rule_verdict}</h3><span class="badge {row.eligible ? 'tone-ok' : 'tone-muted'}">{verdict}</span></div>
      <p>{row.reason ?? i18n.m.inventory.verdict_probe_hint}</p>
    </section>
    {#if error}<p class="callout tone-bad mb-4" role="alert">{error}</p>{/if}
    {#if file.probeError}<p class="callout tone-bad mb-4">{t(i18n.m.inventory.probe_failed, { error: file.probeError })}</p>{/if}
    <dl class="detail-specs">
      <div><dt>{i18n.m.inventory.detail_status}</dt><dd>{file.status}</dd></div>
      <div><dt>{i18n.m.inventory.detail_size}</dt><dd>{formatSize(file.sizeBytes)}</dd></div>
      <div><dt>{i18n.m.inventory.detail_container}</dt><dd>{file.container ?? '—'}</dd></div>
      {#if file.mediaKind !== 'Audio'}<div><dt>{i18n.m.inventory.detail_video}</dt><dd>{file.videoCodec ?? '—'}{#if file.width && file.height}<span>{file.width} × {file.height}</span>{/if}</dd></div>{/if}
      {#if file.mediaKind !== 'Image'}
        <div><dt>{i18n.m.inventory.detail_audio}</dt><dd>{file.audioCodecs ?? '—'}{#if file.audioTrackCount !== null}{t(i18n.m.inventory.audio_tracks, { count: file.audioTrackCount })}{/if}{#if file.audioLanguages}<span>{file.audioLanguages}</span>{/if}</dd></div>
        <div><dt>{i18n.m.inventory.detail_subtitles}</dt><dd>{file.subtitleTrackCount ?? '—'}</dd></div>
        <div><dt>{i18n.m.inventory.detail_duration}</dt><dd>{formatDuration(file.durationSeconds)}</dd></div>
      {/if}
    </dl>
    <div class="detail-path"><h3>{i18n.m.inventory.col_file}</h3><p>{file.relativePath}</p></div>
  </div>
  <footer class="detail-actions">
    <button class="btn" onclick={onprobe} disabled={probing}><Icon name="search" />{probing ? i18n.m.inventory.probing : file.status === 'Discovered' ? i18n.m.inventory.probe : i18n.m.inventory.reprobe}</button>
    {#if row.eligible}<button class="btn btn-primary" onclick={onpreview} disabled={probing}><Icon name="play" />{i18n.m.inventory.preview}</button>{/if}
  </footer>
</dialog>

<style>
  .inventory-dialog { width: 48rem; grid-template-rows: auto minmax(0, 1fr) auto; }
  .detail-hero { position: relative; display: flex; align-items: center; gap: 1.75rem; padding: 2rem; background: var(--raised); isolation: isolate; overflow: hidden; }
  .detail-backdrop { position: absolute; inset: 0; z-index: -2; width: 100%; height: 100%; object-fit: cover; object-position: 50% 35%; filter: blur(10px); transform: scale(1.1); opacity: 0; }
  .detail-backdrop.artwork-loaded { opacity: .3; }
  .detail-wash { position: absolute; inset: 0; z-index: -1; background: linear-gradient(90deg, color-mix(in srgb, var(--panel) 15%, transparent), color-mix(in srgb, var(--panel) 88%, transparent) 65%), linear-gradient(0deg, var(--panel), transparent 70%); }
  .detail-poster { flex-shrink: 0; box-shadow: var(--lift-3); border-radius: .625rem; overflow: hidden; }
  .detail-heading { min-width: 0; padding-right: .5rem; }
  .detail-library { font-size: .6875rem; letter-spacing: .08em; color: var(--ink-2); margin-bottom: .75rem; }
  h2 { font-size: clamp(1.2rem, 3vw, 1.75rem); line-height: 1.25; font-weight: 650; letter-spacing: -.035em; color: var(--ink); overflow-wrap: anywhere; display: -webkit-box; -webkit-line-clamp: 3; line-clamp: 3; -webkit-box-orient: vertical; overflow: hidden; }
  .detail-meta { display: flex; flex-wrap: wrap; gap: .4rem .875rem; margin-top: 1rem; color: var(--ink-2); font-size: .75rem; }
  .detail-close { position: absolute; right: .75rem; top: .75rem; min-width: 2.5rem; min-height: 2.5rem; background: var(--panel); }
  .detail-body { min-height: 0; overflow-y: auto; overscroll-behavior: contain; padding: 1.5rem 2rem 2rem; }
  .detail-verdict { background: var(--sunken); border-radius: .75rem; padding: 1rem 1.125rem; margin-bottom: 1.125rem; }
  h3 { font-size: .75rem; font-weight: 600; color: var(--ink-2); }
  .detail-verdict p { margin-top: .625rem; color: var(--ink-2); font-size: .8125rem; line-height: 1.65; }
  .detail-specs { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); column-gap: 2rem; }
  .detail-specs > div { display: flex; align-items: baseline; justify-content: space-between; gap: 1rem; padding: .875rem 0; border-bottom: 1px solid var(--divide-soft); font-size: .8125rem; }
  dt { color: var(--ink-3); } dd { text-align: right; color: var(--ink-2); overflow-wrap: anywhere; min-width: 0; } dd span { display: block; color: var(--ink-3); font-size: .75rem; margin-top: .25rem; }
  .detail-path { margin-top: 1.5rem; }.detail-path p { margin-top: .5rem; color: var(--ink-3); font: .6875rem/1.8 ui-monospace, monospace; overflow-wrap: anywhere; }
  .detail-actions { display: flex; justify-content: flex-end; gap: .75rem; padding: 1rem 2rem; background: var(--raised); box-shadow: inset 0 1px 0 var(--divide-soft); }.detail-actions .btn { min-height: 2.75rem; }
  @media (max-width: 639px) { .detail-hero { gap: 1rem; padding: 1.5rem 1rem 1rem; }.detail-poster :global([data-thumbnail]) { width: 5rem; height: 7.5rem; }.detail-poster :global([data-thumbnail][data-shape='square']) { height: 5rem; }.detail-heading { padding-right: 1.5rem; }.detail-library { margin-bottom: .5rem; }.detail-meta { margin-top: .5rem; font-size: .6875rem; }.detail-body { padding: 1rem; }.detail-specs { grid-template-columns: 1fr; }.detail-actions { padding: .875rem 1rem; }.detail-actions .btn { flex: 1; }.detail-close { right: .4rem; top: .4rem; } }
  @media (max-height: 540px) { .detail-hero { padding: 1rem; gap: 1rem; }.detail-poster :global([data-thumbnail]) { width: 3rem; height: 4.5rem; }.detail-poster :global([data-thumbnail][data-shape='square']) { height: 3rem; } h2 { -webkit-line-clamp: 1; line-clamp: 1; font-size: 1.125rem; }.detail-meta { margin-top: .25rem; }.detail-library { margin-bottom: .25rem; }.detail-actions { padding-block: .5rem; } }
</style>
