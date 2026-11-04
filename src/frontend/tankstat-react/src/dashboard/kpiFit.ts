/**
 * How a key figure's card fits what its rows offer to its height (#259), worked out step by step from what the card measures in the form it
 * shows (`Kpi`): pure, so the order is tested here, while the measuring is the browser's (jsdom lays nothing out, so there every card shows
 * its full form).
 */

/**
 * The form a card shows. The steps of `nextFit` always end: each drops one more level, turns the one-line form or the short value on (once
 * each), or takes the value one size step down (at most `LAST_STEP` before the short value, which starts from its own size, and as many after
 * it), and last sets whether the label is cut: at most 15 in all.
 */
export interface Fit {
  /**
   * The value's size, a step of `VALUE_STEPS` (#260): one step smaller for a value that wraps where the figure allows it (*Spent this
   * month* in several currencies, at every height), and in the one-line form one step at a time while its line is too wide, down to 14 px.
   */
  step: number
  /** What is left out from the bottom: 0 nothing, 1 the sparkline, 2 the note too, 3 the comparison's line too, which then is its arrow alone (#260). */
  drop: 0 | 1 | 2 | 3
  /** The label and the value on one line each, the label cut with an ellipsis when it does not fit. */
  oneLine: boolean
  /** The value's short form in the one-line form (the main currency and "+1"), when even that line is too wide. */
  short: boolean
  /** The one-line label is cut: it then takes the focus and shows itself whole in a tooltip. */
  labelCut: boolean
}

/** The form a card starts from, and starts from again whenever what it shows or its size changes. */
export const FULL: Fit = { step: 0, drop: 0, oneLine: false, short: false, labelCut: false }

/**
 * The value's sizes, step by step (#260): the headings h3 to h6 (24, 20, 18, 16 px, `theme/typography.ts`), then h6 at body text's size
 * (14 px, in rem like the rest, so it follows the browser's text size), past which a value too wide is cut at the card's edge.
 */
export const VALUE_STEPS = [
  { variant: 'h3' },
  { variant: 'h4' },
  { variant: 'h5' },
  { variant: 'h6' },
  { variant: 'h6', size: { fontSize: '0.875rem', lineHeight: '1.25rem' } },
] as const
const LAST_STEP = VALUE_STEPS.length - 1

/**
 * What a card's rows offer of what it has (#229): the sparkline at four rows, the note at four, or at three without a comparison; a trend
 * figure's comparison as its line from three rows, and as its arrow alone before the value at two (#260), where the sentence has no row.
 */
export interface Offered {
  sparkline: boolean
  hint: boolean
  change: 'line' | 'arrow' | false
  /** The value may go one type step smaller when it wraps. */
  shrink: boolean
  /** The value has a short form for the one-line form. */
  short: boolean
}

export function offered(rows: number, has: { sparkline: boolean; hint: boolean; change: boolean; shrink: boolean; short: boolean }): Offered {
  const full = rows >= 4
  const third = rows >= 3
  return { sparkline: full && has.sparkline, hint: has.hint && (full || (third && !has.change)), change: has.change ? (third ? 'line' : 'arrow') : false, shrink: has.shrink, short: has.short }
}

/** What the card measured in the form it shows. */
export interface Measured {
  /** Its content is taller than the card. */
  overflows: boolean
  /** The value takes more than one line. */
  valueWraps: boolean
  /** The value is wider than its line (only in the one-line form, where it does not wrap). */
  valueTooWide: boolean
  /** The label is wider than its line (only in the one-line form). */
  labelCut: boolean
}

/**
 * The next form to try after measuring `fit`, or `fit` itself when it is the one to show. In order: the value one step smaller when it wraps
 * and may (whether the card overflows or not: a wrapped value is always written smaller); then, while the content is taller than the card,
 * left out from the bottom what the rows offer, the sparkline, the note, the comparison's line (which becomes its arrow alone before the value,
 * #260; its row stays empty: the note never comes into its place, #259's N2); then the one-line form; in it, while the value is wider than
 * its line, one size step down at a time to 14 px (every figure, #260), then, still too wide, the short value in the figure's own size
 * again (the main currency and "+N", #259) and the steps once more; last, whether the one-line label is cut.
 */
export function nextFit(fit: Fit, measured: Measured, offers: Offered): Fit {
  if (offers.shrink && fit.step === 0 && !fit.oneLine && measured.valueWraps) return { ...fit, step: 1 }
  if (measured.overflows) {
    const levels = [offers.sparkline, offers.hint, offers.change === 'line']
    for (let level = fit.drop; level < levels.length; level++) if (levels[level]) return { ...fit, drop: (level + 1) as Fit['drop'] }
    if (!fit.oneLine) return { ...fit, oneLine: true }
  }
  if (fit.oneLine && measured.valueTooWide) {
    if (fit.step < LAST_STEP) return { ...fit, step: fit.step + 1 }
    if (offers.short && !fit.short) return { ...fit, short: true, step: 0 }
  }
  if (fit.oneLine && measured.labelCut !== fit.labelCut) return { ...fit, labelCut: measured.labelCut }
  return fit
}

/**
 * The form a card ends on, measuring each step with `measure`: the whole way at once, for the tests to follow a card's steps with a model of
 * its layout. `Kpi` itself takes the same steps one a render, through its layout effect, measuring the browser's card between them.
 */
export function settle(measure: (fit: Fit) => Measured, offers: Offered, start: Fit = FULL): Fit {
  let fit = start
  for (let step = 0; step < 24; step++) {
    const next = nextFit(fit, measure(fit), offers)
    if (next === fit) return fit
    fit = next
  }
  return fit
}
