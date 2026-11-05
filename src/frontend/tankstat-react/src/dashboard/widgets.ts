import type { TileRecipeInput, WidgetKind } from '../gql/generated.ts'
import { NEW_RECIPE, toConfig, type ChartRecipe } from './chartFormat.ts'
import { canonicalRecipe, defaultPeriodOf, FIGURES, figureRows, isTemplate, templateRecipe, toRecipeInput, type FigureId, type FigureRecipe } from './figureRecipe.ts'
import { canonical, type TrendPeriod } from './trendPeriod.ts'

export { FIGURES, type FigureId } from './figureRecipe.ts'

/**
 * What a vehicle's dashboard can show, and how a stored arrangement becomes the dashboard's tiles (#211). Pure, like `chartFormat.ts`: no
 * React, no MUI, so it can be tested and imported anywhere; the key figures are drawn by `KeyFigure.tsx` and the grid by
 * `DashboardGrid.tsx`, in the dashboard's own chunk.
 */

/** The key figures' grid while the figures load: one column on a phone, two from a small tablet, four on a desktop. */
export const FIGURE_GRID = { display: 'grid', gridTemplateColumns: { xs: 'minmax(0, 1fr)', sm: 'repeat(2, minmax(0, 1fr))', lg: 'repeat(4, minmax(0, 1fr))' }, gap: 1.5 } as const

const recipe = (over: Partial<ChartRecipe>): ChartRecipe => ({ ...NEW_RECIPE, ...over })

/** The ready-made charts: recipes, not stored, titled `dashboard.presets.<id>`; a person's own charts are saved ones. */
export const READY_CHARTS = [
  { id: 'monthlyCosts', recipe: recipe({ metric: 'TOTAL_SPEND', kind: 'BAR', stacked: true }) },
  { id: 'consumption', recipe: recipe({ metric: 'AVERAGE_CONSUMPTION', kind: 'LINE' }) },
  { id: 'fuelPrice', recipe: recipe({ metric: 'AVERAGE_PRICE_PER_UNIT', kind: 'LINE' }) },
  { id: 'categories', recipe: recipe({ metric: 'EXPENSE_COST', grouping: 'CATEGORY', kind: 'DONUT' }) },
  { id: 'distance', recipe: recipe({ metric: 'DISTANCE', kind: 'BAR' }) },
  { id: 'spendSoFar', recipe: recipe({ metric: 'TOTAL_SPEND', kind: 'LINE', range: 'THIS_YEAR', runningTotal: true, compareYearEarlier: true }) },
  { id: 'costPerDistance', recipe: recipe({ metric: 'COST_PER_DISTANCE', kind: 'LINE', trend: 'MOVING_AVERAGE' }) },
  { id: 'consumptionTrend', recipe: recipe({ metric: 'AVERAGE_CONSUMPTION', kind: 'LINE', grouping: 'QUARTER', range: 'ALL', trend: 'LINEAR' }) },
  { id: 'distanceYearEarlier', recipe: recipe({ metric: 'DISTANCE', kind: 'BAR', compareYearEarlier: true }) },
  { id: 'distanceAndConsumption', recipe: recipe({ metric: 'DISTANCE', kind: 'BAR', secondMetric: 'AVERAGE_CONSUMPTION', secondKind: 'LINE' }) },
] as const satisfies readonly { id: string; recipe: ChartRecipe }[]
export type ReadyChart = (typeof READY_CHARTS)[number]

/** The grid: four columns on a desktop (two on a tablet, one on a phone, derived), rows of `ROW_HEIGHT` px with `GAP` px between tiles. */
export const COLUMNS = 4
export const MAX_ROWS = 24
export const ROW_HEIGHT = 36
export const GAP = 12

/**
 * A tile's own recipe (#273): a key figure's (#274: every key figure on the dashboard has one) or a chart's (#275), and its title; null
 * for a figure's default title (its template's, translated).
 */
export interface TileRecipe {
  widget: WidgetKind
  title?: string | null
  chart?: ChartRecipe | null
  figure?: FigureRecipe | null
  /** A chart's ready-made chart it was made from (#275, `READY_CHARTS`' id), which words its default title; a figure's is in its recipe. */
  template?: string | null
}

/**
 * A tile: a widget at its place and size in the four-column grid. Its id is a built-in figure's (an arrangement stored before #274, or a
 * preset's, until it is saved), `tile:<id>` (a tile with its own recipe), `preset:<id>` or `chart:<id>` (a saved widget).
 */
export interface Tile {
  id: string
  x: number
  y: number
  w: number
  h: number
  /** Never moved by dragging the other tiles. */
  pinned: boolean
  /** An old trend figure id's own period as stored (#230); read into its recipe by `resolveLayout`, and never stored again. */
  period?: TrendPeriod | null
  /** A key figure's recipe (every figure tile's, once resolved), or a `tile:`'s own chart. */
  recipe?: TileRecipe | null
}

/** How a dashboard is stored: its tiles, and the saved charts taken off it. */
export interface Layout {
  tiles: readonly Tile[]
  hidden: readonly string[]
}

const PRESET = 'preset:'
const CHART = 'chart:'
const TILE = 'tile:'
export const presetKey = (id: string) => `${PRESET}${id}`
export const chartKey = (id: string) => `${CHART}${id}`
export const tileKey = (id: string) => `${TILE}${id}`
export const isTileKey = (id: string) => id.startsWith(TILE)

/**
 * What a tile shows: a key figure of its own (#274: a built-in one's recipe, or a `tile:`'s), a chart of its own (#275: a ready-made one's
 * recipe, `template` its id, or a `tile:`'s), a saved widget (by its id; `figure` its recipe when it is a key figure), or nothing the app
 * knows.
 */
export type Widget =
  | { kind: 'figure'; figure: FigureRecipe; title: string | null }
  | { kind: 'chart'; chart: ChartRecipe; title: string | null; template: ReadyChart['id'] | null }
  | { kind: 'saved'; id: string; figure: FigureRecipe | null }

/** The ready-made chart an id names, if any. */
export const readyChart = (id: string | null | undefined): ReadyChart | undefined => READY_CHARTS.find((c) => c.id === id)

/** A tile as far as what it shows goes. */
export type TileWidget = Pick<Tile, 'id'> & Partial<Pick<Tile, 'recipe' | 'period'>>
/** The recipe of a saved widget that is a key figure (#273), by its id; null for a chart (or one not known). */
export type SavedFigures = (id: string) => FigureRecipe | null | undefined

export function widgetOf(tile: TileWidget, savedFigure?: SavedFigures): Widget | null {
  const { id, recipe } = tile
  if (recipe?.widget === 'FIGURE' && recipe.figure) return { kind: 'figure', figure: recipe.figure, title: recipe.title ?? null }
  if (recipe?.widget === 'CHART' && recipe.chart) return { kind: 'chart', chart: recipe.chart, title: recipe.title ?? null, template: readyChart(recipe.template)?.id ?? null }
  if (isTemplate(id)) return { kind: 'figure', figure: templateRecipe(id, tile.period), title: null }
  if (id.startsWith(PRESET)) {
    const chart = readyChart(id.slice(PRESET.length))
    return chart ? { kind: 'chart', chart: chart.recipe, title: null, template: chart.id } : null
  }
  if (!id.startsWith(CHART)) return null
  const saved = id.slice(CHART.length)
  return { kind: 'saved', id: saved, figure: savedFigure?.(saved) ?? null }
}

/** The key figure a widget shows (its own or a saved one's), or null for a chart. */
const figureOfWidget = (w: Widget | null): FigureRecipe | null => (w?.kind === 'figure' || w?.kind === 'saved' ? w.figure : null)

export interface Size {
  w: number
  h: number
}

/**
 * A key figure shows what its rows hold (#229, `Kpi.tsx`): two rows (84 px) its label and value, three (132 px) its comparison or note
 * too, four (180 px) its sparkline as well. Every figure can be made as low as two rows (#259): its card fits what it shows to its height by
 * measuring (`Kpi.tsx`), leaving out from the bottom what has no room and putting a label or value that would wrap on one line, so the
 * least rows never depend on a width, a language or the currencies used. Its default rows are what it shows in full, by its recipe
 * (`figureRecipe.ts` `figureRows`): four with a comparison or a sparkline, else three. The fewest rows a figure can take are its label and
 * value (while arranging its value under the edit strip, which names it: #262).
 */
export const MIN_FIGURE_ROWS = 2

/** A widget's size when it is put on the dashboard: a figure one column wide and as low as it shows in full, a chart two columns wide and tall. */
export function defaultSize(tile: TileWidget, savedFigure?: SavedFigures): Size {
  const figure = figureOfWidget(widgetOf(tile, savedFigure))
  return figure ? { w: 1, h: figureRows(figure) } : { w: 2, h: 8 }
}
/** The least a tile can be: a figure its label and value (`MIN_FIGURE_ROWS`), a chart its picture. */
export function minSize(tile: TileWidget, savedFigure?: SavedFigures): Size {
  return figureOfWidget(widgetOf(tile, savedFigure)) ? { w: 1, h: MIN_FIGURE_ROWS } : { w: 1, h: 5 }
}

const tile = (id: string, x: number, y: number, w: number, h: number): Tile => ({ id, x, y, w, h, pinned: false })

/** A built-in figure's tile recipe (#274): its template's, titled by the template. */
export const figureTile = (id: FigureId, period?: TrendPeriod | null): TileRecipe => ({ widget: 'FIGURE', title: null, figure: templateRecipe(id, period) })
/** A ready-made chart's tile recipe (#275): its recipe, titled by it (no title of its own), the ready-made id as its template. */
export const readyTile = (chart: ReadyChart): TileRecipe => ({ widget: 'CHART', title: null, chart: chart.recipe, template: chart.id })

/**
 * A preset's tiles: four figures in a row at the top (their built-in recipes, #274, by their template's id until saved), each as tall as the
 * tallest of them shows in full, then the ready-made charts two by two (their recipes, #275, by their `preset:` id until saved).
 */
function presetTiles(figures: readonly FigureId[], charts: readonly ReadyChart['id'][]): Tile[] {
  const row = Math.max(...figures.map((f) => defaultSize({ id: f }).h))
  return [
    ...figures.map((f, i) => ({ ...tile(f, i, 0, 1, row), recipe: figureTile(f) })),
    ...charts.map((c, i) => ({ ...tile(presetKey(c), (i % 2) * 2, row + Math.floor(i / 2) * 8, 2, 8), recipe: readyTile(readyChart(c)!) })),
  ]
}

/** The layouts a dashboard can start from, named `dashboard.layoutPresets.<id>`: Standard is the dashboard as it always was. */
export const LAYOUT_PRESETS: Record<'standard' | 'spending' | 'usage', Layout> = {
  standard: { tiles: presetTiles(['thisMonth', 'consumption', 'odometer', 'lastFillUp'], ['monthlyCosts', 'consumption', 'fuelPrice', 'categories', 'distance']), hidden: [] },
  spending: { tiles: presetTiles(['thisMonth', 'spendingTrend', 'costPerDistance', 'lastFillUp'], ['monthlyCosts', 'spendSoFar', 'categories', 'costPerDistance', 'fuelPrice']), hidden: [] },
  usage: { tiles: presetTiles(['odometer', 'distanceTrend', 'consumption', 'consumptionTrend'], ['distanceYearEarlier', 'consumptionTrend', 'distanceAndConsumption', 'distance']), hidden: [] },
}
export type LayoutPresetId = keyof typeof LAYOUT_PRESETS

export interface ResolvedLayout<S> {
  /** The tiles, packed: no two overlap, nothing floats above a gap. */
  tiles: Tile[]
  /** The saved charts taken off the dashboard. */
  hidden: S[]
  /** What can be put on it: the built-in figures no tile was made from (#274: by template), the ready-made charts not shown, and the saved widgets taken off. */
  available: { figures: FigureId[]; ready: ReadyChart[]; saved: S[] }
}

/** The tiles in reading order: by row, then by column. */
export const inReadingOrder = (tiles: readonly Tile[]): Tile[] => [...tiles].sort((a, b) => a.y - b.y || a.x - b.x)

const overlaps = (a: Tile, b: Tile) => a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h

/** The lowest row at which a tile of `w` × `h` fits at column `x` among `placed`. */
function lowestFit(placed: readonly Tile[], x: number, w: number, h: number): number {
  let y = 0
  for (;;) {
    const probe = { id: '', x, y, w, h, pinned: false }
    const hit = placed.filter((p) => overlaps(p, probe))
    if (hit.length === 0) return y
    y = Math.max(...hit.map((p) => p.y + p.h))
  }
}

/**
 * Packs tiles into `cols` columns the way the grid draws them: in reading order, each at its own column (clamped into the grid) and as high
 * as it can go without overlapping one placed before it; a pinned tile stays where it is and the others go round it. Nothing is lost.
 */
export function pack(tiles: readonly Tile[], cols = COLUMNS): Tile[] {
  const clamp = (t: Tile) => {
    const w = Math.min(t.w, cols)
    return { ...t, w, x: Math.min(t.x, cols - w) }
  }
  const placed: Tile[] = tiles.filter((t) => t.pinned).map(clamp)
  for (const t of inReadingOrder(tiles).filter((t) => !t.pinned)) {
    const c = clamp(t)
    placed.push({ ...c, y: lowestFit(placed, c.x, c.w, c.h) })
  }
  return inReadingOrder(placed)
}

/**
 * The tiles flowed afresh into `cols` columns in reading order, each at the first place it fits after the one before it (the highest row
 * it can take in any column, the leftmost there, never above or left of the previous tile), so the order holds and nothing floats above a
 * gap: a phone's one column and a tablet's two come from the desktop's four this way, a dashboard saved from a narrower screen goes back
 * into four with the new order and the old sizes, and a tile moved earlier or later by its menu lands where the order says. Pins hold only
 * in the four columns the tiles were placed in.
 */
export function flow(tiles: readonly Tile[], cols: number): Tile[] {
  const placed: Tile[] = []
  let last: Tile | null = null
  for (const t of inReadingOrder(tiles)) {
    last = { ...nextFit(placed, t, cols, last), pinned: cols === COLUMNS && t.pinned }
    placed.push(last)
  }
  return placed
}

/**
 * A tile at the first place it fits after `last` in reading order: of every column, the lowest row it can take there (so nothing is left
 * above it), the places before `last` left out, the highest of them, and the leftmost of those. The column `last` takes always comes after
 * it, so there is always one.
 */
function nextFit(placed: readonly Tile[], t: Tile, cols: number, last: Tile | null): Tile {
  const w = Math.min(t.w, cols)
  let best: { x: number; y: number } | null = null
  for (let x = 0; x + w <= cols; x++) {
    const y = lowestFit(placed, x, w, t.h)
    const after = last === null || y > last.y || (y === last.y && x > last.x)
    if (after && (best === null || y < best.y)) best = { x, y }
  }
  return { ...t, w, x: best!.x, y: best!.y }
}

/** A tile at the first place it fits among `placed`: the highest row, and the leftmost column there. */
function firstFit(placed: readonly Tile[], t: Tile, cols: number): Tile {
  const w = Math.min(t.w, cols)
  let best = { x: 0, y: Number.POSITIVE_INFINITY }
  for (let x = 0; x + w <= cols; x++) {
    const y = lowestFit(placed, x, w, t.h)
    if (y < best.y) best = { x, y }
  }
  return { ...t, w, x: best.x, y: best.y }
}

/** A tile's recipe in the app's shape, whatever it came from (as stored it carries GraphQL's `__typename`s); null for one the app cannot draw. */
function ownRecipe(r: TileRecipe): TileRecipe | null {
  if (r.widget === 'FIGURE' && r.figure) return { widget: 'FIGURE', title: r.title ?? null, figure: canonicalRecipe(r.figure) }
  if (r.widget === 'CHART' && r.chart) return { widget: 'CHART', title: r.title ?? null, chart: { ...NEW_RECIPE, ...toConfig(r.chart) }, template: r.template ?? null }
  return null
}

/**
 * A tile as the dashboard keeps it while shown and arranged: its recipe on it (#274: an old figure id's its template's; #275: an old
 * `preset:` id's its ready-made chart's), no period beside it.
 */
function withRecipe(t: Tile): Tile {
  const own = t.recipe ? ownRecipe(t.recipe) : null
  if (own) return { ...t, recipe: own, period: null }
  if (isTemplate(t.id)) return { ...t, recipe: figureTile(t.id, t.period), period: null }
  const ready = t.id.startsWith(PRESET) ? readyChart(t.id.slice(PRESET.length)) : undefined
  return ready ? { ...t, recipe: readyTile(ready), period: null } : { ...t, recipe: null }
}

/**
 * The template a tile was made from, for what Add widget marks and offers: a figure's its built-in id (#274), a chart's its ready-made id
 * as `preset:<id>` (#275; the ready-made `consumption` is not the figure `consumption`). Also the grid's `data-template`.
 */
export const templateOfTile = (t: Pick<Tile, 'recipe'>): string | null =>
  t.recipe?.widget === 'FIGURE' ? (t.recipe.figure?.template ?? null) : t.recipe?.template ? presetKey(t.recipe.template) : null

/**
 * The dashboard a stored arrangement gives, with nothing stored the Standard preset: today's dashboard. It merges the way a grid's columns
 * do (`useGridSettings.merge`): a tile the app does not know (any more) is dropped; a saved widget the layout does not mention comes at the
 * bottom, so a new one always shows and none is lost (taking one off is what `hidden` is for); a figure or ready-made chart it does not
 * mention is not shown, so the widgets of a later release never appear unasked. A tile is only ever raised to its widget's least size,
 * never lowered, so a stored arrangement keeps its places when a widget's least size goes down. Every figure tile comes with its recipe
 * (#274): a stored old figure id (and its period) as its template's, kept under that id until the next save (`toLayout`).
 */
export function resolveLayout<S extends { id: string; figure?: FigureRecipe | null }>(stored: Layout | null, saved: readonly S[]): ResolvedLayout<S> {
  const layout = stored ?? LAYOUT_PRESETS.standard
  const savedIds = new Set(saved.map((c) => c.id))
  const savedFigure: SavedFigures = (id) => saved.find((c) => c.id === id)?.figure
  const known = (t: Tile) => {
    const widget = widgetOf(t)
    return widget !== null && (widget.kind !== 'saved' || savedIds.has(widget.id))
  }
  const seen = new Set<string>()
  // No smaller than its widget needs (a figure stored lower than its label and value take), and no wider than the grid.
  const sized = (t: Tile): Tile => {
    const min = minSize(t, savedFigure)
    return { ...t, w: Math.min(COLUMNS, Math.max(min.w, t.w)), h: Math.min(MAX_ROWS, Math.max(min.h, t.h)) }
  }
  const tiles = layout.tiles.map(withRecipe).filter((t) => known(t) && !seen.has(t.id) && seen.add(t.id)).map(sized)
  const hiddenKeys = new Set(layout.hidden)
  const mentioned = new Set([...tiles.map((t) => t.id), ...hiddenKeys])
  // The saved widgets the layout does not mention, each at the first free spot at its default size, in the order they were made.
  const packed = pack(tiles)
  for (const c of saved.filter((c) => !mentioned.has(chartKey(c.id)))) {
    const { w, h } = defaultSize({ id: chartKey(c.id) }, savedFigure)
    packed.push(firstFit(packed, tile(chartKey(c.id), 0, 0, w, h), COLUMNS))
  }
  const shown = new Set(packed.map((t) => t.id))
  const made = new Set(packed.map(templateOfTile))
  return {
    tiles: inReadingOrder(packed),
    hidden: saved.filter((c) => hiddenKeys.has(chartKey(c.id)) && !shown.has(chartKey(c.id))),
    available: {
      figures: FIGURES.filter((f) => !made.has(f)),
      ready: READY_CHARTS.filter((c) => !made.has(presetKey(c.id))),
      saved: saved.filter((c) => !shown.has(chartKey(c.id))),
    },
  }
}

/** A tile's recipe as GraphQL takes it, field by field. */
const toTileRecipeInput = (r: TileRecipe): TileRecipeInput => ({
  widget: r.widget,
  title: r.title ?? null,
  chart: r.chart ? toConfig(r.chart) : null,
  figure: r.figure ? toRecipeInput(r.figure) : null,
  template: r.template ?? null,
})

/** A tile's recipe as a saved widget's (#275, `SaveWidgetInput`): a chart's config or a figure's recipe, field by field. */
export const widgetInput = (r: TileRecipe) => (r.widget === 'FIGURE' && r.figure ? { chart: null, figure: toRecipeInput(r.figure) } : { chart: r.chart ? toConfig(r.chart) : null, figure: null })

/** A tile as the server takes it: a tile with a recipe of its own as a `tile:<id>` with it (#274), every other one by its id alone. */
export interface StoredTile {
  id: string
  x: number
  y: number
  w: number
  h: number
  pinned: boolean
  recipe?: TileRecipeInput
}

/**
 * The arrangement's version this app knows (#273), sent with every save: the server refuses a save without it or with an older one
 * (`dashboard.updateApp`), so an app never drops tiles it does not know. Raised with the server's `DashboardLayoutValues.LayoutVersion`
 * whenever tiles an older app would lose start being saved: 2 since #274, which saves every key figure as a `tile:` with its recipe; 3
 * since #275, which saves the ready-made charts as `tile:` recipes with their template and no title (#274's app would drop the template).
 */
export const LAYOUT_VERSION = 3

/**
 * What the server stores for a dashboard as it is arranged on screen: a tile with a recipe of its own (every figure's, #274) as a `tile:`,
 * which keeps its id once it has one; an old figure id or a preset's figure gets a new one (`newId`) on its first save.
 */
export const toLayout = (
  tiles: readonly Tile[],
  hidden: readonly { id: string }[],
  newId: () => string = () => crypto.randomUUID(),
): { tiles: StoredTile[]; hidden: string[]; layoutVersion: number } => ({
  tiles: tiles.map((t) => {
    const base = { id: t.id, x: t.x, y: t.y, w: t.w, h: t.h, pinned: t.pinned }
    return t.recipe ? { ...base, id: isTileKey(t.id) ? t.id : tileKey(newId()), recipe: toTileRecipeInput(t.recipe) } : base
  }),
  hidden: hidden.map((c) => chartKey(c.id)),
  layoutVersion: LAYOUT_VERSION,
})

// ---- what the tile menu does (the keyboard's and a screen reader's way to arrange) --------------------------------

/** A widget put on the dashboard at the bottom, at its default size: by its id, a figure (#274: a `tile:` id) with its recipe. */
export function addTile(tiles: readonly Tile[], id: string, cols = COLUMNS, recipe: TileRecipe | null = null, savedFigure?: SavedFigures): Tile[] {
  const { w, h } = defaultSize({ id, recipe }, savedFigure)
  const bottom = Math.max(0, ...tiles.map((t) => t.y + t.h))
  return pack([...tiles, { ...tile(id, 0, bottom, w, h), recipe }], cols)
}

/** A figure tile given another period (or back to its figure's default with null); a point in time, or any other tile, stays as it is. */
export const setTilePeriod = (tiles: readonly Tile[], id: string, period: TrendPeriod | null): Tile[] =>
  tiles.map((t) => {
    const figure = t.id === id && t.recipe?.widget === 'FIGURE' ? t.recipe.figure : null
    if (!figure || !figure.period) return t
    return { ...t, recipe: { ...t.recipe!, figure: { ...figure, period: period ? canonical(period) : defaultPeriodOf(figure) } } }
  })

export const removeTile = (tiles: readonly Tile[], id: string, cols = COLUMNS): Tile[] => pack(tiles.filter((t) => t.id !== id), cols)

/**
 * A tile given another widget in its place (#247: a ready-made chart customized into a chart of one's own): the same place, size and pin,
 * no period (only a trend figure has one), nothing else moved.
 */
export const replaceTile = (tiles: readonly Tile[], from: string, to: string): Tile[] =>
  tiles.map((t) => (t.id === from ? { id: to, x: t.x, y: t.y, w: t.w, h: t.h, pinned: t.pinned } : t))

/** A tile moved one step earlier or later in the reading order, the tiles flowed afresh so the new order holds on screen. */
export function moveTile(tiles: readonly Tile[], id: string, step: -1 | 1, cols = COLUMNS): Tile[] {
  const order = inReadingOrder(tiles)
  const index = order.findIndex((t) => t.id === id)
  const target = index + step
  if (index < 0 || target < 0 || target >= order.length) return [...tiles]
  ;[order[index], order[target]] = [order[target], order[index]]
  return flow(order.map((t, i) => ({ ...t, y: i, x: 0 })), cols)
}

/** A tile made wider or narrower, taller or shorter, by one step, within what its widget needs and the grid holds. */
export function resizeTile(tiles: readonly Tile[], id: string, change: { w?: -1 | 1; h?: -1 | 1 }, cols = COLUMNS, savedFigure?: SavedFigures): Tile[] {
  const current = tiles.find((t) => t.id === id)
  if (!current) return [...tiles]
  const min = minSize(current, savedFigure)
  const w = Math.max(min.w, Math.min(cols, current.w + (change.w ?? 0)))
  const h = Math.max(min.h, Math.min(MAX_ROWS, current.h + (change.h ?? 0)))
  return pack(tiles.map((t) => (t.id === id ? { ...t, w, h, x: Math.min(t.x, cols - w) } : t)), cols)
}

export const pinTile = (tiles: readonly Tile[], id: string, pinned: boolean): Tile[] => tiles.map((t) => (t.id === id ? { ...t, pinned } : t))
