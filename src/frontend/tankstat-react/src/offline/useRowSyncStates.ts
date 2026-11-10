import { useMemo } from 'react'
import type { ParkedChangeFieldsFragment } from '../gql/generated.ts'
import type { ChangeEntity, PendingMark } from './changes.ts'
import { usePendingMarks } from './outbox.ts'
import { fromParked } from './syncKinds.ts'
import { useParkedChanges } from './useParkedChanges.ts'

/** Where a row of a list stands with the server: a change of it waits on this device, or the server could not apply one. */
export type RowSyncState = { kind: PendingMark } | { kind: 'parked'; parked: ParkedChangeFieldsFragment }

/**
 * Each row's state for a list's sync column (`SyncStateButton`), by the row's id: a change waiting on this device says most (it is what
 * will happen next); otherwise a change the server parked that the user may see (asked while the server can be reached). A visit is a
 * state of each schedule it covers.
 */
export function useRowSyncStates(entity: ChangeEntity): (id: string) => RowSyncState | null {
  const markOf = usePendingMarks(entity)
  const { parked } = useParkedChanges(true, true)
  const parkedOf = useMemo(() => {
    const byTarget = new Map<string, ParkedChangeFieldsFragment>()
    for (const p of parked ?? []) {
      const change = fromParked(p)
      if (!change || change.entity !== entity) continue
      for (const id of change.action === 'markDone' ? (change.targetIds ?? []) : [change.targetId]) if (!byTarget.has(id)) byTarget.set(id, p)
    }
    return byTarget
  }, [parked, entity])
  return useMemo(
    () => (id: string) => {
      const mark = markOf(id)
      if (mark) return { kind: mark }
      const p = parkedOf.get(id)
      return p ? { kind: 'parked', parked: p } : null
    },
    [markOf, parkedOf],
  )
}
