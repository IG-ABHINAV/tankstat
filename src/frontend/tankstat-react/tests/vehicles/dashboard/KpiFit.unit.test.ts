import { describe, expect, it } from 'vitest'
import { FULL, type Fit, type Measured, nextFit, offered, settle } from '../../../src/dashboard/kpiFit.ts'

/**
 * A key figure's card as the browser lays it out, from the heights measured on the published build (#259's triage, a one-column desktop
 * tile is 207 px of content, a phone's 356): the room inside the card by its rows, a label line 20 px, a value line 30 px (26 one step
 * smaller), the note 16, the comparison 16 a line, the sparkline 34, the value's margin 8 at four rows.
 */
interface Card {
  rows: number
  /** Lines the label takes when it may wrap. */
  label: number
  /** Lines the value takes in its own size, and one type step smaller. */
  value: number
  smaller?: number
  note?: boolean
  /** Lines the comparison takes. */
  change?: number
  sparkline?: boolean
  /** The one-line value is wider than its line, in its own size and smaller (the short form always fits). */
  tooWide?: { own: boolean; smaller: boolean }
  shrink?: boolean
  short?: boolean
}
const ROOM: Record<number, number> = { 2: 84 - 24, 3: 132 - 32, 4: 180 - 32 }

const offersOf = (c: Card) => offered(c.rows, { sparkline: !!c.sparkline, hint: !!c.note, change: !!c.change, shrink: !!c.shrink, short: !!c.short })

function measureOf(c: Card) {
  const offers = offersOf(c)
  return (fit: Fit): Measured => {
    const valueLines = fit.oneLine ? 1 : fit.smaller ? (c.smaller ?? c.value) : c.value
    const height =
      (fit.oneLine ? 1 : c.label) * 20 +
      valueLines * (fit.smaller ? 26 : 30) +
      (c.rows >= 4 ? 8 : 0) +
      (offers.hint && fit.drop < 2 ? 16 : 0) +
      (offers.change && fit.drop < 3 ? (c.change ?? 0) * 16 : 0) +
      (offers.sparkline && fit.drop < 1 ? 34 : 0)
    return {
      overflows: height > ROOM[c.rows],
      valueWraps: !fit.oneLine && valueLines > 1,
      valueTooWide: fit.oneLine && !fit.short && !!c.tooWide && (fit.smaller ? c.tooWide.smaller : c.tooWide.own),
      labelCut: fit.oneLine && c.label > 1,
    }
  }
}
const fitOf = (c: Card) => settle(measureOf(c), offersOf(c))
const form = (change: Partial<Fit>): Fit => ({ ...FULL, ...change })

describe('a key figure fitted to its card (#259)', () => {
  it('shows a figure that fits as it is, as every one does on a phone', () => {
    expect(fitOf({ rows: 2, label: 1, value: 1, shrink: true, short: true, note: true, sparkline: true })).toEqual(FULL) // Spent this month in two currencies on a phone
    expect(fitOf({ rows: 2, label: 1, value: 1, note: true, change: 2 })).toEqual(FULL) // Consumption change in Hungarian on a phone
    expect(fitOf({ rows: 4, label: 1, value: 1, note: true, change: 2 })).toEqual(FULL) // and at four rows on a desktop, in English
  })

  it('writes a wrapped value one step smaller at every height, and at two rows puts the main currency and "+1" when even that is too wide', () => {
    const spent = { label: 1, value: 2, smaller: 1, shrink: true, short: true, note: true, sparkline: true } // two currencies, a desktop column
    expect(fitOf({ ...spent, rows: 4 })).toEqual(form({ smaller: true }))
    expect(fitOf({ ...spent, rows: 3 })).toEqual(form({ smaller: true }))
    expect(fitOf({ ...spent, rows: 2 })).toEqual(form({ smaller: true })) // smaller, it fits its line
    // Too wide even smaller: the one-line form with the short value, in the figure's own size again.
    expect(fitOf({ ...spent, rows: 2, smaller: 2, tooWide: { own: true, smaller: true } })).toEqual(form({ oneLine: true, short: true }))
    // At three rows a value too wide even smaller may wrap again: there is room for it.
    expect(fitOf({ ...spent, rows: 3, smaller: 2 })).toEqual(form({ smaller: true }))
    // A value with no short form (a single amount too long) stays on its line, cut at the card's edge only as the last resort.
    expect(fitOf({ ...spent, rows: 2, smaller: 2, short: false, tooWide: { own: true, smaller: true } })).toEqual(form({ smaller: true, oneLine: true }))
  })

  it('puts a label that wraps beside its chip on one line at two rows, and says it is cut', () => {
    expect(fitOf({ rows: 2, label: 2, value: 1, note: true, change: 2 })).toEqual(form({ oneLine: true, labelCut: true })) // Consumption change in English, a desktop column
    expect(fitOf({ rows: 2, label: 3, value: 1, change: 3 })).toEqual(form({ oneLine: true, labelCut: true })) // Cost per distance change in Hungarian
  })

  it('leaves out at three rows a comparison that has no room, and its row stays empty: the note never takes its place', () => {
    // Cost per distance change in Hungarian (label 60, value 30, comparison 48 of 100 px) and Consumption change in Hungarian (40, 30, 48).
    expect(fitOf({ rows: 3, label: 3, value: 1, change: 3 })).toEqual(form({ drop: 3 }))
    expect(fitOf({ rows: 3, label: 2, value: 1, note: true, change: 3 })).toEqual(form({ drop: 3 }))
    // Cost per distance change in English: label 40, value 30 (its unit small, one line), comparison 32: 102 of 100 px.
    expect(fitOf({ rows: 3, label: 2, value: 1, change: 2 })).toEqual(form({ drop: 3 }))
  })

  it('leaves out from the bottom one at a time, the sparkline, the note, then the comparison, only what the rows offer', () => {
    const tall: Card = { rows: 4, label: 3, value: 2, note: true, change: 3, sparkline: true }
    const measure = measureOf(tall)
    const steps: Fit['drop'][] = []
    let fit = FULL
    for (let next = nextFit(fit, measure(fit), offersOf(tall)); next !== fit; next = nextFit(fit, measure(fit), offersOf(tall))) {
      fit = next
      steps.push(fit.drop)
    }
    expect(steps).toEqual([1, 2, 3])
    expect(fit).toEqual(form({ drop: 3 }))
    // At three rows a figure with a comparison offers no note: the comparison is the first and only thing left out.
    expect(nextFit(FULL, { overflows: true, valueWraps: false, valueTooWide: false, labelCut: false }, offered(3, { sparkline: true, hint: true, change: true, shrink: false, short: false }))).toEqual(form({ drop: 3 }))
  })

  it('ends: the form to show is handed back as it is', () => {
    const done = form({ smaller: true })
    expect(nextFit(done, { overflows: false, valueWraps: true, valueTooWide: false, labelCut: false }, offered(3, { sparkline: false, hint: false, change: false, shrink: true, short: false }))).toBe(done)
  })

  it('offers what the rows hold (#229): the comparison from three rows, the note at four or without a comparison, the sparkline at four', () => {
    const has = { sparkline: true, hint: true, change: true, shrink: false, short: false }
    expect(offered(2, has)).toMatchObject({ sparkline: false, hint: false, change: false })
    expect(offered(3, has)).toMatchObject({ sparkline: false, hint: false, change: true })
    expect(offered(3, { ...has, change: false })).toMatchObject({ hint: true })
    expect(offered(4, has)).toMatchObject({ sparkline: true, hint: true, change: true })
  })
})
