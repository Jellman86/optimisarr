<script lang="ts">
  import type { SoundtrackQualityReport as Report } from '../api'
  import { i18n, t } from '../i18n/i18n.svelte'
  import AudioQualityReport from './AudioQualityReport.svelte'
  import MeasureReport from './MeasureReport.svelte'
  let { report }: { report: Report | null } = $props()
  const unavailable = $derived(Boolean(report?.unavailableReason || report?.tracks?.some(item => item.report.unavailableReason)))
</script>

{#if report}
  <MeasureReport title={i18n.m.soundtrack_quality.title}
    hintLabel={t(i18n.m.common.about_information, { label: i18n.m.soundtrack_quality.title })}
    hint={report.gateEnabled ? i18n.m.soundtrack_quality.gate_hint : i18n.m.soundtrack_quality.hint}
    badge={report.gateEnabled
      ? report.gatePassed === true ? { text: i18n.m.audio_quality.passed, tone: 'ok' } : { text: i18n.m.audio_quality.blocked, tone: 'bad' }
      : { text: unavailable ? i18n.m.audio_quality.unavailable : i18n.m.audio_quality.badge, tone: 'muted' }}
    note={i18n.m.soundtrack_quality.coverage_note}>
    {#if report.unavailableReason}
      <p class="mt-3 break-words text-body text-ink-3">{report.unavailableReason}</p>
    {/if}
    {#each report.tracks ?? [] as item}
      <div class="hairline-t mt-4 min-w-0 pt-4">
        <h4 class="break-words text-body font-semibold text-ink">{t(i18n.m.soundtrack_quality.track,
          { track: item.track.candidateAudioIndex + 1, language: item.track.language ?? i18n.m.common.unknown })}</h4>
        {#if item.track.title}<p class="mt-1 break-words text-caption text-ink-3">{item.track.title}</p>{/if}
        <AudioQualityReport report={item.report} nested />
      </div>
    {/each}
  </MeasureReport>
{/if}
