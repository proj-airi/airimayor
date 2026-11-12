import { sortedLastIndex } from 'es-toolkit/compat'

export interface FrameScheduler {
  now: () => number
  request: (callback: (time: number) => void) => number
  cancel: (id: number) => void
}

export class Playback<T> {
  private generation = 0
  private pending = false
  private handle?: number
  private active = false
  private times: number[]

  constructor(
    private recorded: readonly number[],
    private options: {
      read: (position: number) => Promise<T>
      commit: (frame: T) => void
      state: (playing: boolean) => void
      cursor?: (time: number) => void
      error: (error: unknown) => void
    },
    private scheduler: FrameScheduler = {
      now: () => performance.now(),
      request: (callback) => requestAnimationFrame(callback),
      cancel: (id) => cancelAnimationFrame(id),
    },
  ) {
    this.times = [0]
    for (let index = 1; index < recorded.length; index++)
      this.times[index] = this.times[index - 1] + Math.min(1500, Math.max(0, recorded[index] - recorded[index - 1]))
  }

  play(position: number, speed: number, cursor = this.recorded[position]) {
    this.pause()
    position = Math.max(0, sortedLastIndex(this.recorded, cursor) - 1)
    this.active = true
    this.options.state(true)
    const generation = this.generation
    const start = this.scheduler.now()
    const nextTime = this.recorded[position + 1] ?? cursor
    const fraction =
      nextTime > this.recorded[position]
        ? Math.max(0, Math.min(1, (cursor - this.recorded[position]) / (nextTime - this.recorded[position])))
        : 0
    const origin =
      this.times[position] + fraction * ((this.times[position + 1] ?? this.times[position]) - this.times[position])
    let previous = position - 1
    const tick = (now: number) => {
      if (!this.active || generation !== this.generation) return
      this.handle = this.scheduler.request(tick)
      const elapsed = Math.min(this.times.at(-1)!, origin + (now - start) * speed)
      const next = Math.min(this.times.length - 1, Math.max(position, sortedLastIndex(this.times, elapsed) - 1))
      const after = Math.min(next + 1, this.times.length - 1)
      const span = this.times[after] - this.times[next]
      const amount = span > 0 ? (elapsed - this.times[next]) / span : 0
      this.options.cursor?.(this.recorded[next] + amount * (this.recorded[after] - this.recorded[next]))
      if (this.pending) return
      if (next <= previous) return
      previous = next
      this.pending = true
      void this.options
        .read(next)
        .then(
          (frame) => {
            if (!this.active || generation !== this.generation) return
            this.options.commit(frame)
            if (next === this.times.length - 1) this.pause()
          },
          (error) => {
            if (generation === this.generation) {
              this.pause()
              this.options.error(error)
            }
          },
        )
        .finally(() => {
          this.pending = false
        })
    }
    this.handle = this.scheduler.request(tick)
  }

  pause() {
    this.active = false
    this.generation++
    if (this.handle !== undefined) this.scheduler.cancel(this.handle)
    this.handle = undefined
    this.options.state(false)
  }
}
