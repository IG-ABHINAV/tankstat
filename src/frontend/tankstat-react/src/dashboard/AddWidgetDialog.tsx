import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Chip from '@mui/material/Chip'
import List from '@mui/material/List'
import ListItem from '@mui/material/ListItem'
import ListItemButton from '@mui/material/ListItemButton'
import ListItemIcon from '@mui/material/ListItemIcon'
import ListItemText from '@mui/material/ListItemText'
import ListSubheader from '@mui/material/ListSubheader'
import Skeleton from '@mui/material/Skeleton'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import useMediaQuery from '@mui/material/useMediaQuery'
import { ArrowLeft, ChartArea, ChartColumn, ChartLine, ChartNoAxesCombined, ChartPie, Hash, Plus, TrendingUp } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { DialogButtons, DialogCancel, DialogFrame } from '../dialogs/DialogFrame.tsx'
import { Form } from '../forms/Form.tsx'
import type { DistanceUnit, VehicleDashboardQuery, VolumeUnit } from '../gql/generated.ts'
import { useToast } from '../toast/toastContext.ts'
import { MEDIA } from '../theme/media.ts'
import { ChartCard } from './ChartCard.tsx'
import { ChartForm } from './ChartForm.tsx'
import { NEW_RECIPE, recipeOf, type ChartRecipe } from './chartFormat.ts'
import { Figure } from './Figures.tsx'
import { usePeriodText } from './usePeriodText.ts'
import { DEFAULT_PERIOD, isTrendFigure, type TrendFigureId, type TrendPeriod } from './trendPeriod.ts'
import { TrendFigure } from './TrendFigure.tsx'
import { TrendPeriodForm } from './TrendPeriodForm.tsx'
import { useTrendPreview } from './useTrendPreview.ts'
import { chartKey, defaultSize, GAP, presetKey, ROW_HEIGHT, type FigureId, type ReadyChart } from './widgets.ts'

type Summary = NonNullable<NonNullable<VehicleDashboardQuery['vehicle']>['summary']>
type SavedChart = VehicleDashboardQuery['vehicleCharts'][number]
type Units = { distance: DistanceUnit; volume: VolumeUnit }

/** A ready-made chart the dialog offers, and whether it is on the dashboard already (#247: then it can only be customized here). */
export interface ReadyOffer {
  chart: ReadyChart
  onDashboard: boolean
}

/** What the dialog can start from: a key figure, a ready-made chart, one of the user's saved charts, or a blank chart. */
type Choice = { kind: 'figure'; id: FigureId } | ({ kind: 'ready' } & ReadyOffer) | { kind: 'saved'; chart: SavedChart } | { kind: 'blank' }

/**
 * A trend figure's pane (#230): the tile as it will be added, over the period chosen below it (the same form as the tile's chip, which
 * changes it later), the figure's default to start; Add puts it on the dashboard with that period.
 */
function TrendFigurePane({ figure, vehicle, rows, onAdd }: { figure: TrendFigureId; vehicle: { id: string; units: Units }; rows: number; onAdd: (period: TrendPeriod) => void }) {
  const { t } = useTranslation()
  const [period, setPeriod] = useState(DEFAULT_PERIOD[figure])
  const preview = useTrendPreview(figure, vehicle.id, period)
  const periodText = usePeriodText()
  return (
    <Form
      onSubmit={(e) => {
        e.preventDefault()
        onAdd(period)
      }}
    >
      <Stack sx={{ gap: 2 }}>
        <Box role="status" aria-live="polite" aria-label={t('dashboard.period.preview')} sx={{ width: 'min(100%, 18rem)', height: rows * ROW_HEIGHT + (rows - 1) * GAP }}>
          {preview.data ? (
            <TrendFigure figure={figure} period={period} data={preview.data} rows={rows} units={vehicle.units} label={t(`dashboard.kpi.${figure}`)} chip={<Chip size="small" variant="outlined" label={periodText(figure, period)} sx={{ flex: 'none', maxWidth: '60%' }} />} />
          ) : (
            <Skeleton variant="rounded" aria-hidden height="100%" />
          )}
        </Box>
        <TrendPeriodForm figure={figure} value={period} onChange={setPeriod} preview={preview} />
        <DialogButtons>
          <DialogCancel />
          <Button type="submit" disabled={preview.problem !== null}>
            <Plus size={16} aria-hidden />
            {t('dashboard.widget.add')}
          </Button>
        </DialogButtons>
      </Stack>
    </Form>
  )
}

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
 * The one way to put something on the dashboard being arranged (#228): the starting points on the left (the key figures and ready-made
 * charts (#247: also those on it, marked so), the user's saved charts off it, and a blank chart), the picked one on the right with its
 * preview on the vehicle's data. A figure or a saved chart is added as it is; a ready-made chart as it is too, or customized first and
 * then saved as the user's own chart and added (`ChartForm`); one already on the dashboard only customized, saved once something
 * differs (`mustChange`), and added beside it; a blank chart is composed and saved. On a phone the two sides are two steps.
 */
export function AddWidgetDialog({
  open,
  onClose,
  vehicle,
  summary,
  canShare,
  everyone,
  figures,
  ready,
  saved,
  onAdd,
  onChartSaved,
}: {
  open: boolean
  onClose: () => void
  vehicle: { id: string; units: Units }
  summary: Summary | null | undefined
  canShare: boolean
  /** Everyone's dashboard is being arranged: a chart made here must be shared, or it would be dropped from it. */
  everyone: boolean
  figures: readonly FigureId[]
  /** Every ready-made chart, in their order, each saying whether it is on the dashboard. */
  ready: readonly ReadyOffer[]
  saved: readonly SavedChart[]
  /** A widget added, by its tile id; a trend figure with the period chosen for it (#230). */
  onAdd: (id: string, period?: TrendPeriod | null) => void
  /** A chart saved here (the user's own, new): the dashboard adds its tile. */
  onChartSaved: (id: string, title: string) => void | Promise<unknown>
}) {
  const { t } = useTranslation()
  const { toast } = useToast()
  const phone = useMediaQuery(MEDIA.narrow)
  const [picked, setPicked] = useState<Choice | null>(null)
  const [step, setStep] = useState<'list' | 'item'>('list')
  // What the dialog opens on: something that can be added as it is, a ready-made chart already on the dashboard never.
  const readyOff = ready.find((r) => !r.onDashboard)
  const first: Choice = figures.length > 0 ? { kind: 'figure', id: figures[0] } : readyOff ? { kind: 'ready', ...readyOff } : saved.length > 0 ? { kind: 'saved', chart: saved[0] } : { kind: 'blank' }
  const current = picked ?? first
  const pick = (next: Choice) => {
    setPicked(next)
    setStep('item')
  }
  const close = () => {
    onClose()
    setPicked(null)
    setStep('list')
  }
  const added = (id: string, period?: TrendPeriod | null) => {
    onAdd(id, period)
    close()
  }
  const keyOf = (p: Choice) => `${p.kind}:${p.kind === 'blank' ? '' : p.kind === 'figure' ? p.id : p.chart.id}`

  const item = (p: Choice, label: string, glyph: ReactNode, last = false, note?: string) => (
    <ListItem key={keyOf(p)} disablePadding sx={last ? { borderTop: 1, borderColor: 'divider', mt: 1, pt: 1 } : undefined}>
      <ListItemButton selected={keyOf(p) === keyOf(current)} aria-current={keyOf(p) === keyOf(current) ? 'true' : undefined} onClick={() => pick(p)} sx={{ borderRadius: '10px', mx: 1 }}>
        <ListItemIcon sx={{ minWidth: 36, color: 'primary.light' }}>{glyph}</ListItemIcon>
        <ListItemText primary={label} secondary={note} />
      </ListItemButton>
    </ListItem>
  )
  const list = (
    <List dense disablePadding aria-label={t('dashboard.widget.listLabel')} sx={{ minWidth: 0 }}>
      {figures.length > 0 && <ListSubheader disableSticky>{t('dashboard.widget.groups.figures')}</ListSubheader>}
      {figures.map((f) => item({ kind: 'figure', id: f }, t(`dashboard.kpi.${f}`), isTrendFigure(f) ? <TrendingUp size={18} aria-hidden /> : <Hash size={18} aria-hidden />))}
      {ready.length > 0 && <ListSubheader disableSticky>{t('dashboard.widget.groups.ready')}</ListSubheader>}
      {ready.map((r) => item({ kind: 'ready', ...r }, t(`dashboard.presets.${r.chart.id}`), chartGlyph(r.chart.recipe), false, r.onDashboard ? t('dashboard.widget.onDashboard') : undefined))}
      {saved.length > 0 && <ListSubheader disableSticky>{t('dashboard.widget.groups.yours')}</ListSubheader>}
      {saved.map((c) => item({ kind: 'saved', chart: c }, c.title, chartGlyph(c)))}
      {item({ kind: 'blank' }, t('dashboard.widget.blank'), <Plus size={18} aria-hidden />, true)}
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
  const pane = () => {
    switch (current.kind) {
      case 'figure': {
        // As it will be added: at its default rows (#229), in a box as tall as that tile.
        const rows = defaultSize(current.id).h
        if (isTrendFigure(current.id))
          return (
            <>
              {heading(t(`dashboard.kpi.${current.id}`), t(`dashboard.widget.describe.figures.${current.id}`))}
              <TrendFigurePane key={current.id} figure={current.id} vehicle={vehicle} rows={rows} onAdd={(period) => added(current.id, period)} />
            </>
          )
        return (
          <>
            {heading(t(`dashboard.kpi.${current.id}`), t(`dashboard.widget.describe.figures.${current.id}`))}
            <Box sx={{ width: 'min(100%, 18rem)', height: rows * ROW_HEIGHT + (rows - 1) * GAP }}>
              <Figure id={current.id} vehicleId={vehicle.id} rows={rows} summary={summary} units={vehicle.units} />
            </Box>
            <DialogButtons>
              <DialogCancel />
              <Button type="button" onClick={() => added(current.id)}>
                <Plus size={16} aria-hidden />
                {t('dashboard.widget.add')}
              </Button>
            </DialogButtons>
          </>
        )
      }
      case 'saved':
        return (
          <>
            {heading(current.chart.title, current.chart.isShared ? t('dashboard.widget.describe.shared', { name: current.chart.createdBy?.displayName ?? '' }) : t('dashboard.widget.describe.saved'))}
            <ChartCard vehicleId={vehicle.id} title={current.chart.title} recipe={recipeOf(current.chart)} units={vehicle.units} headingLevel={2} />
            <DialogButtons>
              <DialogCancel />
              <Button type="button" onClick={() => added(chartKey(current.chart.id))}>
                <Plus size={16} aria-hidden />
                {t('dashboard.widget.add')}
              </Button>
            </DialogButtons>
          </>
        )
      case 'ready': {
        const title = t(`dashboard.presets.${current.chart.id}`)
        if (current.onDashboard)
          return (
            <>
              {heading(title, `${t(`dashboard.widget.describe.ready.${current.chart.id}`)} ${t('dashboard.widget.describe.readyOnDashboard')}`)}
              <ChartForm
                key={current.chart.id}
                vehicle={vehicle}
                canShare={canShare}
                start={{ title, recipe: current.chart.recipe }}
                mustChange
                shareLocked={everyone}
                previewFirst
                submitLabel={t('dashboard.widget.saveAndAdd')}
                onSaved={async (id, saved) => {
                  await onChartSaved(id, saved)
                  toast(t('toast.saved'))
                  close()
                }}
              />
            </>
          )
        return (
          <>
            {heading(title, `${t(`dashboard.widget.describe.ready.${current.chart.id}`)} ${t('dashboard.widget.describe.readyHint')}`)}
            <ChartForm
              key={current.chart.id}
              vehicle={vehicle}
              canShare={canShare}
              start={{ title, recipe: current.chart.recipe }}
              ready
              shareLocked={everyone}
              previewFirst
              submitLabel={t('dashboard.widget.saveAndAdd')}
              onAddReady={() => added(presetKey(current.chart.id))}
              onSaved={async (id, saved) => {
                await onChartSaved(id, saved)
                toast(t('toast.saved'))
                close()
              }}
            />
          </>
        )
      }
      case 'blank':
        return (
          <>
            {heading(t('dashboard.widget.blank'), t('dashboard.widget.describe.blank'))}
            <ChartForm
              vehicle={vehicle}
              canShare={canShare}
              start={{ title: '', recipe: NEW_RECIPE }}
              shareLocked={everyone}
              previewFirst
              submitLabel={t('dashboard.widget.saveAndAdd')}
              onSaved={async (id, saved) => {
                await onChartSaved(id, saved)
                toast(t('toast.saved'))
                close()
              }}
            />
          </>
        )
    }
  }

  return (
    <DialogFrame open={open} onClose={close} maxWidth={1000} title={t('dashboard.widget.title')} description={t('dashboard.widget.description')}>
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
  )
}
