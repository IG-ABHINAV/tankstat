import Box from '@mui/material/Box'
import Card from '@mui/material/Card'
import Typography from '@mui/material/Typography'
import { ArrowDown, ArrowUp, Minus } from 'lucide-react'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { useFormat } from '../i18n/format.ts'
import { useTilePadding } from './tileChrome.ts'

/**
 * A key figure's card, showing what its tile's rows hold (#229, the rows per figure in `widgets.ts` `FIGURE_ROWS`): two rows the label
 * and the value, packed closer (84 px); three the comparison line too, or the note where there is none (132 px); four or more everything,
 * spaced as always (180 px), so an arrangement of four-row figures looks as it did. The label stays the card's first paragraph; a trend
 * figure's period chip (#230) sits beside it, in the label row under the edit strip, never in it.
 */
export function Kpi({ rows, label, chip, value, hint, change, sparkline }: { rows: number; label: string; chip?: ReactNode; value: ReactNode; hint?: string; change?: ReactNode; sparkline?: ReactNode }) {
  const full = rows >= 4
  const third = rows >= 3
  const pt = useTilePadding(third ? 2 : 1.5) // under the edit strip while the dashboard is arranged
  return (
    <Card sx={{ px: 2, py: third ? 2 : 1.5, pt, height: '100%', boxSizing: 'border-box', overflow: 'hidden' }}>
      <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 1, minWidth: 0 }}>
        <Typography variant="body2" sx={{ color: 'text.secondary', minWidth: 0 }}>
          {label}
        </Typography>
        {chip}
      </Box>
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

/**
 * A measurement's number in the figure's size and its unit after it in small type (#259: "HUF 4,293" and "/ 100 km"), at every height, so
 * a per-distance value fits a narrow tile on one line; read out as one ("HUF 4,293 / 100 km"). `format.consumptionParts` and
 * `costPerDistanceParts` split them.
 */
export function ValueWithUnit({ value, unit }: { value: string; unit: string }) {
  return (
    <>
      {value}{' '}
      <Box component="span" sx={{ typography: 'body2', fontWeight: 600, color: 'text.secondary' }}>
        {unit}
      </Box>
    </>
  )
}

/** What a rise means for the figure: spending or fuel going up is `worse`; more distance is neither good nor bad (`neutral`). */
export type Meaning = 'worseUp' | 'neutral'
/** The arrow's colour by its meaning (#228): never alone, the arrow's direction and the words say the same. */
type Tone = 'bad' | 'good' | 'neutral' | 'same'
const TONE: Record<Tone, 'error' | 'success' | 'primary' | 'neutral'> = { bad: 'error', good: 'success', neutral: 'primary', same: 'neutral' }

/**
 * A change in words with an arrow, coloured by what the change means (up is red for spending and fuel, green down; distance is neutral
 * either way) but never by colour alone: "HUF 60,000 (12%) more than the 12 months before", `than` being what the recent window is
 * compared with (`trendPeriod.ts` `comparedWith`). `size` writes the difference in the figure's unit; left out (consumption, `fuel`), the
 * per cent alone says it; `wrap` adds to a comparison (the fill-ups compared). Nothing to compare with yet is said so (`none`), and so is a
 * recent window with nothing in it (`noneRecent`), which is the other way round.
 */
export function Change({ recent, earlier, fuel = false, than, meaning, size, wrap, none, noneRecent }: { recent: number | null | undefined; earlier: number | null | undefined; fuel?: boolean; than: string; meaning: Meaning; size?: (value: number) => string; wrap?: (sentence: string) => string; none: string; noneRecent?: string }) {
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
    const way = diff > 0 ? 'more' : diff < 0 ? 'less' : 'same'
    const sentence = t(`dashboard.trend.${way}${fuel ? 'Fuel' : ''}`, { change, than })
    text = wrap ? wrap(sentence) : sentence
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
