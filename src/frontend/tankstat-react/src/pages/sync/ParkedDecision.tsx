import { useApolloClient, useMutation, useQuery } from '@apollo/client/react'
import Button from '@mui/material/Button'
import Link from '@mui/material/Link'
import Stack from '@mui/material/Stack'
import TableBody from '@mui/material/TableBody'
import TableCell from '@mui/material/TableCell'
import TableHead from '@mui/material/TableHead'
import TableRow from '@mui/material/TableRow'
import Typography from '@mui/material/Typography'
import { useTranslation } from 'react-i18next'
import { Link as RouterLink } from 'react-router'
import { ConfirmDialog } from '../../components/ConfirmDialog.tsx'
import { SurfaceTable } from '../../components/SurfaceTable.tsx'
import { ResolveSyncChangeDocument, SessionDocument, type ParkedChangeFieldsFragment, type ResolveSyncChangeInput } from '../../gql/generated.ts'
import { useFormat } from '../../i18n/format.ts'
import { useErrorText, useReasonText } from '../../i18n/errors.ts'
import { createdBy, type Change } from '../../offline/changes.ts'
import { changedOnServer, currentValues, merge, situationOf, type Current } from '../../offline/conflicts.ts'
import { toChangeInput, unblockAdd } from '../../offline/push.ts'
import { canForce } from '../../offline/syncKinds.ts'
import { standaloneLink } from '../../theme/components.ts'
import { useFieldText, type Units } from './useFieldText.ts'

type Parked = ParkedChangeFieldsFragment

/** Where what a change concerns can be seen as it is now: its tab on the vehicle page, or the trash. */
function placeOf(change: Change, current: NonNullable<Current>): string {
  if (current.state === 'TRASHED') return '/trash'
  const tab = change.entity === 'vehicles' ? 'details' : change.entity
  return `/vehicles/${change.vehicleId}?tab=${tab}`
}

/**
 * What happened on each side of a parked edit or trash, in plain words: who changed it on the server, when and how (and what, when the
 * change knows what it was made from), what the device did, and where it stands now (already in the trash, gone, ...), with a link to it
 * as it is now. Nothing for other changes (adds, visits, photos), which say why they were not applied as before.
 */
export function ParkedAccount({ parked, change, units, label }: { parked: Parked; change: Change; units: Units; label: string }) {
  const { t } = useTranslation()
  const format = useFormat()
  const fields = useFieldText(units)
  const { current } = parked
  const situation = situationOf(change, current, parked.reason?.key)
  if (!situation) return null

  const who = (name: string | null | undefined) => name ?? t('sync.someone')
  const changed = changedOnServer(change.entity, change.base, current)
  const theirs = currentValues(change.entity, current)
  const offline = change.action === 'update' ? 'update' : change.entity === 'recurring' ? 'delete' : 'trash'
  const state =
    situation === 'alreadyTrashed'
      ? t('sync.conflict.alreadyTrashed')
      : situation === 'gone'
        ? t(change.entity === 'recurring' ? 'sync.conflict.goneSchedule' : 'sync.conflict.gone')
        : situation === 'trashedMeanwhile'
          ? t('sync.conflict.trashedMeanwhile')
          : null

  return (
    <Stack sx={{ gap: 0.25, my: 0.5 }}>
      {current && (
        <Typography variant="body2">
          {t(`sync.conflict.server.${current.lastChange ?? 'unknown'}` as 'sync.conflict.server.EDITED', {
            who: who(current.changedBy?.displayName),
            when: current.changedAt ? format.dateTime(current.changedAt) : t('sync.conflict.earlier'),
          })}
          {/* An edit to merge shows every field in its table. */}
          {changed.length > 0 && situation !== 'edited' && ` ${t('sync.conflict.changedThere', { fields: format.list(changed.map((f) => fields.pair(f, theirs))) })}`}
        </Typography>
      )}
      <Typography variant="body2">{t(`sync.conflict.offline.${offline}`, { who: who(parked.submittedBy?.displayName) })}</Typography>
      {state && <Typography variant="body2">{state}</Typography>}
      {situation === 'edited' && <MergeTable change={change} current={current} units={units} />}
      {current && (
        <Link component={RouterLink} to={placeOf(change, current)} variant="body2" aria-label={t('sync.conflict.openAria', { change: label })} sx={{ alignSelf: 'flex-start', ...standaloneLink }}>
          {t('sync.conflict.open')}
        </Link>
      )}
    </Stack>
  )
}

/**
 * A parked edit next to what is on the server now, every field it sets, and how each came together (changed on both sides, on one, or
 * the same): what Merge… starts from. The words carry the state, never the colour alone.
 */
function MergeTable({ change, current, units }: { change: Change; current: Current; units: Units }) {
  const { t } = useTranslation()
  const fields = useFieldText(units)
  const mine = change.input ?? {}
  const theirs = currentValues(change.entity, current)
  const { fields: rows } = merge(change.entity, change.base, mine, theirs)
  return (
    <SurfaceTable caption={t('sync.merge.caption')}>
      <TableHead>
        <TableRow>
          <TableCell>{t('sync.merge.field')}</TableCell>
          <TableCell>{t('sync.merge.mine')}</TableCell>
          <TableCell>{t('sync.merge.theirs')}</TableCell>
          <TableCell>{t('sync.merge.how')}</TableCell>
        </TableRow>
      </TableHead>
      <TableBody>
        {rows.map(({ field, as }) => (
          <TableRow key={field}>
            <TableCell component="th" scope="row">
              {fields.label(field)}
            </TableCell>
            <TableCell>{fields.value(field, mine)}</TableCell>
            <TableCell>{fields.value(field, theirs)}</TableCell>
            <TableCell sx={as === 'both' ? { color: 'warning.main', fontWeight: 600 } : { color: 'text.secondary' }}>{t(`sync.merge.state.${as}`)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </SurfaceTable>
  )
}

/**
 * Deciding about a parked change, worded for where it stands (`situationOf`): a trash of what was changed meanwhile goes to the trash
 * anyway or is kept; one of what is in the trash already is done; an edit of what is in the trash now comes back with it or stays there;
 * anything else is applied anyway (as the person would online: every rule applies, the version it was made from does not) or discarded.
 * Each confirmation says what the action does for this change. Only for whoever sent it or may change the vehicle.
 */
export function ParkedActions({ parked, change, label, onResolved }: { parked: Parked; change: Change; label: string; onResolved: (message: string) => void }) {
  const { t } = useTranslation()
  const reasonText = useReasonText()
  const errorText = useErrorText()
  const [resolve] = useMutation(ResolveSyncChangeDocument)
  const client = useApolloClient()
  const { data: session } = useQuery(SessionDocument, { fetchPolicy: 'cache-only' })
  if (!parked.canResolve) return null
  // Without sign-in every visitor is the anonymous user, the sender of every change.
  const me = session?.session.user?.id
  const bySender = !me || parked.submittedBy?.id === me
  const situation = situationOf(change, parked.current, parked.reason?.key)

  const run = async (input: Omit<ResolveSyncChangeInput, 'id'>) => {
    try {
      await resolve({ variables: { input: { id: parked.id, ...input } }, refetchQueries: ['ParkedChanges'], awaitRefetchQueries: true })
      // Decided: what was made on it here (when it was an add, or a tyre change that created sets and stretches) may go to the server now.
      void (async () => {
        for (const id of new Set([parked.targetId ?? '', ...createdBy(change)])) await unblockAdd(id) // one at a time: each rewrites the list
      })().catch(() => undefined)
      void client.refetchQueries({ include: 'active' }).catch(() => undefined)
      onResolved(t(input.action === 'APPLY' ? 'sync.appliedNow' : 'sync.discarded'))
    } catch (error) {
      // Refused again: it stays, with the new reason, or someone decided meanwhile; either way the list is asked afresh.
      void client.refetchQueries({ include: ['ParkedChanges'] }).catch(() => undefined)
      onResolved(t('sync.stillNotApplied', { reason: errorText(error) }))
    }
  }

  /** One action: its button, and a confirmation that says what it does. */
  const action = (name: string, description: string, input: Omit<ResolveSyncChangeInput, 'id'>, tone: 'primary' | 'error' = 'primary') => (
    <ConfirmDialog
      key={name}
      trigger={
        <Button variant="soft" color={tone} size="large" aria-label={t('sync.conflict.actionAria', { action: name, change: label })}>
          {name}
        </Button>
      }
      title={t('sync.conflict.confirmTitle', { action: name })}
      description={description}
      confirmLabel={name}
      onConfirm={() => void run(input)}
    />
  )
  /** Discard, as it always was named; `description` says what it does for this change. */
  const discard = (description = t('sync.discardDescription')) => (
    <ConfirmDialog
      key="discard"
      trigger={
        <Button variant="soft" color="error" size="large" aria-label={t('sync.discardAria', { change: label })}>
          {t('sync.discard')}
        </Button>
      }
      title={t('sync.discardTitle')}
      description={description}
      confirmLabel={t('sync.discard')}
      onConfirm={() => void run({ action: 'DISCARD' })}
    />
  )
  /** A discard named for what it keeps (Keep it, Leave it in the trash). */
  const keep = (name: string, description: string) => action(name, description, { action: 'DISCARD' }, 'error')

  let buttons
  switch (situation) {
    case 'alreadyTrashed':
      // Nothing left to decide: the server answers a trash of what is in the trash as applied.
      buttons = [
        <Button key="done" variant="soft" size="large" aria-label={t('sync.conflict.actionAria', { action: t('sync.conflict.done'), change: label })} onClick={() => void run({ action: 'APPLY' })}>
          {t('sync.conflict.done')}
        </Button>,
      ]
      break
    case 'changed':
    case 'restored':
      buttons =
        change.entity === 'recurring'
          ? [action(t('sync.conflict.deleteAnyway'), t('sync.conflict.deleteAnywayDescription'), { action: 'APPLY' }), keep(t('sync.conflict.keep'), t('sync.conflict.keepDescription'))]
          : [
              action(t(situation === 'restored' ? 'sync.conflict.trashAgain' : 'sync.conflict.trashAnyway'), t('sync.conflict.trashAnywayDescription'), { action: 'APPLY' }),
              keep(t('sync.conflict.keep'), t('sync.conflict.keepDescription')),
            ]
      break
    case 'trashedMeanwhile':
      buttons = [
        action(t('sync.conflict.restoreWithChange'), t('sync.conflict.restoreWithChangeDescription'), {
          action: 'APPLY',
          restoreFirst: true,
          // The restore is made from the version seen here: trashed or restored once more meanwhile, it stays parked.
          change: toChangeInput({ ...change, expectedVersion: parked.current?.version ?? null, base: undefined }),
        }),
        keep(t('sync.conflict.leaveInTrash'), t('sync.conflict.leaveInTrashDescription')),
      ]
      break
    case 'gone':
      buttons = [discard()]
      break
    case 'edited':
      // Merge… (next to these, `EditChange`) or one side as a whole.
      buttons = [
        action(t('sync.merge.useMine'), t('sync.merge.useMineDescription'), { action: 'APPLY' }),
        keep(t('sync.merge.keepServers'), t('sync.merge.keepServersDescription')),
      ]
      break
    default: {
      // A swap another device's swap got to first (#287): sent again, it takes that one's place, as the dialog offers online.
      const replaces = change.entity === 'tyres' && parked.reason?.key === 'tyres.swapExists' && !!change.input && 'replace' in change.input
      buttons = [
        replaces &&
          action(t('tyres.dialogs.replaceSwap'), t('sync.conflict.replaceSwapDescription'), {
            action: 'APPLY',
            change: toChangeInput({ ...change, input: { ...change.input, replace: true } }),
          }),
        !replaces && canForce(parked.reason?.key, bySender, parked.current) && (
          <ConfirmDialog
            key="apply"
            trigger={
              <Button variant="soft" size="large" aria-label={t('sync.applyAria', { change: label })}>
                {t('sync.apply')}
              </Button>
            }
            title={t('sync.applyTitle')}
            description={t('sync.applyDescription', { reason: reasonText(parked.reason) })}
            confirmLabel={t('sync.apply')}
            onConfirm={() => void run({ action: 'APPLY' })}
          />
        ),
        discard(),
      ]
    }
  }

  return <Stack direction="row" sx={{ gap: 1, flexWrap: 'wrap' }}>{buttons}</Stack>
}
