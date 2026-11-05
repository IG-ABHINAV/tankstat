import { afterEach, describe, expect, it, vi } from 'vitest'
import { todayIso } from '../../src/forms/dates.ts'
import { connectivity } from '../../src/offline/connectivity.ts'
import { deviceData } from '../../src/offline/deviceData.ts'
import { memoryStorage, type LogRow, type RowStore, type VehicleRow } from '../../src/offline/deviceStorage.ts'
import { keptPhotos } from '../../src/offline/keptPhotos.ts'
import { answerLocally, sortLogs } from '../../src/offline/localResolvers.ts'
import { outbox, type ChangeDraft } from '../../src/offline/outbox.ts'
import fixture from '../../../../../tests/backend/contracts/log-order/log-order.json'

const vehicles = new Map<string, VehicleRow>([
  ['v1', { id: 'v1', name: 'Octavia', logAccess: 'DELETE' }],
  ['v2', { id: 'v2', name: 'astra', logAccess: 'EDIT' }],
])

const refueling = (id: string, fields: Partial<LogRow> = {}): LogRow => ({
  __typename: 'Refueling',
  id,
  vehicleId: 'v1',
  date: '2026-09-01',
  volume: 40,
  totalCost: 20000,
  discount: null,
  odometer: 1000,
  consumption: null,
  deletedAt: null,
  createdBy: { __typename: 'UserRef', id: 'u1', displayName: 'Alice', avatarUrl: null },
  pulledAt: 1,
  ...fields,
})

const ids = (rows: readonly LogRow[]) => rows.map((r) => r.id)

describe('sortLogs, like the repositories', () => {
  it('orders by the field, then the odometer the same way, then the id', () => {
    const rows = [
      refueling('b', { date: '2026-09-01', odometer: 1200 }),
      refueling('a', { date: '2026-09-01', odometer: 1200 }),
      refueling('c', { date: '2026-09-01', odometer: 1100 }),
      refueling('d', { date: '2026-09-05', odometer: 1300 }),
    ]

    expect(ids(sortLogs('refuelings', rows, 'DATE', 'DESC', vehicles))).toEqual(['d', 'a', 'b', 'c']) // the id breaks the tie upwards
    expect(ids(sortLogs('refuelings', rows, 'DATE', 'ASC', vehicles))).toEqual(['c', 'a', 'b', 'd'])
  })

  it('puts missing values first going up and last going down, but counts a missing cost, odometer or price as 0', () => {
    const rows = [refueling('a', { consumption: 6.5 }), refueling('b', { consumption: null }), refueling('c', { consumption: 5 })]
    expect(ids(sortLogs('refuelings', rows, 'CONSUMPTION', 'ASC', vehicles))).toEqual(['b', 'c', 'a'])
    expect(ids(sortLogs('refuelings', rows, 'CONSUMPTION', 'DESC', vehicles))).toEqual(['a', 'c', 'b'])

    const costs = [refueling('a', { totalCost: 10, volume: 2, odometer: 1 }), refueling('b', { totalCost: null, volume: null, odometer: 2 }), refueling('c', { totalCost: 30, volume: 2, odometer: 3 })]
    expect(ids(sortLogs('refuelings', costs, 'TOTAL_COST', 'ASC', vehicles))).toEqual(['b', 'a', 'c'])
    expect(ids(sortLogs('refuelings', costs, 'PRICE_PER_UNIT', 'DESC', vehicles))).toEqual(['c', 'a', 'b'])
  })

  it('orders names without regard to case, and an expense without a category as an empty one', () => {
    const rows = [refueling('a', { vehicleId: 'v1' }), refueling('b', { vehicleId: 'v2' })]
    expect(ids(sortLogs('refuelings', rows, 'VEHICLE', 'ASC', vehicles))).toEqual(['b', 'a']) // "astra" before "Octavia"

    const expenses = [refueling('a', { category: 'Tyres' }), refueling('b', { category: null }), refueling('c', { category: 'insurance' })]
    expect(ids(sortLogs('expenses', expenses, 'CATEGORY', 'ASC', vehicles))).toEqual(['b', 'c', 'a'])
  })
})

describe('answerLocally', () => {
  async function store(rows: LogRow[], complete = true) {
    const device = await (memoryStorage()).open('u1')
    await device.rows.putVehicles([...vehicles.values()])
    await device.rows.putLogs('refuelings', rows)
    for (const id of ['v1', 'v2']) await device.rows.putCursor({ vehicleId: id, from: null, watermark: 'w', next: null, complete, fullStartedAt: null })
    return device.rows
  }

  it('answers a downloaded refuelling with the pump\'s price, and one downloaded before discounts with none and the price paid', async () => {
    const { discount: _discount, ...older } = refueling('old', { date: '2026-09-02', volume: 40, totalCost: 20000 }) // no discount, no pump price
    const rows = await store([refueling('new', { date: '2026-09-03', volume: 43, totalCost: 34744, discount: 430, pricePerUnit: 808, pricePerUnitBeforeDiscount: 818 }), older as LogRow])

    const answer = await answerLocally(rows, 'Refuelings', { vehicleId: 'v1', orderBy: 'DATE', direction: 'DESC', skip: 0, take: 10 })

    const [fresh, old] = answer!.refuelings as Record<string, unknown>[]
    expect(fresh).toMatchObject({ id: 'new', discount: 430, pricePerUnitBeforeDiscount: 818 }) // the server's own, never worked out again
    expect(old).toMatchObject({ id: 'old', discount: null, pricePerUnitBeforeDiscount: 500 }) // so a grid with the column shows it, not a dash
  })

  it('answers any page of a downloaded vehicle, with who may edit following the vehicle today', async () => {
    const rows = await store([refueling('a', { date: '2026-09-03' }), refueling('b', { date: '2026-09-02' }), refueling('c', { date: '2026-09-01' }), refueling('t', { deletedAt: '2026-09-10T00:00:00Z' })])

    const answer = await answerLocally(rows, 'Refuelings', { vehicleId: 'v1', orderBy: 'DATE', direction: 'DESC', skip: 1, take: 1 })

    expect(answer?.refuelingCount).toBe(3) // the trash is not counted
    const [row] = answer!.refuelings as Record<string, unknown>[]
    expect(row).toMatchObject({ id: 'b', canEdit: true, canDelete: true, __typename: 'Refueling' })
    expect(row).not.toHaveProperty('pulledAt')
  })

  it('answers the trash across the vehicles whose logs may be edited, and counts what may be deleted', async () => {
    const rows = await store([refueling('t1', { deletedAt: '2026-09-10T00:00:00Z' }), refueling('t2', { vehicleId: 'v2', deletedAt: '2026-09-11T00:00:00Z' })])

    const answer = await answerLocally(rows, 'RefuelingTrash', { orderBy: 'DELETED_AT', direction: 'DESC', skip: 0, take: 10 })

    expect(ids(answer?.refuelingTrash as LogRow[])).toEqual(['t2', 't1'])
    expect(answer?.refuelingTrashCount).toBe(2)
    expect(answer?.refuelingTrashDeletableCount).toBe(1) // v2's logs may only be edited
    expect((answer!.refuelingTrash as { vehicle: { name: string } }[])[0].vehicle.name).toBe('astra')
  })

  it('leaves to the answers seen what it cannot answer: a vehicle not downloaded yet, a log it does not have, another query', async () => {
    const rows = await store([refueling('a')], false)

    expect(await answerLocally(rows, 'Refuelings', { vehicleId: 'v1', orderBy: 'DATE', direction: 'DESC', skip: 0, take: 10 })).toBeUndefined()
    expect(await answerLocally(rows, 'RefuelingDetails', { id: 'nope' })).toBeUndefined()
    expect(await answerLocally(rows, 'Welcome', {})).toBeUndefined()
    expect(await answerLocally(null, 'Refuelings', { vehicleId: 'v1' })).toBeUndefined()
    expect((await answerLocally(rows, 'RefuelingDetails', { id: 'a' }))?.refueling).toMatchObject({ id: 'a' }) // a log it has, any vehicle

    await rows.putCursor({ vehicleId: 'v1', from: false, watermark: null, next: null, complete: true, fullStartedAt: null }) // window "none"
    expect(await answerLocally(rows, 'Refuelings', { vehicleId: 'v1', orderBy: 'DATE', direction: 'DESC', skip: 0, take: 10 })).toBeUndefined()
  })
})

describe('the same order as the server', () => {
  // The fixture the repositories are tested against too (Infrastructure SortParityTests): any change on either side fails one of them.
  const toRows = (list: Record<string, unknown>[]) => list.map((r) => ({ ...r, vehicleId: 'v1', pulledAt: 1 }) as LogRow)

  it.each(Object.entries(fixture.refuelingOrders))('refuelings, %s', (order, expected) => {
    const [field, direction] = order.split(' ')
    expect(ids(sortLogs('refuelings', toRows(fixture.refuelings), field, direction as 'ASC' | 'DESC', vehicles))).toEqual(expected)
  })

  it.each(Object.entries(fixture.expenseOrders))('expenses, %s', (order, expected) => {
    const [field, direction] = order.split(' ')
    expect(ids(sortLogs('expenses', toRows(fixture.expenses), field, direction as 'ASC' | 'DESC', vehicles))).toEqual(expected)
  })
})

describe('the changes waiting on this device', () => {
  it('lay over the rows: an add is a row, an update its values, a restore takes it out of the trash, a trash leaves it', async () => {
    const { outbox } = await import('../../src/offline/outbox.ts')
    const { deviceData } = await import('../../src/offline/deviceData.ts')
    const { withChanges } = await import('../../src/offline/localResolvers.ts')
    deviceData.reset(memoryStorage())
    await deviceData.signedIn('u1')
    await outbox.reload()
    await outbox.enqueue({ id: 'n', entity: 'refuelings', action: 'add', vehicleId: 'v1', targetId: 'n', input: { id: 'n', date: '2026-10-02', volume: 20, totalCost: 50, discount: 5, currency: 'EUR', odometer: 1500 } })
    await outbox.enqueue({ id: 'u', entity: 'refuelings', action: 'update', vehicleId: 'v1', targetId: 'a', input: { id: 'a', volume: 10, totalCost: 25, date: '2026-09-01' }, expectedVersion: 1 })
    await outbox.enqueue({ id: 'r', entity: 'refuelings', action: 'restore', vehicleId: 'v1', targetId: 't', expectedVersion: 2 })
    await outbox.enqueue({ id: 'x', entity: 'refuelings', action: 'trash', vehicleId: 'v1', targetId: 'b', expectedVersion: 1 })

    const rows = withChanges('refuelings', [refueling('a'), refueling('b'), refueling('t', { deletedAt: '2026-09-10T00:00:00Z' })], 'v1')
    const byId = new Map(rows.map((r) => [r.id, r]))

    expect(byId.get('n')).toMatchObject({ __typename: 'Refueling', volume: 20, discount: 5, pricePerUnit: 2.5, pricePerUnitBeforeDiscount: 2.75, consumption: null, version: 0, reviewState: 'NONE' })
    expect(byId.get('a')).toMatchObject({ volume: 10, pricePerUnit: 2.5, pricePerUnitBeforeDiscount: 2.5 }) // no discount: the two prices are one
    expect(byId.get('t')?.deletedAt).toBeNull()
    expect(byId.get('b')?.deletedAt).toBeNull() // waiting to be trashed: still listed (marked)
    expect(outbox.markOf('refuelings', 'b')).toBe('deleted')
  })

  it('a restore counts as a save, and an add the server has after all keeps what the server said of it', async () => {
    const { outbox } = await import('../../src/offline/outbox.ts')
    const { deviceData } = await import('../../src/offline/deviceData.ts')
    const { withChanges } = await import('../../src/offline/localResolvers.ts')
    deviceData.reset(memoryStorage())
    await deviceData.signedIn('u1')
    await outbox.reload()
    await outbox.enqueue({ id: 'r', entity: 'refuelings', action: 'restore', vehicleId: 'v1', targetId: 't', expectedVersion: 2 })
    // The add reached the server but its answer did not come back; the next download brought the row.
    await outbox.enqueue({ id: 'n', entity: 'refuelings', action: 'add', vehicleId: 'v1', targetId: 'n', input: { id: 'n', volume: 20 }, sent: true })

    const rows = withChanges('refuelings', [refueling('t', { deletedAt: '2026-09-10T00:00:00Z', version: 2 }), refueling('n', { volume: 20, version: 3, consumption: 6.1 })], 'v1')
    const byId = new Map(rows.map((r) => [r.id, r]))

    expect(byId.get('t')).toMatchObject({ deletedAt: null, version: 3 }) // an edit made now is made from the version the restore leaves
    expect(byId.get('n')).toMatchObject({ volume: 20, version: 3, consumption: 6.1 })
  })
})

describe('vehicles added on this device', () => {
  it('are counted on every page of the home list but listed at the end of the last only, and never twice', async () => {
    const { outbox } = await import('../../src/offline/outbox.ts')
    const { deviceData } = await import('../../src/offline/deviceData.ts')
    deviceData.reset(memoryStorage())
    await deviceData.signedIn('u1')
    await outbox.reload()
    const card = (id: string) => ({ __typename: 'Vehicle', id, name: id, recurring: [] })
    const pages: Record<number, Record<string, unknown>> = {
      0: { myVehicles: Array.from({ length: 2 }, (_, i) => card(`v${i}`)), myVehicleCount: 3, vehicleTotal: 3 },
      2: { myVehicles: [card('v2'), card('lost')], myVehicleCount: 4, vehicleTotal: 4 }, // the server has one whose answer was lost
    }
    for (const id of ['golf', 'lost']) await outbox.enqueue({ id, entity: 'vehicles', action: 'add', vehicleId: id, targetId: id, input: { id, name: id, fuelType: 'PETROL' } })
    const welcome = async (skip: number) =>
      (await answerLocally(await deviceData.rows(), 'Welcome', { search: null, skip, take: 2 }, async () => pages[skip])) as { myVehicles: { id: string }[]; myVehicleCount: number }

    const first = await welcome(0)
    expect(first.myVehicles.map((v) => v.id)).toEqual(['v0', 'v1']) // the next page is asked with skip 2, the server's own
    expect(first.myVehicleCount).toBe(5)
    expect((await welcome(2)).myVehicles.map((v) => v.id)).toEqual(['v2', 'lost', 'golf'])
  })
})

describe('a visit waiting on this device', () => {
  it('with an amount shows its expense, new, in the vehicle\'s expenses', async () => {
    const { outbox } = await import('../../src/offline/outbox.ts')
    const { deviceData } = await import('../../src/offline/deviceData.ts')
    const { withChanges } = await import('../../src/offline/localResolvers.ts')
    deviceData.reset(memoryStorage())
    await deviceData.signedIn('u1')
    await outbox.reload()
    const input = { ids: ['s1', 's2'], date: '2026-10-05', amount: 120, currency: 'EUR', title: 'Oil, Filter', category: '', odometer: 1500, expenseId: 'e9', photoIds: [] }
    await outbox.enqueue({ id: 'visit', entity: 'recurring', action: 'markDone', vehicleId: 'v1', targetId: 'e9', targetIds: input.ids, input })
    await outbox.enqueue({ id: 'visit2', entity: 'recurring', action: 'markDone', vehicleId: 'v1', targetId: 'visit2', targetIds: ['s3'], input: { ids: ['s3'], date: '2026-10-05', amount: null } })

    const rows = withChanges('expenses', [], 'v1')

    expect(rows).toHaveLength(1) // a visit without an amount logs no expense
    expect(rows[0]).toMatchObject({ __typename: 'Expense', id: 'e9', title: 'Oil, Filter', amount: 120, category: null, version: 1 }) // as the server will create it
    expect(outbox.markOf('expenses', 'e9')).toBe('new')
    expect(outbox.markOf('recurring', 's2')).toBe('done')
  })

  it('moves its schedules on as the server will: no longer due, the new baseline, one version more', async () => {
    const { outbox } = await import('../../src/offline/outbox.ts')
    const { deviceData } = await import('../../src/offline/deviceData.ts')
    deviceData.reset(memoryStorage())
    await deviceData.signedIn('u1')
    await outbox.reload()
    const status = { __typename: 'RecurrenceStatusInfo', state: 'OVERDUE', limit: 'TIME', dueDate: '2026-09-01', dueOdometer: null, daysLeft: -30, distanceLeft: null }
    const schedule = (id: string) => ({ __typename: 'RecurringExpenseInfo', id, version: 3, title: id, lastDoneDate: '2025-09-01', lastDoneOdometer: 1000, status })
    const seen = { vehicle: { __typename: 'Vehicle', id: 'v1', recurring: [schedule('s1'), schedule('s2')] } }
    await outbox.enqueue({ id: 'visit', entity: 'recurring', action: 'markDone', vehicleId: 'v1', targetId: 'visit', targetIds: ['s1'], input: { ids: ['s1'], date: '2026-10-05', odometer: 1500 } })

    const answer = await answerLocally(await deviceData.rows(), 'RecurringExpenses', { vehicleId: 'v1' }, async (name) => (name === 'RecurringExpenses' ? seen : undefined))
    const [s1, s2] = (answer!.vehicle as { recurring: Record<string, unknown>[] }).recurring

    expect(s1).toMatchObject({ lastDoneDate: '2026-10-05', lastDoneOdometer: 1500, version: 4, status: { state: 'UPCOMING' } }) // not preselected again
    expect(s2).toMatchObject({ lastDoneDate: '2025-09-01', version: 3, status: { state: 'OVERDUE' } })
  })
})

describe('more of the repositories\' orders', () => {
  it('orders by who logged it (nobody first), by vehicle and by when it went to the trash, refuelings and expenses alike', () => {
    const rows = [
      refueling('a', { createdBy: { displayName: 'bob' } }),
      refueling('b', { createdBy: null }),
      refueling('c', { createdBy: { displayName: 'Alice' } }),
    ]
    expect(ids(sortLogs('refuelings', rows, 'CREATED_BY', 'ASC', vehicles))).toEqual(['b', 'c', 'a'])
    expect(ids(sortLogs('expenses', rows, 'CREATED_BY', 'DESC', vehicles))).toEqual(['a', 'c', 'b'])

    const expenses = [refueling('a', { vehicleId: 'v1' }), refueling('b', { vehicleId: 'v2' })]
    expect(ids(sortLogs('expenses', expenses, 'VEHICLE', 'ASC', vehicles))).toEqual(['b', 'a'])

    const trashed = [refueling('a', { deletedAt: '2026-09-10T00:00:00Z' }), refueling('b', { deletedAt: '2026-09-11T00:00:00Z' })]
    expect(ids(sortLogs('expenses', trashed, 'DELETED_AT', 'DESC', vehicles))).toEqual(['b', 'a'])
  })
})

describe('answering offline, the device\'s own data', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  /** A fresh device for user u1, with vehicles v1 (downloaded) and v2 (known, its logs not downloaded). */
  async function device(): Promise<RowStore> {
    deviceData.reset(memoryStorage())
    await deviceData.signedIn('u1')
    await outbox.reload()
    const rows = (await deviceData.rows())!
    await rows.putVehicles([...vehicles.values()])
    await rows.putCursor({ vehicleId: 'v1', from: null, watermark: 'w', next: null, complete: true, fullStartedAt: null })
    return rows
  }
  const enqueue = (draft: ChangeDraft) => outbox.enqueue(draft)
  const expense = (id: string, fields: Partial<LogRow> = {}): LogRow => ({ __typename: 'Expense', id, vehicleId: 'v1', date: '2026-09-01', title: id, category: null, odometer: null, deletedAt: null, pulledAt: 1, ...fields })

  it('a list asked without an order or a page is the newest first, all of it; an unnamed query gets nothing', async () => {
    const rows = await device()
    await rows.putLogs('refuelings', [refueling('old', { date: '2026-08-01' }), refueling('new', { date: '2026-09-15' })])

    const answer = await answerLocally(rows, 'Refuelings', { vehicleId: 'v1' })

    expect(ids(answer!.refuelings as LogRow[])).toEqual(['new', 'old'])
    expect(await answerLocally(rows, undefined, { vehicleId: 'v1' })).toBeUndefined()
  })

  it('an expense downloaded before discounts is answered with none, one downloaded since with its own', async () => {
    const rows = await device()
    await rows.putLogs('expenses', [expense('old', { date: '2026-08-01', amount: 3000 }), expense('new', { date: '2026-09-15', amount: 18800, discount: 1200 })])

    const answer = await answerLocally(rows, 'Expenses', { vehicleId: 'v1' })

    const [fresh, old] = answer!.expenses as Record<string, unknown>[]
    expect(fresh).toMatchObject({ id: 'new', discount: 1200 })
    expect(old).toMatchObject({ id: 'old', discount: null }) // asked for by the grid and the details: never a field the row lacks
  })

  it('a refuelling kept before its photo is read has no price per unit yet', async () => {
    const rows = await device()
    await enqueue({ id: 'n', entity: 'refuelings', action: 'add', vehicleId: 'v1', targetId: 'n', input: { id: 'n', date: '2026-10-01', photoIds: ['local:receipt'] } })

    const [row] = (await answerLocally(rows, 'Refuelings', { vehicleId: 'v1' }))!.refuelings as LogRow[]

    expect(row).toMatchObject({ id: 'n', volume: null, totalCost: null, pricePerUnit: null })
  })

  it('a log\'s details show its photos with the photo changes waiting: one removed is gone, one kept on the device is there', async () => {
    URL.createObjectURL = vi.fn(() => 'blob:kept-photo')
    const rows = await device()
    await rows.putLogs('refuelings', [refueling('a', { photos: [{ __typename: 'LogPhotoInfo', id: 'p1', url: '/media/p1' }, { __typename: 'LogPhotoInfo', id: 'p2', url: '/media/p2' }] })])
    await rows.putLogs('expenses', [expense('e')])
    const key = (await keptPhotos.keep(new Blob([new Uint8Array([1])], { type: 'image/jpeg' }), 'v1'))!
    await enqueue({ id: 'rm', entity: 'refuelings', action: 'removePhoto', vehicleId: 'v1', targetId: 'a', input: { imageId: 'p1' } })
    await enqueue({ id: 'add', entity: 'refuelings', action: 'addPhoto', vehicleId: 'v1', targetId: 'a', input: { key } })
    await enqueue({ id: 'lost', entity: 'refuelings', action: 'addPhoto', vehicleId: 'v1', targetId: 'a', input: { key: 'local:no-longer-on-the-device' } })

    const answer = await answerLocally(rows, 'RefuelingDetails', { id: 'a' })

    expect((answer!.refueling as { photos: unknown[] }).photos).toEqual([
      { __typename: 'LogPhotoInfo', id: 'p2', url: '/media/p2' },
      { __typename: 'LogPhotoInfo', id: key, url: 'blob:kept-photo' },
    ])
    expect((await answerLocally(rows, 'ExpenseDetails', { id: 'e' }))?.expense).toMatchObject({ id: 'e', photos: [] }) // the feed sent none
    expect(await answerLocally(rows, 'ExpenseDetails', { id: null })).toBeUndefined()
  })

  it('a log of a vehicle the device no longer holds may be looked at, not changed', async () => {
    const rows = await device()
    await rows.putLogs('refuelings', [refueling('far', { vehicleId: 'elsewhere' })])

    expect((await answerLocally(rows, 'RefuelingDetails', { id: 'far' }))?.refueling).toMatchObject({ id: 'far', canEdit: false, canDelete: false })
  })

  it('the trash needs a vehicle downloaded, and is the most recently trashed first when asked without an order', async () => {
    const rows = await device()
    await rows.putLogs('refuelings', [refueling('t1', { deletedAt: '2026-09-10T00:00:00Z' }), refueling('t2', { deletedAt: '2026-09-12T00:00:00Z' })])

    expect(ids((await answerLocally(rows, 'RefuelingTrash', {}))!.refuelingTrash as LogRow[])).toEqual(['t2', 't1'])

    await rows.putCursor({ vehicleId: 'v1', from: null, watermark: null, next: 'p2', complete: false, fullStartedAt: 1 })
    expect(await answerLocally(rows, 'RefuelingTrash', {})).toBeUndefined()
  })

  it('a new log starts from the latest reading of the downloaded logs and the latest refuelling\'s currency', async () => {
    const rows = await device()
    await rows.putLogs('refuelings', [
      refueling('a', { date: '2026-09-01', odometer: 1000, currency: 'EUR' }),
      refueling('b', { date: '2026-09-20', odometer: 1500, currency: 'HUF' }),
    ])
    await rows.putLogs('expenses', [
      expense('e1', { date: '2026-09-20', odometer: 1520, category: 'Tyres' }), // the same day, further on
      expense('e2', { date: '2026-09-21', category: 'wash' }), // no reading
      expense('e3', { category: 'Tyres' }),
    ])

    expect((await answerLocally(rows, 'LogDefaults', { vehicleId: 'v1' }))?.logDefaults).toEqual({
      __typename: 'LogDefaults', lastOdometer: 1520, lastDate: '2026-09-20', currency: 'HUF',
    })
    expect(await answerLocally(rows, 'ExpenseCategories', { vehicleId: 'v1' })).toEqual({ expenseCategories: ['Tyres', 'wash'] })

    await rows.deleteLogs('refuelings', ['a', 'b'])
    expect((await answerLocally(rows, 'LogDefaults', { vehicleId: 'v1' }))?.logDefaults).toMatchObject({ lastOdometer: 1520, currency: null }) // no refuelling yet
  })

  it('for a vehicle not downloaded, the defaults and categories are the server\'s while it can be reached', async () => {
    const rows = await device()
    const kept = vi.fn(async () => ({ logDefaults: { lastOdometer: 1 } }))

    expect(await answerLocally(rows, 'LogDefaults', { vehicleId: 'v2' }, kept)).toBeUndefined()
    expect(await answerLocally(rows, 'ExpenseCategories', { vehicleId: 'v2' }, kept)).toBeUndefined()
    expect(kept).not.toHaveBeenCalled()
  })

  it('out of reach, a vehicle not downloaded starts from the last answer seen, else from nothing when the device knows the vehicle', async () => {
    const rows = await device()
    connectivity.failed()
    const seenDefaults = { logDefaults: { __typename: 'LogDefaults', lastOdometer: 800, lastDate: '2026-08-01', currency: 'EUR' } }
    const keptAnswers = (answers: Record<string, unknown>) => async (name: string, variables: Record<string, unknown>) =>
      answers[`${name}:${String(variables.vehicleId ?? variables.id)}`] as Record<string, unknown> | undefined

    // The last answer seen.
    const seen = keptAnswers({ 'LogDefaults:v2': seenDefaults, 'ExpenseCategories:v2': { expenseCategories: ['Oil'] } })
    expect(await answerLocally(rows, 'LogDefaults', { vehicleId: 'v2' }, seen)).toBe(seenDefaults)
    expect(await answerLocally(rows, 'ExpenseCategories', { vehicleId: 'v2' }, seen)).toEqual({ expenseCategories: ['Oil'] })

    // None, but the vehicle is on the home list the device downloaded, or its page was seen.
    const empty = { logDefaults: { __typename: 'LogDefaults', lastOdometer: null, lastDate: null, currency: null } }
    expect(await answerLocally(rows, 'LogDefaults', { vehicleId: 'v2' }, keptAnswers({}))).toEqual(empty)
    expect(await answerLocally(rows, 'ExpenseCategories', { vehicleId: 'v2' }, keptAnswers({}))).toEqual({ expenseCategories: [] })
    const pageSeen = keptAnswers({ 'VehicleDetails:v9': { vehicle: { id: 'v9' } } })
    expect(await answerLocally(rows, 'LogDefaults', { vehicleId: 'v9' }, pageSeen)).toEqual(empty)

    // A vehicle the device knows nothing of.
    expect(await answerLocally(rows, 'LogDefaults', { vehicleId: 'unknown' }, keptAnswers({}))).toBeUndefined()
    expect(await answerLocally(rows, 'ExpenseCategories', { vehicleId: 'unknown' }, keptAnswers({}))).toBeUndefined()
    expect(await answerLocally(rows, 'LogDefaults', {}, keptAnswers({}))).toBeUndefined()
  })

  it('a vehicle with a change waiting is the device\'s: the stored vehicle with the change over it, or nothing when it knows none', async () => {
    const rows = await device()
    expect(await answerLocally(rows, 'VehicleDetails', { id: 'v1' })).toBeUndefined() // nothing waits: the server's (or the last seen)

    await enqueue({ id: 'u1', entity: 'vehicles', action: 'update', vehicleId: 'v1', targetId: 'v1', input: { id: 'v1', name: 'Octavia RS', units: { volume: 'GALLONS' } }, expectedVersion: 2 })
    await enqueue({ id: 'u2', entity: 'vehicles', action: 'update', vehicleId: 'ghost', targetId: 'ghost', input: { id: 'ghost', name: 'Ghost' }, expectedVersion: 1 })

    expect((await answerLocally(rows, 'VehicleDetails', { id: 'v1' }))?.vehicle).toMatchObject({
      id: 'v1', name: 'Octavia RS', logAccess: 'DELETE', units: { __typename: 'MeasurementUnits', volume: 'GALLONS' },
    })
    expect(await answerLocally(rows, 'VehicleDetails', { id: 'ghost' })).toBeUndefined()
  })

  it('a home card with a change waiting shows the schedules with the changes over them', async () => {
    const rows = await device()
    const card = { __typename: 'Vehicle', id: 'v1', name: 'Octavia', recurring: [{ __typename: 'RecurringExpenseInfo', id: 's1', title: 'Oil', version: 2 }] }
    const kept = async (name: string) => (name === 'VehicleCard' ? { vehicle: card } : undefined)
    expect(await answerLocally(rows, 'VehicleCard', { id: 'v1' }, kept)).toBeUndefined() // nothing waits

    await enqueue({ id: 's2', entity: 'recurring', action: 'add', vehicleId: 'v1', targetId: 's2', input: { id: 's2', title: 'Tyres', kind: 'TIME', intervalMonths: 6 } })
    const answer = await answerLocally(rows, 'VehicleCard', { id: 'v1' }, kept)

    expect((answer!.vehicle as { recurring: { id: string; title: string }[] }).recurring.map((s) => [s.id, s.title])).toEqual([['s1', 'Oil'], ['s2', 'Tyres']])
    // A card never seen, of a vehicle the home list brought: its schedules are the changes'.
    expect((await answerLocally(rows, 'VehicleCard', { id: 'v1' }))?.vehicle).toMatchObject({ name: 'Octavia', recurring: [{ id: 's2', status: { state: 'UPCOMING' } }] })
  })

  it('the Recurring tab lays edits and visits over the schedules seen, and leaves alone those it does not know', async () => {
    const rows = await device()
    const status = { __typename: 'RecurrenceStatusInfo', state: 'DUE_SOON' }
    const seen = { vehicle: { __typename: 'Vehicle', id: 'v1', recurring: [{ __typename: 'RecurringExpenseInfo', id: 's1', title: 'Oil', version: 0, lastDoneDate: '2026-01-01', status }] } }
    const kept = async (name: string) => (name === 'RecurringExpenses' ? seen : undefined)
    await enqueue({ id: 'e1', entity: 'recurring', action: 'update', vehicleId: 'v1', targetId: 's1', input: { id: 's1', title: 'Oil and filter' }, expectedVersion: 0 })
    await enqueue({ id: 'e2', entity: 'recurring', action: 'update', vehicleId: 'v1', targetId: 'unknown', input: { id: 'unknown', title: 'Whatever' }, expectedVersion: 1 })
    await enqueue({ id: 'visit', entity: 'recurring', action: 'markDone', vehicleId: 'v1', targetId: 'visit', targetIds: ['s1', 'gone'], input: { ids: ['s1', 'gone'] } })
    await enqueue({ id: 'odd', entity: 'recurring', action: 'markDone', vehicleId: 'v1', targetId: 'odd', input: {} }) // names no schedule

    const [s1, ...others] = ((await answerLocally(rows, 'RecurringExpenses', { vehicleId: 'v1' }, kept))!.vehicle as { recurring: Record<string, unknown>[] }).recurring

    expect(others).toEqual([])
    // A visit without a date or a reading keeps the baseline; a version of 0 (never saved on the server) stays.
    expect(s1).toMatchObject({ title: 'Oil and filter', lastDoneDate: '2026-01-01', version: 0, status: { state: 'UPCOMING' } })

    expect(await answerLocally(rows, 'RecurringExpenses', { vehicleId: 'v1' })).toBeUndefined() // nothing seen: the server's
    expect(await answerLocally(rows, 'RecurringExpenses', { vehicleId: 'v2' }, kept)).toBeUndefined() // nothing waits for v2
  })

  it('a vehicle added here has its schedules, no figures and no charts until the server has it', async () => {
    const rows = await device()
    await enqueue({ id: 'golf', entity: 'vehicles', action: 'add', vehicleId: 'golf', targetId: 'golf', input: { id: 'golf', name: 'Golf', fuelType: 'PETROL' } })
    await enqueue({ id: 's1', entity: 'recurring', action: 'add', vehicleId: 'golf', targetId: 's1', input: { id: 's1', title: 'Oil' } })

    expect(await answerLocally(rows, 'RecurringExpenses', { vehicleId: 'golf' })).toMatchObject({ vehicle: { __typename: 'Vehicle', id: 'golf', recurring: [{ id: 's1', title: 'Oil' }] } })
    expect(await answerLocally(rows, 'VehicleDashboard', { id: 'golf' })).toEqual({ vehicle: { __typename: 'Vehicle', id: 'golf', summary: null, dashboard: null }, vehicleCharts: [] })
    expect(await answerLocally(rows, 'ChartData', { vehicleId: 'golf' })).toEqual({ vehicleChartData: { __typename: 'ChartData', unit: 'COUNT', series: [] } })
    const empty = { __typename: 'TrendWindow', start: todayIso(), end: todayIso(), fillUps: 0 }
    expect(await answerLocally(rows, 'VehicleTrend', { vehicleId: 'golf', figure: 'SPENDING', period: { kind: 'THIS_YEAR' } })).toEqual({
      vehicleTrend: { __typename: 'TrendFigureData', recent: empty, earlier: empty, currencies: [], consumption: null, distance: null }, // the windows are non-null: the tile says it has nothing yet
    })
    expect(await answerLocally(rows, 'VehicleDashboard', { id: 'v1' })).toBeUndefined()
    expect(await answerLocally(rows, 'ChartData', { vehicleId: 'v1' })).toBeUndefined()
    expect(await answerLocally(rows, 'VehicleTrend', { vehicleId: 'v1' })).toBeUndefined()
  })

  it('a vehicle added here has no key figure over a period yet, and its odometer and last fill-up from the logs kept here (#272)', async () => {
    const rows = await device()
    await enqueue({ id: 'golf', entity: 'vehicles', action: 'add', vehicleId: 'golf', targetId: 'golf', input: { id: 'golf', name: 'Golf', fuelType: 'PETROL' } })
    const figure = (measure: string, period?: Record<string, unknown>) => answerLocally(rows, 'VehicleFigure', { vehicleId: 'golf', measure, period })
    const empty = { __typename: 'TrendWindow', start: todayIso(), end: todayIso(), fillUps: 0 }
    const nothing = { __typename: 'FigureData', currencies: [], value: null, lastFillUp: null }

    expect(await figure('TOTAL_SPEND', { kind: 'THIS_MONTH' })).toEqual({ vehicleFigure: { ...nothing, recent: empty, earlier: empty, unit: 'CURRENCY' } })
    expect(await figure('AVERAGE_CONSUMPTION', { kind: 'LAST', unit: 'FILL_UPS', count: 10, against: 'NONE' })).toEqual({ vehicleFigure: { ...nothing, recent: empty, earlier: null, unit: 'CONSUMPTION' } })
    // No logs kept here yet: no reading, no fill-up.
    expect(await figure('ODOMETER')).toEqual({ vehicleFigure: { ...nothing, recent: null, earlier: null, unit: 'DISTANCE' } })
    expect(await figure('LAST_FILL_UP')).toEqual({ vehicleFigure: { ...nothing, recent: null, earlier: null, unit: null, lastFillUp: { __typename: 'LastFillUpFigure', date: null, fillUps: 0, expenses: 0 } } })

    await enqueue({ id: 'r1', entity: 'refuelings', action: 'add', vehicleId: 'golf', targetId: 'r1', input: { id: 'r1', vehicleId: 'golf', date: '2026-10-02', volume: 40, totalCost: 24000, currency: 'HUF', odometer: 12000, isFullTank: true } })
    await enqueue({ id: 'r2', entity: 'refuelings', action: 'add', vehicleId: 'golf', targetId: 'r2', input: { id: 'r2', vehicleId: 'golf', date: '2026-10-20', volume: 30, totalCost: 18000, currency: 'HUF', odometer: 12600, isFullTank: true } })
    await enqueue({ id: 'x1', entity: 'expenses', action: 'add', vehicleId: 'golf', targetId: 'x1', input: { id: 'x1', vehicleId: 'golf', date: '2026-10-25', title: 'Wash', amount: 3000, currency: 'HUF', odometer: 12700 } })
    expect(await figure('ODOMETER')).toMatchObject({ vehicleFigure: { value: { __typename: 'ValueChange', recent: 12700, earlier: null } } }) // the highest reading, the expense's too
    expect(await figure('LAST_FILL_UP')).toMatchObject({ vehicleFigure: { lastFillUp: { date: '2026-10-20', fillUps: 2, expenses: 1 } } })
    // Left for its photos (no litres, cost or amount yet): not counted until they are read, as the server's statistics leave it out.
    await enqueue({ id: 'r3', entity: 'refuelings', action: 'add', vehicleId: 'golf', targetId: 'r3', input: { id: 'r3', vehicleId: 'golf', date: '2026-10-28', currency: 'HUF', odometer: 12900, isFullTank: true } })
    await enqueue({ id: 'x2', entity: 'expenses', action: 'add', vehicleId: 'golf', targetId: 'x2', input: { id: 'x2', vehicleId: 'golf', date: '2026-10-29', title: 'Tyres', currency: 'HUF' } })
    expect(await figure('LAST_FILL_UP')).toMatchObject({ vehicleFigure: { lastFillUp: { date: '2026-10-20', fillUps: 2, expenses: 1 } } })
    expect(await figure('ODOMETER')).toMatchObject({ vehicleFigure: { value: { recent: 12900 } } }) // a reading counts whatever the log lacks
    expect(await answerLocally(rows, 'VehicleFigure', { vehicleId: 'v1', measure: 'ODOMETER' })).toBeUndefined() // the server's
  })

  it('a search on the home list finds the vehicles added here by name or licence plate', async () => {
    const rows = await device()
    const base = { myVehicles: [{ __typename: 'Vehicle', id: 'v1', name: 'Octavia' }], myVehicleCount: 1, vehicleTotal: 3 }
    await enqueue({ id: 'golf', entity: 'vehicles', action: 'add', vehicleId: 'golf', targetId: 'golf', input: { id: 'golf', name: 'Golf', fuelType: 'PETROL' } })
    await enqueue({ id: 'van', entity: 'vehicles', action: 'add', vehicleId: 'van', targetId: 'van', input: { id: 'van', name: 'Van', licensePlate: 'GOL-123', fuelType: 'DIESEL' } })
    await enqueue({ id: 'bike', entity: 'vehicles', action: 'add', vehicleId: 'bike', targetId: 'bike', input: { id: 'bike', name: 'Bike', fuelType: 'ELECTRIC' } })

    const answer = (await answerLocally(rows, 'Welcome', { search: ' gol ', skip: 0, take: 10 }, async () => base)) as { myVehicles: { id: string; recurring: unknown[] }[]; myVehicleCount: number; vehicleTotal: number }

    expect(answer.myVehicles.map((v) => v.id)).toEqual(['v1', 'golf', 'van'])
    expect(answer.myVehicles[0].recurring).toEqual([]) // a card without schedules
    expect(answer.myVehicleCount).toBe(3)
    expect(answer.vehicleTotal).toBe(5)
  })

  it('the administrators\' list offline holds the device\'s vehicles, in any order and page', async () => {
    deviceData.reset(memoryStorage())
    await deviceData.signedIn('u1')
    await outbox.reload()
    expect(await answerLocally((await deviceData.rows())!, 'Vehicles', {})).toBeUndefined() // a device that holds no vehicle

    const fresh = await device()
    await fresh.putVehicles([
      { id: 'v1', name: 'Octavia', logAccess: 'DELETE', licensePlate: 'ABC', fuelType: 'PETROL', owner: { displayName: 'Zoe' } },
      { id: 'v2', name: 'astra', logAccess: 'EDIT', licensePlate: null, fuelType: 'DIESEL', owner: { displayName: 'adam' } },
    ])
    await enqueue({ id: 'golf', entity: 'vehicles', action: 'add', vehicleId: 'golf', targetId: 'golf', input: { id: 'golf', name: 'Golf', licensePlate: 'abc', fuelType: 'ELECTRIC' } })
    const kept = async (name: string, variables: Record<string, unknown>) =>
      name === 'VehicleDetails' && variables.id === 'v1' ? { vehicle: { id: 'v1', refuelingCount: 3 } } : undefined
    const order = async (variables: Record<string, unknown>) =>
      ((await answerLocally(fresh, 'Vehicles', variables, kept))!.vehicles as { id: string }[]).map((v) => v.id)

    expect(await order({ orderBy: 'LICENSE_PLATE', direction: 'ASC' })).toEqual(['v2', 'golf', 'v1']) // the same plate: by id
    expect(await order({ orderBy: 'FUEL_TYPE', direction: 'ASC' })).toEqual(['v2', 'golf', 'v1'])
    expect(await order({ orderBy: 'OWNER', direction: 'ASC' })).toEqual(['golf', 'v2', 'v1'])
    expect(await order({ orderBy: 'REFUELING_COUNT', direction: 'DESC' })).toEqual(['v1', 'golf', 'v2'])
    expect(await order({ skip: 1, take: 1 })).toEqual(['golf']) // by name: astra, Golf, Octavia
    expect((await answerLocally(fresh, 'Vehicles', {}, kept))?.vehicleCount).toBe(3)
  })
})
