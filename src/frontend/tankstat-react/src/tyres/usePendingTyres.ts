import { useEffect, useState } from 'react'
import { deviceData } from '../offline/deviceData.ts'
import { outbox, usePendingCount } from '../offline/outbox.ts'
import { snapshotKey } from '../offline/snapshotPolicy.ts'
import { noTyres, replayTyres } from './replay.ts'
import type { Tyres } from './tyres.ts'

const NONE: ReadonlySet<string> = new Set()

/**
 * The sets of a vehicle that tyre changes waiting on this device touched (#287, Q3): the same replay the device's answer is made by, over
 * the server's last answer kept here, so the tab marks those cards *Changed · not synced* and says their figures are updated once synced.
 */
export function usePendingTyres(vehicleId: string): ReadonlySet<string> {
  const count = usePendingCount()
  const [touched, setTouched] = useState<ReadonlySet<string>>(NONE)
  useEffect(() => {
    let live = true
    const waiting = outbox.changes.filter((c) => c.entity === 'tyres' && c.vehicleId === vehicleId)
    void deviceData
      .read(snapshotKey('VehicleTyres', { vehicleId }))
      .catch(() => undefined)
      .then((kept) => {
        const base = (kept?.data as { vehicleTyres?: Tyres | null } | undefined)?.vehicleTyres ?? noTyres(vehicleId)
        if (live) setTouched(waiting.length === 0 ? NONE : replayTyres(base, waiting).touched)
      })
    return () => {
      live = false
    }
  }, [count, vehicleId])
  return touched
}
