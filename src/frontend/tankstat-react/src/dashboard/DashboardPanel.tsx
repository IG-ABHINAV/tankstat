import { useMutation, useQuery } from '@apollo/client/react'
import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Link from '@mui/material/Link'
import Skeleton from '@mui/material/Skeleton'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { Pencil, Plus, Trash2 } from 'lucide-react'
import { useState } from 'react'
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
import { AddWidgetDialog } from './AddWidgetDialog.tsx'
import { ChartBuilderDialog } from './ChartBuilderDialog.tsx'
import { ChartCard } from './ChartCard.tsx'
import { recipeOf } from './chartFormat.ts'
import { DashboardGrid, type TileAction } from './DashboardGrid.tsx'
import { Figure } from './Figures.tsx'
import {
  addTile,
  chartKey,
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
  /** The Add widget dialog is open. */
  adding: boolean
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

/**
 * The first screen of a vehicle: its key figures and its charts, ready-made and saved, as tiles of a grid arranged the way its dashboard is
 * (#211): the user's own arrangement, else the vehicle's, else the Standard preset (`widgets.ts`). Outside Customize a tile shows only its
 * content (and its table behind a button). Customize turns the grid into an edit mode in place (#228): the vehicle's dashboard (everyone's,
 * with Edit access to its logs) or one's own; tiles dragged, resized, pinned and taken off, one's own saved charts edited or deleted, and
 * anything added through the Add widget dialog (a figure, a ready-made chart as it is or customized into a chart of one's own, a saved
 * chart, or a blank chart); saved when done.
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
  // Everyone's dashboard holds only shared charts, except with sign-in off, where everyone is the same user and every chart is theirs
  // (so a chart of the anonymous user can still be edited and shared from there).
  const chartsFor = (scope: Scope) => (scope === 'shared' && !signInOff ? charts.filter((c) => c.isShared) : charts)
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
    return { scope, tiles: resolved.tiles, hidden: resolved.hidden, adding: false, status: '' }
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
  /** A widget added at the bottom (by its tile id); `name` for a chart just saved, which the charts list may not hold yet. */
  const add = (id: string, name?: string) =>
    update((e) => ({ tiles: addTile(e.tiles, id), hidden: e.hidden.filter((c) => chartKey(c.id) !== id), status: t('dashboard.edit.added', { name: name || titleOf({ id } as Tile) }) }))
  const applyPreset = (id: LayoutPresetId) =>
    update((e) => {
      const resolved = resolveLayout(LAYOUT_PRESETS[id], chartsFor(e.scope))
      return { tiles: resolved.tiles, hidden: resolved.hidden, status: t('dashboard.customize.started', { name: t(`dashboard.layoutPresets.${id}`) }) }
    })
  const onDashboard = new Set(edit?.tiles.map((x) => x.id))
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
          // Changing or deleting a chart is part of arranging the dashboard (#228), offered while it is customized, each button by its own
          // right (#210): the creator changes a chart, Delete access to the logs removes a shared one too.
          (c.canEdit || c.canDelete) && edit ? (
            <Stack direction="row" sx={{ gap: 1 }}>
              {c.canEdit && (
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
              )}
              {c.canDelete && (
                <ConfirmDialog
                  trigger={
                    <IconAction size="large" tone="error" label={t('dashboard.deleteChart', { title: c.title })}>
                      <Trash2 size={16} aria-hidden />
                    </IconAction>
                  }
                  title={t('dashboard.deleteTitle')}
                  description={t('dashboard.deleteDescription', { title: c.title })}
                  confirmLabel={t('dashboard.deleteConfirm')}
                  onConfirm={() => void act(() => remove({ variables: { id: c.id } }), () => update((e) => ({ tiles: removeTile(e.tiles, chartKey(c.id)), hidden: e.hidden.filter((h) => h.id !== c.id) })))}
                />
              )}
            </Stack>
          ) : undefined
        }
      />
    )
  }
  const render = (tile: Tile) => {
    const widget = widgetOf(tile.id)
    if (widget?.kind === 'figure') return <Figure id={widget.id} vehicleId={vehicle.id} period={tile.period} rows={tile.h} summary={s} units={vehicle.units} />
    // The tiles' headings come right under the vehicle's name: h2, with no section heading between.
    if (widget?.kind === 'ready') return <ChartCard vehicleId={vehicle.id} title={t(`dashboard.presets.${widget.chart.id}`)} recipe={widget.chart.recipe} units={vehicle.units} fill headingLevel={2} />
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
              <Button size="large" variant="soft" aria-haspopup="dialog" onClick={() => update(() => ({ adding: true }))}>
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
            {/* Null for a vehicle added on this device: the Standard preset until the server has it. */}
            {dashboard && scopes.length > 0 && (
              <Button size="large" variant="soft" onClick={startEditing}>
                {t('dashboard.customize.button')}
              </Button>
            )}
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
          <Typography variant="body2" role="status" aria-label={t('dashboard.edit.statusName')} sx={{ color: 'text.secondary', minHeight: '1.5em' }}>
            {edit.status}
          </Typography>
          <AddWidgetDialog
            open={edit.adding}
            onClose={() => update(() => ({ adding: false }))}
            vehicle={vehicle}
            summary={s}
            canShare={canShare}
            everyone={edit.scope === 'shared'}
            figures={FIGURES.filter((f) => !onDashboard.has(f))}
            ready={READY_CHARTS.filter((c) => !onDashboard.has(presetKey(c.id)))}
            saved={chartsFor(edit.scope).filter((c) => !onDashboard.has(chartKey(c.id)))}
            onAdd={(id) => add(id)}
            onChartSaved={async (id, title) => {
              await refetch()
              add(chartKey(id), title)
            }}
          />
        </>
      )}
      {actionError !== undefined && <ErrorMessage error={actionError} />}
      {tiles.length > 0 ? (
        <DashboardGrid tiles={tiles} editing={edit !== null} render={render} name={titleOf} onTilesChange={(next) => update(() => ({ tiles: next }))} onAction={onAction} />
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
