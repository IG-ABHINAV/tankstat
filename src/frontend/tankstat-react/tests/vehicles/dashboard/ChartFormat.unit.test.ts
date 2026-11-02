import { describe, expect, it } from 'vitest'
import { barPlaces, chartAxes, isValue, labelStep, MONTH_LABEL_PX, NEW_RECIPE, normalise, parsePeriod, periodLabel, seriesId, shortPeriodLabels, toConfig, withRunningTotal, withTrend, type ChartRecipe, type Series } from '../../../src/dashboard/chartFormat.ts'

const series = (kind: string, currency: string | null, values: Record<string, number | null>, role: Series['role'] = 'VALUE'): Series => ({
  kind,
  currency,
  role,
  slope: null,
  points: Object.entries(values).map(([key, value]) => ({ key, value })),
})

describe('parsePeriod and periodLabel', () => {
  it('read a month and a quarter one way, for the axis and the tooltip alike, and nothing else', () => {
    expect(parsePeriod('2026-09')).toEqual({ year: '2026', month: 9 })
    expect(parsePeriod('2026-Q3')).toEqual({ year: '2026', quarter: 3 })
    expect([parsePeriod('2026'), parsePeriod('Service'), parsePeriod('')]).toEqual([null, null, null])
    expect([periodLabel('2026-09', 'en'), periodLabel('2026-Q3', 'en'), periodLabel('Service', 'en')]).toEqual(['Sep 2026', 'Q3 2026', 'Service'])
  })
})

describe('shortPeriodLabels', () => {
  it('names a month alone, with its year under it on the first and on every January, so more of them fit on an axis', () => {
    const labels = shortPeriodLabels(['2025-11', '2025-12', '2026-01', '2026-02'], 'en', 'MONTH')
    expect([...labels.values()]).toEqual(['Nov\n2025', 'Dec', 'Jan\n2026', 'Feb'])
  })

  it('in Hungarian too', () => {
    const labels = shortPeriodLabels(['2025-12', '2026-01', '2026-03'], 'hu', 'MONTH')
    expect([...labels.values()]).toEqual(['dec.\n2025', 'jan.\n2026', 'márc.'])
  })

  it('names every step-th month, and the year on each named one that starts a year among them, so no year goes unnamed', () => {
    const months = ['2025-11', '2025-12', '2026-01', '2026-02', '2026-03', '2026-04', '2026-05', '2026-06', '2026-07', '2026-08', '2026-09', '2026-10', '2026-11', '2026-12', '2027-01', '2027-02']
    const labels = shortPeriodLabels(months, 'en', 'MONTH', 3)

    // January 2026 and 2027 are left out, so the year goes on the first month named in each.
    expect([...labels.entries()]).toEqual([
      ['2025-11', 'Nov\n2025'],
      ['2026-02', 'Feb\n2026'],
      ['2026-05', 'May'],
      ['2026-08', 'Aug'],
      ['2026-11', 'Nov'],
      ['2027-02', 'Feb\n2027'],
    ])
  })

  it('shortens a quarter, and names nothing in a chart by year or by category (those are named as they are)', () => {
    expect([...shortPeriodLabels(['2025-Q4', '2026-Q1'], 'en', 'QUARTER').values()]).toEqual(['Q4 ’25', 'Q1 ’26'])
    // A category that looks like a month ("2025-03", a project code) stays a category.
    expect(shortPeriodLabels(['Service', '', '2025-03'], 'en', 'CATEGORY').size).toBe(0)
  })
})

describe('labelStep', () => {
  it('names every period while they fit, and every n-th one when they do not', () => {
    expect(labelStep(12, 12 * MONTH_LABEL_PX)).toBe(1)
    expect(labelStep(12, 6 * MONTH_LABEL_PX)).toBe(2)
    expect(labelStep(12, 5 * MONTH_LABEL_PX)).toBe(3)
    expect(labelStep(12, 0)).toBe(1) // not laid out yet
  })
})

describe('chartAxes', () => {
  const huf = series('fuel', 'HUF', { '2026-01': 38000 })
  const eur = series('fuel', 'EUR', { '2026-01': 54 })

  it('gives every currency of a chart of costs a scale of its own, and shows none of them when there are several', () => {
    const axes = chartAxes({ unit: 'CURRENCY', series: [huf, series('expenses', 'HUF', { '2026-01': 4000 }), eur] })

    expect(axes.axes).toEqual([
      { id: 'currency:HUF', currency: 'HUF' },
      { id: 'currency:EUR', currency: 'EUR' },
    ])
    expect([axes.axisOf(huf), axes.axisOf(eur)]).toEqual(['currency:HUF', 'currency:EUR'])
    expect(axes.shown).toBe(false)
  })

  it('shows the axis of a single currency, in that currency', () => {
    expect(chartAxes({ unit: 'CURRENCY', series: [huf] })).toMatchObject({ axes: [{ id: 'currency:HUF', currency: 'HUF' }], shown: true })
  })

  it('gives a cost per distance a scale per currency too, and draws a line drawn from a series on its series\' scale', () => {
    const earlier = series('fuel', 'HUF', { '2026-01': 30000 }, 'YEAR_EARLIER')
    const axes = chartAxes({ unit: 'COST_PER_DISTANCE', series: [huf, eur, earlier] })

    expect(axes.axes.map((a) => a.id)).toEqual(['currency:HUF', 'currency:EUR'])
    expect(axes.axisOf(earlier)).toBe(axes.axisOf(huf))
    expect(axes.shown).toBe(false)
  })

  it('shares one axis for any other unit', () => {
    const axes = chartAxes({ unit: 'VOLUME', series: [series('value', null, { '2026-01': 40 })] })
    expect(axes).toMatchObject({ axes: [{ id: 'value', currency: null }], shown: true })
  })
})

describe('barPlaces', () => {
  const periods = ['2026-01', '2026-02', '2026-03']
  const fuelHuf = series('fuel', 'HUF', { '2026-01': 38000, '2026-02': 0, '2026-03': 37000 })
  const expensesHuf = series('expenses', 'HUF', { '2026-01': 4000, '2026-02': 0, '2026-03': 0 })
  const fuelEur = series('fuel', 'EUR', { '2026-01': 54, '2026-02': 120, '2026-03': 0 })

  it('stacked: a bar per currency the period has, side by side, and the top of each stack is rounded', () => {
    const places = barPlaces({ series: [fuelHuf, expensesHuf, fuelEur] }, periods, true)

    // January has both currencies: HUF first (fuel at the bottom, expenses on top), EUR beside it.
    expect(places.at(fuelHuf, 0)).toEqual({ place: 0, of: 2, top: false })
    expect(places.at(expensesHuf, 0)).toEqual({ place: 0, of: 2, top: true })
    expect(places.at(fuelEur, 0)).toEqual({ place: 1, of: 2, top: true })
    // February has EUR only: one bar, in the middle, and nothing where HUF would be.
    expect(places.at(fuelHuf, 1)).toBeNull()
    expect(places.at(fuelEur, 1)).toEqual({ place: 0, of: 1, top: true })
    // March has HUF fuel only: it is the top of its stack.
    expect(places.at(fuelHuf, 2)).toEqual({ place: 0, of: 1, top: true })
  })

  it('not stacked: a bar per series the period has', () => {
    const places = barPlaces({ series: [fuelHuf, expensesHuf, fuelEur] }, periods, false)

    expect([places.at(fuelHuf, 0), places.at(expensesHuf, 0), places.at(fuelEur, 0)]).toEqual([
      { place: 0, of: 3, top: true },
      { place: 1, of: 3, top: true },
      { place: 2, of: 3, top: true },
    ])
    expect(places.at(fuelEur, 1)).toEqual({ place: 0, of: 1, top: true })
  })

  it('finds a value by its period, whatever order the series lists its points in', () => {
    const shuffled = series('fuel', 'EUR', { '2026-03': 10, '2026-01': 0 })
    expect(barPlaces({ series: [shuffled] }, periods, true).at(shuffled, 2)).toEqual({ place: 0, of: 1, top: true })
    expect(barPlaces({ series: [shuffled] }, periods, true).at(shuffled, 0)).toBeNull()
  })
})

describe('the recipe of a chart over time', () => {
  const recipe = (over: Partial<ChartRecipe>): ChartRecipe => ({ ...NEW_RECIPE, ...over })

  it('keeps a trend line, a running total and a year earlier only for a chart over time, as the server does', () => {
    expect(normalise(recipe({ trend: 'LINEAR', compareYearEarlier: true, kind: 'DONUT' }))).toMatchObject({ trend: 'NONE', compareYearEarlier: false })
    expect(normalise(recipe({ metric: 'EXPENSE_COST', grouping: 'CATEGORY', runningTotal: true, trend: 'MOVING_AVERAGE' }))).toMatchObject({ trend: 'NONE', runningTotal: false })
    expect(normalise(recipe({ metric: 'AVERAGE_CONSUMPTION', runningTotal: true }))).toMatchObject({ runningTotal: false }) // an average does not add up
    expect(normalise(recipe({ metric: 'COST_PER_DISTANCE', runningTotal: true }))).toMatchObject({ runningTotal: false })
    expect(normalise(recipe({ trend: 'LINEAR', runningTotal: true }))).toMatchObject({ trend: 'NONE', runningTotal: true })
    expect(normalise(recipe({ trend: 'LINEAR', compareYearEarlier: true }))).toMatchObject({ trend: 'LINEAR', compareYearEarlier: true })
  })

  it('takes the trend line away when a running total is chosen, and the running total when a trend line is', () => {
    expect(withRunningTotal(recipe({ trend: 'LINEAR' }), true)).toMatchObject({ trend: 'NONE', runningTotal: true })
    expect(withTrend(recipe({ runningTotal: true }), 'MOVING_AVERAGE')).toMatchObject({ trend: 'MOVING_AVERAGE', runningTotal: false })
    expect(withTrend(recipe({ runningTotal: true }), 'NONE')).toMatchObject({ trend: 'NONE', runningTotal: true })
  })

  it('asks the server for them, and dates only for a custom range', () => {
    expect(toConfig(recipe({ trend: 'LINEAR', compareYearEarlier: true, from: '2026-01-01', to: '2026-02-01' }))).toEqual({
      metric: 'TOTAL_SPEND',
      grouping: 'MONTH',
      kind: 'BAR',
      range: 'LAST12_MONTHS',
      stacked: false,
      from: null,
      to: null,
      trend: 'LINEAR',
      runningTotal: false,
      compareYearEarlier: true,
    })
  })

  it('tells the values from the lines drawn from them', () => {
    const value = series('total', 'HUF', {})
    const average = series('total', 'HUF', {}, 'MOVING_AVERAGE')

    expect([isValue(value), isValue(average)]).toEqual([true, false])
    expect(seriesId(value)).not.toBe(seriesId(average))
  })
})
