import { act, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { expect } from 'vitest'
import { graphql } from './graphql.ts'

// Handlers are registered per test with server.use(); unhandled requests fail the test. The defaults: the bell in the top bar asks for
// the unread count on every screen (an empty inbox; notification tests override it with fakeNotificationBackend), the top bar and the
// navigation ask for the changes the server could not apply (none; sync tests answer ParkedChanges themselves), the add dialogs ask
// whether photos are read on the server (they are not; photo reading tests override it with fakeRecognition), and the app asks for the
// user's UI settings once and tells the server about every change (nothing saved, every save echoed; settings tests use
// fakeSettingsBackend). The echoes answer exactly the fields the documents select, or Apollo warns about missing fields in every test.
// A test that opens a vehicle page only to check something else (a card's link, a trash from the Details tab) gets an empty dashboard
// and no schedules; dashboard and recurring tests answer them with their own fakes.
export const server = setupServer(
  graphql.query('VehicleDashboard', ({ variables }) =>
    HttpResponse.json({ data: { vehicle: { __typename: 'Vehicle', id: variables.id, summary: null, dashboard: null }, vehicleCharts: [] } }),
  ),
  // The Standard preset's ready-made charts ask for their data (the grid draws them once it is measured, which can be after the test that
  // passed through the page has ended): nothing to draw.
  graphql.query('ChartData', () => HttpResponse.json({ data: { vehicleChartData: { __typename: 'ChartData', unit: 'CURRENCY', series: [] } } })),
  graphql.query('RecurringExpenses', ({ variables }) =>
    HttpResponse.json({ data: { vehicle: { __typename: 'Vehicle', id: variables.vehicleId, recurring: [] } } }),
  ),
  // The token endpoints (auth/refresh.ts): no test has a refresh cookie, so a refresh says "sign in"; signing out always works.
  http.post('/auth/token/refresh', () => HttpResponse.json({ key: 'auth.unauthenticated' }, { status: 401 })),
  http.post('/auth/token/logout', () => new HttpResponse(null, { status: 204 })),
  // The pictures (`/media/<id>`), which the offline download keeps: any id is a tiny image; picture tests answer them themselves.
  http.get('*/media/:id', () => new HttpResponse(new Uint8Array([1]), { headers: { 'Content-Type': 'image/webp' } })),
  // The Account page lists the devices the user is signed in on (Standalone and OIDC): by default only this one.
  graphql.query('MySessions', () =>
    HttpResponse.json({
      data: {
        mySessions: [
          { id: 's-this', client: 'Firefox on Linux', createdAt: '2026-09-01T08:00:00Z', lastUsedAt: '2026-10-01T08:00:00Z', expiresAt: '2026-12-30T08:00:00Z', current: true },
        ],
      },
    }),
  ),
  // The offline download asks which history the server's data has before it writes anything: by default a server that does not say (as
  // an older one), so no download sets anything aside; generation tests answer it themselves.
  graphql.query('ServerGeneration', () => HttpResponse.json({ data: { session: { __typename: 'Session', instance: { __typename: 'InstanceInfo', generation: '' } } } })),
  graphql.query('UnreadNotificationCount', () => HttpResponse.json({ data: { notificationCount: 0 } })),
  graphql.query('ParkedChanges', () => HttpResponse.json({ data: { parkedChanges: [] } })),
  graphql.query('RecognitionStatus', () => HttpResponse.json({ data: { recognitionStatus: { available: false } } })),
  graphql.query('PasswordRequirements', () => HttpResponse.json({ data: { passwordRequirements: { minLength: 10, maxLength: 128 } } })),
  graphql.query('UiSettings', () => HttpResponse.json({ data: { uiSettings: { navOpen: null, language: null, colorMode: null, surface: null, timeFormat: null, dateFormat: null, defaultTimeFormat: null, defaultDateFormat: null, grids: [] } } })),
  // The Account page's Offline data section: the default window, no vehicles of its own (tests with vehicles answer these themselves).
  graphql.query('OfflineSettings', () => HttpResponse.json({ data: { offlineSettings: { __typename: 'OfflineSettingsInfo', defaultWindow: 'span:P2M', vehicles: [] } } })),
  graphql.query('OfflineVehicles', () => HttpResponse.json({ data: { myVehicles: [] } })),
  graphql.query('OfflineEstimates', () => HttpResponse.json({ data: { myVehicles: [] } })),
  graphql.mutation('UpdateOfflineSettings', ({ variables }) =>
    HttpResponse.json({ data: { updateOfflineSettings: { __typename: 'OfflineSettingsInfo', ...variables.input, vehicles: variables.input.vehicles.map((v: object) => ({ __typename: 'OfflineVehicleWindow', ...v })) } } }),
  ),
  graphql.mutation('UpdateUiSettings', ({ variables }) =>
    HttpResponse.json({
      data: {
        updateUiSettings: {
          navOpen: variables.input.navOpen ?? null,
          language: variables.input.language ?? null,
          colorMode: variables.input.colorMode ?? null,
          surface: variables.input.surface ?? null,
          timeFormat: variables.input.timeFormat ?? null,
          dateFormat: variables.input.dateFormat ?? null,
          defaultTimeFormat: null,
          defaultDateFormat: null,
        },
      },
    }),
  ),
  graphql.mutation('SaveGridSettings', ({ variables }) => HttpResponse.json({ data: { saveGridSettings: variables.input } })),
  graphql.mutation('ResetGridSettings', () => HttpResponse.json({ data: { resetGridSettings: true } })),
)

// The requests the server has taken and not answered yet, by id. A request ends with `request:end`, except one whose handler threw
// (msw answers it with a 500 and emits only the response event) and one no handler takes (`onUnhandledFrame: 'error'` fails it), so
// any of these ends it.
const inFlight = new Set<string>()
server.events.on('request:start', ({ requestId }) => void inFlight.add(requestId))
for (const end of ['request:end', 'request:unhandled', 'response:mocked', 'response:bypass'] as const) {
  server.events.on(end, ({ requestId }) => void inFlight.delete(requestId))
}

/**
 * Waits until every request sent so far is answered and the app has taken the answers in, and nothing new went out meanwhile. A test
 * that marks the server unreachable (`connectivity.failed()`) waits for this first: the app counts any answer as the server being back,
 * and msw 3 answers a few ticks later than msw 2 did, so an answer to the page's first requests could otherwise arrive after the test
 * cut the connection.
 */
export async function answered() {
  do {
    await waitFor(() => expect(inFlight.size).toBe(0))
    // A turn of the event loop: a request the app has just sent reaches msw (and this count) a few ticks after its fetch.
    await act(() => new Promise((resolve) => setTimeout(resolve, 0)))
  } while (inFlight.size > 0)
}
