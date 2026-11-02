import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { CombinedGraphQLErrors } from '@apollo/client/errors'
import { expect, it, vi } from 'vitest'
import { Field, FieldMessage } from '../../src/forms/Field.tsx'
import { FieldDate } from '../../src/forms/FieldDate.tsx'
import { FieldInput } from '../../src/forms/FieldInput.tsx'
import { Form } from '../../src/forms/Form.tsx'
import { ErrorMessage } from '../../src/messages.tsx'
import { ThemeRoot } from '../../src/theme/ThemeRoot.tsx'
import { dateSection, dateValue, findDateField, typeDate } from '../support/dates.ts'

/** A form with a required amount (a number check and a hint), an e-mail and a date; what it sends goes to `sent`. */
function setup({ dateRequired = false, disableFuture = false } = {}) {
  const sent = vi.fn()
  function Demo() {
    const [note, setNote] = useState(false)
    return (
      <Form onSubmit={(e) => { e.preventDefault(); sent(Object.fromEntries(new FormData(e.currentTarget))) }}>
        <Field
          name="amount"
          label="Amount"
          hint="What the receipt says."
          required
          invalid={{ message: 'Enter a number.', test: (v) => v !== '' && Number.isNaN(Number(v)) }}
          extra={note && <FieldMessage>Read from the photo.</FieldMessage>}
        >
          <FieldInput />
        </Field>
        <Field name="email" label="E-mail" typeMismatch="Enter a valid e-mail address">
          <FieldInput type="email" />
        </Field>
        <Field name="day" label="Day" required={dateRequired}>
          <FieldDate disableFuture={disableFuture} />
        </Field>
        <button type="button" onClick={() => setNote(true)}>
          Read a photo
        </button>
        <button type="submit">Save</button>
      </Form>
    )
  }
  render(
    <ThemeRoot instant>
      <Demo />
    </ThemeRoot>,
  )
  return { ui: userEvent.setup(), sent }
}

it('says a missing value only after a submit, blocks it and takes the focus there', async () => {
  const { ui, sent } = setup()
  expect(screen.queryByText('Amount is required')).not.toBeInTheDocument()

  await ui.click(screen.getByRole('button', { name: 'Save' }))

  expect(sent).not.toHaveBeenCalled()
  expect(await screen.findByText('Amount is required')).toBeInTheDocument()
  expect(screen.getByLabelText('Amount')).toHaveFocus()
  expect(screen.getByLabelText('Amount')).toHaveAttribute('aria-invalid', 'true')
  expect(screen.getByLabelText('Amount')).toHaveAccessibleDescription('What the receipt says. Amount is required')
})

it('hides the message while typing and checks again when the edit is finished', async () => {
  const { ui, sent } = setup()
  await ui.click(screen.getByRole('button', { name: 'Save' }))
  await screen.findByText('Amount is required')

  await ui.type(screen.getByLabelText('Amount'), 'abc')
  expect(screen.queryByText('Amount is required')).not.toBeInTheDocument()
  expect(screen.queryByText('Enter a number.')).not.toBeInTheDocument() // not while typing
  await ui.tab() // the edit is finished: the change event checks it

  expect(await screen.findByText('Enter a number.')).toBeInTheDocument()
  await ui.click(screen.getByRole('button', { name: 'Save' }))
  expect(sent).not.toHaveBeenCalled() // the field's own check blocks the form too

  await ui.clear(screen.getByLabelText('Amount'))
  await ui.type(screen.getByLabelText('Amount'), '12.5')
  await ui.click(screen.getByRole('button', { name: 'Save' }))
  expect(sent).toHaveBeenCalledWith(expect.objectContaining({ amount: '12.5', day: '' }))
})

it('checks an e-mail address, and links notes added later to the control too', async () => {
  const { ui, sent } = setup()
  await ui.type(screen.getByLabelText('Amount'), '3')
  await ui.type(screen.getByLabelText('E-mail'), 'not-an-address')
  await ui.click(screen.getByRole('button', { name: 'Save' }))

  expect(await screen.findByText('Enter a valid e-mail address')).toBeInTheDocument()
  expect(sent).not.toHaveBeenCalled()
  await ui.click(screen.getByRole('button', { name: 'Read a photo' }))
  expect(screen.getByLabelText('Amount')).toHaveAccessibleDescription('What the receipt says. Read from the photo.')
})

it('sends a typed date as YYYY-MM-DD from the hidden input', async () => {
  const { ui, sent } = setup()
  await ui.type(screen.getByLabelText('Amount'), '3')
  const day = await findDateField('Day')
  await typeDate(ui, day, '2026-03-01')

  expect(dateValue(day)).toBe('2026-03-01')
  await ui.click(screen.getByRole('button', { name: 'Save' }))
  expect(sent).toHaveBeenCalledWith(expect.objectContaining({ day: '2026-03-01' }))
})

// The day's maximum is the length of the month the field shows: a helper that told the day by 31 alone left the field empty in every
// shorter month, so the tests that type over today's date failed by the calendar (#205). Pinned here on fixed dates instead.
it.each([
  ['2026-02-10', 28],
  ['2024-02-10', 29],
  ['2026-11-02', 30],
  ['2026-10-15', 31],
])('a typed date replaces one in a month of any length (%s, %i days)', async (shown, days) => {
  render(
    <ThemeRoot instant>
      <Form onSubmit={(e) => e.preventDefault()}>
        <Field name="day" label="Day">
          <FieldDate defaultValue={shown} />
        </Field>
      </Form>
    </ThemeRoot>,
  )
  const day = await findDateField('Day')
  expect(within(day).getAllByRole('spinbutton').find((s) => dateSection(s) === 'day')).toHaveAttribute('aria-valuemax', String(days))

  await typeDate(userEvent.setup(), day, '2026-09-20')

  expect(dateValue(day)).toBe('2026-09-20')
})

it('a required date that is missing is said, and the focus goes to the first part of the day', async () => {
  const { ui, sent } = setup({ dateRequired: true })
  await ui.type(screen.getByLabelText('Amount'), '3')
  const day = await findDateField('Day')

  await ui.click(screen.getByRole('button', { name: 'Save' }))

  expect(await screen.findByText('Day is required')).toBeInTheDocument()
  expect(sent).not.toHaveBeenCalled()
  expect(screen.getAllByRole('spinbutton')[0]).toHaveFocus()
  expect(day).toHaveAccessibleDescription('Day is required')
})

it('a partly typed day and a day in the future are problems the field says', async () => {
  const { ui, sent } = setup({ disableFuture: true })
  await ui.type(screen.getByLabelText('Amount'), '3')
  const day = await findDateField('Day')

  await ui.click(screen.getAllByRole('spinbutton')[0])
  await ui.keyboard('03') // the month only
  await ui.click(screen.getByRole('button', { name: 'Save' }))
  expect(await screen.findByText('Enter the whole date.')).toBeInTheDocument()

  await typeDate(ui, day, '2999-01-01')
  await waitFor(() => expect(screen.getByText('The date cannot be in the future.')).toBeInTheDocument()) // a finished day is checked at once
  await ui.click(screen.getByRole('button', { name: 'Save' }))
  expect(sent).not.toHaveBeenCalled()
})

it('a required day that is only partly typed is said to be incomplete, not missing', async () => {
  const { ui, sent } = setup({ dateRequired: true })
  await ui.type(screen.getByLabelText('Amount'), '3')
  await findDateField('Day')

  await ui.click(screen.getAllByRole('spinbutton')[0])
  await ui.keyboard('03') // the month only
  await ui.click(screen.getByRole('button', { name: 'Save' }))

  expect(await screen.findByText('Enter the whole date.')).toBeInTheDocument()
  expect(screen.queryByText('Day is required')).not.toBeInTheDocument()
  expect(sent).not.toHaveBeenCalled()
})

it('editing a day that was already there says nothing while a part of it is being retyped', async () => {
  render(
    <ThemeRoot instant>
      <Form onSubmit={(e) => e.preventDefault()}>
        <Field name="day" label="Day" required>
          <FieldDate defaultValue="2026-03-05" />
        </Field>
      </Form>
    </ThemeRoot>,
  )
  const day = await findDateField('Day')
  expect(dateValue(day)).toBe('2026-03-05')

  const year = within(day).getAllByRole('spinbutton').find((s) => dateSection(s) === 'year')!
  await userEvent.setup().click(year)
  await userEvent.setup().keyboard('{Backspace}') // the year is empty for a moment

  expect(screen.queryByText('Day is required')).not.toBeInTheDocument()
  expect(screen.queryByText('Enter the whole date.')).not.toBeInTheDocument()
})

it('a message goes away when the value is put right without typing (a photo, a calculated amount, a picked option)', async () => {
  function Demo() {
    const [value, setValue] = useState('')
    return (
      <Form onSubmit={(e) => e.preventDefault()}>
        <Field name="volume" label="Volume" required>
          <FieldInput value={value} onChange={(e) => setValue(e.target.value)} />
        </Field>
        <button type="button" onClick={() => setValue('38')}>
          Work it out
        </button>
        <button type="submit">Save</button>
      </Form>
    )
  }
  render(
    <ThemeRoot instant>
      <Demo />
    </ThemeRoot>,
  )
  const ui = userEvent.setup()
  await ui.click(screen.getByRole('button', { name: 'Save' }))
  expect(await screen.findByText('Volume is required')).toBeInTheDocument()

  await ui.click(screen.getByRole('button', { name: 'Work it out' }))

  await waitFor(() => expect(screen.queryByText('Volume is required')).not.toBeInTheDocument())
  expect(screen.getByLabelText('Volume')).not.toHaveAttribute('aria-invalid', 'true')
})

it('a refusal of the server about one field (a required value) is said at that field, which takes the focus, not under the form', async () => {
  const refusal = new CombinedGraphQLErrors({ errors: [{ message: 'A chart needs a title.', extensions: { code: 'VALIDATION_FAILED', key: 'chart.titleRequired' } }] })
  const form = (error?: unknown) => (
    <ThemeRoot>
      <Form onSubmit={(e) => e.preventDefault()} refusal={error}>
        <Field name="title" label="Title">
          <FieldInput />
        </Field>
        {error !== undefined && <ErrorMessage error={error} />}
      </Form>
    </ThemeRoot>
  )
  const { rerender } = render(form())
  rerender(form(refusal)) // the server's answer to the submit

  const title = screen.getByRole('textbox', { name: 'Title' })
  expect(title).toHaveAccessibleDescription('The chart needs a title.')
  expect(title).toHaveAttribute('aria-invalid', 'true')
  await waitFor(() => expect(title).toHaveFocus())
  expect(screen.queryByRole('alert')).not.toBeInTheDocument()
})
