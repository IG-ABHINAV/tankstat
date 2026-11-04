import { useTranslation } from 'react-i18next'
import type { DistanceUnit, VehicleDashboardQuery, VolumeUnit } from '../gql/generated.ts'
import { useFormat } from '../i18n/format.ts'
import { spentText } from '../spending.ts'
import { LazySparkline } from './LazySparkline.tsx'
import { Kpi } from './Kpi.tsx'
import type { PeriodControl } from './TrendPeriodPopover.tsx'
import { TrendTile } from './TrendTile.tsx'
import { isTrendFigure, type TrendPeriod } from './trendPeriod.ts'
import type { FigureId } from './widgets.ts'

type Summary = NonNullable<NonNullable<VehicleDashboardQuery['vehicle']>['summary']>
type Units = { distance: DistanceUnit; volume: VolumeUnit }

/**
 * One key figure of a vehicle's dashboard, by its id (`widgets.ts` `FIGURES`): the plain ones from the summary, a trend figure over its
 * tile's period from its own answer (`TrendTile`, #230).
 */
export function Figure({ id, vehicleId, period, control, rows, summary: s, units }: { id: FigureId; vehicleId: string; period?: TrendPeriod | null; control?: PeriodControl; rows: number; summary: Summary | null | undefined; units: Units }) {
  const { t } = useTranslation()
  const format = useFormat()
  const none = t('dashboard.kpi.none')
  if (isTrendFigure(id)) return <TrendTile figure={id} vehicleId={vehicleId} period={period} control={control} rows={rows} units={units} />
  switch (id) {
    case 'thisMonth':
      return (
        <Kpi
          rows={rows}
          label={t('dashboard.kpi.thisMonth')}
          value={(s && spentText(s.spending, 'thisMonth', format)) ?? none}
          hint={s && s.spending.length > 0 ? t('dashboard.kpi.lastMonth', { amount: spentText(s.spending, 'lastMonth', format) }) : undefined}
          sparkline={s && s.currency && <LazySparkline points={s.spendTrend} currency={s.currency} />}
        />
      )
    case 'consumption':
      return <Kpi rows={rows} label={t('dashboard.kpi.consumption')} value={s?.averageConsumption != null ? format.consumption(s.averageConsumption, units) : none} hint={t('dashboard.kpi.consumptionHint')} />
    case 'odometer':
      return <Kpi rows={rows} label={t('dashboard.kpi.odometer')} value={s?.latestOdometer != null ? format.distance(s.latestOdometer, units.distance) : none} />
    case 'lastFillUp':
      return (
        <Kpi
          rows={rows}
          label={t('dashboard.kpi.lastFillUp')}
          value={s?.lastFillUpDate ? format.date(s.lastFillUpDate) : none}
          hint={s ? t('dashboard.kpi.counts', { fuel: s.fillUpCount, expenses: s.expenseCount }) : undefined}
        />
      )
  }
}
