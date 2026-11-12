import { describe, expect, it } from 'vitest'
import { collapse, discard, type Change } from '../../src/offline/changes.ts'
import { batches, toChangeInput } from '../../src/offline/push.ts'
import { fromParked } from '../../src/offline/syncKinds.ts'
import { sentInput, tyreCreates, tyreFeeExpense, tyreNeeds, tyreTarget } from '../../src/offline/tyreChanges.ts'

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

describe('single tyres kept on the device (#289)', () => {
  const breakUp = tyre('breakUpTyreSet', { setId: 'n', day: '2026-11-10', tyres: [{ id: 't1', label: 'W1', corner: 'FRONT_LEFT' }, { id: 't2', label: 'W2', corner: null }] }, 'n')
  const placeEdit = tyre('updateTyrePlace', { id: 'p1', from: '2026-11-10', until: null, corner: 'REAR_LEFT', tyreId: 't1', installationId: 'st' }, 'p1')

  it('a break-up makes its tyres; a time on the car needs its tyre and installation, kept beside it and never sent', () => {
    expect([tyreTarget('breakUpTyreSet', breakUp.input), tyreCreates('breakUpTyreSet', breakUp.input), tyreNeeds('breakUpTyreSet', breakUp.input)]).toEqual(['n', ['t1', 't2'], ['n']])
    expect(tyreNeeds('updateTyrePlace', placeEdit.input)).toEqual(['p1', 't1', 'st'])
    expect(toChangeInput({ ...placeEdit, expectedVersion: null })).toEqual({
      id: placeEdit.id, expectedVersion: null, updateTyrePlace: { id: 'p1', from: '2026-11-10', until: null, corner: 'REAR_LEFT' },
    })
    expect(sentInput('updateTyrePlace', placeEdit.input)).not.toHaveProperty('tyreId')
    const added = tyre('addTyrePlace', { id: 'p9', tyreId: 't2', from: '2026-12-01', corner: null }, 'p9')
    expect([tyreCreates('addTyrePlace', added.input), tyreNeeds('addTyrePlace', added.input)]).toEqual([['p9'], ['t2']])
  })

  it('a swap names the tyres it places, and sends their corners', () => {
    const placed = tyre('swapTyres', { ...swap.input, corners: [{ tyreId: 't1', corner: 'REAR_LEFT' }] }, 'st')
    expect(tyreNeeds('swapTyres', placed.input)).toEqual(['n', 't1'])
    expect(toChangeInput({ ...placed, expectedVersion: null })).toMatchObject({ swapTyres: { corners: [{ tyreId: 't1', corner: 'REAR_LEFT' }] } })
  })

  it('taking a break-up back takes along what was made on its tyres', () => {
    expect(discard([breakUp, placeEdit], breakUp.id)).toEqual([])
  })

  it('an entry for several tyres makes a line each (and a whole set’s tyres it breaks up); an edit needs the other lines it changes', () => {
    const repair = tyre('addTyreEntry', {
      id: 'l3', vehicleId: 'v1', kind: 'PUNCTURE_REPAIRED', day: '2026-11-12', setId: 'n', tyres: [{ tyreId: 't3', id: 'l3' }, { tyreId: 't4', id: 'l4' }],
      breakUp: { setId: 'n', day: '2026-11-12', tyres: ['t1', 't2', 't3', 't4'].map((id) => ({ id, label: id.toUpperCase(), corner: null })) },
      fee: { amount: 13_000, currency: 'HUF', category: 'Tyres', title: 'Puncture repaired', expenseId: 'x1' },
    }, 'l3')
    expect(tyreCreates('addTyreEntry', repair.input)).toEqual(['l3', 'l3', 'l4', 't1', 't2', 't3', 't4', 'x1'])
    expect(tyreNeeds('addTyreEntry', repair.input)).toEqual(['n'])
    expect(tyreFeeExpense('addTyreEntry', repair.input)).toMatchObject({ expenseId: 'x1', lineId: 'l3', amount: 13_000 })
    const both = tyre('updateTyreEntry', { id: 'l3', kind: 'PUNCTURE_REPAIRED', day: '2026-11-13', setId: 'n', also: ['l4'], sharedExpense: null }, 'l3')
    expect(tyreNeeds('updateTyreEntry', both.input)).toEqual(['l3', 'n', 'l4'])
    expect(toChangeInput({ ...both, expectedVersion: null })).toMatchObject({ updateTyreEntry: { also: ['l4'] } })

    // Taken back, it takes along what was made on its lines and tyres.
    const onS4 = tyre('updateTyreEntry', { id: 'l4', kind: 'PUNCTURE_REPAIRED', day: '2026-11-12', setId: 'n', note: 'Rear right' }, 'l4')
    const onT1 = tyre('updateTyre', { id: 't1', label: 'W1' }, 't1')
    expect(discard([repair, onS4, onT1, other], repair.id)).toEqual([other])
  })

  it('a trash or restore of a tyre or a time on the car is sent as its bare id', () => {
    expect(toChangeInput({ ...tyre('deleteTyre', { id: 't1' }, 't1'), expectedVersion: 2 })).toMatchObject({ deleteTyre: 't1' })
    expect(toChangeInput({ ...tyre('deleteTyrePlace', { id: 'p1', tyreId: 't1', installationId: 'st' }, 'p1'), expectedVersion: 2 })).toMatchObject({ deleteTyrePlace: 'p1' })
  })
})
