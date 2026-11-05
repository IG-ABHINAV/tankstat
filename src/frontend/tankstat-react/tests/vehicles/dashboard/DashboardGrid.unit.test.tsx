import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { ReactElement, ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Layout, LayoutItem } from 'react-grid-layout'
import { DashboardGrid } from '../../../src/dashboard/DashboardGrid.tsx'
import { OutsideTile, useTileStrip } from '../../../src/dashboard/tileChrome.ts'
import type { Tile } from '../../../src/dashboard/widgets.ts'
import { ThemeRoot } from '../../../src/theme/ThemeRoot.tsx'

// jsdom lays nothing out, so react-grid-layout never drags here: the grid is stood in for by a component that keeps the props the
// dashboard hands it, and the test calls its callbacks as the library would during a drag or a resize. Where the tiles are then
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

describe("a tile's menu", () => {
  it('gives a tile its pencil and its bin beside the menu, a narrow one both in its menu, and the tag under the strip (#275)', async () => {
    const ui = userEvent.setup()
    const onAction = vi.fn()
    themed(<DashboardGrid tiles={[tile('preset:monthlyCosts', 0, 0), tile('chart:c1', 2, 0), tile('odometer', 0, 8, 1, 3)]} editing render={show} name={name} editKind={(t) => (t.id === 'chart:c1' ? 'customize' : 'edit')} badge={(t) => (t.id === 'chart:c1' ? 'Saved' : null)} onAction={onAction} />)

    await ui.click(screen.getByRole('button', { name: 'Customize chart:c1' }))
    await ui.click(screen.getByRole('button', { name: 'Remove preset:monthlyCosts from this dashboard' }))
    // One column of the four (the user's choice): no pencil or bin, its menu starts with Edit…, done once the menu has gone.
    expect(screen.queryByRole('button', { name: 'Edit odometer' })).not.toBeInTheDocument()
    await ui.click(screen.getByRole('button', { name: 'Options for odometer' }))
    const items = await screen.findAllByRole('menuitem')
    expect(items[0]).toHaveTextContent('Edit…')
    await ui.click(items[0])
    await waitFor(() => expect(screen.queryByRole('menu')).not.toBeInTheDocument())
    await waitFor(() => expect(onAction.mock.calls.map(([t, a]) => `${t.id} ${a}`)).toEqual(['chart:c1 edit', 'preset:monthlyCosts remove', 'odometer edit']))
    // The tag under the strip (#275, the user's choice C) on the tile that has one, none on the others; no Customize as my chart any more.
    expect(document.querySelector('[data-tile="chart:c1"] [data-badge]')).toHaveTextContent('Saved')
    expect(document.querySelectorAll('[data-badge]')).toHaveLength(1)
    await ui.click(screen.getByRole('button', { name: 'Options for preset:monthlyCosts' }))
    expect(await screen.findByRole('menuitem', { name: 'Remove' })).toBeInTheDocument()
    expect(screen.queryByRole('menuitem', { name: /Customize/ })).not.toBeInTheDocument()
  })


})

describe('DashboardGrid while customizing: the strip is the tile\'s header (#262)', () => {
  /** A tile's content that puts a button into each of the strip's slots, as a trend figure's chip and a chart's buttons do. */
  const renders = vi.fn()
  function Content({ id }: { id: string }) {
    const strip = useTileStrip()
    renders(id)
    if (!strip) return <span>{`${id} own header`}</span>
    return (
      <>
        {strip.lead && createPortal(<button type="button">{`${id} chip`}</button>, strip.lead)}
        {strip.actions && createPortal(<button type="button">{`${id} table`}</button>, strip.actions)}
      </>
    )
  }
  const content = (t: Tile) => <Content id={t.id} />
  const MIXED = [tile('preset:monthlyCosts', 0, 0), tile('odometer', 2, 0, 1, 2)]
  afterEach(() => renders.mockClear())

  it('keeps the rows as tall as in view mode', () => {
    const { rerender } = themed(<DashboardGrid tiles={MIXED} editing={false} render={content} name={name} />)
    const viewRows = (latest() as unknown as { rowHeight: number }).rowHeight
    rerender(<ThemeRoot instant><DashboardGrid tiles={MIXED} editing render={content} name={name} /></ThemeRoot>)
    expect((latest() as unknown as { rowHeight: number }).rowHeight).toBe(viewRows)
  })

  it('names the tile in its strip, a chart as its heading, and puts the content\'s chip and buttons, the pencil and the bin there in order', async () => {
    themed(<DashboardGrid tiles={MIXED} editing render={content} name={name} />)
    const chart = document.querySelector<HTMLElement>('[data-tile="preset:monthlyCosts"]')!
    expect(await within(chart).findByRole('button', { name: 'preset:monthlyCosts chip' })).toBeInTheDocument()
    expect(within(chart).getByRole('heading', { level: 2, name: 'preset:monthlyCosts' })).toBeInTheDocument()
    // The strip's buttons in reading order: the chip after the name, the content's actions before the menu.
    expect(within(chart).getAllByRole('button').map((b) => b.textContent || b.getAttribute('aria-label'))).toEqual([
      'preset:monthlyCosts chip',
      'preset:monthlyCosts table',
      'Edit preset:monthlyCosts',
      'Remove preset:monthlyCosts from this dashboard',
      'Options for preset:monthlyCosts',
    ])
    // A figure's name is text, not a heading; the content draws no header of its own while the strip is there.
    const figure = document.querySelector<HTMLElement>('[data-tile="odometer"]')!
    expect(within(figure).getByText('odometer')).toBeInTheDocument()
    expect(within(figure).queryByRole('heading')).not.toBeInTheDocument()
    expect(screen.queryByText(/own header/)).not.toBeInTheDocument()
    // The handle (the name and the grip) holds no button: a pointer never drags a tile by one.
    for (const handle of document.querySelectorAll('.tk-tile-handle')) expect(handle.querySelector('button')).toBeNull()
  })

  it('gives the content no strip outside Customize, or in a dialog opened from a tile', () => {
    themed(<DashboardGrid tiles={MIXED} editing={false} render={content} name={name} />)
    expect(screen.getByText('preset:monthlyCosts own header')).toBeInTheDocument()
    cleanup()
    themed(<DashboardGrid tiles={MIXED} editing render={(t) => <OutsideTile>{content(t)}</OutsideTile>} name={name} />)
    expect(screen.getByText('preset:monthlyCosts own header')).toBeInTheDocument()
  })

  it('does not render a tile\'s content again on a drag\'s moves (#227): the strip it is given keeps its identity', async () => {
    themed(<DashboardGrid tiles={MIXED} editing render={content} name={name} />)
    await screen.findByRole('button', { name: 'odometer chip' })
    renders.mockClear()
    act(() => latest().onDrag([], null, at('odometer', 1, 0, 1, 2)))
    act(() => latest().onDrag([], null, at('odometer', 0, 8, 1, 2)))
    expect(renders).not.toHaveBeenCalled()
  })
})
