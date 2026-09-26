export interface DailySaving {
  date: string
  bytesSaved: number
  files: number
}

export interface SavingsBar extends DailySaving {
  /** Height as a share of the chart's scale, 0 for a day that saved nothing. */
  fraction: number
}

// The smallest share a day that saved anything is drawn at. Without it a quiet day beside a huge
// one rounds to nothing and reads as a day when nothing happened, which is not true.
const MIN_VISIBLE = 0.02

// Sizes are written in binary units (formatSize), so the scale steps in them too: a gridline at
// 10,000,000,000 bytes would be labelled "9.3 GB".
const UNIT = 1024
const STEPS = [1, 2, 5]
const MAX_INTERVALS = 5

/** The top of the scale and its gridlines: the busiest day rounded up to a whole 1, 2 or 5 step. */
function scaleFor(peakBytes: number): { scaleBytes: number; ticks: number[] } {
  if (peakBytes <= 0) return { scaleBytes: 0, ticks: [0] }
  let unit = 1
  while (peakBytes / unit >= UNIT) unit *= UNIT
  const value = peakBytes / unit
  for (let magnitude = 10 ** (Math.floor(Math.log10(value)) - 1); ; magnitude *= 10) {
    for (const step of STEPS) {
      const intervals = Math.ceil(value / (step * magnitude))
      if (intervals <= MAX_INTERVALS) {
        const size = step * magnitude * unit
        return { scaleBytes: intervals * size, ticks: Array.from({ length: intervals + 1 }, (_, index) => index * size) }
      }
    }
  }
}

export function savingsBars(days: DailySaving[]) {
  const peakBytes = Math.max(0, ...days.map(day => day.bytesSaved))
  const { scaleBytes, ticks } = scaleFor(peakBytes)
  const bars: SavingsBar[] = days.map(day => ({
    ...day,
    fraction: day.bytesSaved > 0 && scaleBytes > 0 ? Math.max(MIN_VISIBLE, day.bytesSaved / scaleBytes) : 0,
  }))
  const totalBytes = days.reduce((sum, day) => sum + day.bytesSaved, 0)
  const averageBytes = days.length > 0 ? totalBytes / days.length : 0
  // Counted back from today, so the rightmost mark is always the day the chart ends on.
  const weekMarks: number[] = []
  for (let index = days.length - 1; index >= 0; index -= 7) weekMarks.unshift(index)
  return {
    bars,
    peakBytes,
    scaleBytes,
    ticks,
    totalBytes,
    totalFiles: days.reduce((sum, day) => sum + day.files, 0),
    averageBytes,
    averageFraction: scaleBytes > 0 ? averageBytes / scaleBytes : 0,
    weekMarks,
  }
}
