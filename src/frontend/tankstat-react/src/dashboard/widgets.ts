import { NEW_RECIPE, type ChartRecipe } from './chartFormat.ts'

/**
 * What a vehicle's dashboard can show, and how a stored arrangement becomes the dashboard (#211). Pure, like `chartFormat.ts`: no React,
 * no MUI, so it can be tested and imported anywhere; the figures are drawn by `Figures.tsx`, in the dashboard's own chunk.
 */

/** The key figures a dashboard can show; their texts are `dashboard.kpi.*` (`dashboard.figures.<id>` names them in the Customize dialog). */
export const FIGURES = ['thisMonth', 'consumption', 'odometer', 'lastFillUp'] as const
export type FigureId = (typeof FIGURES)[number]

/** The key figures' grid: one column on a phone, two from a small tablet, four on a desktop. */
export const FIGURE_GRID = { display: 'grid', gridTemplateColumns: { xs: 'minmax(0, 1fr)', sm: 'repeat(2, minmax(0, 1fr))', lg: 'repeat(4, minmax(0, 1fr))' }, gap: 1.5 } as const

const recipe = (over: Partial<ChartRecipe>): ChartRecipe => ({ ...NEW_RECIPE, ...over })

/** The ready-made charts: recipes, not stored, titled `dashboard.presets.<id>`; a person's own charts are saved ones. */
export const READY_CHARTS = [
  { id: 'monthlyCosts', recipe: recipe({ metric: 'TOTAL_SPEND', kind: 'BAR', stacked: true }) },
  { id: 'consumption', recipe: recipe({ metric: 'AVERAGE_CONSUMPTION', kind: 'LINE' }) },
  { id: 'fuelPrice', recipe: recipe({ metric: 'AVERAGE_PRICE_PER_UNIT', kind: 'LINE' }) },
  { id: 'categories', recipe: recipe({ metric: 'EXPENSE_COST', grouping: 'CATEGORY', kind: 'DONUT' }) },
  { id: 'distance', recipe: recipe({ metric: 'DISTANCE', kind: 'BAR' }) },
] as const satisfies readonly { id: string; recipe: ChartRecipe }[]
export type ReadyChart = (typeof READY_CHARTS)[number]

/** How a dashboard is stored: the figures and the charts in order (`preset:<id>`, `chart:<id>`), and the saved charts taken off it. */
export interface Layout {
  figures: readonly string[]
  charts: readonly string[]
  hidden: readonly string[]
}

const PRESET = 'preset:'
const CHART = 'chart:'

/** The layouts a dashboard can start from, named `dashboard.layoutPresets.<id>`: Standard is the dashboard as it always was. */
export const LAYOUT_PRESETS = {
  standard: { figures: [...FIGURES], charts: READY_CHARTS.map((c) => `${PRESET}${c.id}`), hidden: [] },
} as const satisfies Record<string, Layout>
export type LayoutPresetId = keyof typeof LAYOUT_PRESETS

/** A chart on the dashboard: a ready-made one or a saved one, with the key the stored layout names it by. */
export type ChartItem<S> = { kind: 'ready'; key: string; chart: ReadyChart } | { kind: 'saved'; key: string; chart: S }

export interface ResolvedLayout<S> {
  figures: FigureId[]
  charts: ChartItem<S>[]
  /** The saved charts taken off the dashboard. */
  hidden: S[]
  /** What can be put on it: the figures and ready-made charts not shown, and the saved charts taken off. */
  available: { figures: FigureId[]; ready: ReadyChart[] }
}

export const readyItem = <S>(chart: ReadyChart): ChartItem<S> => ({ kind: 'ready', key: `${PRESET}${chart.id}`, chart })
export const savedItem = <S extends { id: string }>(chart: S): ChartItem<S> => ({ kind: 'saved', key: `${CHART}${chart.id}`, chart })

/**
 * The dashboard a stored arrangement gives, with nothing stored the Standard preset: today's dashboard. It merges the way a grid's columns
 * do (`useGridSettings.merge`): an id the app does not know (any more) is dropped; a saved chart the layout does not mention comes at the
 * end, so a new chart always shows and none is lost (taking one off is what `hidden` is for); a figure or ready-made chart it does not
 * mention is not shown, so the widgets of a later release never appear unasked.
 */
export function resolveLayout<S extends { id: string }>(stored: Layout | null, saved: readonly S[]): ResolvedLayout<S> {
  const layout = stored ?? LAYOUT_PRESETS.standard
  const figures = [...new Set(layout.figures)].filter((f): f is FigureId => (FIGURES as readonly string[]).includes(f))
  const byKey = new Map<string, ChartItem<S>>([...READY_CHARTS.map((c) => readyItem<S>(c)), ...saved.map((c) => savedItem(c))].map((item) => [item.key, item]))
  const charts = [...new Set(layout.charts)].flatMap((key) => byKey.get(key) ?? [])
  const hiddenKeys = new Set(layout.hidden)
  const mentioned = new Set([...charts.map((c) => c.key), ...hiddenKeys])
  const unmentioned = saved.filter((c) => !mentioned.has(`${CHART}${c.id}`)).map((c) => savedItem(c))
  const shown = new Set(charts.map((c) => c.key))
  return {
    figures,
    charts: [...charts, ...unmentioned],
    hidden: saved.filter((c) => hiddenKeys.has(`${CHART}${c.id}`) && !shown.has(`${CHART}${c.id}`)),
    available: { figures: FIGURES.filter((f) => !figures.includes(f)), ready: READY_CHARTS.filter((c) => !shown.has(`${PRESET}${c.id}`)) },
  }
}

/** What the server stores for a dashboard as it is arranged on screen. */
export const toLayout = <S extends { id: string }>(figures: readonly FigureId[], charts: readonly ChartItem<S>[], hidden: readonly S[]): { figures: string[]; charts: string[]; hidden: string[] } => ({
  figures: [...figures],
  charts: charts.map((c) => c.key),
  hidden: hidden.map((c) => `${CHART}${c.id}`),
})
