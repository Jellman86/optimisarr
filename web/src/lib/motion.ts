// The motion tokens live in app.css so CSS transitions and the few script-driven animations
// (Svelte's `animate:` and `transition:` take milliseconds) share one set of durations, and
// both collapse together when someone asks for reduced motion.
export type MotionDuration = 'fast' | 'base' | 'slow'

/** A CSS time ("180ms", "0.3s") in milliseconds. Anything unreadable or negative is no motion. */
export function parseCssTime(value: string): number {
  const match = /^\s*(\d*\.?\d+)(ms|s)\s*$/.exec(value)
  if (!match) return 0
  const amount = Number(match[1])
  return match[2] === 's' ? amount * 1000 : amount
}

/** The current value of a motion token, already zero when reduced motion is on. */
export function motionMs(duration: MotionDuration): number {
  return parseCssTime(getComputedStyle(document.documentElement).getPropertyValue(`--motion-${duration}`))
}
