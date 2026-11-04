import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { expect, it } from 'vitest'
import { axe } from 'vitest-axe'
import type { ChangeWords } from '../../../src/dashboard/changeWords.ts'
import { KpiCard, MoreCount } from '../../../src/dashboard/Kpi.tsx'
import { FULL, type Fit } from '../../../src/dashboard/kpiFit.ts'
import { renderWithApollo } from '../../support/mocks.tsx'

// A key figure's card in each form the fit can end on (#259): jsdom lays nothing out, so `Kpi` itself always shows the full form here,
// and the forms are drawn as `KpiCard` with the fit given. The measuring is the real browser's (dashboard.e2e.ts).

const form = (change: Partial<Fit>): Fit => ({ ...FULL, ...change })
const spent = (fit: Fit) => (
  <KpiCard
    rows={2}
    label="Spent this month"
    value="HUF 22,000 · €40.00"
    shrink
    short={
      <>
        HUF 22,000
        <MoreCount others={['€40.00']} />
      </>
    }
    hint="Last month: HUF 23,300"
    fit={fit}
  />
)

it('shows the main currency and "+1" in the short form, the others in its tooltip and its name, and every amount otherwise', async () => {
  const ui = userEvent.setup()
  const { container, rerender } = renderWithApollo(spent(form({ oneLine: true, short: true })))

  const more = screen.getByRole('button', { name: 'and 1 more: €40.00' })
  expect(more).toHaveTextContent('+1')
  expect(screen.getByText(/^HUF 22,000/).closest('p')).toHaveTextContent('HUF 22,000+1') // the main amount, whole
  await ui.tab()
  expect(more).toHaveFocus()
  expect(await screen.findByRole('tooltip')).toHaveTextContent('€40.00')
  expect((await axe(container, { rules: { 'color-contrast': { enabled: false } } })).violations).toEqual([])

  rerender(spent(form({ step: 1 })))
  expect(screen.queryByRole('button', { name: /more/ })).not.toBeInTheDocument()
  expect(screen.getByText('HUF 22,000 · €40.00')).toHaveClass('MuiTypography-h4') // one type step smaller
  rerender(spent(FULL))
  expect(screen.getByText('HUF 22,000 · €40.00')).toHaveClass('MuiTypography-h3')
  // The size steps (#260) are the headings down to h6 and then 14 px on it (jsdom computes no sizes: the browser's tests read them).
  rerender(spent(form({ oneLine: true, step: 3 })))
  expect(screen.getByText('HUF 22,000 · €40.00')).toHaveClass('MuiTypography-h6')
})

it('makes a cut label a tab stop whose tooltip shows it whole, and leaves a label that fits out of the tab order', async () => {
  const ui = userEvent.setup()
  const card = (fit: Fit) => <KpiCard rows={2} label="Költség távolságra, változás" chip={<span>12 teljes hónap</span>} value="4293 Ft" fit={fit} />
  const { container, rerender } = renderWithApollo(card(form({ oneLine: true, labelCut: true })))

  const label = screen.getByText('Költség távolságra, változás')
  expect(label).toHaveAttribute('tabindex', '0')
  await ui.tab()
  expect(label).toHaveFocus()
  expect(await screen.findByRole('tooltip')).toHaveTextContent('Költség távolságra, változás')
  expect((await axe(container, { rules: { 'color-contrast': { enabled: false } } })).violations).toEqual([])

  rerender(card(form({ oneLine: true })))
  expect(screen.getByText('Költség távolságra, változás')).not.toHaveAttribute('tabindex')
})

const less: ChangeWords = { sentence: 'uses 7% less fuel than the 10 fill-ups before', icon: 'down', tone: 'good' }
const trend = (rows: number, fit: Fit, change: ChangeWords = less) => (
  <KpiCard rows={rows} label="Consumption change" value="6.83" hint="last 10 fill-ups" change={change} fit={fit} />
)

it('shows a comparison with no row of its own as its arrow alone before the value, the sentence its name and tooltip (#260)', async () => {
  const ui = userEvent.setup()
  const { container, rerender } = renderWithApollo(trend(2, FULL)) // two rows: no row for the sentence

  const arrow = screen.getByRole('button', { name: 'uses 7% less fuel than the 10 fill-ups before' })
  expect(arrow.closest('[data-kpi="value-row"]')).toContainElement(screen.getByText('6.83')) // in the value's row, before it
  expect(arrow.compareDocumentPosition(screen.getByText('6.83')) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  expect(arrow.querySelector('[data-tone]')).toHaveAttribute('data-tone', 'good') // its tone as the line's, never alone: the arrow points down
  expect(within(container).queryByText('uses 7% less fuel than the 10 fill-ups before')).not.toBeInTheDocument() // no line
  await ui.tab()
  expect(arrow).toHaveFocus()
  expect(await screen.findByRole('tooltip')).toHaveTextContent('uses 7% less fuel than the 10 fill-ups before')
  expect((await axe(container, { rules: { 'color-contrast': { enabled: false } } })).violations).toEqual([])

  // Nothing to compare with yet: a grey dash, the same way.
  rerender(trend(2, FULL, { sentence: 'needs 3 earlier fill-ups to compare with', icon: 'same', tone: 'same' }))
  expect(screen.getByRole('button', { name: 'needs 3 earlier fill-ups to compare with' }).querySelector('[data-tone]')).toHaveAttribute('data-tone', 'same')
})

it('turns a comparison line with no room at three rows into its arrow, and the note does not take its row', () => {
  const { container, rerender } = renderWithApollo(trend(3, FULL))
  expect(within(container).getByText('uses 7% less fuel than the 10 fill-ups before')).toBeInTheDocument() // the line, with room
  expect(screen.queryByRole('button')).not.toBeInTheDocument()
  expect(within(container).queryByText('last 10 fill-ups')).not.toBeInTheDocument() // three rows: the comparison, not the note (#229)

  rerender(trend(3, form({ drop: 3 })))
  expect(within(container).queryByText('uses 7% less fuel than the 10 fill-ups before')).not.toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'uses 7% less fuel than the 10 fill-ups before' })).toBeInTheDocument()
  expect(within(container).queryByText('last 10 fill-ups')).not.toBeInTheDocument() // its row stays empty (#259 N2, kept in #260)
  expect(within(container).getByText('6.83')).toBeInTheDocument()
})
