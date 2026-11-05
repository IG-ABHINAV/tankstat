import type { FigureMeasure, FigureRecipeInput } from '../gql/generated.ts'
import type { Meaning } from './changeWords.ts'
import { canonical, DEFAULT_PERIOD, isPeriodFigure, toInput, type PeriodFigureId, type TrendFigureId, type TrendPeriod } from './trendPeriod.ts'

/**
 * A key figure as a recipe (#274, over #272's `vehicleFigure` and #273's tile recipes), pure like `widgets.ts`: what it measures, over what
 * period and compared with what, whether it draws a sparkline, and the built-in figure it was made from (`template`), which words its
 * default title. Every key figure on the dashboard is one, drawn by one component (`KeyFigure.tsx`); the eight built-in figures are the
 * templates, and a stored old figure id reads as its template's recipe.
 */

/** The built-in key figures, the templates of a recipe; their texts are `dashboard.kpi.*`. */
export const FIGURES = ['thisMonth', 'consumption', 'odometer', 'lastFillUp', 'spendingTrend', 'costPerDistance', 'consumptionTrend', 'distanceTrend'] as const
export type FigureId = (typeof FIGURES)[number]
export const isTemplate = (id: string | null | undefined): id is FigureId => (FIGURES as readonly (string | null | undefined)[]).includes(id)

/** A key figure's recipe as the app keeps it: every field there, the period in the app's shape (`trendPeriod.ts`), none for a point in time. */
export interface FigureRecipe {
  measure: FigureMeasure
  period: TrendPeriod | null
  sparkline: boolean
  /** The built-in figure it was made from (`FIGURES`), which words its default title; null for none. */
  template: string | null
}

/** What each built-in figure measures. */
export const TEMPLATE_MEASURE: Record<FigureId, FigureMeasure> = {
  thisMonth: 'TOTAL_SPEND',
  consumption: 'AVERAGE_CONSUMPTION',
  odometer: 'ODOMETER',
  lastFillUp: 'LAST_FILL_UP',
  spendingTrend: 'TOTAL_SPEND',
  costPerDistance: 'COST_PER_DISTANCE',
  consumptionTrend: 'AVERAGE_CONSUMPTION',
  distanceTrend: 'DISTANCE',
}

/** The measures of a point in time: no period, no comparison (#272). */
export const isPointInTime = (measure: FigureMeasure) => measure === 'ODOMETER' || measure === 'LAST_FILL_UP'

/**
 * A built-in figure as a recipe (#274): its measure, its period (a stored tile's own, else its default; none for a point in time), the
 * sparkline *Spent this month* always drew, and itself as the template. A stored old figure id reads as this, so it shows what it showed.
 */
export function templateRecipe(id: FigureId, period?: TrendPeriod | null): FigureRecipe {
  return { measure: TEMPLATE_MEASURE[id], period: isPeriodFigure(id) ? (period ? canonical(period) : DEFAULT_PERIOD[id]) : null, sparkline: id === 'thisMonth', template: id }
}

/** The template a recipe still is: its own, while its measure is the template's (a recipe whose measure changed is no longer that figure). */
export const templateOf = (r: Pick<FigureRecipe, 'measure' | 'template'>): FigureId | null => (isTemplate(r.template) && TEMPLATE_MEASURE[r.template] === r.measure ? r.template : null)

/** The period figure a recipe of no built-in figure takes its quick picks and default from, by its measure. */
const PROFILE: Partial<Record<FigureMeasure, TrendFigureId>> = {
  TOTAL_SPEND: 'spendingTrend',
  FUEL_COST: 'spendingTrend',
  EXPENSE_COST: 'spendingTrend',
  FUEL_VOLUME: 'spendingTrend',
  FILL_UPS: 'spendingTrend',
  AVERAGE_PRICE_PER_UNIT: 'spendingTrend',
  AVERAGE_PRICE_PER_UNIT_BEFORE_DISCOUNT: 'spendingTrend',
  COST_PER_DISTANCE: 'costPerDistance',
  FUEL_COST_PER_DISTANCE: 'costPerDistance',
  AVERAGE_CONSUMPTION: 'consumptionTrend',
  DISTANCE: 'distanceTrend',
}

/** The figure whose period helpers a recipe uses (`trendPeriod.ts`): its template's while it is one, else its measure's; none for a point in time. */
export function periodFigureOf(r: Pick<FigureRecipe, 'measure' | 'template'>): PeriodFigureId | null {
  const template = templateOf(r)
  return template && isPeriodFigure(template) ? template : (PROFILE[r.measure] ?? null)
}

/** A recipe's period back at its figure's default (none for a point in time). */
export const defaultPeriodOf = (r: Pick<FigureRecipe, 'measure' | 'template'>): TrendPeriod | null => {
  const figure = periodFigureOf(r)
  return figure ? DEFAULT_PERIOD[figure] : null
}

/** Whether a recipe compares two windows (its arrow and sentence). */
export const hasComparison = (r: Pick<FigureRecipe, 'period'>) => r.period != null && r.period.against !== 'NONE'

/** A figure tile's rows by default (#229), what it shows in full: four with a comparison or a sparkline, else three, as the built-in figures always took. */
export const figureRows = (r: Pick<FigureRecipe, 'period' | 'sparkline'>) => (hasComparison(r) || r.sparkline ? 4 : 3)

/**
 * What a rise of a measure means, its arrow's colour (the owner's table, #274): more spent, more fuel, a higher consumption or price is
 * worse; more distance and more fill-ups are neither. The points in time have no comparison.
 */
export const MEANING: Record<FigureMeasure, Meaning> = {
  TOTAL_SPEND: 'worseUp',
  FUEL_COST: 'worseUp',
  EXPENSE_COST: 'worseUp',
  FUEL_VOLUME: 'worseUp',
  AVERAGE_CONSUMPTION: 'worseUp',
  AVERAGE_PRICE_PER_UNIT: 'worseUp',
  AVERAGE_PRICE_PER_UNIT_BEFORE_DISCOUNT: 'worseUp',
  COST_PER_DISTANCE: 'worseUp',
  FUEL_COST_PER_DISTANCE: 'worseUp',
  DISTANCE: 'neutral',
  FILL_UPS: 'neutral',
  ODOMETER: 'neutral',
  LAST_FILL_UP: 'neutral',
}

/** A recipe in the app's shape, whatever it came from (a stored tile's or a saved widget's, with GraphQL's `__typename`s). */
export const canonicalRecipe = (r: { measure: FigureMeasure; period?: Parameters<typeof canonical>[0] | null; sparkline?: boolean | null; template?: string | null }): FigureRecipe => ({
  measure: r.measure,
  period: r.period ? canonical(r.period) : null,
  sparkline: r.sparkline ?? false,
  template: r.template ?? null,
})

/** The recipe as GraphQL takes it, field by field (never a `__typename`, which the server refuses in an input). */
export const toRecipeInput = (r: FigureRecipe): FigureRecipeInput => ({ measure: r.measure, period: r.period ? toInput(r.period) : null, sparkline: r.sparkline, template: r.template })
