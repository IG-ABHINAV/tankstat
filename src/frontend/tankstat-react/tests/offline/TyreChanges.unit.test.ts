import { describe, expect, it } from 'vitest'
import { collapse, discard, type Change } from '../../src/offline/changes.ts'
import { batches, toChangeInput } from '../../src/offline/push.ts'
import { fromParked } from '../../src/offline/syncKinds.ts'
import { tyreCreates, tyreFeeExpense, tyreNeeds, tyreTarget } from '../../src/offline/tyreChanges.ts'

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

  it('a tyre log line (#288): an add creates it (and the expense its amount makes), a change of a swap’s line needs the swap, a trash says whether its expense goes too', () => {
    const addLine = tyre('addTyreEntry', { vehicleId: 'v1', id: 'e1', kind: 'NOTE', setId: 'n', day: '2026-11-12', note: 'x' }, 'e1')
    const swapLine = tyre('updateTyreEntry', { id: 'st', kind: 'SWAP', setId: 'n', day: '2026-11-11' }, 'st')
    const trashLine = tyre('deleteTyreEntry', { id: 'e1', withExpense: true }, 'e1')
    expect([tyreTarget('addTyreEntry', addLine.input), tyreCreates('addTyreEntry', addLine.input), tyreNeeds('addTyreEntry', addLine.input)]).toEqual(['e1', ['e1'], ['n']])
    expect([tyreTarget('updateTyreEntry', swapLine.input), tyreCreates('updateTyreEntry', swapLine.input), tyreNeeds('updateTyreEntry', swapLine.input)]).toEqual(['st', [], ['st', 'n']])
    expect(toChangeInput({ ...trashLine, expectedVersion: 2 })).toEqual({ id: trashLine.id, expectedVersion: 2, deleteTyreEntry: { id: 'e1', withExpense: true } })
    const paid = { vehicleId: 'v1', id: 'e2', kind: 'OTHER', day: '2026-11-12', fee: { amount: 25, currency: 'EUR', title: 'Other', expenseId: 'x2' } }
    expect(tyreCreates('addTyreEntry', paid)).toEqual(['e2', 'x2'])
    expect(tyreCreates('swapTyres', { ...swap.input, fee: { amount: 60, expenseId: 'x3' } })).toEqual(['st', 'x3'])
    expect(tyreCreates('addTyreSet', { ...addSet.input, price: { amount: 480, expenseId: 'x4' }, boughtEntryId: 'b4' })).toEqual(['n', 'ns', 'b4', 'x4'])
    expect(discard([addSet, swap, swapLine, addLine, trashLine, other], swap.id)).toEqual([addSet, addLine, trashLine, other]) // the swap's line goes with it
    expect(discard([addSet, swap, swapLine, addLine, trashLine, other], addLine.id)).toEqual([addSet, swap, swapLine, other])
    // The expense the amount made goes too, with a waiting edit of it on the Expenses tab.
    const paidLine = tyre('addTyreEntry', { vehicleId: 'v1', id: 'e3', kind: 'OTHER', day: '2026-11-12', fee: { amount: 9, expenseId: 'x3' } }, 'e3')
    const expenseEdit: Change = { id: 'c-edit', seq: 900, createdAt: 900, entity: 'expenses', action: 'update', vehicleId: 'v1', targetId: 'x3', input: { amount: 10 } }
    expect(discard([paidLine, expenseEdit, other], paidLine.id)).toEqual([other])
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

describe('the expense a waiting amount makes (#288)', () => {
  const fee = { amount: 60, currency: 'EUR', category: 'Tyres', title: 'Tyre swap: Nokian', expenseId: 'x1' }

  it('a swap, a change of positions and a set bought with a price: on the line each makes, never an edit', () => {
    expect(tyreFeeExpense('swapTyres', { ...swap.input, odometer: 150_000, fee })).toEqual({
      expenseId: 'x1', lineId: 'st', kind: 'SWAP', day: '2026-11-10', odometer: 150_000, title: 'Tyre swap: Nokian', category: 'Tyres', amount: 60, currency: 'EUR', edit: false,
    })
    expect(tyreFeeExpense('changeTyrePositions', { ...positions.input, fee })).toMatchObject({ lineId: 'st2', kind: 'POSITIONS_CHANGED', day: '2026-11-12', odometer: null, edit: false })
    expect(tyreFeeExpense('addTyreSet', { ...addSet.input, price: { ...fee, amount: 480 }, boughtEntryId: 'b1', boughtOn: '2026-10-01' }))
      .toMatchObject({ lineId: 'b1', kind: 'BOUGHT', day: '2026-10-01', amount: 480, odometer: null, edit: false })
    expect(tyreFeeExpense('addTyreSet', { ...addSet.input, price: fee, boughtEntryId: 'b1' })).toMatchObject({ day: '2026-11-10' }) // bought the day it went on
  })

  it('an edit of a line is one; nothing without an amount, without its id, or for a change that takes none', () => {
    expect(tyreFeeExpense('updateTyreEntry', { id: 'e1', kind: 'STORAGE_FEE', day: '2026-11-01', fee })).toMatchObject({ lineId: 'e1', kind: 'STORAGE_FEE', edit: true })
    expect(tyreFeeExpense('addTyreEntry', { id: 'e1', fee: { ...fee, amount: null } })).toBeNull()
    expect(tyreFeeExpense('addTyreEntry', { id: 'e1', fee: { ...fee, expenseId: undefined } })).toBeNull()
    expect(tyreFeeExpense('addTyreEntry', { id: 'e1' })).toBeNull()
    expect(tyreFeeExpense('deleteTyreSet', { id: 'n', fee })).toBeNull()
  })

  it('past swaps and an installation create their installations', () => {
    expect(tyreCreates('addPastTyreSwaps', { vehicleId: 'v1', swaps: [{ setId: 'n', installationId: 'p1' }, { setId: 'n', installationId: 'p2' }] })).toEqual(['p1', 'p2'])
    expect(tyreCreates('addTyreInstallation', { id: 'i1', setId: 'n' })).toEqual(['i1'])
  })
})
