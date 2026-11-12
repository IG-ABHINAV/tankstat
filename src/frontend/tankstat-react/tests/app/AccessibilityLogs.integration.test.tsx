import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse } from 'msw'
import { expect, it, vi } from 'vitest'
import App from '../../src/App.tsx'
import { check, setup, setupAccessibilityTests } from '../support/accessibility.tsx'
import { server } from '../support/server.ts'
import { graphql } from '../support/graphql.ts'
import { fakeExpense, fakeLogBackend, fakePhotoStore, fakeRecognition, fakeRefueling, fakeVehicle, healthHandler, renderWithApollo, sessionHandler, stubViewport } from '../support/mocks.tsx'

// jsdom cannot decode pictures (the resize is covered in Media.unit.test.ts) and has no object URLs (previews of queued photos).
vi.mock('../../src/pictures/resizeImage.ts', async (original) => ({
  ...(await original<typeof import('../../src/pictures/resizeImage.ts')>()),
  resizeImage: vi.fn(async (file: Blob) => file),
}))

// The refuelling and expense dialogs, their photos and what was read from them.
setupAccessibilityTests()

it('the add refuelling dialog is labelled, described and free of violations', async () => {
  const { ui } = setup('/vehicles/v1?tab=refuelings')
  await screen.findByText(/Sep 1, 2026/)

  await ui.click(screen.getByRole('button', { name: 'Add refuelling' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add refuelling' })
  await within(dialog).findByText(/Last reading/)

  expect(dialog).toHaveAccessibleDescription(/Enter what you filled up/)
  await check(document.body)
})

it('values read from a photo, and a photo value offered next to a typed one, are linked to their fields and free of violations', async () => {
  const { ui } = setup('/vehicles/v1?tab=refuelings')
  server.use(...fakeRecognition({ results: [[{ name: 'TOTAL', value: '24687' }, { name: 'VOLUME', value: '38.52' }]], queuedPolls: 0 }).handlers)
  await screen.findByText(/Sep 1, 2026/)
  await ui.click(screen.getByRole('button', { name: 'Add refuelling' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add refuelling' })
  await within(dialog).findByText(/Last reading/)
  await ui.type(within(dialog).getByLabelText(/^Volume/), '40')

  await ui.upload(within(dialog).getByTestId('photo-camera'), new File([new Uint8Array([1, 2, 3])], 'receipt.png', { type: 'image/png' }))

  expect(await within(dialog).findByText('The photo shows 38.52', {}, { timeout: 5000 })).toBeInTheDocument()
  expect(within(dialog).getByLabelText('Total cost')).toHaveAccessibleDescription(/Read from the photo; check it\./)
  expect(within(dialog).getByLabelText(/^Volume/)).toHaveAccessibleDescription(/The photo shows 38\.52/)
  await check(document.body)
})

it('the reasons a photo gave less than it might have are announced in the dialog and free of violations', async () => {
  const { ui } = setup('/vehicles/v1?tab=refuelings')
  server.use(...fakeRecognition({ results: [[{ name: 'TOTAL', value: '24687' }]], issues: [[{ code: 'UNSURE', field: 'VOLUME' }, { code: 'ODOMETER_BELOW_LATEST', field: 'ODOMETER' }]], queuedPolls: 0 }).handlers)
  await screen.findByText(/Sep 1, 2026/)
  await ui.click(screen.getByRole('button', { name: 'Add refuelling' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add refuelling' })
  await within(dialog).findByText(/Last reading/)

  await ui.upload(within(dialog).getByTestId('photo-camera'), new File([new Uint8Array([1, 2, 3])], 'receipt.png', { type: 'image/png' }))

  const status = await within(dialog).findByRole('status', { name: 'Photo reading status' }, { timeout: 5000 })
  await waitFor(() => expect(status).toHaveTextContent(/lower than the last one logged/), { timeout: 5000 })
  expect(status).toHaveTextContent(/not reliably enough to fill it in/) // inside the live region, so a screen reader hears it
  await check(document.body)
})

it('the add expense dialog is labelled, described and free of violations', async () => {
  const { ui } = setup('/vehicles/v1?tab=expenses')
  await screen.findByText('Oil change')

  await ui.click(screen.getByRole('button', { name: 'Add expense' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add expense' })
  await within(dialog).findByText(/Used before/)

  expect(dialog).toHaveAccessibleDescription(/Money spent on the vehicle/)
  await check(document.body)
})

it('the photo gallery of the expense dialogs is a labelled group and free of violations', async () => {
  const { ui } = setup('/vehicles/v1?tab=expenses')
  await screen.findByText('Oil change')

  await ui.click(screen.getByRole('button', { name: 'Add expense' }))
  const adding = await screen.findByRole('dialog', { name: 'Add expense' })
  await within(adding).findByText(/Used before/)
  expect(within(adding).getByRole('group', { name: 'Photos' })).toBeInTheDocument()
  expect(within(adding).getByRole('button', { name: 'Take photo' })).toBeInTheDocument()
  await check(document.body)
  await ui.click(within(adding).getByRole('button', { name: 'Cancel' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())

  await ui.click(screen.getAllByRole('button', { name: /^Edit the expense/ })[0])
  const editing = await screen.findByRole('dialog', { name: 'Edit expense' })
  await within(editing).findByRole('group', { name: 'Photos' })
  await check(document.body)
})

it('a refuelling waiting for a review is marked in its row, and its edit dialog says what to check, free of violations', async () => {
  stubViewport('desktop')
  const backend = fakeLogBackend(fakeVehicle(), [
    fakeRefueling({ id: 'r1', odometer: 12480, reviewState: 'NEEDS_REVIEW', filledFromPhoto: ['ODOMETER'] }),
    fakeRefueling({ id: 'r2', date: '2026-09-10', odometer: null, reviewState: 'AWAITING_PHOTOS' }),
    fakeRefueling({ id: 'r3', date: '2026-09-20', volume: null, reviewState: 'INCOMPLETE' }),
  ])
  server.use(sessionHandler('NONE', () => null), healthHandler, ...backend.handlers)
  renderWithApollo(<App />, '/vehicles/v1?tab=refuelings')
  const ui = userEvent.setup()
  await screen.findByText('Check values')
  expect(screen.getByText('Reading photo…')).toBeInTheDocument()
  expect(screen.getByText('Values missing')).toBeInTheDocument()
  await check(document.body)

  await ui.click(screen.getAllByRole('button', { name: /^Edit the refuelling/ }).at(-1)!)
  const dialog = await screen.findByRole('dialog', { name: 'Edit refuelling' })
  await within(dialog).findByText(/Check them and save/)

  expect(within(dialog).getByLabelText(/^Odometer/)).toHaveAccessibleDescription(/Read from the photo; check it\./)
  await check(document.body)
})

it('the edit refuelling dialog with its photos is free of violations', async () => {
  const { ui } = setup('/vehicles/v1?tab=refuelings')
  await screen.findByText(/Sep 1, 2026/)

  await ui.click(screen.getAllByRole('button', { name: /^Edit the refuelling/ })[0])
  const dialog = await screen.findByRole('dialog', { name: 'Edit refuelling' })
  await within(dialog).findByRole('group', { name: 'Photos' })
  expect(within(dialog).getByRole('button', { name: 'Take photo' })).toBeInTheDocument()

  await check(document.body)
})

it('a gallery that holds photos is free of violations and its thumbnails and buttons are labelled', async () => {
  const photos = fakePhotoStore({
    e1: [{ id: 'p1', url: '/media/p1' }, { id: 'p2', url: '/media/p2' }],
    e2: [{ id: 'p3', url: '/media/p3' }, { id: 'p4', url: '/media/p4' }],
  })
  const { ui } = setup('/vehicles/v1?tab=expenses', 'desktop', photos)
  await screen.findByText('Oil change')

  await ui.click(screen.getAllByRole('button', { name: /^Edit the expense/ })[0])
  const dialog = await screen.findByRole('dialog', { name: 'Edit expense' })
  expect(await within(dialog).findAllByRole('link', { name: /^Open photo/ })).toHaveLength(2)
  expect(within(dialog).getAllByRole('button', { name: /^Remove photo/ })).toHaveLength(2)

  await check(document.body)
})

it('a photo that could not be uploaded, and the screen after a photo could not be attached, are free of violations', async () => {
  const photos = fakePhotoStore()
  photos.state.failWith = { key: 'photo.tooManyDrafts', args: { max: 20 } }
  const { ui } = setup('/vehicles/v1?tab=expenses', 'desktop', photos)
  await screen.findByText('Oil change')

  await ui.click(screen.getByRole('button', { name: 'Add expense' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add expense' })
  await within(dialog).findByText(/Used before/)
  await ui.type(within(dialog).getByLabelText('Title'), 'Tyres')
  await ui.type(within(dialog).getByLabelText('Amount'), '120000')
  const file = new File([new Uint8Array([1, 2, 3])], 'a.png', { type: 'image/png' })
  await ui.upload(within(dialog).getByTestId('photo-library'), file)
  await within(dialog).findByRole('alert')
  expect(within(dialog).getByRole('button', { name: 'Upload photo 1 again' })).toBeInTheDocument()
  await check(document.body)

  photos.state.failWith = undefined
  photos.state.unattachable.add('draft1')
  await ui.click(within(dialog).getByRole('button', { name: 'Upload photo 1 again' }))
  await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Add expense' })).toBeEnabled())
  await ui.click(within(dialog).getByRole('button', { name: 'Add expense' }))

  expect(await within(dialog).findByRole('alert')).toHaveTextContent(/could not be attached/)
  expect(within(dialog).getByRole('button', { name: 'Done' })).toBeInTheDocument()
  await check(document.body)
})

it('an expense a tyre log line made says so in its edit dialog, with a link to the line, free of violations (#288)', async () => {
  const { ui } = setup('/vehicles/v1?tab=expenses')
  server.use(
    graphql.query('ExpenseDetails', () =>
      HttpResponse.json({ data: { expense: { ...fakeExpense({ id: 'e1' }), photos: [], tyreEntry: { __typename: 'TyreEntryRef', id: 'l1', kind: 'VALVE_REPLACED', setName: 'Michelin Primacy 4', trashed: false } } } }),
    ),
  )
  await screen.findByText('Oil change')

  await ui.click(screen.getByRole('button', { name: 'Edit the expense Oil change' }))
  const dialog = await screen.findByRole('dialog', { name: 'Edit expense' })
  expect(await within(dialog).findByRole('link', { name: 'Open in the tyre log' })).toBeInTheDocument()
  await check(document.body)
})
