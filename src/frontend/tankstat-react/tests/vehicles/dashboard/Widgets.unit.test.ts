import { describe, expect, it } from 'vitest'
import { addTile, chartKey, flow, LAYOUT_PRESETS, moveTile, pack, pinTile, presetKey, READY_CHARTS, removeTile, resizeTile, resolveLayout, toLayout, type Tile } from '../../../src/dashboard/widgets.ts'

const chart = (id: string) => ({ id, title: `Chart ${id}` })
const tile = (id: string, x: number, y: number, w = 2, h = 8, pinned = false): Tile => ({ id, x, y, w, h, pinned })
const places = (tiles: Tile[]) => tiles.map((t) => `${t.id}@${t.x},${t.y} ${t.w}x${t.h}${t.pinned ? ' pinned' : ''}`)

describe('resolveLayout', () => {
  it('gives the Standard preset with nothing stored: the figures in a row, the ready-made charts two by two, then every saved chart', () => {
    const layout = resolveLayout(null, [chart('a'), chart('b'), chart('c')])

    expect(places(layout.tiles)).toEqual([
      'thisMonth@0,0 1x3',
      'consumption@1,0 1x3',
      'odometer@2,0 1x3',
      'lastFillUp@3,0 1x3',
      'preset:monthlyCosts@0,3 2x8',
      'preset:consumption@2,3 2x8',
      'preset:fuelPrice@0,11 2x8',
      'preset:categories@2,11 2x8',
      'preset:distance@0,19 2x8',
      'chart:a@2,19 2x8', // the saved charts fill in below, side by side
      'chart:b@0,27 2x8',
      'chart:c@2,27 2x8',
    ])
    expect(layout.available.saved).toEqual([])
    expect(layout.available.figures).toEqual(['spendingTrend', 'costPerDistance', 'consumptionTrend', 'distanceTrend']) // the trend figures are not on Standard
    expect(layout.available.ready.map((c) => c.id)).toEqual(['spendSoFar', 'costPerDistance', 'consumptionTrend', 'distanceYearEarlier', 'distanceAndConsumption'])
  })

  it('offers Spending and Usage: the trend figures with the charts that go with them, in the same shape as Standard', () => {
    const ids = (tiles: readonly Tile[]) => tiles.map((t) => `${t.id}@${t.x},${t.y}`)

    expect(ids(LAYOUT_PRESETS.spending.tiles)).toEqual([
      'thisMonth@0,0', 'spendingTrend@1,0', 'costPerDistance@2,0', 'lastFillUp@3,0',
      'preset:monthlyCosts@0,3', 'preset:spendSoFar@2,3', 'preset:categories@0,11', 'preset:costPerDistance@2,11', 'preset:fuelPrice@0,19',
    ])
    expect(ids(LAYOUT_PRESETS.usage.tiles)).toEqual([
      'odometer@0,0', 'distanceTrend@1,0', 'consumption@2,0', 'consumptionTrend@3,0',
      'preset:distanceYearEarlier@0,3', 'preset:consumptionTrend@2,3', 'preset:distanceAndConsumption@0,11', 'preset:distance@2,11',
    ])
    expect(READY_CHARTS.find((c) => c.id === 'spendSoFar')?.recipe).toMatchObject({ range: 'THIS_YEAR', runningTotal: true, compareYearEarlier: true })
    expect(READY_CHARTS.find((c) => c.id === 'distanceAndConsumption')?.recipe).toMatchObject({ secondMetric: 'AVERAGE_CONSUMPTION', secondKind: 'LINE' })
  })

  it('follows a stored layout: its places, saved charts it does not mention at the bottom, the ones taken off left out', () => {
    const stored = { tiles: [tile('odometer', 3, 0, 1, 3), tile(chartKey('b'), 0, 0), tile(presetKey('distance'), 2, 3)], hidden: [chartKey('c')] }
    const layout = resolveLayout(stored, [chart('a'), chart('b'), chart('c')])

    expect(places(layout.tiles)).toEqual(['chart:b@0,0 2x8', 'odometer@3,0 1x3', 'preset:distance@2,3 2x8', 'chart:a@0,8 2x8'])
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
    const layout = resolveLayout({ tiles: [tile('lastFillUp', 0, 0, 1, 3, true)], hidden: [chartKey('b')] }, [chart('a'), chart('b')])

    expect(toLayout(layout.tiles, layout.hidden)).toEqual({ tiles: [tile('lastFillUp', 0, 0, 1, 3, true), tile(chartKey('a'), 1, 0)], hidden: [chartKey('b')] })
    expect(LAYOUT_PRESETS.standard.tiles.map((t) => `${t.id}@${t.x},${t.y}`)).toEqual([
      'thisMonth@0,0', 'consumption@1,0', 'odometer@2,0', 'lastFillUp@3,0',
      'preset:monthlyCosts@0,3', 'preset:consumption@2,3', 'preset:fuelPrice@0,11', 'preset:categories@2,11', 'preset:distance@0,19',
    ])
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

describe('the tile menu', () => {
  const start = [tile('thisMonth', 0, 0, 1, 3), tile('odometer', 1, 0, 1, 3), tile(presetKey('distance'), 0, 3)]

  it('adds a widget at the bottom at its default size, and takes one off', () => {
    expect(places(addTile(start, 'lastFillUp'))).toContain('lastFillUp@0,11 1x3')
    expect(places(addTile(start, chartKey('x')))).toContain('chart:x@0,11 2x8')
    expect(places(removeTile(start, 'thisMonth'))).toEqual(['odometer@1,0 1x3', 'preset:distance@0,3 2x8'])
  })

  it('moves a tile earlier or later in the reading order, and not past the ends', () => {
    expect(places(moveTile(start, presetKey('distance'), -1)).map((s) => s.split('@')[0])).toEqual(['thisMonth', 'preset:distance', 'odometer'])
    expect(places(moveTile(start, 'thisMonth', -1))).toEqual(places(start))
    expect(places(moveTile(start, presetKey('distance'), 1))).toEqual(places(start))
  })

  it('resizes a tile by a step within its least size and the grid, and pins it', () => {
    expect(places(resizeTile(start, presetKey('distance'), { w: 1, h: 1 }))).toContain('preset:distance@0,3 3x9')
    expect(places(resizeTile(start, presetKey('distance'), { w: -1 }))).toContain('preset:distance@0,3 1x8')
    expect(places(resizeTile(start, 'thisMonth', { w: -1, h: -1 }))).toContain('thisMonth@0,0 1x3') // no smaller than a figure needs
    expect(places(resizeTile([tile('odometer', 3, 0, 1, 3)], 'odometer', { w: 1 }))).toEqual(['odometer@2,0 2x3']) // widened at the right edge: moved left
    expect(pinTile(start, 'odometer', true).find((t) => t.id === 'odometer')?.pinned).toBe(true)
  })
})
