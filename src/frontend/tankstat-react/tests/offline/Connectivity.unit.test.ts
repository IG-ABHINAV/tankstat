import { ApolloLink, gql, Observable, type ApolloClient } from '@apollo/client'
import { ServerError, ServerParseError } from '@apollo/client/errors'
import { execute } from '@apollo/client/link'
import { createOfflineLink } from '../../src/offline/offlineLink.ts'
import { trackedFetch } from '../../src/offline/trackedFetch.ts'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { connectivity, nextDelay, watchConnectivity } from '../../src/offline/connectivity.ts'
import { classifyFailure, isConnectionFailure, OfflineError } from '../../src/offline/errors.ts'
import { LOCK_RETRY_MS, PROBE_TIMEOUT_MS, probeServer, PUSH_AFTER_MS, REFETCH_GAP_MS, SHOWN_GAP_MS, startOfflineRuntime, withDeadline } from '../../src/offline/runtime.ts'
import { appliedMutations } from '../../src/offline/appliedMutations.ts'
import { deviceData, TABS_CHANNEL } from '../../src/offline/deviceData.ts'
import { memoryStorage } from '../../src/offline/deviceStorage.ts'
import { HealthDocument, ServerGenerationDocument, WelcomeDocument } from '../../src/gql/generated.ts'
import { outbox } from '../../src/offline/outbox.ts'
import { ApiError } from '../../src/pictures/ApiError.ts'
import { signedOut } from '../../src/auth/oidc.ts'

describe('connectivity', () => {
  it('starts as unknown, which counts as reachable, and tells its listeners about changes only', () => {
    const heard = vi.fn()
    const stop = connectivity.subscribe(heard)

    expect([connectivity.state, connectivity.reachable]).toEqual(['unknown', true])
    connectivity.failed()
    connectivity.failed()
    expect([connectivity.state, connectivity.reachable]).toEqual(['unreachable', false])
    connectivity.succeeded()
    expect(heard).toHaveBeenCalledTimes(2)
    stop()
  })

  it('waits longer and longer between probes, at most a minute', () => {
    expect([0, 1, 2, 3, 4, 9].map((n) => nextDelay(n))).toEqual([2_000, 5_000, 15_000, 30_000, 60_000, 60_000])
  })
})

describe('classifyFailure', () => {
  const response = (status: number) => new Response(null, { status })

  it('tells "nothing answered" and "a gateway answered for the app" from an answer of the app', () => {
    expect(classifyFailure(new TypeError('Failed to fetch'))).toBe('network')
    expect(classifyFailure(new ServerParseError(new Error('not JSON'), { response: response(200), bodyText: '<html>' }))).toBe('network')
    expect(classifyFailure(new ServerError('Bad Gateway', { response: response(503), bodyText: '' }))).toBe('gateway')
    expect(classifyFailure(new ApiError(undefined, {}, 'Bad Gateway', 502))).toBe('gateway')
    expect(classifyFailure(new ServerError('Internal', { response: response(500), bodyText: '' }))).toBe('answered')
    expect(classifyFailure(new OfflineError())).toBe('answered')
    expect(isConnectionFailure(new OfflineError())).toBe(true)
  })

  it('a request that got no answer in time is "nothing answered"; one the app took back itself is not', () => {
    expect(classifyFailure(new DOMException('signal timed out', 'TimeoutError'))).toBe('network')
    expect(classifyFailure(new DOMException('aborted', 'AbortError'))).toBe('answered')
  })
})

describe('a late answer (#238)', () => {
  it('to a request sent before the server was judged out of reach leaves it out of reach; a bare success still counts', () => {
    const sentIn = connectivity.epoch
    connectivity.failed()
    connectivity.succeeded(sentIn)
    expect(connectivity.reachable).toBe(false)
    connectivity.succeeded(connectivity.epoch)
    expect(connectivity.reachable).toBe(true)
    connectivity.failed()
    connectivity.succeeded()
    expect(connectivity.reachable).toBe(true)
  })

  it('grows the epoch on the change to out of reach only, so a probe out during a second offline event still counts', async () => {
    let answer: (ok: boolean) => void = () => undefined
    const probe = vi.fn(() => new Promise<boolean>((resolve) => (answer = resolve)))
    const watch = watchConnectivity(probe, { setTimer: () => undefined as unknown as ReturnType<typeof setTimeout>, clearTimer: () => undefined })
    try {
      window.dispatchEvent(new Event('offline'))
      const asked = watch.probeNow()
      window.dispatchEvent(new Event('offline')) // the browser says it again while the probe is out
      answer(true)
      await asked
      expect(connectivity.reachable).toBe(true)
    } finally {
      watch.stop()
    }
  })

  it('through the offline link does not count as the server being back, so its real return refetches the screen and downloads', async () => {
    let now = 0
    const refetchQueries = vi.fn(async () => [])
    const client = { query: vi.fn(() => new Promise(() => undefined)), refetchQueries } as unknown as ApolloClient
    const runtime = startOfflineRuntime(client, () => now)
    // A request sent while the server could be reached; its answer comes whenever the test says.
    let answer: (() => void) | undefined
    const terminal = new ApolloLink(
      () =>
        new Observable((observer) => {
          answer = () => {
            observer.next({ data: { vehicle: null } })
            observer.complete()
          }
        }),
    )
    const received: unknown[] = []
    try {
      connectivity.succeeded()
      const sub = execute(ApolloLink.from([createOfflineLink(), terminal]), { query: gql`query VehicleDetails { vehicle(id: "x") { id } }` }, { client: {} as ApolloClient }).subscribe({
        next: (result) => received.push(result.data),
        error: () => undefined,
      })
      now = 1_190
      window.dispatchEvent(new Event('offline'))
      now = 1_270
      answer!() // landing after the offline event
      await Promise.resolve()
      expect(received).toEqual([{ vehicle: null }]) // its data still goes on
      expect([connectivity.reachable, refetchQueries.mock.calls.length]).toEqual([false, 0])

      now = 3_830
      connectivity.succeeded(connectivity.epoch) // the server is really back
      expect(refetchQueries).toHaveBeenCalledTimes(1)
      sub.unsubscribe()
    } finally {
      runtime.stop()
    }
  })

  it('through trackedFetch does not count either', async () => {
    let answer: (response: Response) => void = () => undefined
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>((resolve) => (answer = resolve))))
    try {
      const asked = trackedFetch('/media/me/avatar', { method: 'PUT' })
      connectivity.failed()
      answer(new Response(null, { status: 204 }))
      expect((await asked).status).toBe(204)
      expect(connectivity.reachable).toBe(false)
    } finally {
      vi.unstubAllGlobals()
    }
  })
})

describe('startOfflineRuntime', () => {
  it('asks the screen again when the server is back, but not more often than every 15 seconds', () => {
    vi.useFakeTimers()
    let now = 0
    const refetchQueries = vi.fn(async () => [])
    const client = { query: vi.fn(() => new Promise(() => undefined)), refetchQueries } as unknown as ApolloClient
    const runtime = startOfflineRuntime(client, () => now)
    try {
      const flap = () => {
        connectivity.failed()
        connectivity.succeeded()
      }
      flap()
      now += 2_000
      flap() // a proxy that keeps answering 503 to the refetch
      expect(refetchQueries).toHaveBeenCalledTimes(1)
      now += REFETCH_GAP_MS
      flap()
      expect(refetchQueries).toHaveBeenCalledTimes(2)
    } finally {
      runtime.stop()
      vi.useRealTimers()
    }
  })

  it('downloads soon after a vehicle came in online, so the device can answer for it offline; not after other changes', async () => {
    deviceData.reset(memoryStorage())
    await deviceData.signedIn('u1') // before the start: no download of its own yet
    vi.useFakeTimers()
    // The download asks the server's history first (an answer that says none), then waits for the home list for ever.
    const query = vi.fn((options: { query: unknown }) => (options.query === ServerGenerationDocument ? Promise.resolve({ data: undefined }) : new Promise(() => undefined)))
    const client = { query, refetchQueries: vi.fn(async () => []) } as unknown as ApolloClient
    const runtime = startOfflineRuntime(client)
    const downloads = () => query.mock.calls.filter(([options]) => options.query === WelcomeDocument).length
    try {
      appliedMutations.tell('UpdateVehicle')
      await vi.advanceTimersByTimeAsync(PUSH_AFTER_MS)
      expect(downloads()).toBe(0)

      appliedMutations.tell('AddVehicle')
      await vi.advanceTimersByTimeAsync(PUSH_AFTER_MS)
      await vi.waitFor(() => expect(downloads()).toBe(1))
    } finally {
      runtime.stop()
      vi.useRealTimers()
    }
  })

  it('another tab signing out of OIDC makes this tab\'s sign-in screen wait for a click, not go back to the provider', async () => {
    deviceData.reset(memoryStorage())
    await deviceData.boot(memoryStorage())
    await deviceData.signedIn('u1')
    const refetchQueries = vi.fn(async () => [])
    const client = { query: vi.fn(() => new Promise(() => undefined)), refetchQueries } as unknown as ApolloClient
    const runtime = startOfflineRuntime(client)
    const otherTab = new BroadcastChannel(TABS_CHANNEL)
    try {
      otherTab.postMessage({ type: 'signedOut', mode: 'OIDC' })

      await vi.waitFor(() => expect(refetchQueries).toHaveBeenCalledWith({ include: ['Session'] }))
      expect(signedOut.isSet()).toBe(true) // marked before the Session asked again shows the sign-in screen
    } finally {
      otherTab.close()
      runtime.stop()
    }
  })
})

describe('withDeadline', () => {
  it('fails once the time is up, without waiting for a request that never ends', async () => {
    vi.useFakeTimers()
    try {
      const hung = withDeadline(new Promise<boolean>(() => undefined), PROBE_TIMEOUT_MS)
      vi.advanceTimersByTime(PROBE_TIMEOUT_MS)
      await expect(hung).rejects.toThrow()
      await expect(withDeadline(Promise.resolve(true), PROBE_TIMEOUT_MS)).resolves.toBe(true)
    } finally {
      vi.useRealTimers()
    }
  })
})

describe('watchConnectivity', () => {
  afterEach(() => vi.useRealTimers())

  it('while the server is out of reach it asks again with growing pauses until it answers', async () => {
    vi.useFakeTimers()
    let answers = false
    const probe = vi.fn(async () => answers)
    const watch = watchConnectivity(probe, { online: () => true })

    connectivity.failed()
    await vi.advanceTimersByTimeAsync(2_000)
    expect(probe).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(4_999)
    expect(probe).toHaveBeenCalledTimes(1) // the second pause is longer
    answers = true
    await vi.advanceTimersByTimeAsync(1)
    expect(probe).toHaveBeenCalledTimes(2)
    expect(connectivity.reachable).toBe(true)
    await vi.advanceTimersByTimeAsync(120_000)
    expect(probe).toHaveBeenCalledTimes(2) // nothing more while it answers
    watch.stop()
  })

  it('the browser going offline marks the server unreachable at once, and coming online asks at once', async () => {
    const probe = vi.fn(async () => true)
    const watch = watchConnectivity(probe, { online: () => true })

    window.dispatchEvent(new Event('offline'))
    expect(connectivity.reachable).toBe(false)
    window.dispatchEvent(new Event('online'))
    await vi.waitFor(() => expect(connectivity.reachable).toBe(true))
    expect(probe).toHaveBeenCalledTimes(1)
    watch.stop()
  })

  it('a browser that starts offline starts unreachable', () => {
    const watch = watchConnectivity(async () => false, { online: () => false, setTimer: () => setTimeout(() => undefined, 0), clearTimer: () => undefined })

    expect(connectivity.reachable).toBe(false)
    watch.stop()
  })
})

describe('the offline runtime', () => {
  /** A client whose requests all answer: Health with its status, the download with nothing (it then gives up), a sync with every change applied. */
  function answeringClient(refetch: () => Promise<unknown> = async () => []) {
    const query = vi.fn(async (options: { query: unknown }) => (options.query === HealthDocument ? { data: { health: { status: 'ok' } } } : { data: undefined }))
    const mutate = vi.fn(async (options: { variables: { input: { changes: { id: string }[] } } }) => ({
      data: { syncChanges: { results: options.variables.input.changes.map((c) => ({ id: c.id, status: 'APPLIED', reason: null })) } },
    }))
    const refetchQueries = vi.fn(refetch)
    const client = { query, mutate, refetchQueries } as unknown as ApolloClient
    const asked = (document: unknown) => query.mock.calls.filter(([options]) => options.query === document).length
    return { client, query, mutate, refetchQueries, asked }
  }

  /** Web Locks as another tab sees them: `held` names the locks that tab holds right now. */
  function otherTabLocks(held: Set<string>): LockManager {
    const request = (name: string, ...rest: unknown[]) => {
      const callback = rest.at(-1) as (lock: Lock | null) => Promise<unknown>
      const options = rest.length > 1 ? (rest[0] as LockOptions) : {}
      return callback(options.ifAvailable && held.has(name) ? null : { name, mode: 'exclusive' })
    }
    return { request, query: async () => ({ held: [], pending: [] }) } as unknown as LockManager
  }

  const refuelling = { id: 'r1', entity: 'refuelings', action: 'add', vehicleId: 'v1', targetId: 'r1', input: { id: 'r1', vehicleId: 'v1' } } as const

  beforeEach(async () => {
    vi.spyOn(console, 'warn').mockImplementation(() => undefined) // the stub client's download gives up, and says so
    deviceData.reset(memoryStorage())
    await deviceData.signedIn('u1') // before the start: no download of its own yet
    await outbox.reload()
  })

  afterEach(async () => {
    vi.useRealTimers()
    vi.restoreAllMocks()
    deviceData.reset(memoryStorage())
    await outbox.reload()
  })

  it('Try again asks the server at once: as a plain request before the runtime runs, through its probe once it does', async () => {
    const failing = { query: vi.fn(() => Promise.reject(new TypeError('Failed to fetch'))) } as unknown as ApolloClient
    await expect(probeServer(failing)).resolves.toBeUndefined()
    expect(failing.query).toHaveBeenCalledWith(expect.objectContaining({ query: HealthDocument, context: { offline: 'bypass' } }))

    const { client, asked } = answeringClient()
    const runtime = startOfflineRuntime(client)
    try {
      connectivity.failed()
      await probeServer(client)
      expect(asked(HealthDocument)).toBe(1)
      expect(connectivity.reachable).toBe(true) // the probe marked it
    } finally {
      runtime.stop()
    }
  })

  it('a change kept while the server can be reached is sent soon after; none while it cannot', async () => {
    vi.useFakeTimers()
    const { client, mutate } = answeringClient()
    const runtime = startOfflineRuntime(client)
    try {
      connectivity.failed()
      await outbox.enqueue({ ...refuelling, id: 'offline', targetId: 'offline' })
      await vi.advanceTimersByTimeAsync(PUSH_AFTER_MS)
      expect(mutate).not.toHaveBeenCalled()
      await outbox.remove(['offline'])

      connectivity.reset()
      await outbox.enqueue(refuelling)
      await vi.advanceTimersByTimeAsync(PUSH_AFTER_MS)

      await vi.waitFor(() => expect(outbox.changes).toEqual([]))
      expect(mutate).toHaveBeenCalledTimes(1)
      expect(mutate.mock.calls[0][0].variables.input.changes.map((c) => c.id)).toEqual(['r1'])
    } finally {
      runtime.stop()
    }
  })

  it('while another tab syncs, the changes kept here are looked at again a little later; with none left, nothing more happens', async () => {
    vi.useFakeTimers()
    const held = new Set(['tankstat-sync'])
    Object.defineProperty(navigator, 'locks', { value: otherTabLocks(held), configurable: true })
    const { client, mutate, asked } = answeringClient()
    const runtime = startOfflineRuntime(client)
    try {
      await outbox.enqueue(refuelling)
      await vi.advanceTimersByTimeAsync(PUSH_AFTER_MS)
      expect(mutate).not.toHaveBeenCalled() // the other tab is at it

      held.clear()
      await vi.advanceTimersByTimeAsync(LOCK_RETRY_MS)
      await vi.waitFor(() => expect(outbox.changes).toEqual([]))
      expect(mutate).toHaveBeenCalledTimes(1)

      held.add('tankstat-sync')
      const downloads = asked(WelcomeDocument)
      appliedMutations.tell('AddVehicle')
      await vi.advanceTimersByTimeAsync(PUSH_AFTER_MS + LOCK_RETRY_MS)
      expect(asked(WelcomeDocument)).toBe(downloads) // kept from it, and nothing waits to be looked at again
    } finally {
      runtime.stop()
      delete (navigator as { locks?: unknown }).locks
    }
  })

  it('showing the tab again downloads only when the last download is a while ago', async () => {
    let now = 0
    const { client, asked } = answeringClient()
    const runtime = startOfflineRuntime(client, () => now)
    const shown = () => document.dispatchEvent(new Event('visibilitychange'))
    try {
      shown()
      await vi.waitFor(() => expect(asked(WelcomeDocument)).toBe(1))

      now += SHOWN_GAP_MS - 1
      shown()
      now += 1
      shown()
      await vi.waitFor(() => expect(asked(WelcomeDocument)).toBe(2))
    } finally {
      runtime.stop()
    }
  })

  it('a screen that fails to be asked again never breaks the runtime: when the server is back, or when another tab signed someone else in', async () => {
    await deviceData.boot(memoryStorage()) // listens to the other tabs
    const { client, refetchQueries } = answeringClient(() => Promise.reject(new Error('A query failed.')))
    const runtime = startOfflineRuntime(client)
    const otherTab = new BroadcastChannel(TABS_CHANNEL)
    try {
      connectivity.failed()
      connectivity.succeeded()
      expect(refetchQueries).toHaveBeenCalledWith({ include: 'active' })

      otherTab.postMessage({ type: 'signedIn', user: 'u2' })
      await vi.waitFor(() => expect(refetchQueries).toHaveBeenCalledWith({ include: ['Session'] }))
    } finally {
      otherTab.close()
      runtime.stop()
    }
  })
})
