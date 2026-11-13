import type { ApolloClient } from '@apollo/client'
import { SyncChangesDocument, type ChangeInput, type SyncChangesMutation } from '../gql/generated.ts'
import { createdBy, isKept, keptPhotosOf, tyreNeedsOf, type Change } from './changes.ts'
import { withoutTypename } from './conflicts.ts'
import { connectivity } from './connectivity.ts'
import { deviceData } from './deviceData.ts'
import { isConnectionFailure } from './errors.ts'
import { keptPhotos } from './keptPhotos.ts'
import { outbox } from './outbox.ts'
import { BARE_ID, isTyreAction, sentInput } from './tyreChanges.ts'

/** Changes per request (the server takes at most 200). */
export const CHUNK_SIZE = 20

type Result = SyncChangesMutation['syncChanges']['results'][number]

/** A change the server could not apply: it keeps it (parked) with the reason; the device shows it until the next sync. */
export interface ParkedChange {
  change: Change
  key: string
  args: Record<string, string>
}

export interface PushState {
  status: 'idle' | 'syncing'
  /**
   * What the last sync did (in this page's life). `photosLeftOut`: photos that were no longer on this device, their changes sent without
   * them. `photosWaiting`: photos the server would not take as drafts this time (full, refused): their changes were not sent, they wait.
   */
  last: { at: number; applied: number; parked: ParkedChange[]; interrupted: boolean; photosLeftOut: number; photosWaiting: number } | null
}

export interface PushDeps {
  client: ApolloClient
  device?: typeof deviceData
  /** The download that follows a sync, so what was applied comes back from the server (ids, versions, what it worked out). */
  pull?: () => Promise<void>
  chunkSize?: number
}

/** What each mutation's input may carry: a form may hold more (a client id twice, values only the screen uses). */
const FIELDS = {
  logRefueling: ['id', 'vehicleId', 'date', 'volume', 'totalCost', 'discount', 'currency', 'odometer', 'isFullTank', 'missedPreviousFillUp', 'note', 'photoIds'],
  updateRefueling: ['id', 'date', 'volume', 'totalCost', 'discount', 'currency', 'odometer', 'isFullTank', 'missedPreviousFillUp', 'note'],
  // `tyre`: an expense's tyre details (#290), the tyre log line(s) it is saved with; null on an edit leaves the lines as they are.
  addExpense: ['id', 'vehicleId', 'date', 'title', 'category', 'amount', 'discount', 'currency', 'odometer', 'note', 'photoIds', 'tyre'],
  updateExpense: ['id', 'date', 'title', 'category', 'amount', 'discount', 'currency', 'odometer', 'note', 'tyre'],
  addVehicle: ['id', 'name', 'licensePlate', 'fuelType', 'units'],
  updateVehicle: ['id', 'name', 'licensePlate', 'fuelType', 'units'],
  addRecurringExpense: ['id', 'vehicleId', 'title', 'category', 'note', 'kind', 'intervalMonths', 'intervalDistance', 'lastDoneDate', 'lastDoneOdometer', 'warnDays', 'warnDistance'],
  updateRecurringExpense: ['id', 'title', 'category', 'note', 'kind', 'intervalMonths', 'intervalDistance', 'lastDoneDate', 'lastDoneOdometer', 'warnDays', 'warnDistance'],
  markRecurringExpensesDone: ['ids', 'date', 'odometer', 'amount', 'discount', 'currency', 'title', 'category', 'photoIds', 'expenseId'],
} as const

const pick = (input: Record<string, unknown> | undefined, keys: readonly string[], extra: Record<string, unknown> = {}) =>
  Object.fromEntries(Object.entries({ ...input, ...extra }).filter(([k, v]) => keys.includes(k) && v !== undefined))

/**
 * A waiting change as the server's `ChangeInput`: its id, the version it was made from, and its one operation. Photos kept on this device
 * are sent as the drafts they were uploaded as (`drafts`: key to draft id); one without a draft is left out.
 */
export function toChangeInput(c: Change, drafts: ReadonlyMap<string, string> = new Map()): ChangeInput {
  const envelope = { id: c.id, expectedVersion: c.expectedVersion ?? null, ...(c.base ? { base: JSON.stringify(c.base) } : {}) }
  const photoIds = Array.isArray(c.input?.photoIds)
    ? { photoIds: (c.input.photoIds as string[]).flatMap((id) => (isKept(id) ? (drafts.has(id) ? [drafts.get(id)!] : []) : [id])) }
    : {}
  const op = (name: keyof typeof FIELDS, extra: Record<string, unknown> = {}) => ({ ...envelope, [name]: pick(c.input, FIELDS[name], { ...photoIds, ...extra }) }) as ChangeInput
  const log = c.entity === 'expenses' ? 'Expense' : 'Refueling'
  // A tyre change is its mutation's input as the dialog sent it (a bare id where the mutation takes one), under its own field.
  if (c.entity === 'tyres' && isTyreAction(c.action))
    return { ...envelope, [c.action]: BARE_ID.has(c.action) ? c.input?.id : withoutTypename(sentInput(c.action, c.input)) } as ChangeInput
  switch (`${c.entity}:${c.action}`) {
    case 'refuelings:add': return op('logRefueling', { id: c.targetId, vehicleId: c.vehicleId })
    case 'refuelings:update': return op('updateRefueling', { id: c.targetId })
    case 'refuelings:trash': return { ...envelope, deleteRefueling: c.targetId }
    case 'refuelings:restore': return { ...envelope, restoreRefueling: c.targetId }
    case 'expenses:add': return op('addExpense', { id: c.targetId, vehicleId: c.vehicleId })
    case 'expenses:update': return op('updateExpense', { id: c.targetId })
    case 'expenses:trash': return { ...envelope, deleteExpense: c.targetId }
    case 'expenses:restore': return { ...envelope, restoreExpense: c.targetId }
    case 'vehicles:add': return op('addVehicle', { id: c.targetId })
    case 'vehicles:update': return op('updateVehicle', { id: c.targetId })
    case 'vehicles:trash': return { ...envelope, deleteVehicle: c.targetId }
    case 'vehicles:restore': return { ...envelope, restoreVehicle: c.targetId }
    case 'recurring:add': return op('addRecurringExpense', { id: c.targetId, vehicleId: c.vehicleId })
    case 'recurring:update': return op('updateRecurringExpense', { id: c.targetId })
    case 'recurring:trash': return { ...envelope, deleteRecurringExpense: c.targetId }
    case 'recurring:markDone': return op('markRecurringExpensesDone', { ids: c.targetIds ?? [] })
    case 'refuelings:addPhoto':
    case 'expenses:addPhoto':
      return { ...envelope, [`add${log}Photo`]: { logId: c.targetId, draftId: drafts.get(String(c.input?.key)) ?? c.input?.draftId } } as ChangeInput
    case 'vehicles:setPicture':
      return { ...envelope, setVehiclePicture: { vehicleId: c.targetId, draftId: drafts.get(String(c.input?.key)) ?? c.input?.draftId } } as ChangeInput
    case 'vehicles:removePicture':
      return { ...envelope, removeVehiclePicture: c.targetId }
    case 'refuelings:removePhoto':
    case 'expenses:removePhoto':
      return { ...envelope, [`remove${log}Photo`]: { logId: c.targetId, imageId: c.input?.imageId } } as ChangeInput
    default: throw new Error(`A change of ${c.entity} cannot ${c.action}.`)
  }
}

/** Where the device remembers the adds the server parked (the ids of what they add). */
export const PARKED_ADDS_KEY = 'push:parkedAdds'

/** The adds the server parked, as remembered on this device. */
export async function parkedAdds(device: typeof deviceData): Promise<string[]> {
  const kept = (await device.read(PARKED_ADDS_KEY))?.data
  return Array.isArray(kept) ? kept.filter((id): id is string => typeof id === 'string') : []
}

/** Forgets that an add was parked (it was applied or discarded on the Sync page): what depends on it is sent again. */
export async function unblockAdd(id: string, device: typeof deviceData = deviceData): Promise<void> {
  await device.keep(PARKED_ADDS_KEY, (await parkedAdds(device)).filter((kept) => kept !== id))
}

/** What a change needs the server to have first: the vehicle it was made on, or the schedule it changes or marks done, when added here. */
const needs = (c: Change): string[] => [c.vehicleId, ...(c.entity === 'recurring' ? [c.targetId, ...(c.targetIds ?? [])] : []), ...tyreNeedsOf(c)]

/**
 * The order the server gets the changes in: the vehicles added here first (everything else may be made on them), then the rest as it was
 * made; requests of `size`, a change never in the same request as the add it depends on.
 */
export function batches(changes: readonly Change[], size = CHUNK_SIZE): Change[][] {
  const vehicles = changes.filter((c) => c.entity === 'vehicles' && c.action === 'add')
  const rest = changes.filter((c) => !vehicles.includes(c)).sort((a, b) => a.seq - b.seq)
  const out: Change[][] = []
  for (let i = 0; i < vehicles.length; i += size) out.push(vehicles.slice(i, i + size))
  let current: Change[] = []
  const addsIn = new Set<string>()
  for (const change of rest) {
    if (current.length === size || needs(change).some((id) => addsIn.has(id))) {
      out.push(current)
      current = []
      addsIn.clear()
    }
    current.push(change)
    for (const id of createdBy(change)) addsIn.add(id)
  }
  if (current.length > 0) out.push(current)
  return out
}

/**
 * A request's changes: a change that carries kept photos goes on its own, as its photos go up as drafts right before it and the server
 * keeps at most 20 drafts per person and vehicle; the others go together, in order.
 */
export function withPhotosAlone(changes: readonly Change[]): Change[][] {
  const out: Change[][] = []
  let current: Change[] = []
  for (const change of changes) {
    if (keptPhotosOf(change).length === 0) {
      current.push(change)
      continue
    }
    if (current.length > 0) out.push(current)
    out.push([change])
    current = []
  }
  if (current.length > 0) out.push(current)
  return out
}

/**
 * Sends the changes kept on this device (`outbox.ts`) once the server can be reached, in order (`batches`), with `syncChanges`. The
 * server applies each through the same rules as online, or parks it with the reason; either way it has it, so it leaves the device (the
 * parked ones are listed with their reason until the next sync, and on the server for everyone who may see the vehicle). A change made on
 * an add the server parked is not sent (it could only fail); it waits on the device. A lost connection ends the run (the server answers a
 * change sent again with what it did the first time). Then the download brings the server's rows, and only then do the kept changes
 * leave the screen, so nothing blinks in between. One run at a time.
 */
export function createPushEngine({ client, device = deviceData, pull, chunkSize = CHUNK_SIZE }: PushDeps) {
  let state: PushState = { status: 'idle', last: null }
  const listeners = new Set<() => void>()
  const set = (next: Partial<PushState>) => {
    state = { ...state, ...next }
    listeners.forEach((listener) => listener())
  }
  let running: Promise<void> | null = null
  let again = false

  async function runOnce() {
    if (!connectivity.reachable || device.user === null || outbox.changes.length === 0) return
    set({ status: 'syncing' })
    const answered: string[] = []
    const parked: ParkedChange[] = []
    // Adds the server parked, in this run or an earlier one: what depends on them waits (sent, it could only fail).
    const blocked = new Set<string>(await parkedAdds(device))
    let interrupted = false
    let photosLeftOut = 0
    let photosWaiting = 0

    // One request. Its kept photos go up as drafts first (a draft is only ever uploaded for a change that is sent right after); the
    // changes are marked sent before they go: from there the server may have them, so nothing folds into them any more (`collapse`), and
    // removing them once answered never takes a later edit along.
    const send = async (group: Change[]) => {
      const drafts = new Map<string, string>()
      for (const key of group.flatMap(keptPhotosOf)) {
        try {
          const id = await keptPhotos.asDraft(key)
          if (id) drafts.set(key, id)
          else photosLeftOut++ // no longer on this device
        } catch (error) {
          if (isConnectionFailure(error)) throw error
          // Not taken this time (the drafts are full, a server error): the change is not sent, so its photos are not lost; it waits.
          console.warn('A photo kept on this device was not taken as a draft; its change waits for the next sync.', error)
          photosWaiting++
          return
        }
      }
      // A photo added to a saved log, or a vehicle's picture, that is gone from the device has nothing left to send: it is done with.
      const nothing = group.filter((c) => (c.action === 'addPhoto' || c.action === 'setPicture') && !drafts.has(String(c.input?.key)))
      const sendable = group.filter((c) => !nothing.includes(c))
      if (sendable.length === 0) {
        answered.push(...nothing.map((c) => c.id))
        return
      }
      // What goes is the changes as kept now (another tab or this one's Sync page may have folded an edit into one since the run looked);
      // one whose photos changed meanwhile (one added to a waiting add) goes next time, with its new photos uploaded first.
      const looked = new Set(group.flatMap(keptPhotosOf))
      const marked = await outbox.markSent(sendable.map((c) => c.id))
      const sent = marked.filter((c) => keptPhotosOf(c).every((key) => looked.has(key)))
      answered.push(...nothing.map((c) => c.id))
      if (sent.length === 0) return
      const { data } = await client.mutate({ mutation: SyncChangesDocument, variables: { input: { changes: sent.map((c) => toChangeInput(c, drafts)) } } })
      const results = new Map<string, Result>((data?.syncChanges.results ?? []).map((r) => [r.id, r]))
      for (const change of sent) {
        const result = results.get(change.id)
        if (!result) continue
        answered.push(change.id)
        if (result.status === 'PARKED') {
          parked.push({ change, key: result.reason?.key ?? '', args: Object.fromEntries((result.reason?.args ?? []).map((a) => [a.name, a.value])) })
          for (const id of createdBy(change)) blocked.add(id)
        }
      }
    }

    // A change the server refuses as a whole (one it cannot take at all) waits, shown on the Sync page where it can be removed.
    const refused = (error: unknown) => console.warn('A change kept on this device was refused; it waits on the Sync page.', error)

    try {
      for (const chunk of batches(outbox.changes, chunkSize)) {
        // As the changes are now: an edit may have folded into one since the run began.
        const current = new Map(outbox.changes.map((c) => [c.id, c]))
        const live = chunk.flatMap((c) => current.get(c.id) ?? []).filter((c) => !needs(c).some((id) => blocked.has(id)))
        for (const group of withPhotosAlone(live)) {
          try {
            await send(group)
          } catch (error) {
            if (isConnectionFailure(error)) throw error
            if (group.length === 1) {
              refused(error)
              continue
            }
            // The request was refused as a whole: one at a time, so the others still go and only the one it cannot take waits.
            for (const change of group) {
              try {
                await send([change])
              } catch (single) {
                if (isConnectionFailure(single)) throw single
                refused(single)
              }
            }
          }
        }
      }
    } catch (error) {
      if (!isConnectionFailure(error)) console.warn('Sending the changes kept on this device failed; the next sync tries again.', error)
      interrupted = true
    }
    // The download runs after every sync, whatever was answered: a change that waits must never keep the device from the server's rows.
    await pull?.().catch(() => undefined) // the server's rows first...
    if (answered.length > 0) {
      await outbox.remove(answered) // ...then the kept changes leave
      void client.refetchQueries({ include: 'active' }).catch(() => undefined)
    }
    // Remembered while anything waiting still needs one of them (until the parked add is applied or discarded on the Sync page).
    const stillNeeded = new Set(outbox.changes.flatMap(needs))
    await device.keep(PARKED_ADDS_KEY, [...blocked].filter((id) => stillNeeded.has(id))).catch(() => undefined)
    set({ status: 'idle', last: { at: Date.now(), applied: answered.length - parked.length, parked, interrupted, photosLeftOut, photosWaiting } })
  }

  return {
    get state(): PushState {
      return state
    },
    subscribe(listener: () => void): () => void {
      listeners.add(listener)
      return () => {
        listeners.delete(listener)
      }
    },
    /** Sends now, or right after the run under way. */
    run(): Promise<void> {
      if (running) {
        again = true
        return running
      }
      running = (async () => {
        do {
          again = false
          await runOnce()
        } while (again)
      })().finally(() => {
        running = null
      })
      return running
    },
  }
}

export type PushEngine = ReturnType<typeof createPushEngine>
