import { useEffect, useState } from 'react'
import { deviceData } from '../offline/deviceData.ts'
import { outbox, usePendingCount } from '../offline/outbox.ts'
import { snapshotKey } from '../offline/snapshotPolicy.ts'
import { noTyres, replayTyres } from './replay.ts'
import type { Tyres } from './tyres.ts'

const NONE: ReadonlyMap<string, 'new' | 'changed'> = new Map()

/**
 * The sets of a vehicle that tyre changes waiting on this device touched (#287, Q3), each `new` (added here) or `changed`: the same replay
 * the device's answer is made by, over the server's last answer kept here, so the tab marks those cards *New · not synced* or *Changed ·
 * not synced* and says their figures are updated once synced.
 */
export function usePendingTyres(vehicleId: string): ReadonlyMap<string, 'new' | 'changed'> {
  const count = usePendingCount()
  const [touched, setTouched] = useState<ReadonlyMap<string, 'new' | 'changed'>>(NONE)
  useEffect(() => {
    let live = true
    const waiting = outbox.changes.filter((c) => c.entity === 'tyres' && c.vehicleId === vehicleId)
    // Nothing waiting: nothing to read either.
    const read = waiting.length === 0 ? Promise.resolve(undefined) : deviceData.read(snapshotKey('VehicleTyres', { vehicleId })).catch(() => undefined)
    void read.then((kept) => {
      if (!live) return
      if (waiting.length === 0) return setTouched(NONE)
      const base = (kept?.data as { vehicleTyres?: Tyres | null } | undefined)?.vehicleTyres ?? noTyres(vehicleId)
      const replayed = replayTyres(base, waiting)
      setTouched(new Map([...replayed.touched].map((id) => [id, replayed.added.has(id) ? 'new' : 'changed'] as const)))
    })
    return () => {
      live = false
    }
  }, [count, vehicleId])
  return touched
}
