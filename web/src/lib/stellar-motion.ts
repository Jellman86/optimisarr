export function createStellarMotion() {
  const pose = { yaw: 0.082, pitch: 0, depth: 1 }
  const velocity = { yaw: 0, pitch: 0, depth: 0 }
  let mode: 'rest' | 'cycle' | 'settle' = 'rest'
  let time = 0,
    lightTime = 0,
    phase = 0,
    activity = 0
  const smooth = (x: number) => x * x * x * (x * (x * 6 - 15) + 10)

  function spring(name: keyof typeof pose, target: number, dt: number, frequency: number) {
    const offset = pose[name] - target
    const term = velocity[name] + frequency * offset
    const decay = Math.exp(-frequency * dt)
    pose[name] = target + (offset + term * dt) * decay
    velocity[name] = (velocity[name] - frequency * term * dt) * decay
  }

  return {
    pose,
    velocity,
    get mode() {
      return mode
    },
    get lightTime() {
      return lightTime
    },
    get phase() {
      return phase
    },
    step(dt: number, working: boolean) {
      if (working && mode !== 'cycle') {
        if (mode === 'rest') time = 0
        mode = 'cycle'
      } else if (!working && mode === 'cycle') mode = 'settle'
      // A state notification must not move the mesh or change its velocity.
      if (dt <= 0) return
      activity += ((working ? 1 : 0) - activity) * (1 - Math.exp(-dt * 1.6))
      if (working) time += dt
      lightTime += dt
      phase += dt * (1.5 + 14.5 * activity)
      const cycle = time % 36
      const target = !working
        ? 1
        : cycle < 10
          ? 1
          : cycle < 15
            ? 1 - 2 * smooth((cycle - 10) / 5)
            : cycle < 21
              ? -1
              : cycle < 26
                ? -1 + 2 * smooth((cycle - 21) / 5)
                : 1
      spring('depth', target, dt, working ? 4 : 2.4)
      spring('yaw', working ? 0.24 * Math.sin(time * 0.3 + 0.35) : 0.082, dt, 2.5)
      spring('pitch', working ? 0.16 * Math.sin(time * 0.23) : 0, dt, 2.5)
      if (
        mode === 'settle' &&
        Math.abs(pose.depth - 1) < 0.0001 &&
        Math.abs(pose.yaw - 0.082) < 0.0001 &&
        Math.abs(pose.pitch) < 0.0001 &&
        Math.hypot(...Object.values(velocity)) < 0.0003 &&
        activity < 0.001
      ) {
        mode = 'rest'
        Object.assign(pose, { yaw: 0.082, pitch: 0, depth: 1 })
        Object.assign(velocity, { yaw: 0, pitch: 0, depth: 0 })
        activity = 0
      }
    },
  }
}
export type StellarMotion = ReturnType<typeof createStellarMotion>
