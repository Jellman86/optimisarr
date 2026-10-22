<script lang="ts">
  import type { PerceptualSearchSummary } from '../api'
  import { formatSize } from '../format'
  import { i18n, t } from '../i18n/i18n.svelte'

  let { report, active = false }: { report: PerceptualSearchSummary | null; active?: boolean } = $props()
  let decision = $derived(report?.result?.decision)
  let retryMinimum = $derived(report?.minimumValue ?? report?.retryValue)
  function setting(value: number) { return report?.kind === 'Audio' ? `${value} kbps` : String(value) }
  // Enough precision to compare candidates, not to imply calibrated accuracy.
  function score(value: number | null, metric: 'audio' | 'image' | 'ssim' = report?.kind === 'Audio' ? 'audio' : 'image') {
    if (value == null || !Number.isFinite(value)) return '—'
    return Number(metric === 'audio' ? value.toPrecision(3) : value.toFixed(metric === 'ssim' ? 3 : 1)).toString()
  }
  let probes = $derived([...(report?.result?.probes ?? [])].sort((a, b) => a.value - b.value))
  let passing = $derived(probes.filter(probe => probe.measurement.available && probe.measurement.meetsTarget).length)
</script>

{#if report && decision}
  <section class="mt-4 min-w-0 rounded-lg border border-line bg-panel p-4" aria-label={i18n.m.quality_first.title}>
    <h3 class="text-sm font-semibold text-ink-2">{i18n.m.quality_first.title}</h3>
    {#if decision.nextValue != null}
    <p class="mt-3 text-sm font-medium text-ink-2" aria-live="polite">{active ? i18n.m.quality_first.searching : i18n.m.quality_first.interrupted}</p>
    {:else if !report.preparationBlocked}
    <p class="mt-3 text-sm font-medium text-ink" aria-live="polite">{decision.fellBack
      ? t(i18n.m.quality_first.headline_fallback, { setting: setting(decision.selectedValue) })
      : t(i18n.m.quality_first.headline_selected, { setting: setting(decision.selectedValue), count: passing })}</p>
    <p class="mt-1 text-xs text-ink-3">{report.retryValue != null ? i18n.m.quality_first.previous_selected : i18n.m.quality_first.selected}: {setting(decision.selectedValue)} · {report.encoder}</p>
    {/if}
    <details class="mt-2 text-xs text-ink-3"><summary class="cursor-pointer">{i18n.m.queue.technical_error}</summary><p class="mt-1 break-words">{decision.reason}</p></details>
    {#if report.kind === 'Audio' && report.maximumAudioDistance != null}
      <p class="mt-2 font-mono text-sm tabular-nums text-ink-2">Zimtohrli ≤ {score(report.maximumAudioDistance)}</p>
      {#if report.audioWindows.length > 0}
        <p class="mt-2 text-xs text-ink-3">{t(i18n.m.audio_quality.coverage, { seconds: report.audioWindows.reduce((total, window) => total + window.durationSeconds, 0), windows: report.audioWindows.length })}</p>
      {/if}
    {:else if report.kind === 'Image' && report.minimumImageScore != null}
      <p class="mt-2 font-mono text-sm tabular-nums text-ink-2">SSIMULACRA2 ≥ {score(report.minimumImageScore)}</p>
    {/if}
    {#if retryMinimum != null}
      <p class="mt-2 break-words text-xs text-ink-3">{i18n.m.quality_first.retry} · {t(i18n.m.quality_first.retry_minimum, { value: setting(retryMinimum) })}</p>
    {/if}
    <ol class="mt-4 grid min-w-0 grid-cols-1 gap-2 sm:grid-cols-2">
      {#each probes as probe}
        {@const chosen = decision.nextValue == null && !decision.fellBack && probe.value === decision.selectedValue}
        <li class="min-w-0 rounded-md border bg-sunken p-3 {chosen ? 'border-accent' : 'border-line-soft'}">
          <div class="flex flex-wrap items-center justify-between gap-2 text-sm">
            <span class="flex items-center gap-2">{i18n.m.quality_first.candidate}: {setting(probe.value)}{#if chosen}<span class="badge tone-accent">{i18n.m.quality_first.chosen}</span>{/if}</span>
            <span class="badge" class:tone-ok={probe.measurement.available && probe.measurement.meetsTarget}
              class:tone-bad={probe.measurement.available && !probe.measurement.meetsTarget}
              class:tone-muted={!probe.measurement.available}>
              {probe.measurement.available ? (probe.measurement.meetsTarget ? i18n.m.audio_quality.passed : i18n.m.audio_quality.blocked) : i18n.m.audio_quality.unavailable}
            </span>
          </div>
          <p class="mt-2 font-mono text-xs tabular-nums text-ink-3">{report.kind === 'Audio' ? 'Zimtohrli' : 'SSIMULACRA2'}: {score(probe.measurement.score)} · {formatSize(probe.encodedBytes)}</p>
          {#if probe.measurement.imageSsim != null}<p class="mt-2 font-mono text-xs text-ink-3">SSIM: {score(probe.measurement.imageSsim, 'ssim')}</p>{/if}
          {#if probe.measurement.loudnessDriftLufs != null}<p class="mt-2 text-xs text-ink-3">{i18n.m.quality_first.loudness_drift}: {score(probe.measurement.loudnessDriftLufs)} LU</p>{/if}
          {#if probe.measurement.targetDetail}<p class="mt-2 break-words text-xs text-ink-3">{probe.measurement.targetDetail}</p>{/if}
          {#if probe.measurement.error}<p class="mt-2 break-words text-xs text-ink-3">{probe.measurement.error}</p>{/if}
        </li>
      {/each}
    </ol>
    <p class="mt-3 border-t border-line-soft pt-3 text-xs leading-relaxed text-ink-3">{i18n.m.quality_first.note}</p>
  </section>
{/if}
