import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import List from '@mui/material/List'
import ListItem from '@mui/material/ListItem'
import ListItemButton from '@mui/material/ListItemButton'
import ListItemIcon from '@mui/material/ListItemIcon'
import ListItemText from '@mui/material/ListItemText'
import ListSubheader from '@mui/material/ListSubheader'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import useMediaQuery from '@mui/material/useMediaQuery'
import { ArrowLeft, ChartArea, ChartColumn, ChartLine, ChartNoAxesCombined, ChartPie, Hash, Plus, Sparkles, Trash2, TrendingUp } from 'lucide-react'
import { useState, type FormEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { ConfirmDialog } from '../components/ConfirmDialog.tsx'
import { IconAction } from '../components/IconAction.tsx'
import { DialogButtons, DialogCancel, DialogFrame } from '../dialogs/DialogFrame.tsx'
import { Form } from '../forms/Form.tsx'
import type { DistanceUnit, VolumeUnit } from '../gql/generated.ts'
import { ErrorMessage } from '../messages.tsx'
import { MEDIA } from '../theme/media.ts'
import { ChartCard } from './ChartCard.tsx'
import { recipeOf, type ChartRecipe } from './chartFormat.ts'
import type { SavedChart } from './DashboardPanel.tsx'
import { FIGURES, figureRows, type FigureId } from './figureRecipe.ts'
import { KeyFigure } from './KeyFigure.tsx'
import { isTrendFigure } from './trendPeriod.ts'
import { useDraftDefaultTitle, useFigureTitle } from './useFigureTitle.ts'
import { draftProblem, figureDraft, readyDraft, recipeOfDraft, scratchDraft, type WidgetDraft } from './widgetDraft.ts'
import { CheckRow, WidgetForm } from './WidgetForm.tsx'
import { GAP, presetKey, READY_CHARTS, ROW_HEIGHT, type ReadyChart, type TileRecipe } from './widgets.ts'

type Units = { distance: DistanceUnit; volume: VolumeUnit }

/** What the dialog starts from: a built-in figure or a ready-made chart (a template), one of the user's saved widgets, or from scratch. */
type Choice = { kind: 'figure'; id: FigureId } | { kind: 'ready'; chart: ReadyChart } | { kind: 'saved'; widget: SavedChart } | { kind: 'scratch' }

const draftOfChoice = (c: Choice): WidgetDraft | null =>
  c.kind === 'figure' ? figureDraft(c.id) : c.kind === 'ready' ? readyDraft(c.chart) : c.kind === 'scratch' ? scratchDraft() : null

/** The glyph of a chart's kind (bars, a line, an area, a donut; bars and a line for a chart with a second measure). */
function chartGlyph(recipe: Pick<ChartRecipe, 'kind' | 'secondMetric'>): ReactNode {
  if (recipe.secondMetric) return <ChartNoAxesCombined size={18} aria-hidden />
  switch (recipe.kind) {
    case 'LINE': return <ChartLine size={18} aria-hidden />
    case 'AREA': return <ChartArea size={18} aria-hidden />
    case 'DONUT': return <ChartPie size={18} aria-hidden />
    default: return <ChartColumn size={18} aria-hidden />
  }
}

/**
 * The one way to put something on the dashboard being arranged (#275): the list in three groups on the left (*Start from*: the 8 built-in
 * figures and the 10 ready-made charts as templates, each marked *Key figure* or *Chart*, and *On the dashboard* when a tile was made from
 * it, still addable; *Your widgets*: the saved widgets not on this dashboard, each with a bin by its own right; *From scratch*), the picked
 * one on the right. A template or *From scratch* opens the builder (`WidgetForm`) filled in or empty, and *Add to the dashboard* puts the
 * widget into the arrangement, with *Also save to my widgets* (off at first) saving it as a saved widget too, shared on Everyone's
 * dashboard and private on one's own; a saved widget shows its preview and is added as it is (pointing at it) or deleted for good after a
 * confirmation. On a phone the two sides are two steps.
 */
export function AddWidgetDialog({
  open,
  onClose,
  vehicle,
  made,
  saved,
  onAdd,
  onAddSaved,
  onDelete,
}: {
  open: boolean
  onClose: () => void
  vehicle: { id: string; units: Units }
  /** The templates some tile was made from (`templateOfTile`): marked *On the dashboard*. */
  made: ReadonlySet<string | null>
  /** The saved widgets not on this dashboard, one's own and the shared ones. */
  saved: readonly SavedChart[]
  /** A widget made in the builder: its recipe, whether it is saved to one's widgets too, and the title it is saved under. */
  onAdd: (recipe: TileRecipe, saveToo: boolean, title: string) => Promise<unknown>
  /** A saved widget added as it is. */
  onAddSaved: (widget: SavedChart) => void
  /** A saved widget deleted for good (after the dialog's confirmation). */
  onDelete: (widget: SavedChart) => Promise<unknown>
}) {
  const { t } = useTranslation()
  const figureTitle = useFigureTitle()
  const draftDefaultTitle = useDraftDefaultTitle()
  const phone = useMediaQuery(MEDIA.narrow)
  const first: Choice = { kind: 'figure', id: FIGURES[0] }
  const [picked, setPicked] = useState<Choice>(first)
  const [draft, setDraft] = useState<WidgetDraft | null>(() => draftOfChoice(first))
  const [step, setStep] = useState<'list' | 'item'>('list')
  const [saveToo, setSaveToo] = useState(false)
  const [busy, setBusy] = useState(false)
  const [failure, setFailure] = useState<unknown>()
  const [deleting, setDeleting] = useState<SavedChart | null>(null)
  // Picked a saved widget that is gone meanwhile (deleted here): back to the first template.
  const current: Choice = picked.kind === 'saved' && !saved.some((w) => w.id === picked.widget.id) ? first : picked

  const pick = (next: Choice) => {
    setPicked(next)
    setDraft(draftOfChoice(next))
    setFailure(undefined)
    setStep('item')
  }
  const close = () => {
    onClose()
    setPicked(first)
    setDraft(draftOfChoice(first))
    setSaveToo(false)
    setFailure(undefined)
    setStep('list')
  }
  const keyOf = (c: Choice) => (c.kind === 'figure' ? `figure:${c.id}` : c.kind === 'ready' ? `ready:${c.chart.id}` : c.kind === 'saved' ? `saved:${c.widget.id}` : 'scratch')
  const named = (w: SavedChart) => (w.figure ? figureTitle(w.figure, w.title) : w.title)

  const item = (c: Choice, label: string, glyph: ReactNode, note?: string, after?: ReactNode) => (
    <ListItem key={keyOf(c)} disablePadding secondaryAction={after}>
      <ListItemButton selected={keyOf(c) === keyOf(current)} aria-current={keyOf(c) === keyOf(current) ? 'true' : undefined} onClick={() => pick(c)} sx={{ borderRadius: '10px', mx: 1 }}>
        <ListItemIcon sx={{ minWidth: 36, color: 'primary.light' }}>{glyph}</ListItemIcon>
        <ListItemText primary={label} secondary={note} />
      </ListItemButton>
    </ListItem>
  )
  const marked = (kind: string, template: string) => (made.has(template) ? t('dashboard.widget.kindOnDashboard', { kind }) : kind)
  const list = (
    <List dense disablePadding aria-label={t('dashboard.widget.listLabel')} sx={{ minWidth: 0 }}>
      <ListSubheader disableSticky>{t('dashboard.widget.groups.start')}</ListSubheader>
      {FIGURES.map((f) =>
        item({ kind: 'figure', id: f }, t(`dashboard.kpi.${f}`), isTrendFigure(f) ? <TrendingUp size={18} aria-hidden /> : <Hash size={18} aria-hidden />, marked(t('dashboard.widget.kindFigure'), f)),
      )}
      {READY_CHARTS.map((c) => item({ kind: 'ready', chart: c }, t(`dashboard.presets.${c.id}`), chartGlyph(c.recipe), marked(t('dashboard.widget.kindChart'), presetKey(c.id))))}
      {saved.length > 0 && <ListSubheader disableSticky>{t('dashboard.widget.groups.yours')}</ListSubheader>}
      {saved.map((w) =>
        item(
          { kind: 'saved', widget: w },
          named(w),
          w.chart ? chartGlyph(w.chart) : <Hash size={18} aria-hidden />,
          w.isShared && !w.canEdit ? t('dashboard.widget.yoursShared', { name: w.createdBy?.displayName ?? '' }) : t(w.figure ? 'dashboard.widget.yoursFigure' : 'dashboard.widget.yoursChart'),
          w.canDelete ? (
            <IconAction size="large" variant="ghost" tone="error" label={t('dashboard.deleteWidget', { title: named(w) })} onClick={() => setDeleting(w)}>
              <Trash2 size={16} aria-hidden />
            </IconAction>
          ) : undefined,
        ),
      )}
      <ListSubheader disableSticky>{t('dashboard.widget.groups.scratch')}</ListSubheader>
      {item({ kind: 'scratch' }, t('dashboard.widget.scratch'), <Sparkles size={18} aria-hidden />, t('dashboard.widget.scratchNote'))}
    </List>
  )

  const heading = (title: string, description: string) => (
    <Stack sx={{ gap: 0.5, mb: 1.5 }}>
      <Typography component="h3" variant="h5">
        {title}
      </Typography>
      <Typography variant="body2" sx={{ color: 'text.secondary' }}>
        {description}
      </Typography>
    </Stack>
  )

  async function add(e: FormEvent) {
    e.preventDefault()
    if (!draft || draftProblem(draft) || busy) return
    const recipe = recipeOfDraft(draft)
    // A saved widget is named when it is saved (#273): its own title, else its default as the builder shows it.
    const title = draft.title.trim() || draftDefaultTitle(draft)
    setBusy(true)
    setFailure(undefined)
    try {
      await onAdd(recipe, saveToo, title)
      close()
    } catch (err) {
      setFailure(err)
    } finally {
      setBusy(false)
    }
  }

  const pane = () => {
    if (current.kind === 'saved') {
      const w = current.widget
      return (
        <>
          {heading(
            named(w),
            w.isShared && !w.canEdit ? t('dashboard.widget.yoursShared', { name: w.createdBy?.displayName ?? '' }) : w.figure ? t('dashboard.widget.yoursFigure') : t('dashboard.widget.describe.saved'),
          )}
          {w.figure ? (
            <Box sx={{ width: 'min(100%, 18rem)', height: figureRows(w.figure) * ROW_HEIGHT + (figureRows(w.figure) - 1) * GAP }}>
              <KeyFigure recipe={w.figure} label={named(w)} vehicleId={vehicle.id} rows={figureRows(w.figure)} units={vehicle.units} />
            </Box>
          ) : w.chart ? (
            <ChartCard vehicleId={vehicle.id} title={w.title} recipe={recipeOf(w.chart)} units={vehicle.units} headingLevel={2} />
          ) : null}
          {/* A delete that failed is said here too, where its button is. */}
          {failure !== undefined && <ErrorMessage error={failure} />}
          <DialogButtons>
            {w.canDelete && (
              <Button type="button" variant="ghost" color="error" onClick={() => setDeleting(w)} sx={{ mr: 'auto' }}>
                <Trash2 size={16} aria-hidden />
                {t('dashboard.deleteConfirm')}
              </Button>
            )}
            <DialogCancel />
            <Button
              type="button"
              onClick={() => {
                onAddSaved(w)
                close()
              }}
            >
              <Plus size={16} aria-hidden />
              {t('dashboard.widget.add')}
            </Button>
          </DialogButtons>
        </>
      )
    }
    if (!draft) return null
    const describe =
      current.kind === 'figure' ? t(`dashboard.widget.describe.figures.${current.id}`)
      : current.kind === 'ready' ? t(`dashboard.widget.describe.ready.${current.chart.id}`)
      : t('dashboard.widget.scratchNote')
    const name = current.kind === 'figure' ? t(`dashboard.kpi.${current.id}`) : current.kind === 'ready' ? t(`dashboard.presets.${current.chart.id}`) : t('dashboard.widget.scratch')
    const problem = draftProblem(draft)
    return (
      <Form onSubmit={add}>
        {heading(name, describe)}
        <Stack sx={{ gap: 2 }}>
          <WidgetForm key={keyOf(current)} vehicle={vehicle} draft={draft} onChange={setDraft} chooseKind />
          {failure !== undefined && <ErrorMessage error={failure} />}
          <DialogButtons>
            <Box sx={{ mr: 'auto' }}>
              <CheckRow label={t('dashboard.widget.saveToo')} checked={saveToo} onChange={setSaveToo} />
            </Box>
            <DialogCancel disabled={busy} />
            <Button type="submit" loading={busy} disabled={problem !== null}>
              <Plus size={16} aria-hidden />
              {t('dashboard.widget.add')}
            </Button>
          </DialogButtons>
          {/* Plain text: a disabled button takes no focus, so a description linked to it would never be read. */}
          {problem === 'titleRequired' && (
            <Typography variant="body2" sx={{ color: 'text.secondary', textAlign: 'right' }}>
              {t('errors.chart.titleRequired')}
            </Typography>
          )}
        </Stack>
      </Form>
    )
  }

  return (
    <>
      <DialogFrame open={open} onClose={close} maxWidth={1000} title={t('dashboard.widget.title')} description={t('dashboard.widget.description')} busy={busy}>
        {phone ? (
          step === 'list' ? (
            <>
              {list}
              <DialogButtons>
                <DialogCancel />
              </DialogButtons>
            </>
          ) : (
            <Stack sx={{ gap: 1 }}>
              <Button type="button" variant="ghost" onClick={() => setStep('list')} sx={{ alignSelf: 'flex-start' }}>
                <ArrowLeft size={16} aria-hidden />
                {t('dashboard.widget.back')}
              </Button>
              {pane()}
            </Stack>
          )
        ) : (
          <Box sx={{ display: 'grid', gridTemplateColumns: 'minmax(14rem, 18rem) minmax(0, 1fr)', gap: 3, alignItems: 'start' }}>
            <Box sx={{ borderRight: 1, borderColor: 'divider', pr: 1 }}>{list}</Box>
            <Box sx={{ minWidth: 0 }}>{pane()}</Box>
          </Box>
        )}
      </DialogFrame>
      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(o) => !o && setDeleting(null)}
        title={t('dashboard.deleteTitle')}
        description={t('dashboard.deleteDescription', { title: deleting ? named(deleting) : '' })}
        confirmLabel={t('dashboard.deleteConfirm')}
        onConfirm={() => {
          const w = deleting
          setDeleting(null)
          if (!w) return
          setFailure(undefined)
          void onDelete(w).then(
            () => {
              // The pane showed the widget just deleted: back to the list, the first template picked and filled in (a saved widget has no draft).
              if (picked.kind !== 'saved' || picked.widget.id !== w.id) return
              setPicked(first)
              setDraft(draftOfChoice(first))
              setStep('list')
            },
            setFailure,
          )
        }}
      />
    </>
  )
}
