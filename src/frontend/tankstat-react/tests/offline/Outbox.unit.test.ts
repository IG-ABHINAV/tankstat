import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { deviceData } from '../../src/offline/deviceData.ts'
import { memoryStorage, type DeviceStorage } from '../../src/offline/deviceStorage.ts'
import { keptPhotos } from '../../src/offline/keptPhotos.ts'
import { outbox, type ChangeDraft } from '../../src/offline/outbox.ts'

const log = (id: string, input: Record<string, unknown> = {}): ChangeDraft => ({ id, entity: 'refuelings', action: 'add', vehicleId: 'v1', targetId: id, input: { id, vehicleId: 'v1', ...input } })
const visit = (id: string, expenseId: string, schedules: string[] | undefined, input: Record<string, unknown> = {}): ChangeDraft => ({
  id, entity: 'recurring', action: 'markDone', vehicleId: 'v1', targetId: expenseId, targetIds: schedules, input: { expenseId, ...input },
})

/** A device whose storage fails in the ways asked for: the changes cannot be read back, the kept photos cannot be listed or removed. */
function unreliableStorage(fail: { changes?: boolean; photos?: boolean }): DeviceStorage {
  const inner = memoryStorage()
  const broken = () => Promise.reject(new Error('The storage failed.'))
  return {
    ...inner,
    async open(user) {
      const store = await inner.open(user)
      const rows = { ...store.rows }
      if (fail.changes) rows.changes = broken
      if (fail.photos) {
        rows.photos = broken
        rows.deletePhotos = broken
      }
      return { ...store, rows }
    },
  }
}

async function openAs(user: string | null, storage: DeviceStorage = memoryStorage()) {
  deviceData.reset(storage)
  if (user) await deviceData.signedIn(user)
  await outbox.reload()
}

beforeEach(() => openAs('u1'))
afterEach(() => openAs(null))

describe('with nobody\'s data open', () => {
  it('keeps nothing, and taking back or answering changes does nothing', async () => {
    await openAs(null)

    await expect(outbox.enqueue(log('r1'))).rejects.toThrow('No user data is open on this device.')
    await outbox.discard('r1') // the failed change does not hold up the next one
    await outbox.remove(['r1'])
    expect(await outbox.markSent(['r1'])).toEqual([])

    expect(outbox.changes).toEqual([])
  })
})

describe('a storage that fails', () => {
  it('changes that cannot be read back count as none, and new ones are still kept', async () => {
    await openAs('u1', unreliableStorage({ changes: true }))

    expect(outbox.changes).toEqual([])
    await outbox.enqueue(log('r1'))

    expect(outbox.changes.map((c) => c.id)).toEqual(['r1'])
  })

  it('kept photos that cannot be cleaned up or removed never stop a change', async () => {
    await openAs('u1', unreliableStorage({ photos: true })) // the clean-up at load fails quietly
    const photo = await keptPhotos.keep(new Blob([new Uint8Array([1])]), 'v1')
    await outbox.enqueue(log('r1', { photoIds: [photo] }))

    await outbox.discard('r1') // its photo is to go, but cannot

    expect(outbox.changes).toEqual([])
  })
})

describe('another tab', () => {
  it('a change another tab kept shows here once that tab says so', async () => {
    const heard = vi.fn()
    const stop = outbox.subscribe(heard)
    const rows = (await deviceData.rows())!
    await rows.putChanges([{ ...log('r1'), seq: 1, createdAt: 1 }])
    const other = new BroadcastChannel('tankstat-outbox')

    other.postMessage('changed')

    await vi.waitFor(() => expect(heard).toHaveBeenCalled())
    expect(outbox.changes.map((c) => c.id)).toEqual(['r1'])
    other.close()
    stop()
  })

  it('every change of the outbox holds the lock the tabs share', async () => {
    const request = vi.fn((_name: string, work: () => Promise<unknown>) => work())
    Object.defineProperty(navigator, 'locks', { value: { request }, configurable: true })
    try {
      await outbox.enqueue(log('r1'))
      await outbox.remove(['r1'])
    } finally {
      delete (navigator as { locks?: unknown }).locks
    }

    expect(request.mock.calls.map(([name]) => name)).toEqual(['tankstat-outbox', 'tankstat-outbox'])
    expect(outbox.changes).toEqual([])
  })
})

describe('marks of a visit', () => {
  it('marks each of its schedules done, and its expense new while it logs one', async () => {
    await outbox.enqueue(visit('d1', 'e1', ['s1', 's2'], { amount: 50 }))
    await outbox.enqueue(visit('d2', 'e2', ['s3'])) // no amount: no expense
    await outbox.enqueue(visit('d3', 'e3', undefined, { amount: 10 })) // names no schedules
    await outbox.enqueue({ id: 'u1', entity: 'recurring', action: 'update', vehicleId: 'v1', targetId: 's4', input: { id: 's4', title: 'Tyres' } })

    expect(Object.fromEntries(outbox.marksOf('recurring'))).toEqual({ s1: 'done', s2: 'done', s3: 'done', s4: 'changed' })
    expect(Object.fromEntries(outbox.marksOf('expenses'))).toEqual({ e1: 'new', e3: 'new' })
    expect(outbox.markOf('expenses', 'e2')).toBeNull()
  })
})

describe('changes an older build kept', () => {
  it('a tyre change under an action this build no longer has is left out, and the others are sent as always (#288)', async () => {
    const rows = (await deviceData.rows())!
    await rows.putChanges([
      { id: 'old', seq: 1, createdAt: 1, entity: 'tyres', action: 'addTyreStretch' as never, vehicleId: 'v1', targetId: 's1', input: { id: 's1', setId: 'set' } },
      { ...log('r1'), seq: 2, createdAt: 2 },
    ])

    await outbox.reload()

    expect(outbox.changes.map((c) => c.id)).toEqual(['r1'])
  })
})
