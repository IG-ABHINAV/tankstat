import { describe, expect, it } from 'vitest'
import contract from '../../../../../../tests/backend/contracts/tyre-swaps/tyre-swaps.json'
import { changePositions, checked, swap, TyreRuleError, type SetState, type StretchState } from '../../../src/tyres/tyreHistory.ts'

interface Case {
  name: string
  op: 'swap' | 'positions' | 'check'
  sets: SetState[]
  stretches: { id: string; set: string; from: string; until: string | null; pairOneFront: boolean | null }[]
  swap?: { set: string; day: string; pairOneFront: boolean | null; replace: boolean; pairsGiven: boolean }
  positions?: { set: string; day: string; pairOneFront: boolean | null }
  moving?: string[]
  expect?: Case['stretches']
  error?: { key: string; args: Record<string, string | null> }
}

const asState = (s: Case['stretches'][number]): StretchState => ({ id: s.id, setId: s.set, from: s.from, until: s.until, pairOneFront: s.pairOneFront })

// The same cases the server's TyreSwapContractTests read (#287): a tyre change made offline shows as the server will make it.
describe('the tyre history follows the contract the server keeps', () => {
  it.each((contract.cases as Case[]).map((c) => [c.name, c] as const))('%s', (_, c) => {
    const stretches = c.stretches.map(asState)
    const run = () => {
      if (c.op === 'swap') return swap(c.sets, stretches, c.swap!.set, c.swap!.day, c.swap!.pairOneFront, { id: 'new', replace: c.swap!.replace, pairsGiven: c.swap!.pairsGiven })
      if (c.op === 'positions') return changePositions(c.sets, stretches, c.positions!.set, c.positions!.day, c.positions!.pairOneFront, 'new')
      return checked(c.sets, stretches, c.moving ? new Set(c.moving) : undefined)
    }
    if (c.error) {
      let thrown: unknown
      try {
        run()
      } catch (error) {
        thrown = error
      }
      expect(thrown).toBeInstanceOf(TyreRuleError)
      expect({ key: (thrown as TyreRuleError).key, args: (thrown as TyreRuleError).args }).toEqual(c.error)
      return
    }
    const after = [...run()].sort((a, b) => (a.from < b.from ? -1 : a.from > b.from ? 1 : 0))
    expect(after).toEqual(c.expect!.map(asState))
    expect(stretches).toEqual(c.stretches.map(asState)) // the history it was given stays as it was
  })
})
