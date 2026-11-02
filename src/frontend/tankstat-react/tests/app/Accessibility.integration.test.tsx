import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse } from 'msw'
import { graphql } from '../support/graphql.ts'
import { expect, it, onTestFinished, vi } from 'vitest'
import App from '../../src/App.tsx'
import { ErrorBoundary } from '../../src/ErrorBoundary.tsx'
import { SIGNED_OUT_KEY } from '../../src/auth/oidc.ts'
import { navigation } from '../../src/navigation.ts'
import { connectivity } from '../../src/offline/connectivity.ts'
import { deviceData } from '../../src/offline/deviceData.ts'
import { outbox } from '../../src/offline/outbox.ts'
import { platform } from '../../src/pwa/platform.ts'
import { check, setup, setupAccessibilityTests } from '../support/accessibility.tsx'
import { answered, server } from '../support/server.ts'
import { backupRecipientHandler, fakeLogBackend, fakeVehicle, fakeVehicleBackend, healthHandler, renderWithApollo, sessionHandler, silenceConsoleError, stubViewport, user, wizardSettings } from '../support/mocks.tsx'

// The pages, the shell and the sign-in screens. The log dialogs and photos are in AccessibilityLogs, the recurring expenses and the home
// cards in AccessibilityRecurring (split so the files run side by side).
setupAccessibilityTests()

it('the message shown when a page crashes is an alert with a reachable reload button and has no violations', async () => {
  silenceConsoleError() // React prints the component that throws
  const Boom = () => {
    throw new Error('boom')
  }
  const view = renderWithApollo(
    <ErrorBoundary>
      <Boom />
    </ErrorBoundary>,
  )

  expect(await screen.findByRole('alert')).toHaveTextContent('Something went wrong while showing this page.')
  expect(screen.getByRole('button', { name: 'Reload the page' })).toBeVisible()
  await check(view.container)
})

it('the vehicle list has no accessibility violations (desktop)', async () => {
  const { view } = setup('/vehicles')
  await screen.findByText('Octavia')

  await check(view.container)
})

it('the vehicle list has no accessibility violations (phone, menu drawer open)', async () => {
  const { view, ui } = setup('/vehicles', 'phone')
  await screen.findByText('Octavia')
  await ui.click(screen.getByRole('button', { name: 'Show menu' }))

  await check(await screen.findByRole('dialog').then((d) => d.ownerDocument.body)).catch((e) => {
    throw e
  })
  expect(view.container).toBeTruthy()
})

it('the vehicle page and its tabs have no violations', async () => {
  const { view, ui } = setup('/vehicles/v1?tab=refuelings')
  await screen.findByText(/Sep 1, 2026/)
  await check(view.container)

  await ui.click(screen.getByRole('tab', { name: /Expenses/ }))
  await screen.findByText('Oil change')
  await check(view.container)

  await ui.click(screen.getByRole('tab', { name: /Details/ }))
  await screen.findByRole('button', { name: 'Choose a picture' })
  await check(view.container)

  await ui.click(screen.getByRole('tab', { name: /Sharing/ }))
  await screen.findByText('Nobody has been given access to the logs yet.')
  await check(view.container)

  // The dashboard as a vehicle without data shows it: the Standard preset's figures and charts (the Customize dialog is scanned by the dashboard's tests).
  await ui.click(screen.getByRole('tab', { name: /Dashboard/ }))
  await screen.findByText(/Compose your own charts/)
  await check(view.container)
})

it('the trash, account and administration pages have no violations', async () => {
  for (const route of ['/trash', '/trash?tab=refuelings', '/trash?tab=expenses', '/account', '/admin', '/admin/people', '/sync']) {
    const { view } = setup(route)
    await screen.findByRole('main')
    await screen.findByRole('heading', { level: 1 })
    await check(view.container)
    view.unmount()
  }
})

it('the changes the server could not apply, what happened to them on each side, and the confirmations of a choice, are labelled and free of violations', async () => {
  const { ui } = setup('/sync')
  server.use(
    graphql.query('ParkedChanges', () =>
      HttpResponse.json({
        data: {
          parkedChanges: [{
            __typename: 'SyncChangeInfo', id: 'c1', kind: 'DELETE_REFUELING', status: 'PARKED', vehicleId: 'v1', targetId: 'r1', change: '{"id":"c1","expectedVersion":1,"deleteRefueling":"r1"}',
            receivedAt: '2026-10-06T09:00:00Z', reason: { __typename: 'SyncReasonInfo', key: 'sync.versionMismatch', args: [] },
            vehicle: { __typename: 'SyncVehicleRef', id: 'v1', name: 'Octavia', distanceUnit: 'KILOMETERS', volumeUnit: 'LITERS' },
            submittedBy: { __typename: 'UserRef', id: 'u2', displayName: 'Bob' }, canResolve: true, base: '{"volume":40}',
            current: {
              __typename: 'SyncCurrent', state: 'LIVE', version: 2, lastChange: 'EDITED', changedAt: '2026-10-06T08:00:00Z', changedBy: { __typename: 'UserRef', id: 'u3', displayName: 'Anna' },
              vehicle: null, expense: null, schedule: null,
              refueling: { __typename: 'Refueling', version: 2, date: '2026-09-01', volume: 41, totalCost: 60, discount: null, currency: 'EUR', odometer: 1000, isFullTank: true, missedPreviousFillUp: false, note: null },
            },
          }],
        },
      }),
    ),
  )
  const region = await screen.findByRole('region', { name: 'Not applied' })
  await check(document.body)

  expect(within(region).getByRole('link', { name: /^Open it as it is now: Refuelling to the trash/ })).toBeInTheDocument()
  for (const [button, title] of [[/^Move it to the trash anyway: Refuelling to the trash/, 'Move it to the trash anyway?'], [/^Keep it: Refuelling to the trash/, 'Keep it?']] as const) {
    await ui.click(await within(region).findByRole('button', { name: button }))
    const dialog = await screen.findByRole('alertdialog', { name: title })
    expect(dialog).toHaveAccessibleDescription(/./)
    await check(document.body)
    await ui.click(within(dialog).getByRole('button', { name: 'Cancel' }))
  }
})

it('the signed-in devices and the confirmation to sign out the others are free of violations', async () => {
  const { view, ui } = setup('/account')
  const at = (id: string, client: string | null, current: boolean) => ({
    id, client, current, createdAt: '2026-09-01T08:00:00Z', lastUsedAt: '2026-10-01T08:00:00Z', expiresAt: '2026-12-30T08:00:00Z',
  })
  server.use(graphql.query('MySessions', () => HttpResponse.json({ data: { mySessions: [at('s1', 'Firefox on Linux', true), at('s2', null, false)] } })))
  const devices = await screen.findByRole('region', { name: 'Signed-in devices' })
  await within(devices).findByText('Unknown device')
  await check(view.container)

  await ui.click(within(devices).getByRole('button', { name: 'Sign out everywhere else' }))
  await screen.findByRole('alertdialog', { name: 'Sign out everywhere else?' })
  await check(document.body)
})

it("the Offline data section, a vehicle's window dialog and the timespan fields are free of violations", async () => {
  const { view, ui } = setup('/account')
  server.use(
    graphql.query('OfflineVehicles', () => HttpResponse.json({ data: { myVehicles: [{ __typename: 'Vehicle', id: 'v1', name: 'Octavia' }] } })),
    graphql.query('OfflineEstimates', () =>
      HttpResponse.json({ data: { myVehicles: [{ __typename: 'Vehicle', id: 'v1', logCountSince: { __typename: 'LogCountSince', refuelings: 10, expenses: 2 } }] } }),
    ),
  )
  const offline = await screen.findByRole('region', { name: 'Offline data' })
  await within(offline).findByText('≈ 12')
  await check(view.container)

  await ui.click(within(offline).getByRole('button', { name: 'Change the window of Octavia' }))
  const dialog = await screen.findByRole('dialog', { name: 'Window of Octavia' })
  await check(document.body)

  await ui.click(within(dialog).getByRole('combobox', { name: 'What to download' }))
  await ui.click(screen.getByRole('option', { name: 'Since a date' }))
  await within(dialog).findByRole('group', { name: 'Since' }) // the date field (its picker loads lazily)
  await check(document.body)
})

it('offline, the top bar, the footer and a page that needs the server are free of violations', async () => {
  const { view, ui } = setup('/')
  await screen.findByRole('heading', { level: 1 })
  await answered()
  connectivity.failed()

  await screen.findByText('Offline')
  await ui.click(screen.getByRole('link', { name: 'Trash' }))
  await screen.findAllByText(/This needs a connection to the server/)
  await check(view.container)
})

it('the warning before a sign-out that deletes changes not sent is a labelled, described alert dialog free of violations', async () => {
  const { ui } = setup('/')
  await screen.findByRole('heading', { level: 1 })
  await deviceData.settled()
  await outbox.enqueue({ id: 't1', entity: 'refuelings', action: 'trash', vehicleId: 'v1', targetId: 'r1', expectedVersion: 1 })

  await ui.click(screen.getByRole('button', { name: 'Sign out' }))

  const dialog = await screen.findByRole('alertdialog', { name: 'Sign out and delete what was not sent?' })
  expect(dialog).toHaveAccessibleDescription(/^1 change made on this device has not reached the server yet\./)
  await check(dialog)
})

it('the notifications page and the open bell are labelled and free of violations', async () => {
  const { view, ui } = setup('/notifications')
  await screen.findByRole('list', { name: 'Notifications' })
  await check(view.container)

  await ui.click(screen.getByRole('button', { name: 'Notifications, 2 unread' }))
  await screen.findByRole('list', { name: 'Latest notifications' })
  await check(document.body)
})

it('the appearance and language menus are labelled radio menus, free of violations', async () => {
  const { ui } = setup('/')
  await screen.findByText('Octavia')

  await ui.click(screen.getByRole('button', { name: 'Appearance' }))
  const modes = await screen.findByRole('menu', { name: 'Appearance' })
  // Two sets of radios, split by a separator; each item's name says its set.
  expect(within(modes).getAllByRole('menuitemradio').map((item) => [item.getAttribute('aria-label'), item.getAttribute('aria-checked')])).toEqual([
    ['Colour mode: Light', 'false'],
    ['Colour mode: Dark', 'true'],
    ['Colour mode: System', 'false'],
    ['Surfaces: Glossy', 'true'],
    ['Surfaces: Transparent', 'false'],
    ['Surfaces: Opaque', 'false'],
  ])
  expect(within(modes).getByRole('separator')).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Appearance', hidden: true })).toHaveAttribute('aria-controls', modes.id)
  await check(modes) // the menu itself: a transient popup outside the page's landmarks, as every popup menu is
  await ui.keyboard('{Escape}')

  await ui.click(screen.getByRole('button', { name: 'Language and formats' }))
  const languages = await screen.findByRole('menu', { name: 'Language and formats' })
  // A language's own name, in its own language as a whole: no "Language:" before it to be read in the Hungarian voice.
  const hungarian = within(languages).getByRole('menuitemradio', { name: 'Magyar' })
  expect(hungarian).toHaveAttribute('lang', 'hu')
  expect(hungarian).not.toHaveAttribute('aria-label')
  await check(languages)
})

it('the floating add button of a phone and its menu are labelled and free of violations', async () => {
  const { view, ui } = setup('/vehicles/v1?tab=refuelings', 'phone')
  await screen.findByText(/Sep 1, 2026/)
  const fab = screen.getByRole('button', { name: 'Add a refuelling or an expense' })
  await check(view.container)

  await ui.click(fab)
  const menu = await screen.findByRole('menu', { name: 'Add a refuelling or an expense' })
  expect(fab).toHaveAttribute('aria-controls', menu.id)
  await check(menu)
})

it('the message that offers Undo after trashing is a status and free of violations', async () => {
  const { ui } = setup('/vehicles/v1?tab=refuelings')
  await screen.findByText(/Sep 1, 2026/)

  await ui.click(screen.getByRole('button', { name: 'Delete the refuelling of Sep 1, 2026' }))
  await ui.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Move to trash' }))

  const undo = await screen.findByRole('button', { name: 'Undo' })
  const message = undo.closest('[role="status"]') as HTMLElement
  expect(message).toHaveTextContent('is in the trash.')
  await check(message)
})

it('sortable columns announce their state and every grid is labelled, its rows named by their first cell', async () => {
  setup('/vehicles/v1?tab=refuelings')
  await screen.findByText(/Sep 1, 2026/)

  const grid = screen.getByRole('grid', { name: 'Refuelings' })
  expect(within(grid).getAllByRole('rowheader').map((cell) => cell.textContent)).toEqual(['Sep 1, 2026', 'Aug 1, 2026'])
  expect(screen.getByRole('columnheader', { name: /Date/ })).toHaveAttribute('aria-sort', 'descending')
  expect(screen.getByRole('columnheader', { name: /Odometer/ })).toHaveAttribute('aria-sort', 'none')
})

it('the details tab with its trash button and confirmation is free of violations', async () => {
  const { ui } = setup('/vehicles/v1?tab=details')
  await ui.click(await screen.findByRole('button', { name: 'Move to trash' }))
  const dialog = await screen.findByRole('alertdialog', { name: 'Move Octavia to the trash?' })
  expect(dialog).toHaveAccessibleDescription(/restore it from the trash/)

  await check(document.body)
})

it('the user administration dialogs are labelled, described and free of violations', async () => {
  const { ui } = setup('/admin/people')
  const table = await screen.findByRole('table')
  await within(table).findByText('bob@example.com')

  for (const [button, dialogName] of [
    ['Edit Bob', 'Edit Bob'],
    ['Set password for Bob', 'Set password for Bob'],
    ['Delete Bob', 'Delete Bob?'],
  ] as const) {
    await ui.click(within(table).getByRole('button', { name: button }))
    const dialog = await screen.findByRole('dialog', { name: dialogName })
    expect(dialog).toHaveAccessibleDescription()
    await check(document.body)
    await ui.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  }
})

it('the system settings page and its confirmation are labelled, described and free of violations', async () => {
  const { view, ui } = setup('/admin/settings')
  const page = within(await screen.findByRole('region', { name: 'Server settings' }))
  await page.findByText('/data/settings.json')
  await check(view.container) // the forms loaded, before a dialog hides them

  await ui.click(page.getByRole('button', { name: 'Restart server' }))
  const restart = await screen.findByRole('alertdialog', { name: 'Restart the server?' })
  expect(restart).toHaveAccessibleDescription()
  await check(document.body)
  await ui.click(within(restart).getByRole('button', { name: 'Cancel' }))
  await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument())
})

it('the backups page, its restore and its key set dialogs are labelled, described and free of violations', async () => {
  const { ui } = setup('/admin/backups')
  const run = { __typename: 'BackupRunInfo', running: false, startedAt: '2026-10-11T08:00:00Z', finishedAt: '2026-10-11T08:00:05Z', file: 'tankstat-backup-20261011T080000Z.zip', bytes: 3_100_000, error: null }
  const point = {
    __typename: 'RestorePointInfo', file: 'tankstat-backup-20261011T080000Z.zip', bytes: 3_100_000, createdAt: '2026-10-11T08:00:00Z', appVersion: '0.13.0', provider: 'Sqlite',
    migration: 'AddInstance', sameMigration: true, olderMigration: false, safetyNet: false, uploaded: false, encrypted: true, recipient: 'age1abc', currentRecipient: true, photos: 'SEPARATE', keys: true, rows: 120,
    settingsFile: { __typename: 'BackupSettingsFileInfo', state: 'INCLUDED', secrets: true, secretsLeftOut: [] },
    environment: { __typename: 'BackupEnvironmentInfo', mode: 'RECORD', settings: ['Auth__Mode', 'Smtp__Host'], missing: ['Smtp__Host'], missingCertain: true, logLevelsMissing: [], overridden: [] },
  }
  server.use(
    graphql.query('Backups', () =>
      HttpResponse.json({
        data: {
          backups: {
            __typename: 'BackupOverviewInfo', folder: '/data/backups', migration: 'AddInstance', run,
            settings: { __typename: 'BackupSettingsInfo', recipient: null, photosApart: false, photos: false, maxUploadMb: 1024, times: ['02:00'], days: ['MONDAY', 'TUESDAY', 'WEDNESDAY', 'THURSDAY', 'FRIDAY', 'SATURDAY', 'SUNDAY'], timeZone: 'UTC', nextRun: '2026-10-12T02:00:00Z', keep: 14, keepDays: null, environment: 'RECORD', secrets: false, passphraseMinLength: 12 },
            sizes: { __typename: 'StorageSizesInfo', database: 5_300_000, pictures: 12_000_000, photos: null },
            restorePoints: [point, { ...point, file: 'uploaded-old.zip', sameMigration: false, uploaded: true, encrypted: false, recipient: null, currentRecipient: false }],
          },
        },
      }),
    ),
    graphql.mutation('PrepareBackupEncryption', () =>
      HttpResponse.json({ data: { prepareBackupEncryption: { __typename: 'BackupKeySetInfo', passphrase: 'K7QM-4XWD-PL2N-ZR8A-HJ3T', generated: true, identity: 'AGE-SECRET-KEY-1QQQ', recipient: 'age1abc', salt: 'c2FsdA==' } } }),
    ),
  )
  await screen.findByRole('table')
  await check(document.body)

  await ui.click(screen.getByRole('button', { name: /^Restore the backup of/ }))
  const restore = await screen.findByRole('alertdialog')
  expect(restore).toHaveAccessibleName(/^Restore the backup of/)
  expect(restore).toHaveAccessibleDescription(/All data on this server is replaced/)
  await check(restore)
  await ui.click(within(restore).getByRole('button', { name: 'Cancel' }))
  await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument())

  await ui.click(screen.getByRole('button', { name: 'Make a key set…' }))
  const keys = await screen.findByRole('dialog', { name: 'A key set for encrypted backups' })
  expect(keys).toHaveAccessibleDescription()
  await ui.click(within(keys).getByRole('button', { name: 'Make the keys' }))
  await within(keys).findByText('K7QM-4XWD-PL2N-ZR8A-HJ3T')
  await check(keys)
})

it('a held backup in its row, and a backup opened, are labelled and free of violations', async () => {
  const { ui } = setup('/admin/backups')
  const point = {
    __typename: 'RestorePointInfo', file: 'tankstat-backup-20261011T080000Z.zip', bytes: 3_100_000, createdAt: '2026-10-11T08:00:00Z', appVersion: '0.13.0', provider: 'Sqlite',
    migration: 'AddInstance', sameMigration: true, olderMigration: false, safetyNet: false, uploaded: false, encrypted: false, recipient: null, currentRecipient: false, photos: 'SEPARATE', keys: false, rows: 120,
    held: true, heldSince: '2026-10-12T08:00:00Z',
    settingsFile: { __typename: 'BackupSettingsFileInfo', state: 'INCLUDED', secrets: true, secretsLeftOut: [] },
    environment: { __typename: 'BackupEnvironmentInfo', mode: 'RECORD', settings: [], missing: [], missingCertain: true, logLevelsMissing: [], overridden: [] },
  }
  server.use(
    graphql.query('Backups', () =>
      HttpResponse.json({
        data: {
          backups: {
            __typename: 'BackupOverviewInfo', folder: '/data/backups', migration: 'AddInstance',
            run: { __typename: 'BackupRunInfo', running: false, startedAt: null, finishedAt: null, file: null, bytes: null, error: null },
            settings: { __typename: 'BackupSettingsInfo', recipient: null, photosApart: false, photos: false, maxUploadMb: 1024, times: [], days: [], timeZone: 'UTC', nextRun: null, keep: null, keepDays: null, environment: 'RECORD', secrets: true, passphraseMinLength: 12 },
            sizes: { __typename: 'StorageSizesInfo', database: 5_300_000, pictures: 12_000_000, photos: null },
            restorePoints: [point],
          },
        },
      }),
    ),
    graphql.query('BackupDetails', () =>
      HttpResponse.json({
        data: {
          backupDetails: {
            __typename: 'BackupDetailsInfo', file: point.file, bytes: point.bytes, held: true, heldSince: point.heldSince, format: 1, writtenBy: 'tankstat', createdAt: point.createdAt,
            appVersion: '0.13.0', provider: 'Sqlite', migration: 'AddInstance', safetyNet: false, uploaded: false, encrypted: false, recipient: null, currentRecipient: false,
            encryptionMethod: null, pictures: true, photos: 'SEPARATE', keys: false, tables: [{ __typename: 'BackupTableInfo', name: 'Refuelings', rows: 120 }],
            sizes: { __typename: 'BackupSizesInfo', tables: 1000, pictures: 2000, photos: 3000, keys: 0, archive: 4000 }, archiveEntry: 'data.zip', sha256: 'ab'.repeat(32),
            settingsFile: { __typename: 'BackupSettingsFileInfo', state: 'INCLUDED', bytes: 300, secrets: true, secretsLeftOut: [], machineLeftOut: ['Database:ConnectionString'] },
            environment: { __typename: 'BackupEnvironmentInfo', mode: 'MERGE', settings: ['Auth__Mode'], server: ['Database__Provider'] },
          },
        },
      }),
    ),
  )
  await screen.findByRole('table')
  expect(screen.getByRole('img', { name: /^Held since/ })).toBeInTheDocument()
  expect(screen.getByRole('img', { name: /^In plain with its secrets/ })).toBeInTheDocument()
  await check(document.body)

  await ui.click(screen.getByRole('button', { name: /^Open the backup of/ }))
  const opened = await screen.findByRole('dialog', { name: /^The backup of/ })
  expect(opened).toHaveAccessibleDescription(/nothing in it is decrypted or unpacked/)
  await within(opened).findByRole('table')
  await check(opened)
})

it('the Arrange dialog is labelled, described and free of violations', async () => {
  stubViewport('desktop')
  const vehicles = fakeVehicleBackend([fakeVehicle({ id: 'a', name: 'Alpha' }), fakeVehicle({ id: 'b', name: 'Beta' })])
  server.use(sessionHandler('NONE', () => null), healthHandler, ...vehicles.handlers, ...fakeLogBackend(fakeVehicle({ id: 'a', name: 'Alpha' }), []).handlers)
  const ui = userEvent.setup()
  renderWithApollo(<App />, '/')
  await screen.findByRole('link', { name: 'Open Alpha' })

  await ui.click(screen.getByRole('button', { name: 'Arrange' }))
  const dialog = await screen.findByRole('dialog', { name: 'Arrange vehicles' })
  await within(dialog).findByRole('button', { name: 'Move Beta up' })

  expect(dialog).toHaveAccessibleDescription(/order/)
  await check(document.body)
})

it('the sign-in screens of every mode have no violations', async () => {
  const replace = vi.spyOn(navigation, 'replace').mockImplementation(() => undefined)
  onTestFinished(() => replace.mockRestore())

  // OIDC: on the way to the provider.
  server.use(sessionHandler('OIDC', () => null), healthHandler)
  let view = renderWithApollo(<App />, '/')
  await screen.findByText('Taking you to your identity provider…')
  await check(view.container)
  view.unmount()

  // OIDC: signed out on purpose.
  window.sessionStorage.setItem(SIGNED_OUT_KEY, '1')
  view = renderWithApollo(<App />, '/')
  await screen.findByRole('heading', { name: 'You have signed out.' })
  await check(view.container)
  view.unmount()
  window.sessionStorage.clear()

  // OIDC: the sign-in failed.
  view = renderWithApollo(<App />, '/?signIn=failed&reason=access_denied')
  await screen.findByRole('alert')
  await check(view.container)
  view.unmount()

  // Standalone: the password form.
  server.use(sessionHandler('STANDALONE', () => null), healthHandler)
  view = renderWithApollo(<App />, '/')
  await screen.findByLabelText(/Password/)
  await check(view.container)
  view.unmount()

  // Standalone, signed in with a password shorter than the minimum now: the change in place of every page.
  server.use(sessionHandler('STANDALONE', () => user({ passwordChangeRequired: true })), healthHandler)
  view = renderWithApollo(<App />, '/')
  await screen.findByRole('heading', { name: 'Choose a new password' })
  await check(view.container)
  view.unmount()

  // Behind a proxy: only an explanation.
  server.use(sessionHandler('PROXY_HEADER', () => null), healthHandler)
  view = renderWithApollo(<App />, '/')
  await screen.findByText(/reverse proxy/i)
  await check(view.container)
})

it('the setup page, its code and its wizard, are labelled and free of violations', async () => {
  const ui = userEvent.setup()
  const pending = { code: 'SETUP_PENDING', severity: 'INFO' as const, message: 'This instance is not set up yet.' }
  server.use(
    sessionHandler('STANDALONE', () => null, [pending]),
    healthHandler,
    graphql.query('Setup', () => HttpResponse.json({ data: { setup: { __typename: 'SetupInfo', pending: true } } })),
    graphql.query('SystemSettings', () => HttpResponse.json({ data: { systemSettings: wizardSettings('STANDALONE') } })),
    graphql.query('SetupRestorePoints', () =>
      HttpResponse.json({
        data: {
          setupRestorePoints: [
            {
              __typename: 'RestorePointInfo',
              file: 'tankstat-backup-20261010T020000Z.zip',
              createdAt: '2026-10-10T02:00:00Z',
              appVersion: '0.15.0',
              sameMigration: true,
              olderMigration: false,
              encrypted: true,
              held: false,
              heldSince: null,
              settingsFile: null,
              environment: null,
            },
          ],
          setupUploadLimitMb: 1024,
        },
      }),
    ),
  )
  const view = renderWithApollo(<App />, '/')

  await ui.type(await screen.findByRole('textbox', { name: /^Setup code/ }), 'ABCD-EFGH-JKLM')
  await check(view.container)
  await ui.click(screen.getByRole('button', { name: 'Continue' }))
  await ui.click(await screen.findByRole('button', { name: 'Start the wizard' }))
  await screen.findByText('E-mail', { selector: '[aria-current="step"]' }) // the stepper, with a settings step: the settings page's own form
  await check(view.container)
  await ui.click(screen.getByRole('button', { name: 'Start' }))
  await ui.click(await screen.findByRole('button', { name: 'Restore from a backup' }))
  await screen.findByRole('region', { name: 'Restore a backup' })
  await check(view.container)
})

it("the setup's done screen, with its key set and next steps, is labelled and free of violations", async () => {
  server.use(
    sessionHandler('STANDALONE', () => user({ isAdmin: true })),
    healthHandler,
    graphql.query('Setup', () => HttpResponse.json({ data: { setup: { __typename: 'SetupInfo', pending: false } } })),
    backupRecipientHandler(),
  )
  const view = renderWithApollo(<App />, '/setup')

  await screen.findByRole('region', { name: 'Encrypted backups' })
  await check(view.container)
})

it('the install how-to for iPhones is a labelled dialog without violations', async () => {
  vi.spyOn(platform, 'isIos').mockReturnValue(true)
  onTestFinished(() => {
    vi.restoreAllMocks()
  })
  const { ui } = setup('/', 'phone')
  await screen.findByText('Octavia')

  await ui.click(screen.getByRole('button', { name: 'Show menu' }))
  await ui.click(await screen.findByRole('button', { name: 'Install app' }))

  const dialog = await screen.findByRole('dialog', { name: 'Install Tankstat' })
  expect(dialog).toHaveAccessibleDescription(/Home Screen/)
  await check(document.body)
})
