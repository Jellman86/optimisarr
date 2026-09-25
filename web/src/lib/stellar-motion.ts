// The cube itself never moves: activity only streams its stars toward the front corner.
// Star positions blend from home by `activity`, so reaching rest (activity exactly 0)
// restores the committed still however far the flow has run.
export function createStellarMotion() {
  let mode: 'rest' | 'cycle' | 'settle' = 'rest'
  let time = 0,
    activity = 0,
    flow = 0,
    drift = 0

  return {
    get mode() {
      return mode
    },
    get time() {
      return time
    },
    get activity() {
      return activity
    },
    get flow() {
      return flow
    },
    get drift() {
      return drift
    },
    step(dt: number, working: boolean) {
      if (working && mode !== 'cycle') mode = 'cycle'
      else if (!working && mode === 'cycle') mode = 'settle'
      // A state notification must not move the stars.
      if (dt <= 0) return
      time += dt
      activity += ((working ? 1 : 0) - activity) * (1 - Math.exp(-dt * 1.8))
      flow += dt * 0.3 * activity
      drift += dt * (0.04 + 0.3 * activity)
      if (mode === 'settle' && activity < 0.001) {
        mode = 'rest'
        activity = 0
      }
    },
  }
}
export type StellarMotion = ReturnType<typeof createStellarMotion>
