import assert from 'node:assert/strict'
import { test } from 'node:test'
import { Playback, type FrameScheduler } from './playback'

function clock() {
  let now = 0,
    id = 0
  const callbacks = new Map<number, (time: number) => void>()
  const scheduler: FrameScheduler = {
    now: () => now,
    request: (callback) => {
      callbacks.set(++id, callback)
      return id
    },
    cancel: (key) => {
      callbacks.delete(key)
    },
  }
  return {
    scheduler,
    tick: (elapsed: number) => {
      now += elapsed
      const pending = [...callbacks.values()]
      callbacks.clear()
      pending.forEach((callback) => callback(now))
    },
  }
}

test('the shared cursor advances between records and through shortened gaps while a worker read is pending', () => {
  const time = clock(),
    cursors: number[] = []
  const player = new Playback<number>(
    [0, 1000, 10000],
    {
      read: () => new Promise(() => {}),
      commit: () => {},
      state: () => {},
      cursor: (value: number) => cursors.push(value),
      error: (error) => {
        throw error
      },
    },
    time.scheduler,
  )
  player.play(0, 1)
  time.tick(16)
  time.tick(984)
  time.tick(750)
  assert.deepEqual(cursors, [16, 1000, 5500])
  player.pause()
  time.tick(100)
  assert.equal(cursors.length, 3)
})

test('resuming keeps the cursor between records instead of jumping back to the last committed record', () => {
  const time = clock(),
    cursors: number[] = []
  const player = new Playback(
    [0, 1000, 10000],
    {
      read: async (position) => position,
      commit: () => {},
      state: () => {},
      cursor: (value) => cursors.push(value),
      error: (error) => {
        throw error
      },
    },
    time.scheduler,
  )
  player.play(0, 1)
  time.tick(400)
  player.pause()
  time.tick(1000)
  player.play(0, 1, 400)
  time.tick(100)
  assert.equal(cursors.at(-1), 500)
  time.tick(1000)
  assert.equal(cursors.at(-1), 4000)
  player.pause()
  player.play(0, 1, 4000)
  time.tick(100)
  assert.equal(cursors.at(-1), 4600)
  player.pause()
})

test('pause is immediate while a worker result is pending and ignores that stale result', async () => {
  const time = clock(),
    states: boolean[] = [],
    committed: number[] = []
  let finish!: (value: number) => void
  const player = new Playback<number>(
    [0, 10, 20],
    {
      read: () =>
        new Promise((resolve) => {
          finish = resolve
        }),
      commit: (value) => committed.push(value),
      state: (value) => states.push(value),
      error: (error) => {
        throw error
      },
    },
    time.scheduler,
  )
  player.play(0, 1)
  time.tick(10)
  player.pause()
  assert.equal(states.at(-1), false)
  finish(1)
  await Promise.resolve()
  time.tick(100)
  assert.deepEqual(committed, [])
})

test('playback batches due updates into one in-flight worker read rather than replaying a render for each event', async () => {
  const time = clock(),
    positions: number[] = [],
    committed: number[] = []
  let finish!: (value: number) => void
  const player = new Playback<number>(
    Array.from({ length: 1000 }, (_, index) => index),
    {
      read: (position) => {
        positions.push(position)
        return new Promise((resolve) => {
          finish = resolve
        })
      },
      commit: (value) => committed.push(value),
      state: () => {},
      error: (error) => {
        throw error
      },
    },
    time.scheduler,
  )
  player.play(0, 2)
  time.tick(16)
  time.tick(16)
  time.tick(16)
  assert.deepEqual(positions, [32])
  finish(32)
  await new Promise((resolve) => setImmediate(resolve))
  time.tick(16)
  assert.deepEqual(positions, [32, 128])
  assert.deepEqual(committed, [32])
  player.pause()
  finish(128)
})
