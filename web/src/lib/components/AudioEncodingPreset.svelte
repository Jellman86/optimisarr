<script lang="ts">
  import { i18n, t } from '../i18n/i18n.svelte'
  import { audioEncodingPresets, type AudioEncodingMode } from '../audio-encoding-presets'
  import InfoTip from './InfoTip.svelte'

  let { id, codec, bitrate, defaultBitrate, mode, video = false, onselect }: {
    id: string
    codec: string
    bitrate: number | null
    defaultBitrate: number
    mode: AudioEncodingMode
    video?: boolean
    onselect: (mode: AudioEncodingMode) => void
  } = $props()

  const stops = $derived([
    { mode: 'default' as const, label: i18n.m.audio_encoding.default, bitrate: defaultBitrate },
    ...audioEncodingPresets(codec).map(preset => ({ ...preset, label: {
      'space-saver': i18n.m.audio_encoding.space_saver,
      balanced: i18n.m.audio_encoding.balanced,
      high: i18n.m.audio_encoding.high,
      'very-high': i18n.m.audio_encoding.very_high,
    }[preset.mode] })),
    { mode: 'custom' as const, label: i18n.m.libraries.stop_custom, bitrate: bitrate ?? defaultBitrate },
  ])
  const selected = $derived(stops.findIndex(stop => stop.mode === mode))
  const budget = $derived(t(i18n.m.audio_encoding.budget, { codec: codec.toUpperCase(), bitrate: bitrate ?? defaultBitrate }))
  const savedDefaultBudget = $derived(mode === 'default' && bitrate != null)
</script>

<div class="mt-4 min-w-0" data-audio-encoding-preset>
  <label class="label" for={id}>{i18n.m.audio_encoding.title}
    <InfoTip label={t(i18n.m.common.about_information, { label: i18n.m.audio_encoding.title })} text={i18n.m.audio_encoding.hint} />
  </label>
  <div class="rounded-lg border border-line-soft bg-sunken px-1 py-2 sm:px-2">
    <div class="mx-[8.333%]">
      <input {id} type="range" min="0" max={stops.length - 1} step="1" value={selected}
        class="block h-11 w-full cursor-pointer accent-cyan-600"
        aria-label={i18n.m.audio_encoding.title} aria-valuetext={`${stops[selected].label} · ${budget}`}
        aria-describedby={`${id}-hint${savedDefaultBudget ? ` ${id}-saved` : ''}`}
        oninput={event => onselect(stops[Number(event.currentTarget.value)].mode)} />
    </div>
    <div class="grid grid-cols-6">
      {#each stops as stop, index}
        <button type="button" class="flex min-h-16 min-w-0 flex-col items-center gap-2 rounded-md px-0.5 py-2 text-[10px] leading-4 transition-colors hover:bg-lit focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent sm:text-xs"
          class:text-accent={selected === index} class:text-ink-3={selected !== index}
          title={t(i18n.m.audio_encoding.budget, { codec: codec.toUpperCase(), bitrate: stop.bitrate })}
          aria-pressed={selected === index} onclick={() => onselect(stop.mode)}>
          <span class="h-1.5 w-1.5 rounded-full" class:bg-accent={selected === index} class:bg-line={selected !== index} aria-hidden="true"></span>
          <span class="max-w-full break-words text-center font-medium">{stop.label}</span>
        </button>
      {/each}
    </div>
  </div>
  <div class="mt-3 flex min-w-0 flex-wrap items-center gap-2">
    <span class="text-sm font-semibold text-ink-2">{stops[selected].label}</span>
    <span class="badge tone-neutral max-w-full whitespace-normal break-words">{budget}</span>
    {#if !video}<span class="text-xs text-ink-3">{t(i18n.m.audio_encoding.format, { container: codec === 'aac' ? 'm4a' : codec })}</span>{/if}
  </div>
  <p id={`${id}-hint`} class="mt-2 text-xs leading-relaxed text-ink-3">{i18n.m.audio_encoding.hint}</p>
  {#if savedDefaultBudget}<p id={`${id}-saved`} class="mt-2 text-xs leading-relaxed text-ink-3">{i18n.m.audio_encoding.saved_budget}</p>{/if}
  <p class="mt-2 text-xs leading-relaxed text-ink-3">{i18n.m.audio_encoding.channel_note}</p>
  {#if mode === 'custom'}<p class="mt-2 text-xs leading-relaxed text-ink-3">{i18n.m.audio_encoding.custom_note}</p>{/if}
  {#if video}<p class="mt-2 text-xs leading-relaxed text-ink-3">{i18n.m.audio_encoding.video_note}</p>{/if}
</div>
