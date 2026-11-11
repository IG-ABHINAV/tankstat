import type { ParkedChangeFieldsFragment, SyncChangeKind } from '../gql/generated.ts'
import type { Change, ChangeAction, ChangeEntity } from './changes.ts'
import { isTyreAction, tyreTarget } from './tyreChanges.ts'

/** What a change the server knows by its kind (LOG_REFUELING, ...) is on this device: the entity and the action, as the outbox says. */
export const SYNC_KINDS: Record<SyncChangeKind, [ChangeEntity, ChangeAction]> = {
  LOG_REFUELING: ['refuelings', 'add'],
  UPDATE_REFUELING: ['refuelings', 'update'],
  DELETE_REFUELING: ['refuelings', 'trash'],
  RESTORE_REFUELING: ['refuelings', 'restore'],
  ADD_EXPENSE: ['expenses', 'add'],
  UPDATE_EXPENSE: ['expenses', 'update'],
  DELETE_EXPENSE: ['expenses', 'trash'],
  RESTORE_EXPENSE: ['expenses', 'restore'],
  ADD_RECURRING_EXPENSE: ['recurring', 'add'],
  UPDATE_RECURRING_EXPENSE: ['recurring', 'update'],
  DELETE_RECURRING_EXPENSE: ['recurring', 'trash'],
  MARK_RECURRING_EXPENSES_DONE: ['recurring', 'markDone'],
  ADD_VEHICLE: ['vehicles', 'add'],
  UPDATE_VEHICLE: ['vehicles', 'update'],
  DELETE_VEHICLE: ['vehicles', 'trash'],
  RESTORE_VEHICLE: ['vehicles', 'restore'],
  ADD_REFUELING_PHOTO: ['refuelings', 'addPhoto'],
  REMOVE_REFUELING_PHOTO: ['refuelings', 'removePhoto'],
  ADD_EXPENSE_PHOTO: ['expenses', 'addPhoto'],
  REMOVE_EXPENSE_PHOTO: ['expenses', 'removePhoto'],
  SET_VEHICLE_PICTURE: ['vehicles', 'setPicture'],
  REMOVE_VEHICLE_PICTURE: ['vehicles', 'removePicture'],
  // A tyre change's action is the field it is sent as (#287).
  ADD_TYRE_SET: ['tyres', 'addTyreSet'],
  UPDATE_TYRE_SET: ['tyres', 'updateTyreSet'],
  SET_TYRE_ROUGH_DISTANCE: ['tyres', 'setTyreRoughDistance'],
  RETIRE_TYRE_SET: ['tyres', 'retireTyreSet'],
  UNRETIRE_TYRE_SET: ['tyres', 'unretireTyreSet'],
  DELETE_TYRE_SET: ['tyres', 'deleteTyreSet'],
  RESTORE_TYRE_SET: ['tyres', 'restoreTyreSet'],
  SWAP_TYRES: ['tyres', 'swapTyres'],
  CHANGE_TYRE_POSITIONS: ['tyres', 'changeTyrePositions'],
  ADD_PAST_TYRE_SWAPS: ['tyres', 'addPastTyreSwaps'],
  ADD_TYRE_INSTALLATION: ['tyres', 'addTyreInstallation'],
  UPDATE_TYRE_INSTALLATION: ['tyres', 'updateTyreInstallation'],
  DELETE_TYRE_INSTALLATION: ['tyres', 'deleteTyreInstallation'],
  RESTORE_TYRE_INSTALLATION: ['tyres', 'restoreTyreInstallation'],
  SET_TYRE_LOG_DAY: ['tyres', 'setTyreLogDay'],
}

/** The ChangeInput field of each kind (LOG_REFUELING: logRefueling). */
const fieldOf = (kind: SyncChangeKind) => kind.toLowerCase().replace(/_([a-z])/g, (_, c: string) => c.toUpperCase())

/** The key of the words for a kind of change (`sync.kinds.refuelings.add`: "New refuelling"). */
export const syncKindKey = (entity: ChangeEntity, action: ChangeAction) => `sync.kinds.${entity}.${action}` as 'sync.kinds.refuelings.add'

export const kindKeyOf = (kind: string): ReturnType<typeof syncKindKey> | null =>
  kind in SYNC_KINDS ? syncKindKey(...SYNC_KINDS[kind as keyof typeof SYNC_KINDS]) : null

/**
 * A change the server parked, as a change of this device (the inverse of `push.ts` `toChangeInput`), so it is named and shown like one
 * that waits here; null for a kind this build does not know. `change` is the ChangeInput as the server keeps it (JSON; absent operations left out or null).
 */
export function fromParked(parked: Pick<ParkedChangeFieldsFragment, 'id' | 'kind' | 'vehicleId' | 'targetId' | 'change' | 'receivedAt'> & { base?: string | null }): Change | null {
  // A kind this build does not know (one a newer server adds while this tab waits for its update): left out, never thrown, so
  // the screens that list parked changes keep working.
  if (!(parked.kind in SYNC_KINDS)) return null
  const [entity, action] = SYNC_KINDS[parked.kind as keyof typeof SYNC_KINDS]
  let envelope: Record<string, unknown> = {}
  try {
    envelope = JSON.parse(parked.change) as Record<string, unknown>
  } catch {
    // shown by its kind alone
  }
  const operation = envelope[fieldOf(parked.kind)]
  // A tyre change that takes a bare id is kept on the device as `{ id }` (`tyreChanges.ts`).
  const bareTyreId = entity === 'tyres' && typeof operation === 'string'
  const input = operation && typeof operation === 'object' ? (operation as Record<string, unknown>) : bareTyreId ? { id: operation } : undefined
  const targetId =
    parked.targetId ??
    (isTyreAction(action) ? tyreTarget(action, input) || parked.id : typeof input?.id === 'string' ? input.id : typeof operation === 'string' ? operation : parked.id)
  const base = baseOfParked(parked.base)
  return {
    id: parked.id,
    seq: 0,
    createdAt: Date.parse(parked.receivedAt),
    entity,
    action,
    vehicleId: parked.vehicleId ?? (typeof input?.vehicleId === 'string' ? input.vehicleId : entity === 'vehicles' ? targetId : ''),
    targetId,
    ...(action === 'markDone' ? { targetIds: (input?.ids as string[] | undefined) ?? [] } : {}),
    ...(input ? { input } : {}),
    expectedVersion: typeof envelope.expectedVersion === 'number' ? envelope.expectedVersion : null,
    ...(base ? { base } : {}),
  }
}

/** The values a parked change was made from, as the device sent them (JSON of an object), or none. */
function baseOfParked(json: string | null | undefined): Record<string, unknown> | undefined {
  if (!json) return undefined
  try {
    const parsed: unknown = JSON.parse(json)
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? (parsed as Record<string, unknown>) : undefined
  } catch {
    return undefined // merged as if none were known
  }
}

/**
 * Whether applying a parked change anyway can work: not when the sender's own try found what it changes gone, or they may not (those can
 * only be discarded). Someone else deciding (the owner of the vehicle) is not the sender: what the sender could no longer see (their
 * access ended) may well be there for them, so the server decides. "Not found" is not final either when the server shows what the change
 * concerns (`current`: it was moved to the trash meanwhile, and can come back). Everything else is tried as they would online; a rule
 * that still refuses says so.
 */
export const canForce = (reasonKey: string | null | undefined, bySender = true, current: unknown = null) =>
  !!reasonKey && !reasonKey.startsWith('auth.') && (!bySender || !!current || !reasonKey.endsWith('.notFound'))
