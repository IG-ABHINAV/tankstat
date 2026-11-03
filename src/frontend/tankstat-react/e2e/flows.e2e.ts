import { readFileSync, statSync } from 'node:fs'
import { expect, test } from './support/test.ts'
import { ADMIN, addVehicle, pageLoaded, signIn } from './support/app.ts'
import { violations } from './support/axe.ts'
import { expectSoundLayout } from './support/layout.ts'

// A few flows across the real server, its sign-in and the routing, at both sizes.

test('signs in with the form and lands on the home page', async ({ page }) => {
  await page.goto('/')
  await page.getByLabel('E-mail').fill(ADMIN.email)
  await page.getByLabel('Password', { exact: true }).fill(ADMIN.password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await pageLoaded(page)
  await expect(page.getByRole('button', { name: 'Notifications' })).toBeVisible()
})

test('adds a vehicle and a refuelling', async ({ page, isMobile }) => {
  await signIn(page)
  const name = `Flow ${crypto.randomUUID().slice(0, 8)}`
  await page.goto('/')
  await pageLoaded(page)
  await page.getByRole('button', { name: 'Add vehicle' }).first().click()
  const vehicleDialog = page.getByRole('dialog', { name: 'Add vehicle' })
  await vehicleDialog.getByLabel('Name').fill(name)
  await vehicleDialog.getByRole('button', { name: 'Add vehicle' }).click()
  await expect(vehicleDialog).toBeHidden()

  // Found by its name: the home page lists a page of vehicles by name, and the other tests' vehicles may fill it. On a touch screen the
  // first tap on a card shows its figures ("Tap again to open"), the second opens the vehicle.
  await page.getByLabel('Search vehicles').fill(name)
  const card = page.getByRole('link', { name })
  await card.click()
  if (isMobile) await card.click()
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible()
  await page.getByRole('tab', { name: 'Refuelings' }).click()
  await page.getByRole('button', { name: 'Add refuelling' }).first().click()
  const logDialog = page.getByRole('dialog', { name: 'Add refuelling' })
  await logDialog.getByLabel(/^Volume/).fill('42.5')
  await logDialog.getByLabel('Total cost').fill('25000')
  await logDialog.getByLabel(/^Odometer/).fill('12345')
  await logDialog.getByRole('button', { name: 'Add refuelling' }).click()
  await expect(logDialog).toBeHidden()
  // The new row in the grid (the odometer column is hidden on a phone; the volume shows at both sizes).
  await expect(page.getByRole('row').filter({ hasText: '42.5 L' })).toBeVisible()
})

test('writes a backup from the Backups page and downloads it', async ({ page }) => {
  await signIn(page)
  // A vehicle of its own, so the backup has something in it whatever ran before.
  await addVehicle(page)
  await page.goto('/admin/backups')
  await pageLoaded(page)
  await page.getByRole('button', { name: 'Back up now' }).click()
  const written = page.getByText(/^The last backup was written/)
  await expect(written).toBeVisible({ timeout: 30_000 })

  // "The last backup was written <time>: <file>, <size>." The other size's run writes one too, so this one is found by its file.
  const file = /: (\S+), [^,]+\.$/.exec((await written.textContent()) ?? '')?.[1]
  expect(file, 'the file named in the last backup line').toBeTruthy()
  const escaped = file!.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  const download = page.waitForEvent('download')
  await page.getByRole('button', { name: new RegExp(`^Download the backup of .*\\(${escaped}\\)$`) }).click()
  expect((await download).suggestedFilename()).toBe(file)
  expect(statSync(await (await download).path()).size).toBeGreaterThan(0)

  // Its restore dialog says what it brings of the server's settings: the variables its environment set, none of them missing here.
  await page.getByRole('button', { name: new RegExp(`^Restore the backup of .*\\(${escaped}\\)$`) }).click()
  const restore = page.getByRole('alertdialog', { name: /^Restore the backup of/ })
  await expect(restore).toContainText("Its server's environment set")
  await expect(restore).toContainText('Auth__Oidc__Authority')
  // Never refused here: this server sets all of it. Whether it has a risk to accept depends on whether a test saved settings before.
  await expect(restore).not.toContainText('The restore will be refused')
  expect(await violations(page, '[role="alertdialog"]')).toEqual([])
  await restore.getByRole('button', { name: 'Cancel' }).click()
  await expect(restore).toBeHidden()
})

test('makes a key set: a typed passphrase comes twice, a made-up one is typed back before its keys can be used', async ({ page }) => {
  await signIn(page)
  await page.goto('/admin/backups')
  await pageLoaded(page)
  await page.getByRole('button', { name: 'Make a key set…' }).click()
  const dialog = page.getByRole('dialog', { name: 'A key set for encrypted backups' })
  const passphrase = dialog.getByLabel(/^Passphrase \(optional\)/)
  const repeat = dialog.getByLabel('Repeat the passphrase', { exact: true })

  await passphrase.fill('correct horse battery staple')
  await repeat.fill('correct horse battery stapel')
  await dialog.getByRole('button', { name: 'Make the keys' }).click()
  await expect(dialog.getByText('The two entries are not the same.')).toBeVisible()
  await expectSoundLayout(page)
  expect(await violations(page, '[role="dialog"]')).toEqual([])

  // Both left empty: one is made up, shown once, and typed back as proof it was written down.
  await passphrase.fill('')
  await repeat.fill('')
  await dialog.getByRole('button', { name: 'Make the keys' }).click()
  const madeUp = dialog.getByText(/^[A-HJ-NP-Z2-9]{4}(-[A-HJ-NP-Z2-9]{4}){4}$/)
  await expect(madeUp).toBeVisible({ timeout: 30_000 }) // Argon2id takes a moment
  const use = dialog.getByRole('button', { name: 'Use these keys for new backups' })
  await expect(use).toBeDisabled()
  await dialog.getByLabel('Type the passphrase back').fill((await madeUp.textContent())!)
  await expect(use).toBeEnabled()
  await expectSoundLayout(page)
  expect(await violations(page, '[role="dialog"]')).toEqual([])

  // Not used: the shared server's backups stay in plain, which the other tests' restores rely on.
  await dialog.getByRole('checkbox', { name: 'I have kept the passphrase in a safe place' }).check()
  await dialog.getByRole('button', { name: 'Done' }).click()
  await expect(dialog).toBeHidden()
})

test('holds a backup, which then cannot be deleted, opens it, and releases it', async ({ page }) => {
  await signIn(page)
  await page.goto('/admin/backups')
  await pageLoaded(page)
  await page.getByRole('button', { name: 'Back up now' }).click()
  const written = page.getByText(/^The last backup was written/)
  await expect(written).toBeVisible({ timeout: 30_000 })
  const file = /: (\S+), [^,]+\.$/.exec((await written.textContent()) ?? '')?.[1]
  expect(file, 'the file named in the last backup line').toBeTruthy()
  const escaped = file!.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  const row = page.getByRole('row').filter({ hasText: file! })

  await row.getByRole('button', { name: new RegExp(`^Hold the backup of .*\\(${escaped}\\)$`) }).click()
  await expect(page.getByText(`${file} is held: retention keeps it, and it counts towards no limit.`)).toBeVisible()
  await expect(row.getByRole('img', { name: /^Held since/ })).toBeVisible()
  await expect(row.getByRole('button', { name: /^Delete the backup of/ })).toHaveCount(0)
  await expect(row).toContainText('Held: release it to delete it')
  await expectSoundLayout(page)

  // Opened: what its manifest says, readable without its key.
  await row.getByRole('button', { name: new RegExp(`^Open the backup of .*\\(${escaped}\\)$`) }).click()
  const opened = page.getByRole('dialog', { name: /^The backup of/ })
  await expect(opened.getByRole('table', { name: /^Tables: \d+ · rows: / })).toBeVisible()
  await expect(opened.getByRole('rowheader', { name: 'Vehicles', exact: true })).toBeVisible()
  await expect(opened).toContainText(/Held since/)
  await expectSoundLayout(page)
  expect(await violations(page, '[role="dialog"]')).toEqual([])
  await opened.getByRole('button', { name: 'Close' }).click()
  await expect(opened).toBeHidden()

  // Released, as found: deletable again.
  await row.getByRole('button', { name: new RegExp(`^Release the backup of .*\\(${escaped}\\)$`) }).click()
  await expect(row.getByRole('button', { name: /^Delete the backup of/ })).toBeVisible()
  await expect(row.getByRole('img', { name: /^Held since/ })).toHaveCount(0)
})

test('says what an older backup can break in its row, and its restore waits until that is accepted', async ({ page }) => {
  await signIn(page)
  await page.goto('/admin/backups')
  await pageLoaded(page)
  // An older release's backup, which every release restores (the upgrade tests' own): no settings file, nothing of its environment.
  const chooser = page.waitForEvent('filechooser')
  await page.getByRole('button', { name: 'Upload a backup…' }).click()
  await (await chooser).setFiles({ name: 'older.zip', mimeType: 'application/zip', buffer: readFileSync('../../../tests/backend/contracts/backups/older-migration-AddExpenses.zip') })
  const uploaded = page.getByText(/^Uploaded as /)
  await expect(uploaded).toBeVisible()
  const file = /^Uploaded as (\S+)\.$/.exec((await uploaded.textContent()) ?? '')?.[1]
  expect(file, 'the name the upload got').toBeTruthy()

  // Said in its row before anything is opened, in words.
  const row = page.getByRole('row').filter({ hasText: file! })
  await expect(row).toContainText('Risk: No settings file (an older release wrote it)')
  await expectSoundLayout(page)

  await row.getByRole('button', { name: /^Restore the backup of/ }).click()
  const restore = page.getByRole('alertdialog', { name: /^Restore the backup of/ })
  await expect(restore).toContainText('What this restore can break here')
  const go = restore.getByRole('button', { name: 'Restore and restart' })
  await expect(go).toBeDisabled()
  await restore.getByRole('checkbox', { name: /^I understand that the restored server may not work as before/ }).check()
  await expect(go).toBeEnabled()
  expect(await violations(page, '[role="alertdialog"]')).toEqual([])
  await restore.getByRole('button', { name: 'Cancel' }).click() // the shared server is never restored
  await expect(restore).toBeHidden()
})

