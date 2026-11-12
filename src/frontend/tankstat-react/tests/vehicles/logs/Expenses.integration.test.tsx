import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse } from 'msw'
import { graphql } from '../../support/graphql.ts'
import { afterAll, afterEach, beforeAll, expect, it, vi } from 'vitest'
import App from '../../../src/App.tsx'
import { server } from '../../support/server.ts'
import { fakeExpense, fakeExpenseBackend, fakeVehicle, gqlError, healthHandler, renderWithApollo, sessionHandler, silenceConsoleError, stubViewport } from '../../support/mocks.tsx'
import { UUID } from '../../support/ids.ts'

beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => {
  server.resetHandlers()
  vi.unstubAllGlobals()
})
afterAll(() => server.close())

const expenses = [
  fakeExpense({ id: 'e1', date: '2026-08-01', title: 'Parking', category: 'Parking', amount: 1500, odometer: null }),
  fakeExpense({ id: 'e2', date: '2026-09-01', title: 'Oil change', category: 'Service', amount: 35000, odometer: 12000, note: 'with filters' }),
]

/** The trash goes in before the page renders: the page asks for it at once, and an answer before it is there shows an empty trash. */
function setup(vehicle = fakeVehicle(), initial = expenses, route = '/vehicles/v1?tab=expenses', trash: ReturnType<typeof fakeExpense>[] = []) {
  stubViewport('desktop')
  const backend = fakeExpenseBackend(vehicle, initial)
  backend.state.trash = trash
  server.use(sessionHandler('NONE', () => null), healthHandler, ...backend.handlers)
  renderWithApollo(<App />, route)
  return { ...backend, ui: userEvent.setup() }
}

it('lists the expenses with their amounts in the currency they were paid in, newest first', async () => {
  const { state } = setup()

  const row = (await screen.findByText('Oil change')).closest<HTMLElement>('[role="row"]')!
  expect(within(row).getByText(/35,000/)).toBeInTheDocument()
  expect(within(row).getByText('Service')).toBeInTheDocument()
  expect(within(row).getByText(/12,000 km/)).toBeInTheDocument()
  expect(screen.getAllByRole('rowheader').map((c) => c.textContent)).toEqual(['Sep 1, 2026', 'Aug 1, 2026'])
  expect(state.requests.at(-1)).toMatchObject({ vehicleId: 'v1', orderBy: 'DATE', direction: 'DESC', withAmount: true })
})

it('an expense logged for a service visit names the recurring expenses it covered, in a column shown on request', async () => {
  const { ui, state } = setup(fakeVehicle(), [fakeExpense({ id: 'e3', title: 'Yearly service', schedules: [{ id: 'rc1', title: 'Oil change' }, { id: 'rc3', title: 'Oil filter' }] })])
  await screen.findByText('Yearly service')
  expect(screen.queryByText('Oil change, Oil filter')).not.toBeInTheDocument()
  expect(screen.queryByRole('columnheader', { name: 'Recurring' })).not.toBeInTheDocument()
  expect(state.requests.at(-1)).toMatchObject({ withSchedules: false }) // hidden by default, so never asked for

  await ui.click(screen.getByRole('button', { name: 'Columns' }))
  await ui.click(within(await screen.findByRole('dialog')).getByRole('checkbox', { name: 'Show Recurring' }))
  await ui.keyboard('{Escape}') // the popover is modal: the grid is out of reach until it closes

  expect(await screen.findByRole('columnheader', { name: 'Recurring' })).toBeInTheDocument()
  expect(await screen.findByText('Oil change, Oil filter')).toBeInTheDocument()
  expect(state.requests.at(-1)).toMatchObject({ withSchedules: true })
})

it('shows a dash where the odometer was not noted', async () => {
  setup()

  const row = (await screen.findAllByText('Parking'))[0].closest<HTMLElement>('[role="row"]')! // the title and the category are both "Parking"

  expect(within(row).getAllByText('–').length).toBeGreaterThan(0)
})

it('adds an expense with the usual currency, an optional odometer and a category', async () => {
  const { ui, state } = setup()
  await screen.findByText('Oil change')

  await ui.click(screen.getByRole('button', { name: 'Add expense' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add expense' })
  await waitFor(() => expect(within(dialog).getByLabelText('Currency')).toHaveValue('HUF'))
  await within(dialog).findByText(/Used before: Parking, Service/)
  await ui.type(within(dialog).getByLabelText('Title'), 'Tyres')
  await ui.type(within(dialog).getByLabelText(/^Category/), 'Maintenance')
  await ui.type(within(dialog).getByLabelText('Amount'), '120000')
  await ui.type(within(dialog).getByLabelText('Discount'), '6000')
  await ui.click(within(dialog).getByRole('button', { name: 'Add expense' }))

  await screen.findByRole('gridcell', { name: 'Tyres' }) // the expense's title: "Tyres" is a tab's name too (#287)
  expect(state.calls.AddExpense).toEqual([
    { input: expect.objectContaining({ vehicleId: 'v1', title: 'Tyres', category: 'Maintenance', amount: 120000, discount: 6000, currency: 'HUF', odometer: null, note: null }) },
  ])
  expect(await screen.findByText('Saved.')).toBeInTheDocument() // a short message once the dialog is gone
})

it('blocks a missing title or amount without calling the server', async () => {
  const { ui, state } = setup()
  await screen.findByText('Oil change')

  await ui.click(screen.getByRole('button', { name: 'Add expense' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add expense' })
  await ui.click(within(dialog).getByRole('button', { name: 'Add expense' }))

  expect(await within(dialog).findByText('Title is required')).toBeInTheDocument()
  expect(within(dialog).getByText('Amount is required')).toBeInTheDocument()
  expect(state.calls.AddExpense).toBeUndefined()
})

it('shows the odometer rule from the server and keeps the dialog open', async () => {
  const { ui, state } = setup()
  state.failWith = { message: 'too low', key: 'odometer.belowPrevious', args: { previous: '12,000 km', date: '2026-09-01' } }
  await screen.findByText('Oil change')

  await ui.click(screen.getByRole('button', { name: 'Add expense' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add expense' })
  await ui.type(within(dialog).getByLabelText('Title'), 'Wash')
  await ui.type(within(dialog).getByLabelText('Amount'), '1000')
  await ui.type(within(dialog).getByLabelText(/^Odometer/), '100')
  await ui.click(within(dialog).getByRole('button', { name: 'Add expense' }))

  expect(await within(dialog).findByRole('alert')).toHaveTextContent('The odometer cannot be lower than 12,000 km')
  expect(screen.getByRole('dialog')).toBeInTheDocument()
})

it('a new expense carries an id chosen in the browser: the same for every Save of one opening, a new one the next time', async () => {
  const { ui, state } = setup()
  state.failWith = { message: 'too low', key: 'odometer.belowPrevious', args: { previous: '12,000 km', date: '2026-09-01' } }
  await screen.findByText('Oil change')
  const add = async () => {
    await ui.click(screen.getByRole('button', { name: 'Add expense' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add expense' })
    await ui.type(within(dialog).getByLabelText('Title'), 'Wash')
    await ui.type(within(dialog).getByLabelText('Amount'), '1000')
    return dialog
  }

  const dialog = await add()
  await ui.click(within(dialog).getByRole('button', { name: 'Add expense' }))
  await within(dialog).findByRole('alert')
  state.failWith = undefined
  await ui.click(within(dialog).getByRole('button', { name: 'Add expense' })) // tried again: it must not become a second expense
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  await ui.click(within(await add()).getByRole('button', { name: 'Add expense' }))
  await waitFor(() => expect(state.calls.AddExpense).toHaveLength(3))

  const ids = (state.calls.AddExpense as { input: { id: string } }[]).map((c) => c.input.id)
  expect(ids[0]).toMatch(UUID)
  expect(ids[1]).toBe(ids[0])
  expect(ids[2]).not.toBe(ids[0])
})

it('edits an expense starting from its real values, and the odometer can be cleared', async () => {
  const { ui, state } = setup()
  await screen.findByText('Oil change')

  await ui.click(screen.getByRole('button', { name: 'Edit the expense Oil change' }))
  const dialog = await screen.findByRole('dialog', { name: 'Edit expense' })
  await waitFor(() => expect(within(dialog).getByLabelText('Title')).toHaveValue('Oil change'))
  expect(within(dialog).getByLabelText('Amount')).toHaveValue('35000')
  expect(within(dialog).getByLabelText(/^Odometer/)).toHaveValue('12000')
  await ui.clear(within(dialog).getByLabelText(/^Odometer/))
  await ui.click(within(dialog).getByRole('button', { name: 'Save changes' }))

  await waitFor(() => expect(state.calls.UpdateExpense).toEqual([{ input: expect.objectContaining({ id: 'e2', title: 'Oil change', amount: 35000, odometer: null, note: 'with filters' }) }]))
})

it('keeps a discount with the amount paid: shown in its column on request, back in the edit dialog, cleared when emptied', async () => {
  const { ui, state } = setup(fakeVehicle(), [fakeExpense({ id: 'e4', title: 'Tyres', amount: 18800, discount: 1200, currency: 'HUF' })])
  await screen.findByRole('gridcell', { name: 'Tyres' }) // the expense's row, not the vehicle's Tyres tab of the same name
  expect(state.requests.at(-1)).toMatchObject({ withDiscount: false }) // hidden by default, so never asked for

  await ui.click(screen.getByRole('button', { name: 'Columns' }))
  await ui.click(within(await screen.findByRole('dialog')).getByRole('checkbox', { name: 'Show Discount' }))
  await ui.keyboard('{Escape}')
  expect(await screen.findByRole('columnheader', { name: 'Discount' })).toBeInTheDocument()
  expect(await screen.findByText(/1,200/)).toBeInTheDocument()

  await ui.click(screen.getByRole('button', { name: 'Edit the expense Tyres' }))
  const dialog = await screen.findByRole('dialog', { name: 'Edit expense' })
  await waitFor(() => expect(within(dialog).getByLabelText('Discount')).toHaveValue('1200'))
  expect(within(dialog).getByLabelText('Amount')).toHaveValue('18800') // what was paid
  await ui.clear(within(dialog).getByLabelText('Discount'))
  await ui.click(within(dialog).getByRole('button', { name: 'Save changes' }))

  await waitFor(() => expect(state.calls.UpdateExpense).toEqual([{ input: expect.objectContaining({ id: 'e4', amount: 18800, discount: 0 }) }])) // 0 clears; left out would keep it
})

it('moves an expense to the trash after a confirmation, not before', async () => {
  const { ui, state } = setup()
  await screen.findByText('Oil change')

  await ui.click(screen.getByRole('button', { name: 'Delete the expense Oil change' }))
  expect(state.calls.DeleteExpense).toBeUndefined()
  await ui.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Move to trash' }))

  await waitFor(() => expect(screen.queryByText('Oil change')).not.toBeInTheDocument())
  expect(state.calls.DeleteExpense).toEqual([{ id: 'e2' }])

  // A message says so, and Undo brings it back.
  expect(screen.getByText('Oil change is in the trash.')).toBeInTheDocument()
  await ui.click(screen.getByRole('button', { name: 'Undo' }))
  expect(await screen.findByText('Oil change')).toBeInTheDocument()
  expect(state.calls.RestoreExpense).toEqual([{ id: 'e2' }])
})

it('a move to the trash the server refuses says why, and the expense stays listed', async () => {
  silenceConsoleError()
  const { ui } = setup()
  server.use(graphql.mutation('DeleteExpense', () => HttpResponse.json(gqlError('Already gone', 'VALIDATION_FAILED', 'expense.alreadyTrashed'))))
  await screen.findByText('Oil change')

  await ui.click(screen.getByRole('button', { name: 'Delete the expense Oil change' }))
  await ui.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Move to trash' }))

  expect(await screen.findByRole('alert')).toHaveTextContent('This expense is already in the trash.')
  expect(screen.getByText('Oil change')).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Undo' })).not.toBeInTheDocument()
})

it('an expense whose creator is no longer known shows a dash for who logged it, the amount it waits for too', async () => {
  setup(fakeVehicle(), [fakeExpense({ id: 'e3', title: 'Car wash', category: 'Care', amount: null, currency: null, odometer: 12000, createdBy: null as never, reviewState: 'AWAITING_PHOTOS' })])

  const row = (await screen.findByText('Car wash')).closest<HTMLElement>('[role="row"]')!
  const byColumn = Object.fromEntries(
    within(row)
      .getAllByRole('gridcell')
      .map((cell) => [cell.getAttribute('data-field'), cell.textContent]),
  )
  expect(byColumn).toMatchObject({ amount: '–', createdBy: '–', category: 'Care' })
})

it('view-only access: no add button and no edit or delete', async () => {
  setup(fakeVehicle({ canEdit: false, logAccess: 'VIEW' }), expenses.map((e) => ({ ...e, canEdit: false })))

  await screen.findByText('Oil change')

  expect(screen.queryByRole('button', { name: 'Add expense' })).not.toBeInTheDocument()
  expect(screen.getAllByText('view only')).toHaveLength(2)
})

it('the trash has an expenses tab: restore and empty', async () => {
  const { ui, state } = setup(fakeVehicle(), [], '/trash?tab=expenses', [
    fakeExpense({ id: 'x1', title: 'Old fee', deletedAt: '2026-10-01T08:00:00Z' }),
    fakeExpense({ id: 'x2', title: 'Older fee', deletedAt: '2026-10-01T08:00:00Z' }),
  ])
  await screen.findByText('Old fee')

  await ui.click(screen.getByRole('button', { name: 'Restore Old fee' }))
  await waitFor(() => expect(state.calls.RestoreExpense).toEqual([{ id: 'x1' }]))
  await waitFor(() => expect(screen.queryByText('Old fee')).not.toBeInTheDocument())

  await ui.click(screen.getByRole('button', { name: 'Empty trash' }))
  await ui.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Empty trash' }))

  await screen.findByText('Permanently deleted 1 expense.')
  expect(await screen.findByText('No expenses in the trash.')).toBeInTheDocument()
})
