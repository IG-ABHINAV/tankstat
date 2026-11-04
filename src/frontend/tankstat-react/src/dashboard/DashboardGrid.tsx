import Box from '@mui/material/Box'
import Divider from '@mui/material/Divider'
import Menu from '@mui/material/Menu'
import MenuItem from '@mui/material/MenuItem'
import Typography from '@mui/material/Typography'
import { EllipsisVertical, GripHorizontal, Pin } from 'lucide-react'
import { cloneElement, useCallback, useId, useMemo, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Responsive, useContainerWidth, verticalCompactor, type Layout as GridItems, type LayoutItem } from 'react-grid-layout'
import { IconAction } from '../components/IconAction.tsx'
import { TILE_STRIP, TileStripContext, type TileStrip } from './tileChrome.ts'
import { COLUMNS, flow, GAP, inReadingOrder, MAX_ROWS, minSize, ROW_HEIGHT, widgetOf, type Tile } from './widgets.ts'

/** The grid's columns by the container's width: four on a desktop, two on a tablet, one on a phone. */
const BREAKPOINTS = { lg: 900, sm: 520, xs: 0 } as const
const COLS = { lg: COLUMNS, sm: 2, xs: 1 } as const
const FALLBACK_WIDTH = 1280
/** The gaps between the tiles and around the grid: constants, since the library's tiles re-render when theirs change identity. */
const MARGIN: [number, number] = [GAP, GAP]
const PADDING: [number, number] = [0, 0]
type Breakpoint = keyof typeof COLS
/** The breakpoint of a width, as the library picks it: the widest whose least width is reached. */
const breakpointOf = (width: number): Breakpoint => (width >= BREAKPOINTS.lg ? 'lg' : width >= BREAKPOINTS.sm ? 'sm' : 'xs')

/**
 * What the tile menu offers: the keyboard's and a screen reader's way to do what dragging does; `customize` (a ready-made chart's only,
 * #247) opens a dialog, so it comes once the menu has gone.
 */
export type TileAction = 'earlier' | 'later' | 'wider' | 'narrower' | 'taller' | 'shorter' | 'pin' | 'unpin' | 'remove' | 'customize'

const CLASS = { handle: 'tk-tile-handle' }

/**
 * The dashboard as a grid of tiles (react-grid-layout, #211): the tiles placed and sized as stored, packed by column, the columns fewer on
 * a tablet or a phone (the tiles flowed in reading order). In edit mode a strip lies across the top of every tile (#228): its grip is the
 * drag handle, its menu moves, sizes, pins or removes the tile, which is how it is done with a keyboard or a screen reader (the library has
 * neither); the corner resizes it. The strip is the tile's header (#262): the rows are as tall as in view mode, so nothing changes size
 * when the dashboard is arranged; the strip names the tile and carries what it covers (a trend figure's period chip, a chart's buttons:
 * `TileStripContext`), and the card under it starts right below it. The library's stylesheet is written here in `sx`, since the app has no
 * stylesheet of its own, and its transitions are off under reduced motion.
 */
export function DashboardGrid({
  tiles,
  editing,
  render,
  name,
  onTilesChange,
  onAction,
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
}) {
  const { t } = useTranslation()
  // Measured before the grid is drawn, so a phone never paints a frame of the desktop's grid first; while the container has no width
  // (jsdom, a tab out of sight) the desktop's columns, never a one-column grid by mistake.
  const { width: measured, containerRef, mounted } = useContainerWidth({ measureBeforeMount: true })
  const width = measured > 0 ? measured : FALLBACK_WIDTH
  const cols = COLS[breakpointOf(width)]
  const ordered = useMemo(() => inReadingOrder(tiles), [tiles])
  // The tile being dragged or resized, and a count of its moves (#227). react-grid-layout 2.3 memoizes its grid items, comparing their
  // place in the grid and their child element, and draws a dragged tile at the pointer only when it renders: with the same child, a tile
  // moved within its cell is not drawn again, so it froze under the pointer until it crossed into another cell. Each move gives that one
  // tile a new element (the same props, so its content does not render again), and the library draws it where the pointer is. The
  // handlers keep their identity across the moves (`useCallback`), since the library compares them too: new ones would draw every tile.
  const [moving, setMoving] = useState<{ id: string; moves: number } | null>(null)
  const follow = useCallback((_layout: GridItems, _old: LayoutItem | null, item: LayoutItem | null) => {
    if (item) setMoving((m) => ({ id: item.i, moves: (m?.moves ?? 0) + 1 }))
  }, [])

  const item = (tile: Tile, inFour: boolean): LayoutItem => {
    const min = minSize(tile.id)
    return { i: tile.id, x: tile.x, y: tile.y, w: tile.w, h: tile.h, minW: min.w, minH: min.h, maxH: MAX_ROWS, static: inFour && tile.pinned }
  }
  const layouts = useMemo(
    () => ({ lg: ordered.map((tile) => item(tile, true)), sm: flow(ordered, 2).map((tile) => item(tile, false)), xs: flow(ordered, 1).map((tile) => item(tile, false)) }),
    [ordered],
  )

  /** The tiles after the grid changed them: in the four-column grid as they are; on fewer columns their new order, flowed back into four. */
  const fromGrid = useCallback(
    (layout: GridItems): Tile[] => {
      const byId = new Map(tiles.map((tile) => [tile.id, tile]))
      // A tile the grid knows keeps its pin (and, on fewer columns, its desktop width).
      const known = (l: LayoutItem): Tile => byId.get(l.i) ?? { id: l.i, x: l.x, y: l.y, w: l.w, h: l.h, pinned: false }
      if (cols === COLUMNS) return inReadingOrder(layout.map((l) => ({ ...known(l), x: l.x, y: l.y, w: l.w, h: l.h })))
      const order = [...layout].sort((a, b) => a.y - b.y || a.x - b.x).map((l, i) => ({ ...known(l), h: l.h, y: i, x: 0 }))
      return flow(order, COLUMNS)
    },
    [tiles, cols],
  )
  /** A drag or a resize ended: the tile is drawn from its place in the grid again, and the tiles as they are now go to the page. */
  const settle = useCallback(
    (layout: GridItems) => {
      setMoving(null)
      onTilesChange?.(fromGrid(layout))
    },
    [fromGrid, onTilesChange],
  )

  // The tiles' elements, in `ordered`'s order, kept across the grid's own renders (a drag's moves): while a tile is dragged only its own
  // element is new (`moving`). They are made again whenever the page renders, since its `render`, `name` and `onAction` are new then.
  const cells = useMemo(
    () =>
      ordered.map((tile, i) => (
        <Box key={tile.id} data-tile={tile.id} sx={{ display: 'flex', flexDirection: 'column', minHeight: 0 }}>
          <TileCell
            editing={editing}
            strip={(slots) => <EditStrip tile={tile} name={name(tile)} index={i} count={ordered.length} cols={cols} slots={slots} onAction={(action) => onAction?.(tile, action)} />}
          >
            {render(tile)}
          </TileCell>
        </Box>
      )),
    [ordered, editing, cols, name, render, onAction],
  )

  const styles = {
    '& .react-grid-layout': { position: 'relative', transition: 'height 200ms ease' },
    '& .react-grid-item': { transition: 'all 200ms ease', transitionProperty: 'left, top, width, height', boxSizing: 'border-box' },
    '& .react-grid-item.cssTransforms': { transitionProperty: 'transform, width, height' },
    '& .react-grid-item.resizing': { transition: 'none', zIndex: 1, willChange: 'width, height' },
    '& .react-grid-item.react-draggable-dragging': { transition: 'none', zIndex: 3, willChange: 'transform', cursor: 'grabbing' },
    '& .react-grid-item.react-grid-placeholder': { backgroundColor: 'primary.main', opacity: 0.2, borderRadius: '12px', transitionDuration: '100ms', zIndex: 2, userSelect: 'none' },
    '& .react-grid-item.react-grid-placeholder.placeholder-resizing': { transition: 'none' },
    [`& .${CLASS.handle}`]: { cursor: 'grab', touchAction: 'none' },
    // The corner that resizes a tile: 44 px for a finger, its mark drawn in the corner.
    '& .react-grid-item > .react-resizable-handle': { position: 'absolute', right: 0, bottom: 0, width: 44, height: 44, cursor: 'se-resize', touchAction: 'none', zIndex: 1 },
    '& .react-grid-item > .react-resizable-handle::after': { content: '""', position: 'absolute', right: 8, bottom: 8, width: 10, height: 10, borderRight: '2px solid', borderBottom: '2px solid', borderColor: 'text.secondary', borderBottomRightRadius: 3 },
    // Outside edit mode the library still renders the corner, marking its tile `react-resizable-hide`; the rule that hides it is in its
    // stylesheet, which the app does not load (#227).
    '& .react-resizable-hide > .react-resizable-handle': { display: 'none' },
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
          margin={MARGIN}
          containerPadding={PADDING}
          compactor={verticalCompactor}
          dragConfig={{ enabled: editing, handle: `.${CLASS.handle}` }}
          resizeConfig={{ enabled: editing, handles: ['se'] }}
          onDrag={follow}
          onResize={follow}
          onDragStop={settle}
          onResizeStop={settle}
        >
          {cells.map((cell, i) => (moving?.id === ordered[i].id ? cloneElement(cell) : cell))}
        </Responsive>
      )}
      {!mounted && ( // the one frame before the container is measured
        <Typography variant="body2" role="status" sx={{ color: 'text.secondary' }}>
          {t('app.loading')}
        </Typography>
      )}
    </Box>
  )
}

/** Where the strip's two slots are, as it mounts them (callback refs). */
interface StripSlots {
  lead: (element: HTMLElement | null) => void
  actions: (element: HTMLElement | null) => void
}

/**
 * A tile's strip and content: while editing, the strip's slots are handed to the content (`TileStripContext`), which portals into them.
 * The slots are state, not refs: the strip is the content's sibling, so the content renders once more when they are there. The context's
 * value keeps its identity while they stay, since a tile being dragged renders on every move (#227) and its content must not.
 */
function TileCell({ editing, strip, children }: { editing: boolean; strip: (slots: StripSlots) => ReactNode; children: ReactNode }) {
  const [lead, setLead] = useState<HTMLElement | null>(null)
  const [actions, setActions] = useState<HTMLElement | null>(null)
  const value = useMemo<TileStrip | null>(() => (editing ? { lead, actions } : null), [editing, lead, actions])
  const slots = useMemo<StripSlots>(() => ({ lead: setLead, actions: setActions }), [])
  return (
    <>
      {editing && strip(slots)}
      <TileStripContext.Provider value={value}>
        <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>{children}</Box>
      </TileStripContext.Provider>
    </>
  )
}

/** The handle's look: a pointer drags the tile by it (the tile's name and the grip), never by a button. */
const HANDLE_SX = { alignSelf: 'stretch', display: 'flex', alignItems: 'center', minWidth: 0 } as const

/**
 * The strip across the top of a tile while the dashboard is edited: over the card's top edge (the tile is positioned, the card starts
 * under it), the tile's header (#262). Left to right: the tile's name (a chart's heading, the card's own title not drawn meanwhile), what
 * the content puts in its lead slot (a trend figure's period chip), the grip, the pin when the tile is pinned, the content's actions (a
 * chart's table, edit and delete buttons) and the menu for everyone. The name and the grip are the handle for a pointer.
 */
function EditStrip({ tile, name, index, count, cols, slots: { lead: leadSlot, actions: actionsSlot }, onAction }: { tile: Tile; name: string; index: number; count: number; cols: number; slots: StripSlots; onAction: (action: TileAction) => void }) {
  const { t } = useTranslation()
  const [anchor, setAnchor] = useState<HTMLElement | null>(null)
  /** An action that opens a dialog, done once the menu is gone (`LogFab`): the menu's leaving would take the focus back from it. */
  const [opening, setOpening] = useState<TileAction | null>(null)
  const menuId = useId()
  const min = minSize(tile.id)
  const kind = widgetOf(tile.id)?.kind
  const ready = kind === 'ready'
  const chart = kind === 'ready' || kind === 'saved'
  const choose = (action: TileAction) => {
    setAnchor(null)
    if (action === 'customize') setOpening(action)
    else onAction(action)
  }
  const items: { action: TileAction; label: string; disabled?: boolean }[] = [
    { action: 'earlier', label: t('dashboard.edit.earlier'), disabled: index === 0 },
    { action: 'later', label: t('dashboard.edit.later'), disabled: index === count - 1 },
    // A width is a share of the desktop's four columns: on fewer, where it cannot be seen, neither is offered (a corner resize there keeps it too).
    { action: 'wider', label: t('dashboard.edit.wider'), disabled: cols < COLUMNS || tile.w >= COLUMNS },
    { action: 'narrower', label: t('dashboard.edit.narrower'), disabled: cols < COLUMNS || tile.w <= min.w },
    { action: 'taller', label: t('dashboard.edit.taller'), disabled: tile.h >= MAX_ROWS },
    { action: 'shorter', label: t('dashboard.edit.shorter'), disabled: tile.h <= min.h },
    { action: tile.pinned ? 'unpin' : 'pin', label: tile.pinned ? t('dashboard.edit.unpin') : t('dashboard.edit.pin') },
    { action: 'remove', label: t('dashboard.edit.remove') },
  ]
  return (
    <Box sx={(theme) => ({ position: 'absolute', top: 0, left: 0, right: 0, height: TILE_STRIP, zIndex: 2, display: 'flex', alignItems: 'center', gap: 0.5, pr: 0.5, borderRadius: '12px 12px 0 0', bgcolor: theme.vars.palette.primary.soft, color: theme.vars.palette.primary.softText })}>
      {/* The name: cut with an ellipsis (with the chip beside it sharing the shrinking; the buttons never give way). A chart's is its heading while arranging. */}
      <Box className={CLASS.handle} sx={{ ...HANDLE_SX, flex: '0 1 auto', pl: 1.5 }}>
        <Typography component={chart ? 'h2' : 'span'} variant="subtitle2" noWrap sx={{ color: 'text.primary', minWidth: 0 }}>
          {name}
        </Typography>
      </Box>
      {/* What it holds takes the slot's width (a chip's own 60% would be of the slot): the period stays readable beside the name. `&&` so
          the rule outranks the chip's own class whichever was inserted first (a trend figure added while arranging comes after the strip). */}
      <Box ref={leadSlot} sx={{ display: 'flex', alignItems: 'center', flex: '0 1 auto', minWidth: 0, maxWidth: '55%', '&:empty': { display: 'none' }, '&& > *': { maxWidth: '100%' }, ml: 1 }} />
      {/* The grip is for a pointer: a keyboard or a screen reader uses the menu, so it says nothing itself. */}
      <Box className={CLASS.handle} sx={{ ...HANDLE_SX, flex: '1 1 auto', minWidth: 24, justifyContent: 'center' }}>
        <GripHorizontal size={18} aria-hidden />
      </Box>
      {tile.pinned && (
        <Box sx={{ display: 'inline-flex', alignItems: 'center', px: 0.5 }} title={t('dashboard.edit.pinned')}>
          <Pin size={16} aria-hidden />
          <span style={{ position: 'absolute', width: 1, height: 1, overflow: 'hidden', clip: 'rect(0 0 0 0)' }}>{t('dashboard.edit.pinned')}</span>
        </Box>
      )}
      <Box ref={actionsSlot} sx={{ display: 'flex', alignItems: 'center', flex: 'none', gap: 0.5, '&:empty': { display: 'none' } }} />
      {/* 44 px for a finger, centred on the strip (it reaches a little past the card's top edge). */}
      <IconAction
        size="large"
        variant="ghost"
        tone="primary"
        label={t('dashboard.edit.options', { name })}
        aria-haspopup="menu"
        aria-controls={anchor ? menuId : undefined}
        aria-expanded={anchor ? true : undefined}
        onClick={(e) => {
          // Opened again before the last closing was over: what was chosen then is dropped, or the next closing would do it.
          setOpening(null)
          setAnchor(e.currentTarget)
        }}
      >
        <EllipsisVertical size={18} aria-hidden />
      </IconAction>
      <Menu
        id={menuId}
        anchorEl={anchor}
        open={anchor !== null}
        onClose={() => setAnchor(null)}
        slotProps={{
          transition: {
            onExited: () => {
              if (opening) onAction(opening)
              setOpening(null)
            },
          },
        }}
      >
        {/* A ready-made chart is made one's own from here (#247), the arranging below. */}
        {ready && (
          <MenuItem onClick={() => choose('customize')}>{t('dashboard.edit.customize')}</MenuItem>
        )}
        {ready && <Divider />}
        {items.map((it) => (
          <MenuItem key={it.action} disabled={it.disabled} onClick={() => choose(it.action)}>
            {it.label}
          </MenuItem>
        ))}
      </Menu>
    </Box>
  )
}
