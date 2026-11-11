import { CombinedGraphQLErrors } from '@apollo/client/errors'
import { useTranslation } from 'react-i18next'
import { UnreadableImageError } from '../pictures/resizeImage.ts'
import { TyreRuleError } from '../tyres/tyreHistory.ts'
import { ApiError } from '../pictures/ApiError.ts'
import { OfflineError } from '../offline/errors.ts'

/**
 * Turns a failed request into text in the UI language. The API sends a stable `key` (e.g. "vehicle.nameRequired")
 * and `args` in the error extensions; the message itself is only an English fallback for unknown keys.
 */
export function useKeyText() {
  const { t, i18n } = useTranslation()
  /** The translated message for an error key and its arguments, or undefined when the key is unknown. */
  return (key: unknown, args: unknown): string | undefined => {
    const path = `errors.${String(key)}`
    return typeof key === 'string' && i18n.exists(path) ? String(t(path as never, (args ?? {}) as never)) : undefined
  }
}

/**
 * How the reason the server gave for a change it could not apply reads: the text of its error key with its arguments, else the key
 * itself. One rule for Waiting to sync and the lists' sync column, so both say the same.
 */
export function useReasonText() {
  const keyText = useKeyText()
  return (reason: { key: string; args: readonly { name: string; value: string }[] } | null | undefined): string =>
    reason ? (keyText(reason.key, Object.fromEntries(reason.args.map((a) => [a.name, a.value]))) ?? reason.key) : ''
}

export function useErrorText() {
  const { t } = useTranslation()
  const byKey = useKeyText()

  return (error: unknown): string => {
    if (error instanceof OfflineError) {
      if (error.reason === 'notLoaded') return t('errors.offlineNotLoaded')
      if (error.reason === 'onlineOnly') return t('errors.offlineOnlineOnly')
      return t('errors.offline')
    }
    if (error instanceof ApiError) return byKey(error.key, error.args) ?? error.message
    if (error instanceof TyreRuleError) return byKey(error.key, error.args) ?? error.message // the device's check of a tyre change it keeps
    if (error instanceof UnreadableImageError) return t('image.unreadable')
    if (CombinedGraphQLErrors.is(error)) {
      const first = error.errors[0]
      return byKey(first?.extensions?.key, first?.extensions?.args) ?? first?.message ?? t('errors.network')
    }
    return t('errors.network')
  }
}
