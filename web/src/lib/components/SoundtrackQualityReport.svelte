<script lang="ts">
  import type { SoundtrackQualityReport as Report } from '../api'
  import { i18n, t } from '../i18n/i18n.svelte'
  import AudioQualityReport from './AudioQualityReport.svelte'
  import InfoTip from './InfoTip.svelte'
  let { report }: { report: Report | null } = $props()
  const unavailable = $derived(Boolean(report?.unavailableReason || report?.tracks?.some(item => item.report.unavailableReason)))
</script>

{#if report}
  <section class="mt-5 min-w-0 rounded-lg border border-line bg-panel p-4" aria-label={i18n.m.soundtrack_quality.title}>
    <div class="flex min-w-0 flex-wrap items-center justify-between gap-2">
      <div class="flex min-w-0 items-center gap-2">
        <h3 class="text-sm font-semibold text-ink-2">{i18n.m.soundtrack_quality.title}</h3>
        <InfoTip label={t(i18n.m.common.about_information, { label: i18n.m.soundtrack_quality.title })}
          text={report.gateEnabled ? i18n.m.soundtrack_quality.gate_hint : i18n.m.soundtrack_quality.hint} />
      </div>
      <span class="badge" class:tone-ok={report.gateEnabled && report.gatePassed === true}
        class:tone-bad={report.gateEnabled && report.gatePassed === false} class:tone-muted={!report.gateEnabled}>
        {report.gateEnabled ? report.gatePassed ? i18n.m.audio_quality.passed : i18n.m.audio_quality.blocked
          : unavailable ? i18n.m.audio_quality.unavailable : i18n.m.audio_quality.badge}
      </span>
    </div>
    {#if report.unavailableReason}
      <p class="mt-3 break-words text-sm text-ink-3">{report.unavailableReason}</p>
    {/if}
    {#each report.tracks ?? [] as item}
      <div class="mt-4 min-w-0 border-t border-line-soft pt-4">
        <h4 class="break-words text-sm font-semibold text-ink-2">{t(i18n.m.soundtrack_quality.track,
          { track: item.track.candidateAudioIndex + 1, language: item.track.language ?? i18n.m.common.unknown })}</h4>
        {#if item.track.title}<p class="mt-1 break-words text-xs text-ink-3">{item.track.title}</p>{/if}
        <AudioQualityReport report={item.report} />
      </div>
    {/each}
    <p class="mt-4 border-t border-line-soft pt-3 text-xs leading-relaxed text-ink-3">{i18n.m.soundtrack_quality.coverage_note}</p>
  </section>
{/if}
