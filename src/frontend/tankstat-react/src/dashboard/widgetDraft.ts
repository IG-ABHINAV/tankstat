import type { ChartMetric, FigureMeasure, WidgetKind } from '../gql/generated.ts'
import { isReady, NEW_RECIPE, normalise, sameRecipe, type ChartRecipe } from './chartFormat.ts'
import { defaultPeriodOf, isPointInTime, templateRecipe, type FigureId, type FigureRecipe } from './figureRecipe.ts'
import { readyChart, type ReadyChart, type TileRecipe } from './widgets.ts'

/**
 * A widget being made or changed in the builder (#275), pure like `widgets.ts`: its kind, its title as typed, and both recipes, so a
 * switch of kind keeps the measure where the other kind has it and switching back keeps the rest; a chart's ready-made chart it was made
 * from (`template`), which words its title while the chart is still that one.
 */
export interface WidgetDraft {
  kind: WidgetKind
  /** As typed: empty is the default title (a figure's, `useFigureTitle`; a ready-made chart's while unchanged). */
  title: string
  figure: FigureRecipe
  chart: ChartRecipe
  template: ReadyChart['id'] | null
}

/** The measures a key figure can show (#272): the chart measures, then the two points in time. */
export const FIGURE_MEASURES: readonly FigureMeasure[] = [
  'TOTAL_SPEND', 'FUEL_COST', 'EXPENSE_COST', 'FUEL_VOLUME', 'DISTANCE', 'AVERAGE_CONSUMPTION', 'AVERAGE_PRICE_PER_UNIT',
  'AVERAGE_PRICE_PER_UNIT_BEFORE_DISCOUNT', 'COST_PER_DISTANCE', 'FUEL_COST_PER_DISTANCE', 'FILL_UPS', 'ODOMETER', 'LAST_FILL_UP',
]

/** A figure of no template over `measure`: its measure's default period (none for a point in time), no sparkline. */
export const figureOf = (measure: FigureMeasure): FigureRecipe => ({ measure, period: defaultPeriodOf({ measure, template: null }), sparkline: false, template: null })

/** From scratch (#275): a key figure, total spend over its default period, compared as that period's figure is. */
export const scratchDraft = (): WidgetDraft => ({ kind: 'FIGURE', title: '', figure: figureOf('TOTAL_SPEND'), chart: NEW_RECIPE, template: null })

/** A built-in figure as a draft: its template's recipe, its title left to the template. */
export const figureDraft = (id: FigureId): WidgetDraft => ({ kind: 'FIGURE', title: '', figure: templateRecipe(id), chart: NEW_RECIPE, template: null })

/** A ready-made chart as a draft: its recipe, its title left to it while unchanged. */
export const readyDraft = (chart: ReadyChart): WidgetDraft => ({ kind: 'CHART', title: '', figure: figureOf(chart.recipe.metric), chart: chart.recipe, template: chart.id })

/** A tile's own recipe, or a saved widget's (`title` its own), as a draft. */
export function draftOf(r: { widget: WidgetKind; title?: string | null; chart?: ChartRecipe | null; figure?: FigureRecipe | null; template?: string | null }): WidgetDraft {
  const chart = r.chart ?? NEW_RECIPE
  const figure = r.figure ?? figureOf(r.chart?.metric ?? 'TOTAL_SPEND')
  return { kind: r.widget, title: r.title ?? '', figure, chart, template: readyChart(r.template)?.id ?? null }
}

/** A figure given another measure: its period kept where it has one, its measure's default otherwise; a point in time has none, nor a sparkline. */
export function withMeasure(figure: FigureRecipe, measure: FigureMeasure): FigureRecipe {
  if (isPointInTime(measure)) return { ...figure, measure, period: null, sparkline: false }
  return { ...figure, measure, period: figure.period ?? defaultPeriodOf({ measure, template: null }) }
}

/**
 * The draft as the other kind (#275): the measure kept where the other kind has it (a chart's metric is a figure's measure; the odometer
 * and the last fill-up have no chart, which keeps its own metric), the rest of each recipe as it was.
 */
export function switchKind(d: WidgetDraft, kind: WidgetKind): WidgetDraft {
  if (kind === d.kind) return d
  if (kind === 'CHART') return isPointInTime(d.figure.measure) ? { ...d, kind } : { ...d, kind, chart: normalise({ ...d.chart, metric: d.figure.measure as ChartMetric }) }
  return { ...d, kind, figure: withMeasure(d.figure, d.chart.metric) }
}

/** Whether a chart is still its ready-made chart (#275): then its title is the ready-made one's, in the viewer's language. */
export const isReadyAsIs = (d: Pick<WidgetDraft, 'chart' | 'template'>) => {
  const ready = readyChart(d.template)
  return !!ready && sameRecipe(d.chart, ready.recipe)
}

/**
 * Why a draft cannot be added yet, or null: a chart's custom period needs both its days (the preview says so), and a chart needs a title
 * unless it is still its ready-made chart (whose title it takes), as the server's `TileRecipe` says.
 */
export const draftProblem = (d: WidgetDraft): 'datesRequired' | 'titleRequired' | null =>
  d.kind !== 'CHART' ? null : !isReady(d.chart) ? 'datesRequired' : !d.title.trim() && !isReadyAsIs(d) ? 'titleRequired' : null

/**
 * The draft as a tile's own recipe: a figure's title its own or none (its default), a chart's its own or none while it is still its
 * ready-made chart, whose id it keeps as its template; a chart changed from its ready-made chart is a chart of no template.
 */
export function recipeOfDraft(d: WidgetDraft): TileRecipe {
  const title = d.title.trim() || null
  if (d.kind === 'FIGURE') return { widget: 'FIGURE', title, figure: d.figure }
  return { widget: 'CHART', title, chart: d.chart, template: isReadyAsIs(d) ? d.template : null }
}
