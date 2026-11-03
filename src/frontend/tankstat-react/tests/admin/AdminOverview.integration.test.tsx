import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse } from 'msw'
import { graphql } from '../support/graphql.ts'
import { afterAll, afterEach, beforeAll, expect, it } from 'vitest'
import App from '../../src/App.tsx'
import { answered, server } from '../support/server.ts'
import { fakeVehicleBackend, healthHandler, renderWithApollo, sessionHandler, user } from '../support/mocks.tsx'

beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => server.resetHandlers())
afterAll(() => server.close())

/** The server's answer to the overview: by default nothing to act on, three users (one administrator, one disabled) and two vehicles. */
function overviewServer({ restartPending = false, ignored = false, databaseReachable = true, exists = true } = {}) {
  const calls: string[] = []
  server.use(
    sessionHandler('STANDALONE', () => user({ isAdmin: true })),
    healthHandler,
    ...fakeVehicleBackend().handlers,
    graphql.query('AdminOverview', () => {
      calls.push('AdminOverview')
      return HttpResponse.json({
        data: {
          systemSettings: {
            __typename: 'SystemSettingsInfo',
            restartPending,
            file: { __typename: 'SettingsFileInfo', path: '/data/settings.json', exists, ignored },
            folders: [
              { __typename: 'ServerFolder', kind: 'PICTURES', path: '/data/uploads', setting: 'Storage:Path', variable: 'Storage__Path', sameAsPictures: false },
              { __typename: 'ServerFolder', kind: 'PHOTOS', path: '/data/uploads', setting: 'Storage:PhotosPath', variable: 'Storage__PhotosPath', sameAsPictures: true },
              { __typename: 'ServerFolder', kind: 'BACKUPS', path: '/data/backups', setting: 'Backups:Path', variable: 'Backups__Path', sameAsPictures: false },
              { __typename: 'ServerFolder', kind: 'KEYS', path: '/data/keys', setting: 'DataProtection:KeysPath', variable: 'DataProtection__KeysPath', sameAsPictures: false },
              { __typename: 'ServerFolder', kind: 'SETTINGS_FILE', path: '/data/settings.json', setting: 'Settings:Path', variable: 'Settings__Path', sameAsPictures: false },
            ],
          },
          users: [
            { __typename: 'UserAccount', id: 'u1', isAdmin: true, isDisabled: false },
            { __typename: 'UserAccount', id: 'u2', isAdmin: false, isDisabled: true },
            { __typename: 'UserAccount', id: 'u3', isAdmin: false, isDisabled: false },
          ],
          vehicleCount: 2,
          health: { __typename: 'HealthReport', version: '1.2.3', databaseReachable },
        },
      })
    }),
  )
  return calls
}

const overview = async () => within(await screen.findByRole('region', { name: 'Administration' }))

it('says in one line that nothing needs attention, and gives the facts with links to the pages behind them', async () => {
  overviewServer()
  renderWithApollo(<App />, '/admin')

  const view = await overview()
  expect(await view.findByText('Nothing needs your attention.')).toBeInTheDocument()
  expect(view.getByText('1.2.3')).toBeInTheDocument()
  expect(view.getByText('Reachable')).toBeInTheDocument()
  expect(view.getByRole('link', { name: '3 users (administrators: 1, disabled: 1)' })).toHaveAttribute('href', '/admin/people') // reads right whatever each count is
  expect(view.getByRole('link', { name: '2 vehicles' })).toHaveAttribute('href', '/admin/vehicles')
  expect(view.getByText('/data/settings.json')).toBeInTheDocument()
})

it('keeps the folders out of the way until asked, then says what each holds and the variable that changes it', async () => {
  overviewServer()
  renderWithApollo(<App />, '/admin')
  const view = await overview()

  const folders = await view.findByRole('button', { name: /^Where the server keeps its files/ })
  expect(folders).toHaveAttribute('aria-expanded', 'false')
  await userEvent.setup().click(folders)

  const panel = within(view.getByRole('region', { name: /^Where the server keeps its files/ }))
  const items = panel.getAllByRole('listitem')
  expect(items.map((item) => within(item).getAllByText(/./)[0].textContent)).toEqual(['Pictures', 'Photos of logs', 'Backups', 'Sign-in keys', 'Settings file'])
  expect(items[0]).toHaveTextContent('/data/uploadsChange it with the environment variable Storage__Path')
  // The photos with the pictures: no second copy of the path, and how to give them a folder of their own.
  expect(items[1]).toHaveTextContent('In the pictures folder.For a folder of their own, set the environment variable Storage__PhotosPath')
  expect(items[3]).toHaveTextContent('DataProtection__KeysPath')
})

it('lists what needs attention, each with where to act on it', async () => {
  const ui = userEvent.setup()
  overviewServer({ restartPending: true, ignored: true })
  renderWithApollo(<App />, '/admin')

  const view = await overview()
  const items = within(await view.findByRole('list')).getAllByRole('listitem')
  expect(items.map((li) => li.textContent)).toEqual([
    'The settings file was left out at the last start: the server runs without it. Go to Server settings',
    'Saved settings wait for a restart of the server. Go to Server settings',
  ])
  expect(view.queryByText('Nothing needs your attention.')).not.toBeInTheDocument()

  await ui.click(within(items[1]).getByRole('link', { name: 'Go to Server settings' }))
  expect(await screen.findByRole('heading', { level: 1, name: 'Server settings' })).toBeInTheDocument()
  // The page asks for its settings, which this file does not answer: that request fails inside this test, never after the teardown's
  // reset of the reachability, where it would leave the next test out of reach with no probe to bring it back.
  await answered()
})

it('asks the server every time it is opened, never answering from what it kept', async () => {
  const calls = overviewServer()
  const first = renderWithApollo(<App />, '/admin')
  await (await overview()).findByText('Nothing needs your attention.')
  first.unmount()

  renderWithApollo(<App />, '/admin')
  await (await overview()).findByText('Nothing needs your attention.')
  expect(calls).toHaveLength(2)
})

it("the old administration page's tabs lead to where they went, and the old vehicle list to its new address with its query", async () => {
  overviewServer()
  server.use(graphql.query('Admin', () => HttpResponse.json({ data: { users: [], canSetUserPasswords: false, accessSettings: { defaultLevelForOthers: 'NONE' }, accessGrants: [] } })))

  const access = renderWithApollo(<App />, '/admin?tab=access')
  expect(await screen.findByRole('heading', { level: 1, name: 'People' })).toBeInTheDocument()
  expect(screen.getByRole('tab', { name: 'Access' })).toHaveAttribute('aria-selected', 'true')
  access.unmount()

  const users = renderWithApollo(<App />, '/admin?tab=users')
  expect(await screen.findByRole('heading', { level: 1, name: 'People' })).toBeInTheDocument()
  expect(screen.getByRole('tab', { name: 'Users' })).toHaveAttribute('aria-selected', 'true')
  users.unmount()

  renderWithApollo(<App />, '/vehicles')
  expect(await screen.findByRole('heading', { level: 1, name: 'Vehicles' })).toBeInTheDocument()
})

it('is for administrators only', async () => {
  server.use(sessionHandler('STANDALONE', () => user()), healthHandler, ...fakeVehicleBackend().handlers)
  renderWithApollo(<App />, '/admin')

  await screen.findByRole('heading', { name: 'Your vehicles' }) // bounced back to the home page
  expect(screen.queryByRole('region', { name: 'Administration' })).not.toBeInTheDocument()
})
