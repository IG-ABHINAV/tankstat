import { describe, expect, it } from 'vitest'
import contract from '../../../../../../tests/backend/contracts/tyre-places/tyre-places.json'
import type { InstallationState } from '../../../src/tyres/tyreHistory.ts'
import { TyreRuleError } from '../../../src/tyres/tyreHistory.ts'
import {
  addPlace, breakUp, changePositionsPlaced, checkPlaces, editInstallationPlaced, editPlace, livePlaces, moveSwapPlaced, placeOf, swapPlaced,
  type Corner, type PlaceBook, type PlaceState,
} from '../../../src/tyres/tyrePlaces.ts'

interface Case {
  name: string
  op: 'breakUp' | 'swap' | 'positions' | 'move' | 'edit' | 'add' | 'addPlace' | 'editPlace' | 'check'
  sets: { id: string; name: string; retiredOn: string | null; brokenUpOn: string | null }[]
  installations: { id: string; set: string; from: string; until: string | null; pairOneFront: boolean | null }[]
  tyres: { id: string; set: string; label: string; retiredOn: string | null }[]
  places: { id: string; tyre: string; installation: string; from: string; until: string | null; corner: string | null }[]
  breakUp?: { set: string; day: string; tyres: { id: string; label: string; corner: string | null }[] }
  swap?: { set: string; day: string; replace: boolean; corners: Record<string, string | null> | null }
  positions?: { set: string; day: string; corners: Record<string, string | null> | null }
  move?: { installation: string; day: string }
  edit?: { installation: string; from: string; until: string | null }
  add?: { set: string; from: string; until: string | null }
  addPlace?: { tyre: string; from: string; until: string | null; corner: string | null }
  editPlace?: { place: string; from: string; until: string | null; corner: string | null }
  moving?: string[]
  restoring?: boolean
  expectPlaces?: { tyre: string; installation: string; from: string; until: string | null; corner: string | null }[]
  expectRetired?: string[]
  error?: { key: string; args: Record<string, string | null> }
}

/** The server's enum name as the schema's (`FrontLeft` → `FRONT_LEFT`). */
const corner = (name: string | null): Corner | null => (name === null ? null : (name.replace(/([a-z])([A-Z])/g, '$1_$2').toUpperCase() as Corner))

/** The contract's short ids as hex ids of the device's own (any id will do, as long as it is hex, for the worked-out place ids). */
const names = new Map<string, string>()
function hex(name: string): string {
  let h = 0x811c9dc5
  const digits: string[] = []
  for (let round = 0; digits.length < 32; round++)
    for (const ch of `${name}#${round}`) {
      h = Math.imul(h ^ ch.charCodeAt(0), 0x01000193) >>> 0
      digits.push((h & 15).toString(16))
    }
  const d = digits.slice(0, 32).join('')
  const id = `${d.slice(0, 8)}-${d.slice(8, 12)}-${d.slice(12, 16)}-${d.slice(16, 20)}-${d.slice(20)}`
  names.set(id, name)
  return id
}
const nameOf = (id: string) => names.get(id) ?? id

const cornersOf = (given: Record<string, string | null> | null) =>
  given === null ? null : Object.fromEntries(Object.entries(given).map(([tyre, c]) => [hex(tyre), corner(c)]))

describe('the device works out the place ids as the server does', () => {
  it.each(contract.placeIds.map((p) => [p.place, p] as const))('%s', (_, p) => {
    expect(placeOf(p.tyre, p.installation)).toBe(p.place)
  })
})

// The same cases the server's TyrePlacesContractTests read (#289): a change made offline shows as the server will make it.
describe('single tyres follow the contract the server keeps', () => {
  it.each((contract.cases as Case[]).map((c) => [c.name, c] as const))('%s', (_, c) => {
    const placeIds = new Map(c.places.map((p) => [p.id, hex(p.id)]))
    const book: PlaceBook = {
      sets: c.sets.map((s) => ({ id: hex(s.id), name: s.name, retiredOn: s.retiredOn, brokenUpOn: s.brokenUpOn })),
      installations: c.installations.map<InstallationState>((s) => ({ id: hex(s.id), setId: hex(s.set), from: s.from, until: s.until, pairOneFront: s.pairOneFront })),
      tyres: c.tyres.map((t) => ({ id: hex(t.id), setId: hex(t.set), label: t.label, retiredOn: t.retiredOn })),
      places: c.places.map<PlaceState>((p) => ({
        id: placeIds.get(p.id)!, tyreId: hex(p.tyre), installationId: hex(p.installation), from: p.from, until: p.until, corner: corner(p.corner),
      })),
    }
    const newId = hex('new')
    const run = (): PlaceBook => {
      switch (c.op) {
        case 'breakUp':
          return breakUp(book, hex(c.breakUp!.set), c.breakUp!.tyres.map((t) => ({ id: hex(t.id), label: t.label, corner: corner(t.corner) })), c.breakUp!.day)
        case 'swap':
          return swapPlaced(book, hex(c.swap!.set), c.swap!.day, null, cornersOf(c.swap!.corners), { id: newId, replace: c.swap!.replace })
        case 'positions':
          return changePositionsPlaced(book, hex(c.positions!.set), c.positions!.day, null, cornersOf(c.positions!.corners), newId)
        case 'move':
          return moveSwapPlaced(book, hex(c.move!.installation), c.move!.day)
        case 'edit':
          return editInstallationPlaced(book, book.installations.map((s) => (s.id === hex(c.edit!.installation) ? { ...s, from: c.edit!.from, until: c.edit!.until } : s)))
        case 'add': {
          const added: InstallationState = { id: newId, setId: hex(c.add!.set), from: c.add!.from, until: c.add!.until, pairOneFront: null }
          return editInstallationPlaced(book, [...book.installations, added])
        }
        case 'addPlace':
          return addPlace(book, hex(c.addPlace!.tyre), c.addPlace!.from, c.addPlace!.until, corner(c.addPlace!.corner), hex('newPlace'))
        case 'editPlace':
          return editPlace(book, placeIds.get(c.editPlace!.place)!, c.editPlace!.from, c.editPlace!.until, corner(c.editPlace!.corner))
        default:
          checkPlaces(book, c.moving ? new Set(c.moving.map((m) => placeIds.get(m)!)) : undefined, c.restoring ?? false)
          return book
      }
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
    const after = run()
    const shown = livePlaces(after).map((p) => `${nameOf(p.tyreId)} ${nameOf(p.installationId)} ${p.from}..${p.until ?? ''} ${p.corner ?? ''}`).sort()
    const want = c.expectPlaces!.map((p) => `${p.tyre} ${p.installation} ${p.from}..${p.until ?? ''} ${corner(p.corner) ?? ''}`).sort()
    expect(shown).toEqual(want)
    if (c.expectRetired) expect(after.tyres.filter((t) => t.retiredOn !== null).map((t) => nameOf(t.id)).sort()).toEqual([...c.expectRetired].sort())
  })
})
