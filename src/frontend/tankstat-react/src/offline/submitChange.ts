import type { ApolloClient } from '@apollo/client'
import { connectivity } from './connectivity.ts'
import { isConnectionFailure } from './errors.ts'
import { keptPhotosOf, type ChangeEntity } from './changes.ts'
import { baseOf } from './conflicts.ts'
import { outbox, type ChangeDraft } from './outbox.ts'

export type Submitted<T> = { queued: true } | { queued: false; result: T }

/** Whether a change of this vehicle is kept on the device rather than sent: the server is out of reach, or its changes wait (in order). */
export const willKeep = (vehicleId: string) => !connectivity.reachable || outbox.vehicleIds().has(vehicleId)

/**
 * Every change of a refuelling or an expense goes through here. Kept on the device (`outbox.ts`) while the server is out of reach, when the
 * vehicle already has changes waiting (they keep their order), or when the request fails for want of the server (with the same id: should
 * the server have saved it after all, the add sent again later is answered with what it saved); otherwise sent as always, and an error
 * the server gave is the caller's to show. A kept change makes the screen ask again, so the device's own answers show it (`offlineLink.ts`).
 * A kept edit or trash takes along the values the entry had at the version it was made from (`base`), should the server park it.
 */
export async function submitChange<T>(client: ApolloClient, change: ChangeDraft, send: () => Promise<T>): Promise<Submitted<T>> {
  // A change with photos kept on this device waits too: they reach the server as drafts right before it (`push.ts`).
  if (willKeep(change.vehicleId) || keptPhotosOf({ ...change, seq: 0, createdAt: 0 }).length > 0) return keepChange(client, change)
  try {
    return { queued: false, result: await send() }
  } catch (error) {
    // The request may have reached the server (only the answer was lost): kept as sent, so nothing later folds into it.
    if (isConnectionFailure(error)) return keepChange(client, { ...change, sent: true })
    throw error
  }
}

/**
 * Keeps the change on this device whatever the connection: what `submitChange` does when it cannot send, and for a change that always
 * waits. The screen is asked again afterwards, unless the caller keeps several and asks once (`refetch: false`).
 */
export async function keepChange(client: ApolloClient, change: ChangeDraft, { refetch = true } = {}): Promise<{ queued: true }> {
  const base = (change.action === 'update' || change.action === 'trash') && !change.base ? baseOf(client, change.entity, change.targetId, change.expectedVersion) : undefined
  await outbox.enqueue(base ? { ...change, base } : change)
  if (refetch) void client.refetchQueries({ include: 'active' }).catch(() => undefined)
  return { queued: true }
}

/** Takes a waiting change back (it is never sent), and the screen shows what the server has again. */
export async function discardChange(client: ApolloClient, id: string): Promise<void> {
  await outbox.discard(id)
  void client.refetchQueries({ include: 'active' }).catch(() => undefined)
}

/** Keep: takes back the trash (or deletion) waiting for something, so it stays. */
export async function keepEntry(client: ApolloClient, entity: ChangeEntity, id: string): Promise<void> {
  const trash = outbox.changes.find((c) => c.entity === entity && c.action === 'trash' && c.targetId === id)
  if (trash) await discardChange(client, trash.id)
}

/**
 * Undo of a trash (the toast after it): one kept on this device (`queued`) is taken back, as Keep does, never answered with a restore
 * (a restore sent for something the server never trashed, after Keep took the trash back, would only be refused); one the server has
 * is restored.
 */
export async function undoTrash<T>(client: ApolloClient, entity: ChangeEntity, id: string, queued: boolean, restore: () => Promise<T>): Promise<void> {
  if (queued) await keepEntry(client, entity, id)
  else await restore()
}
