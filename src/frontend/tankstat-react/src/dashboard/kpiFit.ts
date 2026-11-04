/**
 * How a key figure's card fits what its rows offer to its height (#259), worked out step by step from what the card measures in the form it
 * shows (`Kpi`): pure, so the order is tested here, while the measuring is the browser's (jsdom lays nothing out, so there every card shows
 * its full form).
 */

/** The form a card shows. Each step of `nextFit` turns one of these on, so the steps always end. */
export interface Fit {
  /** The value one type step smaller (h3 → h4), for a value that wraps (*Spent this month* in several currencies), at every height. */
  smaller: boolean
  /** What is left out from the bottom: 0 nothing, 1 the sparkline, 2 the note too, 3 the comparison too. */
  drop: 0 | 1 | 2 | 3
  /** The label and the value on one line each, the label cut with an ellipsis when it does not fit. */
  oneLine: boolean
  /** The value's short form in the one-line form (the main currency and "+1"), when even that line is too wide. */
  short: boolean
  /** The one-line label is cut: it then takes the focus and shows itself whole in a tooltip. */
  labelCut: boolean
}

/** The form a card starts from, and starts from again whenever what it shows or its size changes. */
export const FULL: Fit = { smaller: false, drop: 0, oneLine: false, short: false, labelCut: false }

/** What a card's rows offer of what it has (#229): the sparkline at four rows, the comparison from three, the note at four, or at three without a comparison. */
export interface Offered {
  sparkline: boolean
  hint: boolean
  change: boolean
  /** The value may go one type step smaller when it wraps. */
  shrink: boolean
  /** The value has a short form for the one-line form. */
  short: boolean
}

export function offered(rows: number, has: { sparkline: boolean; hint: boolean; change: boolean; shrink: boolean; short: boolean }): Offered {
  const full = rows >= 4
  const third = rows >= 3
  return { sparkline: full && has.sparkline, hint: has.hint && (full || (third && !has.change)), change: third && has.change, shrink: has.shrink, short: has.short }
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
 * left out from the bottom what the rows offer, the sparkline, the note, the comparison (a left-out comparison leaves its row empty: the note
 * never comes into its place, #259's N2); then the one-line form; in it, the short value when the line is too wide (in the figure's own size
 * again); last, whether the one-line label is cut.
 */
export function nextFit(fit: Fit, measured: Measured, offers: Offered): Fit {
  if (offers.shrink && !fit.smaller && !fit.oneLine && measured.valueWraps) return { ...fit, smaller: true }
  if (measured.overflows) {
    const levels = [offers.sparkline, offers.hint, offers.change]
    for (let level = fit.drop; level < levels.length; level++) if (levels[level]) return { ...fit, drop: (level + 1) as Fit['drop'] }
    if (!fit.oneLine) return { ...fit, oneLine: true }
  }
  if (fit.oneLine && offers.short && !fit.short && measured.valueTooWide) return { ...fit, short: true, smaller: false }
  if (fit.oneLine && measured.labelCut !== fit.labelCut) return { ...fit, labelCut: measured.labelCut }
  return fit
}

/**
 * The form a card ends on, measuring each step with `measure`: the whole way at once, for the tests to follow a card's steps with a model of
 * its layout. `Kpi` itself takes the same steps one a render, through its layout effect, measuring the browser's card between them.
 */
export function settle(measure: (fit: Fit) => Measured, offers: Offered, start: Fit = FULL): Fit {
  let fit = start
  for (let step = 0; step < 12; step++) {
    const next = nextFit(fit, measure(fit), offers)
    if (next === fit) return fit
    fit = next
  }
  return fit
}
