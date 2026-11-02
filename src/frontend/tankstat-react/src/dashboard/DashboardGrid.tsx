import Box from '@mui/material/Box'
import Menu from '@mui/material/Menu'
import MenuItem from '@mui/material/MenuItem'
import Typography from '@mui/material/Typography'
import { EllipsisVertical, GripHorizontal, Pin } from 'lucide-react'
import { useId, useMemo, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Responsive, useContainerWidth, verticalCompactor, type Layout as GridItems, type LayoutItem } from 'react-grid-layout'
import { IconAction } from '../components/IconAction.tsx'
import { COLUMNS, flow, GAP, inReadingOrder, MAX_ROWS, MIN_SIZE, ROW_HEIGHT, widgetOf, type Tile } from './widgets.ts'

/** The grid's columns by the container's width: four on a desktop, two on a tablet, one on a phone. */
const BREAKPOINTS = { lg: 900, sm: 520, xs: 0 } as const
const COLS = { lg: COLUMNS, sm: 2, xs: 1 } as const
const FALLBACK_WIDTH = 1280
type Breakpoint = keyof typeof COLS

/** What the tile menu offers: the keyboard's and a screen reader's way to do what dragging does. */
export type TileAction = 'earlier' | 'later' | 'wider' | 'narrower' | 'taller' | 'shorter' | 'pin' | 'unpin' | 'remove'

const CLASS = { handle: 'tk-tile-handle' }

/**
 * The dashboard as a grid of tiles (react-grid-layout, #211): the tiles placed and sized as stored, packed by column, the columns fewer on
 * a tablet or a phone (the tiles flowed in reading order). In edit mode a tile is dragged by its handle, resized by its corner, and moved,
 * sized, pinned or removed by its menu, which is how it is done with a keyboard or a screen reader (the library has neither); a widget can
 * also be dragged in from outside (`dropping`). The library's stylesheet is written here in `sx`, since the app has no stylesheet of its
 * own, and its transitions are off under reduced motion.
 */
export function DashboardGrid({
  tiles,
  editing,
  render,
  name,
  onTilesChange,
  onAction,
  dropping,
  onDrop,
}: {
  tiles: readonly Tile[]
  editing: boolean
  /** What a tile shows, by its widget's id. */
  render: (tile: Tile) => ReactNode
  /** The name of a tile for its controls, by its widget's id. */
  name: (tile: Tile) => string
  /** After a drag or a resize: the tiles as they are now (in the four-column grid). */
  onTilesChange?: (tiles: Tile[]) => void
  onAction?: (tile: Tile, action: TileAction) => void
  /** A widget being dragged in from outside, with the size it will take. */
  dropping?: { id: string; w: number; h: number } | null
  /** It was dropped: the tiles with it at the spot it was dropped on. */
  onDrop?: (tiles: Tile[]) => void
}) {
  const { t } = useTranslation()
  const { width: measured, containerRef, mounted } = useContainerWidth()
  // Not laid out yet (jsdom, a tab out of sight): the desktop's columns, never a one-column grid by mistake.
  const width = measured > 0 ? measured : FALLBACK_WIDTH
  const [breakpoint, setBreakpoint] = useState<Breakpoint>('lg')
  const cols = COLS[breakpoint]
  const ordered = useMemo(() => inReadingOrder(tiles), [tiles])

  const item = (tile: Tile, inFour: boolean): LayoutItem => {
    const min = MIN_SIZE[widgetOf(tile.id)?.kind ?? 'saved']
    return { i: tile.id, x: tile.x, y: tile.y, w: tile.w, h: tile.h, minW: min.w, minH: min.h, maxH: MAX_ROWS, static: inFour && tile.pinned }
  }
  const layouts = useMemo(
    () => ({ lg: ordered.map((tile) => item(tile, true)), sm: flow(ordered, 2).map((tile) => item(tile, false)), xs: flow(ordered, 1).map((tile) => item(tile, false)) }),
    [ordered],
  )

  /** The tiles after the grid changed them: in the four-column grid as they are; on fewer columns their new order, flowed back into four. */
  const fromGrid = (layout: GridItems): Tile[] => {
    const byId = new Map(tiles.map((tile) => [tile.id, tile]))
    // A tile the grid knows keeps its pin (and, on fewer columns, its desktop width); one just dropped in is new.
    const known = (l: LayoutItem): Tile => byId.get(l.i) ?? { id: l.i, x: l.x, y: l.y, w: l.w, h: l.h, pinned: false }
    if (cols === COLUMNS) return inReadingOrder(layout.map((l) => ({ ...known(l), x: l.x, y: l.y, w: l.w, h: l.h })))
    const order = [...layout].sort((a, b) => a.y - b.y || a.x - b.x).map((l, i) => ({ ...known(l), h: l.h, y: i, x: 0 }))
    return flow(order, COLUMNS)
  }

  const styles = {
    '& .react-grid-layout': { position: 'relative', transition: 'height 200ms ease' },
    '& .react-grid-item': { transition: 'all 200ms ease', transitionProperty: 'left, top, width, height', boxSizing: 'border-box' },
    '& .react-grid-item.cssTransforms': { transitionProperty: 'transform, width, height' },
    '& .react-grid-item.resizing': { transition: 'none', zIndex: 1, willChange: 'width, height' },
    '& .react-grid-item.react-draggable-dragging': { transition: 'none', zIndex: 3, willChange: 'transform', cursor: 'grabbing' },
    '& .react-grid-item.dropping': { visibility: 'hidden' },
    '& .react-grid-item.react-grid-placeholder': { backgroundColor: 'primary.main', opacity: 0.2, borderRadius: '12px', transitionDuration: '100ms', zIndex: 2, userSelect: 'none' },
    '& .react-grid-item.react-grid-placeholder.placeholder-resizing': { transition: 'none' },
    [`& .${CLASS.handle}`]: { cursor: 'grab', touchAction: 'none' },
    // The corner that resizes a tile: 44 px for a finger, its mark drawn in the corner.
    '& .react-grid-item > .react-resizable-handle': { position: 'absolute', right: 0, bottom: 0, width: 44, height: 44, cursor: 'se-resize', touchAction: 'none', zIndex: 1 },
    '& .react-grid-item > .react-resizable-handle::after': { content: '""', position: 'absolute', right: 8, bottom: 8, width: 10, height: 10, borderRight: '2px solid', borderBottom: '2px solid', borderColor: 'text.secondary', borderBottomRightRadius: 3 },
    '@media (prefers-reduced-motion: reduce)': { '& .react-grid-layout, & .react-grid-item, & .react-grid-item.react-grid-placeholder': { transition: 'none' } },
  } as const

  return (
    <Box ref={containerRef} sx={styles}>
      {mounted && (
        <Responsive
          width={width}
          layouts={layouts}
          breakpoints={BREAKPOINTS}
          cols={COLS}
          rowHeight={ROW_HEIGHT}
          margin={[GAP, GAP]}
          containerPadding={[0, 0]}
          compactor={verticalCompactor}
          onBreakpointChange={(next) => setBreakpoint(next as Breakpoint)}
          dragConfig={{ enabled: editing, handle: `.${CLASS.handle}` }}
          resizeConfig={{ enabled: editing, handles: ['se'] }}
          dropConfig={{ enabled: editing && !!dropping, defaultItem: { w: dropping?.w ?? 2, h: dropping?.h ?? 8 } }}
          droppingItem={dropping ? { i: dropping.id, x: 0, y: 0, w: Math.min(dropping.w, cols), h: dropping.h } : undefined}
          onDragStop={(layout) => onTilesChange?.(fromGrid(layout))}
          onResizeStop={(layout) => onTilesChange?.(fromGrid(layout))}
          onDrop={(layout, dropped) => {
            if (!dropping || !dropped) return
            const placed = { id: dropping.id, x: dropped.x, y: dropped.y, w: dropped.w, h: dropped.h, pinned: false }
            onDrop?.(fromGrid([...layout.filter((l) => l.i !== dropped.i && l.i !== dropping.id), { ...dropped, i: dropping.id }]).map((tile) => (tile.id === dropping.id && cols === COLUMNS ? { ...tile, ...placed } : tile)))
          }}
        >
          {ordered.map((tile, i) => (
            <Box key={tile.id} data-tile={tile.id} sx={{ display: 'flex', flexDirection: 'column', minHeight: 0 }}>
              {editing ? (
                <EditChrome tile={tile} name={name(tile)} index={i} count={ordered.length} cols={cols} onAction={(action) => onAction?.(tile, action)} />
              ) : null}
              <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>{render(tile)}</Box>
            </Box>
          ))}
        </Responsive>
      )}
      {!mounted && (
        <Typography variant="body2" role="status" sx={{ color: 'text.secondary' }}>
          {t('app.loading')}
        </Typography>
      )}
    </Box>
  )
}

/** A tile's handle and menu while the dashboard is edited: the handle for a pointer, the menu for everyone. */
function EditChrome({ tile, name, index, count, cols, onAction }: { tile: Tile; name: string; index: number; count: number; cols: number; onAction: (action: TileAction) => void }) {
  const { t } = useTranslation()
  const [anchor, setAnchor] = useState<HTMLElement | null>(null)
  const menuId = useId()
  const min = MIN_SIZE[widgetOf(tile.id)?.kind ?? 'saved']
  const choose = (action: TileAction) => {
    setAnchor(null)
    onAction(action)
  }
  const items: { action: TileAction; label: string; disabled?: boolean }[] = [
    { action: 'earlier', label: t('dashboard.edit.earlier'), disabled: index === 0 },
    { action: 'later', label: t('dashboard.edit.later'), disabled: index === count - 1 },
    { action: 'wider', label: t('dashboard.edit.wider'), disabled: tile.w >= cols },
    { action: 'narrower', label: t('dashboard.edit.narrower'), disabled: tile.w <= min.w },
    { action: 'taller', label: t('dashboard.edit.taller'), disabled: tile.h >= MAX_ROWS },
    { action: 'shorter', label: t('dashboard.edit.shorter'), disabled: tile.h <= min.h },
    { action: tile.pinned ? 'unpin' : 'pin', label: tile.pinned ? t('dashboard.edit.unpin') : t('dashboard.edit.pin') },
    { action: 'remove', label: t('dashboard.edit.remove') },
  ]
  return (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5, mb: 0.5 }}>
      {/* The handle is for a pointer: a keyboard or a screen reader uses the menu, so it says nothing itself. */}
      <Box className={CLASS.handle} aria-hidden sx={{ flex: 1, minHeight: 44, display: 'flex', alignItems: 'center', justifyContent: 'center', color: 'text.secondary', borderRadius: '12px', bgcolor: 'action.hover' }}>
        <GripHorizontal size={18} />
      </Box>
      {tile.pinned && (
        <Box sx={{ display: 'inline-flex', alignItems: 'center', color: 'text.secondary', px: 0.5 }} title={t('dashboard.edit.pinned')}>
          <Pin size={16} aria-hidden />
          <span style={{ position: 'absolute', width: 1, height: 1, overflow: 'hidden', clip: 'rect(0 0 0 0)' }}>{t('dashboard.edit.pinned')}</span>
        </Box>
      )}
      <IconAction size="large" label={t('dashboard.edit.options', { name })} aria-haspopup="menu" aria-controls={anchor ? menuId : undefined} aria-expanded={anchor ? true : undefined} onClick={(e) => setAnchor(e.currentTarget)}>
        <EllipsisVertical size={18} aria-hidden />
      </IconAction>
      <Menu id={menuId} anchorEl={anchor} open={anchor !== null} onClose={() => setAnchor(null)}>
        {items.map((it) => (
          <MenuItem key={it.action} disabled={it.disabled} onClick={() => choose(it.action)}>
            {it.label}
          </MenuItem>
        ))}
      </Menu>
    </Box>
  )
}
