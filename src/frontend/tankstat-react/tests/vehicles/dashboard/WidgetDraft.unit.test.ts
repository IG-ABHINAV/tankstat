import { describe, expect, it } from 'vitest'
import { NEW_RECIPE } from '../../../src/dashboard/chartFormat.ts'
import { DEFAULT_PERIOD } from '../../../src/dashboard/trendPeriod.ts'
import { draftOf, draftProblem, figureDraft, readyDraft, recipeOfDraft, scratchDraft, switchKind, withMeasure } from '../../../src/dashboard/widgetDraft.ts'
import { READY_CHARTS } from '../../../src/dashboard/widgets.ts'

const monthly = READY_CHARTS.find((c) => c.id === 'monthlyCosts')!

describe('the builder\'s draft (#275)', () => {
  it('keeps the measure where the other kind has it, and the rest of each recipe as it was', () => {
    const spending = figureDraft('spendingTrend')
    const chart = switchKind(spending, 'CHART')
    expect(chart).toMatchObject({ kind: 'CHART', chart: { ...NEW_RECIPE, metric: 'TOTAL_SPEND' } })
    const fuel = switchKind({ ...chart, chart: { ...chart.chart, metric: 'FUEL_VOLUME' } }, 'FIGURE')
    expect(fuel.figure).toMatchObject({ measure: 'FUEL_VOLUME', period: DEFAULT_PERIOD.spendingTrend, template: 'spendingTrend' }) // its period kept
    // The odometer has no chart: the chart keeps its own metric.
    expect(switchKind(figureDraft('odometer'), 'CHART').chart.metric).toBe('TOTAL_SPEND')
    expect(switchKind(readyDraft(monthly), 'FIGURE').figure).toMatchObject({ measure: 'TOTAL_SPEND', period: DEFAULT_PERIOD.spendingTrend })
  })

  it('gives a point in time no period and no sparkline, and another measure its own default period', () => {
    expect(withMeasure({ measure: 'TOTAL_SPEND', period: DEFAULT_PERIOD.thisMonth, sparkline: true, template: null }, 'ODOMETER')).toEqual({ measure: 'ODOMETER', period: null, sparkline: false, template: null })
    expect(withMeasure({ measure: 'ODOMETER', period: null, sparkline: false, template: null }, 'AVERAGE_CONSUMPTION').period).toEqual(DEFAULT_PERIOD.consumptionTrend)
    expect(scratchDraft()).toMatchObject({ kind: 'FIGURE', title: '', figure: { measure: 'TOTAL_SPEND', period: DEFAULT_PERIOD.spendingTrend } })
  })

  it('makes a tile\'s recipe: a figure titled by its default, a ready-made chart by itself while unchanged, any other chart by a title of its own', () => {
    expect(recipeOfDraft(figureDraft('thisMonth'))).toEqual({ widget: 'FIGURE', title: null, figure: figureDraft('thisMonth').figure })
    expect(recipeOfDraft(readyDraft(monthly))).toEqual({ widget: 'CHART', title: null, chart: monthly.recipe, template: 'monthlyCosts' })
    const changed = { ...readyDraft(monthly), chart: { ...monthly.recipe, grouping: 'YEAR' as const } }
    expect(draftProblem(changed)).toBe('titleRequired')
    expect(recipeOfDraft({ ...changed, title: ' Yearly ' })).toEqual({ widget: 'CHART', title: 'Yearly', chart: changed.chart, template: null })
    expect(draftProblem({ ...changed, title: 'x', chart: { ...changed.chart, range: 'CUSTOM' } })).toBe('datesRequired')
    expect(draftProblem(figureDraft('odometer'))).toBeNull()
    expect(draftOf({ widget: 'CHART', title: 'Mine', chart: monthly.recipe, template: 'monthlyCosts' })).toMatchObject({ kind: 'CHART', title: 'Mine', template: 'monthlyCosts' })
  })
})
