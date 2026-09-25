export interface DailySaving {
  date: string
  bytesSaved: number
  files: number
}

export interface SavingsBar extends DailySaving {
  /** Height as a share of the busiest day, 0 for a day that saved nothing. */
  fraction: number
}

// The smallest share a day that saved anything is drawn at. Without it a quiet day beside a huge
// one rounds to nothing and reads as a day when nothing happened, which is not true.
const MIN_VISIBLE = 0.02

export function savingsBars(days: DailySaving[]) {
  const peakBytes = Math.max(0, ...days.map(day => day.bytesSaved))
  const bars: SavingsBar[] = days.map(day => ({
    ...day,
    fraction: day.bytesSaved > 0 && peakBytes > 0 ? Math.max(MIN_VISIBLE, day.bytesSaved / peakBytes) : 0,
  }))
  return {
    bars,
    peakBytes,
    totalBytes: days.reduce((sum, day) => sum + day.bytesSaved, 0),
    totalFiles: days.reduce((sum, day) => sum + day.files, 0),
  }
}
