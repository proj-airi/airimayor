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

  constructor(
    private times: readonly number[],
    private options: {
      read: (position: number) => Promise<T>
      commit: (frame: T) => void
      state: (playing: boolean) => void
      error: (error: unknown) => void
    },
    private scheduler: FrameScheduler = {
      now: () => performance.now(),
      request: (callback) => requestAnimationFrame(callback),
      cancel: (id) => cancelAnimationFrame(id),
    },
  ) {}

  play(position: number, speed: number) {
    this.pause()
    this.active = true
    this.options.state(true)
    const generation = this.generation
    const start = this.scheduler.now(),
      origin = this.times[position]
    let previous = position - 1
    const tick = (now: number) => {
      if (!this.active || generation !== this.generation) return
      this.handle = this.scheduler.request(tick)
      if (this.pending) return
      const next = Math.min(
        this.times.length - 1,
        Math.max(position, sortedLastIndex(this.times, origin + (now - start) * speed) - 1),
      )
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
