import Alert from '@mui/material/Alert'
import Link from '@mui/material/Link'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Link as RouterLink } from 'react-router'
import type { TyreEntryKind } from '../gql/generated.ts'

/**
 * An expense a tyre log line made (#288), in its edit dialog: where it comes from, that its date and odometer are the line's (changing them
 * here changes both), and a link to the line in the tyre log, then `actions` (#290: *Remove from the tyre log*, or why a swap's line has
 * none). While the line is in the trash it says so: restoring it there links the two again (the owner's decision 69), no link.
 */
export function TyreLogBanner({
  vehicleId,
  line,
  actions,
}: {
  vehicleId: string
  line: { id: string; kind: TyreEntryKind; setName?: string | null; trashed: boolean }
  actions?: ReactNode
}) {
  const { t } = useTranslation()
  const kind = t(`tyres.entries.kinds.${line.kind}`)
  const what = line.setName ? `${kind}, ${line.setName}` : kind
  return (
    <Alert severity="info" sx={{ mb: 1.5 }}>
      <strong>{t('expenses.tyreLog.bannerTitle')}</strong>
      {': '}
      {t(line.trashed ? 'expenses.tyreLog.bannerTrashed' : 'expenses.tyreLog.banner', { what })}
      {!line.trashed && (
        <>
          {' '}
          <Link component={RouterLink} to={`/vehicles/${vehicleId}?tab=tyres&entry=${line.id}`}>
            {t('expenses.tyreLog.open')}
          </Link>
          {actions && ' '}
          {actions}
        </>
      )}
    </Alert>
  )
}
