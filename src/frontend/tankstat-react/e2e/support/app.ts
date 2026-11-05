import { expect, request, test, type APIRequestContext, type Locator, type Page } from '@playwright/test'

/** The first administrator, made by the setup scripts/test-e2e.sh finishes on the shared server. */
export const ADMIN = { email: 'root@example.com', password: 'initial-password-1' }

/** A short random tag that keeps a test's names apart from every other test's on the shared server. */
export const tag = () => crypto.randomUUID().slice(0, 8)

/**
 * One GraphQL request as the signed-in user of a page (`page.request` shares the context's cookies) or of a request context. Fails the
 * test on an error, so a test's own data is never silently missing.
 */
export async function graphql<T = Record<string, unknown>>(
  client: Page | APIRequestContext,
  query: string,
  variables?: Record<string, unknown>,
): Promise<T> {
  const api = 'request' in client ? client.request : client
  const response = await api.post('/graphql', { data: { query, variables } })
  const body = (await response.json()) as { data?: T; errors?: { message: string }[] }
  expect(body.errors ?? [], `GraphQL errors for ${query}`).toEqual([])
  return body.data as T
}

const LOGIN = 'mutation($i: LoginInput!) { login(input: $i) { id } }'

/**
 * Signs the page's context in (the administrator by default): the session cookies land in the context, so `page.goto` is signed in.
 * A request context of its own (`request.newContext`) is signed in the same way.
 */
export async function signIn(client: Page | APIRequestContext, user: { email: string; password: string } = ADMIN) {
  const data = await graphql<{ login: { id: string } }>(client, LOGIN, { i: { email: user.email, password: user.password } })
  expect(data.login.id).toBeTruthy()
}

export type User = { id: string; email: string; name: string; password: string }

/**
 * A user of the test's own, made by the administrator, with a password: what the user sees (their home page, trash, notifications,
 * language and colour mode, a failed sign-in's lockout) is then the test's alone. The page is not signed in; `signIn(page, user)` does it.
 */
export async function createUser({ admin = false } = {}): Promise<User> {
  const id = tag()
  const user = { email: `user-${id}@example.com`, name: `User ${id}`, password: `password-${id}` }
  const api = await request.newContext({ baseURL: test.info().project.use.baseURL })
  try {
    await graphql(api, LOGIN, { i: ADMIN })
    const created = await graphql<{ createUser: { user: { id: string }; reset: { token: string } } }>(
      api,
      'mutation($i: CreateUserInput!) { createUser(input: $i) { user { id } reset { token } } }',
      { i: { email: user.email, displayName: user.name, isAdmin: admin } },
    )
    // The password comes through the new user's link, as it does for a person (setting one directly is off by default).
    await graphql(api, 'mutation($i: ResetPasswordInput!) { resetPassword(input: $i) }', {
      i: { token: created.createUser.reset.token, newPassword: user.password, newPasswordConfirmation: user.password },
    })
    return { id: created.createUser.user.id, ...user }
  } finally {
    await api.dispose()
  }
}

/** A user of the test's own (see `createUser`), with the page signed in as them. */
export async function signInAsNewUser(page: Page, options?: { admin?: boolean }) {
  const user = await createUser(options)
  await signIn(page, user)
  return user
}

/**
 * A vehicle of the test's own. The suite shares one server and its workers run side by side, so a test makes what it needs and never
 * counts what others made.
 */
export async function addVehicle(client: Page | APIRequestContext, name = `Car ${tag()}`) {
  const data = await graphql<{ addVehicle: { id: string; name: string } }>(
    client,
    'mutation($i: AddVehicleInput!) { addVehicle(input: $i) { id name } }',
    { i: { name, licensePlate: 'E2E-001', fuelType: 'PETROL' } },
  )
  return data.addVehicle
}

/** A full-tank refuelling of a vehicle (the date as `yyyy-MM-dd`). */
export async function logRefueling(page: Page, vehicleId: string, { date = '2026-01-15', volume = 40, totalCost = 24000, odometer = 1000 } = {}) {
  const data = await graphql<{ logRefueling: { id: string } }>(page, 'mutation($i: LogRefuelingInput!) { logRefueling(input: $i) { id } }', {
    i: { vehicleId, date, volume, totalCost, currency: 'HUF', odometer, isFullTank: true },
  })
  return data.logRefueling
}

/** An expense of a vehicle. */
export async function addExpense(page: Page, vehicleId: string, { title = 'Car wash', date = '2026-01-20', amount = 3000, currency = 'HUF' } = {}) {
  const data = await graphql<{ addExpense: { id: string } }>(page, 'mutation($i: AddExpenseInput!) { addExpense(input: $i) { id } }', {
    i: { vehicleId, date, title, amount, currency },
  })
  return data.addExpense
}

/** A yearly recurring expense last done almost a year ago, so it is due soon (and on the home card, and in the notifications). */
export async function addDueRecurring(page: Page, vehicleId: string, title = 'Insurance', category: string | null = null) {
  const lastDone = new Date(Date.now() - 360 * 24 * 3600 * 1000).toISOString().slice(0, 10)
  const data = await graphql<{ addRecurringExpense: { id: string } }>(
    page,
    'mutation($i: AddRecurringExpenseInput!) { addRecurringExpense(input: $i) { id } }',
    { i: { vehicleId, title, category, kind: 'TIME', intervalMonths: 12, lastDoneDate: lastDone } },
  )
  return data.addRecurringExpense
}

/**
 * Waits until an element stands still: its box the same in two frames running (#236). A drag by `page.mouse` reads a box and presses
 * there, so it must not read one an animation is still moving (the dashboard's tiles move while they settle).
 */
export async function settled(target: Locator) {
  await expect
    .poll(() =>
      target.evaluate(
        (element) =>
          new Promise<boolean>((resolve) => {
            const box = () => JSON.stringify(element.getBoundingClientRect())
            const before = box()
            requestAnimationFrame(() => requestAnimationFrame(() => resolve(box() === before)))
          }),
      ),
    )
    .toBe(true)
}

/** Waits until a page has loaded: its heading is there and no loading message is left. */
export async function pageLoaded(page: Page) {
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
  await expect(page.getByText(/^Loading/)).toHaveCount(0)
}

/** Opens a vehicle's page on a tab (`refuelings`, `expenses`, `recurring`, `details`, `sharing`; the dashboard without one). */
export async function openVehicle(page: Page, vehicleId: string, tab?: string) {
  await page.goto(`/vehicles/${vehicleId}${tab ? `?tab=${tab}` : ''}`)
  await pageLoaded(page)
}

/** A small PNG picture drawn by the page's own canvas, for an upload (`setInputFiles`, or a file chooser). */
export async function picture(page: Page, name = 'picture.png') {
  const dataUrl = await page.evaluate(() => {
    const canvas = document.createElement('canvas')
    canvas.width = 64
    canvas.height = 48
    const context = canvas.getContext('2d')!
    context.fillStyle = '#b4562c'
    context.fillRect(0, 0, 64, 48)
    context.fillStyle = '#2c6cb4'
    context.fillRect(16, 12, 32, 24)
    return canvas.toDataURL('image/png')
  })
  return { name, mimeType: 'image/png', buffer: Buffer.from(dataUrl.split(',')[1], 'base64') }
}

/** The dashboard layout version a save sends (#273), as the web app's `LAYOUT_VERSION` (src/dashboard/widgets.ts): the server refuses a save without it. */
export const LAYOUT_VERSION = 3
