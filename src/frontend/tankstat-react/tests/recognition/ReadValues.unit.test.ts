import { describe, expect, it } from 'vitest'
import { applyRead, calculateField, changeField, clearCalculated, mergeReadings, sameValue, takeOffered, type FillState } from '../../src/recognition/readValues.ts'

type F = 'date' | 'volume' | 'currency'
const fields = { date: 'DATE', volume: 'VOLUME', currency: 'CURRENCY' } as const
const start = (values: Record<F, string>): FillState<F> => ({ values, touched: new Set(), filled: {}, offered: {}, calculated: new Set() })
const read = (status: string, values: { name: 'DATE' | 'VOLUME' | 'CURRENCY' | 'TOTAL'; value: string; confidence: number }[]) => ({ status, values })

describe('merging readings', () => {
  it('keeps the surest value of each name, from finished readings only', () => {
    const merged = mergeReadings([
      read('READ', [{ name: 'VOLUME', value: '38.52', confidence: 0.7 }, { name: 'TOTAL', value: '24687', confidence: 0.9 }]),
      read('READ', [{ name: 'VOLUME', value: '38.5', confidence: 0.95 }]),
      read('QUEUED', [{ name: 'DATE', value: '2026-09-17', confidence: 0.99 }]),
      null,
    ])

    expect(merged).toEqual({ VOLUME: { value: '38.5', confidence: 0.95 }, TOTAL: { value: '24687', confidence: 0.9 } })
  })
})

describe('comparing typed and read values', () => {
  it('sees the same number and the same code, however they are written', () => {
    expect(sameValue('38,52', '38.52')).toBe(true)
    expect(sameValue(' huf ', 'HUF')).toBe(true)
    expect(sameValue('40', '38.52')).toBe(false)
    expect(sameValue('', '38.52')).toBe(false)
  })
})

describe('filling the form', () => {
  it('fills the fields the user has not changed, starting values included, and says which', () => {
    const { state, newly } = applyRead(start({ date: '2026-10-03', volume: '', currency: 'HUF' }), fields, {
      DATE: { value: '2026-09-17', confidence: 0.8 },
      VOLUME: { value: '38.52', confidence: 0.9 },
    })

    expect(state.values).toEqual({ date: '2026-09-17', volume: '38.52', currency: 'HUF' })
    expect(newly).toEqual(['date', 'volume'])
    expect(state.filled).toEqual({ date: '2026-09-17', volume: '38.52' })
  })

  it('never overwrites what the user typed: a different value is only offered, the same one needs nothing', () => {
    let state = changeField(start({ date: '2026-10-03', volume: '', currency: 'HUF' }), 'volume', '40')
    state = changeField(state, 'currency', 'eur')

    const result = applyRead(state, fields, { VOLUME: { value: '38.52', confidence: 0.9 }, CURRENCY: { value: 'EUR', confidence: 0.9 } })

    expect(result.state.values).toEqual({ date: '2026-10-03', volume: '40', currency: 'eur' })
    expect(result.state.offered).toEqual({ volume: '38.52' })
    expect(result.newly).toEqual([])
  })

  it('takes an offered value on "Use", and a field the user changes loses its photo mark', () => {
    const offered = applyRead(changeField(start({ date: '', volume: '', currency: '' }), 'volume', '40'), fields, { VOLUME: { value: '38.52', confidence: 0.9 } }).state

    const used = takeOffered(offered, 'volume')
    const edited = changeField(used, 'volume', '38.6')

    expect(used.values.volume).toBe('38.52')
    expect(used.filled.volume).toBe('38.52')
    expect(used.offered).toEqual({})
    expect(edited.filled).toEqual({})
  })

  it('does not report a field again when a later poll brings the same value', () => {
    const once = applyRead(start({ date: '', volume: '', currency: '' }), fields, { VOLUME: { value: '38.52', confidence: 0.9 } }).state

    expect(applyRead(once, fields, { VOLUME: { value: '38.52', confidence: 0.9 } }).newly).toEqual([])
  })
})

describe('fields worked out from others', () => {
  it('keeps the fields the user typed in, a photo value taken with "Use" counting as typed', () => {
    let state = changeField(start({ date: '', volume: '', currency: '' }), 'volume', '40')
    state = changeField(state, 'date', '2026-10-01')
    state = changeField(state, 'volume', '41')

    expect(state.touched).toEqual(new Set(['date', 'volume']))
    const offered = applyRead(state, fields, { DATE: { value: '2026-09-17', confidence: 0.9 } }).state
    expect(takeOffered(offered, 'date').touched).toEqual(new Set(['date', 'volume']))
  })

  it('a calculated value is nobody\'s: it leaves the typed fields and loses the photo mark, a matching offer goes', () => {
    const typed = changeField(start({ date: '', volume: '', currency: '' }), 'volume', '40')
    const offered = applyRead(typed, fields, { VOLUME: { value: '38.52', confidence: 0.9 } }).state

    const calculated = calculateField(offered, 'volume', '38,52')

    expect(calculated.values.volume).toBe('38,52')
    expect([...calculated.touched]).toEqual([])
    expect([...calculated.calculated]).toEqual(['volume'])
    expect(calculated.offered).toEqual({}) // the photo shows the same number
    expect(calculateField(offered, 'volume', '39').offered).toEqual({ volume: '38.52' })
  })

  it('a photo only offers its value for a calculated field, and typing or "Use" makes the field the user\'s again', () => {
    const calculated = calculateField(start({ date: '', volume: '', currency: '' }), 'volume', '39')

    const { state, newly } = applyRead(calculated, fields, { VOLUME: { value: '38.52', confidence: 0.9 } })

    expect(newly).toEqual([])
    expect(state.values.volume).toBe('39')
    expect(state.offered).toEqual({ volume: '38.52' })
    const used = takeOffered(state, 'volume')
    expect([...used.calculated]).toEqual([])
    expect(used.values.volume).toBe('38.52')
    expect([...changeField(calculated, 'volume', '40').calculated]).toEqual([])
  })

  it('empties a calculated field only', () => {
    const typed = changeField(start({ date: '', volume: '', currency: '' }), 'volume', '40')
    const calculated = calculateField(typed, 'currency', 'EUR')

    expect(clearCalculated(calculated, 'currency').values.currency).toBe('')
    expect([...clearCalculated(calculated, 'currency').calculated]).toEqual([])
    expect(clearCalculated(calculated, 'volume')).toBe(calculated)
  })
})

describe('a saved log in its edit dialog', () => {
  const photo = { DATE: { value: '2026-09-17', confidence: 0.9 }, VOLUME: { value: '38.52', confidence: 0.9 }, CURRENCY: { value: 'EUR', confidence: 0.9 } }
  const saved = (): FillState<F> => ({ ...start({ date: '2026-09-01', volume: '40', currency: '' }), kept: new Set<F>(['date', 'volume']) })

  it('keeps the saved values and only offers what the photo shows; an empty field is filled', () => {
    const { state, newly } = applyRead(saved(), fields, photo)

    expect(state.values).toEqual({ date: '2026-09-01', volume: '40', currency: 'EUR' })
    expect(state.offered).toEqual({ date: '2026-09-17', volume: '38.52' })
    expect(newly).toEqual(['currency'])
  })

  it('fills a value the user emptied, and the saved values stay kept while the user types elsewhere', () => {
    const cleared = changeField(saved(), 'volume', '')
    const typed = changeField(cleared, 'currency', 'HUF')

    const { state, newly } = applyRead(typed, fields, photo)

    expect(state.values.volume).toBe('38.52') // emptied: the photo gives it
    expect(state.values.date).toBe('2026-09-01') // still saved: only offered
    expect(state.offered.date).toBe('2026-09-17')
    expect(newly).toEqual(['volume'])
    expect([...state.touched]).toEqual(['currency'])
  })
})
