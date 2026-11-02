import { useMutation, useQuery } from '@apollo/client/react'
import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Link from '@mui/material/Link'
import Skeleton from '@mui/material/Skeleton'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { Copy, Pencil, Plus, Trash2 } from 'lucide-react'
import { useState, type DragEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { ConfirmDialog } from '../components/ConfirmDialog.tsx'
import { IconAction } from '../components/IconAction.tsx'
import { Segmented } from '../components/Segmented.tsx'
import { LabeledSelect } from '../components/UnitSelect.tsx'
import { UserChip } from '../components/UserAvatar.tsx'
import { visuallyHidden } from '../components/visuallyHidden.ts'
import {
  DeleteChartDocument,
  ResetDashboardLayoutDocument,
  SaveDashboardLayoutDocument,
  SessionDocument,
  VehicleDashboardDocument,
  type AccessLevel,
  type DistanceUnit,
  type VehicleDashboardQuery,
  type VolumeUnit,
} from '../gql/generated.ts'
import { ErrorMessage } from '../messages.tsx'
import { useConnectivity } from '../offline/useConnectivity.ts'
import { useToast } from '../toast/toastContext.ts'
import { ChartBuilderDialog } from './ChartBuilderDialog.tsx'
import { ChartCard } from './ChartCard.tsx'
import { type ChartRecipe } from './chartFormat.ts'
import { DashboardGrid, type TileAction } from './DashboardGrid.tsx'
import { Figure } from './Figures.tsx'
import {
  addTile,
  chartKey,
  DEFAULT_SIZE,
  FIGURE_GRID,
  FIGURES,
  inReadingOrder,
  LAYOUT_PRESETS,
  moveTile,
  pinTile,
  presetKey,
  READY_CHARTS,
  removeTile,
  resizeTile,
  resolveLayout,
  toLayout,
  widgetOf,
  type Layout,
  type LayoutPresetId,
  type Tile,
} from './widgets.ts'

type Units = { distance: DistanceUnit; volume: VolumeUnit }
type SavedChart = VehicleDashboardQuery['vehicleCharts'][number]
type Scope = 'shared' | 'personal'

/** The dashboard while it is being arranged: whose, the tiles and the charts taken off as they are now, and what is going on. */
interface Editing {
  scope: Scope
  tiles: Tile[]
  hidden: SavedChart[]
  adding: boolean
  /** A widget being dragged in from the Add widget panel. */
  dropping: { id: string; w: number; h: number } | null
  status: string
}

/** While the figures load: their outlines, and "Loading…" for a screen reader. */
function DashboardSkeleton() {
  const { t } = useTranslation()
  return (
    <>
      <span role="status" style={visuallyHidden}>
        {t('app.loading')}
      </span>
      <Box aria-hidden className="tk-delayed" sx={FIGURE_GRID}>
        {[0, 1, 2, 3].map((i) => (
          <Skeleton key={i} variant="rounded" height={112} sx={{ borderRadius: '12px' }} />
        ))}
      </Box>
    </>
  )
}

const recipeOf = (c: SavedChart): ChartRecipe => ({
  metric: c.metric,
  grouping: c.grouping,
  kind: c.kind,
  range: c.range,
  stacked: c.stacked,
  from: c.rangeFrom ?? null,
  to: c.rangeTo ?? null,
  trend: c.trend,
  runningTotal: c.runningTotal,
  compareYearEarlier: c.compareYearEarlier,
  secondMetric: c.secondMetric ?? null,
  secondKind: c.secondKind ?? null,
})

/**
 * The first screen of a vehicle: its key figures and its charts, ready-made and saved, as tiles of a grid arranged the way its dashboard is
 * (#211): the user's own arrangement, else the vehicle's, else the Standard preset (`widgets.ts`). Customize turns the grid into an edit
 * mode in place: the vehicle's dashboard (everyone's, with Edit access to its logs) or one's own; tiles dragged, resized, pinned, taken off
 * and added back (clicked or dragged in from the Add widget panel); saved when done.
 */
export function DashboardPanel({ vehicle }: { vehicle: { id: string; units: Units; logAccess: AccessLevel } }) {
  const { t } = useTranslation()
  const { toast } = useToast()
  const { reachable } = useConnectivity()
  const { data, error, refetch } = useQuery(VehicleDashboardDocument, { variables: { id: vehicle.id }, fetchPolicy: 'cache-and-network' })
  // With sign-in off everyone is the same anonymous user: an arrangement of one's own would be everyone's.
  const signInOff = useQuery(SessionDocument, { fetchPolicy: 'cache-only' }).data?.session.mode === 'NONE'
  const [remove] = useMutation(DeleteChartDocument)
  const [reset, resetting] = useMutation(ResetDashboardLayoutDocument)
  const [save, saving] = useMutation(SaveDashboardLayoutDocument)
  const [actionError, setActionError] = useState<unknown>()
  const [edit, setEdit] = useState<Editing | null>(null)
  const canShare = vehicle.logAccess === 'EDIT' || vehicle.logAccess === 'DELETE'

  if (error) return <ErrorMessage error={error} />
  if (!data) return <DashboardSkeleton />

  const s = data.vehicle?.summary
  const dashboard = data.vehicle?.dashboard
  const shared: Layout | null = dashboard?.shared ?? null
  const personal: Layout | null = dashboard?.personal ?? null
  const charts = data.vehicleCharts
  const chartsFor = (scope: Scope) => (scope === 'shared' ? charts.filter((c) => c.isShared) : charts)
  const shown = resolveLayout(personal ?? shared, charts)
  const tiles = edit ? edit.tiles : shown.tiles
  const busy = saving.loading || resetting.loading

  const titleOf = (tile: Tile) => {
    const widget = widgetOf(tile.id)
    if (widget?.kind === 'figure') return t(`dashboard.kpi.${widget.id}`)
    if (widget?.kind === 'ready') return t(`dashboard.presets.${widget.chart.id}`)
    return charts.find((c) => c.id === widget?.id)?.title ?? tile.id
  }

  async function act(work: () => Promise<unknown>, done?: () => void) {
    setActionError(undefined)
    try {
      await work()
      await refetch()
      done?.()
    } catch (e) {
      setActionError(e)
    }
  }

  // ---- editing -------------------------------------------------------------------------------------------

  const startEditing = () => {
    const scope: Scope = personal && !signInOff ? 'personal' : dashboard?.canChangeShared ? 'shared' : 'personal'
    setEdit(editingOf(scope))
  }
  const editingOf = (scope: Scope): Editing => {
    const start = scope === 'shared' ? shared : (personal ?? shared)
    const resolved = resolveLayout(start, chartsFor(scope))
    return { scope, tiles: resolved.tiles, hidden: resolved.hidden, adding: false, dropping: null, status: '' }
  }
  const update = (change: (e: Editing) => Partial<Editing>) => setEdit((e) => (e ? { ...e, ...change(e) } : e))

  const onAction = (tile: Tile, action: TileAction) => {
    const name = titleOf(tile)
    update((e) => {
      switch (action) {
        case 'earlier':
        case 'later': {
          const next = moveTile(e.tiles, tile.id, action === 'earlier' ? -1 : 1)
          const order = inReadingOrder(next)
          return { tiles: next, status: t('dashboard.edit.moved', { name, position: order.findIndex((x) => x.id === tile.id) + 1, total: order.length }) }
        }
        case 'wider':
        case 'narrower':
        case 'taller':
        case 'shorter': {
          const change = action === 'wider' ? { w: 1 as const } : action === 'narrower' ? { w: -1 as const } : action === 'taller' ? { h: 1 as const } : { h: -1 as const }
          const next = resizeTile(e.tiles, tile.id, change)
          const sized = next.find((x) => x.id === tile.id)!
          return { tiles: next, status: t('dashboard.edit.resized', { name, w: sized.w, h: sized.h }) }
        }
        case 'pin':
        case 'unpin':
          return { tiles: pinTile(e.tiles, tile.id, action === 'pin'), status: t(action === 'pin' ? 'dashboard.edit.pinnedSaid' : 'dashboard.edit.unpinnedSaid', { name }) }
        case 'remove': {
          const saved = charts.find((c) => chartKey(c.id) === tile.id)
          return { tiles: removeTile(e.tiles, tile.id), hidden: saved ? [...e.hidden, saved] : e.hidden, status: t('dashboard.edit.removed', { name }) }
        }
      }
    })
  }
  const add = (id: string) =>
    update((e) => ({ tiles: addTile(e.tiles, id), hidden: e.hidden.filter((c) => chartKey(c.id) !== id), status: t('dashboard.edit.added', { name: titleOf({ id } as Tile) }) }))
  const dragStart = (id: string) => (event: DragEvent) => {
    const { w, h } = DEFAULT_SIZE[widgetOf(id)?.kind ?? 'saved']
    event.dataTransfer.setData('text/plain', id) // Firefox starts no drag without data
    event.dataTransfer.effectAllowed = 'move'
    update(() => ({ dropping: { id, w, h } }))
  }
  const applyPreset = (id: LayoutPresetId) =>
    update((e) => {
      const resolved = resolveLayout(LAYOUT_PRESETS[id], chartsFor(e.scope))
      return { tiles: resolved.tiles, hidden: resolved.hidden, status: t('dashboard.customize.started', { name: t(`dashboard.layoutPresets.${id}`) }) }
    })
  const available = edit
    ? (() => {
        const ids = new Set(edit.tiles.map((x) => x.id))
        return [
          ...FIGURES.filter((f) => !ids.has(f)).map((f) => ({ id: f, label: t(`dashboard.kpi.${f}`), note: t('dashboard.edit.figure') })),
          ...READY_CHARTS.filter((c) => !ids.has(presetKey(c.id))).map((c) => ({ id: presetKey(c.id), label: t(`dashboard.presets.${c.id}`), note: t('dashboard.edit.ready') })),
          ...chartsFor(edit.scope).filter((c) => !ids.has(chartKey(c.id))).map((c) => ({ id: chartKey(c.id), label: c.title, note: c.isShared ? t('dashboard.edit.sharedChart') : t('dashboard.edit.savedChart') })),
        ]
      })()
    : []
  const scopes: Scope[] = [...(dashboard?.canChangeShared ? (['shared'] as const) : []), ...(!signInOff ? (['personal'] as const) : [])]
  const stored = edit ? (edit.scope === 'shared' ? shared : personal) : null

  // ---- the tiles -----------------------------------------------------------------------------------------

  const savedCard = (c: SavedChart) => {
    const r = recipeOf(c)
    return (
      <ChartCard
        vehicleId={vehicle.id}
        title={c.title}
        recipe={r}
        units={vehicle.units}
        shared={c.isShared}
        fill
        headingLevel={2}
        meta={c.createdBy && c.isShared ? <UserChip user={c.createdBy} /> : undefined}
        actions={
          c.canEdit && !edit ? (
            <Stack direction="row" sx={{ gap: 1 }}>
              <ChartBuilderDialog
                vehicle={vehicle}
                canShare={canShare}
                chart={{ id: c.id, title: c.title, isShared: c.isShared, recipe: r }}
                onSaved={() => refetch()}
                trigger={
                  <IconAction size="large" tone="primary" label={t('dashboard.editChart', { title: c.title })}>
                    <Pencil size={16} aria-hidden />
                  </IconAction>
                }
              />
              <ConfirmDialog
                trigger={
                  <IconAction size="large" tone="error" label={t('dashboard.deleteChart', { title: c.title })}>
                    <Trash2 size={16} aria-hidden />
                  </IconAction>
                }
                title={t('dashboard.deleteTitle')}
                description={t('dashboard.deleteDescription', { title: c.title })}
                confirmLabel={t('dashboard.deleteConfirm')}
                onConfirm={() => void act(() => remove({ variables: { id: c.id } }))}
              />
            </Stack>
          ) : undefined
        }
      />
    )
  }
  const render = (tile: Tile) => {
    const widget = widgetOf(tile.id)
    if (widget?.kind === 'figure') return <Figure id={widget.id} summary={s} units={vehicle.units} />
    // The tiles' headings come right under the vehicle's name: h2, with no section heading between.
    if (widget?.kind === 'ready') {
      const title = t(`dashboard.presets.${widget.chart.id}`)
      return (
        <ChartCard
          vehicleId={vehicle.id}
          title={title}
          recipe={widget.chart.recipe}
          units={vehicle.units}
          fill
          headingLevel={2}
          actions={
            // A ready-made chart copied as one's own: the builder starts from its recipe, the original stays.
            !edit ? (
              <ChartBuilderDialog
                vehicle={vehicle}
                canShare={canShare}
                start={{ title, recipe: widget.chart.recipe }}
                onSaved={() => refetch()}
                trigger={
                  <IconAction size="large" label={t('dashboard.copyChart', { title })}>
                    <Copy size={16} aria-hidden />
                  </IconAction>
                }
              />
            ) : undefined
          }
        />
      )
    }
    const saved = charts.find((c) => c.id === widget?.id)
    return saved ? savedCard(saved) : null
  }

  return (
    <Stack component="section" aria-label={t('dashboard.sectionLabel')} sx={{ gap: 2 }}>
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', gap: 1.5, flexWrap: 'wrap' }}>
        {edit ? (
          <>
            {scopes.length > 1 ? (
              <Segmented
                label={t('dashboard.customize.whose')}
                value={edit.scope}
                onChange={(scope) => setEdit(editingOf(scope))}
                options={[
                  { value: 'shared', label: t('dashboard.customize.everyone') },
                  { value: 'personal', label: t('dashboard.customize.mine') },
                ]}
              />
            ) : (
              <span />
            )}
            <Stack direction="row" sx={{ gap: 1, flexWrap: 'wrap', alignItems: 'center' }}>
              <LabeledSelect<LayoutPresetId | ''>
                label={t('dashboard.customize.preset')}
                placeholder={t('dashboard.customize.choose')}
                value=""
                disabled={busy}
                onChange={(id) => id && applyPreset(id)}
                options={(Object.keys(LAYOUT_PRESETS) as LayoutPresetId[]).map((id) => ({ value: id, label: t(`dashboard.layoutPresets.${id}`) }))}
              />
              <Button size="large" variant="soft" aria-expanded={edit.adding} onClick={() => update((e) => ({ adding: !e.adding }))}>
                <Plus size={16} aria-hidden />
                {t('dashboard.edit.add')}
              </Button>
              {stored && (
                <Button size="large" variant="ghost" disabled={busy || !reachable} onClick={() => void act(() => reset({ variables: { vehicleId: vehicle.id, personal: edit.scope === 'personal' } }), () => setEdit(null))}>
                  {edit.scope === 'shared' ? t('dashboard.customize.resetShared') : t('dashboard.customize.resetPersonal')}
                </Button>
              )}
              <Button size="large" variant="ghost" disabled={busy} onClick={() => setEdit(null)}>
                {t('dashboard.customize.cancel')}
              </Button>
              <Button
                size="large"
                loading={saving.loading}
                disabled={busy || !reachable}
                onClick={() =>
                  void act(
                    () => save({ variables: { input: { vehicleId: vehicle.id, personal: edit.scope === 'personal', ...toLayout(edit.tiles, edit.hidden) } } }),
                    () => {
                      setEdit(null)
                      toast(t('toast.saved'))
                    },
                  )
                }
              >
                {t('dashboard.customize.save')}
              </Button>
            </Stack>
          </>
        ) : (
          <>
            {personal && !signInOff ? (
              <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                {t('dashboard.customize.yours')}{' '}
                <Link component="button" type="button" variant="body2" underline="always" onClick={() => void act(() => reset({ variables: { vehicleId: vehicle.id, personal: true } }))}>
                  {t('dashboard.customize.resetPersonal')}
                </Link>
              </Typography>
            ) : (
              <span />
            )}
            <Stack direction="row" sx={{ gap: 1, flexWrap: 'wrap' }}>
              {/* Null for a vehicle added on this device: the Standard preset until the server has it. */}
              {dashboard && scopes.length > 0 && (
                <Button size="large" variant="soft" onClick={startEditing}>
                  {t('dashboard.customize.button')}
                </Button>
              )}
              <ChartBuilderDialog
                vehicle={vehicle}
                canShare={canShare}
                onSaved={() => refetch()}
                trigger={
                  <Button size="large">
                    <Plus size={16} aria-hidden />
                    {t('dashboard.addChart')}
                  </Button>
                }
              />
            </Stack>
          </>
        )}
      </Stack>
      {edit && (
        <>
          <Typography variant="body2" sx={{ color: 'text.secondary' }}>
            {t('dashboard.edit.hint')}
            {edit.scope === 'shared' && charts.some((c) => !c.isShared) ? ` ${t('dashboard.customize.privateHint')}` : ''}
            {!reachable ? ` ${t('dashboard.customize.needsServer')}` : ''}
          </Typography>
          {edit.adding && (
            <Box component="section" aria-label={t('dashboard.edit.add')} sx={{ p: 1.5, borderRadius: '12px', bgcolor: 'action.hover' }}>
              <Typography variant="body2" sx={{ color: 'text.secondary', mb: 1 }}>
                {available.length > 0 ? t('dashboard.edit.addHint') : t('dashboard.edit.nothingToAdd')}
              </Typography>
              <Stack direction="row" sx={{ gap: 1, flexWrap: 'wrap' }}>
                {available.map((w) => (
                  <Button key={w.id} size="large" variant="soft" draggable onDragStart={dragStart(w.id)} onDragEnd={() => update(() => ({ dropping: null }))} onClick={() => add(w.id)} sx={{ flexDirection: 'column', alignItems: 'flex-start', lineHeight: 1.2 }}>
                    <span>{w.label}</span>
                    <Typography component="span" variant="caption" sx={{ color: 'text.secondary' }}>
                      {w.note}
                    </Typography>
                  </Button>
                ))}
              </Stack>
            </Box>
          )}
          <Typography variant="body2" role="status" aria-label={t('dashboard.edit.statusName')} sx={{ color: 'text.secondary', minHeight: '1.5em' }}>
            {edit.status}
          </Typography>
        </>
      )}
      {actionError !== undefined && <ErrorMessage error={actionError} />}
      {tiles.length > 0 ? (
        <DashboardGrid
          tiles={tiles}
          editing={edit !== null}
          render={render}
          name={titleOf}
          onTilesChange={(next) => update(() => ({ tiles: next }))}
          onAction={onAction}
          dropping={edit?.dropping}
          onDrop={(next) =>
            update((e) => ({
              tiles: next,
              dropping: null,
              hidden: e.hidden.filter((c) => chartKey(c.id) !== e.dropping?.id),
              status: e.dropping ? t('dashboard.edit.dropped', { name: titleOf({ id: e.dropping.id } as Tile) }) : e.status,
            }))
          }
        />
      ) : (
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          {t('dashboard.customize.noCharts')}
        </Typography>
      )}
      {charts.length === 0 && (
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          {t('dashboard.noCharts')}
        </Typography>
      )}
    </Stack>
  )
}
