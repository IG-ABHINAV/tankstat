import Box from '@mui/material/Box'
import Checkbox from '@mui/material/Checkbox'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { useId } from 'react'
import { useTranslation } from 'react-i18next'
import { Segmented } from '../components/Segmented.tsx'
import { LabeledSelect } from '../components/UnitSelect.tsx'
import { Field } from '../forms/Field.tsx'
import { FieldInput } from '../forms/FieldInput.tsx'
import type { DistanceUnit, FigureMeasure, VolumeUnit, WidgetKind } from '../gql/generated.ts'
import { ChartCard } from './ChartCard.tsx'
import { ChartFields } from './ChartFields.tsx'
import { isReady } from './chartFormat.ts'
import { figureRows, isPointInTime, periodFigureOf, type FigureRecipe } from './figureRecipe.ts'
import { KeyFigure } from './KeyFigure.tsx'
import { OutsideTile } from './tileChrome.ts'
import type { TrendPeriod } from './trendPeriod.ts'
import { TrendPeriodForm } from './TrendPeriodForm.tsx'
import { useFigurePreview } from './useFigurePreview.ts'
import { useFigureTitle, useMeasureName } from './useFigureTitle.ts'
import { FIGURE_MEASURES, isReadyAsIs, switchKind, withMeasure, type WidgetDraft } from './widgetDraft.ts'
import { GAP, ROW_HEIGHT } from './widgets.ts'

type Vehicle = { id: string; units: { distance: DistanceUnit; volume: VolumeUnit } }

/** A check with its label beside it, 44 px tall (the builder's sparkline, *Also save to my widgets*). */
export function CheckRow({ label, checked, onChange }: { label: string; checked: boolean; onChange: (checked: boolean) => void }) {
  const id = useId()
  return (
    <Stack direction="row" sx={{ alignItems: 'center', gap: 1, minHeight: 44 }}>
      <Checkbox id={id} checked={checked} onChange={(e) => onChange(e.target.checked)} sx={{ ml: -1 }} />
      <Typography component="label" htmlFor={id} variant="body2">
        {label}
      </Typography>
    </Stack>
  )
}

/** A key figure's period and what it is compared with (`TrendPeriodForm`, its quick picks its figure's or its measure's), over the server's windows. */
function FigurePeriod({ vehicle, figure, onChange }: { vehicle: Vehicle; figure: FigureRecipe & { period: TrendPeriod }; onChange: (period: TrendPeriod) => void }) {
  const preview = useFigurePreview(figure.measure, vehicle.id, figure.period)
  const key = periodFigureOf(figure)!
  // Another measure may have other picks: the form starts afresh with it.
  return <TrendPeriodForm key={key} figure={key} value={figure.period} onChange={onChange} preview={preview} />
}

/**
 * The builder's one form (#275), for adding and for editing: the kind (only when a widget is made, `chooseKind`; switching keeps the
 * measure where the other kind has it), then a key figure's title (empty for its default), what it shows, its period and comparison and
 * the sparkline (left out for the odometer and the last fill-up, the owner's choice), or a chart's title and recipe (`ChartFields`), each
 * with its preview on the vehicle's data. Controlled: the dialog that holds it decides where the widget goes.
 */
export function WidgetForm({ vehicle, draft, onChange, chooseKind }: { vehicle: Vehicle; draft: WidgetDraft; onChange: (draft: WidgetDraft) => void; chooseKind: boolean }) {
  const { t } = useTranslation()
  const figureTitle = useFigureTitle()
  const measureName = useMeasureName()
  const previewId = useId()
  const figure = draft.figure
  const point = isPointInTime(figure.measure)
  const readyAsIs = draft.kind === 'CHART' && isReadyAsIs(draft)
  const defaultTitle = draft.kind === 'FIGURE' ? figureTitle(figure, null) : readyAsIs && draft.template ? t(`dashboard.presets.${draft.template}`) : ''
  const shownTitle = draft.title.trim() || defaultTitle
  const set = (patch: Partial<WidgetDraft>) => onChange({ ...draft, ...patch })
  const setFigure = (patch: Partial<FigureRecipe>) => set({ figure: { ...figure, ...patch } })
  const rows = figureRows(figure)

  return (
    <Stack sx={{ gap: 2, minWidth: 0 }}>
      {chooseKind && (
        <Segmented<WidgetKind>
          label={t('dashboard.widget.kind')}
          value={draft.kind}
          options={[
            { value: 'FIGURE', label: t('dashboard.widget.kindFigure') },
            { value: 'CHART', label: t('dashboard.widget.kindChart') },
          ]}
          onChange={(kind) => onChange(switchKind(draft, kind))}
        />
      )}
      <Field
        name="title"
        label={t('dashboard.widget.fieldTitle')}
        required={draft.kind === 'CHART' && !readyAsIs}
        hint={defaultTitle ? t('dashboard.widget.titleDefault', { title: defaultTitle }) : undefined}
      >
        <FieldInput maxLength={80} autoComplete="off" value={draft.title} placeholder={defaultTitle} onChange={(e) => set({ title: e.target.value })} />
      </Field>
      {draft.kind === 'FIGURE' ? (
        <>
          <LabeledSelect<FigureMeasure> label={t('dashboard.widget.measure')} value={figure.measure} options={FIGURE_MEASURES.map((m) => ({ value: m, label: measureName(m) }))} onChange={(m) => set({ figure: withMeasure(figure, m) })} />
          {!point && figure.period && (
            <>
              <FigurePeriod vehicle={vehicle} figure={{ ...figure, period: figure.period }} onChange={(period) => setFigure({ period })} />
              <CheckRow label={t('dashboard.widget.sparkline')} checked={figure.sparkline} onChange={(sparkline) => setFigure({ sparkline })} />
            </>
          )}
        </>
      ) : (
        <ChartFields recipe={draft.chart} onChange={(chart) => set({ chart })} />
      )}
      <Box component="section" aria-labelledby={previewId}>
        <Typography id={previewId} sx={{ fontWeight: 700, mb: 1 }}>
          {t('dashboard.widget.preview')}
        </Typography>
        {/* Opened from a tile's strip while arranging, the preview is a card of its own, not the tile's (#262). */}
        <OutsideTile>
          {draft.kind === 'FIGURE' ? (
            <Box sx={{ width: 'min(100%, 18rem)', height: rows * ROW_HEIGHT + (rows - 1) * GAP }}>
              <KeyFigure recipe={figure} label={shownTitle} vehicleId={vehicle.id} rows={rows} units={vehicle.units} />
            </Box>
          ) : isReady(draft.chart) ? (
            <ChartCard vehicleId={vehicle.id} title={shownTitle || t('charts.preview')} recipe={draft.chart} units={vehicle.units} headingLevel={2} />
          ) : (
            <Typography variant="body2" sx={{ color: 'text.secondary' }}>
              {t('errors.chart.datesRequired')}
            </Typography>
          )}
        </OutsideTile>
      </Box>
    </Stack>
  )
}
