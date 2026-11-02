import Card from '@mui/material/Card'
import Typography from '@mui/material/Typography'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import type { DistanceUnit, VehicleDashboardQuery, VolumeUnit } from '../gql/generated.ts'
import { useFormat } from '../i18n/format.ts'
import { spentText } from '../spending.ts'
import { LazySparkline } from './LazySparkline.tsx'
import type { FigureId } from './widgets.ts'

type Summary = NonNullable<NonNullable<VehicleDashboardQuery['vehicle']>['summary']>
type Units = { distance: DistanceUnit; volume: VolumeUnit }

function Kpi({ label, value, hint, children }: { label: string; value: string; hint?: string; children?: ReactNode }) {
  return (
    <Card sx={{ p: 2, height: '100%', boxSizing: 'border-box', overflow: 'hidden' }}>
      <Typography variant="body2" sx={{ color: 'text.secondary' }}>
        {label}
      </Typography>
      <Typography variant="h3" component="p" sx={{ my: 0.5 }}>
        {value}
      </Typography>
      {hint && (
        <Typography variant="caption" component="p" sx={{ color: 'text.secondary' }}>
          {hint}
        </Typography>
      )}
      {children}
    </Card>
  )
}

/** One key figure of a vehicle's dashboard, by its id (`widgets.ts` `FIGURES`). */
export function Figure({ id, summary: s, units }: { id: FigureId; summary: Summary | null | undefined; units: Units }) {
  const { t } = useTranslation()
  const format = useFormat()
  const none = t('dashboard.kpi.none')
  switch (id) {
    case 'thisMonth':
      return (
        <Kpi
          label={t('dashboard.kpi.thisMonth')}
          value={(s && spentText(s.spending, 'thisMonth', format)) ?? none}
          hint={s && s.spending.length > 0 ? t('dashboard.kpi.lastMonth', { amount: spentText(s.spending, 'lastMonth', format) }) : undefined}
        >
          {s && s.currency && <LazySparkline points={s.spendTrend} currency={s.currency} />}
        </Kpi>
      )
    case 'consumption':
      return <Kpi label={t('dashboard.kpi.consumption')} value={s?.averageConsumption != null ? format.consumption(s.averageConsumption, units) : none} hint={t('dashboard.kpi.consumptionHint')} />
    case 'odometer':
      return <Kpi label={t('dashboard.kpi.odometer')} value={s?.latestOdometer != null ? format.distance(s.latestOdometer, units.distance) : none} />
    case 'lastFillUp':
      return (
        <Kpi
          label={t('dashboard.kpi.lastFillUp')}
          value={s?.lastFillUpDate ? format.date(s.lastFillUpDate) : none}
          hint={s ? t('dashboard.kpi.counts', { fuel: s.fillUpCount, expenses: s.expenseCount }) : undefined}
        />
      )
  }
}
