import Box from '@mui/material/Box'
import ButtonBase from '@mui/material/ButtonBase'
import Card from '@mui/material/Card'
import Tooltip from '@mui/material/Tooltip'
import Typography from '@mui/material/Typography'
import { ArrowDown, ArrowUp, Minus } from 'lucide-react'
import { type ReactNode, type Ref, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import type { ChangeWords, Tone } from './changeWords.ts'
import { COARSE } from '../theme/components.ts'
import { FULL, type Fit, type Measured, nextFit, offered } from './kpiFit.ts'
import { useTilePadding } from './tileChrome.ts'

export interface KpiProps {
  rows: number
  label: string
  chip?: ReactNode
  value: ReactNode
  /** The value may go one type step smaller when it wraps (#259: *Spent this month* in several currencies). */
  shrink?: boolean
  /** The value's short form, for the one-line form when even that line is too wide (the main currency and "+1", `MoreCount`). */
  short?: ReactNode
  hint?: string
  /** A trend figure's comparison (`useChangeWords`): its line when there is room, else its arrow alone before the value (#260). */
  change?: ChangeWords
  sparkline?: ReactNode
  /**
   * What the card shows, in words, beyond its label and note (its value, its comparison's figures): a change of it fits the card afresh
   * from its full form (#259). The size, the rows, the label and the note do so by themselves.
   */
  fitKey?: string
}

/**
 * A key figure's card, showing what its tile's rows hold (#229): two rows the label and the value, packed closer (84 px); three the
 * comparison line too, or the note where there is none (132 px); four or more everything, spaced as always (180 px), so an arrangement of
 * four-row figures looks as it did. A trend figure's comparison without a row of its own (two rows, or a line that does not fit) is its arrow
 * alone before the value, the sentence in its tooltip (#260). The label stays the card's first paragraph; a trend figure's period chip (#230) sits beside it, in the
 * label row under the edit strip, never in it.
 *
 * What does not fit the card's height is never cut (#259): the card measures itself (`kpiFit.ts`, one step a render in a layout effect, so
 * nothing is painted half way) and leaves out from the bottom what has no room, then puts the label and the value on one line each. It
 * starts from the full form again whenever its size, rows or words change; a card whose height follows its content (a preview) never
 * overflows, so it shows everything, its value smaller only where it wraps.
 */
export function Kpi({ fitKey = '', ...props }: KpiProps) {
  const card = useRef<HTMLDivElement>(null)
  const [size, setSize] = useState('')
  const key = `${props.rows}|${size}|${props.label}|${props.hint ?? ''}|${fitKey}`
  const [state, setState] = useState({ key, fit: FULL })
  // A change of what is measured fits it afresh from the full form (derived state: set while rendering, so the old form is never painted).
  if (state.key !== key) setState({ key, fit: FULL })
  const fit = state.key === key ? state.fit : FULL
  const offers = offersOf(props)

  // Measured once a form is shown: at the start, after each step and after starting again (each step turns a flag on, so it ends).
  useLayoutEffect(() => {
    if (!card.current) return
    const next = nextFit(fit, measure(card.current), offers)
    if (next !== fit) setState({ key, fit: next })
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `state` stands for the form shown; `offers` and `key` are of the same render
  }, [state])
  useEffect(() => {
    const element = card.current
    if (!element || typeof ResizeObserver === 'undefined') return
    // The observer's first callback reports the size the card was fitted at when it mounted: only a size after it fits the card afresh,
    // so a card takes its steps once on mount, not twice.
    let seen = ''
    const observer = new ResizeObserver(([entry]) => {
      const next = `${Math.round(entry.contentRect.width)}x${Math.round(entry.contentRect.height)}`
      const first = seen === ''
      if (next === seen) return
      seen = next
      if (!first) setSize(next)
    })
    observer.observe(element)
    return () => observer.disconnect()
  }, [])

  return <KpiCard {...props} fit={fit} cardRef={card} />
}

/** What a card's rows offer of what it was given (`kpiFit.ts` `offered`): `Kpi` fits by it, `KpiCard` draws by it. */
const offersOf = (p: Pick<KpiProps, 'rows' | 'sparkline' | 'hint' | 'change' | 'shrink' | 'short'>) =>
  offered(p.rows, { sparkline: !!p.sparkline, hint: !!p.hint, change: !!p.change, shrink: !!p.shrink, short: !!p.short })

/** What a card measures in the form it shows (`kpiFit.ts`): jsdom measures 0 and no line height, so there nothing overflows or wraps. */
function measure(card: HTMLElement): Measured {
  const value = card.querySelector<HTMLElement>('[data-kpi="value"]')
  const label = card.querySelector<HTMLElement>('[data-kpi="label"]')
  const line = value ? parseFloat(getComputedStyle(value).lineHeight) : NaN
  return {
    overflows: card.scrollHeight > card.clientHeight + 1,
    valueWraps: !!value && line > 0 && value.getBoundingClientRect().height > line * 1.5,
    valueTooWide: !!value && value.scrollWidth > value.clientWidth + 1,
    labelCut: !!label && label.scrollWidth > label.clientWidth + 1,
  }
}

/** One line, the rest cut off at its end: the one-line form's label (with an ellipsis) and value (the card's edge, a safety net only). */
const ONE_LINE = { whiteSpace: 'nowrap', overflow: 'hidden' } as const

/** A key figure's card in a given form (`Fit`): what `Kpi` draws once it has measured, and what the tests draw in each form. */
export function KpiCard({ rows, label, chip, value, short, hint, change, sparkline, shrink, fit = FULL, cardRef }: Omit<KpiProps, 'fitKey'> & { fit?: Fit; cardRef?: Ref<HTMLDivElement> }) {
  const full = rows >= 4
  const third = rows >= 3
  const offers = offersOf({ rows, sparkline, hint, change, shrink, short })
  const pt = useTilePadding(third ? 2 : 1.5) // under the edit strip while the dashboard is arranged
  return (
    <Card ref={cardRef} sx={{ px: 2, py: third ? 2 : 1.5, pt, height: '100%', boxSizing: 'border-box', overflow: 'hidden' }}>
      <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 1, minWidth: 0 }}>
        <CutLabel label={label} oneLine={fit.oneLine} cut={fit.labelCut} />
        {chip}
      </Box>
      {/* The value's row: the comparison's arrow alone before it (#260), a sibling, so the value's clipping never cuts the arrow's touch area. */}
      <Box data-kpi="value-row" sx={{ display: 'flex', alignItems: 'center', gap: 1, minWidth: 0, my: full ? 0.5 : 0 }}>
        {change && (offers.change === 'arrow' || fit.drop >= 3) && <ChangeArrow words={change} />}
        {/* The short form keeps its line whole (its "+1" reaches past it on a touch screen); only a long value is cut at the card's edge. */}
        <Typography data-kpi="value" variant={fit.smaller ? 'h4' : 'h3'} component="p" sx={{ minWidth: 0, ...(fit.oneLine && (fit.short && short ? { whiteSpace: 'nowrap' } : ONE_LINE)) }}>
          {fit.short && short ? short : value}
        </Typography>
      </Box>
      {offers.hint && fit.drop < 2 && (
        <Typography variant="caption" component="p" sx={{ color: 'text.secondary' }}>
          {hint}
        </Typography>
      )}
      {change && offers.change === 'line' && fit.drop < 3 && <ChangeLine words={change} />}
      {offers.sparkline && fit.drop < 1 && sparkline}
    </Card>
  )
}

/**
 * The card's label: wrapping as it needs, or on one line in the one-line form, cut with an ellipsis. Cut (#259), it takes the focus and shows
 * itself whole in a tooltip on hover, focus and tap, 44 px tall to a finger (padding the margin gives back, so nothing moves); a screen reader
 * reads it whole either way, since only its look is cut.
 */
function CutLabel({ label, oneLine, cut }: { label: string; oneLine: boolean; cut: boolean }) {
  const text = (
    <Typography
      data-kpi="label"
      variant="body2"
      tabIndex={cut ? 0 : undefined}
      sx={{ color: 'text.secondary', minWidth: 0, ...(oneLine && { ...ONE_LINE, textOverflow: 'ellipsis' }), ...(cut && { [COARSE]: { py: '12px', my: '-12px' } }) }}
    >
      {label}
    </Typography>
  )
  if (!cut) return text
  return (
    <Tooltip title={label} describeChild enterTouchDelay={0} leaveTouchDelay={8000}>
      {text}
    </Tooltip>
  )
}

/**
 * "+1" after the main currency's amount (#259): the other currencies spent, in its tooltip (hover, focus, tap) and in its name ("and 1 more:
 * €40.00"). A small pill, its button 44 px to a finger around it (padding the margin gives back).
 */
export function MoreCount({ others }: { others: string[] }) {
  const { t } = useTranslation()
  const list = others.join(' · ')
  return (
    <Tooltip title={list} describeChild enterTouchDelay={0} leaveTouchDelay={8000}>
      <ButtonBase
        aria-label={t('dashboard.kpi.moreCurrencies', { number: others.length, list })}
        sx={{ ml: 0.75, verticalAlign: 'baseline', borderRadius: 9999, [COARSE]: { py: '12px', px: '10px', my: '-12px', mx: '-10px', ml: '-4px' } }}
      >
        <Box component="span" sx={{ typography: 'caption', fontWeight: 600, px: 0.75, py: 0.25, borderRadius: 9999, bgcolor: 'primary.soft', color: 'primary.softText' }}>
          {t('dashboard.kpi.moreCount', { number: others.length })}
        </Box>
      </ButtonBase>
    </Tooltip>
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

/** The arrow's colour by the change's tone (#228): never alone, the arrow's direction and the words say the same. */
const TONE: Record<Tone, 'error' | 'success' | 'primary' | 'neutral'> = { bad: 'error', good: 'success', neutral: 'primary', same: 'neutral' }
const ICON: Record<ChangeWords['icon'], typeof Minus> = { up: ArrowUp, down: ArrowDown, same: Minus }

/** The arrow in its circle, coloured by its tone (`data-tone` for the tests); a picture only, the words say the same. */
function ChangeIcon({ words }: { words: ChangeWords }) {
  const Icon = ICON[words.icon]
  return (
    <Box component="span" data-tone={words.tone} aria-hidden sx={{ display: 'inline-flex', alignItems: 'center', justifyContent: 'center', width: 24, height: 24, borderRadius: '50%', flex: 'none', bgcolor: `${TONE[words.tone]}.soft`, color: `${TONE[words.tone]}.softText` }}>
      <Icon size={14} />
    </Box>
  )
}

/** The comparison's line: the arrow and the sentence beside it. */
export function ChangeLine({ words }: { words: ChangeWords }) {
  return (
    <Typography variant="caption" component="p" sx={{ color: 'text.secondary', display: 'flex', alignItems: 'center', gap: 1 }}>
      <ChangeIcon words={words} />
      <span>{words.sentence}</span>
    </Typography>
  )
}

/**
 * The comparison's arrow alone, before the value (#260): a button named by the sentence, which its tooltip shows on hover, focus and tap,
 * 44 px to a finger around it (padding the margin gives back, so nothing moves).
 */
export function ChangeArrow({ words }: { words: ChangeWords }) {
  return (
    <Tooltip title={words.sentence} describeChild enterTouchDelay={0} leaveTouchDelay={8000}>
      <ButtonBase aria-label={words.sentence} sx={{ flex: 'none', borderRadius: 9999, [COARSE]: { p: '10px', m: '-10px' } }}>
        <ChangeIcon words={words} />
      </ButtonBase>
    </Tooltip>
  )
}
