import { describe, expect, it } from 'vitest'
import { applyRead, changeField, takeOffered, type FillState, type ReadValues } from '../../../src/recognition/readValues.ts'
import { keepAmountsInStep, type AmountField } from '../../../src/refuelingAmounts.ts'

type F = AmountField | 'currency'
const fields = { volume: 'VOLUME', unitPrice: 'UNIT_PRICE', totalCost: 'TOTAL', currency: 'CURRENCY' } as const

// The steps of the dialog (useReadFill), on the pure functions.

/** The dialog opens with these values (the others empty): an edit dialog has volume and total. */
function open(values: Partial<Record<F, string>> = {}): FillState<F> {
  const all = { volume: '', unitPrice: '', discount: '', totalCost: '', currency: 'HUF', ...values }
  const start: FillState<F> = { values: all, touched: new Set(), filled: {}, offered: {}, calculated: new Set() }
  return keepAmountsInStep(start, (Object.keys(all) as F[]).filter((f) => all[f] !== ''), false)
}

/** The user types a whole value into a field. */
const type = (state: FillState<F>, field: F, value: string) => keepAmountsInStep(changeField(state, field, value), [field], true)

/** The user types a value key by key, as in the dialog: every keystroke is a change. */
const typeKeys = (state: FillState<F>, field: F, value: string) =>
  [...value].reduce((s, _, i) => type(s, field, value.slice(0, i + 1)), changeField(state, field, ''))

/** A photo is read. */
function photo(state: FillState<F>, read: Partial<Record<'VOLUME' | 'UNIT_PRICE' | 'TOTAL', string>>) {
  const values: ReadValues = Object.fromEntries(Object.entries(read).map(([name, value]) => [name, { value, confidence: 0.9 }]))
  const { state: next, newly } = applyRead(state, fields, values)
  return keepAmountsInStep(next, newly, false)
}

/** "Use it" under a field. */
const use = (state: FillState<F>, field: F) => keepAmountsInStep(takeOffered(state, field), [field], true)

const amounts = ({ values }: FillState<F>) => ({ volume: values.volume, unitPrice: values.unitPrice, totalCost: values.totalCost })

describe('volume, unit price and total', () => {
  it('works out the third from any two', () => {
    expect(amounts(type(type(open(), 'volume', '40'), 'unitPrice', '600'))).toEqual({ volume: '40', unitPrice: '600', totalCost: '24000' })
    expect(amounts(type(type(open(), 'volume', '40'), 'totalCost', '24000'))).toEqual({ volume: '40', unitPrice: '600', totalCost: '24000' })
    expect(amounts(type(type(open(), 'totalCost', '24000'), 'unitPrice', '600'))).toEqual({ volume: '40', unitPrice: '600', totalCost: '24000' })
  })

  it('with all three filled the unit price follows a change of the volume or the total; a total typed never gives way to the pump price, nor do the litres: the discount does', () => {
    let state = type(type(open(), 'volume', '40'), 'unitPrice', '600') // the total is worked out
    expect(state.calculated).toEqual(new Set(['totalCost']))

    state = type(state, 'totalCost', '24600') // the amount paid: a discount, so the price per litre paid is lower than the pump's
    expect(amounts(state)).toEqual({ volume: '40', unitPrice: '615', totalCost: '24600' })
    expect(state.calculated).toEqual(new Set(['unitPrice']))

    state = type(state, 'unitPrice', '620') // the pump price typed on purpose: the amount paid is the receipt's and stays, as do the litres; the discount is worked out
    expect(amounts(state)).toEqual({ volume: '40', unitPrice: '620', totalCost: '24600' })
    expect(state.values.discount).toBe('200')
    expect(state.calculated).toEqual(new Set(['discount']))

    state = type(state, 'volume', '41') // the litres corrected with the pump price and the amount paid both typed: the worked-out discount follows
    expect(amounts(state)).toEqual({ volume: '41', unitPrice: '620', totalCost: '24600' })
    expect(state.values.discount).toBe('820')

    state = type(state, 'discount', '') // the discount taken back: the unit price gives way, the amount paid stays
    expect(amounts(state)).toEqual({ volume: '41', unitPrice: '600', totalCost: '24600' })
    expect(state.calculated).toEqual(new Set(['unitPrice']))

    state = type(state, 'totalCost', '24700') // the amount paid typed: the unit price follows
    expect(amounts(state)).toEqual({ volume: '41', unitPrice: '602.439', totalCost: '24700' })

    state = type(state, 'volume', '42') // the litres corrected with the total and the price both typed: the unit price gives way, never the litres or the total
    expect(amounts(state)).toEqual({ volume: '42', unitPrice: '588.095', totalCost: '24700' })
  })

  it('the receipt of #204: 43 L, 818 Ft/L and 34 744 Ft paid keep the litres and give the price per litre paid', () => {
    const state = type(type(type(open(), 'volume', '43'), 'unitPrice', '818'), 'totalCost', '34744')

    expect(amounts(state)).toEqual({ volume: '43', unitPrice: '808', totalCost: '34744' })
  })

  it('a discount comes off the total: typed after the litres and the pump price, the total follows; typed after the amount paid, the pump price comes back', () => {
    let state = type(type(type(open(), 'volume', '43'), 'unitPrice', '818'), 'discount', '430')
    expect(amounts(state)).toEqual({ volume: '43', unitPrice: '818', totalCost: '34744' })
    expect(state.calculated).toEqual(new Set(['totalCost']))

    state = type(type(type(open(), 'volume', '43'), 'totalCost', '34744'), 'discount', '430') // the price paid, 808, is worked out again with the discount
    expect(amounts(state)).toEqual({ volume: '43', unitPrice: '818', totalCost: '34744' })
    expect(state.calculated).toEqual(new Set(['unitPrice']))

    state = type(state, 'discount', '') // taken back: none
    expect(amounts(state)).toEqual({ volume: '43', unitPrice: '808', totalCost: '34744' })
  })

  it('with the pump price and the amount paid both typed, a discount moves the unit price, never the litres or the total', () => {
    const state = type(type(type(type(open(), 'volume', '43'), 'unitPrice', '800'), 'totalCost', '34744'), 'discount', '430')

    expect(amounts(state)).toEqual({ volume: '43', unitPrice: '818', totalCost: '34744' })
  })

  it('the litres, the amount paid and the pump price give the discount: worked out once the three are there, gone when it would be below nothing', () => {
    let state = type(type(type(open(), 'volume', '43'), 'totalCost', '34744'), 'unitPrice', '818') // the receipt's three numbers (#204)
    expect(amounts(state)).toEqual({ volume: '43', unitPrice: '818', totalCost: '34744' })
    expect(state.values.discount).toBe('430')
    expect(state.calculated).toEqual(new Set(['discount']))

    state = type(state, 'unitPrice', '810') // the pump price corrected: the discount follows
    expect(state.values.discount).toBe('86')

    state = type(state, 'unitPrice', '800') // below the price paid per litre: no discount, and the amount paid stays the receipt's
    expect(amounts(state)).toEqual({ volume: '43', unitPrice: '800', totalCost: '34744' })
    expect(state.values.discount).toBe('')
    expect(state.calculated).toEqual(new Set())

    // A photo's amounts never give it: a misread one (6409 for 640.9) would become a discount that is saved. A photo reads the discount itself.
    const read = photo(open(), { VOLUME: '43', UNIT_PRICE: '818', TOTAL: '34744' })
    expect(read.values.discount).toBe('')
    const misread = photo(type(open(), 'volume', '43'), { UNIT_PRICE: '6409', TOTAL: '34744' })
    expect({ ...amounts(misread), discount: misread.values.discount }).toEqual({ volume: '43', unitPrice: '6409', totalCost: '34744', discount: '' })

    // The receipt's rounding is no discount: 38.52 L × 640.9 is 24 687.47, printed as 24 687. A card's 2 Ft/L off is one.
    const rounding = type(type(type(open(), 'volume', '38.52'), 'totalCost', '24687'), 'unitPrice', '640.9')
    expect(amounts(rounding)).toEqual({ volume: '38.52', unitPrice: '640.9', totalCost: '24687' })
    expect(rounding.values.discount).toBe('')
    expect(type(type(type(open(), 'volume', '40'), 'totalCost', '25556'), 'unitPrice', '640.9').values.discount).toBe('80')
  })

  it('a discount that is not a number, or more than the fuel, empties a worked-out total rather than show one that no longer follows', () => {
    expect(amounts(type(type(type(open(), 'volume', '43'), 'unitPrice', '818'), 'discount', 'x'))).toEqual({ volume: '43', unitPrice: '818', totalCost: '' })
    expect(amounts(type(type(type(open(), 'volume', '43'), 'unitPrice', '818'), 'discount', '40000'))).toEqual({ volume: '43', unitPrice: '818', totalCost: '' })
    expect(type(type(type(open(), 'volume', '38,2'), 'unitPrice', '599,9'), 'discount', '416,18').values.totalCost).toBe('22500')
  })

  it('a volume read from a photo never gives way to a typed total either', () => {
    let state = type(photo(open(), { VOLUME: '38.52' }), 'unitPrice', '640') // the total is worked out
    expect(amounts(state)).toEqual({ volume: '38.52', unitPrice: '640', totalCost: '24652.8' })

    state = type(state, 'totalCost', '24500')
    expect(amounts(state)).toEqual({ volume: '38.52', unitPrice: '636.033', totalCost: '24500' })
  })

  it('works out the unit price of a saved log on opening it, and among untyped amounts changes the unit price first, then the total', () => {
    const editing = open({ volume: '41.5', totalCost: '22000' })
    expect(amounts(editing)).toEqual({ volume: '41.5', unitPrice: '530.12', totalCost: '22000' })

    expect(amounts(type(editing, 'volume', '44'))).toEqual({ volume: '44', unitPrice: '500', totalCost: '22000' })
    expect(amounts(type(editing, 'unitPrice', '500'))).toEqual({ volume: '41.5', unitPrice: '500', totalCost: '20750' })
    expect(amounts(open({ totalCost: '22000' }))).toEqual({ volume: '', unitPrice: '', totalCost: '22000' }) // its volume is still being read
  })

  it('empties a calculated amount once what it came from is gone, never a typed one', () => {
    let state = type(type(open(), 'volume', '40'), 'unitPrice', '600')

    state = type(state, 'volume', '')
    expect(amounts(state)).toEqual({ volume: '', unitPrice: '600', totalCost: '' })
    expect(state.calculated).toEqual(new Set())

    state = type(state, 'volume', '4')
    expect(amounts(state)).toEqual({ volume: '4', unitPrice: '600', totalCost: '2400' })
    expect(amounts(type(state, 'volume', '4.'))).toEqual({ volume: '4.', unitPrice: '600', totalCost: '' }) // half typed

    expect(amounts(type(state, 'unitPrice', ''))).toEqual({ volume: '4', unitPrice: '', totalCost: '' })
  })

  it('leaves the amounts alone while what they come from is not a positive number', () => {
    expect(amounts(type(type(open(), 'volume', '0'), 'unitPrice', '600'))).toEqual({ volume: '0', unitPrice: '600', totalCost: '' })
    expect(amounts(type(type(open(), 'volume', 'x'), 'unitPrice', '600'))).toEqual({ volume: 'x', unitPrice: '600', totalCost: '' })
    expect(amounts(type(type(open(), 'volume', '0.001'), 'unitPrice', '0.001'))).toEqual({ volume: '0.001', unitPrice: '0.001', totalCost: '' }) // rounds to 0
  })

  it('rounds money to the cent and the rest to the thousandth, written the way the user writes numbers', () => {
    expect(type(type(open(), 'volume', '38,2'), 'unitPrice', '599,9').values.totalCost).toBe('22916,18')
    expect(type(type(open(), 'totalCost', '19100'), 'unitPrice', '499.9').values.volume).toBe('38.208')
    expect(type(type(open(), 'volume', '1'), 'unitPrice', '1.005').values.totalCost).toBe('1.01') // half up, despite 1.005 being 1.00499… in binary
    expect(type(type(open(), 'volume', '3'), 'totalCost', '10').values.unitPrice).toBe('3.333')
  })

  it('never changes the other fields', () => {
    expect(type(type(open(), 'volume', '40'), 'unitPrice', '600').values.currency).toBe('HUF')
  })
})

describe('amounts read from photos', () => {
  it('works out the amount the photos did not show', () => {
    expect(amounts(photo(open(), { VOLUME: '38.52', TOTAL: '24687' }))).toEqual({ volume: '38.52', unitPrice: '640.888', totalCost: '24687' })
    expect(amounts(photo(type(open(), 'unitPrice', '640'), { VOLUME: '38.5' }))).toEqual({ volume: '38.5', unitPrice: '640', totalCost: '24640' })
  })

  it('keeps all three as read when a photo shows all three', () => {
    const state = photo(open(), { VOLUME: '38.52', UNIT_PRICE: '640.9', TOTAL: '24687' })

    expect(amounts(state)).toEqual({ volume: '38.52', unitPrice: '640.9', totalCost: '24687' })
    expect(state.calculated).toEqual(new Set())
  })

  it('never moves what the user typed, even when the amounts no longer add up', () => {
    const typed = changeField(changeField(open(), 'unitPrice', '600'), 'totalCost', '25000')

    expect(amounts(photo(typed, { VOLUME: '40' }))).toEqual({ volume: '40', unitPrice: '600', totalCost: '25000' })
  })

  it('offers a different value next to a calculated amount, and "Use it" takes it: the typed total and the litres stay', () => {
    const state = photo(type(type(open(), 'volume', '40'), 'totalCost', '24000'), { UNIT_PRICE: '599.9' })

    expect(amounts(state)).toEqual({ volume: '40', unitPrice: '600', totalCost: '24000' })
    expect(state.offered).toEqual({ unitPrice: '599.9' })
    expect(amounts(use(state, 'unitPrice'))).toEqual({ volume: '40', unitPrice: '599.9', totalCost: '24000' })
  })

  it('a total typed or read from a photo never gives way to the pump price: the litres and the amount paid are the receipt\'s', () => {
    // Typed: 43 L and 34 744 Ft paid (808 per litre), then the pump's 818: the total stays (#204).
    const typed = type(type(type(open(), 'volume', '43'), 'totalCost', '34744'), 'unitPrice', '818')
    expect(amounts(typed)).toEqual({ volume: '43', unitPrice: '818', totalCost: '34744' })
    expect(typed.values.discount).toBe('430') // the three say what the till took off (the test below)

    // Read from a photo: the fuel's own amount, then the litres typed and the pump's price.
    const read = type(type(photo(open(), { TOTAL: '34744' }), 'volume', '43'), 'unitPrice', '818')
    expect(amounts(read)).toEqual({ volume: '43', unitPrice: '818', totalCost: '34744' })

    // Emptied by the user, the total is worked out again from the next change.
    const emptied = type(type(typed, 'totalCost', ''), 'unitPrice', '820')
    expect(amounts(emptied)).toEqual({ volume: '43', unitPrice: '820', totalCost: '35260' })
    expect(emptied.calculated).toEqual(new Set(['totalCost']))
  })

  it('works out again what it calculated before an amount a photo showed, keystroke after keystroke', () => {
    // The total calculated after the first key must follow the next ones, not the unit price the photo showed.
    expect(amounts(typeKeys(photo(open(), { UNIT_PRICE: '600' }), 'volume', '40'))).toEqual({ volume: '40', unitPrice: '600', totalCost: '24000' })
    expect(amounts(typeKeys(photo(open(), { TOTAL: '24687' }), 'unitPrice', '640'))).toEqual({ volume: '38.573', unitPrice: '640', totalCost: '24687' })
    expect(amounts(typeKeys(photo(open(), { UNIT_PRICE: '600' }), 'totalCost', '24000'))).toEqual({ volume: '40', unitPrice: '600', totalCost: '24000' })
  })

  it('keeps one calculated amount at a time', () => {
    const state = typeKeys(photo(open(), { UNIT_PRICE: '600' }), 'volume', '40')

    expect(state.calculated).toEqual(new Set(['totalCost']))
  })
})

