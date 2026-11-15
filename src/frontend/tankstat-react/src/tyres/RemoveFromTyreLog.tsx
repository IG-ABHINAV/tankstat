import Link from '@mui/material/Link'
import Typography from '@mui/material/Typography'
import { useTranslation } from 'react-i18next'
import { ConfirmDialog } from '../components/ConfirmDialog.tsx'
import { standaloneLink } from '../theme/components.ts'
import { DeleteTyreEntryDocument } from '../gql/generated.ts'
import { isSwapLine, labelsOf, sharersOf } from './entries.ts'
import { type TyreEntry, type Tyres, useTyreMutation } from './tyres.ts'

/**
 * *Remove from the tyre log* (#290, the owner's decisions 55 and 61), in the banner of an expense the tyre log made: every live line holding
 * it goes to the trash, the expense stays with its reading (`deleteTyreEntry` with `withExpense: false`, as the tyre log row's bin does
 * with its box unticked). A swap's line has no such link: removing it would remove the swap, so the banner says where that is done.
 * The lines go one by one: when one is refused the ones before it stay in the trash, and the dialog shows the refusal and the expense as
 * it is now (`onDone`).
 */
export default function RemoveFromTyreLog({
  vehicleId,
  tyres,
  line,
  onDone,
}: {
  vehicleId: string
  tyres: Tyres
  line: TyreEntry
  /** After the lines went (`error` undefined), or one of them was refused. */
  onDone: (error?: unknown) => void
}) {
  const { t } = useTranslation()
  const run = useTyreMutation(vehicleId)
  if (isSwapLine(line)) {
    return (
      <Typography component="span" variant="body2" sx={{ display: 'block', mt: 0.5 }}>
        {t('expenses.tyreLog.swapStays')}
      </Typography>
    )
  }
  const holders = [line, ...sharersOf(tyres, line)]
  const remove = async () => {
    try {
      for (const holder of holders) await run(DeleteTyreEntryDocument, { input: { id: holder.id, withExpense: false } })
      onDone()
    } catch (err) {
      onDone(err)
    }
  }
  return (
    <ConfirmDialog
      trigger={
        <Link component="button" type="button" sx={standaloneLink}>
          {t('expenses.tyreLog.remove')}
        </Link>
      }
      title={t('expenses.tyreLog.removeTitle')}
      description={holders.length > 1 ? t('expenses.tyreLog.removeMany', { tyres: labelsOf(tyres, holders) }) : t('expenses.tyreLog.removeOne')}
      confirmLabel={t('expenses.tyreLog.remove')}
      onConfirm={() => void remove()}
    />
  )
}
