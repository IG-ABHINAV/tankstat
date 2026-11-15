import Button from '@mui/material/Button'
import Stack from '@mui/material/Stack'
import { ScanText } from 'lucide-react'
import { lazy, Suspense } from 'react'
import { useTranslation } from 'react-i18next'
import { LabeledSwitch } from '../components/LabeledSwitch.tsx'
import { Loading } from '../components/Loading.tsx'
import { FieldMessage } from '../forms/Field.tsx'
import { ReadNote } from '../recognition/ReadNote.tsx'
import type { VehicleTyresFieldsFragment } from '../gql/generated.ts'
import { loadSection, type ExpenseTyreDetails } from './useExpenseTyreDetails.ts'

const ExpenseTyreSection = lazy(loadSection)

/** The switch and, while it is on, the section (a lazy chunk with a boundary of its own, so the fields above it stay as they are). */
export function TyreDetailsFields({ details, tyres, hint, note }: { details: ExpenseTyreDetails; tyres: VehicleTyresFieldsFragment; hint: string; note: string }) {
  const { t } = useTranslation()
  if (!details.available) return null
  const photo = details.photo
  const kindName = photo ? t(`tyres.entries.kinds.${photo.kind}`) : ''
  const field = t('tyres.entries.kind')
  return (
    <>
      {!details.linked && <LabeledSwitch label={t('expenses.tyreDetails.switch')} hint={hint} checked={details.open} onChange={details.toggle} />}
      {/* A photo that shows tyre work while the section is closed (#290, decision 67): Use it opens it with that kind. */}
      {photo && !details.open && (
        <Stack direction="row" className="tk-appear" sx={{ alignItems: 'center', gap: 1, flexWrap: 'wrap' }}>
          <FieldMessage sx={{ color: 'text.primary' }}>
            <ScanText size={12} aria-hidden style={{ verticalAlign: '-2px', marginRight: 4 }} />
            {t('expenses.tyreDetails.photoShows', { kind: kindName })}
          </FieldMessage>
          <Button type="button" variant="soft" sx={{ minHeight: 44 }} aria-label={t('reading.useAria', { value: kindName, field })} onClick={photo.use}>
            {t('reading.use')}
          </Button>
        </Stack>
      )}
      {details.open && (
        <Suspense fallback={<Loading />}>
          <ExpenseTyreSection
            tyres={tyres}
            state={details.state}
            onChange={details.change}
            kindError={details.kindError}
            kindRef={details.kindRef}
            note={details.linked ? t('expenses.tyreDetails.linkedNote') : note}
            linked={details.linked}
            kindNote={photo && (photo.filled || photo.offered) ? <ReadNote filled={photo.filled} offered={photo.offered ? kindName : undefined} field={field} onUse={photo.use} /> : undefined}
          />
        </Suspense>
      )}
    </>
  )
}
