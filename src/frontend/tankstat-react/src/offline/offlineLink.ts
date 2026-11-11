import { ApolloLink } from '@apollo/client'
import { getMainDefinition, print } from '@apollo/client/utilities'
import type { DocumentNode } from 'graphql'
import { catchError, defer, from, map, mergeMap, of, tap, throwError, type Observable } from 'rxjs'
import { tellLeftOverSignOut } from '../auth/signedOutHere.ts'
import type { AuthMode, SessionQuery } from '../gql/generated.ts'
import { appliedMutations } from './appliedMutations.ts'
import { connectivity } from './connectivity.ts'
import { ANONYMOUS_USER, deviceData } from './deviceData.ts'
import { classifyFailure, OfflineError } from './errors.ts'
import { outbox } from './outbox.ts'
import { policyFor, snapshotKey } from './snapshotPolicy.ts'

/** Signing in or out: what comes back afterwards may be another account's, so nothing is kept until `Session` says whose it is. */
const SIGN_IN_MUTATIONS = new Set(['Login', 'Logout', 'ResetPassword'])

/**
 * The device's own answer while it is signed out without the server knowing (#162): nobody, in the mode it signed out of, so the sign-in
 * screen shows (offline too) and the cookies still in the browser never sign it back in. No notices: they are the server's to send.
 */
const signedOutSession = (mode: string): SessionQuery => ({
  session: { mode: mode as AuthMode, user: null, instance: { generation: '', restorePoint: null } },
  notices: [],
})

interface SessionAnswer {
  session?: { mode?: string; user?: { id?: string } | null; instance?: { generation?: string } | null }
}

const documentIds = new WeakMap<DocumentNode, string>()

/**
 * A short fingerprint of a query's document (FNV-1a of its printed text): an answer kept for another version of the query (an older build,
 * before a field was added) is not answered from, since it may lack what the screen now reads.
 */
export function documentId(query: DocumentNode): string {
  let id = documentIds.get(query)
  if (id === undefined) {
    let hash = 0x811c9dc5
    for (const char of print(query)) hash = Math.imul(hash ^ char.charCodeAt(0), 0x01000193)
    id = (hash >>> 0).toString(36)
    documentIds.set(query, id)
  }
  return id
}

/** Whose data a `Session` answer is: the user's, the anonymous user's when sign-in is off, or nobody's. */
export function userOf(data: unknown): string | null {
  const session = (data as SessionAnswer | null)?.session
  if (!session) return null
  if (session.user?.id) return session.user.id
  return session.mode === 'NONE' ? ANONYMOUS_USER : null
}

/** Which history the server's data belongs to, as a `Session` answer says it (none from an older server or an older kept answer). */
export function generationOf(data: unknown): string | undefined {
  const generation = (data as SessionAnswer | null)?.session?.instance?.generation
  return typeof generation === 'string' && generation ? generation : undefined
}

/** The last answer the device kept of a query, for the device's own answers to build on (a vehicle's page, the home list, the rows found). */
export const keptBy = (device: typeof deviceData) => async (operationName: string, variables: Record<string, unknown>) =>
  (await device.read(snapshotKey(operationName, variables)))?.data as Record<string, unknown> | undefined

/**
 * Whether changes waiting on this device concern what a query asks: then the device answers it itself (when it holds the logs), so what
 * the user changed shows, in its place, until the server has it.
 */
function waitingFor(name: string | undefined, variables: Record<string, unknown>): boolean {
  if (outbox.changes.length === 0) return false
  switch (name) {
    case 'Refuelings':
    case 'Expenses':
    case 'LogDefaults':
    case 'ExpenseCategories':
    case 'RecurringExpenses':
    case 'VehicleTyres': // a tyre change waiting shows as done (#287)
    case 'ChartData':
    case 'VehicleFigure': // every key figure (#274): a vehicle added here is the device's to answer until the server has it
      return outbox.vehicleIds().has(String(variables.vehicleId))
    case 'VehicleDetails':
    case 'VehicleCard':
    case 'VehicleDashboard':
      return outbox.vehicleIds().has(String(variables.id))
    case 'Welcome':
      return outbox.changes.some((c) => c.entity === 'vehicles' || c.entity === 'recurring')
    case 'RefuelingDetails':
    case 'ExpenseDetails':
      return outbox.changes.some((c) => c.targetId === variables.id || c.input?.expenseId === variables.id)
    // Never the trash: the device holds only what it downloaded, and Empty trash empties the server's whole trash, so while the server
    // can be reached the trash shown is the server's (a log trashed here shows there once it is sent).
    default:
      return false
  }
}

/**
 * Tells `connectivity` how every GraphQL request went, and keeps the last answer of the queries worth keeping on this device
 * (`snapshotPolicy.ts`, `deviceData.ts`). While the server is known to be unreachable nothing is sent: a kept query is answered from the
 * downloaded window when it can be (`localResolvers.ts`), else with its last answer; anything else fails at once with an `OfflineError`
 * saying why. A query that fails because the server just went out of
 * reach is answered the same way. The reachability probe passes `context: { offline: 'bypass' }` to get through.
 */
export function createOfflineLink(device = deviceData): ApolloLink {
  return new ApolloLink((operation, forward) => {
    const bypass = operation.getContext().offline === 'bypass'
    const definition = getMainDefinition(operation.query)
    const isQuery = definition.kind === 'OperationDefinition' && definition.operation === 'query'
    const name = operation.operationName
    const policy = isQuery ? policyFor(name) : 'never'
    const key = snapshotKey(name ?? '', operation.variables)
    const doc = policy === 'never' ? undefined : documentId(operation.query)

    const fromDevice = (): Observable<ApolloLink.Result> => {
      if (policy === 'onlineOnly') return throwError(() => new OfflineError('onlineOnly'))
      if (policy === 'never') return throwError(() => new OfflineError())
      // The downloaded window answers what it can (any page or order of a vehicle's logs); otherwise the last answer seen.
      // Loaded when first needed (not with the app's first paint); the service worker keeps it for offline starts.
      const answer = async () => {
        const { answerLocally } = await import('./localResolvers.ts')
        return (await answerLocally(await device.rows(), name, operation.variables, keptBy(device))) ?? (await device.read(key, doc))?.data
      }
      return from(answer()).pipe(
        // Nobody's session kept (signed out, or never signed in here): the sign-in screen needs the server, nothing is "not loaded".
        mergeMap((data) => (data !== undefined ? of({ data } as ApolloLink.Result) : throwError(() => new OfflineError(name === 'Session' ? undefined : 'notLoaded')))),
      )
    }

    // The epoch is taken as the request goes (on subscription), so a late answer to one sent before the server went out of reach does
    // not count as the server being back (#238); its data still goes on and is kept.
    const fromServer = (): Observable<ApolloLink.Result> =>
      defer(() => {
        const sentIn = connectivity.epoch
        return forward(operation).pipe(tap({ next: () => connectivity.succeeded(sentIn) }))
      }).pipe(
        tap({
          next: (result) => {
            if (result.errors?.length || result.data == null) return
            if (!isQuery) {
              if (name && SIGN_IN_MUTATIONS.has(name)) device.unconfirm()
              if (name) appliedMutations.tell(name)
              return
            }
            if (name === 'Session') {
              const data = result.data
              // Whose data, then which history (a change drops every kept answer, so this answer is kept after that, also when the check
              // failed: the next answer, or the next download, tries it again).
              void device
                .signedIn(userOf(data))
                .then(() => device.generationSeen(generationOf(data)).catch(() => undefined))
                .then(() => device.keep(key, data, undefined, doc))
                .catch(() => undefined)
            } else if (policy === 'keep') {
              void device.keep(key, result.data, undefined, doc).catch(() => undefined)
            }
          },
          error: (error: unknown) => {
            if (classifyFailure(error) !== 'answered') connectivity.failed()
          },
        }),
        // The server went out of reach during this request: answer it like the next one would be (never for the probe itself).
        catchError((error: unknown) => (!bypass && classifyFailure(error) !== 'answered' && isQuery && policy !== 'never' ? fromDevice() : throwError(() => error))),
      )

    const route = (): Observable<ApolloLink.Result> => {
      if (!bypass && !connectivity.reachable) return fromDevice()
      if (!bypass && isQuery && waitingFor(name, operation.variables)) {
        const local = import('./localResolvers.ts').then(async ({ answerLocally }) => answerLocally(await device.rows(), name, operation.variables, keptBy(device)))
        return from(local).pipe(mergeMap((data) => (data !== undefined ? of({ data } as ApolloLink.Result) : fromServer())))
      }
      return fromServer()
    }

    // Signed out here without the server hearing of it: told now if it can be, and until then nobody is signed in on this device. When the
    // device cannot read whether it was, the server is asked as usual: a guess of "signed out" would lock a signed-in person out.
    if (!bypass && name === 'Session') {
      return from(device.signedOutHere().catch(() => null)).pipe(
        mergeMap((signedOut) => {
          if (!signedOut) return route()
          const told = tellLeftOverSignOut(device).catch(() => false)
          return from(told).pipe(mergeMap((done) => (done ? route() : of({ data: signedOutSession(signedOut.mode) } as ApolloLink.Result))))
        }),
      )
    }
    // Signing in (only while the server can be reached: offline the sign-in itself is refused, and the device must stay signed out): a
    // sign-out left over is told once more first. Forgotten only once the sign-in worked, whatever came of telling it, since its cookies
    // replace the old ones (an old session nobody holds any more ends with its lifetime); a failed one leaves the old cookies, which must
    // not sign the device back in. Forgotten before the answer goes on: the `Session` asked right after must not end the new session.
    if (!bypass && name === 'Login' && connectivity.reachable) {
      return from(tellLeftOverSignOut(device).catch(() => false)).pipe(
        mergeMap(() => route()),
        mergeMap((result) =>
          !result.errors?.length && (result.data as { login?: unknown } | null | undefined)?.login
            ? from(device.setSignedOutHere(null).catch(() => undefined)).pipe(map(() => result))
            : of(result),
        ),
      )
    }
    return route()
  })
}
