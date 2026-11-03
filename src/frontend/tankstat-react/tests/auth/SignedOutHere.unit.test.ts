import { waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { afterAll, afterEach, beforeAll, expect, it } from 'vitest'
import { resetSignedOutHere, tellLeftOverSignOut } from '../../src/auth/signedOutHere.ts'
import { connectivity } from '../../src/offline/connectivity.ts'
import { deviceData } from '../../src/offline/deviceData.ts'
import { graphql } from '../support/graphql.ts'
import { server } from '../support/server.ts'

beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => server.resetHandlers())
afterAll(() => server.close())

/** A sign-out noted on the device while the server was out of reach (#162, #236), and what the server is then asked. */
async function signedOutWhileUnreachable() {
  const calls = { logout: 0, session: 0 }
  server.use(
    http.post('/auth/token/logout', () => (calls.logout++, new HttpResponse(null, { status: 204 }))),
    graphql.query('Session', () => (calls.session++, HttpResponse.json({ data: { session: null } }))),
  )
  await deviceData.setSignedOutHere({ mode: 'STANDALONE', at: 1 })
  connectivity.failed()
  expect(await tellLeftOverSignOut()).toBe(false) // out of reach: not told yet
  expect(calls.logout).toBe(0)
  return calls
}

it('tells the server as soon as it answers again, once, without waiting for any query, and forgets the note', async () => {
  const calls = await signedOutWhileUnreachable()

  connectivity.succeeded() // the server answered something (a probe, a late answer)
  await waitFor(() => expect(calls.logout).toBe(1))
  await waitFor(async () => expect(await deviceData.signedOutHere()).toBeNull())

  connectivity.failed()
  connectivity.succeeded() // back again: nothing left to tell
  await new Promise((resolve) => setTimeout(resolve, 20))
  expect(calls).toEqual({ logout: 1, session: 0 })
})

it('tells once on the return when the server answers everything but this, then waits RETRY_MS, never in a loop with the probes', async () => {
  const calls = await signedOutWhileUnreachable()
  server.use(http.post('/auth/token/logout', () => (calls.logout++, HttpResponse.error()))) // no answer: out of reach again

  connectivity.succeeded() // a probe answered
  await waitFor(() => expect(calls.logout).toBe(1))
  await waitFor(() => expect(connectivity.state).toBe('unreachable')) // the tell itself took the server for gone
  connectivity.succeeded() // the next probe answers too
  await new Promise((resolve) => setTimeout(resolve, 20))

  expect(calls.logout).toBe(1) // not told again on every return: after RETRY_MS
  expect(await deviceData.signedOutHere()).not.toBeNull()
})

it('never ends a session signed in meanwhile: the note is read again first', async () => {
  const calls = await signedOutWhileUnreachable()

  await deviceData.setSignedOutHere(null) // a sign-in forgot it
  connectivity.succeeded()
  await new Promise((resolve) => setTimeout(resolve, 20))

  expect(calls.logout).toBe(0)
})

it('waits for a real answer: "nothing tried yet" counts as reachable but is no sign the server is back', async () => {
  const calls = await signedOutWhileUnreachable()

  connectivity.reset()
  await new Promise((resolve) => setTimeout(resolve, 20))
  expect(calls.logout).toBe(0)

  connectivity.succeeded()
  await waitFor(() => expect(calls.logout).toBe(1))
})

it('stops waiting when reset', async () => {
  const calls = await signedOutWhileUnreachable()

  resetSignedOutHere()
  connectivity.succeeded()
  await new Promise((resolve) => setTimeout(resolve, 20))

  expect(calls.logout).toBe(0)
})
