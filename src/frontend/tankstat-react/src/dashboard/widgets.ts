import { NEW_RECIPE, type ChartRecipe } from './chartFormat.ts'

/**
 * What a vehicle's dashboard can show, and how a stored arrangement becomes the dashboard's tiles (#211). Pure, like `chartFormat.ts`: no
 * React, no MUI, so it can be tested and imported anywhere; the figures are drawn by `Figures.tsx` and the grid by `DashboardGrid.tsx`,
 * in the dashboard's own chunk.
 */

/** The key figures a dashboard can show; their texts are `dashboard.kpi.*`. */
export const FIGURES = ['thisMonth', 'consumption', 'odometer', 'lastFillUp'] as const
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

/** A widget's size when it is put on the dashboard: a figure one column wide and low, a chart two columns wide and tall. */
export const DEFAULT_SIZE: Record<Widget['kind'], { w: number; h: number }> = { figure: { w: 1, h: 3 }, ready: { w: 2, h: 8 }, saved: { w: 2, h: 8 } }
/** The least a tile can be: a figure needs its label and value, a chart its picture. */
export const MIN_SIZE: Record<Widget['kind'], { w: number; h: number }> = { figure: { w: 1, h: 3 }, ready: { w: 1, h: 5 }, saved: { w: 1, h: 5 } }

const tile = (id: string, x: number, y: number, w: number, h: number): Tile => ({ id, x, y, w, h, pinned: false })

/** The layouts a dashboard can start from, named `dashboard.layoutPresets.<id>`: Standard is the dashboard as it always was. */
export const LAYOUT_PRESETS = {
  standard: {
    tiles: [
      ...FIGURES.map((f, i) => tile(f, i, 0, 1, 3)),
      tile(presetKey('monthlyCosts'), 0, 3, 2, 8),
      tile(presetKey('consumption'), 2, 3, 2, 8),
      tile(presetKey('fuelPrice'), 0, 11, 2, 8),
      tile(presetKey('categories'), 2, 11, 2, 8),
      tile(presetKey('distance'), 0, 19, 2, 8),
    ],
    hidden: [],
  },
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
 * The tiles flowed afresh into `cols` columns, in reading order, each at the first place it fits (the highest row, then the leftmost
 * column): a phone's one column and a tablet's two come from the desktop's four this way, and a dashboard saved from a narrower screen
 * goes back into four with the new order and the old sizes. Pins hold only in the four columns the tiles were placed in.
 */
export function flow(tiles: readonly Tile[], cols: number): Tile[] {
  const placed: Tile[] = []
  for (const t of inReadingOrder(tiles)) placed.push({ ...firstFit(placed, t, cols), pinned: cols === COLUMNS && t.pinned })
  return placed
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
 * mention is not shown, so the widgets of a later release never appear unasked.
 */
export function resolveLayout<S extends { id: string }>(stored: Layout | null, saved: readonly S[]): ResolvedLayout<S> {
  const layout = stored ?? LAYOUT_PRESETS.standard
  const savedIds = new Set(saved.map((c) => c.id))
  const known = (t: Tile) => {
    const widget = widgetOf(t.id)
    return widget !== null && (widget.kind !== 'saved' || savedIds.has(widget.id))
  }
  const seen = new Set<string>()
  const tiles = layout.tiles.filter((t) => known(t) && !seen.has(t.id) && seen.add(t.id))
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

/** What the server stores for a dashboard as it is arranged on screen. */
export const toLayout = (tiles: readonly Tile[], hidden: readonly { id: string }[]): { tiles: Tile[]; hidden: string[] } => ({
  tiles: tiles.map((t) => ({ id: t.id, x: t.x, y: t.y, w: t.w, h: t.h, pinned: t.pinned })),
  hidden: hidden.map((c) => chartKey(c.id)),
})

// ---- what the tile menu does (the keyboard's and a screen reader's way to arrange) --------------------------------

/** A widget put on the dashboard at the bottom, at its default size. */
export function addTile(tiles: readonly Tile[], id: string, cols = COLUMNS): Tile[] {
  const kind = widgetOf(id)?.kind ?? 'saved'
  const { w, h } = DEFAULT_SIZE[kind]
  const bottom = Math.max(0, ...tiles.map((t) => t.y + t.h))
  return pack([...tiles, tile(id, 0, bottom, w, h)], cols)
}

export const removeTile = (tiles: readonly Tile[], id: string, cols = COLUMNS): Tile[] => pack(tiles.filter((t) => t.id !== id), cols)

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
  const min = MIN_SIZE[widgetOf(id)?.kind ?? 'saved']
  const w = Math.max(min.w, Math.min(cols, current.w + (change.w ?? 0)))
  const h = Math.max(min.h, Math.min(MAX_ROWS, current.h + (change.h ?? 0)))
  return pack(tiles.map((t) => (t.id === id ? { ...t, w, h, x: Math.min(t.x, cols - w) } : t)), cols)
}

export const pinTile = (tiles: readonly Tile[], id: string, pinned: boolean): Tile[] => tiles.map((t) => (t.id === id ? { ...t, pinned } : t))
