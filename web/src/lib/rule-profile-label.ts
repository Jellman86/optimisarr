import type { Messages } from './i18n/en'

// Friendly display names for raw rule-profile ids so a badge reads "Scott's Settings", not the
// PascalCase enum name "ScottsSettings".
export function ruleProfileLabel(profile: string, m: Messages['libraries']): string {
  const labels: Record<string, string> = {
    ConservativeHevc: m.profile_conservative_hevc,
    CompatibilityH264: m.profile_compatibility_h264,
    ExperimentalAv1: m.profile_experimental_av1,
    RemuxCleanup: m.profile_remux_cleanup,
    ScottsSettings: m.profile_scotts_settings,
    TrackCleanup: m.profile_track_cleanup,
  }
  return labels[profile] ?? profile
}
