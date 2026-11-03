import Alert from '@mui/material/Alert'
import Button from '@mui/material/Button'
import Snackbar from '@mui/material/Snackbar'
import { CircleCheck, CircleX, Info, TriangleAlert } from 'lucide-react'
import { useMemo, useRef, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { glass } from '../theme/components.ts'
import { ToastContext, type Toast, type ToastApi } from './toastContext.ts'

const SHORT_MS = 4_000
/** Long enough to find and press Undo; the timer also stops while the pointer is on Undo or the focus is in the message. */
const WITH_ACTION_MS = 10_000

const ICONS = { success: CircleCheck, info: Info, warning: TriangleAlert, error: CircleX }

/**
 * Shows the app's short messages one at a time, in order, at the bottom of the screen; above the routes, so a message survives
 * navigation (a vehicle trashed from its own page). A status, not an alert: it is announced politely. An error (a failed undo) does not
 * wait: it cuts the current message short and comes next. A message lets taps through to the page under it, only its button takes them
 * (#239): it comes at the bottom of the screen, over whatever control is there, sometimes on its own time ("can now be used offline").
 */
export function ToastProvider({ children }: { children: ReactNode }) {
  const { t } = useTranslation()
  // Each message knows whether it is on its way out (closing); the first one is the one shown.
  const [queue, setQueue] = useState<(Toast & { key: number; closing?: boolean })[]>([])
  const next = useRef(0)
  const current = queue[0]

  const api = useMemo<ToastApi>(() => {
    const toast: ToastApi['toast'] = (item) => {
      const entry = { ...(typeof item === 'string' ? { message: item } : item), key: next.current++ }
      setQueue((all) =>
        entry.severity !== 'error' || all.length === 0 ? [...all, entry]
        : [{ ...all[0], closing: true }, entry, ...all.slice(1)], // the current one leaves now; the error is next
      )
    }
    return {
      toast,
      undoable: (message, undo) =>
        toast({
          message,
          severity: 'info',
          action: { label: t('common.undo'), run: () => undo().catch(() => toast({ message: t('toast.undoFailed'), severity: 'error' })) },
        }),
    }
  }, [t])

  const close = () => setQueue(([first, ...rest]) => (first ? [{ ...first, closing: true }, ...rest] : rest))
  const severity = current?.severity ?? 'success'
  const Icon = ICONS[severity]
  return (
    <ToastContext.Provider value={api}>
      {children}
      <Snackbar
        key={current?.key}
        open={current !== undefined && !current.closing}
        autoHideDuration={current?.action ? WITH_ACTION_MS : SHORT_MS}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
        onClose={(_, reason) => reason !== 'clickaway' && close()}
        // Inherited by the message: a tap on it reaches the page (`auto` again on its button). On a phone the root is the screen's width.
        sx={{ pointerEvents: 'none' }}
        slotProps={{ transition: { onExited: () => setQueue((all) => all.slice(1)) } }}
      >
        {current && (
          <Alert
            role="status"
            severity={severity}
            icon={<Icon size={16} aria-hidden />}
            // It floats over whatever the page shows, so it is a surface of its own (glossy, transparent or opaque, as the user chose), its
            // kind's tint over it, in the text colour: the icon and a hairline say the kind, never a see-through tint alone (#63).
            sx={(theme) => ({
              ...glass(theme),
              backgroundImage: `linear-gradient(${theme.vars.palette[severity].soft}, ${theme.vars.palette[severity].soft})`,
              color: 'text.primary',
              boxShadow: `0 0 0 1px color-mix(in srgb, ${theme.vars.palette[severity].main} 55%, transparent), ${theme.shadows[9]}`,
              alignItems: 'center',
              '& .MuiAlert-icon': { color: theme.vars.palette[severity].softText },
            })}
            action={
              current.action && (
                <Button
                  size="small"
                  variant="soft"
                  color={severity === 'error' ? 'error' : 'primary'}
                  sx={{ pointerEvents: 'auto' }}
                  onClick={() => {
                    current.action!.run()
                    close()
                  }}
                >
                  {current.action.label}
                </Button>
              )
            }
          >
            {current.message}
          </Alert>
        )}
      </Snackbar>
    </ToastContext.Provider>
  )
}
