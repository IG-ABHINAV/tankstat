import { within } from '@testing-library/react'
import type { UserEvent } from '@testing-library/user-event'

/** The date field labelled `label`: the group of its typed sections, once its picker has loaded (it is lazy). */
export const findDateField = (label: string | RegExp, scope: HTMLElement = document.body) => within(scope).findByRole('group', { name: label })

/** What a date field holds as the form sends it: YYYY-MM-DD, or '' while it is empty or only partly typed. */
export function dateValue(field: HTMLElement): string {
  const box = field.closest('[data-date-field]')
  return box?.querySelector<HTMLInputElement>('input[name]')?.value ?? ''
}

/**
 * Which part of a date a section of a date field is, told by its maximum, the same in every language: 12 is the month, 28 to 31 the day
 * (the field caps it at the length of the month it shows, so in November it is 30, and 31 while it shows none), 9999 the year (99 for a
 * two-digit one). Anything else throws, so a section the tests do not know fails here and not as a field left empty further on.
 */
export function dateSection(section: HTMLElement): 'year' | 'month' | 'day' {
  const max = Number(section.getAttribute('aria-valuemax'))
  if (max === 12) return 'month'
  if (max >= 28 && max <= 31) return 'day'
  if (max === 9999 || max === 99) return 'year'
  throw new Error(`A date field section "${section.getAttribute('aria-label')}" with aria-valuemax="${section.getAttribute('aria-valuemax')}" is not a year, a month or a day.`)
}

/** Types a day (YYYY-MM-DD) into a date field, replacing what it held, section by section in the field's own order (month first in English). */
export async function typeDate(ui: UserEvent, field: HTMLElement, iso: string) {
  const [year, month, day] = iso.split('-')
  const parts = { year, month, day }
  const sections = within(field).getAllByRole('spinbutton')
  const digits = sections.map((s) => parts[dateSection(s)]).join('')
  await ui.click(sections[0])
  await ui.keyboard('{Control>}a{/Control}{Backspace}')
  await ui.keyboard(digits)
}
