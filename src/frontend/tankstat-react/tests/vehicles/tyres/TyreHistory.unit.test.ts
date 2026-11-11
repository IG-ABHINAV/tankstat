import { describe, expect, it } from 'vitest'
import contract from '../../../../../../tests/backend/contracts/tyre-swaps/tyre-swaps.json'
import { changePositions, checked, moveSwap, swap, TyreRuleError, type SetState, type InstallationState } from '../../../src/tyres/tyreHistory.ts'
import { follow, lineFor, type Joined } from '../../../src/tyres/tyreLog.ts'

interface Case {
  name: string
  op: 'swap' | 'positions' | 'check' | 'move'
  sets: SetState[]
  installations: { id: string; set: string; from: string; until: string | null; pairOneFront: boolean | null }[]
  swap?: { set: string; day: string; pairOneFront: boolean | null; replace: boolean; pairsGiven: boolean }
  positions?: { set: string; day: string; pairOneFront: boolean | null }
  move?: { installation: string; day: string }
  moving?: string[]
  lineNotes?: Record<string, string>
  lines?: { id: string; kind: string; set: string; day: string; note: string | null }[]
  trashedLines?: string[]
  expect?: Case['installations']
  error?: { key: string; args: Record<string, string | null> }
}

/** The server's enum name as the schema's (`PositionsChanged` → `POSITIONS_CHANGED`). */
const kindOf = (name: string) => name.replace(/([a-z])([A-Z])/g, '$1_$2').toUpperCase()

const asState = (s: Case['installations'][number]): InstallationState => ({ id: s.id, setId: s.set, from: s.from, until: s.until, pairOneFront: s.pairOneFront })

// The same cases the server's TyreSwapContractTests read (#287): a tyre change made offline shows as the server will make it.
describe('the tyre history follows the contract the server keeps', () => {
  it.each((contract.cases as Case[]).map((c) => [c.name, c] as const))('%s', (_, c) => {
    const installations = c.installations.map(asState)
    const joined: Joined[] = []
    const run = () => {
      if (c.op === 'swap')
        return swap(c.sets, installations, c.swap!.set, c.swap!.day, c.swap!.pairOneFront, {
          id: 'new', replace: c.swap!.replace, pairsGiven: c.swap!.pairsGiven, joined: (id, day) => joined.push({ id, day }),
        })
      if (c.op === 'move') return moveSwap(c.sets, installations, c.move!.installation, c.move!.day)
      if (c.op === 'positions') return changePositions(c.sets, installations, c.positions!.set, c.positions!.day, c.positions!.pairOneFront, 'new')
      return checked(c.sets, installations, c.moving ? new Set(c.moving) : undefined)
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
    expect(installations).toEqual(c.installations.map(asState)) // the history it was given stays as it was
    if (!c.lines) return

    // The tyre log's lines follow (#288): every installation has its line, with its id.
    const lines = installations.map((i) => ({ ...lineFor(i, installations), note: c.lineNotes?.[i.id] ?? null }))
    const followed = follow(installations, after, lines, joined, () => 'kept')
    const shown = [...followed.lines].sort((a, b) => (a.day < b.day ? -1 : a.day > b.day ? 1 : a.id < b.id ? -1 : 1))
    expect(shown.map((l) => ({ id: l.id, kind: l.kind, set: l.setId, day: l.day, note: l.note })))
      .toEqual(c.lines.map((l) => ({ ...l, kind: kindOf(l.kind) })))
    expect(followed.trashed.map((l) => l.id).sort()).toEqual(c.trashedLines)
  })
})
