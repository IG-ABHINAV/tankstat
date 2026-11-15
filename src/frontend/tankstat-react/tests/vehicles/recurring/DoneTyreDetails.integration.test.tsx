import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterAll, afterEach, beforeAll, expect, it, vi } from 'vitest'
import App from '../../../src/App.tsx'
import { server } from '../../support/server.ts'
import { fakeExpenseBackend, fakeRecurring, fakeRecurringBackend, fakeVehicle, healthHandler, renderWithApollo, sessionHandler, stubViewport } from '../../support/mocks.tsx'
import { fakeTyres, tyreSummer, tyreWinter } from '../../support/tyres.ts'
import { UUID } from '../../support/ids.ts'

// A visit's tyre details in the Done dialog (#290, the owner's decisions 57, 63 and 64).

beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => {
  server.resetHandlers()
  vi.unstubAllGlobals()
})
afterAll(() => server.close())

const balancing = fakeRecurring({ id: 'rc1', title: 'Wheel balancing', category: 'Tyres', kind: 'TIME', intervalDistance: null, lastDoneOdometer: null })

function setup(schedule = balancing) {
  stubViewport('desktop')
  const recurring = fakeRecurringBackend([schedule])
  const tyres = fakeTyres([tyreSummer, tyreWinter])
  server.use(sessionHandler('NONE', () => null), healthHandler, ...fakeExpenseBackend(fakeVehicle(), []).handlers, ...recurring.handlers, ...tyres.handlers)
  renderWithApollo(<App />, '/vehicles/v1?tab=recurring')
  return { ...recurring, ui: userEvent.setup() }
}

async function doneDialog(ui: ReturnType<typeof userEvent.setup>, title: string) {
  await ui.click(await screen.findByRole('button', { name: `Mark ${title} as done` }))
  const dialog = await screen.findByRole('dialog', { name: `Mark as done: ${title}` })
  await waitFor(() => expect(within(dialog).getByLabelText('Currency')).toHaveValue('HUF'))
  return dialog
}

async function chooseKind(ui: ReturnType<typeof userEvent.setup>, dialog: HTMLElement, kind: string) {
  const group = await within(dialog).findByRole('group', { name: 'Tyre details' })
  await ui.click(within(group).getByRole('combobox', { name: 'What was done' }))
  await ui.click(await screen.findByRole('option', { name: kind }))
  return group
}

it("opens by itself for the schedules' tyre category, keeps their title, and with an amount the visit's expense is the line's", async () => {
  const { ui, state } = setup()
  const dialog = await doneDialog(ui, 'Wheel balancing')
  const group = await chooseKind(ui, dialog, 'Wheel balancing')
  expect(within(dialog).getByLabelText('Expense title')).toHaveValue('Wheel balancing') // the schedules' (decision 64), not "{kind}: {set}"
  await ui.type(within(dialog).getByLabelText('Amount (optional)'), '8000')
  expect(within(group).getByText("The line takes this expense's date and odometer, and its amount is this expense, counted once.")).toBeInTheDocument()
  await ui.click(within(dialog).getByRole('button', { name: 'Mark as done' }))

  await waitFor(() => expect(state.calls.MarkRecurringExpensesDone).toHaveLength(1))
  expect(state.calls.MarkRecurringExpensesDone).toEqual([
    { input: expect.objectContaining({ ids: ['rc1'], amount: 8000, expenseId: expect.stringMatching(UUID), tyre: { kind: 'WHEEL_BALANCING', setId: 's1', lineId: expect.stringMatching(UUID) } }) },
  ])
})

it('without an amount logs the line alone, and says so', async () => {
  const { ui, state } = setup()
  const dialog = await doneDialog(ui, 'Wheel balancing')
  const group = await chooseKind(ui, dialog, 'Storage fee')
  expect(within(group).getByText('Without an amount the line is logged on its own, with no expense.')).toBeInTheDocument()
  await ui.click(within(dialog).getByRole('button', { name: 'Mark as done' }))

  expect(await screen.findByText('Marked as done. Logged on the tyre log, without an amount.')).toBeInTheDocument()
  const [{ input }] = state.calls.MarkRecurringExpensesDone as { input: Record<string, unknown> }[]
  expect(input).toMatchObject({ amount: null, tyre: { kind: 'STORAGE_FEE', setId: 's1', lineId: expect.stringMatching(UUID) } })
  expect(input).not.toHaveProperty('expenseId') // no expense: nothing for an id to name
})

it('another category opens nothing until switched on, and then stops at What was done until one is chosen', async () => {
  const { ui, state } = setup(fakeRecurring({ id: 'rc1', title: 'Oil change', category: 'Service', kind: 'TIME', intervalDistance: null, lastDoneOdometer: null }))
  const dialog = await doneDialog(ui, 'Oil change')
  expect(within(dialog).queryByRole('group', { name: 'Tyre details' })).not.toBeInTheDocument()
  await ui.click(within(dialog).getByRole('switch', { name: 'Tyre details' }))
  await within(dialog).findByRole('group', { name: 'Tyre details' })
  expect(within(dialog).getByLabelText('Expense category (optional)')).toHaveValue('Service') // a category of its own stays
  await ui.click(within(dialog).getByRole('button', { name: 'Mark as done' }))

  expect(await within(dialog).findByText('Choose what was done.')).toBeInTheDocument()
  expect(state.calls.MarkRecurringExpensesDone).toBeUndefined()
})
