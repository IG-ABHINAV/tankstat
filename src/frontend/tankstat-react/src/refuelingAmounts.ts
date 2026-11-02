import { parseDecimal } from './i18n/format.ts'
import { calculateField, clearCalculated, type FillState } from './recognition/readValues.ts'

/** The amounts of a refuelling that follow each other: total = volume × unit price − discount. */
export type AmountField = 'volume' | 'unitPrice' | 'totalCost' | 'discount'

/**
 * The order in which the amounts give way when another one moved: the unit price first (it is never saved), then the total (never one
 * the user typed or a photo read), then the discount (the receipt's three amounts say what was taken off); the volume, which the
 * consumption is worked out from, only while it is empty (see `keepAmountsInStep`).
 */
const GIVES_WAY: readonly AmountField[] = ['unitPrice', 'totalCost', 'discount', 'volume']

/** Decimals of a calculated amount: money to the cent, volume and unit price to the thousandth (as the server keeps them). */
const DECIMALS: Record<AmountField, number> = { totalCost: 2, discount: 2, volume: 3, unitPrice: 3 }

/**
 * Below this share of the pump's amount (volume × unit price), a difference to the total is the receipt's rounding, not a discount: a
 * total is printed to the currency's smallest unit (cash in forints to five), a price per litre to a tenth, together under 0.08 % even
 * of a 5 L fill-up, while a card's 2 Ft/L off 640 Ft/L is 0.3 % and stays visible (the user's choice, #204). A smaller discount is typed.
 */
const ROUNDING_SHARE = 0.001

/**
 * Keeps volume, unit price, discount and total in step once `changed` moved: one of the other amounts is worked out from the ones it
 * depends on, in a fixed order. The one calculated before comes first (it says nothing of its own), then the unit price (never saved, so
 * it is the one that follows: with a discount it stays the pump's price, without one it becomes the price actually paid per litre), then
 * the total, unless the user typed it or a photo read it (the receipt's amount, like the litres: it stays; a saved total in the edit
 * dialog still follows a typed price), then the discount (the litres, the pump's price and the amount paid, all three given, say what
 * the till took off; never below nothing, and never the receipt's rounding: under `ROUNDING_SHARE` of the pump's amount nothing is
 * worked out), and the volume only while it is empty. A volume that holds a value (typed, read from a photo,
 * saved) never gives way: the consumption is worked out from it, and litres worked out from an amount paid and a pump price are wrong
 * by a discount nobody typed (#204). A change that is not the user's (`byUser` false: a photo, the starting values) never moves what
 * the user typed.
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

  // Who may give way: a calculated amount (rank 0), the unit price (1), the total (2), the discount (3), an empty volume (4); a volume
  // with a value never, nor a total the user typed or a photo read. A change that is not the user's leaves what the user typed (and did
  // not empty since) alone.
  const typed = (field: AmountField) => state.touched.has(field) && state.values[field].trim() !== ''
  const rank = (field: AmountField): number | undefined => {
    if (state.calculated.has(field)) return 0
    if (field === 'volume') return state.values.volume.trim() === '' ? 4 : undefined
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

/**
 * The amount worked out from the ones it depends on, as text, or undefined when they are not all positive numbers (or the result is
 * not): the total is volume × unit price − discount, the discount volume × unit price − total, the volume and the unit price are
 * (total + discount) ÷ the other. An empty discount is none; one that is not a number leaves the amounts alone.
 */
function workedOut(values: Record<AmountField, string>, field: AmountField): string | undefined {
  const volume = positive(values.volume)
  const unitPrice = positive(values.unitPrice)
  const total = positive(values.totalCost)
  const discount = values.discount.trim() === '' ? 0 : parseDecimal(values.discount)
  if (discount === undefined || discount < 0) return undefined
  let value: number | undefined
  switch (field) {
    case 'totalCost':
      value = volume && unitPrice ? volume * unitPrice - discount : undefined
      break
    case 'discount': {
      const pump = volume && unitPrice ? volume * unitPrice : undefined
      value = pump && total && pump - total >= pump * ROUNDING_SHARE ? pump - total : undefined
      break
    }
    case 'volume':
      value = total && unitPrice ? (total + discount) / unitPrice : undefined
      break
    case 'unitPrice':
      value = total && volume ? (total + discount) / volume : undefined
      break
  }
  const result = value === undefined ? undefined : rounded(value, DECIMALS[field])
  if (result === undefined) return undefined
  // Written the way the user writes numbers: with a decimal comma when what it came from has one.
  const comma = (Object.keys(values) as AmountField[]).some((other) => other !== field && values[other].includes(','))
  return comma ? String(result).replace('.', ',') : String(result)
}

function positive(text: string): number | undefined {
  const value = parseDecimal(text)
  return value !== undefined && value > 0 ? value : undefined
}

/**
 * Rounded half up to `decimals` (through the exponent, so 1.005 becomes 1.01, not 1.00), or undefined outside the amounts a refuelling
 * can have (a tiny value would round to 0, a discount larger than the fuel would give a total of nothing, a pump price below the price
 * paid a discount of less, and a huge one is written with an exponent).
 */
function rounded(value: number, decimals: number): number | undefined {
  if (!(value > 0 && value < 1e9)) return undefined
  const result = Number(`${Math.round(Number(`${value}e${decimals}`))}e-${decimals}`)
  return result > 0 ? result : undefined
}
