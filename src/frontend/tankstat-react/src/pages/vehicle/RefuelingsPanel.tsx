import { useApolloClient, useMutation } from '@apollo/client/react'
import Button from '@mui/material/Button'
import Chip from '@mui/material/Chip'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { Pencil, Trash2 } from 'lucide-react'
import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ConfirmDialog } from '../../components/ConfirmDialog.tsx'
import { IconAction } from '../../components/IconAction.tsx'
import { UserChip } from '../../components/UserAvatar.tsx'
import {
  DeleteRefuelingDocument,
  RefuelingsDocument,
  RestoreRefuelingDocument,
  UpdateRefuelingDocument,
  type DistanceUnit,
  type RefuelingSortField,
  type RefuelingsQuery,
  type RefuelingsQueryVariables,
  type VolumeUnit,
} from '../../gql/generated.ts'
import { ServerGrid, type GridColumn } from '../../grid/ServerGrid.tsx'
import { useLeavingRows } from '../../grid/useLeavingRows.ts'
import { useFormat } from '../../i18n/format.ts'
import { ErrorMessage } from '../../messages.tsx'
import { RefuelingFormDialog } from '../../RefuelingFormDialog.tsx'
import { anyAwaiting } from '../../recognition/review.ts'
import { outbox, usePendingCount } from '../../offline/outbox.ts'
import { undoTrash } from '../../offline/submitChange.ts'
import { KeepButton } from '../../components/KeepButton.tsx'
import { useLogChange } from '../../offline/useLogChange.ts'
import { ReviewBadge } from '../../recognition/ReviewState.tsx'
import { useToast } from '../../toast/toastContext.ts'
import { REFUELING_QUERIES, useLogMutations } from './useLogMutations.ts'

type Row = RefuelingsQuery['refuelings'][number]

const refetch = { refetchQueries: REFUELING_QUERIES, awaitRefetchQueries: true }

export function RefuelingsPanel({
  vehicle,
  canLog,
}: {
  vehicle: { id: string; units: { distance: DistanceUnit; volume: VolumeUnit } }
  canLog: boolean
}) {
  const { t } = useTranslation()
  const format = useFormat()
  const add = useLogMutations(vehicle.id)
  const [updateRefueling] = useMutation(UpdateRefuelingDocument, refetch)
  const [deleteRefueling] = useMutation(DeleteRefuelingDocument, { ...refetch, refetchQueries: [...refetch.refetchQueries, 'RefuelingTrash'] })
  const [restoreRefueling] = useMutation(RestoreRefuelingDocument, { ...refetch, refetchQueries: [...refetch.refetchQueries, 'RefuelingTrash'] })
  const client = useApolloClient()
  usePendingCount(vehicle.id) // the rows' actions follow the changes waiting
  const changes = useLogChange('refuelings', vehicle.id)
  const { leaving, leave } = useLeavingRows()
  const { toast, undoable } = useToast()
  const [actionError, setActionError] = useState<unknown>()
  const { units } = vehicle

  const columns = useMemo<GridColumn<Row, RefuelingsQueryVariables, RefuelingSortField>[]>(() => {
    const none = t('common.none')
    return [
      {
        id: 'date',
        label: 'columns.date',
        hideable: false,
        mobile: true,
        sortField: 'DATE',
        // Always shown, so it also says when a log waits for its photos or for someone to check what they showed.
        cell: (r) => (
          <Stack sx={{ alignItems: 'flex-start', gap: 0.5 }}>
            {format.date(r.date)}
            <ReviewBadge state={r.reviewState} />
          </Stack>
        ),
      },
      { id: 'volume', label: 'columns.volume', include: 'withVolume', mobile: true, sortField: 'VOLUME', cell: (r) => (r.volume == null ? none : format.volume(r.volume, units.volume)) },
      { id: 'cost', label: 'columns.cost', include: 'withCost', mobile: true, sortField: 'TOTAL_COST', cell: (r) => (r.totalCost == null || !r.currency ? none : format.money(r.totalCost, r.currency)) },
      {
        id: 'price',
        label: 'columns.price',
        include: 'withPrice',
        sortField: 'PRICE_PER_UNIT',
        cell: (r) => (r.pricePerUnit == null || !r.currency ? none : format.pricePerUnit(r.pricePerUnit, r.currency, units.volume)),
      },
      // What the till took off and the pump's price: hidden until wanted (most logs have no discount); not sortable, the server orders by the paid price only.
      { id: 'discount', label: 'columns.discount', include: 'withDiscount', defaultHidden: true, cell: (r) => (r.discount == null || !r.currency ? none : format.money(r.discount, r.currency)) },
      {
        id: 'listPrice',
        label: 'columns.listPrice',
        include: 'withListPrice',
        defaultHidden: true,
        cell: (r) => (r.pricePerUnitBeforeDiscount == null || !r.currency ? none : format.pricePerUnit(r.pricePerUnitBeforeDiscount, r.currency, units.volume)),
      },
      {
        id: 'consumption',
        label: 'columns.consumption',
        include: 'withConsumption',
        sortField: 'CONSUMPTION',
        cell: (r) => (r.consumption == null ? none : format.consumption(r.consumption, units)),
      },
      { id: 'odometer', label: 'columns.odometer', include: 'withOdometer', sortField: 'ODOMETER', cell: (r) => (r.odometer == null ? none : format.distance(r.odometer, units.distance)) },
      {
        id: 'fullTank',
        label: 'columns.fullTank',
        include: 'withFullTank',
        cell: (r) =>
          r.isFullTank == null ? (
            none
          ) : (
            <Stack sx={{ alignItems: 'flex-start', gap: 0.5 }}>
              {r.isFullTank ? t('refuelings.full') : <Chip color="warning" label={t('refuelings.partial')} />}
              {r.missedPreviousFillUp && <Chip color="neutral" label={t('refuelings.missedBefore')} />}
            </Stack>
          ),
      },
      { id: 'note', label: 'columns.note', include: 'withNote', cell: (r) => r.note || none },
      { id: 'createdBy', label: 'columns.createdBy', include: 'withCreatedBy', sortField: 'CREATED_BY', cell: (r) => (r.createdBy ? <UserChip user={r.createdBy} /> : none) },
    ]
  }, [t, format, units])

  async function moveToTrash(row: Row) {
    setActionError(undefined)
    try {
      // Kept on the device while the server is out of reach: the row stays, marked "to be removed", and Undo takes the change back. One
      // added on this device and never sent is simply gone: there is nothing to undo on the server.
      const neverSent = outbox.markOf('refuelings', row.id) === 'new'
      const done = await changes.trash(row.id, row.version, () => leave(row.id, () => deleteRefueling({ variables: { id: row.id } })))
      if (neverSent) toast(t('offline.discarded'))
      else
        undoable(t('toast.refuelingTrashed', { date: format.date(row.date) }), () =>
          undoTrash(client, 'refuelings', row.id, done.queued, () => changes.restore(row.id, row.version + 1, () => restoreRefueling({ variables: { id: row.id } }))),
        )
    } catch (e) {
      setActionError(e)
    }
  }

  return (
    <>
      {actionError !== undefined && <ErrorMessage error={actionError} />}
      <ServerGrid
        gridId="refuelings"
        caption={t('refuelings.title')}
        query={RefuelingsDocument}
        variables={{ vehicleId: vehicle.id }}
        select={(d) => ({ rows: d.refuelings, total: d.refuelingCount })}
        rowKey={(r) => r.id}
        columns={columns}
        defaultSort={{ column: 'date', direction: 'DESC' }}
        emptyText={t('refuelings.empty')}
        pollWhile={anyAwaiting}
        leaving={leaving}
        syncState={{ entity: 'refuelings', name: (r) => format.date(r.date) }}
        toolbar={({ total }) => (
          <Stack direction="row" sx={{ alignItems: 'center', gap: 1.5, flexWrap: 'wrap' }}>
            {canLog && <RefuelingFormDialog vehicle={vehicle} trigger={<Button size="large">{t('refuelings.add')}</Button>} onSubmit={add.refuel} />}
            <Typography variant="body2" aria-live="polite" sx={{ color: 'text.secondary' }}>
              {t('refuelings.countLabel', { count: total })}
            </Typography>
          </Stack>
        )}
        actions={(r) =>
          // Waiting on this device to be trashed: Keep takes that back; editing it meanwhile would change nothing.
          outbox.markOf('refuelings', r.id) === 'deleted' ? (
            <KeepButton entity="refuelings" id={r.id} name={format.date(r.date)} />
          ) : r.canEdit ? (
            <Stack direction="row" sx={{ gap: 1, justifyContent: 'flex-end' }}>
              <RefuelingFormDialog
                vehicle={vehicle}
                refuelingId={r.id}
                trigger={
                  <IconAction size="large" tone="primary" label={t('refuelings.editAria', { date: format.date(r.date) })}>
                    <Pencil size={16} aria-hidden />
                  </IconAction>
                }
                onSubmit={async (input) => void (await changes.update(r.id, r.version, { ...input, id: r.id }, () => updateRefueling({ variables: { input: { ...input, id: r.id } } })))}
              />
              <ConfirmDialog
                trigger={
                  <IconAction size="large" tone="error" label={t('refuelings.deleteAria', { date: format.date(r.date) })}>
                    <Trash2 size={16} aria-hidden />
                  </IconAction>
                }
                title={t('refuelings.trashTitle')}
                description={t('refuelings.trashDescription', { date: format.date(r.date) })}
                confirmLabel={t('refuelings.trashConfirm')}
                onConfirm={() => void moveToTrash(r)}
              />
            </Stack>
          ) : (
            <Typography variant="body2" sx={{ color: 'text.secondary' }}>
              {t('refuelings.viewOnly')}
            </Typography>
          )
        }
      />
    </>
  )
}
