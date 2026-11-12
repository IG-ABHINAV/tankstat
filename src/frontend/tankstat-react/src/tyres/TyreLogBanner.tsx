import Alert from '@mui/material/Alert'
import Link from '@mui/material/Link'
import { useTranslation } from 'react-i18next'
import { Link as RouterLink } from 'react-router'
import type { TyreEntryKind } from '../gql/generated.ts'

/**
 * An expense a tyre log line made (#288), in its edit dialog: where it comes from, that its date and odometer are the line's (changing them
 * here changes both), and a link to the line in the tyre log, while that is not in the trash.
 */
export function TyreLogBanner({ vehicleId, line }: { vehicleId: string; line: { id: string; kind: TyreEntryKind; setName?: string | null; trashed: boolean } }) {
  const { t } = useTranslation()
  const kind = t(`tyres.entries.kinds.${line.kind}`)
  return (
    <Alert severity="info" sx={{ mb: 1.5 }}>
      <strong>{t('expenses.tyreLog.bannerTitle')}</strong>
      {': '}
      {t('expenses.tyreLog.banner', { what: line.setName ? `${kind}, ${line.setName}` : kind })}
      {!line.trashed && (
        <>
          {' '}
          <Link component={RouterLink} to={`/vehicles/${vehicleId}?tab=tyres&entry=${line.id}`}>
            {t('expenses.tyreLog.open')}
          </Link>
        </>
      )}
    </Alert>
  )
}
