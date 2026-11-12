import Button from '@mui/material/Button'
import type { ReactNode } from 'react'
import { DialogButtons, DialogCancel, DialogFrame } from '../dialogs/DialogFrame.tsx'
import { DialogTrigger } from '../dialogs/DialogTrigger.tsx'
import { useDialogState } from '../dialogs/useDialogState.ts'

/**
 * A confirmation before something hard to undo. Focus starts on Cancel (the safe choice) and returns to the trigger. It opens from its
 * `trigger`, or, when something else opens it (a form's submit), from `open` + `onOpenChange`. `onClosed` runs once it is fully gone.
 */
export function ConfirmDialog({
  trigger,
  open: openAsked,
  onOpenChange,
  onClosed,
  title,
  description,
  confirmLabel,
  onConfirm,
  children,
}: {
  trigger?: ReactNode
  open?: boolean
  onOpenChange?: (open: boolean) => void
  onClosed?: () => void
  title: string
  description: string
  confirmLabel: string
  onConfirm: () => void
  /** What the confirmation asks besides (a box to tick), between the description and the buttons. */
  children?: ReactNode
}) {
  const [open, setOpen] = useDialogState({ open: openAsked, onOpenChange })
  return (
    <>
      <DialogTrigger trigger={trigger} open={open} onOpen={() => setOpen(true)} />
      <DialogFrame open={open} onClose={() => setOpen(false)} onClosed={onClosed} title={title} description={description} role="alertdialog">
        {children}
        <DialogButtons>
          <DialogCancel autoFocus />
          <Button
            color="error"
            onClick={() => {
              setOpen(false)
              onConfirm()
            }}
          >
            {confirmLabel}
          </Button>
        </DialogButtons>
      </DialogFrame>
    </>
  )
}
