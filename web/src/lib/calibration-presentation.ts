export type CalibrationSetting = { profile: string | null, quality: number }

export type CalibrationSettingChange = {
  now: CalibrationSetting
  after: CalibrationSetting
  unchanged: boolean
}

type ResultSettings = {
  recommendedQuality: number | null
  recommendedProfile: string | null
  currentQuality: number
  currentProfile: string | null
}

// Apply writes only the recommended quality (and, for preset comparisons, the preset), so the
// saved preset is named only when the comparison could change it.
export function settingChange(result: ResultSettings): CalibrationSettingChange | null {
  if (result.recommendedQuality === null) return null
  const profileChanges = result.recommendedProfile !== null
  return {
    now: { profile: profileChanges ? result.currentProfile : null, quality: result.currentQuality },
    after: { profile: result.recommendedProfile, quality: result.recommendedQuality },
    unchanged: result.recommendedQuality === result.currentQuality
      && (!profileChanges || result.recommendedProfile === result.currentProfile),
  }
}

export function nextUnratedName(candidateNames: string[], ratings: Record<string, unknown>, activeName: string): string | null {
  const start = candidateNames.indexOf(activeName) + 1
  const ordered = [...candidateNames.slice(start), ...candidateNames.slice(0, start)]
  return ordered.find((name) => name !== activeName && !ratings[name]) ?? null
}

// Fit-to-view scales the image by the smaller axis ratio; 1:1 is the inverse of that scale.
export function actualPixelsZoom(naturalWidth: number, naturalHeight: number, viewWidth: number, viewHeight: number): number | null {
  if (naturalWidth <= 0 || naturalHeight <= 0 || viewWidth <= 0 || viewHeight <= 0) return null
  return 1 / Math.min(viewWidth / naturalWidth, viewHeight / naturalHeight)
}

// O always means the original reference; every other key must be a candidate's own blind letter.
export function sampleForKey(key: string, names: string[]): string | null {
  if (key.length !== 1) return null
  const letter = key.toUpperCase()
  if (letter === 'O') return names.includes('ORIGINAL') ? 'ORIGINAL' : null
  return names.find((name) => name === letter) ?? null
}
