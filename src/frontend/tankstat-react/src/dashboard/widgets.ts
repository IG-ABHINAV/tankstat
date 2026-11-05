import { NEW_RECIPE, type ChartRecipe } from './chartFormat.ts'
import { isTrendFigure, stored, toInput, type TrendPeriod } from './trendPeriod.ts'

/**
 * What a vehicle's dashboard can show, and how a stored arrangement becomes the dashboard's tiles (#211). Pure, like `chartFormat.ts`: no
 * React, no MUI, so it can be tested and imported anywhere; the figures are drawn by `Figures.tsx` and the grid by `DashboardGrid.tsx`,
 * in the dashboard's own chunk.
 */

/** The key figures a dashboard can show; their texts are `dashboard.kpi.*`. */
export const FIGURES = ['thisMonth', 'consumption', 'odometer', 'lastFillUp', 'spendingTrend', 'costPerDistance', 'consumptionTrend', 'distanceTrend'] as const
export type FigureId = (typeof FIGURES)[number]

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

/** A tile as stored: a widget's id (a figure's, `preset:<id>` or `chart:<id>`) at its place and size in the four-column grid. */
export interface Tile {
  id: string
  x: number
  y: number
  w: number
  h: number
  /** Never moved by dragging the other tiles. */
  pinned: boolean
  /** A trend figure's own period (#230, `trendPeriod.ts`); none (or null) for its default, and for every other tile. */
  period?: TrendPeriod | null
}

/** How a dashboard is stored: its tiles, and the saved charts taken off it. */
export interface Layout {
  tiles: readonly Tile[]
  hidden: readonly string[]
}

const PRESET = 'preset:'
const CHART = 'chart:'
export const presetKey = (id: string) => `${PRESET}${id}`
export const chartKey = (id: string) => `${CHART}${id}`

/** What a tile's id names: a key figure, a ready-made chart, a saved chart (by its id), or nothing the app knows. */
export type Widget = { kind: 'figure'; id: FigureId } | { kind: 'ready'; chart: ReadyChart } | { kind: 'saved'; id: string }

export function widgetOf(id: string): Widget | null {
  if ((FIGURES as readonly string[]).includes(id)) return { kind: 'figure', id: id as FigureId }
  if (id.startsWith(PRESET)) {
    const chart = READY_CHARTS.find((c) => c.id === id.slice(PRESET.length))
    return chart ? { kind: 'ready', chart } : null
  }
  return id.startsWith(CHART) ? { kind: 'saved', id: id.slice(CHART.length) } : null
}

export interface Size {
  w: number
  h: number
}

/**
 * A key figure shows what its rows hold (#229, `Kpi.tsx`): two rows (84 px) its label and value, three (132 px) its comparison or note
 * too, four (180 px) its sparkline as well. Every figure can be made as low as two rows (#259): its card fits what it shows to its height by
 * measuring (`Kpi.tsx`), leaving out from the bottom what has no room and putting a label or value that would wrap on one line, so the
 * least rows never depend on a width, a language or the currencies used. Its default rows are what it shows in full: the sparkline's and
 * the trend figures' four, the plain figures' three.
 */
const FIGURE_ROWS: Record<FigureId, number> = {
  thisMonth: 4,
  consumption: 3,
  odometer: 3,
  lastFillUp: 3,
  spendingTrend: 4,
  costPerDistance: 4,
  consumptionTrend: 4,
  distanceTrend: 4,
}
/** The fewest rows a figure can take, its label and value (while arranging its value under the edit strip, which names it: #262). */
export const MIN_FIGURE_ROWS = 2

/** A widget's size when it is put on the dashboard: a figure one column wide and as low as it shows in full, a chart two columns wide and tall. */
export function defaultSize(id: string): Size {
  const widget = widgetOf(id)
  return widget?.kind === 'figure' ? { w: 1, h: FIGURE_ROWS[widget.id] } : { w: 2, h: 8 }
}
/** The least a tile can be: a figure its label and value (`MIN_FIGURE_ROWS`), a chart its picture. */
export function minSize(id: string): Size {
  const widget = widgetOf(id)
  return widget?.kind === 'figure' ? { w: 1, h: MIN_FIGURE_ROWS } : { w: 1, h: 5 }
}

const tile = (id: string, x: number, y: number, w: number, h: number): Tile => ({ id, x, y, w, h, pinned: false })

/** A preset's tiles: four figures in a row at the top, each as tall as the tallest of them shows in full, then the charts two by two. */
function presetTiles(figures: readonly FigureId[], charts: readonly ReadyChart['id'][]): Tile[] {
  const row = Math.max(...figures.map((f) => defaultSize(f).h))
  return [...figures.map((f, i) => tile(f, i, 0, 1, row)), ...charts.map((c, i) => tile(presetKey(c), (i % 2) * 2, row + Math.floor(i / 2) * 8, 2, 8))]
}

/** The layouts a dashboard can start from, named `dashboard.layoutPresets.<id>`: Standard is the dashboard as it always was. */
export const LAYOUT_PRESETS = {
  standard: { tiles: presetTiles(['thisMonth', 'consumption', 'odometer', 'lastFillUp'], ['monthlyCosts', 'consumption', 'fuelPrice', 'categories', 'distance']), hidden: [] },
  spending: { tiles: presetTiles(['thisMonth', 'spendingTrend', 'costPerDistance', 'lastFillUp'], ['monthlyCosts', 'spendSoFar', 'categories', 'costPerDistance', 'fuelPrice']), hidden: [] },
  usage: { tiles: presetTiles(['odometer', 'distanceTrend', 'consumption', 'consumptionTrend'], ['distanceYearEarlier', 'consumptionTrend', 'distanceAndConsumption', 'distance']), hidden: [] },
} as const satisfies Record<string, Layout>
export type LayoutPresetId = keyof typeof LAYOUT_PRESETS

export interface ResolvedLayout<S> {
  /** The tiles, packed: no two overlap, nothing floats above a gap. */
  tiles: Tile[]
  /** The saved charts taken off the dashboard. */
  hidden: S[]
  /** What can be put on it: the figures and ready-made charts not shown, and the saved charts taken off. */
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

/**
 * The dashboard a stored arrangement gives, with nothing stored the Standard preset: today's dashboard. It merges the way a grid's columns
 * do (`useGridSettings.merge`): a tile the app does not know (any more) is dropped; a saved chart the layout does not mention comes at the
 * bottom, so a new chart always shows and none is lost (taking one off is what `hidden` is for); a figure or ready-made chart it does not
 * mention is not shown, so the widgets of a later release never appear unasked. A tile is only ever raised to its widget's least size,
 * never lowered, so a stored arrangement keeps its places when a widget's least size goes down.
 */
export function resolveLayout<S extends { id: string }>(stored: Layout | null, saved: readonly S[]): ResolvedLayout<S> {
  const layout = stored ?? LAYOUT_PRESETS.standard
  const savedIds = new Set(saved.map((c) => c.id))
  const known = (t: Tile) => {
    const widget = widgetOf(t.id)
    return widget !== null && (widget.kind !== 'saved' || savedIds.has(widget.id))
  }
  const seen = new Set<string>()
  // No smaller than its widget needs (a figure stored lower than its label and value take), and no wider than the grid.
  const sized = (t: Tile): Tile => {
    const min = minSize(t.id)
    return { ...t, w: Math.min(COLUMNS, Math.max(min.w, t.w)), h: Math.min(MAX_ROWS, Math.max(min.h, t.h)) }
  }
  const tiles = layout.tiles.filter((t) => known(t) && !seen.has(t.id) && seen.add(t.id)).map(sized)
  const hiddenKeys = new Set(layout.hidden)
  const mentioned = new Set([...tiles.map((t) => t.id), ...hiddenKeys])
  // The saved charts the layout does not mention, each at the first free spot, in the order they were made.
  const packed = pack(tiles)
  for (const c of saved.filter((c) => !mentioned.has(chartKey(c.id)))) packed.push(firstFit(packed, tile(chartKey(c.id), 0, 0, 2, 8), COLUMNS))
  const shown = new Set(packed.map((t) => t.id))
  return {
    tiles: inReadingOrder(packed),
    hidden: saved.filter((c) => hiddenKeys.has(chartKey(c.id)) && !shown.has(chartKey(c.id))),
    available: {
      figures: FIGURES.filter((f) => !shown.has(f)),
      ready: READY_CHARTS.filter((c) => !shown.has(presetKey(c.id))),
      saved: saved.filter((c) => !shown.has(chartKey(c.id))),
    },
  }
}

/** A tile as the server takes it: its period only when it has one of its own (a trend figure's, not its default), in GraphQL's shape. */
export type StoredTile = Omit<Tile, 'period'> & { period?: ReturnType<typeof toInput> }

/**
 * The arrangement's version this app knows (#273), sent with every save: the server refuses a save without it or with an older one
 * (`dashboard.updateApp`), so an app never drops tiles it does not know. Raised with the server's `DashboardLayoutValues.LayoutVersion`
 * when tiles an older app would lose start being saved (#275's `tile:` tiles).
 */
export const LAYOUT_VERSION = 1

/** What the server stores for a dashboard as it is arranged on screen. */
export const toLayout = (tiles: readonly Tile[], hidden: readonly { id: string }[]): { tiles: StoredTile[]; hidden: string[]; layoutVersion: number } => ({
  tiles: tiles.map((t) => {
    const period = isTrendFigure(t.id) ? stored(t.id, t.period) : null
    return { id: t.id, x: t.x, y: t.y, w: t.w, h: t.h, pinned: t.pinned, ...(period ? { period: toInput(period) } : {}) }
  }),
  hidden: hidden.map((c) => chartKey(c.id)),
  layoutVersion: LAYOUT_VERSION,
})

// ---- what the tile menu does (the keyboard's and a screen reader's way to arrange) --------------------------------

/** A widget put on the dashboard at the bottom, at its default size; a trend figure with the period chosen for it (none: its default). */
export function addTile(tiles: readonly Tile[], id: string, cols = COLUMNS, period: TrendPeriod | null = null): Tile[] {
  const { w, h } = defaultSize(id)
  const bottom = Math.max(0, ...tiles.map((t) => t.y + t.h))
  const added = tile(id, 0, bottom, w, h)
  const own = isTrendFigure(id) ? stored(id, period) : null
  return pack([...tiles, own ? { ...added, period: own } : added], cols)
}

/** A trend figure given a period of its own (or back to its default with null). */
export const setTilePeriod = (tiles: readonly Tile[], id: string, period: TrendPeriod | null): Tile[] =>
  tiles.map((t) => (t.id === id ? { ...t, period: isTrendFigure(id) ? stored(id, period) : null } : t))

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
export function resizeTile(tiles: readonly Tile[], id: string, change: { w?: -1 | 1; h?: -1 | 1 }, cols = COLUMNS): Tile[] {
  const current = tiles.find((t) => t.id === id)
  if (!current) return [...tiles]
  const min = minSize(id)
  const w = Math.max(min.w, Math.min(cols, current.w + (change.w ?? 0)))
  const h = Math.max(min.h, Math.min(MAX_ROWS, current.h + (change.h ?? 0)))
  return pack(tiles.map((t) => (t.id === id ? { ...t, w, h, x: Math.min(t.x, cols - w) } : t)), cols)
}

export const pinTile = (tiles: readonly Tile[], id: string, pinned: boolean): Tile[] => tiles.map((t) => (t.id === id ? { ...t, pinned } : t))
