import { createContext, useContext } from 'react'

/** The strip across the top of a tile while the dashboard is edited (#228): its height, and the top padding a card takes to start under it. */
export const TILE_STRIP = 40
export const TILE_INSET = TILE_STRIP + 8

/** True inside a tile that carries the edit strip: a card under it starts `TILE_INSET` px down instead of its own padding. */
export const TileInsetContext = createContext(false)

/**
 * The place on a tile's edit strip for what the tile shows about itself while the dashboard is arranged (#260: a trend figure's period
 * chip, which outside Customize is not shown); null outside Customize, or until the strip is drawn.
 */
export const TileStripSlotContext = createContext<HTMLElement | null>(null)

/** The top padding of a card in a tile: under the strip while editing, its own otherwise. */
export const useTilePadding = (own: number | string) => (useContext(TileInsetContext) ? `${TILE_INSET}px` : own)
