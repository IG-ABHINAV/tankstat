import { type Page } from '@playwright/test'
import { expect, test, newContext } from './support/test.ts'
import { addVehicle, createUser, pageLoaded, signIn, signInAsNewUser, tag } from './support/app.ts'

// What an administrator does with people: a new user with the one-time link (and the same user created twice at once), disabling someone, a grant on someone else's data, and
// the list of every vehicle. The administrator is the shared server's first one; the people are the test's own.

test('creates a user, who sets a password through the link and signs in', async ({ page, browser }) => {
  await signIn(page)
  await page.goto('/admin/people')
  await pageLoaded(page)
  const id = tag()
  const email = `new-${id}@example.com`
  await page.getByLabel('E-mail').fill(email)
  await page.getByLabel('Name').fill(`New ${id}`)
  await page.getByRole('button', { name: 'Create user' }).click()
  const link = page.getByRole('status').getByRole('code')
  await expect(link).toContainText('resetToken=')
  await expect(page.getByRole('rowheader', { name: new RegExp(`New ${id}`) })).toBeVisible()
  const url = (await link.textContent())!.trim()

  // The new user, in a browser of their own.
  const theirs = await newContext(browser)
  const them = await theirs.newPage()
  try {
    await them.goto(url)
    await expect(them.getByRole('heading', { name: 'Set your password' })).toBeVisible()
    await them.getByLabel('New password', { exact: true }).fill(`chosen-${id}`)
    await them.getByLabel('Repeat the new password').fill(`chosen-${id}`)
    await them.getByRole('button', { name: 'Set password' }).click()
    await expect(them.getByText('Your password has been set.')).toBeVisible()
    await them.getByRole('button', { name: 'Continue to sign in' }).or(them.getByRole('link', { name: 'Continue to sign in' })).click()
    await them.getByLabel('E-mail').fill(email)
    await them.getByLabel('Password', { exact: true }).fill(`chosen-${id}`)
    await them.getByRole('button', { name: 'Sign in' }).click()
    await pageLoaded(them)
    await expect(them.getByRole('heading', { level: 1, name: 'Your vehicles' })).toBeVisible()
  } finally {
    await theirs.close()
  }
})

test('creating the same user twice at once creates one, and the others are told the address is taken', async ({ page }) => {
  await signIn(page)
  const email = `twice-${tag()}@example.com`
  // Sent with page.request, not the graphql helper: that one fails the test on any error, and the answers of the losers are the point.
  const create = async () => {
    const response = await page.request.post('/graphql', {
      data: { query: 'mutation($i: CreateUserInput!) { createUser(input: $i) { user { id } } }', variables: { i: { email, displayName: 'Twice', isAdmin: false } } },
    })
    return (await response.json()) as { errors?: { extensions: { key?: string } }[] }
  }
  const answers = await Promise.all([create(), create(), create(), create()])

  expect(answers.filter((a) => !a.errors)).toHaveLength(1)
  expect(answers.filter((a) => a.errors).map((a) => a.errors![0].extensions.key)).toEqual(['user.emailExists', 'user.emailExists', 'user.emailExists'])
  await page.goto('/admin/people')
  await pageLoaded(page)
  await expect(page.getByRole('cell', { name: email, exact: true })).toHaveCount(1)
})

test('disables a user, who can no longer sign in', async ({ page, browser }) => {
  const user = await createUser()
  await signIn(page)
  await page.goto('/admin/people')
  await pageLoaded(page)
  await page.getByRole('checkbox', { name: `Disabled: ${user.name}` }).click()
  await expect(page.getByRole('checkbox', { name: `Disabled: ${user.name}` })).toBeChecked()

  const theirs = await newContext(browser)
  const them = await theirs.newPage()
  try {
    await them.goto('/')
    await them.getByLabel('E-mail').fill(user.email)
    await them.getByLabel('Password', { exact: true }).fill(user.password)
    await them.getByRole('button', { name: 'Sign in' }).click()
    await expect(them.getByText('Invalid e-mail or password, or the account is temporarily locked.')).toBeVisible()
  } finally {
    await theirs.close()
  }
})

/** Picks an option of a select by its visible name (the options open in a list of their own). */
async function choose(page: Page, label: string, option: string) {
  await page.getByRole('combobox', { name: label }).click()
  await page.getByRole('option', { name: option }).click()
}

test("grants a user view access to another user's data", async ({ page, browser }) => {
  // The owner and their vehicle.
  const ownerContext = await newContext(browser)
  const ownerPage = await ownerContext.newPage()
  const owner = await signInAsNewUser(ownerPage)
  const vehicle = await addVehicle(ownerPage)
  await ownerContext.close()
  const grantee = await createUser()

  await signIn(page)
  await page.goto('/admin/people')
  await pageLoaded(page)
  await page.getByRole('tab', { name: 'Access' }).click()
  await choose(page, 'Grant to', grantee.name)
  await choose(page, 'Data owner', owner.name)
  await page.getByRole('button', { name: 'Grant access' }).click()
  await expect(page.getByText(`${grantee.name} may view the data of ${owner.name}`)).toBeVisible()

  const theirs = await newContext(browser)
  const them = await theirs.newPage()
  try {
    await signIn(them, grantee)
    await them.goto(`/vehicles/${vehicle.id}`)
    await pageLoaded(them)
    await expect(them.getByRole('heading', { level: 1, name: vehicle.name })).toBeVisible()
  } finally {
    await theirs.close()
  }
})

test('opens a vehicle from the list of every vehicle', async ({ page }) => {
  await signIn(page)
  // The list is sorted by name and other tests' vehicles fill its pages: a name starting with a digit comes first.
  const vehicle = await addVehicle(page, `0 Fleet ${tag()}`)
  await page.goto('/admin/vehicles')
  await pageLoaded(page)
  await page.getByRole('link', { name: `Open ${vehicle.name}` }).click()
  await expect(page.getByRole('heading', { level: 1, name: vehicle.name })).toBeVisible()
})
