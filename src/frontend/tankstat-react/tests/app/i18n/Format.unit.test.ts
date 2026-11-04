import { act, renderHook } from '@testing-library/react'
import { expect, it } from 'vitest'
import { sampleDate, useFormat } from '../../../src/i18n/format.ts'
import { currentFormats, setFormats } from '../../../src/i18n/formatStore.ts'
import { i18n } from '../../../src/i18n/index.ts'

it('shows a size on disk in the unit that suits it, never as 1,000 of a smaller one', () => {
  const { bytes } = renderHook(() => useFormat()).result.current

  expect([999, 999.6, 999_960, 5_300_000, 340_000_000, 999_960_000].map(bytes)).toEqual(['999 byte', '1 kB', '1 MB', '5.3 MB', '340 MB', '1 GB'])
})

it('writes a time of day the way the language does, whatever the time zone, and leaves anything else as it came', () => {
  const { timeOfDay } = renderHook(() => useFormat()).result.current

  // English writes 02:00 as 2:00 AM (with a narrow space before AM in newer data, so any space is accepted).
  expect(timeOfDay('02:00')).toMatch(/^2:00\sAM$/)
  expect(timeOfDay('2:30')).toMatch(/^2:30\sAM$/)
  expect(timeOfDay('23:59')).toMatch(/^11:59\sPM$/)
  expect(timeOfDay('02.00')).toBe('02.00')
})

it('writes times on the clock and dates in the order chosen, the instance default when none is, else as the language does', () => {
  const format = () => renderHook(() => useFormat()).result.current

  act(() => setFormats({ defaultTime: 'TWENTY_FOUR_HOUR' }))
  expect(format().timeOfDay('13:29')).toBe('13:29') // English, but the instance writes a 24-hour clock
  act(() => setFormats({ time: 'TWELVE_HOUR' }))
  expect(format().timeOfDay('13:29')).toMatch(/^1:29\sPM$/) // the user's own choice wins

  expect(format().date('2026-10-03')).toBe('Oct 3, 2026')
  const dates = (['ISO', 'DAY_MONTH_YEAR_DOTS', 'DAY_MONTH_YEAR_SLASHES', 'MONTH_DAY_YEAR', 'YEAR_MONTH_DAY_DOTS'] as const).map((date) => {
    act(() => setFormats({ date }))
    return format().date('2026-10-03')
  })
  expect(dates).toEqual(['2026-10-03', '03.10.2026', '03/10/2026', '10/03/2026', '2026. 10. 03.'])

  act(() => setFormats({ date: 'ISO', time: 'TWENTY_FOUR_HOUR' }))
  const local = new Date(2026, 9, 3, 14, 5) // in the browser's own zone, as an instant is shown
  expect(format().dateTime(local.toISOString())).toBe('2026-10-03 14:05')
})

it('takes only the formats it knows, so a format from a newer server writes dates as the language does instead of breaking every page', () => {
  const format = () => renderHook(() => useFormat()).result.current

  // What a server one release ahead may send while this tab waits for its update.
  act(() => setFormats({ date: 'DAY_MONTH_YEAR_DASHES' as never, defaultDate: 'DAY_MONTH_YEAR_DASHES' as never, defaultTime: 'NEWER_CLOCK' as never }))

  expect(currentFormats()).toEqual({ time: null, date: null, defaultTime: null, defaultDate: null })
  expect(format().date('2026-10-03')).toBe('Oct 3, 2026')
  expect(sampleDate('en', currentFormats().defaultDate)).toBe('Oct 13, 2026') // the language menu's Default still has its example
})

it('splits a consumption and a cost per distance at their number, the unit after it, joined back exactly as written whole (#259)', async () => {
  const units = [
    { distance: 'KILOMETERS', volume: 'LITERS' },
    { distance: 'MILES', volume: 'US_GALLONS' },
    { distance: 'MILES', volume: 'IMPERIAL_GALLONS' },
  ] as const
  const seen: Record<string, string[]> = {}
  try {
    for (const language of ['en', 'hu']) {
      await act(() => i18n.changeLanguage(language))
      const format = renderHook(() => useFormat()).result.current
      for (const u of units) {
        const parts = format.consumptionParts(6.83, u)
        expect(`${parts.value} ${parts.unit}`).toBe(format.consumption(6.83, u))
        ;(seen[language] ??= []).push(`${parts.value}|${parts.unit}`)
      }
      const cost = format.costPerDistanceParts(4293, 'HUF', 'KILOMETERS')
      expect(`${cost.value} ${cost.unit}`).toBe(format.costPerDistance(4293, 'HUF', 'KILOMETERS'))
      seen[language].push(`${cost.value}|${cost.unit}`.replace(/\s/g, ' '))
    }
  } finally {
    await act(() => i18n.changeLanguage('en'))
  }
  expect(seen).toEqual({
    en: ['6.83|L/100 km', '14.6|mpg (US)', '14.6|mpg (imp.)', 'HUF 4,293|/ 100 km'],
    hu: ['6,83|l/100 km', '14,6|mérföld/gallon (US)', '14,6|mérföld/gallon (imp.)', '4293 Ft|/ 100 km'],
  })
})
