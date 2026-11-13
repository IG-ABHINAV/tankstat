/**
 * The tyre category (#290, the owner's decision 58): a category that opens an expense's tyre details by itself. *Tyres* or *Gumik* in
 * either language, and the category of the vehicle's last tyre cost (`VehicleTyres.expenseCategory`), compared without case and accents.
 * Kept small and free of the tyre modules: the Expense dialog is eager (the home cards open it), its tyre section is not.
 */
const WORDS = ['Tyres', 'Gumik'] as const

/** A category as the comparison sees it: trimmed, lowercase, without its accents (`Gumík` is `gumik`). */
export const foldCategory = (text: string): string => text.trim().toLowerCase().normalize('NFD').replace(/\p{M}/gu, '')

/** The tyre categories for the switch's hint: *Tyres*, *Gumik* and the last tyre cost's, without repeats. */
export function tyreCategories(last: string | null | undefined): string[] {
  const seen = new Set<string>()
  return [...WORDS, ...(last?.trim() ? [last.trim()] : [])].filter((c) => {
    const folded = foldCategory(c)
    if (seen.has(folded)) return false
    seen.add(folded)
    return true
  })
}

/** Whether a category is a tyre category; an empty one never is. */
export function isTyreCategory(category: string | null | undefined, last: string | null | undefined): boolean {
  const folded = foldCategory(category ?? '')
  return folded !== '' && tyreCategories(last).some((c) => foldCategory(c) === folded)
}
