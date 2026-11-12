import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { CalendarPlus, NotebookPen, Pencil, Replace, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ConfirmDialog } from '../components/ConfirmDialog.tsx'
import { IconAction } from '../components/IconAction.tsx'
import { DialogButtons, DialogCancel, DialogFrame } from '../dialogs/DialogFrame.tsx'
import { Field } from '../forms/Field.tsx'
import { FieldInput } from '../forms/FieldInput.tsx'
import {
  DeleteTyreDocument, DeleteTyrePlaceDocument, RestoreTyreDocument, RestoreTyrePlaceDocument, UpdateTyreDocument, type DistanceUnit,
} from '../gql/generated.ts'
import { useFormat } from '../i18n/format.ts'
import { ErrorMessage } from '../messages.tsx'
import { EntryDialog, TrashEntryDialog } from './EntryDialog.tsx'
import { isDeviceLine, isSwapLine, labelsOf, sharersOf, useEntryWords } from './entries.ts'
import { PlaceDialog } from './PlaceDialog.tsx'
import { ReplaceDialog } from './ReplaceDialog.tsx'
import { useCornerWords } from './singleTyres.ts'
import { type Tyres, useTyreMutation, useTyreToasts, useTyreWords } from './tyres.ts'

/**
 * A single tyre's dialog (#289), *Tyre {{label}}*: what it is now, its age, distance with its front/rear share, its repairs and what it cost
 * (its even share of a shared amount) and its label; its history newest first, its times on the car and its own lines each with Edit and a
 * bin (a line's amount saying whom else it is shared with), the set's own lines as *for the whole set*; *Add a time on the car*, *Add entry*
 * and *Move the tyre to the trash*. A retired tyre's has no edit buttons. `pending`: a change waiting on this device touched it, so its
 * figures are the server's to work out once synced; `costsPending` the same for what it cost.
 */
export function TyreDialog({
  open, onOpenChange, vehicleId, unit, tyres, tyreId, canLog, pending, costsPending = false,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  vehicleId: string
  unit: DistanceUnit
  tyres: Tyres
  tyreId: string
  canLog: boolean
  pending: boolean
  costsPending?: boolean
}) {
  const { t } = useTranslation()
  const format = useFormat()
  const words = useTyreWords(unit)
  const corners = useCornerWords()
  const entryWords = useEntryWords()
  const notify = useTyreToasts()
  const run = useTyreMutation(vehicleId)
  const info = tyres.sets.find((s) => (s.tyres ?? []).some((x) => x.tyre.id === tyreId))
  const tyre = info?.tyres.find((x) => x.tyre.id === tyreId)
  const [label, setLabel] = useState(tyre?.tyre.label ?? '')
  const [error, setError] = useState<unknown>()
  const [busy, setBusy] = useState(false)
  if (!info || !tyre) return null
  const { set } = info
  const editable = canLog && !tyre.tyre.isRetired
  const open_ = tyre.places.find((p) => p.place.until == null)
  const now = tyre.tyre.isRetired && tyre.tyre.retiredOn
    ? t('tyres.single.facts.retired', { date: format.date(tyre.tyre.retiredOn), why: t(`tyres.single.why.${tyre.tyre.retiredWhy ?? 'SET_RETIRED'}`) })
    : open_
      ? t('tyres.single.facts.onAt', { corner: corners.of(open_.place.corner), date: format.date(open_.place.from) })
      : t('tyres.single.facts.stored', { corner: corners.of(tyre.corner) })
  const { front, rear, distance, approximate } = tyre.figures
  const setLines = tyres.entries.filter((e) => e.setId === set.id && !isSwapLine(e) && !e.tyreId)
  const ownLines = tyres.entries.filter((e) => e.tyreId === tyre.tyre.id)
  type Row = { day: string; key: string; place?: (typeof tyre.places)[number]; line?: (typeof setLines)[number]; own?: boolean }
  const history: Row[] = [
    ...tyre.places.map((p): Row => ({ day: p.place.from, key: p.place.id, place: p })),
    ...ownLines.map((e): Row => ({ day: e.day, key: e.id, line: e, own: true })),
    ...setLines.map((e): Row => ({ day: e.day, key: e.id, line: e })),
  ].sort((a, b) => (a.day < b.day ? 1 : a.day > b.day ? -1 : 0))
  const spent = tyre.spent ?? []

  async function act(action: () => Promise<unknown>) {
    setBusy(true)
    setError(undefined)
    try {
      await action()
    } catch (err) {
      setError(err)
    } finally {
      setBusy(false)
    }
  }

  return (
    <DialogFrame
      open={open}
      onClose={() => onOpenChange(false)}
      onClosed={() => {
        setError(undefined)
        setLabel(tyre.tyre.label)
      }}
      busy={busy}
      maxWidth={560}
      title={t('tyres.single.tyreTitle', { label: tyre.tyre.label })}
      description={tyre.tyre.name ?? set.name}
    >
      {open && (
        <Stack sx={{ gap: 2 }}>
          <Box component="dl" sx={{ m: 0, display: 'grid', gridTemplateColumns: 'auto minmax(0,1fr)', gap: '6px 16px' }}>
            <Typography component="dt" variant="body2" sx={{ color: 'text.secondary' }}>
              {t('tyres.single.facts.now')}
            </Typography>
            <Typography component="dd" variant="body2" sx={{ m: 0 }}>
              {now}
            </Typography>
            <Typography component="dt" variant="body2" sx={{ color: 'text.secondary' }}>
              {t('tyres.age')}
            </Typography>
            <Typography component="dd" variant="body2" sx={{ m: 0 }}>
              {words.age(tyre.tyre)}
            </Typography>
            <Typography component="dt" variant="body2" sx={{ color: 'text.secondary' }}>
              {t('tyres.distance')}
            </Typography>
            <Box component="dd" sx={{ m: 0 }}>
              <Typography variant="body2" sx={{ fontWeight: 700 }}>
                {pending ? t('tyres.updatedOnSync') : words.distance(distance, approximate)}
              </Typography>
              {!pending && front + rear > 0 && (
                <>
                  <Box
                    role="img"
                    aria-label={t('tyres.single.facts.bar', { front: words.distance(front), rear: words.distance(rear) })}
                    sx={{ display: 'flex', height: 10, borderRadius: 5, overflow: 'hidden', gap: '2px', mt: 0.75, bgcolor: 'action.hover' }}
                  >
                    <Box sx={{ flexGrow: front, bgcolor: 'primary.main' }} />
                    <Box sx={{ flexGrow: rear, bgcolor: 'text.secondary' }} />
                  </Box>
                  <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                    {t('tyres.single.facts.frontRear', { front: words.distance(front), rear: words.distance(rear) })}
                  </Typography>
                </>
              )}
            </Box>
          </Box>

          <Box component="dl" sx={{ m: 0, display: 'grid', gridTemplateColumns: 'auto minmax(0,1fr)', gap: '6px 16px' }}>
            <Typography component="dt" variant="body2" sx={{ color: 'text.secondary' }}>
              {t('tyres.repairs')}
            </Typography>
            <Typography component="dd" variant="body2" sx={{ m: 0 }}>
              {costsPending ? t('tyres.updatedOnSync') : format.number(tyre.repairs ?? 0)}
            </Typography>
            <Typography component="dt" variant="body2" sx={{ color: 'text.secondary' }}>
              {t('tyres.spent')}
            </Typography>
            <Box component="dd" sx={{ m: 0 }}>
              <Typography variant="body2" sx={{ fontVariantNumeric: 'tabular-nums' }}>
                {costsPending ? t('tyres.updatedOnSync') : spent.length === 0 ? t('common.none') : spent.map((x) => format.money(x.amount, x.currency)).join(' · ')}
              </Typography>
              <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                {t('tyres.single.facts.spentNote')}
              </Typography>
            </Box>
          </Box>

          {editable && (
            <Stack direction="row" sx={{ gap: 1, alignItems: 'flex-end' }}>
              <Box sx={{ flex: 1, minWidth: 0 }}>
                <Field name="label" label={t('tyres.single.label')}>
                  <FieldInput value={label} maxLength={6} autoComplete="off" onChange={(e) => setLabel(e.target.value)} />
                </Field>
              </Box>
              <Button
                variant="soft"
                disabled={busy || label.trim() === tyre.tyre.label}
                onClick={() =>
                  void act(async () => {
                    const saved = await run(UpdateTyreDocument, { input: { id: tyre.tyre.id, label: label.trim() } })
                    notify.saved(saved, t('tyres.single.labelSaved'))
                  })
                }
              >
                {t('tyres.single.saveLabel')}
              </Button>
            </Stack>
          )}

          <Box>
            <Typography component="h3" variant="subtitle2" sx={{ mb: 0.5 }}>
              {t('tyres.history')}
            </Typography>
            <Box component="ul" sx={{ listStyle: 'none', m: 0, p: 0 }}>
              {history.map((row) => (
                <Box component="li" key={row.key} sx={{ display: 'flex', alignItems: 'center', gap: 1, py: 1, borderTop: 1, borderColor: 'divider', '&:first-of-type': { borderTop: 0 } }}>
                  <Box sx={{ flex: 1, minWidth: 0 }}>
                    {row.place ? (
                      <>
                        <Typography variant="body2" sx={{ fontWeight: 600 }}>
                          {t('tyres.single.placeLine', { corner: corners.of(row.place.place.corner) })}
                        </Typography>
                        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                          {t('tyres.single.placeRange', {
                            from: format.date(row.place.place.from),
                            until: row.place.place.until ? format.date(row.place.place.until) : t('tyres.dialogs.now'),
                            distance: row.place.place.version === 0 ? t('tyres.updatedOnSync') : words.distance(row.place.distance.distance, row.place.distance.approximate),
                          })}
                        </Typography>
                      </>
                    ) : row.line && row.own ? (
                      <>
                        <Typography variant="body2" sx={{ fontWeight: 600 }}>
                          {entryWords.kind(row.line.kind)}
                        </Typography>
                        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                          {[
                            format.date(row.line.day),
                            entryWords.amount(row.line) && (sharersOf(tyres, row.line).length > 0
                              ? `${entryWords.amount(row.line)}, ${t('tyres.entries.sharedWith', { tyres: labelsOf(tyres, sharersOf(tyres, row.line)) })}`
                              : entryWords.amount(row.line)),
                            row.line.note,
                          ].filter(Boolean).join(' · ')}
                        </Typography>
                      </>
                    ) : (
                      row.line && (
                        <>
                          <Typography variant="body2" sx={{ fontWeight: 600 }}>
                            {entryWords.kind(row.line.kind)}
                          </Typography>
                          <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                            {[format.date(row.line.day), set.brokenUpOn && row.line.day < set.brokenUpOn ? t('tyres.single.beforeBrokenUp') : t('tyres.single.forWholeSet'), row.line.note]
                              .filter(Boolean)
                              .join(' · ')}
                          </Typography>
                        </>
                      )
                    )}
                  </Box>
                  {row.line && row.own && editable && !isDeviceLine(row.line) && (
                    <>
                      <EntryDialog vehicleId={vehicleId} unit={unit} tyres={tyres} entry={row.line} trigger={
                          <IconAction label={`${t('tyres.entries.editTitle')}: ${entryWords.kind(row.line.kind)}, ${format.date(row.line.day)}`}>
                            <Pencil size={16} />
                          </IconAction>
                        }
                      />
                      <TrashEntryDialog vehicleId={vehicleId} unit={unit} tyres={tyres} entry={row.line} onError={setError} />
                    </>
                  )}
                  {row.place && editable && (
                    <>
                      <PlaceDialog vehicleId={vehicleId} tyres={tyres} tyreId={tyre.tyre.id} place={row.place.place} trigger={
                          <IconAction label={t('tyres.single.editPlace')}>
                            <Pencil size={16} />
                          </IconAction>
                        }
                      />
                      <IconAction
                        label={t('tyres.single.trashPlace', { from: format.date(row.place.place.from) })}
                        onClick={() => {
                          const place = row.place!.place
                          void act(async () => {
                            const saved = await run(DeleteTyrePlaceDocument, { id: place.id }, { tyreId: tyre.tyre.id, installationId: place.installationId })
                            notify.undoable(saved, t('tyres.single.placeTrashed'), () => run(RestoreTyrePlaceDocument, { id: place.id }))
                          })
                        }}
                      >
                        <Trash2 size={16} />
                      </IconAction>
                    </>
                  )}
                </Box>
              ))}
            </Box>
          </Box>

          {error !== undefined && <ErrorMessage error={error} />}
          {editable && (
            <Stack direction="row" sx={{ gap: 1, flexWrap: 'wrap' }}>
              <PlaceDialog vehicleId={vehicleId} tyres={tyres} tyreId={tyre.tyre.id} trigger={<Button variant="soft" startIcon={<CalendarPlus size={16} aria-hidden />}>{t('tyres.single.addPlace')}</Button>} />
              <EntryDialog vehicleId={vehicleId} unit={unit} tyres={tyres} tyreId={tyre.tyre.id} trigger={<Button variant="soft" startIcon={<NotebookPen size={16} aria-hidden />}>{t('tyres.entries.add')}</Button>} />
              {!set.isRetired && (
                <ReplaceDialog vehicleId={vehicleId} unit={unit} tyres={tyres} setId={set.id} tyreId={tyre.tyre.id}
                  trigger={<Button variant="soft" startIcon={<Replace size={16} aria-hidden />}>{t('tyres.replace.thisTyre')}</Button>} />
              )}
              <ConfirmDialog
                trigger={<Button variant="soft" color="error" startIcon={<Trash2 size={16} aria-hidden />}>{t('tyres.single.trashTyre')}</Button>}
                title={t('tyres.single.trashTyreTitle')}
                description={t('tyres.single.trashTyreText')}
                confirmLabel={t('tyres.single.trashTyre')}
                onConfirm={() =>
                  void act(async () => {
                    const saved = await run(DeleteTyreDocument, { id: tyre.tyre.id })
                    onOpenChange(false)
                    notify.undoable(saved, t('tyres.toast.trashed'), () => run(RestoreTyreDocument, { id: tyre.tyre.id }))
                  })
                }
              >
                <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                  {t('tyres.single.trashTyreRestore')}
                </Typography>
              </ConfirmDialog>
            </Stack>
          )}
          <DialogButtons>
            <DialogCancel label={t('common.close')} />
          </DialogButtons>
        </Stack>
      )}
    </DialogFrame>
  )
}
