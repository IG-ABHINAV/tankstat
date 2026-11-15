import { lazy, Suspense } from 'react'
import { useTranslation } from 'react-i18next'
import { LabeledSwitch } from '../components/LabeledSwitch.tsx'
import { Loading } from '../components/Loading.tsx'
import type { VehicleTyresFieldsFragment } from '../gql/generated.ts'
import { loadSection, type ExpenseTyreDetails } from './useExpenseTyreDetails.ts'

const ExpenseTyreSection = lazy(loadSection)

/** The switch and, while it is on, the section (a lazy chunk with a boundary of its own, so the fields above it stay as they are). */
export function TyreDetailsFields({ details, tyres, hint, note }: { details: ExpenseTyreDetails; tyres: VehicleTyresFieldsFragment; hint: string; note: string }) {
  const { t } = useTranslation()
  if (!details.available) return null
  return (
    <>
      {!details.linked && <LabeledSwitch label={t('expenses.tyreDetails.switch')} hint={hint} checked={details.open} onChange={details.toggle} />}
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
          />
        </Suspense>
      )}
    </>
  )
}
