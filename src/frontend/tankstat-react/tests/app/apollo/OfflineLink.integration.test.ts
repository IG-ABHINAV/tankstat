import { waitFor } from '@testing-library/react'
import { ApolloClient, ApolloLink, HttpLink, InMemoryCache } from '@apollo/client'
import { parse } from 'graphql'
import { http, HttpResponse } from 'msw'
import { graphql } from '../../support/graphql.ts'
import { afterAll, afterEach, beforeAll, expect, it, vi } from 'vitest'
import { createApolloClient } from '../../../src/apolloClient.ts'
import { AdminDocument, HealthDocument, LoginDocument, RefuelingDetailsDocument, ResetPasswordDocument, SessionDocument, VehicleDefaultsDocument, VehicleFigureDocument } from '../../../src/gql/generated.ts'
import { appliedMutations } from '../../../src/offline/appliedMutations.ts'
import { connectivity } from '../../../src/offline/connectivity.ts'
import { deviceData } from '../../../src/offline/deviceData.ts'
import { OfflineError } from '../../../src/offline/errors.ts'
import { createOfflineLink } from '../../../src/offline/offlineLink.ts'
import { outbox } from '../../../src/offline/outbox.ts'
import { trackedFetch } from '../../../src/offline/trackedFetch.ts'
import { fakeInstance, gqlError, silenceConsoleError } from '../../support/mocks.tsx'
import { server } from '../../support/server.ts'

beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => server.resetHandlers())
afterAll(() => server.close())

const health = { health: { status: 'ok', version: '1.2.3', databaseReachable: true } }

it('a request that gets no answer marks the server unreachable; then nothing is sent until the probe gets through', async () => {
  const consoleError = silenceConsoleError()
  let sent = 0
  server.use(graphql.query('Health', () => (sent++, HttpResponse.error())))
  const client = createApolloClient('http://localhost/graphql')

  await expect(client.query({ query: HealthDocument, fetchPolicy: 'network-only' })).rejects.toBeInstanceOf(TypeError)
  expect(connectivity.reachable).toBe(false)

  await expect(client.query({ query: HealthDocument, fetchPolicy: 'network-only' })).rejects.toBeInstanceOf(OfflineError)
  expect(sent).toBe(1) // the second one was never sent
  expect(consoleError).toHaveBeenCalledTimes(1) // and not reported: the screen says offline

  server.use(graphql.query('Health', () => HttpResponse.json({ data: health })))
  await client.query({ query: HealthDocument, fetchPolicy: 'network-only', context: { offline: 'bypass' } }) // the probe
  expect(connectivity.reachable).toBe(true)
})

it('an answer, even an error, means the server is reachable; a gateway answering for it means it is not', async () => {
  silenceConsoleError()
  const client = createApolloClient('http://localhost/graphql')
  connectivity.failed()

  server.use(graphql.query('Health', () => HttpResponse.json({ errors: [{ message: 'Unexpected Execution Error' }] })))
  await expect(client.query({ query: HealthDocument, fetchPolicy: 'network-only', context: { offline: 'bypass' } })).rejects.toBeDefined()
  expect(connectivity.reachable).toBe(true)

  // A reverse proxy whose app is down answers with its own page, not GraphQL.
  server.use(http.post('http://localhost/graphql', () => new HttpResponse('<html>Bad gateway</html>', { status: 502 })))
  await expect(client.query({ query: HealthDocument, fetchPolicy: 'network-only' })).rejects.toBeDefined()
  expect(connectivity.reachable).toBe(false)
})

it('the REST requests report their outcome too', async () => {
  server.use(http.put('/media/me/avatar', () => HttpResponse.error()))
  await expect(trackedFetch('/media/me/avatar', { method: 'PUT' })).rejects.toBeInstanceOf(TypeError)
  expect(connectivity.reachable).toBe(false)
  await expect(trackedFetch('/media/me/avatar', { method: 'PUT' })).rejects.toBeInstanceOf(OfflineError)
})

const defaults = { vehicleDefaults: { __typename: 'VehicleDefaults', distanceUnit: 'KILOMETRES', volumeUnit: 'LITRES', currency: 'EUR', recurringWarnDays: 14, recurringWarnDistance: 500 } }
const session = { session: { __typename: 'Session', mode: 'NONE', user: null, instance: fakeInstance() }, notices: [] }

async function signedIn(client: ReturnType<typeof createApolloClient>) {
  server.use(graphql.query('Session', () => HttpResponse.json({ data: session })))
  await client.query({ query: SessionDocument, fetchPolicy: 'network-only' })
  await deviceData.settled()
}

it('keeps the answer of a query and gives it again while the server is out of reach, without sending anything', async () => {
  const client = createApolloClient('http://localhost/graphql')
  await signedIn(client)
  let sent = 0
  server.use(graphql.query('VehicleDefaults', () => (sent++, HttpResponse.json({ data: defaults }))))
  await client.query({ query: VehicleDefaultsDocument, fetchPolicy: 'network-only' })
  await deviceData.settled()

  connectivity.failed()
  const offline = createApolloClient('http://localhost/graphql') // a fresh cache: the answer comes from the device
  const { data } = await offline.query({ query: VehicleDefaultsDocument, fetchPolicy: 'network-only' })

  expect(data?.vehicleDefaults.currency).toBe('EUR')
  expect(sent).toBe(1)
})

it('a query that fails because the server just went out of reach is answered from the device', async () => {
  const client = createApolloClient('http://localhost/graphql')
  await signedIn(client)
  server.use(graphql.query('VehicleDefaults', () => HttpResponse.json({ data: defaults })))
  await client.query({ query: VehicleDefaultsDocument, fetchPolicy: 'network-only' })
  await deviceData.settled()

  server.use(graphql.query('VehicleDefaults', () => HttpResponse.error()))
  const { data } = await createApolloClient('http://localhost/graphql').query({ query: VehicleDefaultsDocument, fetchPolicy: 'network-only' })

  expect(data?.vehicleDefaults.currency).toBe('EUR')
  expect(connectivity.reachable).toBe(false)
})

it('an answer with errors is never kept, and nothing kept says it is not on this device yet', async () => {
  const client = createApolloClient('http://localhost/graphql')
  await signedIn(client)
  silenceConsoleError()
  server.use(graphql.query('VehicleDefaults', () => HttpResponse.json({ data: null, errors: [{ message: 'Unexpected Execution Error' }] })))
  await expect(client.query({ query: VehicleDefaultsDocument, fetchPolicy: 'network-only' })).rejects.toBeDefined()
  await deviceData.settled()

  connectivity.failed()
  const error = await client.query({ query: VehicleDefaultsDocument, fetchPolicy: 'network-only' }).catch((e: unknown) => e)
  expect(error).toBeInstanceOf(OfflineError)
  expect((error as OfflineError).reason).toBe('notLoaded')
})

it('a screen that needs the server says so, and a change is not sent', async () => {
  const client = createApolloClient('http://localhost/graphql')
  connectivity.failed()

  const admin = await client.query({ query: AdminDocument, fetchPolicy: 'network-only' }).catch((e: unknown) => e)
  expect((admin as OfflineError).reason).toBe('onlineOnly')
  const login = await client.mutate({ mutation: LoginDocument, variables: { input: { email: 'a@b.c', password: 'x' } } }).catch((e: unknown) => e)
  expect(login).toBeInstanceOf(OfflineError)
  expect((login as OfflineError).reason).toBeUndefined()
})

it('signing in stops keeping until the server says who is signed in', async () => {
  const client = createApolloClient('http://localhost/graphql')
  await signedIn(client)
  server.use(
    graphql.mutation('Login', () => HttpResponse.json({ data: { login: { __typename: 'UserInfo', id: 'u2' } } })),
    graphql.query('VehicleDefaults', () => HttpResponse.json({ data: defaults })),
  )
  await client.mutate({ mutation: LoginDocument, variables: { input: { email: 'a@b.c', password: 'x' } } })
  await client.query({ query: VehicleDefaultsDocument, fetchPolicy: 'network-only' })
  await deviceData.settled()

  connectivity.failed()
  const error = await client.query({ query: VehicleDefaultsDocument, fetchPolicy: 'network-only' }).catch((e: unknown) => e)
  expect((error as OfflineError).reason).toBe('notLoaded')
})

it('an operation without a name goes to the server as it is: nothing is kept of it, and no screen hears it applied', async () => {
  server.use(
    http.post('http://localhost/graphql', async ({ request }) => {
      const { query } = (await request.json()) as { query: string }
      return HttpResponse.json({ data: query.trimStart().startsWith('mutation') ? { logout: true } : health })
    }),
  )
  const applied: string[] = []
  const stop = appliedMutations.subscribe((name) => applied.push(name))
  const client = createApolloClient('http://localhost/graphql')
  await signedIn(client)

  const { data } = await client.query({ query: parse('{ health { status version databaseReachable } }'), fetchPolicy: 'network-only' })
  await client.mutate({ mutation: parse('mutation { logout }') })
  await deviceData.settled()
  stop()

  expect(data).toEqual(health)
  expect(applied).toEqual([])
  connectivity.failed()
  await expect(client.query({ query: parse('{ health { status version databaseReachable } }'), fetchPolicy: 'network-only' })).rejects.toBeInstanceOf(OfflineError)
})

it('while a change of a log waits, its details are answered by the device even though the server can be reached', async () => {
  let asked = 0
  server.use(graphql.query('RefuelingDetails', () => (asked++, HttpResponse.json({ data: { refueling: null } }))))
  await deviceData.signedIn('u1')
  await outbox.reload()
  const rows = (await deviceData.rows())!
  await rows.putLogs('refuelings', [{
    __typename: 'Refueling', id: 'r1', vehicleId: 'v1', date: '2026-10-01', volume: 40, totalCost: 100, discount: null, currency: 'EUR', odometer: 1000, isFullTank: true,
    missedPreviousFillUp: false, note: null, reviewState: 'NONE', filledFromPhoto: [], version: 2, deletedAt: null, photos: [], pulledAt: 1,
  } as never])
  await outbox.enqueue({ id: 't1', entity: 'refuelings', action: 'trash', vehicleId: 'v1', targetId: 'other' })
  await outbox.enqueue({ id: 'd1', entity: 'recurring', action: 'markDone', vehicleId: 'v1', targetId: 'e9', targetIds: ['s1'], input: { expenseId: 'e9' } })
  await outbox.enqueue({ id: 'u1', entity: 'refuelings', action: 'update', vehicleId: 'v1', targetId: 'r1', expectedVersion: 2, input: { id: 'r1', volume: 31 } })
  const client = createApolloClient('http://localhost/graphql')

  const { data } = await client.query({ query: RefuelingDetailsDocument, variables: { id: 'r1' }, fetchPolicy: 'network-only' })
  const other = await client.query({ query: RefuelingDetailsDocument, variables: { id: 'r2' }, fetchPolicy: 'network-only' })

  expect(data?.refueling).toMatchObject({ id: 'r1', volume: 31 })
  expect(other.data?.refueling).toBeNull() // nothing waits for that one: the server answered
  expect(asked).toBe(1)
})

it('a key figure of a vehicle added here, while its add waits, is the device\'s answer; another vehicle\'s is the server\'s (#274)', async () => {
  const asked: string[] = []
  server.use(graphql.query('VehicleFigure', ({ variables }) => (asked.push(String(variables.vehicleId)), HttpResponse.json({ errors: [{ message: 'no such vehicle', extensions: { key: 'vehicle.notFound', code: 'NOT_FOUND' } }] }))))
  await deviceData.signedIn('u1')
  await outbox.reload()
  await outbox.enqueue({ id: 'golf', entity: 'vehicles', action: 'add', vehicleId: 'golf', targetId: 'golf', input: { id: 'golf', name: 'Golf', fuelType: 'PETROL' } })
  const client = createApolloClient('http://localhost/graphql')

  const added = await client.query({ query: VehicleFigureDocument, variables: { vehicleId: 'golf', measure: 'ODOMETER', period: null }, fetchPolicy: 'network-only' })

  expect(added.data?.vehicleFigure).toMatchObject({ unit: 'DISTANCE', value: null }) // no reading kept yet: the device's empty answer, not the server's NOT_FOUND
  expect(asked).toEqual([])
  await client.query({ query: VehicleFigureDocument, variables: { vehicleId: 'v9', measure: 'ODOMETER', period: null }, fetchPolicy: 'network-only', errorPolicy: 'all' })
  expect(asked).toEqual(['v9']) // nothing waits for that one: the server is asked
})

it('an answer the device cannot keep (its storage is full) still reaches the screen', async () => {
  const faulty = Object.assign(Object.create(deviceData) as typeof deviceData, { keep: () => Promise.reject(new Error('The storage is full.')) })
  server.use(
    graphql.query('Session', () => HttpResponse.json({ data: session })),
    graphql.query('VehicleDefaults', () => HttpResponse.json({ data: defaults })),
  )
  const client = new ApolloClient({ link: ApolloLink.from([createOfflineLink(faulty), new HttpLink({ uri: 'http://localhost/graphql' })]), cache: new InMemoryCache() })

  const signed = await client.query({ query: SessionDocument, fetchPolicy: 'network-only' })
  const { data } = await client.query({ query: VehicleDefaultsDocument, fetchPolicy: 'network-only' })
  await deviceData.settled()

  expect(signed.data?.session.mode).toBe('NONE')
  expect(data?.vehicleDefaults.currency).toBe('EUR')
  connectivity.failed()
  const error = await client.query({ query: VehicleDefaultsDocument, fetchPolicy: 'network-only' }).catch((e: unknown) => e)
  expect((error as OfflineError).reason).toBe('notLoaded') // nothing was kept
})

/** Signed out while the server was out of reach (#162): the device remembers it, and the server still knows the session. */
async function signedOutHere(logout: () => Response | Promise<Response> = () => new HttpResponse(null, { status: 204 })) {
  const calls = { logout: 0, session: 0, refresh: 0, login: 0 }
  let ended = false
  server.use(
    http.post('/auth/token/logout', () => (calls.logout++, (ended = true), logout())),
    http.post('/auth/token/refresh', () => (calls.refresh++, new HttpResponse(null, { status: 204 }))),
    graphql.query('Session', () => (calls.session++, HttpResponse.json({ data: { session: { mode: 'STANDALONE', user: ended ? null : { id: 'u1', displayName: 'Alice', email: 'a@x.co', isAdmin: false, avatarUrl: null }, instance: fakeInstance() }, notices: [] } }))),
    graphql.mutation('Login', () => (calls.login++, HttpResponse.json({ data: { login: { id: 'u1' } } }))),
  )
  await deviceData.setSignedOutHere({ mode: 'STANDALONE', at: 1 })
  return calls
}

it('signed out here, the device answers the session itself as nobody while the server is out of reach, and sends nothing', async () => {
  const calls = await signedOutHere()
  connectivity.failed()
  const client = createApolloClient('http://localhost/graphql')

  const { data } = await client.query({ query: SessionDocument, fetchPolicy: 'network-only' })

  expect(data?.session).toEqual({ mode: 'STANDALONE', user: null, instance: { generation: '', restorePoint: null } })
  expect(data?.notices).toEqual([])
  expect(calls).toEqual({ logout: 0, session: 0, refresh: 0, login: 0 })
})

it('once the server can be reached, the sign-out it missed is sent first, once, and the session is then the server\'s', async () => {
  const calls = await signedOutHere()
  const client = createApolloClient('http://localhost/graphql')

  const first = await client.query({ query: SessionDocument, fetchPolicy: 'network-only' })
  const again = await client.query({ query: SessionDocument, fetchPolicy: 'network-only' })

  expect(first.data?.session.user).toBeNull() // the cookies' session ended before it was asked
  expect(again.data?.session.user).toBeNull()
  expect(calls.logout).toBe(1)
  expect(await deviceData.signedOutHere()).toBeNull()
})

it('a sign-out the server could not be told of keeps the device signed out, and the cookies are never refreshed meanwhile', async () => {
  silenceConsoleError()
  const calls = await signedOutHere(() => HttpResponse.error() as unknown as Response)
  const client = createApolloClient('http://localhost/graphql')

  const { data } = await client.query({ query: SessionDocument, fetchPolicy: 'network-only' })

  expect(data?.session.user).toBeNull() // the device's own answer: the server's would still name the user
  expect(calls.session).toBe(0)
  expect(await deviceData.signedOutHere()).not.toBeNull() // still to be told
  connectivity.succeeded()
  // Back: told again at once (#236), not left to a query; this server still cannot be told, so the device stays signed out.
  await waitFor(() => expect(calls.logout).toBe(2))
  const { refreshSession } = await import('../../../src/auth/refresh.ts')
  await expect(refreshSession()).resolves.toBe(false)
  expect(calls.refresh).toBe(0)
  expect(await deviceData.signedOutHere()).not.toBeNull()
})

it('signing in forgets a sign-out left over, after telling the server once more', async () => {
  const calls = await signedOutHere(() => new HttpResponse(null, { status: 500 })) // reachable, but the logout went wrong: best effort
  const client = createApolloClient('http://localhost/graphql')

  await client.mutate({ mutation: LoginDocument, variables: { input: { email: 'a@x.co', password: 'secret-password' } } })

  expect(calls.logout).toBe(1)
  expect(calls.login).toBe(1)
  expect(await deviceData.signedOutHere()).toBeNull() // the new sign-in's cookies replace the old ones
})

it('a gateway answering for an app that is down tells the server nothing: the device stays signed out', async () => {
  silenceConsoleError()
  const calls = await signedOutHere(() => new HttpResponse('Bad gateway', { status: 503 }))
  const client = createApolloClient('http://localhost/graphql')

  const { data } = await client.query({ query: SessionDocument, fetchPolicy: 'network-only' })

  expect(data?.session.user).toBeNull()
  expect(calls.logout).toBe(1)
  expect(calls.session).toBe(0)
  expect(await deviceData.signedOutHere()).not.toBeNull()
})

it('a sign-in that fails (a wrong password) keeps the sign-out left over: the old cookies must not sign the device back in', async () => {
  const calls = await signedOutHere(() => new HttpResponse(null, { status: 500 }))
  server.use(graphql.mutation('Login', () => (calls.login++, HttpResponse.json(gqlError('Wrong e-mail or password.', 'auth.invalidCredentials')))))
  const client = createApolloClient('http://localhost/graphql')

  await client.mutate({ mutation: LoginDocument, variables: { input: { email: 'a@x.co', password: 'wrong-password' } } }).catch(() => undefined)

  expect(calls.login).toBe(1)
  expect(await deviceData.signedOutHere()).not.toBeNull()
  const { refreshSession } = await import('../../../src/auth/refresh.ts')
  await expect(refreshSession()).resolves.toBe(false) // the old refresh cookie is never traded in
  expect(calls.refresh).toBe(0)
})

it('setting a password from a reset link signs nobody in, so a sign-out left over stays', async () => {
  const calls = await signedOutHere(() => new HttpResponse(null, { status: 500 }))
  server.use(graphql.mutation('ResetPassword', () => HttpResponse.json({ data: { resetPassword: true } })))
  const client = createApolloClient('http://localhost/graphql')

  await client.mutate({ mutation: ResetPasswordDocument, variables: { input: { token: 't', newPassword: 'a-new-password' } } })

  expect(calls.logout).toBe(0)
  expect(await deviceData.signedOutHere()).not.toBeNull()
})

it('a server with no sessions to end (sign-in off or a proxy now: 404) counts as told, so the device is not held signed out', async () => {
  const calls = await signedOutHere(() => new HttpResponse(null, { status: 404 }))
  server.use(graphql.query('Session', () => (calls.session++, HttpResponse.json({ data: { session: { mode: 'NONE', user: null, instance: fakeInstance() }, notices: [] } }))))
  const client = createApolloClient('http://localhost/graphql')

  const { data } = await client.query({ query: SessionDocument, fetchPolicy: 'network-only' })

  expect(data?.session.mode).toBe('NONE') // the server's answer, its notices and all
  expect(calls).toMatchObject({ logout: 1, session: 1 })
  expect(await deviceData.signedOutHere()).toBeNull()
})

it('a sign-out the server does not answer in time is called off, not left running, and the server counts as out of reach', async () => {
  silenceConsoleError()
  let aborted = false
  await signedOutHere(() => new Promise<Response>(() => undefined))
  server.use(
    http.post('/auth/token/logout', ({ request }) => {
      request.signal.addEventListener('abort', () => (aborted = true))
      return new Promise<Response>(() => undefined) // never answers: a late 204 would clear a newer sign-in's cookies
    }),
  )
  const timeout = AbortSignal.timeout.bind(AbortSignal)
  vi.spyOn(AbortSignal, 'timeout').mockImplementation(() => timeout(20)) // the probes' deadline, made short
  const { endSessionOnServer } = await import('../../../src/auth/signedOutHere.ts')

  await expect(endSessionOnServer()).resolves.toBe(false)

  await vi.waitFor(() => expect(aborted).toBe(true))
  expect(connectivity.reachable).toBe(false) // the next Session is answered by the device at once, not after another wait
  vi.restoreAllMocks()
})

it('a sign-out the server answered without ending the session is told again a while later, with nothing else asking', async () => {
  let answer = 500
  const calls = await signedOutHere(() => new HttpResponse(null, { status: answer }))
  const { RETRY_MS, tellLeftOverSignOut } = await import('../../../src/auth/signedOutHere.ts')
  let retry: (() => void) | undefined
  const original = globalThis.setTimeout
  vi.spyOn(globalThis, 'setTimeout').mockImplementation(((fn: () => void, ms?: number) => {
    if (ms !== RETRY_MS) return original(fn, ms)
    retry = fn
    return 0
  }) as unknown as typeof setTimeout)

  await expect(tellLeftOverSignOut()).resolves.toBe(false)
  vi.restoreAllMocks()
  expect(retry).toBeDefined()
  answer = 204
  retry!()

  await vi.waitFor(async () => expect(await deviceData.signedOutHere()).toBeNull())
  expect(calls.logout).toBe(2)
})

it('signing in while the server is out of reach is refused and keeps the device signed out', async () => {
  const calls = await signedOutHere()
  connectivity.failed()
  const client = createApolloClient('http://localhost/graphql')

  await expect(client.mutate({ mutation: LoginDocument, variables: { input: { email: 'a@x.co', password: 'secret-password' } } })).rejects.toBeInstanceOf(OfflineError)

  expect(calls).toEqual({ logout: 0, session: 0, refresh: 0, login: 0 })
  expect(await deviceData.signedOutHere()).not.toBeNull()
})

