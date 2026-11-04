import { type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { DialogFrame } from '../dialogs/DialogFrame.tsx'
import { DialogTrigger } from '../dialogs/DialogTrigger.tsx'
import { useDialogState } from '../dialogs/useDialogState.ts'
import type { DistanceUnit, VolumeUnit } from '../gql/generated.ts'
import { useToast } from '../toast/toastContext.ts'
import type { ChartRecipe } from './chartFormat.ts'
import { ChartForm, type EditableChart } from './ChartForm.tsx'
import { OutsideTile } from './tileChrome.ts'

export type { EditableChart } from './ChartForm.tsx'

/**
 * A chart in a dialog: its title, recipe and sharing (`ChartForm`), with the live preview. It edits a saved chart of one's own (`chart`),
 * or makes a chart of one's own from a ready-made one on the dashboard (`copyOf`, #247: from its tile's menu), saved only once something
 * differs. A new chart is otherwise made from the dashboard's Add widget dialog (`AddWidgetDialog`), which uses the same form. It opens
 * from its `trigger`, or from something else through `open` and `onOpenChange` (a menu).
 */
export function ChartBuilderDialog({
  trigger,
  open: openProp,
  onOpenChange,
  onClosed,
  vehicle,
  chart,
  copyOf,
  canShare,
  shareLocked = false,
  onSaved,
}: {
  trigger?: ReactNode
  open?: boolean
  onOpenChange?: (open: boolean) => void
  /** Once the dialog is gone (to put the focus somewhere when what opened it is gone too). */
  onClosed?: () => void
  vehicle: { id: string; units: { distance: DistanceUnit; volume: VolumeUnit } }
  /** Sharing with everyone who sees the vehicle needs Edit access to its logs. */
  canShare: boolean
  /** On everyone's dashboard a chart made there must be shared, or it would be dropped from it. */
  shareLocked?: boolean
  /** Saved: the chart's id and title. */
  onSaved: (id: string, title: string) => void | Promise<unknown>
} & (
  | { chart: EditableChart; copyOf?: never }
  | {
      chart?: never
      /** The ready-made chart a copy starts from (its title and recipe). */
      copyOf: { title: string; recipe: ChartRecipe }
    }
)) {
  const { t } = useTranslation()
  const { toast } = useToast()
  const [open, setOpen] = useDialogState({ open: openProp, onOpenChange })

  return (
    <>
      <DialogTrigger trigger={trigger} open={open} onOpen={() => setOpen(true)} />
      <DialogFrame
        open={open}
        onClose={() => setOpen(false)}
        onClosed={onClosed}
        maxWidth={640}
        title={copyOf ? t('charts.dialogCustomize') : t('charts.dialogEdit')}
        description={copyOf ? t('charts.dialogCustomizeDescription', { title: copyOf.title }) : t('charts.dialogDescription')}
      >
        {open && (
          // Opened from a tile's strip while arranging: its preview is a card of its own, not the tile's (#262).
          <OutsideTile>
            <ChartForm
              vehicle={vehicle}
              chart={chart}
              start={copyOf}
              mustChange={copyOf !== undefined}
              shareLocked={shareLocked}
              submitLabel={copyOf ? t('charts.saveAsMine') : undefined}
              canShare={canShare}
              onSaved={async (id, title) => {
                await onSaved(id, title)
                setOpen(false)
                toast(t('toast.saved'))
              }}
            />
          </OutsideTile>
        )}
      </DialogFrame>
    </>
  )
}
