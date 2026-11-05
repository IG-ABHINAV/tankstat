import Box from '@mui/material/Box'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { LabeledSwitch } from '../components/LabeledSwitch.tsx'
import { Segmented } from '../components/Segmented.tsx'
import { TabbedPanels } from '../components/TabbedPanels.tsx'
import { LabeledSelect } from '../components/UnitSelect.tsx'
import { todayIso } from '../forms/dates.ts'
import { Field } from '../forms/Field.tsx'
import { FieldDate } from '../forms/FieldDate.tsx'
import { FieldInput } from '../forms/FieldInput.tsx'
import type { TrendComparison, TrendUnit } from '../gql/generated.ts'
import { useErrorText } from '../i18n/errors.ts'
import { canBeWhole, EARLIEST, MAX_COUNT, QUICK_PICKS, sameWindow, startOf, tabOf, type PeriodFigureId, type PeriodTab, type TrendPeriod, type Words } from './trendPeriod.ts'
import { TrendTimeline } from './TrendTimeline.tsx'
import { usePeriodText } from './usePeriodText.ts'
import type { useFigurePreview } from './useFigurePreview.ts'

const UNITS: readonly TrendUnit[] = ['DAYS', 'WEEKS', 'MONTHS', 'YEARS', 'FILL_UPS']
const UNIT_KEY: Record<TrendUnit, 'days' | 'weeks' | 'months' | 'years' | 'fillUps'> = { DAYS: 'days', WEEKS: 'weeks', MONTHS: 'months', YEARS: 'years', FILL_UPS: 'fillUps' }

/**
 * A trend figure's period being chosen (#230), with no buttons of its own: the tile's popover and the Add widget dialog's figure pane put
 * it in a form with theirs. Tabs *Quick* (the figure's picks), *Last* (a number and a unit, whole weeks, months or years if asked) and
 * *Since* (a first day), the comparison, and the two windows the server worked out for it (`preview`, the caller's `useFigurePreview`, so
 * the dialog's tile and this read one answer).
 */
export function TrendPeriodForm({ figure, value, onChange, preview }: { figure: PeriodFigureId; value: TrendPeriod; onChange: (period: TrendPeriod) => void; preview: ReturnType<typeof useFigurePreview> }) {
  const { t } = useTranslation()
  const errorText = useErrorText()
  const periodText = usePeriodText()
  const [tab, setTab] = useState<PeriodTab>(() => tabOf(figure, value))
  // The number as typed (an empty field is no period yet, not a 0).
  const [count, setCount] = useState(() => String(value.count ?? ''))
  const say = (w: Words) => String(t(w.key as never, w.values))
  const choose = (next: PeriodTab) => {
    const start = startOf(next, figure, value, todayIso())
    setTab(next)
    setCount(String(start.count ?? ''))
    onChange(start)
  }
  const picks = QUICK_PICKS[figure]
  const picked = picks.findIndex((q) => sameWindow(q, value))
  const unit = value.unit ?? 'DAYS'

  const quick = () => (
    <Segmented
      label={t('dashboard.period.quickLabel')}
      value={picked < 0 ? '' : String(picked)}
      options={picks.map((q, i) => ({ value: String(i), label: periodText(figure, { ...q, against: value.against }) }))}
      onChange={(i) => onChange({ ...picks[Number(i)], against: value.against })}
    />
  )
  const last = () => (
    <Stack sx={{ gap: 1.5 }}>
      <Stack direction="row" sx={{ gap: 1.5, alignItems: 'flex-start', '& > :first-of-type': { flex: '0 0 7rem' }, '& > :last-of-type': { flex: '1 1 8rem', minWidth: 0 } }}>
        <Box>
          <Field name="count" label={t('dashboard.period.count')} required invalid={{ message: t('errors.trend.countRange', { max: MAX_COUNT }), test: (v) => !/^\d+$/.test(v) || Number(v) < 1 || Number(v) > MAX_COUNT }}>
            <FieldInput
              type="number"
              inputMode="numeric"
              value={count}
              onChange={(e) => {
                setCount(e.target.value)
                onChange({ ...value, count: /^\d+$/.test(e.target.value) ? Number(e.target.value) : null })
              }}
              slotProps={{ input: { min: 1, max: MAX_COUNT, step: 1 } }}
            />
          </Field>
        </Box>
        <Box>
          <LabeledSelect
            label={t('dashboard.period.unit')}
            value={unit}
            options={UNITS.map((u) => ({ value: u, label: t(`dashboard.period.units.${UNIT_KEY[u]}`) }))}
            onChange={(u) => onChange({ ...value, unit: u, whole: canBeWhole(u) && value.whole })}
          />
        </Box>
      </Stack>
      {canBeWhole(unit) ? (
        <LabeledSwitch label={t(`dashboard.period.wholeSwitch.${UNIT_KEY[unit] as 'weeks' | 'months' | 'years'}`)} hint={t(value.whole ? 'dashboard.period.wholeHint' : 'dashboard.period.endsToday')} checked={value.whole} onChange={(whole) => onChange({ ...value, whole })} />
      ) : (
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          {t(unit === 'FILL_UPS' ? 'dashboard.period.fillUpsHint' : 'dashboard.period.endsToday')}
        </Typography>
      )}
    </Stack>
  )
  const since = () => (
    <Field
      name="since"
      label={t('dashboard.period.sinceDate')}
      hint={t('dashboard.period.endsToday')}
      required
      invalid={{ message: t('errors.trend.sinceRange', { earliest: EARLIEST, latest: todayIso() }), test: (v) => v !== '' && v < EARLIEST }}
    >
      <FieldDate disableFuture value={value.since ?? ''} onChange={(day) => onChange({ ...value, since: day || null })} />
    </Field>
  )

  const { problem, data, error } = preview
  return (
    <Stack sx={{ gap: 2, minWidth: 0 }}>
      <Box>
        <TabbedPanels
          label={t('dashboard.period.tabsLabel')}
          value={tab}
          onChange={choose}
          tabs={[
            { value: 'quick', label: t('dashboard.period.tabs.quick'), content: quick },
            { value: 'last', label: t('dashboard.period.tabs.last'), content: last },
            { value: 'since', label: t('dashboard.period.tabs.since'), content: since },
          ]}
        />
      </Box>
      <LabeledSelect<TrendComparison>
        label={t('dashboard.period.against')}
        value={value.against}
        options={[
          // No comparison (#274, the owner's choice): the figure shows its value alone, without an arrow or a sentence.
          { value: 'NONE', label: t('dashboard.period.againstNone') },
          { value: 'BEFORE', label: t('dashboard.period.againstBefore') },
          { value: 'YEAR_EARLIER', label: t('dashboard.period.againstYearEarlier') },
        ]}
        onChange={(against) => onChange({ ...value, against })}
      />
      {/* The windows as the server works them out; until it answers (or while the period is not valid) the room stays. */}
      <Box sx={{ minHeight: 36 }}>
        {data?.recent ? (
          <TrendTimeline recent={data.recent} earlier={data.earlier ?? null} />
        ) : error ? (
          <Typography variant="body2" sx={{ color: 'text.secondary' }}>
            {errorText(error)}
          </Typography>
        ) : problem ? (
          <Typography variant="body2" sx={{ color: 'text.secondary' }}>
            {say(problem)}
          </Typography>
        ) : null}
      </Box>
    </Stack>
  )
}
