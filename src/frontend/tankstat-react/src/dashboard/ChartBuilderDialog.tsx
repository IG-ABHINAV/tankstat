import { type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { DialogFrame } from '../dialogs/DialogFrame.tsx'
import { DialogTrigger } from '../dialogs/DialogTrigger.tsx'
import { useDialogState } from '../dialogs/useDialogState.ts'
import type { DistanceUnit, VolumeUnit } from '../gql/generated.ts'
import { useToast } from '../toast/toastContext.ts'
import { ChartForm, type EditableChart } from './ChartForm.tsx'

export type { EditableChart } from './ChartForm.tsx'

/**
 * Edits a saved chart of one's own in a dialog: its title, recipe and sharing (`ChartForm`), with the live preview. A new chart is made
 * from the dashboard's Add widget dialog (`AddWidgetDialog`), which uses the same form.
 */
export function ChartBuilderDialog({
  trigger,
  vehicle,
  chart,
  canShare,
  onSaved,
}: {
  trigger: ReactNode
  vehicle: { id: string; units: { distance: DistanceUnit; volume: VolumeUnit } }
  chart: EditableChart
  /** Sharing with everyone who sees the vehicle needs Edit access to its logs. */
  canShare: boolean
  onSaved: () => void | Promise<unknown>
}) {
  const { t } = useTranslation()
  const { toast } = useToast()
  const [open, setOpen] = useDialogState()

  return (
    <>
      <DialogTrigger trigger={trigger} open={open} onOpen={() => setOpen(true)} />
      <DialogFrame open={open} onClose={() => setOpen(false)} maxWidth={640} title={t('charts.dialogEdit')} description={t('charts.dialogDescription')}>
        {open && (
          <ChartForm
            vehicle={vehicle}
            chart={chart}
            canShare={canShare}
            onSaved={async () => {
              await onSaved()
              setOpen(false)
              toast(t('toast.saved'))
            }}
          />
        )}
      </DialogFrame>
    </>
  )
}
