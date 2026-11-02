import { describe, expect, it } from 'vitest'
import type { Change } from '../../src/offline/changes.ts'
import type { SyncChangeKind } from '../../src/gql/generated.ts'
import { batches, toChangeInput, withPhotosAlone } from '../../src/offline/push.ts'
import { canForce, fromParked, SYNC_KINDS } from '../../src/offline/syncKinds.ts'

let seq = 0
const change = (over: Partial<Change>): Change => ({ id: `c${++seq}`, seq, createdAt: seq, entity: 'refuelings', action: 'add', vehicleId: 'v1', targetId: `t${seq}`, ...over })

describe('toChangeInput', () => {
  it('sends each change as its one operation, with only what that input takes', () => {
    const add = change({ id: 'r1', targetId: 'r1', input: { id: 'r1', vehicleId: 'v1', date: '2026-10-01', volume: 30, totalCost: 90, currency: 'EUR', odometer: 2000, isFullTank: true, photoIds: [], unitPrice: 3 } })
    expect(toChangeInput(add)).toEqual({
      id: 'r1', expectedVersion: null,
      logRefueling: { id: 'r1', vehicleId: 'v1', date: '2026-10-01', volume: 30, totalCost: 90, currency: 'EUR', odometer: 2000, isFullTank: true, photoIds: [] },
    })
    expect(toChangeInput(change({ id: 'x', action: 'trash', targetId: 'r9', expectedVersion: 4 }))).toEqual({ id: 'x', expectedVersion: 4, deleteRefueling: 'r9' })
    expect(toChangeInput(change({ id: 'u', entity: 'vehicles', action: 'update', targetId: 'v1', expectedVersion: 2, input: { id: 'v1', name: 'Golf', fuelType: 'PETROL', units: { distance: 'KILOMETERS', volume: 'LITERS' } } })))
      .toEqual({ id: 'u', expectedVersion: 2, updateVehicle: { id: 'v1', name: 'Golf', fuelType: 'PETROL', units: { distance: 'KILOMETERS', volume: 'LITERS' } } })
    expect(toChangeInput(change({ id: 'd', entity: 'recurring', action: 'markDone', targetId: 'e1', targetIds: ['s1', 's2'], input: { ids: ['s1'], date: '2026-10-02', amount: 50, discount: 5, currency: 'EUR', title: 'Oil', category: '', expenseId: 'e1', photoIds: [] } })))
      .toEqual({ id: 'd', expectedVersion: null, markRecurringExpensesDone: { ids: ['s1', 's2'], date: '2026-10-02', amount: 50, discount: 5, currency: 'EUR', title: 'Oil', category: '', expenseId: 'e1', photoIds: [] } })
    // The discount of a log reaches the server with the change (the allowlists keep it).
    expect(toChangeInput(change({ id: 'e', entity: 'expenses', action: 'add', targetId: 'e2', input: { id: 'e2', vehicleId: 'v1', date: '2026-10-02', title: 'Tyres', amount: 18800, discount: 1200, currency: 'HUF', photoIds: [] } })))
      .toEqual({ id: 'e', expectedVersion: null, addExpense: { id: 'e2', vehicleId: 'v1', date: '2026-10-02', title: 'Tyres', amount: 18800, discount: 1200, currency: 'HUF', photoIds: [] } })
    expect(toChangeInput(change({ id: 'f', action: 'update', targetId: 'r1', expectedVersion: 3, input: { id: 'r1', date: '2026-10-01', volume: 43, totalCost: 34744, discount: 430, currency: 'HUF', odometer: 2000, isFullTank: true } })))
      .toEqual({ id: 'f', expectedVersion: 3, updateRefueling: { id: 'r1', date: '2026-10-01', volume: 43, totalCost: 34744, discount: 430, currency: 'HUF', odometer: 2000, isFullTank: true } })
    expect(toChangeInput(change({ id: 's', entity: 'recurring', action: 'trash', targetId: 's3' }))).toEqual({ id: 's', expectedVersion: null, deleteRecurringExpense: 's3' })
  })

  it('a visit that names no schedules marks none, and a photo change whose photo did not go up names no draft', () => {
    expect(toChangeInput(change({ id: 'd', entity: 'recurring', action: 'markDone', targetId: 'e1', input: { date: '2026-10-02' } })))
      .toEqual({ id: 'd', expectedVersion: null, markRecurringExpensesDone: { ids: [], date: '2026-10-02' } })
    expect(toChangeInput(change({ id: 'p', action: 'addPhoto', targetId: 'r1' }))).toEqual({ id: 'p', expectedVersion: null, addRefuelingPhoto: { logId: 'r1', draftId: undefined } })
    expect(toChangeInput(change({ id: 'q', entity: 'expenses', action: 'removePhoto', targetId: 'e1' }))).toEqual({ id: 'q', expectedVersion: null, removeExpensePhoto: { logId: 'e1', imageId: undefined } })
  })

  it('sets a vehicle\'s picture by the draft its kept picture went up as, and removes it by the vehicle', () => {
    const drafts = new Map([['local:pic', 'draft-pic']])
    expect(toChangeInput(change({ id: 'p', entity: 'vehicles', action: 'setPicture', vehicleId: 'v2', targetId: 'v2', input: { key: 'local:pic' } }), drafts))
      .toEqual({ id: 'p', expectedVersion: null, setVehiclePicture: { vehicleId: 'v2', draftId: 'draft-pic' } })
    expect(toChangeInput(change({ id: 'r', entity: 'vehicles', action: 'removePicture', vehicleId: 'v2', targetId: 'v2' })))
      .toEqual({ id: 'r', expectedVersion: null, removeVehiclePicture: 'v2' })
  })

  it('refuses a change no operation of the server stands for', () => {
    expect(() => toChangeInput(change({ entity: 'vehicles', action: 'markDone' }))).toThrow('A change of vehicles cannot markDone.')
  })
})

describe('photos kept on this device', () => {
  const drafts = new Map([['local:a', 'draft-a'], ['local:b', 'draft-b']])

  it('are sent as the drafts they went up as; one without a draft is left out', () => {
    const add = change({ id: 'r1', targetId: 'r1', input: { id: 'r1', vehicleId: 'v1', date: '2026-10-01', photoIds: ['local:a', 'local:gone', 'srv1'] } })
    expect(toChangeInput(add, drafts).logRefueling).toMatchObject({ photoIds: ['draft-a', 'srv1'] })
    expect(toChangeInput(change({ id: 'p', entity: 'expenses', action: 'addPhoto', targetId: 'e1', input: { key: 'local:b' } }), drafts))
      .toEqual({ id: 'p', expectedVersion: null, addExpensePhoto: { logId: 'e1', draftId: 'draft-b' } })
    expect(toChangeInput(change({ id: 'q', action: 'removePhoto', targetId: 'r1', input: { imageId: 'img9' } })))
      .toEqual({ id: 'q', expectedVersion: null, removeRefuelingPhoto: { logId: 'r1', imageId: 'img9' } })
  })

  it('a change that carries some goes in a request of its own; the others stay together, in order', () => {
    const plain = (id: string) => change({ id })
    const withPhoto = change({ id: 'x', input: { photoIds: ['local:a'] } })
    expect(withPhotosAlone([plain('1'), plain('2'), withPhoto, plain('3')]).map((g) => g.map((c) => c.id))).toEqual([['1', '2'], ['x'], ['3']])
  })
})

describe('batches', () => {
  it('sends the vehicles added here first, the rest in order, and never a change with the add it depends on', () => {
    const log = change({ id: 'log', vehicleId: 'golf' })
    const other = change({ id: 'other', action: 'trash', targetId: 'r1' })
    const golf = change({ id: 'golf', entity: 'vehicles', action: 'add', vehicleId: 'golf', targetId: 'golf' })
    const schedule = change({ id: 's1', entity: 'recurring', action: 'add', targetId: 's1' })
    const visit = change({ id: 'visit', entity: 'recurring', action: 'markDone', targetId: 'e1', targetIds: ['s1'] })

    expect(batches([log, other, golf, schedule, visit]).map((b) => b.map((c) => c.id))).toEqual([['golf'], ['log', 'other', 's1'], ['visit']])
  })

  it('cuts requests at the size given', () => {
    const many = Array.from({ length: 5 }, () => change({ action: 'trash' }))
    expect(batches(many, 2).map((b) => b.length)).toEqual([2, 2, 1])
  })

  it('vehicles added here alone make their own requests and nothing after them', () => {
    const golf = change({ id: 'golf', entity: 'vehicles', action: 'add', vehicleId: 'golf', targetId: 'golf' })
    expect(batches([golf]).map((b) => b.map((c) => c.id))).toEqual([['golf']])
    expect(batches([])).toEqual([])
  })
})

describe('fromParked', () => {
  const kindOf = (c: Change) => (Object.keys(SYNC_KINDS) as SyncChangeKind[]).find((k) => SYNC_KINDS[k][0] === c.entity && SYNC_KINDS[k][1] === c.action)!
  // What the server keeps: the ChangeInput as sent, with nulls for what was left out (an edited change is written back by the server).
  const asKept = (c: Change, nulls: boolean) => JSON.stringify(toChangeInput(c), (_, v: unknown) => (v === undefined ? (nulls ? null : undefined) : v))

  it.each([false, true])('reads every kind of change the server parked back as a change of this device (nulls kept: %s)', (nulls) => {
    const all: Change[] = [
      change({ id: 'r1', action: 'add', targetId: 'r1', input: { id: 'r1', vehicleId: 'v1', date: '2026-10-01', volume: 40, totalCost: 60, currency: 'EUR', odometer: 1200, isFullTank: true } }),
      change({ id: 'u1', action: 'update', targetId: 'r1', expectedVersion: 2, input: { id: 'r1', date: '2026-10-01', volume: 41, totalCost: 60, currency: 'EUR', odometer: 1200, isFullTank: true } }),
      ...(['trash', 'restore'] as const).flatMap((action) => [
        change({ id: `r-${action}`, action, targetId: 'r9', expectedVersion: 3 }),
        change({ id: `e-${action}`, entity: 'expenses', action, targetId: 'e9', expectedVersion: 3 }),
        change({ id: `v-${action}`, entity: 'vehicles', action, vehicleId: 'v2', targetId: 'v2', expectedVersion: 3 }),
      ]),
      change({ id: 'e1', entity: 'expenses', action: 'add', targetId: 'e1', input: { id: 'e1', vehicleId: 'v1', date: '2026-10-02', title: 'Oil', amount: 30, currency: 'EUR' } }),
      change({ id: 'e2', entity: 'expenses', action: 'update', targetId: 'e1', expectedVersion: 1, input: { id: 'e1', date: '2026-10-02', title: 'Oil', amount: 35, currency: 'EUR' } }),
      change({ id: 'g', entity: 'vehicles', action: 'add', vehicleId: 'g', targetId: 'g', input: { id: 'g', name: 'Golf', fuelType: 'PETROL' } }),
      change({ id: 'g2', entity: 'vehicles', action: 'update', vehicleId: 'g', targetId: 'g', expectedVersion: 1, input: { id: 'g', name: 'Golf GTI', fuelType: 'PETROL' } }),
      change({ id: 's1', entity: 'recurring', action: 'add', targetId: 's1', input: { id: 's1', vehicleId: 'v1', title: 'Service', kind: 'TIME', intervalMonths: 12 } }),
      change({ id: 's2', entity: 'recurring', action: 'update', targetId: 's1', expectedVersion: 1, input: { id: 's1', title: 'Service', kind: 'TIME', intervalMonths: 6 } }),
      change({ id: 's3', entity: 'recurring', action: 'trash', targetId: 's1' }),
      change({ id: 'd', entity: 'recurring', action: 'markDone', targetId: 'x1', targetIds: ['s1', 's2'], input: { ids: ['s1', 's2'], date: '2026-10-02', amount: 50, currency: 'EUR', expenseId: 'x1' } }),
      // A picture as the server keeps it: by the draft it went up as.
      change({ id: 'p1', entity: 'vehicles', action: 'setPicture', vehicleId: 'v2', targetId: 'v2', input: { draftId: 'draft-p' } }),
      change({ id: 'p2', entity: 'vehicles', action: 'removePicture', vehicleId: 'v2', targetId: 'v2' }),
    ]
    expect(new Set(all.map(kindOf)).size).toBe(18)

    for (const c of all) {
      const back = fromParked({ id: c.id, kind: kindOf(c), vehicleId: c.vehicleId, targetId: c.targetId, change: asKept(c, nulls), receivedAt: '2026-10-03T08:00:00Z' })
      expect(back).toMatchObject({ id: c.id, entity: c.entity, action: c.action, vehicleId: c.vehicleId, targetId: c.targetId, expectedVersion: c.expectedVersion ?? null })
      expect(toChangeInput(back)).toEqual(toChangeInput(c)) // sent again, it would be the same change
    }
  })

  it('reads back the values a change was made from, sent beside it, and none from what is not an object', () => {
    const edit = change({ id: 'u', action: 'update', targetId: 'r1', expectedVersion: 2, input: { id: 'r1', volume: 41 }, base: { volume: 40, note: null } })
    const sent = toChangeInput(edit)
    expect(sent.base).toBe('{"volume":40,"note":null}')
    const parked = { id: 'u', kind: 'UPDATE_REFUELING' as const, vehicleId: 'v1', targetId: 'r1', change: JSON.stringify({ ...sent, base: undefined }), receivedAt: '2026-10-03T08:00:00Z' }
    expect(fromParked({ ...parked, base: sent.base }).base).toEqual({ volume: 40, note: null })
    expect(fromParked({ ...parked, base: '[1]' }).base).toBeUndefined()
    expect(fromParked({ ...parked, base: 'not json' }).base).toBeUndefined()
    expect(toChangeInput(change({ id: 't', action: 'trash', targetId: 'r1' }))).not.toHaveProperty('base')
  })

  it('a vehicle the server never took has no vehicle id of its own: its changes are named by the add', () => {
    const add = change({ id: 'g', entity: 'vehicles', action: 'add', vehicleId: 'g', targetId: 'g', input: { id: 'g', name: 'Golf', fuelType: 'PETROL' } })
    expect(fromParked({ id: 'g', kind: 'ADD_VEHICLE', vehicleId: null, targetId: 'g', change: asKept(add, true), receivedAt: '2026-10-03T08:00:00Z' }).vehicleId).toBe('g')
  })

  it('offers Apply anyway unless what the change is about is gone or the person may not', () => {
    expect(['sync.versionMismatch', 'odometer.belowPrevious', 'log.valuesRequired'].every((key) => canForce(key))).toBe(true)
    expect(['refueling.notFound', 'vehicle.notFound', 'auth.forbidden', null].some((key) => canForce(key))).toBe(false)
  })

  it('to someone other than the sender, "not found" is no reason not to try: the sender may only have lost access', () => {
    expect(canForce('vehicle.notFound', false)).toBe(true)
    expect(canForce('auth.forbidden', false)).toBe(false)
  })

  it('to the sender, "not found" is no reason either while the server shows what it concerns (moved to the trash meanwhile)', () => {
    expect(canForce('refueling.notFound', true, { state: 'TRASHED' })).toBe(true)
    expect(canForce('refueling.notFound', true, null)).toBe(false)
    expect(canForce('auth.forbidden', true, { state: 'LIVE' })).toBe(false)
  })
})
