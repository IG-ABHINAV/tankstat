import { describe, expect, it } from 'vitest'
import { canBeWhole, canonical, comparedWith, daysIn, DEFAULT_PERIOD, isDefault, namesFillUps, periodOf, periodProblem, periodWords, QUICK_PICKS, sameWindow, startOf, stored, tabOf, toInput, TREND_FIGURES, type TrendPeriod } from '../../../src/dashboard/trendPeriod.ts'

const last = (unit: TrendPeriod['unit'], count: number, over: Partial<TrendPeriod> = {}): TrendPeriod => ({ kind: 'LAST', unit, count, since: null, whole: false, against: 'BEFORE', ...over })
const window = (start: string, end: string, fillUps = 0) => ({ start, end, fillUps })
const THIS_MONTH: TrendPeriod = { kind: 'THIS_MONTH', unit: null, count: null, since: null, whole: false, against: 'BEFORE' }
const words = (w: { key: string; values: Record<string, string | number> } | null) => (w ? `${w.key} ${JSON.stringify(w.values)}` : null)

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
    expect(canonical({ kind: 'THIS_MONTH', against: 'NONE' })).toEqual({ ...THIS_MONTH, against: 'NONE' }) // this month without a comparison kept as it is (#272)
  })

  it('hands GraphQL a fresh input with every field, never a read-back object', () => {
    const p = { __typename: 'TrendPeriod', ...last('WEEKS', 8, { against: 'YEAR_EARLIER' }) }
    expect(toInput(p)).toEqual({ kind: 'LAST', unit: 'WEEKS', count: 8, since: null, whole: false, against: 'YEAR_EARLIER' })
    expect(toInput(p)).not.toHaveProperty('__typename')
  })
})

describe('choosing a period', () => {
  const today = '2026-11-03'

  it('checks it as the server does: a whole count from 1 to 1000, a first day from 2000 to today', () => {
    expect(periodProblem(last('WEEKS', 8), today)).toBeNull()
    expect(periodProblem(THIS_MONTH, today)).toBeNull() // nothing to check: no count, no date (#272)
    expect(periodProblem(last('WEEKS', 0), today)).toEqual({ key: 'errors.trend.countRange', values: { max: 1000 } })
    expect(periodProblem(last('WEEKS', 1001), today)?.key).toBe('errors.trend.countRange')
    expect(periodProblem(last('WEEKS', null as never), today)?.key).toBe('errors.trend.countRange') // an empty field
    const since = (day: string | null): TrendPeriod => ({ kind: 'SINCE', unit: null, count: null, since: day, whole: false, against: 'BEFORE' })
    expect(periodProblem(since('2026-11-03'), today)).toBeNull()
    expect(periodProblem(since('2026-11-04'), today)).toEqual({ key: 'errors.trend.sinceRange', values: { earliest: '2000-01-01', latest: today } })
    expect(periodProblem(since('1999-12-31'), today)?.key).toBe('errors.trend.sinceRange')
    expect(periodProblem(since(null), today)?.key).toBe('errors.trend.sinceRange')
    expect(periodProblem(DEFAULT_PERIOD.distanceTrend, today)).toBeNull()
  })

  it('opens on the tab a period is set on, a quick pick whatever it is compared with, and starts each tab from its first values', () => {
    expect(tabOf('spendingTrend', DEFAULT_PERIOD.spendingTrend)).toBe('quick')
    expect(tabOf('spendingTrend', last('MONTHS', 6, { whole: true, against: 'YEAR_EARLIER' }))).toBe('quick') // the window is a pick's
    expect(tabOf('spendingTrend', last('MONTHS', 6))).toBe('last') // not whole: no pick
    expect(tabOf('distanceTrend', { ...DEFAULT_PERIOD.distanceTrend, against: 'BEFORE' })).toBe('quick')
    expect(tabOf('spendingTrend', { kind: 'SINCE', unit: null, count: null, since: '2026-03-15', whole: false, against: 'BEFORE' })).toBe('since')
    expect(tabOf('spendingTrend', THIS_MONTH)).toBe('quick') // this month so far, like this year, is only a quick pick (#272)
    expect(sameWindow(last('MONTHS', 6, { whole: true }), last('MONTHS', 6, { whole: true, against: 'YEAR_EARLIER' }))).toBe(true)

    const yearEarlier = last('MONTHS', 6, { whole: true, against: 'YEAR_EARLIER' })
    expect(startOf('last', 'spendingTrend', yearEarlier, today)).toEqual(last('WEEKS', 8, { against: 'YEAR_EARLIER' })) // the comparison kept
    expect(startOf('last', 'consumptionTrend', DEFAULT_PERIOD.consumptionTrend, today)).toEqual(DEFAULT_PERIOD.consumptionTrend) // already a Last period
    expect(startOf('since', 'spendingTrend', yearEarlier, today)).toEqual({ kind: 'SINCE', unit: null, count: null, since: '2026-08-01', whole: false, against: 'YEAR_EARLIER' })
    expect(startOf('since', 'spendingTrend', yearEarlier, '2026-02-10').since).toBe('2025-11-01') // across the year
    expect(startOf('quick', 'spendingTrend', last('DAYS', 30, { against: 'YEAR_EARLIER' }), today)).toEqual(last('MONTHS', 12, { whole: true, against: 'YEAR_EARLIER' }))
    expect([canBeWhole('WEEKS'), canBeWhole('MONTHS'), canBeWhole('YEARS'), canBeWhole('DAYS'), canBeWhole('FILL_UPS')]).toEqual([true, true, true, false, false])
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
    // This month so far; no comparison says nothing on the chip (#272: how a key figure without one says so is the builder's, #275).
    expect(chip('spendingTrend', THIS_MONTH)).toEqual(['dashboard.period.thisMonth {}', null])
    expect(chip('spendingTrend', { ...THIS_MONTH, against: 'NONE' })).toEqual(['dashboard.period.thisMonth {}', null])
    expect(chip('spendingTrend', last('WEEKS', 8, { against: 'NONE' }))).toEqual(['dashboard.period.last.weeks {"count":8}', null])
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
    // This month so far against the whole month before, "the month before" (#272), or the same days a year earlier; nothing, nothing.
    expect(words(comparedWith(THIS_MONTH, window('2026-10-01', '2026-10-31')))).toBe('dashboard.trend.than.before.months {"count":1}')
    expect(words(comparedWith({ ...THIS_MONTH, against: 'YEAR_EARLIER' }, earlier))).toBe('dashboard.trend.than.sameDaysYearEarlier {}')
    expect(comparedWith({ ...THIS_MONTH, against: 'NONE' }, earlier)).toBeNull()
    expect(comparedWith(last('FILL_UPS', 10, { against: 'NONE' }), earlier)).toBeNull()
  })

  it('counts a window\'s days with both ends, across a leap day, and names the fill-ups compared only for a window by dates', () => {
    expect(daysIn(window('2024-02-28', '2024-03-01'))).toBe(3)
    expect(daysIn(window('2026-11-03', '2026-11-03'))).toBe(1)
    expect(namesFillUps(DEFAULT_PERIOD.consumptionTrend)).toBe(false)
    expect(namesFillUps(last('WEEKS', 8))).toBe(true)
  })
})
