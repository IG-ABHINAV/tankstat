import { type Page } from '@playwright/test'
import { mkdirSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { graphql, openVehicle, pageLoaded } from './support/app.ts'
// No service worker (support/test.ts), so no "can now be used offline" toast on its own time: main.tsx registers one only where the browser has it.
import { expect, test } from './support/test.ts'

// The README's screenshots, one file per page and project (`home-desktop-dark.png`), see playwright.screenshots.config.ts: the tag
// `@light` puts a page in the light scheme too, `@phone-only` keeps a dialog off the desktop. The server runs the seeder's data with
// sign-in off, so nothing signs in and the first vehicle by name is the one shown.
const folder = join(import.meta.dirname, '../../../../docs/screenshots')
mkdirSync(folder, { recursive: true })

// Hidden in every frame, never taken out of the page (React owns these nodes): the notice that sign-in is off (the server's, not the
// app's) with its aside, whose margin would push the page down, and any toast.
const HIDDEN = 'aside:has(> [role="note"][data-severity]), .MuiSnackbar-root { display: none !important }'

/** Waits until two frames taken an interval apart are the same (the charts and the cards animate in), then saves that frame. */
async function capture(page: Page, name: string) {
  let last: Buffer | undefined
  await expect
    .poll(
      async () => {
        // The first call only keeps its frame: it runs at once, so it is compared with the next one, an interval later.
        const next = await page.screenshot({ style: HIDDEN })
        const same = last !== undefined && next.equals(last)
        last = next
        return same
      },
      { intervals: [400], timeout: 15_000 },
    )
    .toBe(true)
  writeFileSync(join(folder, `${name}-${test.info().project.name}.png`), last!)
}

async function firstVehicle(page: Page) {
  const data = await graphql<{ myVehicles: { id: string; name: string }[] }>(page, 'query { myVehicles(take: 1) { id name } }')
  expect(data.myVehicles, 'the seeded vehicles').not.toHaveLength(0)
  return data.myVehicles[0]
}

test('home', { tag: '@light' }, async ({ page }) => {
  const vehicle = await firstVehicle(page)
  await page.goto('/')
  await pageLoaded(page)
  await page.getByRole('link', { name: vehicle.name }).first().waitFor()
  await capture(page, 'home')
})

test('dashboard', { tag: '@light' }, async ({ page }) => {
  const vehicle = await firstVehicle(page)
  await openVehicle(page, vehicle.id)
  await expect(page.locator('figure svg').first()).toBeVisible()
  await capture(page, 'dashboard')
})

test('refuelings', async ({ page }) => {
  const vehicle = await firstVehicle(page)
  await openVehicle(page, vehicle.id, 'refuelings')
  await expect(page.getByRole('row').nth(2)).toBeVisible()
  await capture(page, 'refuelings')
})

test('add refuelling', { tag: '@phone-only' }, async ({ page }) => {
  const vehicle = await firstVehicle(page)
  await openVehicle(page, vehicle.id, 'refuelings')
  await page.getByRole('button', { name: 'Add refuelling' }).first().click()
  const dialog = page.getByRole('dialog', { name: 'Add refuelling' })
  await expect(dialog.getByRole('textbox', { name: /^Odometer/ })).toBeVisible()
  await expect(dialog.getByText(/^Loading/)).toHaveCount(0)
  await capture(page, 'add-refuelling')
})

test('recurring', async ({ page }) => {
  const vehicle = await firstVehicle(page)
  await openVehicle(page, vehicle.id, 'recurring')
  // The schedules come in a query of their own after the tab: until then it shows none, as still as a loaded page.
  await expect(page.getByRole('button', { name: /^Mark .* as done$/, disabled: false }).first()).toBeVisible()
  await capture(page, 'recurring')
})

test('mark as done', { tag: '@phone-only' }, async ({ page }) => {
  const vehicle = await firstVehicle(page)
  await openVehicle(page, vehicle.id, 'recurring')
  // The first schedule's Done button (not the disabled "Mark 0 selected as done" above the table): the dialog ticks it and every
  // other due one of its category.
  await page.getByRole('button', { name: /^Mark .* as done$/, disabled: false }).first().click()
  const dialog = page.getByRole('dialog', { name: /^Mark as done/ })
  await expect(dialog.getByRole('checkbox').first()).toBeVisible()
  await expect(dialog.getByText(/^Loading/)).toHaveCount(0)
  await capture(page, 'mark-as-done')
})
