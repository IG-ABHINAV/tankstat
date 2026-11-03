import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Circle, Square } from 'lucide-react'
import { expect, it, vi } from 'vitest'
import { Segmented, SegmentedChecks } from '../../src/components/Segmented.tsx'
import { ThemeRoot } from '../../src/theme/ThemeRoot.tsx'

it('names an icon segment by its words, hidden beside the icon, and says why one cannot be chosen', async () => {
  const ui = userEvent.setup()
  const onChange = vi.fn()
  render(
    <ThemeRoot instant>
      <Segmented
        label="Shape"
        value="circle"
        onChange={onChange}
        options={[
          { value: 'circle', label: 'Circle', icon: <Circle size={16} aria-hidden /> },
          { value: 'square', label: 'Square', icon: <Square size={16} aria-hidden />, disabled: true, reason: 'Squares are for later.' },
        ]}
      />
    </ThemeRoot>,
  )

  const group = screen.getByRole('radiogroup', { name: 'Shape' })
  expect(within(group).getByRole('radio', { name: 'Circle' })).toBeChecked()
  const square = within(group).getByRole('radio', { name: 'Square' })
  expect(square).toBeDisabled()
  expect(within(group).queryByText('Circle', { ignore: 'span[style]' })).toBeNull() // drawn as its icon, named for assistive technology

  await ui.hover(square.closest('label')!)
  expect(await screen.findByRole('tooltip')).toHaveTextContent('Squares are for later.')
  await ui.click(square.closest('label')!)
  expect(onChange).not.toHaveBeenCalled()
})

it('is a group of checkboxes for things that can be on together', async () => {
  const ui = userEvent.setup()
  const onChange = vi.fn()
  render(
    <ThemeRoot instant>
      <SegmentedChecks label="Also" values={['a']} onChange={onChange} options={[{ value: 'a', label: 'Alpha' }, { value: 'b', label: 'Beta' }]} />
    </ThemeRoot>,
  )

  const group = screen.getByRole('group', { name: 'Also' })
  expect(within(group).getByRole('checkbox', { name: 'Alpha' })).toBeChecked()
  await ui.click(within(group).getByRole('checkbox', { name: 'Beta' }))
  expect(onChange).toHaveBeenCalledWith('b', true)
})
