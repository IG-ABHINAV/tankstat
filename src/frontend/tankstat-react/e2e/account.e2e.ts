import { type Page } from '@playwright/test'
import { expect, test, newContext } from './support/test.ts'
import { createUser, pageLoaded, picture, signIn, signInAsNewUser, type User } from './support/app.ts'
import { violations } from './support/axe.ts'
import { RAISED_MINIMUM } from './support/global-setup.ts'
import { expectSoundLayout } from './support/layout.ts'

// What a person does with their own account: signing in and out, the password, other devices, the language, the colour mode and the
// profile picture. A user of the test's own each, so a lockout or a language never reaches another test.

/** Signs in through the form on the sign-in page. */
async function signInWithForm(page: Page, user: { email: string; password: string }) {
  await page.getByLabel('E-mail').fill(user.email)
  await page.getByLabel('Password', { exact: true }).fill(user.password)
  await page.getByRole('button', { name: 'Sign in' }).click()
}

/** On a phone the navigation (with Sign out, the language and the colour mode) is behind the menu button. */
async function openNavigation(page: Page, isMobile: boolean) {
  if (isMobile) await page.getByRole('button', { name: 'Show menu' }).click()
}

test('refuses a wrong password and signs in with the right one', async ({ page }) => {
  const user = await createUser()
  await page.goto('/')
  await signInWithForm(page, { email: user.email, password: 'not-the-password' })
  await expect(page.getByText('Invalid e-mail or password, or the account is temporarily locked.')).toBeVisible()
  await signInWithForm(page, user)
  await pageLoaded(page)
  await expect(page.getByRole('heading', { level: 1, name: 'Your vehicles' })).toBeVisible()
})

test('signs out', async ({ page, isMobile }) => {
  await signInAsNewUser(page)
  await page.goto('/')
  await pageLoaded(page)
  await openNavigation(page, isMobile)
  await page.getByRole('button', { name: 'Sign out', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible()
  await page.reload()
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible()
})

test('changes the password and signs in with the new one', async ({ page, isMobile }) => {
  const user: User = await signInAsNewUser(page)
  await page.goto('/account')
  await pageLoaded(page)
  const newPassword = `${user.password}-new`
  await page.getByLabel('Current password').fill(user.password)
  await page.getByLabel('New password', { exact: true }).fill(newPassword)
  await page.getByLabel('Repeat the new password').fill(newPassword)
  await page.getByRole('button', { name: 'Update password' }).click()
  await expect(page.getByText('Password changed. Your other sessions were signed out.')).toBeVisible()

  await openNavigation(page, isMobile)
  await page.getByRole('button', { name: 'Sign out', exact: true }).click()
  await signInWithForm(page, { email: user.email, password: user.password })
  await expect(page.getByText('Invalid e-mail or password, or the account is temporarily locked.')).toBeVisible()
  await signInWithForm(page, { email: user.email, password: newPassword })
  await pageLoaded(page)
})

test('a password shorter than the minimum now signs in, and only its change is shown until it is changed', async ({ page }, info) => {
  // Made by the global setup with 10 characters, before the shared server's minimum went up to RAISED_MINIMUM.
  const user = (JSON.parse(process.env.E2E_SHORT_PASSWORDS ?? '{}') as Record<string, { email: string; password: string }>)[info.project.name]
  expect(user, 'the global setup made a user for this project').toBeDefined()
  await page.goto('/')
  await signInWithForm(page, user)

  const heading = page.getByRole('heading', { name: 'Choose a new password' })
  await expect(heading).toBeVisible()
  await expect(page.getByRole('navigation', { name: 'Main navigation' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Show menu' })).toHaveCount(0)
  await expect(page.getByText(`At least ${RAISED_MINIMUM} characters.`)).toBeVisible()
  await expectSoundLayout(page)
  expect(await violations(page)).toEqual([])
  await page.goto('/trash') // no page past it
  await expect(heading).toBeVisible()

  const newPassword = `${user.password}-long-enough`
  await page.getByLabel('Current password').fill(user.password)
  await page.getByLabel('New password', { exact: true }).fill(newPassword)
  await page.getByLabel('Repeat the new password').fill(newPassword)
  await page.getByRole('button', { name: 'Update password' }).click()
  await expect(page.getByText('Password changed. Your other sessions were signed out.')).toBeVisible()
  await pageLoaded(page) // the trash, asked for before
  await expect(heading).toHaveCount(0)
})

test('signs another device out from the account page', async ({ page, browser }) => {
  const user = await signInAsNewUser(page)
  const other = await newContext(browser)
  const otherPage = await other.newPage()
  try {
    await signIn(otherPage, user)
    await otherPage.goto('/')
    await pageLoaded(otherPage)

    await page.goto('/account')
    await pageLoaded(page)
    const devices = page.getByRole('region', { name: 'Signed-in devices' })
    const others = devices.getByRole('listitem').filter({ hasNotText: 'This device' })
    await expect(others).toHaveCount(1)
    await others.getByRole('button', { name: /^Sign out of/ }).click()
    await expect(others).toHaveCount(0)

    await otherPage.reload()
    await expect(otherPage.getByRole('button', { name: 'Sign in' })).toBeVisible()
  } finally {
    await other.close()
  }
})

test('switches the language to Hungarian and keeps it with the account', async ({ page, isMobile, browser }) => {
  const user = await signInAsNewUser(page)
  await page.goto('/')
  await pageLoaded(page)
  await openNavigation(page, isMobile)
  await page.getByRole('button', { name: 'Language and formats' }).click()
  await page.getByRole('menuitemradio', { name: 'Magyar' }).click()
  await expect(page.locator('html')).toHaveAttribute('lang', /^hu/)
  await expect(page.getByRole('heading', { level: 1, name: 'Your vehicles' })).toHaveCount(0)

  // Another device of the same account (a browser that never chose) opens in Hungarian too.
  const other = await newContext(browser)
  const otherPage = await other.newPage()
  try {
    await signIn(otherPage, user)
    await otherPage.goto('/')
    await expect(otherPage.locator('html')).toHaveAttribute('lang', /^hu/)
  } finally {
    await other.close()
  }
})

test('switches to the light colour mode and keeps it after a reload', async ({ page, isMobile }) => {
  await signInAsNewUser(page)
  await page.goto('/')
  await pageLoaded(page)
  await openNavigation(page, isMobile)
  await page.getByRole('button', { name: 'Appearance' }).click()
  await page.getByRole('menuitemradio', { name: 'Colour mode: Light' }).click()
  await expect(page.locator('html')).toHaveClass(/\blight\b/)
  await page.reload()
  await pageLoaded(page)
  await expect(page.locator('html')).toHaveClass(/\blight\b/)
})

test('sets a profile picture', async ({ page }) => {
  await signInAsNewUser(page)
  await page.goto('/account')
  await pageLoaded(page)
  const region = page.getByRole('region', { name: 'Profile picture' })
  const chooser = page.waitForEvent('filechooser')
  await region.getByRole('button', { name: 'Choose a picture' }).click()
  await (await chooser).setFiles(await picture(page, 'me.png'))
  await expect(region.getByRole('button', { name: 'Change picture' })).toBeVisible()
  await expect(region.locator('img')).toBeVisible()
})

test('asks for a reset link with the forgotten password form', async ({ page }) => {
  const user = await createUser()
  await page.goto('/')
  await page.getByRole('link', { name: 'Forgot password?' }).or(page.getByRole('button', { name: 'Forgot password?' })).click()
  await expect(page.getByRole('heading', { name: 'Forgot your password?' })).toBeVisible()
  await page.getByLabel('E-mail').fill(user.email)
  await page.getByRole('button', { name: 'Send reset link' }).click()
  // The same answer whether the address is known or not; this server sends no e-mail.
  await expect(page.getByText(/^If that address is registered and the server can send e-mail/)).toBeVisible()
  await page.getByRole('button', { name: 'Back to sign in' }).click()
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible()
})
