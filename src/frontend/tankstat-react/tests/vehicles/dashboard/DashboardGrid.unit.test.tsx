import { act, render } from '@testing-library/react'
import type { ReactElement, ReactNode } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Layout, LayoutItem } from 'react-grid-layout'
import { DashboardGrid } from '../../../src/dashboard/DashboardGrid.tsx'
import type { Tile } from '../../../src/dashboard/widgets.ts'
import { ThemeRoot } from '../../../src/theme/ThemeRoot.tsx'

// jsdom lays nothing out, so react-grid-layout never drags here: the grid is stood in for by a component that keeps the props the
// dashboard hands it, and the test calls its callbacks as the library would during a drag, a resize or a drop. Where the tiles are then
// drawn is the real browser's to check (`e2e/dashboard.e2e.ts`).
const grid = vi.hoisted(() => ({ width: 1280, props: [] as Record<string, unknown>[] }))
vi.mock('react-grid-layout', async (importOriginal) => ({
  ...(await importOriginal<typeof import('react-grid-layout')>()),
  Responsive: (props: Record<string, unknown> & { children: ReactNode }) => {
    grid.props.push(props)
    return <div>{props.children}</div>
  },
  useContainerWidth: () => ({ width: grid.width, containerRef: { current: null }, mounted: true }),
}))

const latest = () => grid.props[grid.props.length - 1] as {
  children: ReactElement[]
  onDrag: (layout: Layout, old: LayoutItem | null, item: LayoutItem | null) => void
  onResize: (layout: Layout, old: LayoutItem | null, item: LayoutItem | null) => void
  onDragStop: (layout: Layout) => void
  onResizeStop: (layout: Layout) => void
  onDrop: (layout: Layout, item: LayoutItem | undefined) => void
}
const tile = (id: string, x: number, y: number, w = 2, h = 8, pinned = false): Tile => ({ id, x, y, w, h, pinned })
const at = (i: string, x: number, y: number, w = 2, h = 8): LayoutItem => ({ i, x, y, w, h })
// Kept across renders, as the page keeps them: the tiles' elements are made again whenever these change.
const show = (t: Tile) => <span>{t.id}</span>
const name = (t: Tile) => t.id
/** The grid in the app's theme (its styles read the palette), without transitions. */
const themed = (ui: ReactElement) => render(<ThemeRoot instant>{ui}</ThemeRoot>)
const TILES = [tile('preset:monthlyCosts', 0, 0), tile('preset:consumption', 2, 0, 2, 8, true), tile('preset:fuelPrice', 0, 8)]

afterEach(() => {
  grid.props = []
  grid.width = 1280
})

describe('DashboardGrid while a tile is dragged or resized (#227, #240)', () => {
  it('gives only the moving tile a new element on each move, so the library draws it at the pointer, and settles it at the stop', () => {
    const onTilesChange = vi.fn()
    themed(<DashboardGrid tiles={TILES} editing render={show} name={name} onTilesChange={onTilesChange} />)
    const before = latest().children

    act(() => latest().onDrag([], null, at('preset:fuelPrice', 1, 8)))
    const moved = latest().children
    expect(moved[2]).not.toBe(before[2])
    expect([moved[0], moved[1]]).toEqual([before[0], before[1]])
    expect(moved[0]).toBe(before[0])
    expect(moved[1]).toBe(before[1])

    act(() => latest().onDrag([], null, at('preset:fuelPrice', 2, 8)))
    expect(latest().children[2]).not.toBe(moved[2]) // every move, not only the first

    act(() => latest().onDragStop([at('preset:monthlyCosts', 0, 0), at('preset:consumption', 2, 0), at('preset:fuelPrice', 2, 8)]))
    expect(latest().children[2]).toBe(before[2]) // drawn from its place in the grid again
    // The tiles as they are now, in reading order, the pin kept.
    expect(onTilesChange).toHaveBeenCalledWith([tile('preset:monthlyCosts', 0, 0), tile('preset:consumption', 2, 0, 2, 8, true), tile('preset:fuelPrice', 2, 8)])
  })

  it('follows a resize the same way, and gives the tiles at its stop', () => {
    const onTilesChange = vi.fn()
    themed(<DashboardGrid tiles={TILES} editing render={show} name={name} onTilesChange={onTilesChange} />)
    const before = latest().children

    act(() => latest().onResize([], null, at('preset:monthlyCosts', 0, 0, 3)))
    expect(latest().children[0]).not.toBe(before[0])
    expect(latest().children[2]).toBe(before[2])

    act(() => latest().onResizeStop([at('preset:monthlyCosts', 0, 0, 3), at('preset:consumption', 2, 0), at('preset:fuelPrice', 0, 8)]))
    expect(latest().children[0]).toBe(before[0])
    expect(onTilesChange).toHaveBeenCalledWith([tile('preset:monthlyCosts', 0, 0, 3), tile('preset:consumption', 2, 0, 2, 8, true), tile('preset:fuelPrice', 0, 8)])
  })

  it('leaves a widget dragged in from outside alone: the library draws its drop spot itself, and no drag stop ever ends it', () => {
    const onDrop = vi.fn()
    const dropping = { id: 'preset:distance', w: 2, h: 8 }
    themed(<DashboardGrid tiles={TILES} editing render={show} name={name} dropping={dropping} onDrop={onDrop} />)
    const before = latest().children

    act(() => latest().onDrag([], null, at('preset:distance', 0, 16)))
    act(() => latest().onDrag([], null, null)) // the library can call it without an item
    expect(latest().children).toEqual(before)
    latest().children.forEach((cell, i) => expect(cell).toBe(before[i]))

    // Dropped: the tiles with it where it landed, at the width it was brought in with.
    act(() => latest().onDrop([at('preset:monthlyCosts', 0, 0), at('preset:consumption', 2, 0), at('preset:fuelPrice', 0, 8), at('__dropping-elem__', 2, 8)], at('__dropping-elem__', 2, 8)))
    expect(onDrop).toHaveBeenCalledWith([tile('preset:monthlyCosts', 0, 0), tile('preset:consumption', 2, 0, 2, 8, true), tile('preset:fuelPrice', 0, 8), tile('preset:distance', 2, 8)])
  })

  it('on fewer columns gives the tiles in their new order, flowed back into four', () => {
    grid.width = 400 // a phone: one column
    const onTilesChange = vi.fn()
    themed(<DashboardGrid tiles={TILES} editing render={show} name={name} onTilesChange={onTilesChange} />)

    // Fuel price dragged to the top of the single column.
    act(() => latest().onDragStop([at('preset:fuelPrice', 0, 0, 1), at('preset:monthlyCosts', 0, 8, 1), at('preset:consumption', 0, 16, 1)]))
    const ids = (onTilesChange.mock.calls[0][0] as Tile[]).map((t) => t.id)
    expect(ids).toEqual(['preset:fuelPrice', 'preset:monthlyCosts', 'preset:consumption'])
    // Their desktop widths and the pin are kept: a phone's single column says nothing about them.
    expect((onTilesChange.mock.calls[0][0] as Tile[]).map((t) => [t.w, t.pinned])).toEqual([[2, false], [2, false], [2, true]])
  })
})
