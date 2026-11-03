import { connectivity, PROBE_TIMEOUT_MS } from '../offline/connectivity.ts'
import { deviceData } from '../offline/deviceData.ts'
import { signOutDevice } from './refresh.ts'

/** How long a sign-out left over waits to be told again when the server answered but did not end the session (a 500, say). */
export const RETRY_MS = 30_000

/**
 * Ends this device's session on the server, best effort: only while it can be reached, and never waiting longer than a probe would. The
 * request is given up at the deadline, never left running: a late answer clears the cookies by name, a new sign-in's too; a timeout counts
 * as no answer, so the server is taken as out of reach. True when the server ended it (a 2xx), or has no sessions to end (404: sign-in off
 * or behind a proxy, whose mode change ended them all); false when it could not be asked or did not do it. A gateway answering for an app
 * that is down (502, 503, 504) is no answer of the server's.
 */
export async function endSessionOnServer(): Promise<boolean> {
  if (!connectivity.reachable) return false
  try {
    const signal = typeof AbortSignal.timeout === 'function' ? AbortSignal.timeout(PROBE_TIMEOUT_MS) : undefined
    const response = await signOutDevice(signal)
    return response.ok || response.status === 404
  } catch {
    return false
  }
}

let telling: Promise<boolean> | null = null
let retry: ReturnType<typeof setTimeout> | null = null
/** Waiting for the server to answer again (`connectivity`), to tell it then: its unsubscribe. */
let onReturn: (() => void) | null = null
/** Counts resets: a tell still under way when it is reset arms nothing afterwards. */
let generation = 0

/**
 * A sign-out the server has not heard of (`SignedOutHere`): told once it can be reached, and forgotten when it answered. True when none
 * is left, false while one still waits; rejects when the device cannot read whether one is left. Callers at the same moment share one
 * request. Out of reach, it is told as soon as the server answers again (#236: not left to the runtime's refetch, which a late answer
 * can hold back, nor to a `Session` query nothing asks on the sign-in screen). When the server answered without ending the session, or
 * a tell made on its return failed again (it answers the probes but not this: a timeout or a gateway error marks it out of reach once
 * more), it is told again after `RETRY_MS`, never in a loop with the probes. Whichever way, the note is read again first: a sign-in
 * meanwhile has forgotten it, and is never ended.
 */
export function tellLeftOverSignOut(device: typeof deviceData = deviceData, afterReturn = false): Promise<boolean> {
  // A caller joining a tell under way shares it, and with it how that one ends (`afterReturn` is the first caller's).
  if (telling) return telling
  const started = generation
  const tell: Promise<boolean> = (async () => {
    if (!(await device.signedOutHere())) return true
    if (!(await endSessionOnServer())) {
      if (started !== generation) return false
      if (connectivity.reachable || afterReturn)
        retry ??= setTimeout(() => {
          retry = null
          void tellLeftOverSignOut(device).catch(() => undefined)
        }, RETRY_MS)
      else
        // Only a real answer, never "nothing tried yet" (which also counts as reachable): the server must be back to be told.
        onReturn ??= connectivity.subscribe(() => {
          if (connectivity.state !== 'reachable') return
          onReturn?.()
          onReturn = null
          void tellLeftOverSignOut(device, true).catch(() => undefined)
        })
      return false
    }
    // Reset meanwhile (tests): the device is a fresh one, whose note this tell never read.
    if (started === generation) await device.setSignedOutHere(null)
    return true
  })().finally(() => {
    // Only its own: a tell from before a reset never drops the one that came after.
    if (telling === tell) telling = null
  })
  telling = tell
  return tell
}

/** Tests: no sign-out being told, and none waiting to be told again. */
export function resetSignedOutHere(): void {
  generation++
  if (retry) clearTimeout(retry)
  retry = null
  onReturn?.()
  onReturn = null
  telling = null
}
