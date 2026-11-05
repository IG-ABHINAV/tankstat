import { useMutation, useQuery } from '@apollo/client/react'
import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Link from '@mui/material/Link'
import Skeleton from '@mui/material/Skeleton'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { Plus } from 'lucide-react'
import { useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Segmented } from '../components/Segmented.tsx'
import { LabeledSelect } from '../components/UnitSelect.tsx'
import { UserChip } from '../components/UserAvatar.tsx'
import { visuallyHidden } from '../components/visuallyHidden.ts'
import {
  DeleteChartDocument,
  SaveVehicleWidgetDocument,
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
import { uuidV4 } from '../offline/uuid.ts'
import { useToast } from '../toast/toastContext.ts'
import { AddWidgetDialog } from './AddWidgetDialog.tsx'
import { WidgetEditDialog, type EditChoice, type EditMode } from './WidgetEditDialog.tsx'
import { draftOf, recipeOfDraft, type WidgetDraft } from './widgetDraft.ts'
import { ChartCard } from './ChartCard.tsx'
import { recipeOf } from './chartFormat.ts'
import { DashboardGrid, type TileAction } from './DashboardGrid.tsx'
import { canonicalRecipe, periodFigureOf, type FigureRecipe } from './figureRecipe.ts'
import { KeyFigure } from './KeyFigure.tsx'
import { useDraftDefaultTitle, useFigureTitle } from './useFigureTitle.ts'
import {
  addTile,
  chartKey,
  FIGURE_GRID,
  inReadingOrder,
  LAYOUT_PRESETS,
  moveTile,
  pinTile,
  removeTile,
  replaceTile,
  resizeTile,
  resolveLayout,
  setTilePeriod,
  templateOfTile,
  tileKey,
  toLayout,
  widgetOf,
  type Layout,
  type LayoutPresetId,
  type SavedFigures,
  type Tile,
  type TileRecipe,
  widgetInput,
} from './widgets.ts'
import type { PeriodControl } from './TrendPeriodPopover.tsx'
import { usePeriodText } from './usePeriodText.ts'

type Units = { distance: DistanceUnit; volume: VolumeUnit }
/** A saved widget (#273), its key figure's recipe in the app's shape. */
export type SavedChart = Omit<VehicleDashboardQuery['vehicleWidgets'][number], 'figure'> & { figure: ReturnType<typeof canonicalRecipe> | null }
type Scope = 'shared' | 'personal'

/** The dashboard while it is being arranged: whose, the tiles and the charts taken off as they are now, and what is going on. */
interface Editing {
  scope: Scope
  tiles: Tile[]
  hidden: SavedChart[]
  /** The Add widget dialog is open. */
  adding: boolean
  /**
   * The widget being edited from its tile's pencil (#275), while its dialog is shown: its tile, what the pencil edits and where it starts;
   * once saved, the tile in its place, whose pencil takes the focus when the dialog is gone.
   */
  editing: { tile: string; open: boolean; mode: EditMode; start: WidgetDraft; notes: string[]; focus?: string } | null
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
  const [saveWidget] = useMutation(SaveVehicleWidgetDocument)
  const [reset, resetting] = useMutation(ResetDashboardLayoutDocument)
  const [save, saving] = useMutation(SaveDashboardLayoutDocument)
  const [actionError, setActionError] = useState<unknown>()
  const [edit, setEdit] = useState<Editing | null>(null)
  const periodText = usePeriodText()
  const figureTitle = useFigureTitle()
  const draftDefaultTitle = useDraftDefaultTitle()
  const panel = useRef<HTMLElement>(null)
  // The saved widgets (#273) in the app's shape, and the recipe of each key figure among them, kept while the answer is (the grid's layouts follow it).
  const answered = data?.vehicleWidgets
  const charts: SavedChart[] = useMemo(() => (answered ?? []).map((w) => ({ ...w, figure: w.figure ? canonicalRecipe(w.figure) : null })), [answered])
  const savedFigure: SavedFigures = useMemo(() => (id) => charts.find((c) => c.id === id)?.figure, [charts])

  if (error) return <ErrorMessage error={error} />
  if (!data) return <DashboardSkeleton />

  const dashboard = data.vehicle?.dashboard
  const shared: Layout | null = dashboard?.shared ?? null
  const personal: Layout | null = dashboard?.personal ?? null
  // Everyone's dashboard holds only shared charts, except with sign-in off, where everyone is the same user and every chart is theirs
  // (so a chart of the anonymous user can still be edited and shared from there).
  const chartsFor = (scope: Scope) => (scope === 'shared' && !signInOff ? charts.filter((c) => c.isShared) : charts)
  const shown = resolveLayout(personal ?? shared, charts)
  const tiles = edit ? edit.tiles : shown.tiles
  const busy = saving.loading || resetting.loading

  const titleOf = (tile: Tile) => {
    const widget = widgetOf(tile, savedFigure)
    if (widget?.kind === 'figure') return figureTitle(widget.figure, widget.title)
    // A chart's own title, else its ready-made chart's, in the viewer's language (#275).
    if (widget?.kind === 'chart') return widget.title || (widget.template ? t(`dashboard.presets.${widget.template}`) : '')
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

  // Whose dashboard a change is made to: one's own if there is one, else the vehicle's when one may change it, else a new one of one's own.
  const ownScope: Scope = personal && !signInOff ? 'personal' : dashboard?.canChangeShared ? 'shared' : 'personal'
  const startEditing = () => setEdit(editingOf(ownScope))
  const editingOf = (scope: Scope): Editing => {
    const start = scope === 'shared' ? shared : (personal ?? shared)
    const resolved = resolveLayout(start, chartsFor(scope))
    return { scope, tiles: resolved.tiles, hidden: resolved.hidden, adding: false, editing: null, status: '' }
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
          const next = resizeTile(e.tiles, tile.id, change, undefined, savedFigure)
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
        case 'edit':
          return { editing: pencilOf(tile, e) }
      }
    })
  }
  /**
   * A saved widget added at the bottom (`chart:<id>`), out of the ones taken off; `name` and `figure` (its recipe when a key figure, null for a
   * chart) for one just saved, which this render's list does not hold yet: without it a key figure would be sized as a chart.
   */
  const addSaved = (id: string, name?: string, said?: 'added' | 'addedSaved', figure?: FigureRecipe | null) =>
    update((e) => ({
      tiles: addTile(e.tiles, chartKey(id), undefined, null, figure === undefined ? savedFigure : (x) => (x === id ? figure : savedFigure(x))),
      hidden: e.hidden.filter((c) => c.id !== id),
      status: t(`dashboard.edit.${said ?? 'added'}`, { name: name || titleOf({ id: chartKey(id) } as Tile) }),
    }))
  /**
   * A widget made in the builder (#275): a tile of its own in the arrangement, kept by Save dashboard; with *Also save to my widgets* a saved
   * widget at once (shared on Everyone's dashboard, private on one's own: the owner's choice) and a tile pointing at it.
   */
  const addMade = async (recipe: TileRecipe, saveToo: boolean, title: string) => {
    if (saveToo) {
      const result = await saveWidget({ variables: { input: { vehicleId: vehicle.id, title, shared: edit?.scope === 'shared', ...widgetInput(recipe) } } })
      const id = result.data?.saveVehicleWidget.id
      if (!id) return
      await refetch()
      addSaved(id, title, 'addedSaved', recipe.widget === 'FIGURE' ? (recipe.figure ?? null) : null)
      return
    }
    const tile: Tile = { id: tileKey(uuidV4()), x: 0, y: 0, w: 1, h: 1, pinned: false, recipe }
    update((e) => ({ tiles: addTile(e.tiles, tile.id, undefined, recipe), status: t('dashboard.edit.added', { name: titleOf(tile) }) }))
  }
  /** A saved widget deleted for good from *Your widgets* (#275): from every dashboard it is on, at once. */
  const deleteSaved = async (w: SavedChart) => {
    await remove({ variables: { id: w.id } })
    await refetch()
    update((e) => ({ tiles: removeTile(e.tiles, chartKey(w.id)), hidden: e.hidden.filter((h) => h.id !== w.id), status: t('dashboard.edit.deleted', { name: w.figure ? figureTitle(w.figure, w.title) : w.title }) }))
  }

  /**
   * What a key figure tile's chip does with a period (#230): it is shown only while the dashboard is arranged (#260, on the tile's strip),
   * and Apply changes the tile being edited, saved with the rest by Save dashboard. Outside Customize there is no chip, so no control; a
   * figure without a period (a point in time) and a saved one (its recipe is its own, #275) have none either.
   */
  const periodControl = (tile: Tile): PeriodControl | undefined => {
    const figure = tile.recipe?.widget === 'FIGURE' ? tile.recipe.figure : null
    const key = figure && periodFigureOf(figure)
    if (!figure?.period || !key || !dashboard || !edit) return undefined
    const id = tile.id
    return {
      hint: t('dashboard.period.saveTo.editing'),
      canApply: true,
      apply: async (period) => update((e) => ({ tiles: setTilePeriod(e.tiles, id, period), status: t('dashboard.period.changedSaid', { name: titleOf(tile), period: periodText(key, period) }) })),
    }
  }
  const applyPreset = (id: LayoutPresetId) =>
    update((e) => {
      const resolved = resolveLayout(LAYOUT_PRESETS[id], chartsFor(e.scope))
      return { tiles: resolved.tiles, hidden: resolved.hidden, status: t('dashboard.customize.started', { name: t(`dashboard.layoutPresets.${id}`) }) }
    })
  /** The saved widget a tile points at, if any. */
  const savedOf = (tile: Pick<Tile, 'id'>) => charts.find((c) => chartKey(c.id) === tile.id)
  /**
   * What a tile's pencil edits (#275): one's own saved widget, someone else's (copied as one's own in its place), else the tile's own recipe
   * in the arrangement; where it starts, and what the dialog says about where it is (the other arrangement the user sees, when it holds the
   * same saved widget; that others may have a shared one).
   */
  const pencilOf = (tile: Tile, e: Editing): Editing['editing'] => {
    const saved = savedOf(tile)
    if (saved) {
      const start = draftOf({ widget: saved.widget, title: saved.title, chart: saved.chart ? recipeOf(saved.chart) : null, figure: saved.figure })
      if (!saved.canEdit) return { tile: tile.id, open: true, mode: 'copy', start, notes: [t('dashboard.widget.copyNote')] }
      const other = e.scope === 'shared' ? personal : shared
      const elsewhere = other?.tiles.some((x) => x.id === tile.id) ? [t(e.scope === 'shared' ? 'dashboard.widget.alsoOnOwn' : 'dashboard.widget.alsoOnShared')] : []
      return { tile: tile.id, open: true, mode: 'own', start, notes: [...elsewhere, ...(saved.isShared ? [t('dashboard.widget.othersMayHave')] : [])] }
    }
    const recipe = tile.recipe
    return recipe ? { tile: tile.id, open: true, mode: 'arrangement', start: draftOf(recipe), notes: [t('dashboard.widget.inArrangement')] } : null
  }
  /** A saved widget's name for its title when it is saved: the draft's own, else its default as the builder shows it. */
  const titleOfDraft = (d: WidgetDraft) => d.title.trim() || draftDefaultTitle(d)
  /**
   * What a save in the edit dialog does (#275). In the arrangement: the tile's recipe changed in place, or, saved to one's widgets too, a
   * saved widget the tile then points at. One's own saved widget: only here (a copy in the arrangement takes the tile's place) or
   * everywhere (the saved widget itself, at once, keeping its sharing). Someone else's: saved as one's own (shared on Everyone's), in its
   * place. Everything but a saved widget's change waits for Save dashboard, as the rest of the arrangement does.
   */
  const saveEdit = async (from: string, draft: WidgetDraft, choice: EditChoice) => {
    const recipe = recipeOfDraft(draft)
    const before = edit?.tiles.find((x) => x.id === from)
    const name = before ? titleOf(before) : titleOfDraft(draft)
    const saveNew = async (shared: boolean) => {
      const result = await saveWidget({ variables: { input: { vehicleId: vehicle.id, title: titleOfDraft(draft), shared, ...widgetInput(recipe) } } })
      await refetch()
      return result.data?.saveVehicleWidget.id
    }
    // A saved widget whose tile another takes (a copy here, one's own copy of someone else's) goes to `hidden`, as the bin's Remove puts
    // it: a saved widget the arrangement does not mention comes back at the bottom once it is loaded again (`resolveLayout`).
    const replaced = savedOf({ id: from })
    const placed = (to: string, recipeOf: TileRecipe | null, said: 'changed' | 'copied') =>
      update((e) =>
        e.tiles.some((x) => x.id === from)
          ? {
              tiles: replaceTile(e.tiles, from, to, recipeOf),
              hidden: replaced && to !== from && !e.hidden.some((h) => h.id === replaced.id) ? [...e.hidden, replaced] : e.hidden,
              status: t(`dashboard.edit.${said}`, { name }),
              editing: e.editing && { ...e.editing, focus: to },
            }
          : {},
      )
    switch (choice.kind) {
      case 'arrangement': {
        if (!choice.saveToo) return placed(from, recipe, 'changed')
        const id = await saveNew(edit?.scope === 'shared')
        if (id) placed(chartKey(id), null, 'changed')
        return
      }
      case 'here':
        return placed(tileKey(uuidV4()), recipe, 'changed')
      case 'everywhere': {
        const saved = savedOf({ id: from })
        if (!saved) return
        await saveWidget({ variables: { input: { id: saved.id, vehicleId: vehicle.id, title: titleOfDraft(draft), shared: saved.isShared, ...widgetInput(recipe) } } })
        await refetch()
        return placed(from, null, 'changed')
      }
      case 'copy': {
        const id = await saveNew(edit?.scope === 'shared')
        if (id) placed(chartKey(id), null, 'copied')
      }
    }
  }
  /** A tile's badge while customizing (#275): in the arrangement, saved, or saved and shared by someone else. */
  const badgeOf = (tile: Tile) => {
    const saved = savedOf(tile)
    if (!saved) return tile.recipe ? t('dashboard.widget.badge.arrangement') : null
    return saved.isShared && !saved.canEdit ? t('dashboard.widget.badge.sharedBy', { name: saved.createdBy?.displayName ?? '' }) : t('dashboard.widget.badge.saved')
  }
  /** What the pencil does: Customize someone else's saved widget (copied as one's own), Edit every other. */
  const editKindOf = (tile: Tile): 'edit' | 'customize' => (savedOf(tile)?.canEdit === false ? 'customize' : 'edit')
  const onDashboard = new Set(edit?.tiles.map((x) => x.id))
  // The templates some tile was made from (#274, #275): Add widget offers the other figures, and marks the ready-made charts on it.
  const made = new Set(edit?.tiles.map(templateOfTile))
  const scopes: Scope[] = [...(dashboard?.canChangeShared ? (['shared'] as const) : []), ...(!signInOff ? (['personal'] as const) : [])]
  const stored = edit ? (edit.scope === 'shared' ? shared : personal) : null

  // ---- the tiles -----------------------------------------------------------------------------------------

  const savedCard = (c: SavedChart) => {
    if (!c.chart) return null
    const r = recipeOf(c.chart)
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
      />
    )
  }
  const render = (tile: Tile) => {
    const widget = widgetOf(tile, savedFigure)
    if (widget?.kind === 'figure') return <KeyFigure recipe={widget.figure} label={titleOf(tile)} vehicleId={vehicle.id} control={periodControl(tile)} rows={tile.h} units={vehicle.units} />
    // The tiles' headings come right under the vehicle's name: h2, with no section heading between.
    if (widget?.kind === 'chart') return <ChartCard vehicleId={vehicle.id} title={titleOf(tile)} recipe={widget.chart} units={vehicle.units} fill headingLevel={2} />
    const saved = charts.find((c) => c.id === widget?.id)
    // A saved key figure (#273) is drawn like a tile's own; changing its recipe is the builder's (#275).
    if (saved?.figure) return <KeyFigure recipe={saved.figure} label={figureTitle(saved.figure, saved.title)} vehicleId={vehicle.id} rows={tile.h} units={vehicle.units} />
    return saved ? savedCard(saved) : null
  }

  return (
    <Stack ref={panel} component="section" aria-label={t('dashboard.sectionLabel')} sx={{ gap: 2 }}>
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
            made={made}
            saved={chartsFor(edit.scope).filter((c) => !onDashboard.has(chartKey(c.id)))}
            onAdd={addMade}
            onAddSaved={(w) => addSaved(w.id)}
            onDelete={deleteSaved}
          />
          {edit.editing && (
            <WidgetEditDialog
              key={edit.editing.tile}
              vehicle={vehicle}
              mode={edit.editing.mode}
              start={edit.editing.start}
              notes={edit.editing.notes}
              open={edit.editing.open}
              onOpenChange={(open) => update((e) => ({ editing: e.editing && { ...e.editing, open } }))}
              onClosed={() => {
                // The focus back on the pencil of the tile in its place (a copy or a saved widget's may have replaced it).
                const id = edit.editing?.focus ?? edit.editing?.tile
                update(() => ({ editing: null }))
                // Its pencil, or its menu where a narrow tile has the pencil in it (#275).
                if (id) panel.current?.querySelector<HTMLElement>(`[data-tile="${CSS.escape(id)}"] [data-action="edit"], [data-tile="${CSS.escape(id)}"] [aria-haspopup="menu"]`)?.focus()
              }}
              onSave={(draft, choice) => saveEdit(edit.editing!.tile, draft, choice)}
            />
          )}
        </>
      )}
      {actionError !== undefined && <ErrorMessage error={actionError} />}
      {tiles.length > 0 ? (
        <DashboardGrid
          tiles={tiles}
          editing={edit !== null}
          render={render}
          name={titleOf}
          editKind={editKindOf}
          badge={badgeOf}
          savedFigure={savedFigure}
          onTilesChange={(next) => update(() => ({ tiles: next }))}
          onAction={onAction}
        />
      ) : (
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          {t('dashboard.customize.noCharts')}
        </Typography>
      )}
      {!charts.some((c) => c.chart) && (
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          {t('dashboard.noCharts')}
        </Typography>
      )}
    </Stack>
  )
}
