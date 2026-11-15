import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterAll, afterEach, beforeAll, expect, it } from 'vitest'
import App from '../../src/App.tsx'
import { createApolloClient } from '../../src/apolloClient.ts'
import { connectivity } from '../../src/offline/connectivity.ts'
import { deviceData } from '../../src/offline/deviceData.ts'
import { memoryStorage } from '../../src/offline/deviceStorage.ts'
import { outbox } from '../../src/offline/outbox.ts'
import { createPullEngine } from '../../src/offline/pull.ts'
import { fakeVehicle, fakeVehicleBackend, healthHandler, renderWithApollo, sessionHandler, stubViewport } from '../support/mocks.tsx'
import { fakeFeed, now } from '../support/offlineFeed.ts'
import { answered, server } from '../support/server.ts'

beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => server.resetHandlers())
afterAll(() => server.close())

/** Octavia (v1) with a refuelling downloaded for offline use; then the server goes out of reach. */
async function downloaded() {
  deviceData.reset(memoryStorage())
  stubViewport('desktop')
  const feed = fakeFeed()
  feed.add('a', '2026-09-01')
  server.use(sessionHandler('NONE', () => null), healthHandler, ...fakeVehicleBackend([fakeVehicle()]).handlers, ...feed.handlers)
  const online = renderWithApollo(<App />, '/')
  await screen.findByText('Octavia')
  await deviceData.settled()
  await createPullEngine({ client: createApolloClient('http://localhost/graphql'), now }).run()
  online.unmount()
  await answered()
  connectivity.failed()
}

async function editOnSyncPage(name: RegExp) {
  renderWithApollo(<App />, '/sync')
  const ui = userEvent.setup()
  await ui.click(await screen.findByRole('button', { name }, { timeout: 10_000 }))
  return { ui, dialog: await screen.findByRole('dialog', { name: 'Edit the change' }) }
}

const saved = 'The change is saved on this device; it is sent when the server can be reached.'

it('Edit, on an expense waiting here, opens the expense dialog with what it carries, and saving keeps the change, edited', async () => {
  await downloaded()
  await outbox.enqueue({
    id: 'x1', entity: 'expenses', action: 'add', vehicleId: 'v1', targetId: 'x1',
    input: { id: 'x1', vehicleId: 'v1', date: '2026-10-05', title: 'Car wash', category: null, amount: 4000, currency: 'EUR', odometer: null, note: null },
  })

  const { ui, dialog } = await editOnSyncPage(/^Edit the change: New expense/)
  await waitFor(() => expect(within(dialog).getByLabelText('Title')).toHaveValue('Car wash'))
  expect(within(dialog).getByLabelText('Amount')).toHaveValue('4000')
  expect(within(dialog).queryByRole('group', { name: 'Photos' })).not.toBeInTheDocument()
  await ui.clear(within(dialog).getByLabelText('Amount'))
  await ui.type(within(dialog).getByLabelText('Amount'), '4500')
  await ui.click(within(dialog).getByRole('button', { name: 'Save the change' }))

  expect(await screen.findByText(saved)).toBeInTheDocument()
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(outbox.changes).toHaveLength(1)
  expect(outbox.changes[0]).toMatchObject({ id: 'x1', action: 'add', input: { id: 'x1', title: 'Car wash', amount: 4500 } }) // still one add
})

it('Edit, on a schedule waiting here, opens the schedule dialog with what it carries, and saving keeps the change, edited', async () => {
  await downloaded()
  await outbox.enqueue({
    id: 's1', entity: 'recurring', action: 'add', vehicleId: 'v1', targetId: 's1',
    input: {
      id: 's1', vehicleId: 'v1', title: 'Oil change', category: 'Service', note: null, kind: 'TIME', intervalMonths: 12, intervalDistance: null,
      lastDoneDate: '2026-10-01', lastDoneOdometer: null, warnDays: 30, warnDistance: 500,
    },
  })

  const { ui, dialog } = await editOnSyncPage(/^Edit the change: New recurring expense/)
  expect(within(dialog).getByLabelText('Title')).toHaveValue('Oil change')
  expect(within(dialog).getByLabelText(/^Every \(months\)/)).toHaveValue('12')
  await ui.clear(within(dialog).getByLabelText(/^Every \(months\)/))
  await ui.type(within(dialog).getByLabelText(/^Every \(months\)/), '6')
  await ui.click(within(dialog).getByRole('button', { name: 'Save the change' }))

  expect(await screen.findByText(saved)).toBeInTheDocument()
  expect(screen.queryByText('Saved.')).not.toBeInTheDocument() // the page says what came of it, not the dialog
  expect(outbox.changes).toHaveLength(1)
  expect(outbox.changes[0]).toMatchObject({ id: 's1', action: 'add', input: { id: 's1', title: 'Oil change', intervalMonths: 6, warnDays: 30 } })
})

it('Edit, on a visit waiting here (#290, decision 71), opens the Done dialog with what it carries, and saving edits that visit in place', async () => {
  await downloaded()
  await outbox.enqueue({
    id: 'm1', entity: 'recurring', action: 'markDone', vehicleId: 'v1', targetId: 'e7', targetIds: ['s-v1'],
    input: { ids: ['s-v1'], date: '2026-10-05', odometer: null, amount: 250, discount: null, currency: 'EUR', title: 'Insurance', category: 'Fees', expenseId: 'e7', photoIds: [] },
  })

  const { ui, dialog } = await editOnSyncPage(/^Edit the change: Marked as done/)
  await waitFor(() => expect(within(dialog).getByRole('checkbox', { name: 'Insurance' })).toBeChecked())
  expect(within(dialog).getByLabelText('Amount (optional)')).toHaveValue('250')
  expect(within(dialog).getByLabelText('Expense title')).toHaveValue('Insurance')
  expect(within(dialog).queryByRole('group', { name: 'Photos' })).not.toBeInTheDocument()
  await ui.clear(within(dialog).getByLabelText('Amount (optional)'))
  await ui.type(within(dialog).getByLabelText('Amount (optional)'), '275')
  await ui.click(within(dialog).getByRole('button', { name: 'Save the change' }))

  expect(await screen.findByText(saved)).toBeInTheDocument()
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(outbox.changes).toHaveLength(1) // still one visit: visits never fold, the edit takes its place
  expect(outbox.changes[0]).toMatchObject({ id: 'm1', action: 'markDone', targetId: 'e7', targetIds: ['s-v1'], input: { ids: ['s-v1'], amount: 275, expenseId: 'e7', photoIds: [] } })
})
