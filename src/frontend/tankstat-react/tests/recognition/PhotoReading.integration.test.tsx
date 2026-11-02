import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { afterAll, afterEach, beforeAll, expect, it, vi } from 'vitest'
import App from '../../src/App.tsx'
import { resizeImage } from '../../src/pictures/resizeImage.ts'
import { server } from '../support/server.ts'
import {
  fakeExpense,
  fakeExpenseBackend,
  fakeLogBackend,
  fakePhotoStore,
  fakeRecognition,
  fakeRecurring,
  fakeRecurringBackend,
  fakeRefueling,
  fakeVehicle,
  healthHandler,
  renderWithApollo,
  sessionHandler,
  stubViewport,
  type FakeReadValue,
} from '../support/mocks.tsx'
import { dateValue, findDateField } from '../support/dates.ts'

// jsdom cannot decode pictures: the browser-side resize is covered in Media.unit.test.ts, here it passes the file through.
vi.mock('../../src/pictures/resizeImage.ts', async (original) => ({
  ...(await original<typeof import('../../src/pictures/resizeImage.ts')>()),
  resizeImage: vi.fn(async (file: Blob) => file),
}))

beforeAll(() => {
  server.listen({ onUnhandledFrame: 'error' })
  let n = 0
  URL.createObjectURL = () => `blob:preview-${n++}`
  URL.revokeObjectURL = () => undefined
})
afterEach(() => server.resetHandlers())
afterAll(() => server.close())

/** Polls come every 1.5 seconds: a reading that is queued at first arrives with the second one. */
const READ_WAIT = { timeout: 5000 }

const photo = (name = 'receipt.png') => new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], name, { type: 'image/png' })
const camera = (dialog: HTMLElement) => within(dialog).getByTestId('photo-camera') as HTMLInputElement

const fuelReceipt: FakeReadValue[] = [
  { name: 'TOTAL', value: '24687' },
  { name: 'VOLUME', value: '38.52' },
  { name: 'CURRENCY', value: 'EUR' },
  { name: 'DATE', value: '2026-09-17' },
]

function setupRefuelings(recognition = fakeRecognition({ results: [fuelReceipt] })) {
  stubViewport('desktop')
  const photos = fakePhotoStore()
  const backend = fakeLogBackend(fakeVehicle(), [fakeRefueling({ id: 'r1', odometer: 12000 })], photos)
  server.use(sessionHandler('NONE', () => null), healthHandler, ...backend.handlers, ...recognition.handlers)
  renderWithApollo(<App />, '/vehicles/v1?tab=refuelings')
  return { ...backend, photos, recognition, ui: userEvent.setup() }
}

async function openAddRefueling(ui: ReturnType<typeof userEvent.setup>) {
  // The file's first test also loads the page and the data grid for the first time, which can take longer than the usual wait.
  await screen.findAllByRole('button', { name: /^Edit the refuelling/ }, { timeout: 10_000 })
  await ui.click(screen.getByRole('button', { name: 'Add refuelling' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add refuelling' })
  await waitFor(() => expect(within(dialog).getByLabelText('Currency')).toHaveValue('HUF'))
  return dialog
}

it('fills a new refuelling from a receipt photo read on the server, marks what it filled and says so', async () => {
  const { ui, photos, recognition, state } = setupRefuelings()
  const dialog = await openAddRefueling(ui)

  await ui.upload(camera(dialog), photo())

  expect(await within(dialog).findByText('Reading…')).toBeInTheDocument() // queued at first
  await waitFor(() => expect(within(dialog).getByLabelText(/^Volume/)).toHaveValue('38.52'), READ_WAIT)
  expect(within(dialog).getByLabelText('Total cost')).toHaveValue('24687')
  expect(within(dialog).getByLabelText('Currency')).toHaveValue('EUR') // the receipt's currency replaces the last one used
  expect(dateValue(await findDateField('Date', dialog))).toBe('2026-09-17')
  expect(within(dialog).getByLabelText(/^Volume/)).toHaveAccessibleDescription(/Read from the photo; check it\./)
  expect(within(dialog).getAllByText('Read from the photo; check it.')).toHaveLength(4)
  expect(within(dialog).getByRole('status', { name: 'Photo reading status' })).toHaveTextContent(
    'Filled in from the photo: Date, Volume (L), Total cost, Currency. Check them before saving.',
  )
  expect(within(dialog).queryByText('Reading…')).not.toBeInTheDocument()
  expect(photos.state.draftQueries).toEqual(['?form=refueling&locale=en'])
  expect(recognition.state.asked.at(-1)).toEqual(['draft1'])
  expect(vi.mocked(resizeImage)).toHaveBeenCalledWith(expect.anything(), { maxEdge: 1600, format: 'jpeg' }) // a photo that is read goes as JPEG

  await ui.type(within(dialog).getByLabelText(/^Odometer/), '12500')
  await ui.click(within(dialog).getByRole('button', { name: 'Add refuelling' }))

  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(state.calls.LogRefueling).toEqual([
    { input: expect.objectContaining({ date: '2026-09-17', volume: 38.52, totalCost: 24687, currency: 'EUR', odometer: 12500, photoIds: ['draft1'] }) },
  ])
})

it('fills the discount from a receipt with the other amounts, and saves it with what was paid', async () => {
  const receipt: FakeReadValue[] = [
    { name: 'TOTAL', value: '34744' },
    { name: 'VOLUME', value: '43' },
    { name: 'UNIT_PRICE', value: '818' },
    { name: 'DISCOUNT', value: '430' },
    { name: 'CURRENCY', value: 'HUF' },
  ]
  const { ui, state } = setupRefuelings(fakeRecognition({ results: [receipt] }))
  const dialog = await openAddRefueling(ui)

  await ui.upload(camera(dialog), photo())

  await waitFor(() => expect(within(dialog).getByLabelText('Discount')).toHaveValue('430'), READ_WAIT) // the receipt of #204
  expect(within(dialog).getByLabelText('Discount')).toHaveAccessibleDescription(/Read from the photo; check it\./)
  expect(within(dialog).getByLabelText('Price per L')).toHaveValue('818') // the pump's price, as printed
  expect(within(dialog).getByLabelText('Total cost')).toHaveValue('34744')
  expect(within(dialog).getByLabelText(/^Volume/)).toHaveValue('43')
  expect(within(dialog).getByRole('status', { name: 'Photo reading status' })).toHaveTextContent(/Discount/)

  await ui.type(within(dialog).getByLabelText(/^Odometer/), '12500')
  await ui.click(within(dialog).getByRole('button', { name: 'Add refuelling' }))

  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(state.calls.LogRefueling).toEqual([{ input: expect.objectContaining({ volume: 43, totalCost: 34744, discount: 430, currency: 'HUF' }) }])
})

it('keeps the smaller WebP where the server says photo reading is off', async () => {
  const { ui, recognition } = setupRefuelings(fakeRecognition({ available: false }))
  const dialog = await openAddRefueling(ui)
  await waitFor(() => expect(recognition.state.statusAsked).toBeGreaterThan(0)) // the dialog asked, and was told "off"

  await ui.upload(camera(dialog), photo())

  await waitFor(() => expect(vi.mocked(resizeImage)).toHaveBeenCalledWith(expect.anything(), { maxEdge: 1600 }))
})

it('never overwrites what the user typed: it offers the photo value, and "Use it" takes it', async () => {
  const { ui } = setupRefuelings()
  const dialog = await openAddRefueling(ui)
  await ui.type(within(dialog).getByLabelText(/^Volume/), '40')

  await ui.upload(camera(dialog), photo())

  expect(await within(dialog).findByText('The photo shows 38.52', {}, READ_WAIT)).toBeInTheDocument()
  expect(within(dialog).getByLabelText(/^Volume/)).toHaveValue('40')
  expect(within(dialog).getByLabelText('Total cost')).toHaveValue('24687') // untouched fields are still filled
  await ui.click(within(dialog).getByRole('button', { name: 'Use 38.52 from the photo for Volume (L)' }))

  expect(within(dialog).getByLabelText(/^Volume/)).toHaveValue('38.52')
  expect(within(dialog).queryByText('The photo shows 38.52')).not.toBeInTheDocument()
})

it('fills the odometer from a dashboard photo', async () => {
  const { ui } = setupRefuelings(fakeRecognition({ results: [[{ name: 'ODOMETER', value: '12480' }]], queuedPolls: 0 }))
  const dialog = await openAddRefueling(ui)

  await ui.upload(camera(dialog), photo('dashboard.png'))

  await waitFor(() => expect(within(dialog).getByLabelText(/^Odometer/)).toHaveValue('12480'), READ_WAIT)
  expect(within(dialog).getByRole('status', { name: 'Photo reading status' })).toHaveTextContent('Filled in from the photo: Odometer (kilometers).')
})

it('says so when nothing could be read from the photos', async () => {
  const { ui } = setupRefuelings(fakeRecognition({ results: [[]], queuedPolls: 0 }))
  const dialog = await openAddRefueling(ui)

  await ui.upload(camera(dialog), photo())

  expect(await within(dialog).findByText('Nothing could be read from the photos.', {}, READ_WAIT)).toBeInTheDocument()
  expect(within(dialog).getByLabelText(/^Volume/)).toHaveValue('')
})

it('says why nothing was filled in: an odometer lower than the last one logged', async () => {
  const { ui } = setupRefuelings(fakeRecognition({ results: [[]], issues: [[{ code: 'ODOMETER_BELOW_LATEST', field: 'ODOMETER' }]], queuedPolls: 0 }))
  const dialog = await openAddRefueling(ui)

  await ui.upload(camera(dialog), photo('dashboard.png'))

  const status = within(dialog).getByRole('status', { name: 'Photo reading status' })
  await waitFor(() => expect(status).toHaveTextContent('Nothing could be filled in from the photos.'), READ_WAIT)
  expect(status).toHaveTextContent('The photo shows an odometer reading that is lower than the last one logged (12,000 km), so it was not filled in.')
  expect(within(dialog).getByLabelText(/^Odometer/)).toHaveValue('')
})

it('says why part of a receipt was left out, next to what was filled in', async () => {
  const { ui } = setupRefuelings(
    fakeRecognition({
      results: [[{ name: 'TOTAL', value: '24687' }, { name: 'CURRENCY', value: 'EUR' }]],
      issues: [[{ code: 'UNSURE', field: 'VOLUME' }, { code: 'UNSURE', field: 'UNIT_PRICE' }]],
      queuedPolls: 0,
    }),
  )
  const dialog = await openAddRefueling(ui)

  await ui.upload(camera(dialog), photo())

  const status = within(dialog).getByRole('status', { name: 'Photo reading status' })
  await waitFor(() => expect(status).toHaveTextContent('Filled in from the photo: Total cost, Currency.'), READ_WAIT)
  expect(status).toHaveTextContent(/Volume \(.*\): the photo could be read here, but not reliably enough to fill it in\./)
  expect(status).toHaveTextContent('Price per L: the photo could be read here, but not reliably enough to fill it in.') // nor could it be worked out
  expect(within(dialog).getByLabelText(/^Volume/)).toHaveValue('')
  expect(within(dialog).getByLabelText('Price per L')).toHaveValue('')
})

it('works out the unit price from the volume and total on a receipt, and then needs no reason why the photo did not show it', async () => {
  const { ui } = setupRefuelings(fakeRecognition({ results: [fuelReceipt], issues: [[{ code: 'UNSURE', field: 'UNIT_PRICE' }]], queuedPolls: 0 }))
  const dialog = await openAddRefueling(ui)

  await ui.upload(camera(dialog), photo())

  await waitFor(() => expect(within(dialog).getByLabelText('Price per L')).toHaveValue('640.888'), READ_WAIT)
  expect(within(dialog).getByLabelText('Price per L')).toHaveAccessibleDescription(/Worked out from the other amounts\./)
  const status = within(dialog).getByRole('status', { name: 'Photo reading status' })
  expect(status).toHaveTextContent('Filled in from the photo: Date, Volume (L), Total cost, Currency.')
  expect(status).not.toHaveTextContent('Price per L')
})

it('offers the unit price on a photo next to a calculated one, and "Use it" takes it: the typed total and the litres stay', async () => {
  const { ui } = setupRefuelings(fakeRecognition({ results: [[{ name: 'UNIT_PRICE', value: '599.9' }]], queuedPolls: 0 }))
  const dialog = await openAddRefueling(ui)
  await ui.type(within(dialog).getByLabelText(/^Volume/), '40')
  await ui.type(within(dialog).getByLabelText('Total cost'), '24000')
  expect(within(dialog).getByLabelText('Price per L')).toHaveValue('600')

  await ui.upload(camera(dialog), photo('pump.png'))

  expect(await within(dialog).findByText('The photo shows 599.9', {}, READ_WAIT)).toBeInTheDocument()
  expect(within(dialog).getByLabelText('Price per L')).toHaveValue('600') // what the user typed decides until they choose
  await ui.click(within(dialog).getByRole('button', { name: 'Use 599.9 from the photo for Price per L' }))

  expect(within(dialog).getByLabelText('Price per L')).toHaveValue('599.9')
  expect(within(dialog).getByLabelText(/^Volume/)).toHaveValue('40') // the litres never move by themselves (#204)
  expect(within(dialog).getByLabelText('Total cost')).toHaveValue('24000') // nor does the amount paid the user typed: the receipt's
})

it('keeps the total following the volume typed key by key next to a unit price read from a photo', async () => {
  const { ui } = setupRefuelings(fakeRecognition({ results: [[{ name: 'UNIT_PRICE', value: '600' }]], queuedPolls: 0 }))
  const dialog = await openAddRefueling(ui)

  await ui.upload(camera(dialog), photo('pump.png'))
  await waitFor(() => expect(within(dialog).getByLabelText('Price per L')).toHaveValue('600'), READ_WAIT)
  await ui.type(within(dialog).getByLabelText(/^Volume/), '40')

  expect(within(dialog).getByLabelText('Total cost')).toHaveValue('24000') // not the price worked out from the total after the first key
  expect(within(dialog).getByLabelText('Price per L')).toHaveValue('600')
})

it('says so when a photo could not be read at all', async () => {
  const { ui } = setupRefuelings(fakeRecognition({ results: [[]], failed: [1], queuedPolls: 0 }))
  const dialog = await openAddRefueling(ui)

  await ui.upload(camera(dialog), photo())

  const status = within(dialog).getByRole('status', { name: 'Photo reading status' })
  await waitFor(() => expect(status).toHaveTextContent('Nothing could be filled in from the photos.'), READ_WAIT)
  expect(status).toHaveTextContent('The photo could not be read.')
})

it('tells an expense photo that was no odometer or receipt, and an odometer below the last without its number (the dialog does not know it)', async () => {
  stubViewport('desktop')
  const recognition = fakeRecognition({
    results: [[], []],
    issues: [[{ code: 'UNRECOGNISED' }], [{ code: 'ODOMETER_BELOW_LATEST', field: 'ODOMETER' }]],
    queuedPolls: 0,
  })
  const backend = fakeExpenseBackend(fakeVehicle(), [fakeExpense({ id: 'e1', title: 'Oil change' })], fakePhotoStore())
  server.use(sessionHandler('NONE', () => null), healthHandler, ...backend.handlers, ...recognition.handlers)
  renderWithApollo(<App />, '/vehicles/v1?tab=expenses')
  const ui = userEvent.setup()
  await screen.findByText('Oil change')
  await ui.click(screen.getByRole('button', { name: 'Add expense' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add expense' })
  await waitFor(() => expect(within(dialog).getByLabelText('Currency')).toHaveValue('HUF'))

  await ui.upload(camera(dialog), photo())
  await ui.upload(camera(dialog), photo('dashboard.png'))

  const status = within(dialog).getByRole('status', { name: 'Photo reading status' })
  await waitFor(() => expect(status).toHaveTextContent('The photo could not be recognised as an odometer or a receipt.'), READ_WAIT)
  await waitFor(() => expect(status).toHaveTextContent('The photo shows an odometer reading that is lower than the last one logged, so it was not filled in.'), READ_WAIT)
})

it('without photo reading on the server, photos are only uploaded and nothing waits for readings', async () => {
  const { ui, photos, recognition } = setupRefuelings(fakeRecognition({ available: false, results: [fuelReceipt] }))
  const dialog = await openAddRefueling(ui)

  await ui.upload(camera(dialog), photo())

  await within(dialog).findByRole('button', { name: 'Remove photo 1' })
  expect(within(dialog).queryByText('Reading…')).not.toBeInTheDocument()
  expect(recognition.state.asked).toEqual([])
  expect(photos.state.draftQueries).toEqual(['?form=refueling&locale=en']) // the server decides; it reads nothing when it is off
  expect(within(dialog).getByLabelText(/^Volume/)).toHaveValue('')
})

it('asks again after an upload when photo reading was off as the dialog opened, and fills in once the provider is back', async () => {
  const { ui, recognition } = setupRefuelings(fakeRecognition({ available: false, results: [fuelReceipt], queuedPolls: 0 }))
  const dialog = await openAddRefueling(ui)

  await ui.upload(camera(dialog), photo())
  await within(dialog).findByRole('button', { name: 'Remove photo 1' })
  recognition.state.available = true // the provider was only briefly unreachable; the server read the queued photo meanwhile

  await waitFor(() => expect(within(dialog).getByLabelText(/^Volume/)).toHaveValue('38.52'), { timeout: 8000 })
  expect(recognition.state.asked.at(-1)).toEqual(['draft1'])
}, 10_000)

it('fills a new expense from its receipt: the shop becomes the title', async () => {
  stubViewport('desktop')
  const photos = fakePhotoStore()
  const recognition = fakeRecognition({
    results: [[{ name: 'TITLE', value: 'Csillag Autómosó Kft.' }, { name: 'TOTAL', value: '25870' }, { name: 'DATE', value: '2026-09-25' }]],
  })
  const backend = fakeExpenseBackend(fakeVehicle(), [fakeExpense({ id: 'e1', title: 'Oil change' })], photos)
  server.use(sessionHandler('NONE', () => null), healthHandler, ...backend.handlers, ...recognition.handlers)
  renderWithApollo(<App />, '/vehicles/v1?tab=expenses')
  const ui = userEvent.setup()
  await screen.findByText('Oil change')
  await ui.click(screen.getByRole('button', { name: 'Add expense' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add expense' })
  await waitFor(() => expect(within(dialog).getByLabelText('Currency')).toHaveValue('HUF'))

  await ui.upload(camera(dialog), photo())

  await waitFor(() => expect(within(dialog).getByLabelText('Title')).toHaveValue('Csillag Autómosó Kft.'), READ_WAIT)
  expect(within(dialog).getByLabelText('Amount')).toHaveValue('25870')
  expect(dateValue(await findDateField('Date', dialog))).toBe('2026-09-25')
  expect(within(dialog).getByLabelText('Currency')).toHaveValue('HUF') // the receipt showed none: the last one stays
  expect(photos.state.draftQueries).toEqual(['?form=expense&locale=en'])
})

function setupRecurring(recognition = fakeRecognition({ results: [[{ name: 'ODOMETER', value: '62480' }, { name: 'TOTAL', value: '35000' }]], queuedPolls: 0 })) {
  stubViewport('desktop')
  const photos = fakePhotoStore()
  const expenses = fakeExpenseBackend(fakeVehicle(), [], photos)
  const recurring = fakeRecurringBackend([fakeRecurring()])
  server.use(sessionHandler('NONE', () => null), healthHandler, ...expenses.handlers, ...recurring.handlers, ...recognition.handlers)
  renderWithApollo(<App />, '/vehicles/v1?tab=recurring')
  return { ...recurring, photos, ui: userEvent.setup() }
}

async function openDone(ui: ReturnType<typeof userEvent.setup>) {
  await screen.findByRole('rowheader', { name: /Oil change/ })
  await ui.click(screen.getByRole('button', { name: 'Mark Oil change as done' }))
  const dialog = await screen.findByRole('dialog', { name: 'Mark as done: Oil change' })
  await waitFor(() => expect(within(dialog).getByLabelText('Currency')).toHaveValue('HUF'))
  return dialog
}

it('marking a schedule done takes the odometer and cost from a photo, which goes with the logged expense', async () => {
  const { ui, state, photos } = setupRecurring()
  const dialog = await openDone(ui)

  await ui.upload(camera(dialog), photo('dashboard.png'))

  await waitFor(() => expect(within(dialog).getByLabelText(/^Odometer/)).toHaveValue('62480'), READ_WAIT)
  expect(within(dialog).getByLabelText('Amount (optional)')).toHaveValue('35000')
  expect(photos.state.draftQueries).toEqual(['?form=expense&locale=en'])
  await ui.click(within(dialog).getByRole('button', { name: 'Mark as done' }))

  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(state.calls.MarkRecurringExpensesDone).toEqual([
    { input: expect.objectContaining({ ids: ['rc1'], odometer: 62480, amount: 35000, currency: 'HUF', photoIds: ['draft1'] }) },
  ])
  expect(photos.state.draftDeletes).toEqual([])
})

it('marking a schedule done without an amount keeps no photos: they are not sent and are deleted', async () => {
  const { ui, state, photos } = setupRecurring()
  const dialog = await openDone(ui)
  await ui.upload(camera(dialog), photo('dashboard.png'))
  await waitFor(() => expect(within(dialog).getByLabelText(/^Odometer/)).toHaveValue('62480'), READ_WAIT)

  await ui.clear(within(dialog).getByLabelText('Amount (optional)')) // the reading is done: without an amount no expense is logged
  expect(within(dialog).getByText('Without an amount no expense is logged, so the photos are not kept.')).toBeInTheDocument()
  await ui.click(within(dialog).getByRole('button', { name: 'Mark as done' }))

  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(state.calls.MarkRecurringExpensesDone).toEqual([{ input: expect.objectContaining({ odometer: 62480, amount: null, photoIds: [] }) }])
  await waitFor(() => expect(photos.state.draftDeletes).toEqual(['draft1']))
})

it('saves a refuelling while its photo is still being read, leaving the odometer to it, and lists it as waiting', async () => {
  const { ui, state } = setupRefuelings(fakeRecognition({ results: [[{ name: 'ODOMETER', value: '12480' }]], queuedPolls: 1000 }))
  const dialog = await openAddRefueling(ui)
  await ui.upload(camera(dialog), photo('dashboard.png'))
  expect(await within(dialog).findByText('Reading…')).toBeInTheDocument()

  expect(within(dialog).getByLabelText(/^Odometer/)).toHaveAccessibleDescription(/Leave it empty: it is filled in from the photo after saving\./)
  expect(within(dialog).getByLabelText(/^Odometer/)).not.toBeRequired()
  await ui.type(within(dialog).getByLabelText(/^Volume/), '40')
  await ui.type(within(dialog).getByLabelText('Total cost'), '24000')
  await ui.click(within(dialog).getByRole('button', { name: 'Add refuelling' }))

  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(state.calls.LogRefueling).toEqual([{ input: expect.objectContaining({ volume: 40, totalCost: 24000, odometer: null, photoIds: ['draft1'] }) }])
  expect(await screen.findByText('Reading photo…')).toBeInTheDocument()
})

it('without a photo being read, every value of a refuelling is required', async () => {
  const { ui, state } = setupRefuelings()
  const dialog = await openAddRefueling(ui)

  await ui.type(within(dialog).getByLabelText(/^Volume/), '40')
  await ui.type(within(dialog).getByLabelText('Total cost'), '24000')
  await ui.click(within(dialog).getByRole('button', { name: 'Add refuelling' }))

  expect(await within(dialog).findByText('Odometer (kilometers) is required')).toBeInTheDocument()
  expect(within(dialog).queryByText(/Leave it empty/)).not.toBeInTheDocument()
  expect(state.calls.LogRefueling).toBeUndefined()
})

it('a discount the server filled in from the photo is marked in the edit dialog with the total it came with', async () => {
  stubViewport('desktop')
  const filled = fakeRefueling({ id: 'r1', volume: 43, totalCost: 34744, discount: 430, odometer: 12480, reviewState: 'NEEDS_REVIEW', filledFromPhoto: ['TOTAL', 'DISCOUNT'] })
  const backend = fakeLogBackend(fakeVehicle(), [filled])
  server.use(sessionHandler('NONE', () => null), healthHandler, ...backend.handlers, ...fakeRecognition().handlers)
  renderWithApollo(<App />, '/vehicles/v1?tab=refuelings')
  const ui = userEvent.setup()

  await ui.click(await screen.findByRole('button', { name: /^Edit the refuelling/ }))
  const dialog = await screen.findByRole('dialog', { name: 'Edit refuelling' })

  await waitFor(() => expect(within(dialog).getByLabelText('Discount')).toHaveValue('430'))
  expect(within(dialog).getByLabelText('Discount')).toHaveAccessibleDescription(/Read from the photo; check it\./)
  expect(within(dialog).getByLabelText('Total cost')).toHaveAccessibleDescription(/Read from the photo; check it\./)
  expect(within(dialog).getByLabelText(/^Volume/)).not.toHaveAccessibleDescription(/Read from the photo/)
})

it('a refuelling filled in from its photo is marked in the list, and its edit dialog says what to check', async () => {
  stubViewport('desktop')
  const backend = fakeLogBackend(fakeVehicle(), [fakeRefueling({ id: 'r1', odometer: 12480, reviewState: 'NEEDS_REVIEW', filledFromPhoto: ['ODOMETER'] })])
  server.use(sessionHandler('NONE', () => null), healthHandler, ...backend.handlers, ...fakeRecognition().handlers)
  renderWithApollo(<App />, '/vehicles/v1?tab=refuelings')
  const ui = userEvent.setup()

  expect(await screen.findByText('Check values')).toBeInTheDocument()
  await ui.click(screen.getByRole('button', { name: /^Edit the refuelling/ }))
  const dialog = await screen.findByRole('dialog', { name: 'Edit refuelling' })

  expect(await within(dialog).findByText('Some values were read from its photos (marked below). Check them and save.')).toBeInTheDocument()
  expect(within(dialog).getByLabelText(/^Odometer/)).toHaveAccessibleDescription(/Read from the photo; check it\./)
  expect(within(dialog).getByLabelText(/^Volume/)).not.toHaveAccessibleDescription(/Read from the photo/)
  await ui.click(within(dialog).getByRole('button', { name: 'Save changes' }))

  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(backend.state.calls.UpdateRefueling).toEqual([{ input: expect.objectContaining({ id: 'r1', odometer: 12480 }) }])
  await waitFor(() => expect(screen.queryByText('Check values')).not.toBeInTheDocument())
})

it('saves an expense while its receipt is still being read, leaving the amount to it', async () => {
  stubViewport('desktop')
  const photos = fakePhotoStore()
  const recognition = fakeRecognition({ results: [[{ name: 'TOTAL', value: '25870' }]], queuedPolls: 1000 })
  const backend = fakeExpenseBackend(fakeVehicle(), [fakeExpense({ id: 'e1', title: 'Oil change' })], photos)
  server.use(sessionHandler('NONE', () => null), healthHandler, ...backend.handlers, ...recognition.handlers)
  renderWithApollo(<App />, '/vehicles/v1?tab=expenses')
  const ui = userEvent.setup()
  await screen.findByText('Oil change')
  await ui.click(screen.getByRole('button', { name: 'Add expense' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add expense' })
  await waitFor(() => expect(within(dialog).getByLabelText('Currency')).toHaveValue('HUF'))

  await ui.upload(camera(dialog), photo())
  expect(await within(dialog).findByText('Reading…')).toBeInTheDocument()
  // Said where the user looks before saving, inside the reading status region: saving now is fine.
  expect(within(dialog).getByRole('status', { name: 'Photo reading status' })).toHaveTextContent(
    'Reading the photo… You can save now: what you leave empty is filled in from the photo.',
  )
  await ui.type(within(dialog).getByLabelText('Title'), 'Car wash')
  await ui.click(within(dialog).getByRole('button', { name: 'Add expense' }))

  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(backend.state.calls.AddExpense).toEqual([{ input: expect.objectContaining({ title: 'Car wash', amount: null, photoIds: ['draft1'] }) }])
  expect(await screen.findByText('Reading photo…')).toBeInTheDocument()
})

// ---- photos added to a saved log, in its edit dialog -----------------------------------------------------

function setupExpenseEdit(recognitionOptions: Parameters<typeof fakeRecognition>[0] = {}) {
  stubViewport('desktop')
  const photos = fakePhotoStore()
  const recognition = fakeRecognition({
    results: [[{ name: 'TOTAL', value: '25870' }, { name: 'CURRENCY', value: 'EUR' }, { name: 'DATE', value: '2026-09-25' }]],
    queuedPolls: 0,
    photos,
    ...recognitionOptions,
  })
  const backend = fakeExpenseBackend(fakeVehicle(), [fakeExpense({ id: 'e1', title: 'Oil change', date: '2026-09-01', amount: 35000, currency: 'HUF' })], photos)
  server.use(sessionHandler('NONE', () => null), healthHandler, ...backend.handlers, ...recognition.handlers)
  renderWithApollo(<App />, '/vehicles/v1?tab=expenses')
  return { ...backend, photos, recognition, ui: userEvent.setup() }
}

async function openEditExpense(ui: ReturnType<typeof userEvent.setup>) {
  await screen.findByText('Oil change')
  await ui.click(screen.getByRole('button', { name: 'Edit the expense Oil change' }))
  const dialog = await screen.findByRole('dialog', { name: 'Edit expense' })
  await waitFor(() => expect(within(dialog).getByLabelText(/^Amount/)).toHaveValue('35000'))
  return dialog
}

it('a photo added to a saved expense is read: the saved values stay, the photo only offers what differs', async () => {
  const { ui, photos } = setupExpenseEdit()
  const dialog = await openEditExpense(ui)

  await ui.upload(camera(dialog), photo())

  expect(await within(dialog).findAllByText(/The photo shows/, {}, READ_WAIT)).not.toHaveLength(0)
  expect(within(dialog).getByLabelText(/^Amount/)).toHaveValue('35000') // saved: kept
  expect(within(dialog).getByLabelText('Currency')).toHaveValue('HUF')
  expect(within(dialog).getByText(/The photo shows 25870/)).toBeInTheDocument()
  expect(photos.state.putQueries).toEqual(['?form=expense&locale=en'])
  expect(vi.mocked(resizeImage)).toHaveBeenLastCalledWith(expect.anything(), { maxEdge: 1600, format: 'jpeg' }) // a photo that is read goes as JPEG
})

it('an amount emptied in the edit dialog is filled in from the new photo', async () => {
  const { ui } = setupExpenseEdit()
  const dialog = await openEditExpense(ui)

  await ui.clear(within(dialog).getByLabelText(/^Amount/))
  await ui.upload(camera(dialog), photo())

  await waitFor(() => expect(within(dialog).getByLabelText(/^Amount/)).toHaveValue('25870'), READ_WAIT)
  expect(dateValue(await findDateField('Date', dialog))).toBe('2026-09-01') // still the saved day
})

it('a saved expense can be saved with its amount emptied while its new photo is still being read', async () => {
  const { ui, state } = setupExpenseEdit({ queuedPolls: 1000 })
  const dialog = await openEditExpense(ui)

  await ui.upload(camera(dialog), photo())
  expect(await within(dialog).findByText('Reading…')).toBeInTheDocument()
  await ui.clear(within(dialog).getByLabelText(/^Amount/))
  expect(within(dialog).getByText('Leave it empty: it is filled in from the photo after saving.')).toBeInTheDocument()
  await ui.click(within(dialog).getByRole('button', { name: 'Save changes' }))

  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(state.calls.UpdateExpense).toEqual([{ input: expect.objectContaining({ id: 'e1', amount: null }) }])
})

it('without a photo being read, an emptied amount of a saved expense is still required', async () => {
  const { ui, state } = setupExpenseEdit({ available: false })
  const dialog = await openEditExpense(ui)

  await ui.upload(camera(dialog), photo())
  await ui.clear(within(dialog).getByLabelText(/^Amount/))
  await ui.click(within(dialog).getByRole('button', { name: 'Save changes' }))

  expect(screen.getByRole('dialog', { name: 'Edit expense' })).toBeInTheDocument()
  expect(state.calls.UpdateExpense).toBeUndefined()
})

it('saving waits while a photo is still uploading, and says why', async () => {
  stubViewport('desktop')
  const photos = fakePhotoStore()
  const recognition = fakeRecognition({ available: false })
  const backend = fakeExpenseBackend(fakeVehicle(), [fakeExpense({ id: 'e1', title: 'Oil change' })], photos)
  let release!: () => void
  const held = new Promise<void>((resolve) => (release = resolve))
  server.use(
    sessionHandler('NONE', () => null),
    healthHandler,
    http.put('/media/vehicles/:vehicleId/photo-drafts', async () => {
      await held // the upload is slow
      return HttpResponse.json({ id: 'draft1', url: '/media/draft1' })
    }),
    ...backend.handlers,
    ...recognition.handlers,
  )
  renderWithApollo(<App />, '/vehicles/v1?tab=expenses')
  const ui = userEvent.setup()
  await screen.findByText('Oil change')
  await ui.click(screen.getByRole('button', { name: 'Add expense' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add expense' })
  await waitFor(() => expect(within(dialog).getByLabelText('Currency')).toHaveValue('HUF'))

  await ui.upload(camera(dialog), photo())

  expect(await within(dialog).findByText('Saving waits until the photos are uploaded.')).toBeInTheDocument()
  expect(within(dialog).getByRole('button', { name: 'Add expense' })).toBeDisabled()
  release()
  await waitFor(() => expect(within(dialog).queryByText('Saving waits until the photos are uploaded.')).not.toBeInTheDocument())
  expect(within(dialog).getByRole('button', { name: 'Add expense' })).toBeEnabled()
})

