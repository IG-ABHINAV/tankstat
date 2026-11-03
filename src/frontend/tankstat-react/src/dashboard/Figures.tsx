import Box from '@mui/material/Box'
import Card from '@mui/material/Card'
import Typography from '@mui/material/Typography'
import { ArrowDown, ArrowUp, Minus } from 'lucide-react'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import type { DistanceUnit, VehicleDashboardQuery, VolumeUnit } from '../gql/generated.ts'
import { useFormat } from '../i18n/format.ts'
import { spentText } from '../spending.ts'
import { LazySparkline } from './LazySparkline.tsx'
import { useTilePadding } from './tileChrome.ts'
import type { FigureId } from './widgets.ts'

type Summary = NonNullable<NonNullable<VehicleDashboardQuery['vehicle']>['summary']>
type Units = { distance: DistanceUnit; volume: VolumeUnit }

function Kpi({ label, value, hint, children }: { label: string; value: string; hint?: string; children?: ReactNode }) {
  const pt = useTilePadding(2) // under the edit strip while the dashboard is arranged
  return (
    <Card sx={{ p: 2, pt, height: '100%', boxSizing: 'border-box', overflow: 'hidden' }}>
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

type Words = 'moreThanBefore' | 'lessThanBefore' | 'sameAsBefore' | 'moreFuel' | 'lessFuel' | 'sameFuel' | 'moreThanLastYear' | 'lessThanLastYear' | 'sameAsLastYear'
/** What a rise means for the figure: spending or fuel going up is `worse`; more distance is neither good nor bad (`neutral`). */
type Meaning = 'worseUp' | 'neutral'
/** The arrow's colour by its meaning (#228): never alone, the arrow's direction and the words say the same. */
type Tone = 'bad' | 'good' | 'neutral' | 'same'
const TONE: Record<Tone, 'error' | 'success' | 'primary' | 'neutral'> = { bad: 'error', good: 'success', neutral: 'primary', same: 'neutral' }

/**
 * A change in words with an arrow, coloured by what the change means (up is red for spending and fuel, green down; distance is neutral
 * either way) but never by colour alone: "HUF 60,000 (12%) more than the 12 months before". `size` writes the difference in the
 * figure's unit; left out (consumption), the per cent alone says it; `count` goes into the words (the fill-ups compared with). Nothing to
 * compare with yet is said so (`none`), and so is a recent window with nothing in it (`noneRecent`), which is the other way round.
 */
function Change({ recent, earlier, words, meaning, size, count, none, noneRecent }: { recent: number | null | undefined; earlier: number | null | undefined; words: readonly [Words, Words, Words]; meaning: Meaning; size?: (value: number) => string; count?: number; none: string; noneRecent?: string }) {
  const { t } = useTranslation()
  const format = useFormat()
  let text: string
  let Icon = Minus
  let tone: Tone = 'same'
  if (recent == null) text = earlier != null && noneRecent ? noneRecent : none
  else if (earlier == null || earlier === 0) text = none
  else {
    const diff = recent - earlier
    const percent = format.number(Math.abs(diff) / earlier, { style: 'percent', maximumFractionDigits: 0 })
    const change = size ? `${size(Math.abs(diff))} (${percent})` : percent
    text = t(`dashboard.trend.${words[diff > 0 ? 0 : diff < 0 ? 1 : 2]}`, { change, count })
    Icon = diff > 0 ? ArrowUp : diff < 0 ? ArrowDown : Minus
    tone = diff === 0 ? 'same' : meaning === 'neutral' ? 'neutral' : (diff > 0) === (meaning === 'worseUp') ? 'bad' : 'good'
  }
  return (
    <Typography variant="caption" component="p" sx={{ color: 'text.secondary', display: 'flex', alignItems: 'center', gap: 1 }}>
      <Box component="span" data-tone={tone} aria-hidden sx={(theme) => ({ display: 'inline-flex', alignItems: 'center', justifyContent: 'center', width: 24, height: 24, borderRadius: '50%', flex: 'none', bgcolor: theme.vars.palette[TONE[tone]].soft, color: theme.vars.palette[TONE[tone]].softText })}>
        <Icon size={14} />
      </Box>
      <span>{text}</span>
    </Typography>
  )
}

/** One key figure of a vehicle's dashboard, by its id (`widgets.ts` `FIGURES`). */
export function Figure({ id, summary: s, units }: { id: FigureId; summary: Summary | null | undefined; units: Units }) {
  const { t } = useTranslation()
  const format = useFormat()
  const none = t('dashboard.kpi.none')
  const trends = s?.trends
  switch (id) {
    case 'spendingTrend': {
      const main = trends?.spending[0]
      const others = trends?.spending.slice(1).filter((c) => c.recent) ?? []
      return (
        <Kpi label={t('dashboard.kpi.spendingTrend')} value={main ? format.money(main.recent ?? 0, main.currency) : none} hint={others.length > 0 ? others.map((c) => format.money(c.recent ?? 0, c.currency)).join(' · ') : undefined}>
          {main && <Change recent={main.recent} earlier={main.earlier} words={['moreThanBefore', 'lessThanBefore', 'sameAsBefore']} meaning="worseUp" size={(v) => format.money(v, main.currency)} none={t('dashboard.trend.nothingBefore')} />}
        </Kpi>
      )
    }
    case 'costPerDistance': {
      const main = trends?.costPerDistance[0]
      return (
        <Kpi label={t('dashboard.kpi.costPerDistance')} value={main?.recent != null ? format.costPerDistance(main.recent, main.currency, units.distance) : none}>
          {main && <Change recent={main.recent} earlier={main.earlier} words={['moreThanBefore', 'lessThanBefore', 'sameAsBefore']} meaning="worseUp" size={(v) => format.costPerDistance(v, main.currency, units.distance)} none={t('dashboard.trend.nothingBefore')} noneRecent={t('dashboard.trend.nothingDriven')} />}
        </Kpi>
      )
    }
    case 'consumptionTrend': {
      const c = trends?.consumption
      // Fuel per distance in both windows (the stored figure), whatever unit the words show it in: more is more fuel, in mpg too.
      return (
        <Kpi label={t('dashboard.kpi.consumptionTrend')} value={c?.recent != null ? format.consumption(c.recent, units) : none} hint={c ? t('dashboard.kpi.lastFillUps', { count: c.recentFillUps }) : undefined}>
          {trends && <Change recent={c?.recent} earlier={c?.earlier} words={['moreFuel', 'lessFuel', 'sameFuel']} meaning="worseUp" count={c?.earlierFillUps} none={t('dashboard.trend.tooFew')} />}
        </Kpi>
      )
    }
    case 'distanceTrend': {
      const d = trends?.distance
      return (
        <Kpi label={t('dashboard.kpi.distanceTrend')} value={d?.recent != null ? format.distance(d.recent, units.distance) : none}>
          {d && <Change recent={d.recent} earlier={d.earlier} words={['moreThanLastYear', 'lessThanLastYear', 'sameAsLastYear']} meaning="neutral" size={(v) => format.distance(v, units.distance)} none={t('dashboard.trend.nothingBefore')} />}
        </Kpi>
      )
    }
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
