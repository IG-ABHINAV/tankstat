import { act, renderHook, waitFor } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { Change } from '../../src/offline/changes.ts'
import { deviceData } from '../../src/offline/deviceData.ts'
import { memoryStorage } from '../../src/offline/deviceStorage.ts'
import { outbox } from '../../src/offline/outbox.ts'
import { snapshotKey } from '../../src/offline/snapshotPolicy.ts'
import { useDescribed, useWaitingChanges } from '../../src/offline/waitingChanges.ts'

let seq = 0
const change = (fields: Partial<Change> & Pick<Change, 'entity' | 'action' | 'targetId'>): Change => ({
  id: `c${++seq}`, seq, createdAt: seq, vehicleId: 'v1', ...fields,
})

async function described(changes: Change[]) {
  const { result } = renderHook(() => useDescribed(changes, 1))
  await waitFor(() => expect(result.current).not.toBeNull())
  return result.current!
}

describe('the changes waiting, as the Sync page names them', () => {
  it('names schedules from the Recurring tab kept on the device and from the changes, and a visit by the schedules it marks', async () => {
    deviceData.reset(memoryStorage())
    await deviceData.signedIn('alice')
    await deviceData.rows().then((rows) => rows!.putVehicles([{ id: 'v1', name: 'Octavia', logAccess: 'DELETE', units: { volume: 'LITERS', distance: 'KILOMETERS' } }]))
    await deviceData.keep(snapshotKey('RecurringExpenses', { vehicleId: 'v1' }), {
      vehicle: { recurring: [{ id: 's1', title: 'Oil change' }, { id: 's2', title: 'Inspection' }] },
    })
    const changes = [
      change({ entity: 'recurring', action: 'update', targetId: 's1', input: { id: 's1', intervalMonths: 12 } }),
      change({ entity: 'recurring', action: 'add', targetId: 's3', input: { id: 's3', title: 'Tyres' } }),
      change({ entity: 'recurring', action: 'markDone', targetId: 'e1', targetIds: ['s2', 's3', 'gone'], input: { ids: ['s2', 's3', 'gone'], date: '2026-10-05', amount: 90, currency: 'EUR', title: 'Visit' } }),
      change({ entity: 'recurring', action: 'markDone', targetId: 'e2', input: { date: '2026-10-06', amount: null, currency: null } }),
    ]

    const [group] = await described(changes)

    expect(group).toMatchObject({ vehicleId: 'v1', vehicleName: 'Octavia', volumeUnit: 'LITERS', distanceUnit: 'KILOMETERS' })
    expect(group.items.map((i) => i.title ?? i.titles)).toEqual(['Oil change', 'Tyres', ['Inspection', 'Tyres', ''], []]) // the visit's title is not a schedule's
    expect(group.items[2]).toMatchObject({ date: '2026-10-05', amount: 90, currency: 'EUR' })
  })

  it('without any data open, names what the changes themselves say and leaves the rest unnamed', async () => {
    deviceData.reset()
    const changes = [
      change({ entity: 'refuelings', action: 'update', targetId: 'r1', input: { id: 'r1', date: '2026-10-01', volume: 30, totalCost: 15000, currency: 'HUF' } }),
      change({ entity: 'vehicles', action: 'add', vehicleId: 'v2', targetId: 'v2', input: { id: 'v2', name: 'Golf', units: { volume: 'GALLONS', distance: 'MILES' } } }),
      change({ entity: 'expenses', action: 'add', vehicleId: 'v2', targetId: 'x1', input: { id: 'x1', title: 'Wash', amount: 10, currency: 'USD' } }),
    ]

    const groups = await described(changes)

    expect(groups).toHaveLength(2)
    expect(groups[0]).toMatchObject({ vehicleId: 'v1', vehicleName: null, volumeUnit: null, distanceUnit: null })
    expect(groups[0].items[0]).toMatchObject({ date: '2026-10-01', volume: 30, totalCost: 15000, currency: 'HUF' })
    expect(groups[1]).toMatchObject({ vehicleId: 'v2', vehicleName: 'Golf', volumeUnit: 'GALLONS', distanceUnit: 'MILES' }) // added here
    expect(groups[1].items.map((i) => i.name ?? i.title)).toEqual(['Golf', 'Wash'])
  })

  it('follows the outbox: an edit or a trash of a downloaded log shows its stored values, with the edit over them', async () => {
    deviceData.reset(memoryStorage())
    await deviceData.signedIn('alice')
    await outbox.reload()
    const rows = (await deviceData.rows())!
    await rows.putLogs('refuelings', [{ __typename: 'Refueling', id: 'r1', vehicleId: 'v1', date: '2026-09-01', volume: 40, totalCost: 20000, discount: null, currency: 'HUF', pulledAt: 1 }])
    await rows.putLogs('expenses', [{ __typename: 'Expense', id: 'e1', vehicleId: 'v1', date: '2026-09-02', title: 'Wash', amount: 3000, currency: 'HUF', pulledAt: 1 }])
    const { result } = renderHook(() => useWaitingChanges())
    await waitFor(() => expect(result.current).toEqual([]))

    await act(() => outbox.enqueue({ id: 'u', entity: 'refuelings', action: 'update', vehicleId: 'v1', targetId: 'r1', input: { id: 'r1', volume: 42 }, expectedVersion: 1 }))
    await act(() => outbox.enqueue({ id: 't', entity: 'expenses', action: 'trash', vehicleId: 'v1', targetId: 'e1', expectedVersion: 1 }))
    await act(() => outbox.enqueue({ id: 's1', entity: 'recurring', action: 'add', vehicleId: 'v1', targetId: 's1', input: { id: 's1', title: 'Oil' } }))

    await waitFor(() => expect(result.current?.[0].items).toHaveLength(3))
    const [edit, trash, schedule] = result.current![0].items
    expect(edit).toMatchObject({ date: '2026-09-01', volume: 42, totalCost: 20000, currency: 'HUF' })
    expect(trash).toMatchObject({ date: '2026-09-02', title: 'Wash', amount: 3000 })
    expect(schedule).toMatchObject({ title: 'Oil' }) // nothing kept of the Recurring tab: the change names it
  })
})
