import { request, type APIRequestContext, type Page } from '@playwright/test'
import { expect, test } from './support/test.ts'
import { ADMIN, addVehicle, graphql, pageLoaded, signIn, tag } from './support/app.ts'
import { violations } from './support/axe.ts'
import { expectSoundLayout } from './support/layout.ts'
import { waitingServer, type WaitingServer } from './support/setup.ts'

// The setup of a new instance (#90), at both sizes. Each test takes over a server of its own that waits for its setup (scripts/test-e2e.sh,
// support/setup.ts): finishing the setup, or restoring a backup into it, cannot be undone. Only the restore's backup comes from the shared
// server.

/** Opens a waiting server, where the setup page stands in for every page, and enters its one-time code: the wizard's start. */
async function enterCode(page: Page, server: WaitingServer) {
  await page.goto(server.url)
  await expect(page.getByRole('heading', { level: 1, name: 'Set up Tankstat' })).toBeVisible()
  await expectSoundLayout(page)
  expect(await violations(page)).toEqual([])
  await page.getByRole('textbox', { name: /^Setup code/ }).fill(server.code)
  await page.getByRole('button', { name: 'Continue' }).click()
  await expect(page.getByRole('button', { name: 'Start the wizard' })).toBeVisible()
  await expectSoundLayout(page)
}

/** The wizard's step at hand, by its label (the stepper's current step). */
const stepAt = (page: Page, name: string) => expect(page.locator('[aria-current="step"]')).toHaveText(name)

/** Signs in with the form, as a person does after the restart a new sign-in mode (or a restore) brings. */
async function signInWithTheForm(page: Page, user: { email: string; password: string }) {
  // The restart takes a few seconds, and the page waits for it before it asks.
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible({ timeout: 60_000 })
  await page.getByLabel('E-mail').fill(user.email)
  await page.getByLabel('Password', { exact: true }).fill(user.password)
  await page.getByRole('button', { name: 'Sign in' }).click()
}

test('a new instance is set up through the wizard: the sign-in mode, the first administrator, one restart, then the done screen', async ({
  page,
}) => {
  await enterCode(page, waitingServer('fresh'))
  await page.getByRole('button', { name: 'Start the wizard' }).click()
  await stepAt(page, 'Sign-in')

  // The mode must be chosen (none is preselected): said at its field.
  await page.getByRole('button', { name: 'Next' }).click()
  await expect(page.getByText('Choose one to go on.')).toBeVisible()
  await expectSoundLayout(page)
  expect(await violations(page)).toEqual([])
  await page.getByRole('combobox', { name: /^Sign-in mode/ }).click()
  await page.getByRole('option', { name: 'Local accounts (Standalone)' }).click()
  await page.getByRole('button', { name: 'Next' }).click()
  // Saved with the code; local accounts bring the e-mail's step. Every step opens by its label: straight on to the last one.
  await stepAt(page, 'E-mail')
  await page.getByRole('button', { name: 'Finish', exact: true }).click()
  await stepAt(page, 'Finish')

  const last = page.getByRole('region', { name: 'Finish' })
  const admin = { email: `admin-${tag()}@example.com`, password: `password-${tag()}` }
  await last.getByRole('textbox', { name: /^E-mail/ }).fill(admin.email)
  await last.getByLabel(/^Password/).fill(admin.password)
  await last.getByLabel(/^Repeat the password/).fill(admin.password)
  await expectSoundLayout(page)
  await last.getByRole('button', { name: 'Create the administrator' }).click()

  // Sign-in off ran, so the server restarts into local accounts, which signs everyone out: the new administrator signs in and finds
  // what to set up next.
  await signInWithTheForm(page, admin)
  await expect(page.getByText("The setup is done: you are this instance's administrator.")).toBeVisible()
  await expect(page.getByRole('link', { name: 'People: add the other users' })).toBeVisible()
  await expectSoundLayout(page)
})

test('an instance switched to local accounts outside the app, without an administrator, reopens its setup, which makes the first one', async ({
  page,
}) => {
  // Set up with sign-in off, then started in Standalone (scripts/test-e2e.sh): nobody can sign in, so the setup page stands in for the
  // sign-in again, the only way to the first administrator (#149).
  await enterCode(page, waitingServer('reopened'))
  await page.getByRole('button', { name: 'Start the wizard' }).click()
  await stepAt(page, 'Sign-in')
  // The environment chose the mode: shown, not offered.
  const signIn = page.getByRole('form', { name: 'Sign-in settings' })
  await expect(signIn.getByText('Local accounts (Standalone)')).toBeVisible()
  await expect(signIn.getByRole('combobox', { name: /^Sign-in mode/ })).toHaveCount(0)
  await page.getByRole('button', { name: 'Next' }).click()
  await stepAt(page, 'E-mail')
  await page.getByRole('button', { name: 'Finish', exact: true }).click()
  await stepAt(page, 'Finish')

  const last = page.getByRole('region', { name: 'Finish' })
  const admin = { email: `admin-${tag()}@example.com`, password: `password-${tag()}` }
  await last.getByRole('textbox', { name: /^E-mail/ }).fill(admin.email)
  await last.getByLabel(/^Password/).fill(admin.password)
  await last.getByLabel(/^Repeat the password/).fill(admin.password)
  await last.getByRole('button', { name: 'Create the administrator' }).click()

  // Local accounts run already: no restart, the new administrator is signed in at once and the setup is done.
  await expect(page.getByText("The setup is done: you are this instance's administrator.")).toBeVisible()
  await expectSoundLayout(page)
})

type BackupRun = { running: boolean; startedAt: string | null; file: string | null; error: string | null }

/** The notification setting each size saves on the shared server before its backup (a setting of its own, as the e2e rules ask), and its label. */
const MOVED_SETTING = { phone: ['Notifications:ReadRetentionDays', 'Keep read notifications (days)'], desktop: ['Notifications:MaxPerHour', 'Notifications per hour'] } as const

/**
 * A backup of the shared server that holds a vehicle made for it and, in its settings file, a setting saved for it, as its file: the
 * backup the restore test moves to its new server. The other tests write backups too, one at a time, so a run that started after both were
 * saved is waited for (and asked for again while the last one is older).
 */
async function backupOfTheSharedServer(api: APIRequestContext) {
  await signIn(api)
  const vehicle = await addVehicle(api, `Moved ${tag()}`)
  const [key, label] = MOVED_SETTING[test.info().project.name as keyof typeof MOVED_SETTING]
  const value = String(100 + Math.floor(Math.random() * 800))
  await graphql(api, 'mutation($c: [SystemSettingChangeInput!]!) { updateSystemSettings(changes: $c) { restartPending } }', { c: [{ key, value }] })
  const since = Date.now()
  let run: BackupRun | undefined
  await expect
    .poll(
      async () => {
        run = (await graphql<{ backups: { run: BackupRun } }>(api, '{ backups { run { running startedAt file error } } }')).backups.run
        if (run.running) return 'running'
        if (run.startedAt && Date.parse(run.startedAt) >= since) return run.error ?? 'written'
        await graphql(api, 'mutation { runBackup }')
        return 'asked'
      },
      { timeout: 60_000, intervals: [500] },
    )
    .toBe('written')
  const download = await api.get(`/media/backups/${encodeURIComponent(run!.file!)}`)
  expect(download.ok()).toBe(true)
  return { vehicle, setting: { label, value }, file: run!.file!, body: await download.body() }
}

test('a new instance is restored from a backup uploaded on the setup page, and its data and its settings are back', async ({ page }) => {
  const api = await request.newContext({ baseURL: test.info().project.use.baseURL })
  const backup = await backupOfTheSharedServer(api).finally(() => api.dispose())

  const server = waitingServer('restore')
  await enterCode(page, server)
  await page.getByRole('button', { name: 'Restore from a backup' }).click()
  await stepAt(page, 'Restore a backup')
  const restore = page.getByRole('region', { name: 'Restore a backup' })
  await expect(restore).toContainText('There is no backup in the server')

  // A backup of the old server, from this device: uploaded with the code, then listed to restore.
  const chooser = page.waitForEvent('filechooser')
  await restore.getByRole('button', { name: 'Upload a backup…' }).click()
  await (await chooser).setFiles({ name: backup.file, mimeType: 'application/zip', buffer: backup.body })
  const point = restore.getByRole('button', { name: /^Restore the backup of .*\(uploaded-[^)]+\.zip\)$/ })
  await expect(point).toBeVisible({ timeout: 30_000 })
  await expectSoundLayout(page)
  expect(await violations(page)).toEqual([])

  await point.click()
  const dialog = page.getByRole('alertdialog', { name: /^Restore the backup of/ })
  // The old server's environment is named, and this one sets the same (the same compose file): nothing missing, so nothing refuses it.
  await expect(dialog).toContainText("Its server's environment set")
  await expect(dialog.getByRole('alert')).toHaveCount(0)
  await dialog.getByRole('button', { name: 'Restore and restart' }).click()

  // The server restarts and restores the backup as it starts; the page reloads once it serves the restored data, which brings its own
  // administrator, and the vehicle made for the backup is there.
  await signInWithTheForm(page, ADMIN)
  await pageLoaded(page)
  await page.getByLabel('Search vehicles').fill(backup.vehicle.name)
  await expect(page.getByRole('link', { name: backup.vehicle.name })).toBeVisible()

  // The backup's settings file came back with it: the setting saved on the old server is this one's now.
  await page.goto(`${server.url}/admin/settings?tab=notifications`)
  await pageLoaded(page)
  const form = page.getByRole('form', { name: 'Notifications settings' })
  await expect(form.getByRole('textbox', { name: backup.setting.label, exact: true })).toHaveValue(backup.setting.value)
})
