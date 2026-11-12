import { useEffect, useState } from 'react'
import { deviceData } from '../offline/deviceData.ts'
import { outbox, usePendingCount } from '../offline/outbox.ts'
import { snapshotKey } from '../offline/snapshotPolicy.ts'
import { noTyres, replayTyres, touchesTyres } from './replay.ts'
import type { Tyres } from './tyres.ts'

/** What waiting changes did to a vehicle's tyres, as the tab marks it. */
export interface PendingTyres {
  /** The sets touched, each `new` (added here) or `changed`. */
  cards: ReadonlyMap<string, 'new' | 'changed'>
  /** Whose costs are out of date until synced (#288): set ids, `NO_SET` for the lines of no set (`Replayed.costs`). */
  costs: ReadonlySet<string>
}

const NONE: PendingTyres = { cards: new Map(), costs: new Set() }

/**
 * The sets of a vehicle that tyre changes waiting on this device touched (#287, Q3), each `new` (added here) or `changed`: the same replay
 * the device's answer is made by, over the server's last answer kept here, so the tab marks those cards *New · not synced* or *Changed ·
 * not synced* and says their figures are updated once synced; and whose costs those changes, or a waiting change of a line's expense on the
 * Expenses tab, put out of date (#288).
 */
export function usePendingTyres(vehicleId: string): PendingTyres {
  const count = usePendingCount()
  const [pending, setPending] = useState<PendingTyres>(NONE)
  useEffect(() => {
    let live = true
    const waiting = outbox.changes.filter((c) => (touchesTyres(c) || c.entity === 'expenses') && c.vehicleId === vehicleId)
    // Nothing waiting: nothing to read either.
    const read = waiting.length === 0 ? Promise.resolve(undefined) : deviceData.read(snapshotKey('VehicleTyres', { vehicleId })).catch(() => undefined)
    void read.then((kept) => {
      if (!live) return
      if (waiting.length === 0) return setPending(NONE)
      const base = (kept?.data as { vehicleTyres?: Tyres | null } | undefined)?.vehicleTyres ?? noTyres(vehicleId)
      const replayed = replayTyres(base, waiting)
      setPending({ cards: new Map([...replayed.touched].map((id) => [id, replayed.added.has(id) ? 'new' : 'changed'] as const)), costs: replayed.costs })
    })
    return () => {
      live = false
    }
  }, [count, vehicleId])
  return pending
}
