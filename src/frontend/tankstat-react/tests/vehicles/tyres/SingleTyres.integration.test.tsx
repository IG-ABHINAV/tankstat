import { act, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterAll, afterEach, beforeAll, expect, it, vi } from 'vitest'
import App from '../../../src/App.tsx'
import { answered, server } from '../../support/server.ts'
import { connectivity } from '../../../src/offline/connectivity.ts'
import { outbox } from '../../../src/offline/outbox.ts'
import { fakeExpenseBackend, fakeVehicle, healthHandler, renderWithApollo, sessionHandler, stubViewport } from '../../support/mocks.tsx'
import { UUID } from '../../support/ids.ts'
import { brokenUpSummer, fakeTyres, tyreSummer, tyreWinter, type FakeTyreEntry, type FakeTyreSet } from '../../support/tyres.ts'

beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => {
  server.resetHandlers()
  vi.unstubAllGlobals()
})
afterAll(() => server.close())

// Single tyres on the Tyres tab (#289): the break-up, a broken-up set's card, a tyre's dialog, and the corners of a swap.
function setup(sets: FakeTyreSet[], entries: FakeTyreEntry[] = [], route = '/vehicles/v1?tab=tyres') {
  stubViewport('desktop')
  const vehicle = fakeVehicle()
  const expenses = fakeExpenseBackend(vehicle, [])
  const tyres = fakeTyres(sets, entries)
  server.use(sessionHandler('NONE', () => null), healthHandler, ...expenses.handlers, ...tyres.handlers)
  renderWithApollo(<App />, route)
  return { ...tyres, ui: userEvent.setup() }
}

/** The summer set broken up, in storage since the winter set went on on 10 Nov 2026 (its tyres' places ended then). */
const storedBrokenUp = (): FakeTyreSet => {
  const set = brokenUpSummer()
  return {
    ...set,
    onTheCar: false,
    installations: [{ ...set.installations[0], until: '2026-11-10' }],
    tyres: set.tyres!.map((x) => ({ ...x, places: x.places.map((p) => ({ ...p, until: '2026-11-10' })) })),
  }
}
const winterOn: FakeTyreSet = {
  ...tyreWinter, onTheCar: true, installations: [...tyreWinter.installations, { id: 'r3', setId: 's2', from: '2026-11-10', until: null, pairOneFront: true, distance: 1_000 }],
}

it('tracks a set tyre by tyre from its History: a label and a corner per tyre, the S labels suggested', async () => {
  const { ui, state } = setup([tyreSummer, tyreWinter])

  const card = await screen.findByRole('article', { name: 'Michelin Primacy 4' })
  await ui.click(within(card).getByRole('button', { name: 'History' }))
  const history = await screen.findByRole('dialog', { name: 'Michelin Primacy 4' })
  await ui.click(within(history).getByRole('button', { name: 'Track tyres one by one' }))
  const dialog = await screen.findByRole('dialog', { name: 'Track tyres one by one' })
  expect(within(dialog).getAllByLabelText(/^Label/).map((input) => (input as HTMLInputElement).value)).toEqual(['S1', 'S2', 'S3', 'S4'])
  expect(within(dialog).getByText('This cannot be undone: the set stays tracked tyre by tyre.')).toBeInTheDocument()
  // The fourth tyre: not sure where it is.
  await ui.click(within(dialog).getAllByRole('combobox', { name: 'Where it is now' })[3])
  await ui.click(await screen.findByRole('option', { name: 'Not sure' }))
  await ui.click(within(dialog).getByRole('button', { name: 'Track one by one' }))

  await waitFor(() => expect(state.calls.BreakUpTyreSet).toHaveLength(1))
  expect(state.calls.BreakUpTyreSet[0].input).toMatchObject({
    setId: 's1',
    tyres: [
      { id: UUID, label: 'S1', corner: 'FRONT_LEFT' }, { id: UUID, label: 'S2', corner: 'FRONT_RIGHT' }, { id: UUID, label: 'S3', corner: 'REAR_LEFT' }, { id: UUID, label: 'S4', corner: null },
    ],
  })
})

it('shows a broken-up set on the car as its tyres at their corners, a tyre without one as a row, and opens a tyre’s dialog', async () => {
  const { ui, state } = setup([brokenUpSummer(['FRONT_LEFT', 'FRONT_RIGHT', 'REAR_LEFT', null]), tyreWinter])

  const card = await screen.findByRole('article', { name: 'Michelin Primacy 4' })
  expect(within(card).getByText(/^Tracked tyre by tyre · 4 of 4 on the car since/)).toBeInTheDocument()
  expect(within(card).getByRole('button', { name: /^Front left: tyre S1, ≈ 40,600 km, / })).toBeInTheDocument()
  expect(within(card).getByRole('button', { name: /S4.*on the car, corner not recorded/ })).toBeInTheDocument()

  await ui.click(within(card).getByRole('button', { name: /^Rear left: tyre S3,/ }))
  const dialog = await screen.findByRole('dialog', { name: 'Tyre S3' })
  expect(within(dialog).getByText(/^On the car, rear left, since/)).toBeInTheDocument()
  expect(within(dialog).getByRole('img', { name: 'Front 0 km, rear 7,700 km' })).toBeInTheDocument()

  // Its label, then its time on the car to the trash.
  await ui.clear(within(dialog).getByLabelText(/^Label/))
  await ui.type(within(dialog).getByLabelText(/^Label/), 'S3x')
  await ui.click(within(dialog).getByRole('button', { name: 'Save label' }))
  await waitFor(() => expect(state.calls.UpdateTyre).toEqual([{ input: { id: 't3', label: 'S3x' } }]))
  await ui.click(within(dialog).getByRole('button', { name: /^Move to the trash: time on the car,/ }))
  await waitFor(() => expect(state.calls.DeleteTyrePlace).toEqual([{ id: 'p3' }]))
})

it('adds a time on the car for a tyre, and the same for another tyre ticked, each at its own corner', async () => {
  const { ui, state } = setup([brokenUpSummer(), tyreWinter])

  await ui.click(await screen.findByRole('button', { name: /^Rear right: tyre S4,/ }))
  const dialog = await screen.findByRole('dialog', { name: 'Tyre S4' })
  await ui.click(within(dialog).getByRole('button', { name: 'Add a time on the car' }))
  const add = await screen.findByRole('dialog', { name: 'Add a time on the car' })
  expect(within(add).getByText('A tyre is on the car only while its set is.')).toBeInTheDocument()
  await ui.click(within(add).getByRole('checkbox', { name: 'S1' }))
  await ui.click(within(add).getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(state.calls.AddTyrePlace).toHaveLength(2))
  expect(state.calls.AddTyrePlace.map((c) => (c.input as Record<string, unknown>).tyreId)).toEqual(['t4', 't1'])
  expect(state.calls.AddTyrePlace[1].input).toMatchObject({ corner: 'FRONT_LEFT', id: UUID }) // S1's own corner
})

it('swaps a broken-up set on: the same corners as last time at first, two tyres tapped exchange places', async () => {
  const { ui, state } = setup([storedBrokenUp(), winterOn])

  const card = await screen.findByRole('article', { name: 'Michelin Primacy 4' })
  expect(within(card).getByText('Tracked tyre by tyre · in storage')).toBeInTheDocument()
  expect(within(card).getByRole('button', { name: /S1.*last front left/ })).toBeInTheDocument()
  await ui.click(within(card).getByRole('button', { name: 'Swap to this set' }))
  const dialog = await screen.findByRole('dialog', { name: 'Swap tyres' })
  const where = within(dialog).getByRole('group', { name: 'Where each tyre goes' })
  expect(within(where).getByRole('radio', { name: 'Same as last time' })).toBeChecked()
  expect(within(where).getByRole('status')).toHaveTextContent('Tap two tyres to exchange them.')

  await ui.click(within(where).getByRole('button', { name: 'Front left: Tyre S1' }))
  expect(within(where).getByRole('status')).toHaveTextContent('Tyre S1 is picked. Tap the tyre it changes places with.')
  await ui.click(within(where).getByRole('button', { name: 'Rear left: Tyre S3' }))
  expect(within(where).getByRole('radio', { name: 'Your own' })).toBeChecked()
  expect(within(where).getByRole('button', { name: 'Rear left: Tyre S1' })).toBeInTheDocument()
  await ui.click(within(dialog).getByRole('button', { name: 'Swap tyres' }))

  await waitFor(() => expect(state.calls.SwapTyres).toHaveLength(1))
  expect(state.calls.SwapTyres[0].input).toMatchObject({
    setId: 's1', pairOneFront: null,
    corners: [{ tyreId: 't1', corner: 'REAR_LEFT' }, { tyreId: 't2', corner: 'FRONT_RIGHT' }, { tyreId: 't3', corner: 'FRONT_LEFT' }, { tyreId: 't4', corner: 'REAR_RIGHT' }],
  })
})

it('moves a tyre to the trash from its dialog, saying its corner is empty for that time', async () => {
  const { ui, state } = setup([brokenUpSummer(), tyreWinter])

  await ui.click(await screen.findByRole('button', { name: /^Front right: tyre S2,/ }))
  const dialog = await screen.findByRole('dialog', { name: 'Tyre S2' })
  await ui.click(within(dialog).getByRole('button', { name: 'Move the tyre to the trash' }))
  const confirm = await screen.findByRole('alertdialog', { name: 'Move the tyre to the trash?' })
  expect(within(confirm).getByText(/Its corner is simply empty for that time\./)).toBeInTheDocument()
  await ui.click(within(confirm).getByRole('button', { name: 'Move the tyre to the trash' }))
  await waitFor(() => expect(state.calls.DeleteTyre).toEqual([{ id: 't2' }]))
})

it('changes a broken-up set\'s positions front to rear at first, and puts a tyre without a corner at an empty one (#289, decision 50)', async () => {
  const { ui, state } = setup([brokenUpSummer(['FRONT_LEFT', 'FRONT_RIGHT', 'REAR_LEFT', null]), tyreWinter])

  const card = await screen.findByRole('article', { name: 'Michelin Primacy 4' })
  await ui.click(within(card).getByRole('button', { name: 'Change positions' }))
  const dialog = await screen.findByRole('dialog', { name: 'Change positions' })
  const where = within(dialog).getByRole('group', { name: 'Where each tyre goes' })
  expect(within(where).getByRole('radio', { name: 'Front ↔ rear, same side' })).toBeChecked()
  expect(within(where).getByRole('button', { name: 'Rear left: Tyre S1' })).toBeInTheDocument()

  // S4 has no corner: tapped, then the empty corner (front right, S2 at the rear now) takes it.
  await ui.click(within(where).getByRole('button', { name: 'Tyre S4, corner not recorded' }))
  expect(within(where).getByRole('status')).toHaveTextContent('Tyre S4 is picked. Tap an empty corner, or the tyre it changes places with.')
  await ui.click(within(where).getByRole('button', { name: 'Put S4 at front right' }))
  expect(within(where).getByRole('button', { name: 'Front right: Tyre S4' })).toBeInTheDocument()
  expect(within(where).getByRole('radio', { name: 'Your own' })).toBeChecked()
  await ui.click(within(dialog).getByRole('button', { name: 'Save positions' }))

  await waitFor(() => expect(state.calls.ChangeTyrePositions).toHaveLength(1))
  expect(state.calls.ChangeTyrePositions[0].input).toMatchObject({
    setId: 's1', pairOneFront: null,
    corners: [{ tyreId: 't1', corner: 'REAR_LEFT' }, { tyreId: 't2', corner: 'REAR_RIGHT' }, { tyreId: 't3', corner: 'FRONT_LEFT' }, { tyreId: 't4', corner: 'FRONT_RIGHT' }],
  })
})

it('says a refusal of a time on the car in words: the tyre at that corner, the set on the car then (#289)', async () => {
  const { ui, state } = setup([brokenUpSummer(), tyreWinter])
  state.failNext.push({ key: 'tyres.cornerTaken', args: { label: 'S2', from: '2026-03-28', until: null } })
  state.failNext.push({ key: 'tyres.placeWhileOtherSet', args: { set: 'Michelin Primacy 4', label: 'S1', other: 'Continental WinterContact', otherFrom: '2025-11-15', otherUntil: '2026-03-28' } })

  await ui.click(await screen.findByRole('button', { name: /^Front left: tyre S1,/ }))
  const tyre = await screen.findByRole('dialog', { name: 'Tyre S1' })
  await ui.click(within(tyre).getByRole('button', { name: 'Edit time on the car' }))
  const edit = await screen.findByRole('dialog', { name: 'Edit time on the car' })
  await ui.click(within(edit).getByRole('button', { name: 'Save' }))
  expect(await within(edit).findByText('Tyre S2 is at that corner then, from 2026-03-28.')).toBeInTheDocument()
  await ui.click(within(edit).getByRole('button', { name: 'Save' }))
  expect(await within(edit).findByText(/^The Michelin Primacy 4 was not on the car then: Continental WinterContact was on from 2025-11-15\./)).toBeInTheDocument()
})

// Entries for several tyres sharing one expense (#289 step 2).

/** A flat on S3 and S4 of the broken-up summer set: two lines made together, one expense of 13,000. */
const flat = (more: Partial<FakeTyreEntry> = {}): FakeTyreEntry[] => [
  { id: 'l3', kind: 'PUNCTURE_REPAIRED', setId: 's1', day: '2026-09-20', odometer: 148_200, note: 'Screws', expenseId: 'x1', amount: 13_000, currency: 'HUF', tyreId: 't3', groupId: 'l3', ...more },
  { id: 'l4', kind: 'PUNCTURE_REPAIRED', setId: 's1', day: '2026-09-20', odometer: 148_200, note: 'Screws', expenseId: 'x1', amount: 13_000, currency: 'HUF', tyreId: 't4', groupId: 'l3', ...more },
]

it('adds an entry for two tyres of a whole set: tapped on the car picture, the set broken up on save, a line each with one expense', async () => {
  const { ui, state } = setup([tyreSummer, tyreWinter])

  await ui.click(await screen.findByRole('button', { name: 'Add entry' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add entry' })
  await ui.click(within(dialog).getByRole('checkbox', { name: 'The whole set' }))
  expect(within(dialog).getAllByLabelText(/^Label, /).map((input) => (input as HTMLInputElement).value)).toEqual(['S1', 'S2', 'S3', 'S4'])
  // Nothing tapped: said, nothing sent.
  await ui.click(within(dialog).getByRole('button', { name: 'Save' }))
  expect(await within(dialog).findByText('Tap the tyres it is for.')).toBeInTheDocument()
  expect(state.calls.AddTyreEntry).toBeUndefined()

  await ui.click(within(dialog).getByRole('button', { name: 'The rear left tyre' }))
  await ui.click(within(dialog).getByRole('button', { name: 'The rear right tyre' }))
  expect(within(dialog).getByRole('button', { name: 'The rear left tyre' })).toHaveAttribute('aria-pressed', 'true')
  expect(within(dialog).getByText(/For several tyres it is one expense, counted once\./)).toBeInTheDocument()
  await ui.type(within(dialog).getByRole('textbox', { name: 'Paid' }), '13000')
  await ui.click(within(dialog).getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(state.calls.AddTyreEntry).toHaveLength(1))
  const input = state.calls.AddTyreEntry[0].input as Record<string, unknown> & { tyres: { id: string; tyreId: string }[]; breakUp: { tyres: { id: string }[] } }
  expect(input).toMatchObject({
    kind: 'PUNCTURE_REPAIRED', setId: 's1',
    tyres: [{ tyreId: UUID, id: UUID }, { tyreId: UUID, id: UUID }],
    breakUp: { setId: 's1', tyres: [{ label: 'S1', corner: 'FRONT_LEFT' }, { label: 'S2', corner: 'FRONT_RIGHT' }, { label: 'S3', corner: 'REAR_LEFT' }, { label: 'S4', corner: 'REAR_RIGHT' }] },
    fee: { amount: 13_000 },
  })
  expect(input.id).toBe(input.tyres[0].id) // the first line's id is the entry's
  expect(input.tyres.map((x) => x.tyreId)).toEqual([input.breakUp.tyres[2].id, input.breakUp.tyres[3].id]) // the rear tyres of the break-up
  expect(await screen.findByText('Saved for S3, S4, with one expense.')).toBeInTheDocument()
})

it('shows lines made together as one row, and its edit changes every tyre ticked under Also change it for', async () => {
  const { ui, state } = setup([brokenUpSummer(), tyreWinter], flat())

  const row = await screen.findByRole('button', { name: /^Puncture repaired, Michelin Primacy 4 · S3, S4, Sep 20, 2026, HUF/ })
  expect(screen.getAllByRole('button', { name: /^Puncture repaired/ })).toHaveLength(1) // one row, the amount once
  await ui.click(row)
  const dialog = await screen.findByRole('dialog', { name: 'Edit entry' })
  expect(within(dialog).getByRole('checkbox', { name: 'Tyre S4' })).toBeChecked()
  expect(within(dialog).getByText(/Opened from the tyre log: every tyre of the line is ticked\./)).toBeInTheDocument()
  expect(within(dialog).getByText(/One expense, shared with S4: a new amount changes it for all\./)).toBeInTheDocument()
  const note = within(dialog).getByRole('textbox', { name: 'Note' })
  await ui.clear(note)
  await ui.type(note, 'Two screws')
  await ui.click(within(dialog).getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(state.calls.UpdateTyreEntry).toHaveLength(1))
  expect(state.calls.UpdateTyreEntry[0].input).toMatchObject({ id: 'l3', note: 'Two screws', also: ['l4'] })
  expect(await screen.findByText('Changed for S3, S4.')).toBeInTheDocument()
})

it('asks on save when a tyre’s shared amount is emptied, from its own dialog with the others unticked', async () => {
  const { ui, state } = setup([{ ...brokenUpSummer(), tyres: brokenUpSummer().tyres!.map((x) => (x.id === 't3' ? { ...x, spent: [{ currency: 'HUF', amount: 6_500 }], repairs: 1 } : x)) }, tyreWinter], flat())

  await ui.click(await screen.findByRole('button', { name: /^Rear left: tyre S3,/ }))
  const tyre = await screen.findByRole('dialog', { name: 'Tyre S3' })
  expect(within(tyre).getByText('An amount shared by several tyres counts here by its even share.')).toBeInTheDocument()
  expect(within(tyre).getByText(/HUF\s?6,500/)).toBeInTheDocument()
  expect(within(tyre).getByText(/shared with S4/)).toBeInTheDocument()
  await ui.click(within(tyre).getByRole('button', { name: /^Edit entry: Puncture repaired, Sep 20, 2026/ }))
  const dialog = await screen.findByRole('dialog', { name: 'Edit entry' })
  expect(within(dialog).getByRole('checkbox', { name: 'Tyre S4' })).not.toBeChecked()
  await ui.clear(within(dialog).getByRole('textbox', { name: 'Paid' }))
  await ui.click(within(dialog).getByRole('button', { name: 'Save' }))

  const ask = await screen.findByRole('dialog', { name: 'The amount is shared' })
  expect(within(ask).getByRole('radio', { name: /Only S3 lets go of it/ })).toBeChecked()
  expect(within(ask).getByText('The expense stays with S4.')).toBeInTheDocument()
  expect(state.calls.UpdateTyreEntry).toBeUndefined()
  await ui.click(within(ask).getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(state.calls.UpdateTyreEntry).toHaveLength(1))
  expect(state.calls.UpdateTyreEntry[0].input).toMatchObject({ id: 'l3', fee: { amount: null }, sharedExpense: 'LET_GO' })
  expect(state.calls.UpdateTyreEntry[0].input).not.toHaveProperty('also')
})

it('a row’s bin asks which tyres’ lines go: the expense box only while every tyre holding it goes', async () => {
  const { ui, state } = setup([brokenUpSummer(), tyreWinter], flat())

  await ui.click(await screen.findByRole('button', { name: 'Move to the trash: Puncture repaired, Sep 20, 2026' }))
  const ask = await screen.findByRole('alertdialog', { name: 'Move to the trash?' })
  expect(within(ask).getByRole('checkbox', { name: 'Tyre S3' })).toBeChecked()
  expect(within(ask).getByRole('checkbox', { name: /^Also move its expense/ })).toBeChecked()
  // None ticked: said, and nothing to confirm.
  await ui.click(within(ask).getByRole('checkbox', { name: 'Tyre S3' }))
  await ui.click(within(ask).getByRole('checkbox', { name: 'Tyre S4' }))
  expect(within(ask).getByRole('alert')).toHaveTextContent('Tick at least one tyre.')
  expect(within(ask).getByRole('button', { name: 'Move to the trash' })).toBeDisabled()
  await ui.click(within(ask).getByRole('checkbox', { name: 'Tyre S3' }))
  expect(within(ask).queryByRole('checkbox', { name: /^Also move its expense/ })).not.toBeInTheDocument()
  expect(within(ask).getByText(/stays with S4\./)).toBeInTheDocument()
  await ui.click(within(ask).getByRole('button', { name: 'Move to the trash' }))

  await waitFor(() => expect(state.calls.DeleteTyreEntry).toHaveLength(1))
  expect(state.calls.DeleteTyreEntry[0].input).toEqual({ id: 'l3', withExpense: false })
})

it('an expense’s Open in the tyre log naming any line of a group opens the group’s dialog, every tyre ticked', async () => {
  setup([brokenUpSummer(), tyreWinter], flat(), '/vehicles/v1?tab=tyres&entry=l4')

  const dialog = await screen.findByRole('dialog', { name: 'Edit entry' })
  expect(within(dialog).getByRole('checkbox', { name: 'Tyre S4' })).toBeChecked()
})

it('a day of its own for one tyre of a shared amount sends no amount: it parts from the expense', async () => {
  const { ui, state } = setup([brokenUpSummer(), tyreWinter], flat())

  await ui.click(await screen.findByRole('button', { name: /^Rear left: tyre S3,/ }))
  const tyre = await screen.findByRole('dialog', { name: 'Tyre S3' })
  await ui.click(within(tyre).getByRole('button', { name: /^Edit entry: Puncture repaired/ }))
  const dialog = await screen.findByRole('dialog', { name: 'Edit entry' })
  expect(within(dialog).getByText(/A new day or odometer for this tyre alone parts it from the shared expense\./)).toBeInTheDocument()
  const odometer = within(dialog).getByRole('textbox', { name: /^Odometer/ })
  await ui.clear(odometer)
  await ui.type(odometer, '148300')
  await ui.click(within(dialog).getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(state.calls.UpdateTyreEntry).toHaveLength(1))
  expect(state.calls.UpdateTyreEntry[0].input).toMatchObject({ id: 'l3', odometer: 148_300 })
  expect(state.calls.UpdateTyreEntry[0].input).not.toHaveProperty('fee')
  expect(screen.queryByRole('dialog', { name: 'The amount is shared' })).not.toBeInTheDocument()
})

it('a row’s bin offline keeps a change per line, and Undo takes every one of them back', async () => {
  const { ui, state } = setup([brokenUpSummer(), tyreWinter], flat())
  await screen.findByRole('button', { name: /^Puncture repaired, Michelin Primacy 4 · S3, S4,/ })
  await answered()
  act(() => connectivity.failed())

  await ui.click(screen.getByRole('button', { name: 'Move to the trash: Puncture repaired, Sep 20, 2026' }))
  await ui.click(within(await screen.findByRole('alertdialog', { name: 'Move to the trash?' })).getByRole('button', { name: 'Move to the trash' }))
  await waitFor(() => expect(outbox.changes.map((c) => c.input?.id)).toEqual(['l3', 'l4']))
  await ui.click(await screen.findByRole('button', { name: 'Undo' }))

  await waitFor(() => expect(outbox.changes).toEqual([]))
  expect(state.calls.DeleteTyreEntry).toBeUndefined()
})

// Tyres replaced early (#289 step 3).

it('replaces two tyres from the set’s History: a new tyre each with the next label, why, and one price', async () => {
  const { ui, state } = setup([brokenUpSummer(), tyreWinter])

  await ui.click(within(await screen.findByRole('article', { name: 'Michelin Primacy 4' })).getByRole('button', { name: 'History' }))
  const history = await screen.findByRole('dialog', { name: 'Michelin Primacy 4' })
  await ui.click(within(history).getByRole('button', { name: 'Replace tyres' }))
  const dialog = await screen.findByRole('dialog', { name: 'Replace tyres' })
  // Nothing tapped: said, nothing sent.
  await ui.click(within(dialog).getByRole('button', { name: 'Replace' }))
  expect(await within(dialog).findByText('Tap the tyres to replace.')).toBeInTheDocument()
  expect(state.calls.ReplaceTyres).toBeUndefined()

  await ui.click(within(dialog).getByRole('button', { name: 'Tyre S1, front left' }))
  await ui.click(within(dialog).getByRole('button', { name: 'Tyre S2, front right' }))
  expect(within(dialog).getByRole('group', { name: 'New tyre S5, for S1' })).toBeInTheDocument()
  const second = within(dialog).getByRole('group', { name: 'New tyre S6, for S2' })
  const made = within(second).getByRole('textbox', { name: 'Made' })
  await ui.type(made, '2026')
  await ui.click(within(dialog).getByRole('combobox', { name: 'Why' }))
  await ui.click(await screen.findByRole('option', { name: 'Worn' }))
  await ui.type(within(dialog).getByRole('textbox', { name: 'Price' }), '78000')
  await ui.click(within(dialog).getByRole('button', { name: 'Replace' }))

  await waitFor(() => expect(state.calls.ReplaceTyres).toHaveLength(1))
  expect(state.calls.ReplaceTyres[0].input).toMatchObject({
    setId: 's1', why: 'WORN',
    tyres: [
      { tyreId: 't1', id: UUID, lineId: UUID, label: 'S5', name: null, madeWeek: null },
      { tyreId: 't2', id: UUID, lineId: UUID, label: 'S6', name: null, madeWeek: 20, madeYear: 2026 },
    ],
    price: { amount: 78_000, expenseId: UUID },
  })
  expect(state.calls.ReplaceTyres[0].input).not.toHaveProperty('breakUp')
  expect(await screen.findByText('Replaced. S1, S2 are under Retired.')).toBeInTheDocument()
})

it('replaces one tyre from its own dialog, picked at first; a retired tyre is a row under Retired that opens read only', async () => {
  const set = brokenUpSummer()
  const { ui, state } = setup([{ ...set, tyres: [...set.tyres!, { id: 't0', label: 'S0', distance: 30_000, isRetired: true, retiredWhy: 'DAMAGED', places: [] }] }, tyreWinter])

  await ui.click(await screen.findByRole('button', { name: /^Rear left: tyre S3,/ }))
  const tyre = await screen.findByRole('dialog', { name: 'Tyre S3' })
  await ui.click(within(tyre).getByRole('button', { name: 'Replace this tyre' }))
  const dialog = await screen.findByRole('dialog', { name: 'Replace this tyre' })
  expect(within(dialog).getByRole('button', { name: 'Tyre S3, rear left' })).toHaveAttribute('aria-pressed', 'true')
  await ui.click(within(dialog).getByRole('button', { name: 'Replace' }))
  await waitFor(() => expect(state.calls.ReplaceTyres).toHaveLength(1))
  expect(state.calls.ReplaceTyres[0].input).toMatchObject({ why: 'DAMAGED', tyres: [{ tyreId: 't3', label: 'S5' }] })
  expect(state.calls.ReplaceTyres[0].input).not.toHaveProperty('price')
  await ui.click(within(tyre).getByRole('button', { name: 'Close' }))

  await ui.click(screen.getByRole('button', { name: /^Tyre S0, Michelin Primacy 4, retired on Oct 1, 2026: Damaged/ }))
  const retired = await screen.findByRole('dialog', { name: 'Tyre S0' })
  expect(within(retired).getByText(/^Retired on Oct 1, 2026: Damaged/)).toBeInTheDocument()
  expect(within(retired).queryByRole('button', { name: 'Replace this tyre' })).not.toBeInTheDocument()
})
