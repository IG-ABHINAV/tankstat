import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterAll, afterEach, beforeAll, expect, it, vi } from 'vitest'
import App from '../../../src/App.tsx'
import { server } from '../../support/server.ts'
import { fakeExpenseBackend, fakeVehicle, healthHandler, renderWithApollo, sessionHandler, stubViewport } from '../../support/mocks.tsx'
import { UUID } from '../../support/ids.ts'
import { brokenUpSummer, fakeTyres, tyreSummer, tyreWinter, type FakeTyreSet } from '../../support/tyres.ts'

beforeAll(() => server.listen({ onUnhandledFrame: 'error' }))
afterEach(() => {
  server.resetHandlers()
  vi.unstubAllGlobals()
})
afterAll(() => server.close())

// Single tyres on the Tyres tab (#289): the break-up, a broken-up set's card, a tyre's dialog, and the corners of a swap.
function setup(sets: FakeTyreSet[]) {
  stubViewport('desktop')
  const vehicle = fakeVehicle()
  const expenses = fakeExpenseBackend(vehicle, [])
  const tyres = fakeTyres(sets)
  server.use(sessionHandler('NONE', () => null), healthHandler, ...expenses.handlers, ...tyres.handlers)
  renderWithApollo(<App />, '/vehicles/v1?tab=tyres')
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
