import { describe, expect, it } from 'vitest'
import { collapse, discard, type Change } from '../../src/offline/changes.ts'
import { batches, toChangeInput } from '../../src/offline/push.ts'
import { fromParked } from '../../src/offline/syncKinds.ts'

let seq = 0
const tyre = (action: Change['action'], input: Record<string, unknown>, targetId = ''): Change => ({ id: `c${++seq}`, seq, createdAt: seq, entity: 'tyres', action, vehicleId: 'v1', targetId, input })

const addSet = tyre('addTyreSet', { id: 'n', stretchId: 'ns', vehicleId: 'v1', name: 'Nokian', season: 'WINTER', since: '2026-11-01', fitted: false, day: '2026-11-10', roughDistance: 0, replace: false }, 'n')
const swap = tyre('swapTyres', { vehicleId: 'v1', setId: 'n', day: '2026-11-10', pairOneFront: null, replace: false, stretchId: 'st' }, 'st')
const positions = tyre('changeTyrePositions', { vehicleId: 'v1', setId: 'n', day: '2026-11-12', pairOneFront: false, replace: false, stretchId: 'st2' }, 'st2')
const trashStretch = tyre('deleteTyreStretch', { id: 'st' }, 'st')
const other = tyre('updateTyreSet', { id: 's1', name: 'Michelin', season: 'SUMMER', since: '2023-03-01' }, 's1')

describe('tyre changes kept on the device (#287)', () => {
  it('never fold: each waits as it was made, in order', () => {
    const waiting = [addSet, swap].reduce<Change[]>((all, c) => collapse(all, c), [])
    expect(collapse(waiting, tyre('swapTyres', { ...swap.input, day: '2026-11-11' }, 'st'))).toHaveLength(3)
  })

  it('taking back an add takes along what was made on it, and on that in turn', () => {
    expect(discard([addSet, swap, trashStretch, other], addSet.id)).toEqual([other])
    expect(discard([addSet, swap, trashStretch, other], swap.id)).toEqual([addSet, other])
  })

  it('go to the server as their mutation’s input, a bare id where it takes one, never in the request of the add they need', () => {
    expect(toChangeInput({ ...swap, expectedVersion: null })).toEqual({ id: swap.id, expectedVersion: null, swapTyres: swap.input })
    expect(toChangeInput({ ...trashStretch, expectedVersion: 4 })).toEqual({ id: trashStretch.id, expectedVersion: 4, deleteTyreStretch: 'st' })
    expect(batches([addSet, swap, positions, other]).map((b) => b.map((c) => c.action))).toEqual([['addTyreSet'], ['swapTyres', 'changeTyrePositions', 'updateTyreSet']])
  })

  it('come back from the server parked as the change they were', () => {
    const parked = (kind: string, change: Record<string, unknown>) =>
      fromParked({ id: 'p1', kind: kind as never, vehicleId: 'v1', targetId: null, change: JSON.stringify(change), receivedAt: '2026-11-10T10:00:00Z' })
    expect(parked('SWAP_TYRES', { id: 'p1', swapTyres: swap.input })).toMatchObject({ entity: 'tyres', action: 'swapTyres', targetId: 'st', input: swap.input })
    expect(parked('DELETE_TYRE_STRETCH', { id: 'p1', expectedVersion: 4, deleteTyreStretch: 'st' })).toMatchObject({ action: 'deleteTyreStretch', targetId: 'st', input: { id: 'st' }, expectedVersion: 4 })
  })
})
