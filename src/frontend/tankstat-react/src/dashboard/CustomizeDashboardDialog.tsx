import { useMutation } from '@apollo/client/react'
import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { ArrowDown, ArrowUp, X } from 'lucide-react'
import { AnimatePresence, motion } from 'motion/react'
import { useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { IconAction } from '../components/IconAction.tsx'
import { QUICK } from '../components/motion.ts'
import { Segmented } from '../components/Segmented.tsx'
import { LabeledSelect } from '../components/UnitSelect.tsx'
import { DialogButtons, DialogCancel, DialogFrame } from '../dialogs/DialogFrame.tsx'
import { DialogTrigger } from '../dialogs/DialogTrigger.tsx'
import { useDialogState } from '../dialogs/useDialogState.ts'
import { ResetDashboardLayoutDocument, SaveDashboardLayoutDocument } from '../gql/generated.ts'
import { ErrorMessage } from '../messages.tsx'
import { useConnectivity } from '../offline/useConnectivity.ts'
import { useToast } from '../toast/toastContext.ts'
import { FIGURES, LAYOUT_PRESETS, READY_CHARTS, readyItem, resolveLayout, savedItem, toLayout, type ChartItem, type FigureId, type Layout, type LayoutPresetId } from './widgets.ts'

/** A saved chart as the dashboard lists it. */
export interface SavedChartRef {
  id: string
  title: string
  isShared: boolean
}

type Scope = 'shared' | 'personal'

/**
 * Arrange a vehicle's dashboard (#211): the vehicle's, which everyone who sees it gets (with Edit access to its logs), or the user's own,
 * which wins for them alone. Start from a preset, move the key figures and the charts up and down, take them off and add them back, then
 * save; or go back (the vehicle's to Standard, one's own to the vehicle's). Up and down buttons, as Arrange on the home page: they work
 * with a keyboard, a screen reader and a finger alike. Saving needs the server.
 */
export function CustomizeDashboardDialog({
  vehicleId,
  shared,
  personal,
  canChangeShared,
  allowPersonal,
  savedCharts,
  onSaved,
}: {
  vehicleId: string
  shared: Layout | null
  personal: Layout | null
  canChangeShared: boolean
  /** False with sign-in off: everyone is the same anonymous user, so "just me" would be everyone's too. */
  allowPersonal: boolean
  savedCharts: readonly SavedChartRef[]
  onSaved: () => Promise<unknown>
}) {
  const { t } = useTranslation()
  const [open, setOpen] = useDialogState()
  const initial: Scope = personal && allowPersonal ? 'personal' : canChangeShared ? 'shared' : 'personal'
  const [scope, setScope] = useState<Scope>(initial)
  const scopes = [...(canChangeShared ? (['shared'] as const) : []), ...(allowPersonal ? (['personal'] as const) : [])]

  return (
    <>
      <DialogTrigger
        trigger={
          <Button size="large" variant="soft">
            {t('dashboard.customize.button')}
          </Button>
        }
        open={open}
        onOpen={() => {
          setScope(initial)
          setOpen(true)
        }}
      />
      <DialogFrame open={open} onClose={() => setOpen(false)} maxWidth={640} title={t('dashboard.customize.title')} description={t('dashboard.customize.description')}>
        {open && (
          <Stack sx={{ gap: 2 }}>
            {scopes.length > 1 && (
              <Segmented
                label={t('dashboard.customize.whose')}
                value={scope}
                onChange={setScope}
                options={[
                  { value: 'shared', label: t('dashboard.customize.everyone') },
                  { value: 'personal', label: t('dashboard.customize.mine') },
                ]}
              />
            )}
            {/* A list per scope, started afresh from what that scope shows. */}
            <Editor
              key={scope}
              vehicleId={vehicleId}
              scope={scope}
              stored={scope === 'shared' ? shared : personal}
              start={scope === 'shared' ? shared : (personal ?? shared)}
              saved={scope === 'shared' ? savedCharts.filter((c) => c.isShared) : savedCharts}
              privateLeftOut={scope === 'shared' && savedCharts.some((c) => !c.isShared)}
              onDone={async () => {
                await onSaved()
                setOpen(false)
              }}
            />
          </Stack>
        )}
      </DialogFrame>
    </>
  )
}

function Editor({
  vehicleId,
  scope,
  stored,
  start,
  saved,
  privateLeftOut,
  onDone,
}: {
  vehicleId: string
  scope: Scope
  /** The layout this scope keeps (null: none yet, so there is nothing to go back from). */
  stored: Layout | null
  /** What the lists start from: one's own starts from what one sees, the vehicle's when there is none of one's own. */
  start: Layout | null
  saved: readonly SavedChartRef[]
  privateLeftOut: boolean
  onDone: () => Promise<void>
}) {
  const { t } = useTranslation()
  const { toast } = useToast()
  const { reachable } = useConnectivity()
  const first = resolveLayout(start, saved)
  const [figures, setFigures] = useState<FigureId[]>(first.figures)
  const [charts, setCharts] = useState<ChartItem<SavedChartRef>[]>(first.charts)
  const [hidden, setHidden] = useState<SavedChartRef[]>(first.hidden)
  const [status, setStatus] = useState('')
  const [save, saving] = useMutation(SaveDashboardLayoutDocument)
  const [reset, resetting] = useMutation(ResetDashboardLayoutDocument)
  const busy = saving.loading || resetting.loading
  const [failure, setFailure] = useState<unknown>()

  const figureName = (id: FigureId) => t(`dashboard.kpi.${id}`)
  const chartName = (item: ChartItem<SavedChartRef>) => (item.kind === 'ready' ? t(`dashboard.presets.${item.chart.id}`) : item.chart.title)
  const chartNote = (item: ChartItem<SavedChartRef>) =>
    item.kind === 'ready' ? t('dashboard.customize.ready') : item.chart.isShared ? t('dashboard.customize.sharedChart') : t('dashboard.customize.savedChart')

  const applyPreset = (id: LayoutPresetId) => {
    const next = resolveLayout(LAYOUT_PRESETS[id], saved)
    setFigures(next.figures)
    setCharts(next.charts)
    setHidden(next.hidden)
    setStatus(t('dashboard.customize.started', { name: t(`dashboard.layoutPresets.${id}`) }))
  }
  const addFigure = (id: FigureId) => {
    setFigures([...figures, id])
    setStatus(t('dashboard.customize.added', { name: figureName(id) }))
  }
  const addChart = (key: string) => {
    const ready = READY_CHARTS.find((c) => `preset:${c.id}` === key)
    const back = hidden.find((c) => `chart:${c.id}` === key)
    const item = ready ? readyItem<SavedChartRef>(ready) : back ? savedItem(back) : undefined
    if (!item) return
    setCharts([...charts, item])
    if (back) setHidden(hidden.filter((c) => c !== back))
    setStatus(t('dashboard.customize.added', { name: chartName(item) }))
  }
  const removeChart = (item: ChartItem<SavedChartRef>) => {
    setCharts(charts.filter((c) => c !== item))
    if (item.kind === 'saved') setHidden([...hidden, item.chart])
    setStatus(t('dashboard.customize.removed', { name: chartName(item) }))
  }

  async function run(work: () => Promise<unknown>) {
    setFailure(undefined)
    try {
      await work()
      await onDone()
      toast(t('toast.saved'))
    } catch (e) {
      setFailure(e)
    }
  }

  const figureOptions = FIGURES.filter((f) => !figures.includes(f)).map((f) => ({ value: f, label: figureName(f) }))
  const chartOptions = [
    ...READY_CHARTS.filter((c) => !charts.some((item) => item.key === `preset:${c.id}`)).map((c) => ({ value: `preset:${c.id}`, label: t(`dashboard.presets.${c.id}`) })),
    ...hidden.map((c) => ({ value: `chart:${c.id}`, label: c.title })),
  ]

  return (
    <Stack sx={{ gap: 2 }}>
      <LabeledSelect<LayoutPresetId | ''>
        label={t('dashboard.customize.preset')}
        hint={t('dashboard.customize.presetHint')}
        placeholder={t('dashboard.customize.choose')}
        value=""
        disabled={busy}
        onChange={(id) => id && applyPreset(id)}
        options={(Object.keys(LAYOUT_PRESETS) as LayoutPresetId[]).map((id) => ({ value: id, label: t(`dashboard.layoutPresets.${id}`) }))}
      />
      <MovableList
        label={t('dashboard.customize.figures')}
        items={figures}
        keyOf={(id) => id}
        nameOf={figureName}
        disabled={busy}
        onChange={(next, moved) => {
          setFigures(next)
          setStatus(moved)
        }}
        onRemove={(id) => {
          setFigures(figures.filter((f) => f !== id))
          setStatus(t('dashboard.customize.removed', { name: figureName(id) }))
        }}
      />
      {figureOptions.length > 0 && (
        <LabeledSelect<FigureId | ''> label={t('dashboard.customize.addFigure')} placeholder={t('dashboard.customize.choose')} value="" disabled={busy} onChange={(id) => id && addFigure(id)} options={figureOptions} />
      )}
      <MovableList
        label={t('dashboard.customize.charts')}
        items={charts}
        keyOf={(item) => item.key}
        nameOf={chartName}
        noteOf={chartNote}
        disabled={busy}
        onChange={(next, moved) => {
          setCharts(next)
          setStatus(moved)
        }}
        onRemove={removeChart}
      />
      {chartOptions.length > 0 && (
        <LabeledSelect<string> label={t('dashboard.customize.addChart')} placeholder={t('dashboard.customize.choose')} value="" disabled={busy} onChange={(key) => key && addChart(key)} options={chartOptions} />
      )}
      {privateLeftOut && (
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          {t('dashboard.customize.privateHint')}
        </Typography>
      )}
      <Typography variant="body2" role="status" sx={{ color: 'text.secondary', minHeight: '1.5em' }}>
        {status}
      </Typography>
      {!reachable && (
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          {t('dashboard.customize.needsServer')}
        </Typography>
      )}
      {failure !== undefined && <ErrorMessage error={failure} />}
      <DialogButtons>
        {stored && (
          <Button variant="ghost" disabled={busy || !reachable} onClick={() => void run(() => reset({ variables: { vehicleId, personal: scope === 'personal' } }))} sx={{ mr: 'auto' }}>
            {scope === 'shared' ? t('dashboard.customize.resetShared') : t('dashboard.customize.resetPersonal')}
          </Button>
        )}
        <DialogCancel disabled={busy} />
        <Button
          loading={saving.loading}
          disabled={busy || !reachable}
          onClick={() => void run(() => save({ variables: { input: { vehicleId, personal: scope === 'personal', ...toLayout(figures, charts, hidden) } } }))}
        >
          {t('dashboard.customize.save')}
        </Button>
      </DialogButtons>
    </Stack>
  )
}

/** A list moved with up and down buttons; the focus travels with the moved row, a line says where it went. */
function MovableList<T>({
  label,
  items,
  keyOf,
  nameOf,
  noteOf,
  disabled,
  onChange,
  onRemove,
}: {
  label: string
  items: readonly T[]
  keyOf: (item: T) => string
  nameOf: (item: T) => string
  noteOf?: (item: T) => string
  disabled: boolean
  onChange: (items: T[], said: string) => void
  onRemove: (item: T) => void
}) {
  const { t } = useTranslation()
  const focusNext = useRef<{ key: string; direction: 'up' | 'down' } | null>(null)
  const list = useRef<HTMLOListElement>(null)
  const id = useId()

  const move = (index: number, delta: -1 | 1) => {
    const target = index + delta
    if (target < 0 || target >= items.length) return
    const next = [...items]
    next[index] = items[target]
    next[target] = items[index]
    focusNext.current = { key: keyOf(items[index]), direction: delta === -1 ? 'up' : 'down' }
    onChange(next, t('dashboard.customize.moved', { name: nameOf(items[index]), position: target + 1, total: next.length }))
  }

  useEffect(() => {
    const pending = focusNext.current
    if (!pending) return
    focusNext.current = null
    const row = [...(list.current?.querySelectorAll<HTMLElement>('[data-key]') ?? [])].find((r) => r.dataset.key === pending.key)
    const same = row?.querySelector<HTMLButtonElement>(`[data-direction="${pending.direction}"]`)
    const other = row?.querySelector<HTMLButtonElement>(`[data-direction="${pending.direction === 'up' ? 'down' : 'up'}"]`)
    ;(same && !same.disabled ? same : other)?.focus()
  }, [items])

  const button = { width: 44, height: 44 }
  let body: ReactNode
  if (items.length === 0) {
    body = (
      <Typography variant="body2" sx={{ color: 'text.secondary' }}>
        {t('dashboard.customize.empty')}
      </Typography>
    )
  } else {
    body = (
      <Stack component="ol" ref={list} aria-labelledby={id} sx={{ gap: 1, listStyle: 'none', p: 0, m: 0 }}>
        <AnimatePresence initial={false}>
          {items.map((item, i) => (
            <motion.li key={keyOf(item)} data-key={keyOf(item)} layout transition={QUICK}>
              <Stack direction="row" sx={{ alignItems: 'center', gap: 1 }}>
                <Box sx={{ flexGrow: 1, minWidth: 0 }}>
                  <Typography sx={{ fontWeight: 'fontWeightMedium', overflowWrap: 'anywhere' }}>{nameOf(item)}</Typography>
                  {noteOf && (
                    <Typography variant="caption" component="p" sx={{ color: 'text.secondary' }}>
                      {noteOf(item)}
                    </Typography>
                  )}
                </Box>
                <IconAction size="large" sx={button} data-direction="up" disabled={i === 0 || disabled} label={t('dashboard.customize.moveUp', { name: nameOf(item) })} onClick={() => move(i, -1)}>
                  <ArrowUp size={18} aria-hidden />
                </IconAction>
                <IconAction size="large" sx={button} data-direction="down" disabled={i === items.length - 1 || disabled} label={t('dashboard.customize.moveDown', { name: nameOf(item) })} onClick={() => move(i, 1)}>
                  <ArrowDown size={18} aria-hidden />
                </IconAction>
                <IconAction size="large" tone="error" sx={button} disabled={disabled} label={t('dashboard.customize.remove', { name: nameOf(item) })} onClick={() => onRemove(item)}>
                  <X size={18} aria-hidden />
                </IconAction>
              </Stack>
            </motion.li>
          ))}
        </AnimatePresence>
      </Stack>
    )
  }
  return (
    <section aria-labelledby={id}>
      <Typography id={id} component="h3" variant="h6" sx={{ mb: 1 }}>
        {label}
      </Typography>
      {body}
    </section>
  )
}
