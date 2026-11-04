import { describe, expect, it } from 'vitest'
import { addTile, chartKey, defaultSize, FIGURES, flow, LAYOUT_PRESETS, MIN_FIGURE_ROWS, minSize, moveTile, pack, pinTile, presetKey, READY_CHARTS, removeTile, replaceTile, resizeTile, resolveLayout, setTilePeriod, toLayout, type Tile } from '../../../src/dashboard/widgets.ts'
import { DEFAULT_PERIOD } from '../../../src/dashboard/trendPeriod.ts'

const chart = (id: string) => ({ id, title: `Chart ${id}` })
const tile = (id: string, x: number, y: number, w = 2, h = 8, pinned = false): Tile => ({ id, x, y, w, h, pinned })
const places = (tiles: Tile[]) => tiles.map((t) => `${t.id}@${t.x},${t.y} ${t.w}x${t.h}${t.pinned ? ' pinned' : ''}`)

describe('resolveLayout', () => {
  it('gives the Standard preset with nothing stored: the figures in a row, the ready-made charts two by two, then every saved chart', () => {
    const layout = resolveLayout(null, [chart('a'), chart('b'), chart('c')])

    expect(places(layout.tiles)).toEqual([
      'thisMonth@0,0 1x4',
      'consumption@1,0 1x4',
      'odometer@2,0 1x4',
      'lastFillUp@3,0 1x4',
      'preset:monthlyCosts@0,4 2x8',
      'preset:consumption@2,4 2x8',
      'preset:fuelPrice@0,12 2x8',
      'preset:categories@2,12 2x8',
      'preset:distance@0,20 2x8',
      'chart:a@2,20 2x8', // the saved charts fill in below, side by side
      'chart:b@0,28 2x8',
      'chart:c@2,28 2x8',
    ])
    expect(layout.available.saved).toEqual([])
    expect(layout.available.figures).toEqual(['spendingTrend', 'costPerDistance', 'consumptionTrend', 'distanceTrend']) // the trend figures are not on Standard
    expect(layout.available.ready.map((c) => c.id)).toEqual(['spendSoFar', 'costPerDistance', 'consumptionTrend', 'distanceYearEarlier', 'distanceAndConsumption'])
  })

  it('offers Spending and Usage: the trend figures with the charts that go with them, in the same shape as Standard', () => {
    const ids = (tiles: readonly Tile[]) => tiles.map((t) => `${t.id}@${t.x},${t.y}`)

    expect(ids(LAYOUT_PRESETS.spending.tiles)).toEqual([
      'thisMonth@0,0', 'spendingTrend@1,0', 'costPerDistance@2,0', 'lastFillUp@3,0',
      'preset:monthlyCosts@0,4', 'preset:spendSoFar@2,4', 'preset:categories@0,12', 'preset:costPerDistance@2,12', 'preset:fuelPrice@0,20',
    ])
    expect(ids(LAYOUT_PRESETS.usage.tiles)).toEqual([
      'odometer@0,0', 'distanceTrend@1,0', 'consumption@2,0', 'consumptionTrend@3,0',
      'preset:distanceYearEarlier@0,4', 'preset:consumptionTrend@2,4', 'preset:distanceAndConsumption@0,12', 'preset:distance@2,12',
    ])
    expect(READY_CHARTS.find((c) => c.id === 'spendSoFar')?.recipe).toMatchObject({ range: 'THIS_YEAR', runningTotal: true, compareYearEarlier: true })
    expect(READY_CHARTS.find((c) => c.id === 'distanceAndConsumption')?.recipe).toMatchObject({ secondMetric: 'AVERAGE_CONSUMPTION', secondKind: 'LINE' })
  })

  it('follows a stored layout: its places, saved charts it does not mention at the bottom, the ones taken off left out', () => {
    const stored = { tiles: [tile('odometer', 3, 0, 1, 3), tile(chartKey('b'), 0, 0), tile(presetKey('distance'), 2, 3)], hidden: [chartKey('c')] }
    const layout = resolveLayout(stored, [chart('a'), chart('b'), chart('c')])

    expect(places(layout.tiles)).toEqual(['chart:b@0,0 2x8', 'odometer@3,0 1x3', 'preset:distance@2,3 2x8', 'chart:a@0,8 2x8']) // the figure keeps its three rows: it needs no more (#229)
    expect(layout.hidden.map((c) => c.id)).toEqual(['c'])
    expect(layout.available.figures).toEqual(['thisMonth', 'consumption', 'lastFillUp', 'spendingTrend', 'costPerDistance', 'consumptionTrend', 'distanceTrend'])
    expect(layout.available.ready.map((c) => c.id)).toEqual(['monthlyCosts', 'consumption', 'fuelPrice', 'categories', 'spendSoFar', 'costPerDistance', 'consumptionTrend', 'distanceYearEarlier', 'distanceAndConsumption'])
    expect(layout.available.saved.map((c) => c.id)).toEqual(['c'])
  })

  it('drops what it does not know, a chart deleted meanwhile and duplicates, and never shows a widget a layout does not name', () => {
    const stored = { tiles: [tile('odometer', 0, 0, 1, 3), tile('tyrePressure', 1, 0, 1, 3), tile('odometer', 2, 0, 1, 3), tile(presetKey('future'), 0, 3), tile(chartKey('gone'), 2, 3)], hidden: [chartKey('gone')] }
    const layout = resolveLayout(stored, [])

    expect(places(layout.tiles)).toEqual(['odometer@0,0 1x3'])
    expect(layout.hidden).toEqual([])
  })

  it('stores what is on screen, and Standard holds every figure and ready-made chart', () => {
    const layout = resolveLayout({ tiles: [tile('lastFillUp', 0, 0, 1, 4, true)], hidden: [chartKey('b')] }, [chart('a'), chart('b')])

    expect(toLayout(layout.tiles, layout.hidden)).toEqual({ tiles: [tile('lastFillUp', 0, 0, 1, 4, true), tile(chartKey('a'), 1, 0)], hidden: [chartKey('b')] })
    expect(LAYOUT_PRESETS.standard.tiles.map((t) => `${t.id}@${t.x},${t.y}`)).toEqual([
      'thisMonth@0,0', 'consumption@1,0', 'odometer@2,0', 'lastFillUp@3,0',
      'preset:monthlyCosts@0,4', 'preset:consumption@2,4', 'preset:fuelPrice@0,12', 'preset:categories@2,12', 'preset:distance@0,20',
    ])
  })

  it('keeps a trend figure\'s own period through the merge and the packing, and stores it as GraphQL takes it, the default as none (#230)', () => {
    const weeks = { kind: 'LAST', unit: 'WEEKS', count: 8, since: null, whole: false, against: 'YEAR_EARLIER' } as const
    const readBack = { __typename: 'TrendPeriod', ...weeks } // as the server answers it
    const stored = { tiles: [{ ...tile('spendingTrend', 0, 5, 1, 4), period: readBack }, { ...tile('consumptionTrend', 1, 0, 1, 4), period: DEFAULT_PERIOD.consumptionTrend }, tile('odometer', 2, 0, 1, 3)], hidden: [] }
    const layout = resolveLayout(stored, [])

    expect(layout.tiles.find((t) => t.id === 'spendingTrend')).toMatchObject({ y: 0, period: weeks }) // lifted by the packing, the period kept
    expect(toLayout(layout.tiles, []).tiles).toEqual([
      { id: 'spendingTrend', x: 0, y: 0, w: 1, h: 4, pinned: false, period: weeks }, // no __typename: the server refuses it in an input
      { id: 'consumptionTrend', x: 1, y: 0, w: 1, h: 4, pinned: false }, // its default: none stored
      tile('odometer', 2, 0, 1, 3),
    ])
    expect(toLayout([{ ...tile('odometer', 0, 0, 1, 3), period: weeks }], []).tiles).toEqual([tile('odometer', 0, 0, 1, 3)]) // never on a figure without one
    expect(addTile([], 'distanceTrend', 4, weeks)).toEqual([{ ...tile('distanceTrend', 0, 0, 1, 4), period: weeks }])
    expect(addTile([], 'distanceTrend', 4, DEFAULT_PERIOD.distanceTrend)).toEqual([tile('distanceTrend', 0, 0, 1, 4)])
    expect(setTilePeriod(layout.tiles, 'spendingTrend', null).find((t) => t.id === 'spendingTrend')?.period).toBeNull()
  })
})

describe('pack and flow', () => {
  it('packs tiles up into the gaps at their own columns, keeps a pinned one where it is, and never overlaps', () => {
    const tiles = [tile('a', 0, 5, 2, 3), tile('b', 2, 0, 2, 3, true), tile('c', 2, 9, 2, 3), tile('d', 3, 20, 2, 3)]

    expect(places(pack(tiles))).toEqual(['a@0,0 2x3', 'b@2,0 2x3 pinned', 'c@2,3 2x3', 'd@2,6 2x3']) // d clamped into the grid
  })

  it('flows the tiles into fewer columns in reading order, and back into four with the new order and the old sizes', () => {
    const desktop = [tile('a', 0, 0, 1, 3, true), tile('b', 1, 0, 1, 3), tile('c', 0, 3, 2, 8), tile('d', 2, 3, 2, 8)]

    expect(places(flow(desktop, 1))).toEqual(['a@0,0 1x3', 'b@0,3 1x3', 'c@0,6 1x8', 'd@0,14 1x8']) // one column: one under the other, no pins
    expect(places(flow(desktop, 2))).toEqual(['a@0,0 1x3', 'b@1,0 1x3', 'c@0,3 2x8', 'd@0,11 2x8'])
    const reordered = [tile('d', 0, 0, 1, 8), tile('a', 0, 8, 1, 3, true), tile('b', 0, 11, 1, 3), tile('c', 0, 14, 1, 8)]
    expect(places(flow(reordered.map((t) => ({ ...t, w: desktop.find((o) => o.id === t.id)!.w })), 4))).toEqual(['d@0,0 2x8', 'a@2,0 1x3 pinned', 'b@3,0 1x3', 'c@2,3 2x8'])
  })
})

describe('the reading order', () => {
  it('holds when a tile is moved past a taller one, and when a phone column goes back into four columns', () => {
    const tiles = [tile('a', 0, 0, 2, 8), tile('c', 2, 0, 1, 3), tile('b', 0, 8, 4, 3)] // a and c side by side, b under both

    expect(places(moveTile(tiles, 'c', 1))).toEqual(['a@0,0 2x8', 'b@0,8 4x3', 'c@0,11 1x3']) // c after b: never lifted back beside a
    expect(places(moveTile(tiles, 'b', -1))).toEqual(['a@0,0 2x8', 'b@0,8 4x3', 'c@0,11 1x3'])
    const phone = [tile('a', 0, 0, 2, 8), tile('b', 0, 8, 4, 3), tile('c', 0, 11, 1, 3)]
    expect(places(flow(phone, 4))).toEqual(['a@0,0 2x8', 'b@0,8 4x3', 'c@0,11 1x3'])
    expect(places(flow([tile('a', 0, 0, 1, 8), tile('b', 0, 8, 1, 3), tile('c', 0, 11, 1, 3)], 4))).toEqual(['a@0,0 1x8', 'b@1,0 1x3', 'c@2,0 1x3']) // beside, when that is after
  })
})

describe('the tile menu', () => {
  const start = [tile('thisMonth', 0, 0, 1, 3), tile('odometer', 1, 0, 1, 3), tile(presetKey('distance'), 0, 3)]

  it('adds a widget at the bottom at its default size, and takes one off', () => {
    expect(places(addTile(start, 'lastFillUp'))).toContain('lastFillUp@0,11 1x3') // a plain figure shows in full at three rows
    expect(places(addTile(start, 'spendingTrend'))).toContain('spendingTrend@0,11 1x4') // a trend figure's comparison line and note take four
    expect(places(addTile(start, chartKey('x')))).toContain('chart:x@0,11 2x8')
    expect(places(removeTile(start, 'thisMonth'))).toEqual(['odometer@1,0 1x3', 'preset:distance@0,3 2x8'])
  })

  it('moves a tile earlier or later in the reading order, and not past the ends', () => {
    expect(places(moveTile(start, presetKey('distance'), -1)).map((s) => s.split('@')[0])).toEqual(['thisMonth', 'preset:distance', 'odometer'])
    expect(places(moveTile(start, 'thisMonth', -1))).toEqual(places(start))
    expect(places(moveTile(start, presetKey('distance'), 1))).toEqual(places(start))
  })

  it('lets every figure go down to two rows, gives each the rows it shows in full, and lifts a stored figure only up to two (#229, #259)', () => {
    // Every figure fits what it shows to its card by measuring (#259), so the least is its label and value: two rows, whatever it lists.
    expect(Object.fromEntries(FIGURES.map((f) => [f, `${minSize(f).h}..${defaultSize(f).h}`]))).toEqual({
      thisMonth: '2..4',
      consumption: '2..3',
      odometer: '2..3',
      lastFillUp: '2..3',
      spendingTrend: '2..4',
      costPerDistance: '2..4',
      consumptionTrend: '2..4',
      distanceTrend: '2..4',
    })
    expect(MIN_FIGURE_ROWS).toBe(2)
    expect(minSize(presetKey('distance'))).toEqual({ w: 1, h: 5 })
    expect(defaultSize(chartKey('x'))).toEqual({ w: 2, h: 8 })
    // Shorter stops at two rows for every figure, the ones whose words wrap in a desktop column too.
    const row = [tile('odometer', 0, 0, 1, 3), tile('thisMonth', 1, 0, 1, 4), tile('costPerDistance', 2, 0, 1, 4)]
    expect(places(resizeTile(resizeTile(row, 'odometer', { h: -1 }), 'odometer', { h: -1 }))).toContain('odometer@0,0 1x2')
    const lowered = resizeTile(resizeTile(resizeTile(row, 'thisMonth', { h: -1 }), 'thisMonth', { h: -1 }), 'thisMonth', { h: -1 })
    expect(places(lowered)).toContain('thisMonth@1,0 1x2')
    expect(places(resizeTile(resizeTile(resizeTile(row, 'costPerDistance', { h: -1 }), 'costPerDistance', { h: -1 }), 'costPerDistance', { h: -1 }))).toContain('costPerDistance@2,0 1x2')
    // A stored arrangement is kept as it was saved down to two rows, and only a figure stored lower is raised to them.
    expect(places(resolveLayout({ tiles: [tile('costPerDistance', 0, 0, 1, 3), tile('odometer', 1, 0, 1, 1)], hidden: [] }, []).tiles)).toEqual(['costPerDistance@0,0 1x3', 'odometer@1,0 1x2'])
  })

  it('resizes a tile by a step within its least size and the grid, and pins it', () => {
    expect(places(resizeTile(start, presetKey('distance'), { w: 1, h: 1 }))).toContain('preset:distance@0,3 3x9')
    expect(places(resizeTile(start, presetKey('distance'), { w: -1 }))).toContain('preset:distance@0,3 1x8')
    expect(places(resizeTile(start, 'thisMonth', { w: -1, h: -1 }))).toContain('thisMonth@0,0 1x2') // no lower than two rows, its label and value (#259)
    expect(places(resizeTile([tile('odometer', 3, 0, 1, 3)], 'odometer', { w: 1 }))).toEqual(['odometer@2,0 2x3']) // widened at the right edge: moved left
    expect(pinTile(start, 'odometer', true).find((t) => t.id === 'odometer')?.pinned).toBe(true)
  })

  it('puts a chart of one\'s own in a ready-made chart\'s place: the same place, size and pin, nothing else moved (#247)', () => {
    const tiles = [tile('odometer', 0, 0, 1, 3), tile(presetKey('consumptionTrend'), 2, 4, 2, 8, true), tile(presetKey('distance'), 0, 12)]
    expect(places(replaceTile(tiles, presetKey('consumptionTrend'), chartKey('c9')))).toEqual(['odometer@0,0 1x3', 'chart:c9@2,4 2x8 pinned', 'preset:distance@0,12 2x8'])
    expect(replaceTile([{ ...tile('spendingTrend', 0, 0, 1, 4), period: DEFAULT_PERIOD.spendingTrend }], 'spendingTrend', chartKey('c9'))[0]).not.toHaveProperty('period')
    expect(places(replaceTile(tiles, presetKey('fuelPrice'), chartKey('c9')))).toEqual(places(tiles))
  })
})
