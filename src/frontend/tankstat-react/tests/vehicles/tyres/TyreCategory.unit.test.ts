import { describe, expect, it } from 'vitest'
import { foldCategory, isTyreCategory, tyreCategories } from '../../../src/tyres/tyreCategory.ts'

describe('the tyre category (#290, the owner decision 58)', () => {
  it('is Tyres or Gumik in either language, and the last tyre cost’s, whatever their case and accents', () => {
    expect(['Tyres', 'tyres', ' TYRES ', 'Gumik', 'gumík', 'GUMIK'].map((c) => isTyreCategory(c, null))).toEqual([true, true, true, true, true, true])
    expect(isTyreCategory('gumi', 'Gumi')).toBe(true)
    expect(isTyreCategory('Gúmi', 'Gumi')).toBe(true)
    expect(isTyreCategory('Gumi', null)).toBe(false)
  })

  it('never is empty, nor another category', () => {
    expect([isTyreCategory('', 'Gumi'), isTyreCategory('  ', null), isTyreCategory(null, null), isTyreCategory('Service', 'Gumi')]).toEqual([false, false, false, false])
  })

  it('names each one once for the hint', () => {
    expect(tyreCategories('Gumi')).toEqual(['Tyres', 'Gumik', 'Gumi'])
    expect(tyreCategories('tyres')).toEqual(['Tyres', 'Gumik'])
    expect(tyreCategories(undefined)).toEqual(['Tyres', 'Gumik'])
    expect(foldCategory(' Gumík ')).toBe('gumik')
  })
})
