import { useMemo, useSyncExternalStore } from 'react'
import { collapse, discard, keptPhotosOf, markOf, type Change, type ChangeEntity, type PendingMark } from './changes.ts'
import { deviceData } from './deviceData.ts'
import type { RowStore } from './deviceStorage.ts'
import { isTyreAction, tyreFeeExpense } from './tyreChanges.ts'
import { keptPhotos } from './keptPhotos.ts'

/**
 * The changes this device keeps until the server can take them, in the open user's database (`deviceStorage.ts`), so they survive a
 * restart and another account signing in never sees them. A new change folds into the waiting ones (`collapse`). Changes are made by the
 * user on screen, so they are kept in whichever user's data is open, before the server confirmed who it is too (an offline start).
 * The push engine (`push.ts`) sends them when the server is reachable.
 */
let changes: Change[] = []
let rows: RowStore | null = null
let nextSeq = 1
let version = 0
let loading: Promise<void> = Promise.resolve()
const listeners = new Set<() => void>()

function notify() {
  version++
  listeners.forEach((listener) => listener())
}

/**
 * A change this build can send: a tyre change kept by an older build under an action it no longer has (#288 renamed them; the owner: a
 * preview's changes need not survive) is left out, never sent and never in the way of the others.
 */
const understood = (c: Change) => c.entity !== 'tyres' || isTyreAction(c.action)

async function load() {
  rows = await deviceData.rows()
  changes = rows ? (await rows.changes().catch(() => [])).filter(understood) : []
  nextSeq = changes.reduce((max, c) => Math.max(max, c.seq), 0) + 1
  notify()
  // A photo kept for a change that is gone (the app closed between the two writes) is removed; no dialog holds one yet.
  if (rows) await keptPhotos.gc(new Set(changes.flatMap(keptPhotosOf))).catch(() => undefined)
}

deviceData.onStoreChange(() => {
  loading = load()
})
loading = load()

/** What changed the outbox in one tab tells the others, so what they show (and would send) follows. */
const tabs = typeof BroadcastChannel === 'undefined' ? null : new BroadcastChannel('tankstat-outbox')
if (tabs) tabs.onmessage = () => void exclusive(async () => notify())

/** Reads the changes as stored now: another tab of the same account may have changed them since this one last looked. */
async function fresh() {
  if (!rows) return
  try {
    changes = (await rows.changes()).filter(understood)
    nextSeq = changes.reduce((max, c) => Math.max(max, c.seq), nextSeq - 1) + 1
  } catch {
    // What this tab holds stays the best it knows.
  }
}

let tail: Promise<unknown> = Promise.resolve()

/**
 * One change of the outbox at a time, in this tab and (Web Locks) across the tabs of this site, each starting from what is stored: a
 * change kept in one tab while another sends never folds into a copy that tab has already sent, and nothing is written over a newer one.
 */
function exclusive<T>(work: () => Promise<T>): Promise<T> {
  const run = async () => {
    await loading
    const locks = globalThis.navigator?.locks
    const step = async () => {
      await fresh()
      return work()
    }
    return locks ? (locks.request('tankstat-outbox', step) as Promise<T>) : step()
  }
  const result = tail.then(run, run)
  tail = result.catch(() => undefined)
  return result
}

/** After a change of the outbox: this tab's screens, and the other tabs. */
function changed() {
  notify()
  tabs?.postMessage('changed')
}

/**
 * Writes what differs between two lists of changes, and removes the kept photos no change carries any more (a change folded away, taken
 * back or answered by the server; `incoming` is a new change that may have folded into nothing).
 */
async function persist(before: readonly Change[], after: readonly Change[], incoming?: Change) {
  if (!rows) return
  const kept = new Set(after.map((c) => c.id))
  const old = new Map(before.map((c) => [c.id, c]))
  await rows.deleteChanges(before.filter((c) => !kept.has(c.id)).map((c) => c.id))
  await rows.putChanges(after.filter((c) => old.get(c.id) !== c))
  const still = new Set(after.flatMap(keptPhotosOf))
  const dropped = [...before, ...(incoming ? [incoming] : [])].flatMap(keptPhotosOf).filter((key) => !still.has(key))
  await keptPhotos.remove(dropped).catch(() => undefined)
}

export type ChangeDraft = Omit<Change, 'seq' | 'createdAt'>

const MARK_ORDER: readonly PendingMark[] = ['deleted', 'done', 'new', 'restored', 'changed']

export const outbox = {
  /** The changes waiting, oldest first. */
  get changes(): readonly Change[] {
    return changes
  },

  /** Keeps a change on the device; it folds into the ones waiting for the same log. */
  enqueue(draft: ChangeDraft): Promise<void> {
    return exclusive(async () => {
      if (!rows) throw new Error('No user data is open on this device.')
      const incoming = { ...draft, seq: nextSeq++, createdAt: Date.now() }
      const next = collapse(changes, incoming)
      await persist(changes, next, incoming)
      changes = next
      changed()
    })
  },

  /** Takes back one waiting change. */
  discard(id: string): Promise<void> {
    return exclusive(async () => {
      const next = discard(changes, id)
      await persist(changes, next)
      changes = next
      changed()
    })
  },

  /** The changes the server has answered: they leave the device as they are (what depends on them stays and goes next). */
  remove(ids: readonly string[]): Promise<void> {
    return exclusive(async () => {
      const gone = new Set(ids)
      const next = changes.filter((c) => !gone.has(c.id))
      await persist(changes, next)
      changes = next
      changed()
    })
  },

  /**
   * About to be sent: from now on the server may have them, so nothing folds into them (`collapse`). Returns them as they are now, which
   * is what goes (an edit may have folded into one since the sender looked; one taken back meanwhile is not among them).
   */
  markSent(ids: readonly string[]): Promise<Change[]> {
    return exclusive(async () => {
      const sending = new Set(ids)
      const next = changes.map((c) => (sending.has(c.id) && !c.sent ? { ...c, sent: true } : c))
      await persist(changes, next)
      changes = next // nothing shows it, and the other tabs read it when they next change something
      return next.filter((c) => sending.has(c.id))
    })
  },

  /**
   * How something is marked while a change of it waits (a visit marks each of its schedules done); null when none does. With several (an
   * edit and a visit, one sent already and one made after it) the one that says most wins: on its way to the trash, done, new, restored,
   * changed.
   */
  /** `markOf` for every row of an entity that has a mark, worked out in one pass over the changes (a list asks for each of its rows). */
  marksOf(entity: ChangeEntity): Map<string, PendingMark> {
    const ids = new Set<string>()
    for (const c of changes) {
      if (c.entity === entity) for (const id of c.action === 'markDone' ? (c.targetIds ?? []) : [c.targetId]) ids.add(id)
      if (entity === 'expenses' && c.action === 'markDone') ids.add(c.targetId)
      const fee = entity === 'expenses' && c.entity === 'tyres' ? tyreFeeExpense(c.action, c.input) : null
      if (fee) ids.add(fee.expenseId)
    }
    const marks = new Map<string, PendingMark>()
    for (const id of ids) {
      const mark = this.markOf(entity, id)
      if (mark) marks.set(id, mark)
    }
    return marks
  },

  markOf(entity: ChangeEntity, id: string): PendingMark | null {
    const marks = changes.filter((c) => c.entity === entity && (c.action === 'markDone' ? c.targetIds?.includes(id) : c.targetId === id)).map(markOf)
    const mark = MARK_ORDER.find((m) => marks.includes(m))
    if (mark) return mark
    // The expense a visit logs is new until the server has it.
    if (entity === 'expenses' && changes.some((c) => c.action === 'markDone' && c.targetId === id && c.input?.amount != null)) return 'new'
    // The expense a tyre log line's amount makes (#288) is new until the server has it; one a waiting edit changes is changed.
    const fees = entity === 'expenses' ? changes.filter((c) => c.entity === 'tyres').map((c) => tyreFeeExpense(c.action, c.input)).filter((f) => f?.expenseId === id) : []
    return fees.length === 0 ? null : fees.every((f) => f!.edit) ? 'changed' : 'new'
  },

  /** The vehicles with a change waiting: the device answers their logs itself, so what was changed shows. */
  vehicleIds(): Set<string> {
    return new Set(changes.map((c) => c.vehicleId))
  },

  count(vehicleId?: string): number {
    return vehicleId ? changes.filter((c) => c.vehicleId === vehicleId).length : changes.length
  },

  subscribe(listener: () => void): () => void {
    listeners.add(listener)
    return () => {
      listeners.delete(listener)
    }
  },

  /** Read again from the open user's data (tests: after a reset or a restart). */
  reload(): Promise<void> {
    loading = load()
    return loading
  },
}

const useVersion = () => useSyncExternalStore(outbox.subscribe, () => version)

/** How a log is marked while a change of it waits (new, changed, to be removed, restored), re-rendering when it changes. */
export function usePendingMark(entity: ChangeEntity, id: string): PendingMark | null {
  useVersion()
  return outbox.markOf(entity, id)
}

/**
 * `usePendingMark` for every row of a list at once (one subscription, one table per change of the outbox): how each is marked,
 * re-rendering when the changes do.
 */
export function usePendingMarks(entity: ChangeEntity): (id: string) => PendingMark | null {
  const current = useVersion()
  // eslint-disable-next-line react-hooks/exhaustive-deps -- `current` stands for the changes
  const marks = useMemo(() => outbox.marksOf(entity), [entity, current])
  return useMemo(() => (id: string) => marks.get(id) ?? null, [marks])
}

/** How many changes wait (for a vehicle, or in all). */
export function usePendingCount(vehicleId?: string): number {
  useVersion()
  return outbox.count(vehicleId)
}
