import { SparkLineChart } from '@mui/x-charts/SparkLineChart'
import { useTranslation } from 'react-i18next'
import { useFormat } from '../i18n/format.ts'
import { PALETTE } from './chartFormat.ts'

/**
 * A tiny trend line without axes. It is decorative: the same figures are in the accessible label. It is not animated: it usually sits in a
 * hidden or sliding panel (a draw-in animation that starts out of sight can stall half way), and the panel's own slide is the motion.
 */
/** `label` names what it draws (#275: a key figure's measure); the vehicle card's spending by default. */
export function Sparkline({ points, currency, height = 36, label: name }: { points: { month: string; amount: number }[]; currency?: string | null; height?: number; label?: string }) {
  const { t } = useTranslation()
  const format = useFormat()
  const label = `${name ?? t('welcome.card.trend')}: ${points.map((p) => (currency ? format.money(p.amount, currency) : String(p.amount))).join(', ')}`

  return (
    <div role="img" aria-label={label} style={{ height, width: '100%' }}>
      <div aria-hidden style={{ height: '100%' }}>
        <SparkLineChart data={points.map((p) => p.amount)} height={height} area curve="monotoneX" color={PALETTE[0]} skipAnimation disableKeyboardNavigation />
      </div>
    </div>
  )
}
