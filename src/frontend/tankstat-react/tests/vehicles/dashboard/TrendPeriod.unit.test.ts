import { describe, expect, it } from 'vitest'
import { canonical, comparedWith, daysIn, DEFAULT_PERIOD, isDefault, namesFillUps, periodOf, periodWords, QUICK_PICKS, stored, toInput, TREND_FIGURES, type TrendPeriod } from '../../../src/dashboard/trendPeriod.ts'

const last = (unit: TrendPeriod['unit'], count: number, over: Partial<TrendPeriod> = {}): TrendPeriod => ({ kind: 'LAST', unit, count, since: null, whole: false, against: 'BEFORE', ...over })
const window = (start: string, end: string, fillUps = 0) => ({ start, end, fillUps })
const words = (w: { key: string; values: Record<string, string | number> }) => `${w.key} ${JSON.stringify(w.values)}`

describe('the defaults', () => {
  it('are what each figure showed before periods: 12 whole months, 10 fill-ups, this year against last', () => {
    expect(DEFAULT_PERIOD).toEqual({
      spendingTrend: last('MONTHS', 12, { whole: true }),
      costPerDistance: last('MONTHS', 12, { whole: true }),
      consumptionTrend: last('FILL_UPS', 10),
      distanceTrend: { kind: 'THIS_YEAR', unit: null, count: null, since: null, whole: false, against: 'YEAR_EARLIER' },
    })
    expect(periodOf('consumptionTrend', null)).toBe(DEFAULT_PERIOD.consumptionTrend)
    for (const f of TREND_FIGURES) expect(QUICK_PICKS[f].some((q) => isDefault(f, q)), f).toBe(true) // a quick pick takes a tile back to its default
  })

  it('stores nothing for a default, and a period of its own in the app\'s shape whatever it came from', () => {
    expect(stored('spendingTrend', last('MONTHS', 12, { whole: true }))).toBeNull()
    expect(stored('spendingTrend', null)).toBeNull()
    const readBack = { __typename: 'TrendPeriod', kind: 'SINCE', unit: null, count: null, since: '2026-03-15', whole: false, against: 'BEFORE' } as const
    expect(stored('spendingTrend', readBack)).toEqual({ kind: 'SINCE', unit: null, count: null, since: '2026-03-15', whole: false, against: 'BEFORE' })
    expect(canonical({ kind: 'LAST', unit: 'DAYS', count: 30 })).toEqual(last('DAYS', 30)) // left out, as the server reads it
  })

  it('hands GraphQL a fresh input with every field, never a read-back object', () => {
    const p = { __typename: 'TrendPeriod', ...last('WEEKS', 8, { against: 'YEAR_EARLIER' }) }
    expect(toInput(p)).toEqual({ kind: 'LAST', unit: 'WEEKS', count: 8, since: null, whole: false, against: 'YEAR_EARLIER' })
    expect(toInput(p)).not.toHaveProperty('__typename')
  })
})

describe('the words', () => {
  const date = (day: string) => `«${day}»`

  it('names the period on the chip, and the comparison only when it is not the figure\'s own', () => {
    const chip = (f: (typeof TREND_FIGURES)[number], p: TrendPeriod) => {
      const w = periodWords(f, p, date)
      return [words(w.period), w.against && words(w.against)]
    }
    expect(chip('spendingTrend', DEFAULT_PERIOD.spendingTrend)).toEqual(['dashboard.period.whole.months {"count":12}', null])
    expect(chip('spendingTrend', last('WEEKS', 8, { against: 'YEAR_EARLIER' }))).toEqual(['dashboard.period.last.weeks {"count":8}', 'dashboard.period.vs.yearEarlier {}'])
    expect(chip('distanceTrend', DEFAULT_PERIOD.distanceTrend)).toEqual(['dashboard.period.thisYear {}', null]) // a year earlier is distance's own
    expect(chip('distanceTrend', { ...DEFAULT_PERIOD.distanceTrend, against: 'BEFORE' })).toEqual(['dashboard.period.thisYear {}', 'dashboard.period.vs.before {}'])
    expect(chip('consumptionTrend', { kind: 'SINCE', unit: null, count: null, since: '2026-03-15', whole: false, against: 'BEFORE' })).toEqual(['dashboard.period.since {"date":"«2026-03-15»"}', null])
  })

  it('keeps the defaults\' sentences, and words every other period from its own count or the earlier window\'s days', () => {
    const earlier = window('2026-01-01', '2026-01-31', 7)
    expect(words(comparedWith(DEFAULT_PERIOD.spendingTrend, earlier))).toBe('dashboard.trend.than.before.months {"count":12}') // "the 12 months before"
    expect(words(comparedWith(DEFAULT_PERIOD.consumptionTrend, earlier))).toBe('dashboard.trend.than.before.fillUps {"count":7}') // those found before, maybe fewer
    expect(words(comparedWith(DEFAULT_PERIOD.distanceTrend, earlier))).toBe('dashboard.trend.than.lastYearToDate {}') // "last year to this day"
    expect(words(comparedWith(last('WEEKS', 8, { against: 'YEAR_EARLIER' }), earlier))).toBe('dashboard.trend.than.yearEarlier.weeks {"count":8}')
    expect(words(comparedWith(last('FILL_UPS', 5, { against: 'YEAR_EARLIER' }), earlier))).toBe('dashboard.trend.than.sameDaysYearEarlier {}')
    const since = { kind: 'SINCE', unit: null, count: null, since: '2026-02-01', whole: false, against: 'BEFORE' } as const
    expect(words(comparedWith(since, earlier))).toBe('dashboard.trend.than.before.days {"count":31}')
    expect(words(comparedWith({ ...since, against: 'YEAR_EARLIER' }, earlier))).toBe('dashboard.trend.than.sameDaysYearEarlier {}')
    expect(words(comparedWith({ ...DEFAULT_PERIOD.distanceTrend, against: 'BEFORE' }, window('2025-02-28', '2025-12-31')))).toBe('dashboard.trend.than.before.days {"count":307}')
  })

  it('counts a window\'s days with both ends, across a leap day, and names the fill-ups compared only for a window by dates', () => {
    expect(daysIn(window('2024-02-28', '2024-03-01'))).toBe(3)
    expect(daysIn(window('2026-11-03', '2026-11-03'))).toBe(1)
    expect(namesFillUps(DEFAULT_PERIOD.consumptionTrend)).toBe(false)
    expect(namesFillUps(last('WEEKS', 8))).toBe(true)
  })
})
