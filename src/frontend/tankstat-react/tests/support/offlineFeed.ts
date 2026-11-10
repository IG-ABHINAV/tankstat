import { HttpResponse } from 'msw'
import { graphql } from './graphql.ts'

// The device's clock: 7 October 2026. The default window (the last two months) starts on 7 August.
export const now = () => new Date(2026, 9, 7, 12, 0, 0)
const OVERLAP = 2 * 60 * 1000

interface FeedRow {
  kind: 'refuelings' | 'expenses'
  id: string
  vehicleId: string
  date: string
  volume: number
  updatedAt: number
}

/**
 * The server's feed (OfflineFeedService) in memory: a first download by the window's start, later ones by the watermark minus the
 * overlap, with what was removed since; a page per table of `take` rows, continued by an opaque `next`.
 */
export function fakeFeed() {
  let clock = Date.UTC(2026, 9, 7, 10)
  const rows: FeedRow[] = []
  const removed: { type: string; id: string; vehicleId: string; at: number }[] = []
  const asked: Record<string, unknown>[] = []
  let rule = 'span:P2M'
  let resyncNext = false
  let failAfter: number | null = null
  let refuseCursors = false
  let tyresHidden = false
  let onAsk: ((n: number) => Promise<void> | void) | null = null

  const touch = (row: FeedRow) => (row.updatedAt = clock += 1000)
  const refueling = (r: FeedRow) => ({
    __typename: 'Refueling', id: r.id, vehicleId: r.vehicleId, date: r.date, volume: r.volume, totalCost: 100, discount: null, currency: 'EUR', pricePerUnit: 100 / r.volume, pricePerUnitBeforeDiscount: 100 / r.volume,
    consumption: null, odometer: 1000, isFullTank: true, missedPreviousFillUp: false, note: null, reviewState: 'NONE', filledFromPhoto: [], canEdit: true,
    canDelete: true, deletedAt: null, version: 1, updatedAt: new Date(r.updatedAt).toISOString(), createdBy: null, photos: [],
  })
  const vehicle = (id: string) => ({
    __typename: 'Vehicle', id, version: 3, name: `Car ${id}`, licensePlate: null, fuelType: 'PETROL', pictureUrl: null, canEdit: true, logAccess: 'DELETE', refuelingCount: 0,
    units: { __typename: 'MeasurementUnits', distance: 'KILOMETERS', volume: 'LITERS' }, owner: null,
  })

  // Every field the Recurring tab reads (RecurringFields), so the answer kept for it works offline.
  const schedule = (vehicleId: string) => ({
    __typename: 'RecurringExpenseInfo', id: `s-${vehicleId}`, version: 1, title: 'Insurance', category: 'Fees', note: null, kind: 'TIME', intervalMonths: 12,
    intervalDistance: null, lastDoneDate: '2026-01-15', lastDoneOdometer: null, warnDays: 30, warnDistance: 500,
    status: { __typename: 'RecurrenceStatusInfo', state: 'UPCOMING', limit: 'TIME', dueDate: '2027-01-15', dueOdometer: null, daysLeft: 100, distanceLeft: null },
  })

  const handlers = [
    graphql.query('OfflineSettings', () => HttpResponse.json({ data: { offlineSettings: { __typename: 'OfflineSettingsInfo', defaultWindow: rule, vehicles: [] } } })),
    graphql.query('OfflineChanges', async ({ variables }) => {
      const input = variables.input as { vehicleId: string; from?: string | null; since?: string | null; after?: string | null; take: number }
      asked.push(input)
      await onAsk?.(asked.length)
      if (failAfter !== null && asked.length > failAfter) return HttpResponse.error()
      if (refuseCursors && input.after)
        return HttpResponse.json({ data: null, errors: [{ message: 'Cannot continue.', extensions: { code: 'VALIDATION_FAILED', key: 'sync.cursorInvalid' } }] })
      const cursor = input.after
        ? (JSON.parse(atob(input.after)) as { from: string | null; since: number | null; watermark: number; skip: number })
        : { from: input.since ? null : (input.from ?? null), since: input.since ? Date.parse(input.since) : null, watermark: clock, skip: 0 }
      if (cursor.since !== null && resyncNext) {
        resyncNext = false
        return HttpResponse.json({ data: { offlineChanges: { __typename: 'OfflineChanges', vehicle: vehicle(input.vehicleId), refuelings: [], expenses: [], recurring: [], tyres: null, removed: [], next: null, watermark: new Date(clock).toISOString(), resync: true } } })
      }
      const matching = rows
        .filter((r) => r.vehicleId === input.vehicleId && r.kind === 'refuelings')
        .filter((r) => (cursor.since !== null ? r.updatedAt >= cursor.since - OVERLAP : cursor.from === null || r.date >= cursor.from))
        .sort((a, b) => a.updatedAt - b.updatedAt || a.id.localeCompare(b.id))
      const pageRows = matching.slice(cursor.skip, cursor.skip + input.take)
      const more = matching.length > cursor.skip + input.take
      return HttpResponse.json({
        data: {
          offlineChanges: {
            __typename: 'OfflineChanges',
            vehicle: vehicle(input.vehicleId),
            refuelings: pageRows.map(refueling),
            expenses: [],
            recurring: input.after ? [] : [schedule(input.vehicleId)],
            // A vehicle's tyres, whole on the first page (#287).
            tyres: input.after || tyresHidden ? null : { __typename: 'VehicleTyres', vehicleId: input.vehicleId, hasReadings: false, readings: [], days: [], sets: [] },
            removed: !input.after && cursor.since !== null ? removed.filter((t) => t.vehicleId === input.vehicleId && t.at >= cursor.since! - OVERLAP).map((t) => ({ __typename: 'RemovedEntity', type: t.type, id: t.id })) : [],
            next: more ? btoa(JSON.stringify({ ...cursor, skip: cursor.skip + input.take })) : null,
            watermark: new Date(cursor.watermark).toISOString(),
            resync: false,
          },
        },
      })
    }),
  ]

  return {
    handlers,
    asked,
    add(id: string, date: string, vehicleId = 'v1') {
      const row: FeedRow = { kind: 'refuelings', id, vehicleId, date, volume: 40, updatedAt: 0 }
      touch(row)
      rows.push(row)
      return row
    },
    edit(id: string, change: Partial<FeedRow>) {
      const row = rows.find((r) => r.id === id)!
      Object.assign(row, change)
      touch(row)
    },
    purge(id: string) {
      const index = rows.findIndex((r) => r.id === id)
      const [row] = rows.splice(index, 1)
      removed.push({ type: 'REFUELING', id, vehicleId: row.vehicleId, at: (clock += 1000) })
    },
    later: () => (clock += 10 * 60 * 1000),
    setRule: (next: string) => (rule = next),
    resync: () => (resyncNext = true),
    failAfter: (requests: number | null) => (failAfter = requests),
    /** A cursor of an older server version: refused, the device must start that download afresh. */
    refuseCursors: () => (refuseCursors = true),
    acceptCursors: () => (refuseCursors = false),
    /** The reader may no longer see the vehicle's logs: its tyres come as null on the first page too. */
    hideTyres: () => (tyresHidden = true),
    /** Runs before the n-th request is answered (something happening while a page is on its way). */
    onAsk: (hook: (n: number) => Promise<void> | void) => (onAsk = hook),
  }
}
