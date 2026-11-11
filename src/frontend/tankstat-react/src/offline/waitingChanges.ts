import { useEffect, useState } from 'react'
import { createdBy, type Change } from './changes.ts'
import { deviceData } from './deviceData.ts'
import { outbox, usePendingCount } from './outbox.ts'
import { snapshotKey } from './snapshotPolicy.ts'

/** What the Sync page says about one waiting change: the values it carries, or those of what it changes, as the device knows them. */
export interface WaitingChange {
  change: Change
  date?: string
  volume?: number | null
  totalCost?: number | null
  amount?: number | null
  currency?: string | null
  title?: string
  name?: string
  /** A visit's schedules. */
  titles?: string[]
}

export interface WaitingGroup {
  vehicleId: string
  /** Null when the device knows nothing of it (it was never downloaded). */
  vehicleName: string | null
  volumeUnit: string | null
  distanceUnit: string | null
  items: WaitingChange[]
}

/**
 * The changes waiting, grouped by vehicle in the order they were made, each named from what the device holds: the downloaded vehicles and
 * logs, the answers kept for the Recurring tab, and the changes themselves (a vehicle or schedule added here). Re-read when they change.
 */
export function useWaitingChanges(): WaitingGroup[] | null {
  const count = usePendingCount()
  return useDescribed(outbox.changes, count)
}

/** Any changes (those the server parked in the last sync too), named and grouped the same way; `version` says when to read again. */
export function useDescribed(changes: readonly Change[], version: unknown): WaitingGroup[] | null {
  const [groups, setGroups] = useState<WaitingGroup[] | null>(null)
  useEffect(() => {
    let live = true
    void describe(changes).then((g) => live && setGroups(g))
    return () => {
      live = false
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `version` stands for the changes
  }, [version])
  return groups
}

async function describe(changes: readonly Change[]): Promise<WaitingGroup[]> {
  const rows = await deviceData.rows()
  const vehicles = new Map((rows ? await rows.vehicles() : []).map((v) => [v.id, v]))
  const names = new Map<string, string>([...vehicles].map(([id, v]) => [id, v.name]))
  for (const c of changes) if (c.entity === 'vehicles' && c.input?.name) names.set(c.targetId, String(c.input.name))

  const schedules = new Map<string, string>()
  for (const vehicleId of new Set(changes.filter((c) => c.entity === 'recurring').map((c) => c.vehicleId))) {
    const kept = (await deviceData.read(snapshotKey('RecurringExpenses', { vehicleId })))?.data as { vehicle?: { recurring?: { id: string; title: string }[] } } | undefined
    for (const s of kept?.vehicle?.recurring ?? []) schedules.set(s.id, s.title)
  }
  for (const c of changes) if (c.entity === 'recurring' && c.input?.title && c.action !== 'markDone') schedules.set(c.targetId, String(c.input.title))

  // Tyre changes are named by their set (#287): from the kept answer of the Tyres tab, a set added here by its own change; an installation's
  // and a tyre log line's by the set they belong to (#288). An answer an older build kept (#288: its sets have stretches, no installations,
  // and days, no lines) still names the sets.
  const tyreSets = new Map<string, string>()
  const installationSet = new Map<string, string>()
  for (const vehicleId of new Set(changes.filter((c) => c.entity === 'tyres').map((c) => c.vehicleId))) {
    const kept = (await deviceData.read(snapshotKey('VehicleTyres', { vehicleId })))?.data as
      | { vehicleTyres?: { sets?: { set: { id: string; name: string }; installations?: { installation: { id: string } }[] }[]; entries?: { id: string; setId?: string | null }[] } | null }
      | undefined
    for (const s of kept?.vehicleTyres?.sets ?? []) {
      tyreSets.set(s.set.id, s.set.name)
      for (const x of s.installations ?? []) installationSet.set(x.installation.id, s.set.id)
    }
    for (const e of kept?.vehicleTyres?.entries ?? []) if (e.setId) installationSet.set(e.id, e.setId)
  }
  for (const c of changes) {
    if (c.action === 'addTyreSet' || c.action === 'updateTyreSet') tyreSets.set(String(c.input?.id), String(c.input?.name))
    for (const id of createdBy(c)) if (typeof c.input?.setId === 'string') installationSet.set(id, c.input.setId)
    if (c.action === 'addTyreSet' && typeof c.input?.installationId === 'string') installationSet.set(c.input.installationId, String(c.input.id))
  }
  const tyreSetOf = (c: Change, input: Record<string, unknown>) => {
    const swaps = Array.isArray(input.swaps) ? (input.swaps as { setId: string }[]) : []
    const ids = [input.setId, ...swaps.map((s) => s.setId), installationSet.get(c.targetId), input.id].filter((id): id is string => typeof id === 'string')
    return [...new Set(ids.map((id) => tyreSets.get(id)).filter((n): n is string => !!n))].join(', ') || undefined
  }

  const items = await Promise.all(
    changes.map(async (change): Promise<WaitingChange> => {
      const input = change.input ?? {}
      switch (change.entity) {
        case 'refuelings':
        case 'expenses': {
          const row = change.action === 'add' ? undefined : await rows?.log(change.entity, change.targetId)
          const values = { ...row, ...input }
          return {
            change, date: values.date as string | undefined, volume: values.volume as number | null | undefined, totalCost: values.totalCost as number | null | undefined,
            amount: values.amount as number | null | undefined, currency: values.currency as string | null | undefined, title: values.title as string | undefined,
          }
        }
        case 'vehicles':
          return { change, name: names.get(change.targetId) }
        case 'recurring':
          return change.action === 'markDone'
            ? { change, date: input.date as string, amount: input.amount as number | null, currency: input.currency as string | null, titles: (change.targetIds ?? []).map((id) => schedules.get(id) ?? '') }
            : { change, title: schedules.get(change.targetId) }
        case 'tyres': {
          const swaps = Array.isArray(input.swaps) ? (input.swaps as { day: string }[]) : []
          const date = (input.day ?? input.from ?? [...swaps].map((s) => s.day).sort()[0]) as string | undefined
          return { change, name: tyreSetOf(change, input), date }
        }
      }
    }),
  )

  const groups = new Map<string, WaitingGroup>()
  for (const item of items) {
    const id = item.change.vehicleId
    const vehicle = vehicles.get(id) as { units?: { volume?: string; distance?: string } } | undefined
    const added = changes.find((c) => c.entity === 'vehicles' && c.action === 'add' && c.targetId === id)?.input?.units as { volume?: string; distance?: string } | undefined
    const group = groups.get(id) ?? {
      vehicleId: id,
      vehicleName: names.get(id) ?? null,
      volumeUnit: vehicle?.units?.volume ?? added?.volume ?? null,
      distanceUnit: vehicle?.units?.distance ?? added?.distance ?? null,
      items: [],
    }
    group.items.push(item)
    groups.set(id, group)
  }
  return [...groups.values()]
}
