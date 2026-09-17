export function createBrandMotion() {
  const sliceCount = 15
  const angles: number[] = Array(sliceCount).fill(0),
    velocities: number[] = Array(sliceCount).fill(0)
  let opening = 0,
    openingVelocity = 0,
    cycleTime = 0,
    turnBase = 0,
    settleAngle = 0,
    mode: 'rest' | 'cycle' | 'settle' = 'rest',
    previousRequest = false
  const tau = 2 * Math.PI
  const smooth = (x: number) => {
    x = Math.max(0, Math.min(1, x))
    return x * x * x * (x * (x * 6 - 15) + 10)
  }
  function sequence(phase: number) {
    return Array.from({ length: sliceCount }, (_, i) =>
      i === 0 ? 0 : turnBase + tau * smooth((phase - 1.2 - (i - 1) * 0.065) / 4.9),
    )
  }
  function spring(position: number, velocity: number, target: number, frequency: number, dt: number) {
    const offset = position - target,
      decay = Math.exp(-frequency * dt),
      travel = (velocity + frequency * offset) * dt
    return [target + (offset + travel) * decay, (velocity - frequency * travel) * decay]
  }
  function updateMotion(dt: number, working: boolean) {
    if (working !== previousRequest) {
      if (working) {
        if (mode === 'rest') {
          turnBase = settleAngle
          cycleTime = 0
        }
        mode = 'cycle'
      } else {
        settleAngle = tau * Math.round(angles.slice(1).reduce((a, b) => a + b, 0) / (sliceCount - 1) / tau)
        mode = 'settle'
      }
      previousRequest = working
    }
    let targets
    if (mode === 'cycle') {
      cycleTime += dt
      while (cycleTime >= 8.8) {
        cycleTime -= 8.8
        turnBase += tau
      }
      targets = sequence(cycleTime)
    } else targets = angles.map((_, i) => (i === 0 ? 0 : settleAngle))
    for (let i = 1; i < sliceCount; i++)
      [angles[i], velocities[i]] = spring(angles[i], velocities[i], targets[i], 10, dt)
    angles[0] = 0
    velocities[0] = 0
    const alignedTo = mode === 'cycle' ? turnBase + (cycleTime > 4 ? tau : 0) : settleAngle
    const maxError = Math.max(...angles.slice(1).map((a) => Math.abs(a - alignedTo)))
    const maxVelocity = Math.max(...velocities.slice(1).map(Math.abs))
    let targetOpening =
      mode === 'cycle' ? smooth((cycleTime - 0.6) / 0.6) * (1 - smooth((cycleTime - 6.945) / 0.6)) : 0
    // Keep the cuts open until the turning has settled; the gap itself is spring-driven.
    if (maxError > 0.006 || maxVelocity > 0.05) targetOpening = 1
    ;[opening, openingVelocity] = spring(opening, openingVelocity, targetOpening, 9, dt)
    if (
      mode === 'settle' &&
      maxError < 1e-8 &&
      maxVelocity < 1e-8 &&
      Math.abs(opening) < 1e-8 &&
      Math.abs(openingVelocity) < 1e-8
    )
      mode = 'rest'
  }

  let lightPhase = Math.atan2(2.5, 5.6)
  return {
    angles,
    velocities,
    get opening() {
      return opening
    },
    get openingVelocity() {
      return openingVelocity
    },
    get mode() {
      return mode
    },
    get lightPhase() {
      return lightPhase
    },
    step(dt: number, working: boolean) {
      lightPhase = (lightPhase + (dt * tau) / 32) % tau
      updateMotion(dt, working)
    },
  }
}
export type BrandMotion = ReturnType<typeof createBrandMotion>
