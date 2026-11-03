import Card from '@mui/material/Card'
import Typography from '@mui/material/Typography'
import { ArrowDown, ArrowUp, Minus } from 'lucide-react'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import type { DistanceUnit, VehicleDashboardQuery, VolumeUnit } from '../gql/generated.ts'
import { useFormat } from '../i18n/format.ts'
import { spentText } from '../spending.ts'
import { LazySparkline } from './LazySparkline.tsx'
import type { FigureId } from './widgets.ts'

type Summary = NonNullable<NonNullable<VehicleDashboardQuery['vehicle']>['summary']>
type Units = { distance: DistanceUnit; volume: VolumeUnit }

/**
 * A key figure's card, showing what its tile's rows hold (#229, the rows per figure in `widgets.ts` `FIGURE_ROWS`): two rows the label
 * and the value, packed closer (84 px); three the comparison line too, or the note where there is none (132 px); four or more everything,
 * spaced as always (180 px), so an arrangement of four-row figures looks as it did. The label stays the card's first paragraph.
 */
function Kpi({ rows, label, value, hint, change, sparkline }: { rows: number; label: string; value: string; hint?: string; change?: ReactNode; sparkline?: ReactNode }) {
  const full = rows >= 4
  const third = rows >= 3
  return (
    <Card sx={{ px: 2, py: third ? 2 : 1.5, height: '100%', boxSizing: 'border-box', overflow: 'hidden' }}>
      <Typography variant="body2" sx={{ color: 'text.secondary' }}>
        {label}
      </Typography>
      <Typography variant="h3" component="p" sx={{ my: full ? 0.5 : 0 }}>
        {value}
      </Typography>
      {hint && (full || (third && !change)) && (
        <Typography variant="caption" component="p" sx={{ color: 'text.secondary' }}>
          {hint}
        </Typography>
      )}
      {third && change}
      {full && sparkline}
    </Card>
  )
}

type Words = 'moreThanBefore' | 'lessThanBefore' | 'sameAsBefore' | 'moreFuel' | 'lessFuel' | 'sameFuel' | 'moreThanLastYear' | 'lessThanLastYear' | 'sameAsLastYear'

/**
 * A change in words with an arrow, never by colour: "HUF 60,000 (12%) more than the 12 months before". `size` writes the difference in the
 * figure's unit; left out (consumption), the per cent alone says it; `count` goes into the words (the fill-ups compared with). Nothing to
 * compare with yet is said so (`none`), and so is a recent window with nothing in it (`noneRecent`), which is the other way round.
 */
function Change({ recent, earlier, words, size, count, none, noneRecent }: { recent: number | null | undefined; earlier: number | null | undefined; words: readonly [Words, Words, Words]; size?: (value: number) => string; count?: number; none: string; noneRecent?: string }) {
  const { t } = useTranslation()
  const format = useFormat()
  let text: string
  let Icon = Minus
  if (recent == null) text = earlier != null && noneRecent ? noneRecent : none
  else if (earlier == null || earlier === 0) text = none
  else {
    const diff = recent - earlier
    const percent = format.number(Math.abs(diff) / earlier, { style: 'percent', maximumFractionDigits: 0 })
    const change = size ? `${size(Math.abs(diff))} (${percent})` : percent
    text = t(`dashboard.trend.${words[diff > 0 ? 0 : diff < 0 ? 1 : 2]}`, { change, count })
    Icon = diff > 0 ? ArrowUp : diff < 0 ? ArrowDown : Minus
  }
  return (
    <Typography variant="caption" component="p" sx={{ color: 'text.secondary', display: 'flex', alignItems: 'center', gap: 0.5 }}>
      <Icon size={14} aria-hidden />
      <span>{text}</span>
    </Typography>
  )
}

/** One key figure of a vehicle's dashboard, by its id (`widgets.ts` `FIGURES`). */
export function Figure({ id, rows, summary: s, units }: { id: FigureId; rows: number; summary: Summary | null | undefined; units: Units }) {
  const { t } = useTranslation()
  const format = useFormat()
  const none = t('dashboard.kpi.none')
  const trends = s?.trends
  switch (id) {
    case 'spendingTrend': {
      const main = trends?.spending[0]
      const others = trends?.spending.slice(1).filter((c) => c.recent) ?? []
      return (
        <Kpi
          rows={rows}
          label={t('dashboard.kpi.spendingTrend')}
          value={main ? format.money(main.recent ?? 0, main.currency) : none}
          hint={others.length > 0 ? others.map((c) => format.money(c.recent ?? 0, c.currency)).join(' · ') : undefined}
          change={main && <Change recent={main.recent} earlier={main.earlier} words={['moreThanBefore', 'lessThanBefore', 'sameAsBefore']} size={(v) => format.money(v, main.currency)} none={t('dashboard.trend.nothingBefore')} />}
        />
      )
    }
    case 'costPerDistance': {
      const main = trends?.costPerDistance[0]
      return (
        <Kpi
          rows={rows}
          label={t('dashboard.kpi.costPerDistance')}
          value={main?.recent != null ? format.costPerDistance(main.recent, main.currency, units.distance) : none}
          change={main && <Change recent={main.recent} earlier={main.earlier} words={['moreThanBefore', 'lessThanBefore', 'sameAsBefore']} size={(v) => format.costPerDistance(v, main.currency, units.distance)} none={t('dashboard.trend.nothingBefore')} noneRecent={t('dashboard.trend.nothingDriven')} />}
        />
      )
    }
    case 'consumptionTrend': {
      const c = trends?.consumption
      // Fuel per distance in both windows (the stored figure), whatever unit the words show it in: more is more fuel, in mpg too.
      return (
        <Kpi
          rows={rows}
          label={t('dashboard.kpi.consumptionTrend')}
          value={c?.recent != null ? format.consumption(c.recent, units) : none}
          hint={c ? t('dashboard.kpi.lastFillUps', { count: c.recentFillUps }) : undefined}
          change={trends && <Change recent={c?.recent} earlier={c?.earlier} words={['moreFuel', 'lessFuel', 'sameFuel']} count={c?.earlierFillUps} none={t('dashboard.trend.tooFew')} />}
        />
      )
    }
    case 'distanceTrend': {
      const d = trends?.distance
      return (
        <Kpi
          rows={rows}
          label={t('dashboard.kpi.distanceTrend')}
          value={d?.recent != null ? format.distance(d.recent, units.distance) : none}
          change={d && <Change recent={d.recent} earlier={d.earlier} words={['moreThanLastYear', 'lessThanLastYear', 'sameAsLastYear']} size={(v) => format.distance(v, units.distance)} none={t('dashboard.trend.nothingBefore')} />}
        />
      )
    }
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
