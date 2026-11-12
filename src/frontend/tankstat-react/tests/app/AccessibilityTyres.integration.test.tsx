import { screen, within } from '@testing-library/react'
import { expect, it } from 'vitest'
import { check, setup, setupAccessibilityTests } from '../support/accessibility.tsx'
import { server } from '../support/server.ts'
import { fakeTyres, tyreSummer, tyreWinter } from '../support/tyres.ts'

// A vehicle's tyres (#287): the tab, its dialogs and the tyre log's (#288).
setupAccessibilityTests()

const withTyres = (route: string) => {
  const opened = setup(route)
  server.use(
    ...fakeTyres([tyreSummer, tyreWinter], [{ id: 'e1', kind: 'STORAGE_FEE', setId: null, day: '2025-06-01', odometer: 133_000, note: 'Stored', expenseId: 'x1', amount: 9000, currency: 'HUF', category: 'Tyres' }], { r1: { odometer: 141_000 } }).handlers,
  )
  return opened
}

it('the Tyres tab and its swap dialog are labelled and free of violations', async () => {
  const { ui } = withTyres('/vehicles/v1?tab=tyres')
  await screen.findByRole('article', { name: 'Michelin Primacy 4' })
  await check(document.body)

  await ui.click(screen.getByRole('button', { name: 'Swap tyres' }))
  const swap = await screen.findByRole('dialog', { name: 'Swap tyres' })
  expect(within(swap).getByRole('radiogroup', { name: 'Where each tyre goes' })).toBeInTheDocument()
  await check(document.body)
})

it('the add set, past swaps and history dialogs are labelled and free of violations', async () => {
  const { ui } = withTyres('/vehicles/v1?tab=tyres')
  await screen.findByRole('article', { name: 'Michelin Primacy 4' })

  await ui.click(screen.getByRole('button', { name: 'Add a tyre set' }))
  await screen.findByRole('dialog', { name: 'Add a tyre set' })
  await check(document.body)
  await ui.click(within(screen.getByRole('dialog', { name: 'Add a tyre set' })).getByRole('button', { name: 'Cancel' }))

  await ui.click(screen.getByRole('button', { name: 'Add past swaps' }))
  await screen.findByRole('dialog', { name: 'Add past swaps' })
  await check(document.body)
  await ui.click(within(screen.getByRole('dialog', { name: 'Add past swaps' })).getByRole('button', { name: 'Cancel' }))

  await ui.click(within(screen.getByRole('article', { name: 'Michelin Primacy 4' })).getByRole('button', { name: 'History' }))
  await screen.findByRole('dialog', { name: 'Michelin Primacy 4' })
  await check(document.body)

  await ui.click(within(screen.getByRole('dialog', { name: 'Michelin Primacy 4' })).getByRole('button', { name: 'Edit set' }))
  await screen.findByRole('dialog', { name: 'Edit tyre set' })
  await check(document.body)
})

it('the tyre log’s entry dialogs and the remove dialog are labelled and free of violations (#288)', async () => {
  const { ui } = withTyres('/vehicles/v1?tab=tyres')
  await screen.findByRole('article', { name: 'Michelin Primacy 4' })

  await ui.click(screen.getByRole('button', { name: 'Add entry' }))
  const add = await screen.findByRole('dialog', { name: 'Add entry' })
  expect(within(add).getByRole('combobox', { name: 'What was done' })).toBeInTheDocument()
  await check(document.body)
  await ui.click(within(add).getByRole('button', { name: 'Cancel' }))

  await ui.click(screen.getByRole('button', { name: 'Tyre swap, Michelin Primacy 4, Mar 28, 2026' }))
  const edit = await screen.findByRole('dialog', { name: 'Edit entry' })
  await check(document.body) // the kind and the set disabled, with why in their hints
  await ui.click(within(edit).getByRole('button', { name: 'Cancel' }))

  await ui.click(screen.getByRole('button', { name: 'Move to the trash: Storage fee, Jun 1, 2025' }))
  const confirm = await screen.findByRole('alertdialog', { name: 'Move to the trash?' })
  expect(within(confirm).getByRole('checkbox', { name: /^Also move its expense/ })).toBeChecked() // #288: the box, labelled
  await check(document.body)
})
