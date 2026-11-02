import { parseDecimal } from './i18n/format.ts'
import { calculateField, clearCalculated, type FillState } from './recognition/readValues.ts'

/** The amounts of a refuelling that follow each other: total = volume × unit price. */
export type AmountField = 'volume' | 'unitPrice' | 'totalCost'

/**
 * The order in which the amounts give way when another one moved: the unit price first (it is never saved), then the total (never one
 * the user typed or a photo read); the volume, which the consumption is worked out from, only while it is empty (see `keepAmountsInStep`).
 */
const GIVES_WAY: readonly AmountField[] = ['unitPrice', 'totalCost', 'volume']

/** What each amount is worked out from: the total is the product of the other two, the volume and the unit price are quotients. */
const SOURCES: Record<AmountField, readonly [AmountField, AmountField]> = {
  totalCost: ['volume', 'unitPrice'],
  volume: ['totalCost', 'unitPrice'],
  unitPrice: ['totalCost', 'volume'],
}

/** Decimals of a calculated amount: money to the cent, volume and unit price to the thousandth (as the server keeps them). */
const DECIMALS: Record<AmountField, number> = { totalCost: 2, volume: 3, unitPrice: 3 }

/**
 * Keeps volume, unit price and total in step once `changed` moved: one of the other amounts is worked out from the two it depends on, in a
 * fixed order. The one calculated before comes first (it says nothing of its own), then the unit price (never saved, so it is the one
 * that follows: a discount on the receipt makes it the price actually paid per litre), then the total, unless the user typed it or a
 * photo read it (the receipt's amount: it stays, and the price shown is simply the pump's; a saved total in the edit dialog still
 * follows a typed price); the volume only while it is empty. A volume that holds a value (typed, read from a photo, saved) never gives
 * way: the consumption is worked out from it, and litres worked out from an amount paid and a pump price are wrong by any discount
 * (#204). A change that is not the user's (`byUser` false: a photo, the starting values) never moves what the user typed.
 * When an amount cannot be worked out (what it depends on is empty or not a number), the next one is tried; a calculated one is emptied
 * on the way, so it never shows a value that no longer follows. What the user typed is never emptied.
 */
export function keepAmountsInStep<F extends string>(
  state: FillState<F | AmountField>,
  changed: readonly (F | AmountField)[],
  byUser: boolean,
): FillState<F | AmountField> {
  const moved = GIVES_WAY.filter((field) => changed.includes(field))
  if (moved.length === 0 || moved.length === GIVES_WAY.length) return state

  // Who may give way: a calculated amount (rank 0), the unit price (1), the total (2), an empty volume (3); a volume with a value never,
  // nor a total the user typed or a photo read. A change that is not the user's leaves what the user typed (and did not empty since) alone.
  const typed = (field: AmountField) => state.touched.has(field) && state.values[field].trim() !== ''
  const rank = (field: AmountField): number | undefined => {
    if (state.calculated.has(field)) return 0
    if (field === 'volume') return state.values.volume.trim() === '' ? 3 : undefined
    // An amount paid that the user typed or a photo read is the receipt's: it never gives way to a pump price either (#204).
    if (field === 'totalCost' && (typed(field) || state.filled.totalCost !== undefined)) return undefined
    return GIVES_WAY.indexOf(field) + 1
  }
  const candidates = GIVES_WAY.filter((field) => !moved.includes(field) && rank(field) !== undefined && (byUser || !typed(field)))
    .sort((a, b) => rank(a)! - rank(b)!)

  let next = state
  for (const field of candidates) {
    const value = workedOut(next.values, field)
    if (value !== undefined) return calculateField(next, field, value)
    next = clearCalculated(next, field)
  }
  return next
}

/** The amount worked out from the two it depends on, as text, or undefined when they are not both positive numbers. */
function workedOut(values: Record<AmountField, string>, field: AmountField): string | undefined {
  const [a, b] = SOURCES[field]
  const x = positive(values[a])
  const y = positive(values[b])
  if (x === undefined || y === undefined) return undefined
  const value = rounded(field === 'totalCost' ? x * y : x / y, DECIMALS[field])
  if (value === undefined) return undefined
  // Written the way the user writes numbers: with a decimal comma when what it came from has one.
  return values[a].includes(',') || values[b].includes(',') ? String(value).replace('.', ',') : String(value)
}

function positive(text: string): number | undefined {
  const value = parseDecimal(text)
  return value !== undefined && value > 0 ? value : undefined
}

/**
 * Rounded half up to `decimals` (through the exponent, so 1.005 becomes 1.01, not 1.00), or undefined outside the amounts a refuelling
 * can have (a tiny value would round to 0, and a huge one is written with an exponent).
 */
function rounded(value: number, decimals: number): number | undefined {
  if (!(value > 0 && value < 1e9)) return undefined
  const result = Number(`${Math.round(Number(`${value}e${decimals}`))}e-${decimals}`)
  return result > 0 ? result : undefined
}
