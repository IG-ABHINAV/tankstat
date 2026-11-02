import { describe, expect, it } from 'vitest'
import { addTile, chartKey, flow, LAYOUT_PRESETS, moveTile, pack, pinTile, presetKey, removeTile, resizeTile, resolveLayout, toLayout, type Tile } from '../../../src/dashboard/widgets.ts'

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
    expect(layout.available).toEqual({ figures: [], ready: [], saved: [] })
  })

  it('follows a stored layout: its places, saved charts it does not mention at the bottom, the ones taken off left out', () => {
    const stored = { tiles: [tile('odometer', 3, 0, 1, 3), tile(chartKey('b'), 0, 0), tile(presetKey('distance'), 2, 3)], hidden: [chartKey('c')] }
    const layout = resolveLayout(stored, [chart('a'), chart('b'), chart('c')])

    expect(places(layout.tiles)).toEqual(['chart:b@0,0 2x8', 'odometer@3,0 1x3', 'preset:distance@2,3 2x8', 'chart:a@0,8 2x8'])
    expect(layout.hidden.map((c) => c.id)).toEqual(['c'])
    expect(layout.available.figures).toEqual(['thisMonth', 'consumption', 'lastFillUp'])
    expect(layout.available.ready.map((c) => c.id)).toEqual(['monthlyCosts', 'consumption', 'fuelPrice', 'categories'])
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
    expect(LAYOUT_PRESETS.standard.tiles.map((t) => t.id)).toEqual(['thisMonth', 'consumption', 'odometer', 'lastFillUp', 'preset:monthlyCosts', 'preset:consumption', 'preset:fuelPrice', 'preset:categories', 'preset:distance'])
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
    expect(places(addTile(start, 'lastFillUp'))).toContain('lastFillUp@0,11 1x4')
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
    expect(places(resizeTile(start, 'thisMonth', { w: -1, h: -1 }))).toContain('thisMonth@0,0 1x4') // no smaller than a figure needs (four rows: this one was stored lower)
    expect(places(resizeTile([tile('odometer', 3, 0, 1, 3)], 'odometer', { w: 1 }))).toEqual(['odometer@2,0 2x4']) // widened at the right edge: moved left (and no lower than a figure needs)
    expect(pinTile(start, 'odometer', true).find((t) => t.id === 'odometer')?.pinned).toBe(true)
  })
})
