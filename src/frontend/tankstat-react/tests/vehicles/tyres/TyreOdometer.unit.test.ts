import { describe, expect, it } from 'vitest'
import { madeDay, madeText, monthsBetween, neighbours, odometerHint, odometerOn, parseMade, sortedReadings } from '../../../src/tyres/odometer.ts'

// The same readings and answers as the server's TyreCalculatorTests: the device works a day's odometer out exactly as the server does.
const log = sortedReadings([
  { date: '2026-11-10', value: 148_200 },
  { date: '2024-06-01', value: 118_000 },
  { date: '2025-04-08', value: 129_150 },
  { date: '2025-03-29', value: 128_150 },
])

describe('the odometer of a day', () => {
  it('is the reading of that day, the highest when there are several', () => {
    const sorted = sortedReadings([{ date: '2026-01-05', value: 1000 }, { date: '2026-01-05', value: 1060 }, { date: '2026-01-01', value: 900 }])
    expect(odometerOn(sorted, '2026-01-05')).toEqual({ value: 1060, approximate: false })
  })

  it('lies on a straight line between readings, marked approximate', () => {
    expect(odometerOn(log, '2025-04-02')).toEqual({ value: 128_550, approximate: true }) // 100 km a day
  })

  it('is not known before the first reading, and is the latest after it, approximate', () => {
    expect(odometerOn(log, '2024-05-31')).toBeNull()
    expect(odometerOn(log, '2026-12-01')).toEqual({ value: 148_200, approximate: true })
    expect(odometerOn([], '2026-01-01')).toBeNull()
  })

  it('is worded for a form', () => {
    expect(odometerHint([], '2026-01-01')).toEqual({ kind: 'none' })
    expect(odometerHint(log, '2024-01-01')).toEqual({ kind: 'before', firstDate: '2024-06-01' })
    expect(odometerHint(log, '2025-03-29')).toEqual({ kind: 'exact', value: 128_150 })
    expect(odometerHint(log, '2025-04-02')).toEqual({ kind: 'worked', value: 128_550 })
    expect(neighbours(log, '2025-03-29')).toEqual({ previous: { date: '2024-06-01', value: 118_000 }, next: { date: '2025-04-08', value: 129_150 } })
  })
})

describe('a set’s age and made date', () => {
  it('counts whole months', () => {
    expect(monthsBetween('2023-03-25', '2026-11-10')).toBe(43)
    expect(monthsBetween('2026-11-10', '2026-11-09')).toBe(0)
  })

  it('reads the DOT week and year, and writes them back', () => {
    expect(parseMade('0523')).toEqual({ week: 5, year: 2023 })
    expect(parseMade('5523')).toBeNull()
    expect(parseMade('523')).toBeNull()
    expect(madeText(5, 2023)).toBe('0523')
    expect(madeDay(5, 2023)).toBe('2023-01-29')
  })
})
