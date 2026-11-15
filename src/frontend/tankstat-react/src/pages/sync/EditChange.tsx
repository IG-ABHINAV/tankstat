import { useApolloClient, useMutation, useQuery } from '@apollo/client/react'
import Button from '@mui/material/Button'
import { useTranslation } from 'react-i18next'
import type { ChangeEdit, MergeInfo } from '../../dialogs/changeEdit.ts'
import { ExpenseFormDialog, type ExpenseValues } from '../../ExpenseFormDialog.tsx'
import { RecurringExpensesDocument, ResolveSyncChangeDocument, type DistanceUnit, type ParkedChangeFieldsFragment, type VolumeUnit } from '../../gql/generated.ts'
import { keptPhotosOf, type Change } from '../../offline/changes.ts'
import { currentValues, merge, situationOf, type Current } from '../../offline/conflicts.ts'
import { outbox } from '../../offline/outbox.ts'
import { toChangeInput } from '../../offline/push.ts'
import { uuidV4 } from '../../offline/uuid.ts'
import { RecurringDoneDialog, type DoneValues } from '../../RecurringDoneDialog.tsx'
import { RecurringFormDialog, type RecurringValues } from '../../RecurringFormDialog.tsx'
import { RefuelingFormDialog, type RefuelingValues } from '../../RefuelingFormDialog.tsx'
import { VehicleFormDialog, type VehicleValues } from '../../VehicleFormDialog.tsx'
import { useFieldText } from './useFieldText.ts'

type Values = RefuelingValues | ExpenseValues | VehicleValues | RecurringValues | DoneValues

/**
 * Edit, on Waiting to sync, in the dialog of the entry the change concerns, starting from the values the change carries:
 * - a change waiting on this device stays on it, as an edit that folds into it (an add keeps its id and its photos);
 * - a change the server parked (`parked`) is applied with the edited values, as the person would online, against the version shown of what
 *   is there now (`current`); a refusal stays in the dialog with its reason (and the change stays parked with it).
 */
export function EditChange({
  change,
  units,
  label,
  parked,
  disabled,
  onDone,
}: {
  change: Change
  disabled?: boolean
  units: { distance: DistanceUnit; volume: VolumeUnit }
  /** What the change does and carries, for the button's name. */
  label: string
  parked?: ParkedChangeFieldsFragment
  onDone: (message: string) => void
}) {
  const { t } = useTranslation()
  const client = useApolloClient()
  const [resolve] = useMutation(ResolveSyncChangeDocument)
  const fields = useFieldText(units)

  const visit = change.action === 'markDone'
  const save = async (values: Values) => {
    // The values replace the change's own (the add's id and vehicle stay); photos are never part of an edit. A visit (#290, decision 71)
    // has no id of its own to keep, and a waiting one keeps its kept photos; a parked one has none (the server keeps none of a change).
    const input = visit
      ? { ...change.input, ...values, photoIds: parked ? undefined : change.input?.photoIds }
      : { ...change.input, ...values, id: change.targetId, photoIds: undefined }
    if (!parked && visit) {
      // Visits never fold: the edit takes the waiting visit's place (its id, order and kept photos stay).
      await outbox.replaceInput(change.id, input, (values as DoneValues).ids)
      void client.refetchQueries({ include: 'active' }).catch(() => undefined)
      onDone(t('sync.edited'))
      return
    }
    if (!parked) {
      await outbox.enqueue({ id: uuidV4(), entity: change.entity, action: 'update', vehicleId: change.vehicleId, targetId: change.targetId, input, expectedVersion: change.expectedVersion ?? null })
      void client.refetchQueries({ include: 'active' }).catch(() => undefined)
      onDone(t('sync.edited'))
      return
    }
    try {
      await resolve({
        // Applied over what the person sees now: changed once more meanwhile, it stays parked (none seen: over whatever is there).
        variables: { input: { id: parked.id, action: 'APPLY', change: toChangeInput({ ...change, input, expectedVersion: parked.current?.version ?? null, base: undefined }) } },
        refetchQueries: ['ParkedChanges'],
        awaitRefetchQueries: true,
      })
    } catch (error) {
      void client.refetchQueries({ include: ['ParkedChanges'] }).catch(() => undefined) // its new reason
      throw error
    }
    void client.refetchQueries({ include: 'active' }).catch(() => undefined)
    onDone(t('sync.appliedNow'))
  }

  // A parked edit of what was changed meanwhile is merged: the dialog starts from the merged values and says, under each field, what the
  // other side has.
  // A visit is no edit of what is there: it has nothing to merge with.
  const merging = parked && !visit && situationOf(change, parked.current, parked.reason?.key) === 'edited' ? mergeInfo(change, parked.current, fields) : null
  const edit: ChangeEdit<never> = merging
    ? { initial: { ...change.input, ...merging.values } as never, title: t('sync.merge.title'), submitLabel: t('sync.merge.apply'), merge: merging.info, targetId: change.targetId, parked: true }
    : {
        initial: change.input as never,
        title: parked ? t('sync.editApplyTitle') : t('sync.editTitle'),
        submitLabel: parked ? t('sync.applyEdited') : t('sync.saveChange'),
        mayWait: !parked && change.action === 'add' && keptPhotosOf(change).length > 0,
        targetId: change.targetId,
        parked: !!parked,
      }
  const trigger = merging ? (
    <Button variant="soft" size="large" disabled={disabled} aria-label={t('sync.merge.openAria', { change: label })}>
      {t('sync.merge.open')}
    </Button>
  ) : (
    <Button variant="soft" size="large" disabled={disabled} aria-label={parked ? t('sync.editApplyAria', { change: label }) : t('sync.editAria', { change: label })}>
      {parked ? t('sync.editApply') : t('sync.edit')}
    </Button>
  )
  const vehicle = { id: change.vehicleId, units }

  switch (change.entity) {
    case 'refuelings':
      return <RefuelingFormDialog trigger={trigger} vehicle={vehicle} change={edit} onSubmit={save} />
    case 'expenses':
      return <ExpenseFormDialog trigger={trigger} vehicle={vehicle} change={edit} onSubmit={save} />
    case 'vehicles':
      return <VehicleFormDialog trigger={trigger} change={edit} onSubmit={save} />
    case 'recurring':
      return visit ? (
        <VisitEdit trigger={trigger} vehicle={vehicle} edit={edit} onSubmit={save} />
      ) : (
        <RecurringFormDialog trigger={trigger} vehicleId={change.vehicleId} unit={units.distance} change={edit} onSubmit={save} />
      )
  }
}

/** A visit's edit (#290, decision 71): the Done dialog over the vehicle's schedules (kept on the device), the visit's ticked. */
function VisitEdit({
  trigger,
  vehicle,
  edit,
  onSubmit,
}: {
  trigger: React.ReactElement
  vehicle: { id: string; units: { distance: DistanceUnit } }
  edit: ChangeEdit<never>
  onSubmit: (values: DoneValues) => Promise<void>
}) {
  const schedules = useQuery(RecurringExpensesDocument, { variables: { vehicleId: vehicle.id } })
  return (
    <RecurringDoneDialog
      trigger={trigger}
      vehicle={vehicle}
      items={schedules.data?.vehicle?.recurring ?? []}
      selected={[]}
      change={edit as ChangeEdit<DoneValues>}
      onSubmit={async (values) => {
        await onSubmit(values)
        return undefined
      }}
    />
  )
}

/** The merged values of a parked edit and what the dialog says under each field: both sides of a field changed on both, in words. */
function mergeInfo(change: Change, current: Current, fields: ReturnType<typeof useFieldText>): { values: Record<string, unknown>; info: MergeInfo } {
  const mine = change.input ?? {}
  const theirs = currentValues(change.entity, current)
  const merged = merge(change.entity, change.base, mine, theirs)
  const info: MergeInfo = { conflicts: {}, merged: {} }
  for (const { field, as } of merged.fields) {
    if (as === 'both')
      info.conflicts[field] = { mine: { value: mine[field], text: fields.value(field, mine) }, theirs: { value: theirs[field], text: fields.value(field, theirs) } }
    else if (as === 'mine' || as === 'theirs') info.merged[field] = as
  }
  return { values: merged.values, info }
}
