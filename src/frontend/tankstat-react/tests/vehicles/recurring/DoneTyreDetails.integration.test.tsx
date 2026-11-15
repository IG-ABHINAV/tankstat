import { act, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterAll, afterEach, beforeAll, expect, it, vi } from 'vitest'
import App from '../../../src/App.tsx'
import { RecurringDoneDialog, type DoneValues } from '../../../src/RecurringDoneDialog.tsx'
import { answered, server } from '../../support/server.ts'
import { connectivity } from '../../../src/offline/connectivity.ts'
import { outbox } from '../../../src/offline/outbox.ts'
import { fakeExpenseBackend, fakeRecurring, fakeRecurringBackend, fakeVehicle, healthHandler, renderWithApollo, sessionHandler, stubViewport } from '../../support/mocks.tsx'
import { brokenUpSummer, fakeTyres, tyreSummer, tyreWinter } from '../../support/tyres.ts'
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

it('kept on the device, its line is on the tyre log at once; a refusal the device can foresee is said in the dialog', async () => {
  const { ui, state } = setup()
  const dialog = await doneDialog(ui, 'Wheel balancing')
  await chooseKind(ui, dialog, 'Puncture repaired')
  await answered()
  act(() => connectivity.failed())
  const group = within(dialog).getByRole('group', { name: 'Tyre details' })
  await ui.click(within(group).getByRole('checkbox', { name: 'The whole set' }))
  await ui.click(within(group).getByRole('button', { name: /front left/ }))
  await ui.clear(within(group).getByLabelText(/^Label, front right/))
  await ui.type(within(group).getByLabelText(/^Label, front right/), 'S1') // the same label twice: the break-up is refused
  await ui.click(within(dialog).getByRole('button', { name: 'Mark as done' }))
  expect(await within(dialog).findByText('Each tyre needs its own label.')).toBeInTheDocument()
  expect(outbox.changes).toEqual([])

  await ui.clear(within(group).getByLabelText(/^Label, front right/))
  await ui.type(within(group).getByLabelText(/^Label, front right/), 'S2')
  await ui.click(within(dialog).getByRole('button', { name: 'Mark as done' }))
  expect(await screen.findByText('Saved on this device · syncs when online.')).toBeInTheDocument()
  expect(state.calls.MarkRecurringExpensesDone).toBeUndefined()
  expect(outbox.changes).toMatchObject([{ entity: 'recurring', action: 'markDone', input: { tyre: { kind: 'PUNCTURE_REPAIRED', setId: 's1' } } }])
  await ui.click(screen.getByRole('tab', { name: 'Tyres' }))
  const log = await screen.findByRole('region', { name: 'Tyre log' })
  expect(await within(log).findByRole('button', { name: /^Puncture repaired, Michelin Primacy 4 · S1,/ })).toBeInTheDocument()
})

it("a visit edited on Waiting to sync shows its tyre details, keeps its own line ids, and switched off sends them as none", async () => {
  stubViewport('desktop')
  server.use(sessionHandler('NONE', () => null), healthHandler, ...fakeExpenseBackend(fakeVehicle(), []).handlers, ...fakeTyres([brokenUpSummer(), tyreWinter]).handlers)
  const sent: DoneValues[] = []
  const tyre = { kind: 'PUNCTURE_REPAIRED' as const, setId: 's1', tyres: [{ tyreId: 't3', id: 'L3' }, { tyreId: 't4', id: 'L4' }] }
  const edit = (initialTyre: typeof tyre) => (
    <RecurringDoneDialog
      open
      vehicle={{ id: 'v1', units: { distance: 'KILOMETERS' } }}
      items={[balancing]}
      selected={[]}
      change={{
        initial: { ids: ['rc1'], date: '2026-10-05', odometer: null, amount: 5000, discount: null, currency: 'HUF', title: 'Wheel balancing', category: 'Tyres', expenseId: 'e5', tyre: initialTyre },
        title: 'Edit the change',
        submitLabel: 'Save the change',
      }}
      onSubmit={async (values) => {
        sent.push(values)
        return undefined
      }}
    />
  )
  const view = renderWithApollo(edit(tyre))
  const ui = userEvent.setup()
  const dialog = await screen.findByRole('dialog', { name: 'Edit the change' })
  expect(within(dialog).getByRole('checkbox', { name: 'Wheel balancing' })).toBeChecked()
  const group = await within(dialog).findByRole('group', { name: 'Tyre details' })
  expect(within(group).getByRole('button', { name: /^Tyre S3/ })).toHaveAttribute('aria-pressed', 'true')
  expect(within(dialog).queryByRole('group', { name: 'Photos' })).not.toBeInTheDocument()
  await ui.click(within(group).getByRole('combobox', { name: 'What was done' }))
  await ui.click(await screen.findByRole('option', { name: 'Valve replaced' }))
  await ui.click(within(dialog).getByRole('button', { name: 'Save the change' }))
  await waitFor(() => expect(sent).toHaveLength(1))
  expect(sent[0]).toMatchObject({ ids: ['rc1'], amount: 5000, expenseId: 'e5', tyre: { ...tyre, kind: 'VALVE_REPLACED' } }) // its own line ids
  view.unmount()

  renderWithApollo(edit(tyre))
  const again = await screen.findByRole('dialog', { name: 'Edit the change' })
  await within(again).findByRole('group', { name: 'Tyre details' })
  await ui.click(within(again).getByRole('switch', { name: 'Tyre details' }))
  await ui.click(within(again).getByRole('button', { name: 'Save the change' }))
  await waitFor(() => expect(sent).toHaveLength(2))
  expect(sent[1].tyre).toBeNull()
})


/** A visit edited on Waiting to sync (decision 71), the vehicle's tyres `sets`; `parked`: one the server refused (Edit and apply). */
function visitEdit(sets: Parameters<typeof fakeTyres>[0], tyre: Record<string, unknown>, parked = false) {
  stubViewport('desktop')
  server.use(sessionHandler('NONE', () => null), healthHandler, ...fakeExpenseBackend(fakeVehicle(), []).handlers, ...fakeTyres(sets).handlers)
  const sent: DoneValues[] = []
  renderWithApollo(
    <RecurringDoneDialog
      open
      vehicle={{ id: 'v1', units: { distance: 'KILOMETERS' } }}
      items={[balancing]}
      selected={[]}
      change={{
        initial: { ids: ['rc1'], date: '2026-10-05', odometer: null, amount: 5000, discount: null, currency: 'HUF', title: 'Wheel balancing', category: 'Tyres', expenseId: 'e5', tyre: tyre as never },
        title: 'Edit the change',
        submitLabel: 'Save the change',
        parked,
      }}
      onSubmit={async (values) => {
        sent.push(values)
        return undefined
      }}
    />,
  )
  return { sent, ui: userEvent.setup() }
}

it("an edit of a visit's amount alone sends its tyre details as they were, its set kept", async () => {
  const tyre = { kind: 'WHEEL_BALANCING', setId: 's1', lineId: 'L7' }
  const { sent, ui } = visitEdit([tyreSummer, tyreWinter], tyre)
  const dialog = await screen.findByRole('dialog', { name: 'Edit the change' })
  await within(dialog).findByRole('group', { name: 'Tyre details' })
  await ui.clear(within(dialog).getByLabelText('Amount (optional)'))
  await ui.type(within(dialog).getByLabelText('Amount (optional)'), '6000')
  await ui.click(within(dialog).getByRole('button', { name: 'Save the change' }))
  await waitFor(() => expect(sent).toHaveLength(1))
  expect(sent[0].tyre).toEqual(tyre)
})

it('a waiting visit with no tyre set to offer keeps its tyre details unseen; a parked one shows them, to switch off', async () => {
  const tyre = { kind: 'WHEEL_BALANCING', setId: 's1', lineId: 'L7' }
  const waiting = visitEdit([], tyre)
  const dialog = await screen.findByRole('dialog', { name: 'Edit the change' })
  await within(dialog).findByLabelText('Amount (optional)')
  expect(within(dialog).queryByRole('switch', { name: 'Tyre details' })).not.toBeInTheDocument()
  await waiting.ui.click(within(dialog).getByRole('button', { name: 'Save the change' }))
  await waitFor(() => expect(waiting.sent).toHaveLength(1))
  expect(waiting.sent[0]).not.toHaveProperty('tyre') // the device may know no sets at all: the visit keeps its own
})

it('a parked visit whose set is gone shows its tyre details with no set left, and switched off sends them as none', async () => {
  const { sent, ui } = visitEdit([], { kind: 'WHEEL_BALANCING', setId: 's1', lineId: 'L7' }, true)
  const dialog = await screen.findByRole('dialog', { name: 'Edit the change' })
  await within(dialog).findByRole('group', { name: 'Tyre details' })
  await ui.click(within(dialog).getByRole('switch', { name: 'Tyre details' }))
  await ui.click(within(dialog).getByRole('button', { name: 'Save the change' }))
  await waitFor(() => expect(sent).toHaveLength(1))
  expect(sent[0].tyre).toBeNull()
})
