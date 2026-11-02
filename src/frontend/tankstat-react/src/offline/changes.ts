/**
 * Changes kept on this device until the server can take them (the outbox, `outbox.ts`). Pure: what a change is, and how a new one folds
 * into the ones already waiting, so the server only ever gets what is left to do (`collapse`).
 */
export type LogEntity = 'refuelings' | 'expenses'
/** What a change can concern: the logs, a vehicle, a recurring expense (schedule). */
export type ChangeEntity = LogEntity | 'vehicles' | 'recurring'
/**
 * `trash` of a schedule deletes it for good (schedules have no trash); `markDone` is a service visit of one or more schedules; `addPhoto`
 * and `removePhoto` change the photos of a saved log (input `{ key }`, a photo kept on this device, or `{ imageId }`); `setPicture` (input
 * `{ key }`, a picture kept on this device) and `removePicture` change a vehicle's picture.
 */
export type ChangeAction = 'add' | 'update' | 'trash' | 'restore' | 'markDone' | 'addPhoto' | 'removePhoto' | 'setPicture' | 'removePicture'

export interface Change {
  /** The change's own id; for an add, the id of what it adds (the client chose it, the server keeps it). */
  id: string
  /** The order changes were made in (the server takes them in this order). */
  seq: number
  createdAt: number
  entity: ChangeEntity
  action: ChangeAction
  vehicleId: string
  /** What it changes: the entity's id (an add's own id); for a visit, the id of the expense it logs (or its own id). */
  targetId: string
  /** A visit's schedules. */
  targetIds?: string[]
  /**
   * What the mutation takes (`LogRefuelingInput`, `UpdateExpenseInput`, ...); none for a restore. A trash carries the values of the edit it
   * took the place of (never sent with it), so taking the trash back (a restore, Keep, Remove) brings the edit back.
   */
  input?: Record<string, unknown>
  /** The log's version this change was made from (`version` on the server): a change made meanwhile is noticed at upload. */
  expectedVersion?: number | null
  /**
   * The values the entry had at that version (an edit's or a trash's, as this screen held them; `conflicts.ts` `baseOf`): should the
   * server park the change, whoever decides can tell what this device changed from what was changed meanwhile, and merge the two.
   */
  base?: Record<string, unknown>
  /**
   * Sent to the server at least once, so it may have been applied with the answer lost: nothing folds into it any more (it would be answered
   * as already done, and what folded in lost); a later change of the same log follows it as a change of its own.
   */
  sent?: boolean
}

/** How the device marks something with a change waiting. */
export type PendingMark = 'new' | 'changed' | 'deleted' | 'restored' | 'done'

const MARKS: Record<ChangeAction, PendingMark> = {
  add: 'new', update: 'changed', trash: 'deleted', restore: 'restored', markDone: 'done', addPhoto: 'changed', removePhoto: 'changed',
  setPicture: 'changed', removePicture: 'changed',
}
export const markOf = (change: Change): PendingMark => MARKS[change.action]

/** The prefix of a photo kept on this device (`keptPhotos.ts`) where a draft's id would be. */
export const KEPT = 'local:'
export const isKept = (id: unknown): id is string => typeof id === 'string' && id.startsWith(KEPT)

/** The photos kept on this device that a change carries: those picked for a new log or a visit, one added to a saved log, a vehicle's picture. */
export function keptPhotosOf(change: Change): string[] {
  const ids = Array.isArray(change.input?.photoIds) ? (change.input.photoIds as unknown[]) : []
  return [...ids, change.action === 'addPhoto' || change.action === 'setPicture' ? change.input?.key : undefined].filter(isKept)
}

/**
 * The changes that can be edited before they are sent or applied (Waiting to sync): adds and edits of logs, vehicles and schedules. A
 * trash, a restore, a visit or a photo is only kept, removed, applied or discarded.
 */
export const canEdit = (change: Change) => change.action === 'add' || change.action === 'update'

const isPhoto = (c: Change) => c.action === 'addPhoto' || c.action === 'removePhoto'
const isPicture = (c: Change) => c.action === 'setPicture' || c.action === 'removePicture'

/** The fields an update sets (and carries over onto an add that is still waiting: the add's own id and vehicle stay). */
export const UPDATABLE: Record<ChangeEntity, readonly string[]> = {
  refuelings: ['date', 'volume', 'totalCost', 'discount', 'currency', 'odometer', 'isFullTank', 'missedPreviousFillUp', 'note'],
  expenses: ['date', 'title', 'category', 'amount', 'discount', 'currency', 'odometer', 'note'],
  vehicles: ['name', 'licensePlate', 'fuelType', 'units'],
  recurring: ['title', 'category', 'note', 'kind', 'intervalMonths', 'intervalDistance', 'lastDoneDate', 'lastDoneOdometer', 'warnDays', 'warnDistance'],
}

/**
 * The changes waiting after `incoming` joins them. Changes of one log fold into one:
 * - an add then an update is the add with the new values; an add then a trash is nothing at all;
 * - an update then an update is the last one, from the first one's version (and values, `base`); an update then a trash is the trash,
 *   from that version, keeping the edit's values so that taking the trash back brings the edit back;
 * - a trash then a restore, or a restore then a trash, is nothing (back where the server is, or the edit the trash took the place of);
 * - a second trash or restore of the same log, or an edit of one on its way to the trash, changes nothing.
 * A change that was sent already (`sent`) is never folded into: what follows it waits as a change of its own, from the version the log has
 * once it is applied. Anything else waits as it is, in order.
 */
export function collapse(existing: readonly Change[], incoming: Change): Change[] {
  // A visit never folds: each one is a visit of its own.
  if (incoming.action === 'markDone') return [...existing, incoming]
  // The expense a visit waiting here would log, trashed: the visit logs none (its schedules are still done), and nothing else of it waits.
  const visit = incoming.entity === 'expenses' && incoming.action === 'trash' ? existing.find((c) => isVisitOf(c, incoming.targetId) && !c.sent) : undefined
  if (visit) {
    const without = { ...visit, input: { ...visit.input, amount: null, currency: null, photoIds: null } }
    return existing.filter((c) => !(c.entity === 'expenses' && c.targetId === incoming.targetId)).map((c) => (c === visit ? without : c))
  }
  const sameLog = (c: Change) => c.action !== 'markDone' && !isPhoto(c) && !isPicture(c) && c.entity === incoming.entity && c.targetId === incoming.targetId
  const photosOfLog = (c: Change) => isPhoto(c) && c.entity === incoming.entity && c.targetId === incoming.targetId && !c.sent
  const waiting = existing.filter((c) => sameLog(c) && !c.sent)
  const sentBefore = existing.filter((c) => sameLog(c) && c.sent).at(-1)
  const others = existing.filter((c) => !waiting.includes(c))
  const add = waiting.find((c) => c.action === 'add')
  const update = waiting.find((c) => c.action === 'update')
  const trash = waiting.find((c) => c.action === 'trash')
  const restore = waiting.find((c) => c.action === 'restore')
  const next = sentBefore ? { ...incoming, expectedVersion: versionAfter(sentBefore) } : incoming

  switch (incoming.action) {
    case 'setPicture':
    case 'removePicture':
      if (trash) return [...existing] // on its way to the trash: the picture could not be changed after it
      // A vehicle's picture: only the last one counts, so it takes the place of the picture change waiting for the vehicle (one sent
      // already stays: it may be on the server). Its kept picture goes with it (the outbox removes the photos of what folds away).
      return [...existing.filter((c) => !(isPicture(c) && c.targetId === incoming.targetId && !c.sent)), incoming]
    case 'addPhoto': {
      if (trash) return [...existing] // on its way to the trash: a photo could not be added to it
      const key = incoming.input?.key
      if (add) return replace(existing, add, { ...add, input: { ...add.input, photoIds: [...((add.input?.photoIds as string[] | undefined) ?? []), key] } })
      return [...existing, incoming]
    }
    case 'removePhoto': {
      const imageId = incoming.input?.imageId
      if (isKept(imageId)) {
        // A photo kept here never reached the server: taking it back is enough.
        if (add) return replace(existing, add, { ...add, input: { ...add.input, photoIds: ((add.input?.photoIds as string[] | undefined) ?? []).filter((id) => id !== imageId) } })
        return existing.filter((c) => !(photosOfLog(c) && c.action === 'addPhoto' && c.input?.key === imageId))
      }
      if (trash || existing.some((c) => photosOfLog(c) && c.action === 'removePhoto' && c.input?.imageId === imageId)) return [...existing]
      return [...existing, incoming]
    }
    case 'add':
      return [...existing, incoming]
    case 'update':
      if (trash) return [...existing] // on its way to the trash: nothing to edit
      if (add) return replace(existing, add, { ...add, input: { ...add.input, ...pick(incoming.input, UPDATABLE[incoming.entity]) } })
      if (update) return replace(existing, update, { ...from(update, incoming), id: update.id, seq: update.seq, createdAt: update.createdAt })
      return [...existing, next]
    case 'trash': {
      if (trash) return [...existing] // already waiting to go
      if (add) return withoutDependents(others, incoming).filter((c) => !photosOfLog(c)) // never sent: nothing to take back, nor anything made to it
      // Photos of a log in the trash can be neither added nor removed: those changes go (a restore does not bring them back).
      const kept = existing.filter((c) => !photosOfLog(c))
      if (restore) return kept.filter((c) => c !== restore)
      if (update) return replace(kept, update, { ...from(update, incoming), id: update.id, seq: update.seq, createdAt: update.createdAt, input: update.input })
      return [...kept, next]
    }
    case 'restore':
      if (trash) return trash.input ? replace(existing, trash, asEdit(trash)) : existing.filter((c) => c !== trash)
      if (restore) return [...existing] // already waiting to come back
      return [...existing, next]
  }
}

/** `incoming` as made from where `first` started: its version and the values it had then (none known: none). */
const from = (first: Change, incoming: Change): Change => {
  const made: Change = { ...incoming, expectedVersion: first.expectedVersion }
  if (first.base) made.base = first.base
  else delete made.base
  return made
}

/** The version a log has once a change sent before is applied: an add's is the server's to say (no check), any other one is one more. */
const versionAfter = (sent: Change) => (sent.action === 'add' || sent.expectedVersion == null ? null : sent.expectedVersion + 1)

/** A trash that took the place of an edit, taken back: the edit again. */
const asEdit = (trash: Change): Change => ({ ...trash, action: 'update' })

/**
 * Takes back one waiting change (Keep, Remove on the Sync page): an add taken back takes what was made to it along, as a trash would; a
 * trash that took the place of an edit leaves the edit.
 */
export function discard(existing: readonly Change[], id: string): Change[] {
  const removed = existing.find((c) => c.id === id)
  if (removed?.action === 'trash' && removed.input) return replace(existing, removed, asEdit(removed))
  // A visit taken back logs no expense: what was made to that expense here goes with it.
  if (removed && isVisitOf(removed, removed.targetId)) return existing.filter((c) => c.id !== id && !(c.entity === 'expenses' && c.targetId === removed.targetId))
  const rest = existing.filter((c) => c.id !== id)
  return removed?.action === 'add' ? withoutDependents(rest, removed) : rest
}

/** A visit that logs the expense `expenseId` (one with an amount; its expense id is its target). */
const isVisitOf = (c: Change, expenseId: string) => c.action === 'markDone' && c.targetId === expenseId && c.input?.amount != null

/**
 * What else goes with an add that is taken back: a vehicle's logs, schedules and visits (they were made to a vehicle that never reached the
 * server), or a schedule's place in the visits waiting (a visit left with none is dropped).
 */
function withoutDependents(list: readonly Change[], removed: Change): Change[] {
  if (removed.entity === 'vehicles') return list.filter((c) => c.vehicleId !== removed.targetId)
  if (removed.entity !== 'recurring') return [...list]
  return list.flatMap((c) => {
    if (c.action !== 'markDone' || !c.targetIds?.includes(removed.targetId)) return [c]
    const left = c.targetIds.filter((id) => id !== removed.targetId)
    return left.length > 0 ? [{ ...c, targetIds: left, input: { ...c.input, ids: left } }] : []
  })
}

const replace = (list: readonly Change[], old: Change, next: Change) => list.map((c) => (c === old ? next : c))

function pick(input: Record<string, unknown> | undefined, keys: readonly string[]) {
  return Object.fromEntries(keys.filter((k) => input && k in input).map((k) => [k, input![k]]))
}
