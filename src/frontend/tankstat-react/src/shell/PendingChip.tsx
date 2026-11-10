import Chip from '@mui/material/Chip'
import { CloudAlert, CloudUpload } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { usePendingCount } from '../offline/outbox.ts'
import { useParkedChanges } from '../offline/useParkedChanges.ts'

/**
 * In the top bar while changes wait on this device for the server: how many, as a link to the page that lists them; otherwise, while the
 * server keeps changes it could not apply that the user may see (`parked`: only for someone whose data is open), how many. Gone otherwise.
 */
export function PendingChip({ parked: askParked, compact = false }: { parked: boolean; compact?: boolean }) {
  const { t } = useTranslation()
  const count = usePendingCount()
  const notApplied = useParkedChanges(askParked).parked?.length ?? 0
  if (count === 0 && notApplied === 0) return null
  const waiting = count > 0
  const text = waiting ? t('offline.waiting', { count }) : t('offline.notApplied', { count: notApplied })
  return (
    <Chip
      component={Link}
      to="/sync"
      clickable
      className="tk-appear"
      color={waiting ? 'neutral' : 'warning'}
      icon={waiting ? <CloudUpload size={14} aria-hidden /> : <CloudAlert size={14} aria-hidden />}
      // On a phone only the number shows (the navigation says the rest); the link's name is the whole sentence either way.
      label={compact ? (waiting ? count : notApplied) : text}
      aria-label={compact ? text : undefined}
      sx={{ minHeight: 44, minWidth: 44, flexShrink: 0 }} // a 44 px touch target, its number alone too
    />
  )
}
