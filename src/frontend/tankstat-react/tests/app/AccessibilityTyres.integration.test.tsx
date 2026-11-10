import { screen, within } from '@testing-library/react'
import { expect, it } from 'vitest'
import { check, setup, setupAccessibilityTests } from '../support/accessibility.tsx'
import { server } from '../support/server.ts'
import { fakeTyres, tyreSummer, tyreWinter } from '../support/tyres.ts'

// A vehicle's tyres (#287): the tab, its dialogs and the Trash page's tab.
setupAccessibilityTests()

const withTyres = (route: string) => {
  const opened = setup(route)
  server.use(...fakeTyres([tyreSummer, tyreWinter], [{ day: '2026-03-28', odometer: 141_000, note: null }]).handlers)
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
})
