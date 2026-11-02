import { CombinedGraphQLErrors } from '@apollo/client/errors'
import { ApiError } from '../pictures/ApiError.ts'

/**
 * The field a server's refusal is about, by its key: a required value the form let through (one the server needs only in some cases, or a
 * form that lost its own check) is said at that field, as the form's own checks are, not under the form. Field names are the forms' own.
 */
const FIELD_OF_KEY: Readonly<Record<string, string>> = {
  'vehicle.nameRequired': 'name',
  'expense.titleRequired': 'title',
  'cost.discountWithoutAmount': 'discount',
  'cost.discountNegative': 'discount',
  'recurring.titleRequired': 'title',
  'recurring.odometerRequired': 'lastDoneOdometer',
  'recurring.doneOdometerRequired': 'odometer',
  'chart.titleRequired': 'title',
  'chart.datesRequired': 'from',
  'backup.passphraseRequired': 'passphrase',
  'backup.keyRequired': 'secret',
}

/** The field (by its `name`) a submit's refusal names, if any. */
export function fieldOf(error: unknown): string | undefined {
  const key = CombinedGraphQLErrors.is(error) ? error.errors[0]?.extensions?.key : error instanceof ApiError ? error.key : undefined
  return typeof key === 'string' ? FIELD_OF_KEY[key] : undefined
}
