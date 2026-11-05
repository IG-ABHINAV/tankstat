import type { TrendComparison, TrendFigure, TrendPeriodInput, TrendUnit, TrendWindowKind } from '../gql/generated.ts'

/**
 * A trend figure's period (#230), pure like `widgets.ts`: the four trend figures, their defaults (exactly what each showed before periods
 * existed), the quick picks, and the words of the tile's chip and of its comparison as i18n keys with their values, so the tile, the
 * popover and the Add widget dialog word a period the same way. The windows themselves are the server's (`StatsCalculator.Trend`).
 */

/** The key figures that compare two windows of a period; each loads its own figure (`vehicleTrend`). */
export const TREND_FIGURES = ['spendingTrend', 'costPerDistance', 'consumptionTrend', 'distanceTrend'] as const
export type TrendFigureId = (typeof TREND_FIGURES)[number]
export const isTrendFigure = (id: string): id is TrendFigureId => (TREND_FIGURES as readonly string[]).includes(id)

/** The server's name of each trend figure. */
export const TREND_FIGURE: Record<TrendFigureId, TrendFigure> = {
  spendingTrend: 'SPENDING',
  costPerDistance: 'COST_PER_DISTANCE',
  consumptionTrend: 'CONSUMPTION',
  distanceTrend: 'DISTANCE',
}

/** A period as the app keeps it: every field there, none of GraphQL's own (a period read back carries `__typename`). */
export interface TrendPeriod {
  kind: TrendWindowKind
  unit: TrendUnit | null
  count: number | null
  /** The first day (`yyyy-MM-dd`), for `SINCE` only. */
  since: string | null
  whole: boolean
  against: TrendComparison
}

const last = (unit: TrendUnit, count: number, whole = false): TrendPeriod => ({ kind: 'LAST', unit, count, since: null, whole, against: 'BEFORE' })
const THIS_YEAR: TrendPeriod = { kind: 'THIS_YEAR', unit: null, count: null, since: null, whole: false, against: 'YEAR_EARLIER' }

/** What a figure shows without a period of its own: the server's defaults (`TrendPeriod.TwelveWholeMonths` and the others). */
export const DEFAULT_PERIOD: Record<TrendFigureId, TrendPeriod> = {
  spendingTrend: last('MONTHS', 12, true),
  costPerDistance: last('MONTHS', 12, true),
  consumptionTrend: last('FILL_UPS', 10),
  distanceTrend: THIS_YEAR,
}

/** Each figure's quick picks: whole months for the money, fill-ups for consumption, the year so far or 12 whole months for distance. */
export const QUICK_PICKS: Record<TrendFigureId, readonly TrendPeriod[]> = {
  spendingTrend: [3, 6, 12].map((n) => last('MONTHS', n, true)),
  costPerDistance: [3, 6, 12].map((n) => last('MONTHS', n, true)),
  consumptionTrend: [5, 10, 20].map((n) => last('FILL_UPS', n)),
  distanceTrend: [THIS_YEAR, last('MONTHS', 12, true)],
}

/** A period in the app's shape, whatever it came from: the fields one by one, the left-out ones as the server reads them. */
export function canonical(p: Partial<TrendPeriodInput> & { kind: TrendWindowKind }): TrendPeriod {
  return { kind: p.kind, unit: p.unit ?? null, count: p.count ?? null, since: p.since ?? null, whole: p.whole ?? false, against: p.against ?? 'BEFORE' }
}

/** A tile's period, its figure's default when it has none of its own. */
export const periodOf = (figure: TrendFigureId, period: TrendPeriod | null | undefined): TrendPeriod => (period ? canonical(period) : DEFAULT_PERIOD[figure])

/** The period as GraphQL takes it (a fresh object: never one with `__typename`, which the server refuses in an input). */
export const toInput = (p: TrendPeriod): TrendPeriodInput => ({ kind: p.kind, unit: p.unit, count: p.count, since: p.since, whole: p.whole, against: p.against })

export const samePeriod = (a: TrendPeriod, b: TrendPeriod) =>
  a.kind === b.kind && a.unit === b.unit && a.count === b.count && a.since === b.since && a.whole === b.whole && a.against === b.against

/** The same window, whatever each is compared with: a quick pick sets the window only, the comparison is a choice of its own. */
export const sameWindow = (a: TrendPeriod, b: TrendPeriod) => samePeriod({ ...a, against: b.against }, b)

export const isDefault = (figure: TrendFigureId, p: TrendPeriod) => samePeriod(p, DEFAULT_PERIOD[figure])

/** What a tile stores: nothing for its figure's default, so a layout keeps reading the same when a default is ever changed. */
export const stored = (figure: TrendFigureId, p: TrendPeriod | null | undefined): TrendPeriod | null => (p && !isDefault(figure, canonical(p)) ? canonical(p) : null)

/** A text of `en.json` with its values. */
export interface Words {
  key: string
  values: Record<string, string | number>
}

/** The longest period, and the first day one may start from: the server's `TrendPeriod.MaxCount` and `Earliest`. */
export const MAX_COUNT = 1000
export const EARLIEST = '2000-01-01'

/**
 * What is wrong with a period being chosen, worded by the server's own keys (`TrendPeriod.Validated`), or null: a count that is not a whole
 * number from 1 to `MAX_COUNT`, a first day missing, before `EARLIEST` or after `today`. Whole weeks, months or years need no check: the
 * switch is only offered with them.
 */
export function periodProblem(p: TrendPeriod, today: string): Words | null {
  if (p.kind === 'LAST' && !(Number.isInteger(p.count) && p.count! >= 1 && p.count! <= MAX_COUNT)) return { key: 'errors.trend.countRange', values: { max: MAX_COUNT } }
  if (p.kind === 'SINCE' && !(p.since && p.since >= EARLIEST && p.since <= today)) return { key: 'errors.trend.sinceRange', values: { earliest: EARLIEST, latest: today } }
  return null
}

/** Whether a unit can be whole: weeks (Monday to Sunday), calendar months and years. */
export const canBeWhole = (unit: TrendUnit | null) => unit === 'WEEKS' || unit === 'MONTHS' || unit === 'YEARS'

/** The tab a period is set on: a quick pick's, `SINCE`'s, else `LAST`'s (this year and this month so far are only quick picks). */
export type PeriodTab = 'quick' | 'last' | 'since'
export function tabOf(figure: TrendFigureId, p: TrendPeriod): PeriodTab {
  if (p.kind === 'THIS_YEAR' || p.kind === 'THIS_MONTH' || QUICK_PICKS[figure].some((q) => sameWindow(q, p))) return 'quick'
  return p.kind === 'SINCE' ? 'since' : 'last'
}

/**
 * The period a tab starts from when it is chosen: the period itself when it is already of that tab, else the tab's first values (the last
 * 8 weeks, or 10 fill-ups for consumption; since the first of the month 3 months ago; the figure's default for the quick picks), keeping
 * what it is compared with.
 */
export function startOf(tab: PeriodTab, figure: TrendFigureId, p: TrendPeriod, today: string): TrendPeriod {
  if (tabOf(figure, p) === tab) return p
  if (tab === 'quick') return { ...DEFAULT_PERIOD[figure], against: p.against }
  if (tab === 'last') return figure === 'consumptionTrend' ? { ...last('FILL_UPS', 10), against: p.against } : { ...last('WEEKS', 8), against: p.against }
  const [y, m] = [Number(today.slice(0, 4)), Number(today.slice(5, 7))]
  const back = new Date(Date.UTC(y, m - 1 - 3, 1)).toISOString().slice(0, 10)
  return { kind: 'SINCE', unit: null, count: null, since: back, whole: false, against: p.against }
}

const UNIT_KEY: Record<TrendUnit, string> = { DAYS: 'days', WEEKS: 'weeks', MONTHS: 'months', YEARS: 'years', FILL_UPS: 'fillUps' }

/**
 * The chip's words: "12 full months", "Last 8 weeks", "Since 15 Mar 2026", "This year", "This month"; with the comparison when it is not
 * the figure's default ("… · vs a year earlier"). No comparison (`NONE`, #272) says nothing here: a key figure without one has no arrow,
 * and how its chip says so is the builder's (#275). `date` writes the `SINCE` day in the user's format.
 */
export function periodWords(figure: TrendFigureId, p: TrendPeriod, date: (day: string) => string): { period: Words; against: Words | null } {
  const period: Words =
    p.kind === 'THIS_YEAR' ? { key: 'dashboard.period.thisYear', values: {} }
    : p.kind === 'THIS_MONTH' ? { key: 'dashboard.period.thisMonth', values: {} }
    : p.kind === 'SINCE' ? { key: 'dashboard.period.since', values: { date: p.since ? date(p.since) : '' } }
    : { key: `dashboard.period.${p.whole ? 'whole' : 'last'}.${UNIT_KEY[p.unit ?? 'DAYS']}`, values: { count: p.count ?? 0 } }
  const against = p.against === DEFAULT_PERIOD[figure].against || p.against === 'NONE' ? null : { key: `dashboard.period.vs.${p.against === 'YEAR_EARLIER' ? 'yearEarlier' : 'before'}`, values: {} }
  return { period, against }
}

/** The days a window spans, both ends counted. */
export function daysIn(window: { start: string; end: string }): number {
  const day = (iso: string) => Date.UTC(Number(iso.slice(0, 4)), Number(iso.slice(5, 7)) - 1, Number(iso.slice(8, 10)))
  return Math.round((day(window.end) - day(window.start)) / 86_400_000) + 1
}

/**
 * What the recent window is compared with, for the sentence under the value ("… more than <this>"): the defaults keep their words ("the
 * 12 months before", "the 10 fill-ups before", "last year to this day"). The N before name the period's own count (for fill-ups those
 * found before, which can be fewer); this month so far is against "the month before" (the whole of it, #272); a window by dates (since a
 * day, this year against the days before) counts the earlier window's days. No comparison (`NONE`) is compared with nothing: null.
 */
export function comparedWith(p: TrendPeriod, earlier: { start: string; end: string; fillUps: number }): Words | null {
  if (p.against === 'NONE') return null
  if (p.kind === 'THIS_MONTH') return p.against === 'YEAR_EARLIER' ? { key: 'dashboard.trend.than.sameDaysYearEarlier', values: {} } : { key: 'dashboard.trend.than.before.months', values: { count: 1 } }
  if (p.kind === 'LAST' && p.unit) {
    const unit = UNIT_KEY[p.unit]
    if (p.against === 'YEAR_EARLIER') return p.unit === 'FILL_UPS' ? { key: 'dashboard.trend.than.sameDaysYearEarlier', values: {} } : { key: `dashboard.trend.than.yearEarlier.${unit}`, values: { count: p.count ?? 0 } }
    return { key: `dashboard.trend.than.before.${unit}`, values: { count: p.unit === 'FILL_UPS' ? earlier.fillUps : (p.count ?? 0) } }
  }
  if (p.against === 'YEAR_EARLIER') return { key: p.kind === 'THIS_YEAR' ? 'dashboard.trend.than.lastYearToDate' : 'dashboard.trend.than.sameDaysYearEarlier', values: {} }
  return { key: 'dashboard.trend.than.before.days', values: { count: daysIn(earlier) } }
}

/** Whether the consumption sentence names the fill-ups compared (a window by dates holds any number of them; fill-ups name their own). */
export const namesFillUps = (p: TrendPeriod) => !(p.kind === 'LAST' && p.unit === 'FILL_UPS')
