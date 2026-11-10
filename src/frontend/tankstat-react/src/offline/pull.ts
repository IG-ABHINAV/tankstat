import type { ApolloClient } from '@apollo/client'
import { fetchSignedIn } from '../auth/refresh.ts'
import {
  OfflineChangesDocument,
  OfflineSettingsDocument,
  ServerGenerationDocument,
  SessionDocument,
  VehicleDefaultsDocument,
  WelcomeDocument,
  type OfflineChangesQuery,
  type SessionQuery,
} from '../gql/generated.ts'
import { HOME_PAGE_SIZE } from '../homePaging.ts'
import { connectivity } from './connectivity.ts'
import { deviceData, GENERATION_KEY } from './deviceData.ts'
import type { LogKind, LogRow, PullCursor, RowStore, VehicleRow } from './deviceStorage.ts'
import { isConnectionFailure } from './errors.ts'
import { keptPictures, pictureIdOf } from './keptPictures.ts'
import { generationOf, keptBy } from './offlineLink.ts'
import { DEFAULT_RULE, fromDate, narrower } from './offlineWindow.ts'
import { snapshotKey } from './snapshotPolicy.ts'

/** Rows per table and page (the server's maximum). */
export const PAGE_SIZE = 200
/**
 * The download asks the server, never the device: while changes wait, the link would answer some of these queries itself (the home list
 * with the vehicles added here), and storing that as the server's would duplicate them. Their answers are still kept as usual.
 */
const SERVER = { offline: 'bypass' }

/** Where the time of the last complete download is kept with the user's data (not an operation's answer). */
export const LAST_PULL_KEY = 'meta:lastPull'

/** The home list the download covers, like the Arrange dialog: the user's vehicles up to the server's page limit. */
export const MAX_VEHICLES = 200

/** How many pictures download at the same time. */
const PICTURE_CONCURRENCY = 2

type Page = OfflineChangesQuery['offlineChanges']

export interface PullState {
  status: 'idle' | 'pulling'
  vehiclesDone: number
  vehiclesTotal: number
  /** When the last download finished (Date.now()), in this page's life. */
  lastPullAt: number | null
  /** The last one stopped because the server went out of reach. */
  interrupted: boolean
}

export interface PullDeps {
  client: ApolloClient
  device?: typeof deviceData
  now?: () => Date
  pageSize?: number
  /** How many vehicles download at the same time. */
  concurrency?: number
  /** Asks the server for a picture (`/media/<id>`): signed in, so a request after the access cookie ran out is sent again after a refresh. */
  fetchPicture?: (url: string) => Promise<Response>
  /** Told when the pictures kept changed (the screens show the new ones). */
  onPictures?: () => void
}

/**
 * Downloads the user's offline window and keeps it current (`offlineChanges` on the server, `offline-download` in the docs):
 * 0. which history the server's data has (`ServerGeneration`): after a restore the device sets what it holds aside and starts afresh
 *    before anything is written (`deviceData.generationSeen`), whether or not a `Session` answer said so already;
 * 1. the home list (one request): the vehicles are kept, the ones no longer listed are dropped with everything below them (access lost,
 *    trashed or purged by someone else), and its pages are kept for the home page;
 * 2. the defaults a new vehicle or schedule starts from (kept for adding offline), and the window rules (`offlineSettings`), turned into a
 *    start date per vehicle on this device's clock;
 * 3. per vehicle, two at a time: a **full** download the first time, when the window grew or the server says the last one is too old
 *    (`resync`); otherwise only **what changed** since the last one's watermark, whatever its date, plus what was removed for good. A
 *    window that shrank drops the older logs here and downloads nothing again. Each page is stored before the next is asked, and a full
 *    download goes on from where it stopped; at its end the logs it did not bring again are removed. The vehicle's details and schedules
 *    come with the first page and are kept as the answers of `VehicleDetails` and `RecurringExpenses` (its tyres as `VehicleTyres`);
 * 4. the pictures the screens show of it (`keepPictures`), last because they matter least.
 * One run at a time; a run asked for meanwhile follows it. A lost connection ends the run (the next one goes on from the cursors).
 */
export function createPullEngine({
  client,
  device = deviceData,
  now = () => new Date(),
  pageSize = PAGE_SIZE,
  concurrency = 2,
  fetchPicture = (url) => fetchSignedIn(url, { credentials: 'same-origin' }),
  onPictures = keptPictures.refresh,
}: PullDeps) {
  let state: PullState = { status: 'idle', vehiclesDone: 0, vehiclesTotal: 0, lastPullAt: null, interrupted: false }
  const listeners = new Set<() => void>()
  const set = (next: Partial<PullState>) => {
    state = { ...state, ...next }
    listeners.forEach((listener) => listener())
  }
  let running: Promise<void> | null = null
  let again = false

  const query = async <T>(run: () => Promise<{ data?: T }>): Promise<T> => {
    const { data } = await run()
    if (!data) throw new Error('The server sent no data.')
    return data
  }

  let account: string | null = null
  /** Stops the run when the server has named another account since it began (a sign-in elsewhere, in this tab or another). */
  const sameAccount = () => {
    if (device.user !== account) throw new AccountChanged()
  }

  /**
   * The server's history the run downloads (kept by `deviceData.generationSeen`). Every cursor carries the one it was written in: a run that
   * went on while another tab started afresh for a new history (a restore came during the run) writes cursors of the old one, which the
   * next run takes for none, and a run that sees the history change under it is followed by another at once.
   */
  let generation: string | undefined
  const keptGeneration = async () => {
    const kept = (await device.read(GENERATION_KEY))?.data
    return typeof kept === 'string' ? kept : undefined
  }
  /** Whether a cursor was written in this run's history (or before histories were told apart): only such a one is gone on from. */
  const sameHistory = (cursor: PullCursor | undefined) => cursor?.generation === undefined || cursor.generation === generation

  async function keepHomePages(vehicles: readonly VehicleRow[], total: number) {
    sameAccount()
    for (let skip = 0; skip === 0 || skip < vehicles.length; skip += HOME_PAGE_SIZE) {
      const key = snapshotKey('Welcome', { search: null, skip, take: HOME_PAGE_SIZE })
      await device.keep(key, { myVehicles: vehicles.slice(skip, skip + HOME_PAGE_SIZE), myVehicleCount: total, vehicleTotal: total })
    }
  }

  async function keepVehicleAnswers(vehicleId: string, page: Page) {
    sameAccount()
    await device.keep(snapshotKey('VehicleDetails', { id: vehicleId }), { vehicle: page.vehicle })
    await device.keep(snapshotKey('RecurringExpenses', { vehicleId }), { vehicle: { __typename: 'Vehicle', id: vehicleId, recurring: page.recurring } })
    // The vehicle's tyres, whole on the first page (#287): the Tyres tab's answer (none when the reader may not see them).
    if (page.tyres) await device.keep(snapshotKey('VehicleTyres', { vehicleId }), { vehicleTyres: page.tyres })
  }

  /** Stores a page's logs; one that moved out of the window (dated before its start) is removed instead. */
  async function ingest(rows: RowStore, kind: LogKind, list: readonly Record<string, unknown>[], from: string | null, stamp: number) {
    sameAccount()
    const inside: LogRow[] = []
    const outside: string[] = []
    for (const row of list as readonly LogRow[]) {
      if (from !== null && row.date < from) outside.push(row.id)
      else inside.push({ ...row, pulledAt: stamp })
    }
    await rows.putLogs(kind, inside)
    await rows.deleteLogs(kind, outside)
  }

  async function removeBefore(rows: RowStore, vehicleId: string, test: (row: LogRow) => boolean) {
    for (const kind of ['refuelings', 'expenses'] as const) await rows.deleteLogs(kind, (await rows.logs(kind, vehicleId)).filter(test).map((r) => r.id))
  }

  const ask = (input: Record<string, unknown>) =>
    query(() => client.query({ query: OfflineChangesDocument, variables: { input: input as never }, fetchPolicy: 'no-cache', context: SERVER })).then((d) => d.offlineChanges)

  async function full(rows: RowStore, cursor: PullCursor): Promise<void> {
    const from = cursor.from === false ? null : cursor.from
    const stamp = cursor.fullStartedAt ?? Date.now()
    let next = cursor.next
    for (;;) {
      const page = await ask(next ? { vehicleId: cursor.vehicleId, after: next, take: pageSize } : { vehicleId: cursor.vehicleId, from, take: pageSize })
      await ingest(rows, 'refuelings', page.refuelings, from, stamp)
      await ingest(rows, 'expenses', page.expenses, from, stamp)
      if (!next) await keepVehicleAnswers(cursor.vehicleId, page)
      next = page.next ?? null
      if (!next) {
        await removeBefore(rows, cursor.vehicleId, (r) => r.pulledAt < stamp) // what this download did not bring again is gone
        await rows.putCursor({ ...cursor, watermark: page.watermark, next: null, complete: true, fullStartedAt: null })
        return
      }
      await rows.putCursor({ ...cursor, next, fullStartedAt: stamp })
    }
  }

  async function later(rows: RowStore, cursor: PullCursor): Promise<void> {
    const from = cursor.from === false ? null : cursor.from
    const stamp = Date.now()
    let next: string | null = null
    let watermark: string | null = null
    do {
      const page: Page = await ask(next ? { vehicleId: cursor.vehicleId, after: next, take: pageSize } : { vehicleId: cursor.vehicleId, since: cursor.watermark, take: pageSize })
      if (page.resync) return full(rows, await restart(rows, cursor)) // the last download is older than what the server remembers
      if (!next) {
        watermark = page.watermark
        await keepVehicleAnswers(cursor.vehicleId, page)
        for (const kind of ['refuelings', 'expenses'] as const) {
          const type = kind === 'refuelings' ? 'REFUELING' : 'EXPENSE'
          await rows.deleteLogs(kind, page.removed.filter((r) => r.type === type).map((r) => r.id))
        }
      }
      await ingest(rows, 'refuelings', page.refuelings, from, stamp)
      await ingest(rows, 'expenses', page.expenses, from, stamp)
      next = page.next ?? null
    } while (next)
    await rows.putCursor({ ...cursor, watermark })
  }

  async function restart(rows: RowStore, cursor: PullCursor | undefined, from: string | null = cursor?.from === false ? null : (cursor?.from ?? null)) {
    // A vehicle that held its logs keeps answering from them while they download again; one that held none (window "none") does not
    // until the download is through: a first page alone is not the list.
    const complete = cursor?.from === false || !sameHistory(cursor) ? false : (cursor?.complete ?? false)
    const fresh: PullCursor = { vehicleId: cursor!.vehicleId, from, watermark: null, next: null, complete, fullStartedAt: Date.now(), generation }
    await rows.putCursor(fresh)
    return fresh
  }

  /** The signed-in user's avatar, from the answer that named them (none when sign-in is off). */
  async function ownAvatar(): Promise<unknown> {
    try {
      const cached = client.readQuery({ query: SessionDocument })
      if (cached) return cached.session.user?.avatarUrl
    } catch {
      // Not in the cache in a shape that can be read: the answer kept on the device says the same.
    }
    return ((await device.read(snapshotKey('Session', undefined)))?.data as SessionQuery | undefined)?.session.user?.avatarUrl
  }

  /**
   * Keeps the pictures the screens show of what was downloaded, for offline use (`keptPictures.ts`): the vehicles' pictures, their owners'
   * avatars and the user's own. An image never changes under its id, so one kept is never asked for again; those no longer shown go (a
   * vehicle dropped from the list takes its picture along). A picture the server does not give is left out until the next run; a lost
   * connection or a full storage ends this step, never the download: the logs are what matter offline.
   */
  async function keepPictures(rows: RowStore) {
    sameAccount()
    const wanted = new Set<string>()
    const want = (url: unknown) => {
      const id = typeof url === 'string' ? pictureIdOf(url) : null
      if (id) wanted.add(id)
    }
    for (const vehicle of await rows.vehicles()) {
      want(vehicle.pictureUrl)
      want((vehicle.owner as { avatarUrl?: unknown } | null | undefined)?.avatarUrl)
    }
    want(await ownAvatar())
    const kept = new Set(await rows.pictureIds())
    const unused = [...kept].filter((id) => !wanted.has(id))
    const queue = [...wanted].filter((id) => !kept.has(id))
    if (unused.length === 0 && queue.length === 0) return
    let full = false
    const worker = async () => {
      for (let id = queue.shift(); id && !full; id = queue.shift()) {
        const response = await fetchPicture(`/media/${id}`)
        if (!response.ok) continue // gone, or no longer to be seen: asked again next time
        const blob = await response.blob()
        sameAccount()
        try {
          await rows.putPicture({ id, type: blob.type, bytes: await blob.arrayBuffer(), keptAt: Date.now() }) // the type is the answer's Content-Type
        } catch (error) {
          if ((error as { name?: unknown } | null)?.name !== 'QuotaExceededError') throw error
          full = true
          console.warn('The device has no room for more pictures; the others show when the server can be reached.')
        }
      }
    }
    try {
      await rows.deletePictures(unused)
      await Promise.all(Array.from({ length: Math.min(PICTURE_CONCURRENCY, queue.length) }, worker))
    } catch (error) {
      if (error instanceof AccountChanged) throw error
      if (!isConnectionFailure(error)) console.warn('Keeping the pictures for offline use failed; the next download tries again.', error)
    } finally {
      onPictures()
    }
  }

  async function pullVehicle(rows: RowStore, vehicleId: string, rule: string) {
    const from = fromDate(rule, now())
    const kept = await rows.cursor(vehicleId)
    const cursor = kept && sameHistory(kept) ? { ...kept, generation } : undefined
    if (from === false) {
      // Nothing of its logs on this device: the vehicle's card and page come from the home list and the answers seen.
      if (cursor?.from !== false) await removeBefore(rows, vehicleId, () => true)
      await rows.putCursor({ vehicleId, from: false, watermark: null, next: null, complete: true, fullStartedAt: null, generation })
      return
    }
    const started = cursor?.fullStartedAt != null
    const firstOrWider = !cursor || cursor.from === false || (!started && cursor.watermark === null) || narrower(cursor.from, from)
    if (firstOrWider || (started && cursor.from !== from)) return full(rows, await restart(rows, { ...(cursor ?? { vehicleId, complete: false }), vehicleId } as PullCursor, from))
    if (started) return full(rows, cursor)
    if (narrower(from, cursor.from)) {
      await removeBefore(rows, vehicleId, (r) => r.date < from!) // the window shrank: nothing to download again
      await rows.putCursor({ ...cursor, from })
      return later(rows, { ...cursor, from })
    }
    return later(rows, cursor)
  }

  async function runOnce() {
    if (!(await device.writableRows()) || !connectivity.reachable) return
    set({ status: 'pulling', vehiclesDone: 0, vehiclesTotal: 0, interrupted: false })
    try {
      // Which history the server's data has, before anything is written: a restore may have taken it back to a backup since this device
      // last heard (no Session answer comes to a tab whose requests all went through the short restart, and the one that does may come
      // while this run goes). The device then sets what it holds aside and starts afresh first (`deviceData.generationSeen`, which waits
      // for a check another answer or tab began), so nothing of the new history overwrites, or drops, what it has to set aside.
      const server = await client.query({ query: ServerGenerationDocument, fetchPolicy: 'no-cache', context: SERVER }).then(
        (answer) => answer.data,
        (error: unknown) => {
          if (isConnectionFailure(error)) throw error
          return undefined // a server that cannot say (an older one): nothing to compare with
        },
      )
      await device.generationSeen(generationOf(server))
      const rows = await device.writableRows()
      if (!rows) throw new AccountChanged() // signed out meanwhile
      // A run belongs to the account it started for: once the server names someone else, nothing more of it lands anywhere.
      account = device.user
      generation = await keptGeneration()
      const home = await query(() => client.query({ query: WelcomeDocument, variables: { search: null, skip: 0, take: MAX_VEHICLES }, fetchPolicy: 'network-only', context: SERVER }))
      const vehicles = home.myVehicles as unknown as VehicleRow[]
      await rows.putVehicles(vehicles)
      await keepHomePages(vehicles, home.vehicleTotal)
      if (vehicles.length === home.vehicleTotal) {
        const listed = new Set(vehicles.map((v) => v.id))
        for (const gone of (await rows.vehicles()).filter((v) => !listed.has(v.id))) await rows.dropVehicle(gone.id)
      }
      // What a new vehicle or schedule starts from: kept (the link keeps its answer), so they can be added offline too.
      await client.query({ query: VehicleDefaultsDocument, fetchPolicy: 'network-only', context: SERVER })
      const settings = (await query(() => client.query({ query: OfflineSettingsDocument, fetchPolicy: 'network-only', context: SERVER }))).offlineSettings
      const rules = new Map(settings.vehicles.map((v) => [v.vehicleId, v.window]))
      set({ vehiclesTotal: vehicles.length })
      const queue = [...vehicles]
      const worker = async () => {
        for (let vehicle = queue.shift(); vehicle; vehicle = queue.shift()) {
          try {
            await pullVehicle(rows, vehicle.id, rules.get(vehicle.id) ?? settings.defaultWindow ?? DEFAULT_RULE)
          } catch (error) {
            if (isConnectionFailure(error) || error instanceof AccountChanged) throw error
            if (keyOf(error) === 'vehicle.notFound') await rows.dropVehicle(vehicle.id) // access lost meanwhile
            // A download that cannot be continued (the server no longer reads where it stopped): the next one starts it afresh.
            else if (keyOf(error) === 'sync.cursorInvalid') await restart(rows, await rows.cursor(vehicle.id))
            else console.warn('The offline download of a vehicle failed; the next one tries again.', error)
          }
          set({ vehiclesDone: state.vehiclesDone + 1 })
        }
      }
      await Promise.all(Array.from({ length: Math.min(concurrency, queue.length) }, worker))
      // The device started afresh for another history meanwhile (another tab heard of a restore while this run went on): what this run
      // kept since is no window, and its cursors say so. The next run downloads it again, at once.
      if ((await keptGeneration()) !== generation) throw new HistoryChanged()
      const finished = Date.now()
      await device.keep(LAST_PULL_KEY, finished)
      // Rows set aside when the server's history changed: those the download covers are judged, and the ones it has again leave the list
      // (`foundRows.ts`). Not worth failing the download over.
      try {
        await (await import('./foundRows.ts')).foundRows.reconcile(rows, keptBy(device))
      } catch (error) {
        console.warn('What was found on this device could not be compared with the download; the next one tries again.', error)
      }
      await keepPictures(rows)
      set({ status: 'idle', lastPullAt: finished })
    } catch (error) {
      if (error instanceof HistoryChanged) again = true
      set({ status: 'idle', interrupted: isConnectionFailure(error) })
      if (!isConnectionFailure(error) && !(error instanceof AccountChanged) && !(error instanceof HistoryChanged))
        console.warn('The offline download failed; the next one tries again.', error)
    }
  }

  return {
    get state(): PullState {
      return state
    },
    subscribe(listener: () => void): () => void {
      listeners.add(listener)
      return () => {
        listeners.delete(listener)
      }
    },
    /** Downloads now, or right after the run under way. */
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

export type PullEngine = ReturnType<typeof createPullEngine>

/** The server named another account during a run: it stops, keeping nothing more. */
class AccountChanged extends Error {}

/** The device started afresh for another history during a run: another run follows it at once. */
class HistoryChanged extends Error {}

function keyOf(error: unknown): string | undefined {
  const errors = (error as { errors?: readonly { extensions?: { key?: unknown } }[] } | null)?.errors
  const key = errors?.[0]?.extensions?.key
  return typeof key === 'string' ? key : undefined
}
