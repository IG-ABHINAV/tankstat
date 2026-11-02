import { describe, expect, it } from 'vitest'
import { FIGURES, LAYOUT_PRESETS, READY_CHARTS, resolveLayout, toLayout } from '../../../src/dashboard/widgets.ts'

const chart = (id: string) => ({ id, title: `Chart ${id}` })
const keys = (items: { key: string }[]) => items.map((i) => i.key)

describe('resolveLayout', () => {
  it('gives the Standard preset with nothing stored: the figures and ready-made charts as they always were, then every saved chart', () => {
    const layout = resolveLayout(null, [chart('a'), chart('b')])

    expect(layout.figures).toEqual(['thisMonth', 'consumption', 'odometer', 'lastFillUp'])
    expect(keys(layout.charts)).toEqual(['preset:monthlyCosts', 'preset:consumption', 'preset:fuelPrice', 'preset:categories', 'preset:distance', 'chart:a', 'chart:b'])
    expect(layout.available).toEqual({ figures: [], ready: [] })
  })

  it('follows a stored layout: its order, saved charts it does not mention at the end, the ones taken off left out', () => {
    const layout = resolveLayout({ figures: ['odometer', 'thisMonth'], charts: ['chart:b', 'preset:distance'], hidden: ['chart:c'] }, [chart('a'), chart('b'), chart('c')])

    expect(layout.figures).toEqual(['odometer', 'thisMonth'])
    expect(keys(layout.charts)).toEqual(['chart:b', 'preset:distance', 'chart:a']) // a new chart is never lost
    expect(layout.hidden.map((c) => c.id)).toEqual(['c'])
    expect(layout.available.figures).toEqual(['consumption', 'lastFillUp'])
    expect(layout.available.ready.map((c) => c.id)).toEqual(['monthlyCosts', 'consumption', 'fuelPrice', 'categories'])
  })

  it('drops what it does not know, a chart deleted meanwhile and duplicates, and never shows a widget a layout does not name', () => {
    const layout = resolveLayout({ figures: ['odometer', 'tyrePressure', 'odometer'], charts: ['preset:future', 'chart:gone', 'preset:distance', 'preset:distance'], hidden: ['chart:gone'] }, [])

    expect(layout.figures).toEqual(['odometer'])
    expect(keys(layout.charts)).toEqual(['preset:distance'])
    expect(layout.hidden).toEqual([])
  })

  it('stores what is arranged on screen, and Standard holds every figure and ready-made chart', () => {
    const layout = resolveLayout({ figures: ['lastFillUp'], charts: ['chart:a', 'preset:fuelPrice'], hidden: ['chart:b'] }, [chart('a'), chart('b')])

    expect(toLayout(layout.figures, layout.charts, layout.hidden)).toEqual({ figures: ['lastFillUp'], charts: ['chart:a', 'preset:fuelPrice'], hidden: ['chart:b'] })
    expect(LAYOUT_PRESETS.standard).toEqual({ figures: [...FIGURES], charts: READY_CHARTS.map((c) => `preset:${c.id}`), hidden: [] })
  })
})
