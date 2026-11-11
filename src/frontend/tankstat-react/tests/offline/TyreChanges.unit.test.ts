import { describe, expect, it } from 'vitest'
import { collapse, discard, type Change } from '../../src/offline/changes.ts'
import { batches, toChangeInput } from '../../src/offline/push.ts'
import { fromParked } from '../../src/offline/syncKinds.ts'
import { tyreCreates, tyreNeeds, tyreTarget } from '../../src/offline/tyreChanges.ts'

let seq = 0
const tyre = (action: Change['action'], input: Record<string, unknown>, targetId = ''): Change => ({ id: `c${++seq}`, seq, createdAt: seq, entity: 'tyres', action, vehicleId: 'v1', targetId, input })

const addSet = tyre('addTyreSet', { id: 'n', installationId: 'ns', vehicleId: 'v1', name: 'Nokian', season: 'WINTER', since: '2026-11-01', fitted: false, day: '2026-11-10', roughDistance: 0, replace: false }, 'n')
const swap = tyre('swapTyres', { vehicleId: 'v1', setId: 'n', day: '2026-11-10', pairOneFront: null, replace: false, installationId: 'st' }, 'st')
const positions = tyre('changeTyrePositions', { vehicleId: 'v1', setId: 'n', day: '2026-11-12', pairOneFront: false, replace: false, installationId: 'st2' }, 'st2')
const trashInstallation = tyre('deleteTyreInstallation', { id: 'st' }, 'st')
const other = tyre('updateTyreSet', { id: 's1', name: 'Michelin', season: 'SUMMER', since: '2023-03-01' }, 's1')

describe('tyre changes kept on the device (#287)', () => {
  it('never fold: each waits as it was made, in order', () => {
    const waiting = [addSet, swap].reduce<Change[]>((all, c) => collapse(all, c), [])
    expect(collapse(waiting, tyre('swapTyres', { ...swap.input, day: '2026-11-11' }, 'st'))).toHaveLength(3)
  })

  it('taking back an add takes along what was made on it, and on that in turn', () => {
    expect(discard([addSet, swap, trashInstallation, other], addSet.id)).toEqual([other])
    expect(discard([addSet, swap, trashInstallation, other], swap.id)).toEqual([addSet, other])
  })

  it('go to the server as their mutation’s input, a bare id where it takes one, never in the request of the add they need', () => {
    expect(toChangeInput({ ...swap, expectedVersion: null })).toEqual({ id: swap.id, expectedVersion: null, swapTyres: swap.input })
    expect(toChangeInput({ ...trashInstallation, expectedVersion: 4 })).toEqual({ id: trashInstallation.id, expectedVersion: 4, deleteTyreInstallation: 'st' })
    expect(batches([addSet, swap, positions, other]).map((b) => b.map((c) => c.action))).toEqual([['addTyreSet'], ['swapTyres', 'changeTyrePositions', 'updateTyreSet']])
  })

  it('a tyre log line (#288): an add creates it, a change of a swap’s line needs the swap that made it, a trash goes as a bare id', () => {
    const addLine = tyre('addTyreEntry', { vehicleId: 'v1', id: 'e1', kind: 'NOTE', setId: 'n', day: '2026-11-12', note: 'x' }, 'e1')
    const swapLine = tyre('updateTyreEntry', { id: 'st', kind: 'SWAP', setId: 'n', day: '2026-11-11' }, 'st')
    const trashLine = tyre('deleteTyreEntry', { id: 'e1' }, 'e1')
    expect([tyreTarget('addTyreEntry', addLine.input), tyreCreates('addTyreEntry', addLine.input), tyreNeeds('addTyreEntry', addLine.input)]).toEqual(['e1', ['e1'], ['n']])
    expect([tyreTarget('updateTyreEntry', swapLine.input), tyreCreates('updateTyreEntry', swapLine.input), tyreNeeds('updateTyreEntry', swapLine.input)]).toEqual(['st', [], ['st', 'n']])
    expect(toChangeInput({ ...trashLine, expectedVersion: 2 })).toEqual({ id: trashLine.id, expectedVersion: 2, deleteTyreEntry: 'e1' })
    expect(discard([addSet, swap, swapLine, addLine, trashLine, other], swap.id)).toEqual([addSet, addLine, trashLine, other]) // the swap's line goes with it
    expect(discard([addSet, swap, swapLine, addLine, trashLine, other], addLine.id)).toEqual([addSet, swap, swapLine, other])
  })

  it('come back from the server parked as the change they were', () => {
    const parked = (kind: string, change: Record<string, unknown>) =>
      fromParked({ id: 'p1', kind: kind as never, vehicleId: 'v1', targetId: null, change: JSON.stringify(change), receivedAt: '2026-11-10T10:00:00Z' })
    expect(parked('SWAP_TYRES', { id: 'p1', swapTyres: swap.input })).toMatchObject({ entity: 'tyres', action: 'swapTyres', targetId: 'st', input: swap.input })
    expect(parked('DELETE_TYRE_INSTALLATION', { id: 'p1', expectedVersion: 4, deleteTyreInstallation: 'st' })).toMatchObject({ action: 'deleteTyreInstallation', targetId: 'st', input: { id: 'st' }, expectedVersion: 4 })
    expect(parked('UPDATE_TYRE_ENTRY', { id: 'p1', expectedVersion: 2, updateTyreEntry: { id: 'e1', kind: 'NOTE', day: '2026-11-12' } })).toMatchObject({ action: 'updateTyreEntry', targetId: 'e1', expectedVersion: 2 })
    expect(parked('SET_TYRE_LOG_DAY', { id: 'p1', setTyreLogDay: { day: '2026-11-12' } })).toBeNull() // an older build's kind: not known here
  })
})
