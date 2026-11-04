import { createContext, createElement, useContext, type ReactNode } from 'react'

/**
 * The strip across the top of a tile while the dashboard is edited (#228): its height, and the top padding a card takes to start under it.
 * The strip is the tile's header (#262: the tile is as tall as in view mode), so the card starts right under it: a two-row figure (84 px)
 * keeps its value (30 px) and its bottom padding (12 px) under 40 px of strip, with 2 px to spare.
 */
export const TILE_STRIP = 40
export const TILE_INSET = TILE_STRIP

/**
 * Where a tile's content puts what the edit strip carries while the dashboard is arranged (#262): `lead` after the tile's name (a trend
 * figure's period chip), `actions` before its menu (a chart's table, edit and delete buttons). The content portals them there, so they stay
 * in its own React tree (the chip remains its popover's anchor, the table button keeps its data). Each is null until the strip has mounted.
 */
export interface TileStrip {
  lead: HTMLElement | null
  actions: HTMLElement | null
}

/** Set inside a tile that carries the edit strip, null otherwise (view mode, and a dialog or popover opened from a tile: `OutsideTile`). */
export const TileStripContext = createContext<TileStrip | null>(null)

/** The tile's strip while the dashboard is arranged, or null. */
export const useTileStrip = () => useContext(TileStripContext)

/** The top padding of a card in a tile: under the strip while editing, its own otherwise. */
export const useTilePadding = (own: number | string) => (useContext(TileStripContext) ? `${TILE_INSET}px` : own)

/**
 * What a dialog or a popover opened from a tile shows (the Edit chart dialog, a trend figure's period popover with its preview): portalled
 * out of the tile but not out of its React tree, so it leaves the strip behind and draws its cards as it would anywhere else.
 */
export const OutsideTile = ({ children }: { children: ReactNode }) => createElement(TileStripContext.Provider, { value: null }, children)
