import { expect, it } from 'vitest'
import { canonicalRecipe, defaultPeriodOf, FIGURES, figureRows, hasComparison, MEANING, periodFigureOf, templateOf, templateRecipe, toRecipeInput } from '../../../src/dashboard/figureRecipe.ts'
import { DEFAULT_PERIOD } from '../../../src/dashboard/trendPeriod.ts'

const days30 = { kind: 'LAST', unit: 'DAYS', count: 30, since: null, whole: false, against: 'NONE' } as const

it('colours a rise as the owner chose: more money, fuel, consumption or price is worse, more distance or fill-ups neither (#274)', () => {
  const worse = Object.entries(MEANING).filter(([, m]) => m === 'worseUp').map(([k]) => k)
  expect(worse.sort()).toEqual(['AVERAGE_CONSUMPTION', 'AVERAGE_PRICE_PER_UNIT', 'AVERAGE_PRICE_PER_UNIT_BEFORE_DISCOUNT', 'COST_PER_DISTANCE', 'EXPENSE_COST', 'FUEL_COST', 'FUEL_COST_PER_DISTANCE', 'FUEL_VOLUME', 'TOTAL_SPEND'])
  expect([MEANING.DISTANCE, MEANING.FILL_UPS]).toEqual(['neutral', 'neutral'])
})

it('gives a figure its rows by its recipe: four with a comparison or a sparkline, else three, as the built-in figures took', () => {
  expect(Object.fromEntries(FIGURES.map((f) => [f, figureRows(templateRecipe(f))]))).toEqual({
    thisMonth: 4, consumption: 3, odometer: 3, lastFillUp: 3, spendingTrend: 4, costPerDistance: 4, consumptionTrend: 4, distanceTrend: 4,
  })
  expect(figureRows({ period: days30, sparkline: false })).toBe(3) // no comparison
  expect(hasComparison({ period: { ...days30, against: 'BEFORE' } })).toBe(true)
  expect(hasComparison({ period: null })).toBe(false)
})

it('takes the period helpers of its template while it is that figure, else of its measure; a point in time has none', () => {
  expect(periodFigureOf(templateRecipe('thisMonth'))).toBe('thisMonth')
  expect(periodFigureOf({ measure: 'FUEL_VOLUME', template: null })).toBe('spendingTrend')
  expect(periodFigureOf({ measure: 'FUEL_COST_PER_DISTANCE', template: null })).toBe('costPerDistance')
  expect(periodFigureOf({ measure: 'AVERAGE_CONSUMPTION', template: 'thisMonth' })).toBe('consumptionTrend') // its measure changed: no longer Spent this month
  expect(templateOf({ measure: 'AVERAGE_CONSUMPTION', template: 'thisMonth' })).toBeNull()
  expect(templateOf({ measure: 'TOTAL_SPEND', template: 'thisMonth' })).toBe('thisMonth')
  expect(periodFigureOf({ measure: 'ODOMETER', template: 'odometer' })).toBeNull()
  expect(defaultPeriodOf({ measure: 'DISTANCE', template: null })).toEqual(DEFAULT_PERIOD.distanceTrend)
})

it('reads a recipe in the app\'s shape and hands GraphQL one field by field, never a __typename', () => {
  const readBack = { __typename: 'FigureRecipe', measure: 'FUEL_VOLUME', period: { __typename: 'TrendPeriod', ...days30 }, sparkline: true, template: null } as const
  const recipe = canonicalRecipe(readBack)
  expect(recipe).toEqual({ measure: 'FUEL_VOLUME', period: days30, sparkline: true, template: null })
  expect(toRecipeInput(recipe)).toEqual({ measure: 'FUEL_VOLUME', period: days30, sparkline: true, template: null })
  expect(JSON.stringify(toRecipeInput(canonicalRecipe(readBack)))).not.toContain('__typename')
  expect(canonicalRecipe({ measure: 'ODOMETER' })).toEqual({ measure: 'ODOMETER', period: null, sparkline: false, template: null })
})
