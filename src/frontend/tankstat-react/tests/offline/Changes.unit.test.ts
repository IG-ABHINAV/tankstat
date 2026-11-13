import { describe, expect, it } from 'vitest'
import { collapse, discard, keptPhotosOf, markOf, type Change } from '../../src/offline/changes.ts'

let seq = 0
const change = (action: Change['action'], targetId = 'r1', over: Partial<Change> = {}): Change => ({
  id: `${action}-${++seq}`,
  seq,
  createdAt: seq,
  entity: 'refuelings',
  action,
  vehicleId: 'v1',
  targetId,
  ...over,
})
const add = (id = 'r1', input: Record<string, unknown> = { id, vehicleId: 'v1', date: '2026-10-01', volume: 40, totalCost: 100, currency: 'EUR' }) =>
  change('add', id, { id, input })
const fold = (...changes: Change[]) => changes.reduce<Change[]>((list, c) => collapse(list, c), [])

describe('collapse', () => {
  it('an add then an update is the add with the new values', () => {
    const [only, ...rest] = fold(add(), change('update', 'r1', { input: { id: 'r1', date: '2026-10-02', volume: 41, note: 'x' } }))
    expect(rest).toEqual([])
    expect(only).toMatchObject({ action: 'add', id: 'r1', input: { id: 'r1', vehicleId: 'v1', date: '2026-10-02', volume: 41, note: 'x', totalCost: 100 } })
  })

  it("an expense's tyre details edited before it was sent fold into its add, and switched off they go (#290)", () => {
    const tyre = { kind: 'PUNCTURE_REPAIRED', setId: 's1', lineId: 'l1' }
    const expenseAdd = change('add', 'e1', { id: 'e1', entity: 'expenses', input: { id: 'e1', vehicleId: 'v1', date: '2026-10-01', title: 'Puncture', amount: 25, currency: 'EUR', tyre } })
    const edited = fold(expenseAdd, change('update', 'e1', { entity: 'expenses', input: { id: 'e1', title: 'Valve', tyre: { ...tyre, kind: 'VALVE_REPLACED' } } }))
    expect(edited).toEqual([expect.objectContaining({ action: 'add', input: expect.objectContaining({ title: 'Valve', tyre: { ...tyre, kind: 'VALVE_REPLACED' } }) })])
    const off = fold(expenseAdd, change('update', 'e1', { entity: 'expenses', input: { id: 'e1', title: 'Puncture', tyre: null } }))
    expect(off[0].input).toMatchObject({ tyre: null })
  })

  it('an add then a trash is nothing at all, whatever came in between', () => {
    expect(fold(add(), change('update', 'r1', { input: { volume: 2 } }), change('trash'))).toEqual([])
  })

  it('updates fold into the last, made from the first one’s version', () => {
    const first = change('update', 'r1', { input: { volume: 1 }, expectedVersion: 3 })
    const [only, ...rest] = fold(first, change('update', 'r1', { input: { volume: 2 }, expectedVersion: 4 }))
    expect(rest).toEqual([])
    expect(only).toMatchObject({ id: first.id, seq: first.seq, action: 'update', input: { volume: 2 }, expectedVersion: 3 })
  })

  it('an update then a trash is the trash, from the update’s version', () => {
    const update = change('update', 'r1', { input: { volume: 1 }, expectedVersion: 3 })
    const [only] = fold(update, change('trash', 'r1', { expectedVersion: 9 }))
    expect(only).toMatchObject({ id: update.id, action: 'trash', expectedVersion: 3 })
  })

  it('a trash then a restore, or a restore then a trash, is nothing', () => {
    expect(fold(change('trash'), change('restore'))).toEqual([])
    expect(fold(change('restore'), change('trash'))).toEqual([])
  })

  it('a second trash or restore, or an edit of a log on its way to the trash, changes nothing', () => {
    const trash = change('trash', 'r1', { expectedVersion: 2 })
    expect(fold(trash, change('trash'))).toEqual([trash])
    expect(fold(trash, change('update', 'r1', { input: { volume: 3 } }))).toEqual([trash])
    const restore = change('restore', 'r2')
    expect(fold(restore, change('restore', 'r2'))).toEqual([restore])
  })

  it('keeps other logs’ changes, and their order', () => {
    const a = add('r1')
    const b = change('trash', 'r2')
    const c = change('update', 'r3', { input: { volume: 1 } })
    expect(fold(a, b, c, change('update', 'r1', { input: { volume: 9 } })).map((x) => x.targetId)).toEqual(['r1', 'r2', 'r3'])
    expect(fold(a, b, c, change('trash', 'r1')).map((x) => x.targetId)).toEqual(['r2', 'r3'])
  })

  it('tells a refuelling from an expense with the same id', () => {
    const list = fold(add('x'), change('trash', 'x', { entity: 'expenses' }))
    expect(list.map((c) => `${c.entity}:${c.action}`)).toEqual(['refuelings:add', 'expenses:trash'])
  })
})

describe('collapse, keeping what was edited', () => {
  it('an edit, a trash and a restore leave the edit (Undo after trashing an edited log)', () => {
    const update = change('update', 'r1', { input: { volume: 41 }, expectedVersion: 3 })
    const [trashed] = fold(update, change('trash'))
    expect(trashed).toMatchObject({ action: 'trash', expectedVersion: 3 })

    const [back, ...rest] = collapse([trashed], change('restore'))
    expect(rest).toEqual([])
    expect(back).toMatchObject({ id: update.id, action: 'update', input: { volume: 41 }, expectedVersion: 3 })
  })

  it('taking back a trash that took the place of an edit (Keep, Remove) leaves the edit', () => {
    const [trashed] = fold(change('update', 'r1', { input: { volume: 41 }, expectedVersion: 3 }), change('trash'))

    expect(discard([trashed], trashed.id)).toEqual([expect.objectContaining({ action: 'update', input: { volume: 41 } })])
  })
})

describe('collapse, keeping what a change was made from', () => {
  it('folded edits keep the first one’s base, and so does the trash that takes their place, and the edit it gives back', () => {
    const first = change('update', 'r1', { input: { volume: 1 }, expectedVersion: 3, base: { volume: 40, note: null } })
    const [edited] = fold(first, change('update', 'r1', { input: { volume: 2 }, expectedVersion: 4, base: { volume: 1 } }))
    expect(edited).toMatchObject({ input: { volume: 2 }, expectedVersion: 3, base: { volume: 40, note: null } })

    const [trash] = fold(first, change('trash', 'r1', { expectedVersion: 4, base: { volume: 1 } }))
    expect(trash).toMatchObject({ action: 'trash', expectedVersion: 3, base: { volume: 40, note: null } })
    expect(fold(first, change('trash', 'r1', { expectedVersion: 4 }), change('restore'))[0]).toMatchObject({ action: 'update', base: { volume: 40, note: null } })
  })

  it('an edit without a base folded into another stays without one, and an add never has one', () => {
    const [edited] = fold(change('update', 'r1', { input: { volume: 1 } }), change('update', 'r1', { input: { volume: 2 }, base: { volume: 1 } }))
    expect(edited.base).toBeUndefined()
    expect(fold(add(), change('update', 'r1', { input: { volume: 2 }, base: { volume: 40 } }))[0].base).toBeUndefined()
  })
})

describe('collapse, after a change that was sent', () => {
  it('never folds into it: an answer that was lost would make the server answer "done" for what folded in', () => {
    const sent = change('update', 'r1', { input: { volume: 41 }, expectedVersion: 3, sent: true })
    const list = fold(sent, change('update', 'r1', { input: { volume: 42 }, expectedVersion: 3 }))

    expect(list).toHaveLength(2)
    expect(list[0]).toEqual(sent)
    expect(list[1]).toMatchObject({ action: 'update', input: { volume: 42 }, expectedVersion: 4 }) // from the version the first leaves
  })

  it('an add that was sent is not taken back by a trash, and what follows it does not check a version', () => {
    const sentAdd = { ...add(), sent: true }
    const list = fold(sentAdd, change('update', 'r1', { input: { volume: 2 }, expectedVersion: 0 }), change('trash', 'r1', { expectedVersion: 0 }))

    expect(list).toEqual([sentAdd, expect.objectContaining({ action: 'trash', expectedVersion: null, input: { volume: 2 } })])
  })
})

describe('discard and markOf', () => {
  it('takes back one change, and says how its log is marked', () => {
    const list = fold(add('r1'), change('trash', 'r2'))
    expect(discard(list, 'r1').map((c) => c.targetId)).toEqual(['r2'])
    expect(list.map(markOf)).toEqual(['new', 'deleted'])
    expect(markOf(change('update'))).toBe('changed')
    expect(markOf(change('restore'))).toBe('restored')
  })
})

describe('vehicles, schedules and visits', () => {
  const vehicleAdd = (id = 'car') => change('add', id, { id, entity: 'vehicles', vehicleId: id, input: { id, name: 'Golf', fuelType: 'PETROL' } })
  const scheduleAdd = (id = 's1') => change('add', id, { id, entity: 'recurring', input: { id, title: 'Oil' } })
  const visit = (ids: string[], expenseId?: string) => change('markDone', expenseId ?? 'visit', { entity: 'recurring', targetIds: ids, input: { ids, expenseId } })

  it('a vehicle added here and removed takes everything made to it along, and nothing else', () => {
    const list = fold(vehicleAdd('car'), add('r1', { id: 'r1', vehicleId: 'car' }), change('trash', 'r9'), scheduleAdd('s1'))
    const onCar = list.map((c) => (c.targetId === 'r1' || c.targetId === 's1' ? { ...c, vehicleId: 'car' } : c))

    const after = collapse(onCar, change('trash', 'car', { entity: 'vehicles', vehicleId: 'car' }))

    expect(after.map((c) => c.targetId)).toEqual(['r9'])
  })

  it('a vehicle added then edited is the add with the new values', () => {
    const [only] = fold(vehicleAdd(), change('update', 'car', { entity: 'vehicles', vehicleId: 'car', input: { id: 'car', name: 'Golf GTI', fuelType: 'DIESEL' } }))
    expect(only).toMatchObject({ action: 'add', input: { name: 'Golf GTI', fuelType: 'DIESEL' } })
  })

  it('visits never fold, and a schedule added here and deleted leaves the visits waiting', () => {
    const list = fold(scheduleAdd('s1'), visit(['s1', 's2'], 'e1'), visit(['s1']))
    expect(list.filter((c) => c.action === 'markDone')).toHaveLength(2)

    const after = collapse(list, change('trash', 's1', { entity: 'recurring' }))

    expect(after.map((c) => [c.action, c.targetIds])).toEqual([['markDone', ['s2']]]) // the visit of s1 alone is gone
    expect(after[0].input).toMatchObject({ ids: ['s2'], expenseId: 'e1' })
    expect(markOf(after[0])).toBe('done')
  })

  const paidVisit = (expenseId: string) => change('markDone', expenseId, { entity: 'recurring', targetIds: ['s1'], input: { ids: ['s1'], expenseId, amount: 80, currency: 'EUR', photoIds: ['local:a'] } })
  const ofExpense = (action: Change['action'], id: string, over: Partial<Change> = {}) => change(action, id, { entity: 'expenses', ...over })

  it('the expense of a visit waiting here, trashed: the visit logs none, and what was made to it goes too', () => {
    const list = fold(paidVisit('e1'), ofExpense('update', 'e1', { input: { id: 'e1', note: 'x' }, expectedVersion: 1 }))
    expect(list).toHaveLength(2) // an edit goes after the visit, from the version the visit leaves

    const after = collapse(list, ofExpense('trash', 'e1', { expectedVersion: 1 }))

    expect(after).toHaveLength(1)
    expect(after[0]).toMatchObject({ action: 'markDone', targetIds: ['s1'], input: { amount: null, photoIds: null } })
  })

  it('a visit taken back takes what was made to its expense along', () => {
    const list = fold(paidVisit('e1'), ofExpense('update', 'e1', { input: { id: 'e1', note: 'x' } }), ofExpense('trash', 'e7'))
    const after = discard(list, list[0].id)
    expect(after.map((c) => c.targetId)).toEqual(['e7'])
  })
})

describe('a vehicle\'s picture', () => {
  const picture = (action: 'setPicture' | 'removePicture', vehicle = 'v1', over: Partial<Change> = {}) =>
    change(action, vehicle, { entity: 'vehicles', vehicleId: vehicle, ...(action === 'setPicture' ? { input: { key: `local:${seq + 1}` } } : {}), ...over })

  it('only the last change counts: a picture replaces the one waiting, a removal takes its place, a new picture after a removal is the picture', () => {
    const first = picture('setPicture')
    const second = picture('setPicture')
    expect(fold(first, second)).toEqual([second])
    const removed = picture('removePicture')
    expect(fold(first, removed)).toEqual([removed])
    expect(fold(removed, second)).toEqual([second])
    expect(keptPhotosOf(second)).toEqual([second.input!.key])
    expect(markOf(second)).toBe('changed')
  })

  it('never folds into the vehicle\'s other changes, nor into another vehicle\'s picture, nor into one that was sent', () => {
    const edit = change('update', 'v1', { entity: 'vehicles', input: { name: 'Golf' }, expectedVersion: 2 })
    const set = picture('setPicture')
    const other = picture('setPicture', 'v2')
    const sent = picture('setPicture', 'v1', { sent: true })
    expect(fold(edit, set, other)).toEqual([edit, set, other])
    expect(fold(sent, set)).toEqual([sent, set])
    expect(fold(set, change('update', 'v1', { entity: 'vehicles', input: { name: 'Polo' } }))).toHaveLength(2)
  })

  it('a picture changed while the vehicle waits to be trashed is left out: it could not be applied after the trash', () => {
    const trash = change('trash', 'v1', { entity: 'vehicles', vehicleId: 'v1', expectedVersion: 2 })
    expect(fold(trash, picture('setPicture'))).toEqual([trash])
    expect(fold(trash, picture('removePicture'))).toEqual([trash])
  })

  it('a vehicle added here and taken back takes its picture along', () => {
    const golf = change('add', 'g', { entity: 'vehicles', vehicleId: 'g', id: 'g', input: { id: 'g', name: 'Golf' } })
    expect(fold(golf, picture('setPicture', 'g'), change('trash', 'g', { entity: 'vehicles', vehicleId: 'g' }))).toEqual([])
  })
})

describe('photos of a log', () => {
  const addPhoto = (key: string, log = 'r1') => change('addPhoto', log, { input: { key } })
  const removePhoto = (imageId: string, log = 'r1') => change('removePhoto', log, { input: { imageId } })

  it('a photo added to a log that waits to be added joins its photos; taken back, it leaves them', () => {
    const [only, ...rest] = fold(add('r1', { id: 'r1', vehicleId: 'v1', photoIds: ['local:a'] }), addPhoto('local:b'))
    expect(rest).toEqual([])
    expect(only.input?.photoIds).toEqual(['local:a', 'local:b'])
    expect(keptPhotosOf(only)).toEqual(['local:a', 'local:b'])

    expect(collapse([only], removePhoto('local:a'))[0].input?.photoIds).toEqual(['local:b'])
  })

  it('on a saved log: a kept photo added then removed is nothing; a server photo removed twice waits once', () => {
    expect(fold(addPhoto('local:a'), removePhoto('local:a'))).toEqual([])
    const removed = fold(removePhoto('img1'), removePhoto('img1'))
    expect(removed.map((c) => [c.action, c.input?.imageId])).toEqual([['removePhoto', 'img1']])
    expect(markOf(removed[0])).toBe('changed')
  })

  it('a log on its way to the trash takes its photo changes along, and gets none after; a restore does not bring them back', () => {
    const trashed = fold(addPhoto('local:a'), removePhoto('img1'), change('trash'))
    expect(trashed.map((c) => c.action)).toEqual(['trash'])
    expect(collapse(trashed, addPhoto('local:b')).map((c) => c.action)).toEqual(['trash'])
    expect(collapse(trashed, change('restore'))).toEqual([])
  })

  it('photo changes of one log never fold into another log’s, nor into its edits', () => {
    const list = fold(change('update', 'r1', { input: { volume: 2 } }), addPhoto('local:a'), addPhoto('local:b', 'r2'))
    expect(list.map((c) => [c.action, c.targetId])).toEqual([['update', 'r1'], ['addPhoto', 'r1'], ['addPhoto', 'r2']])
  })
})
