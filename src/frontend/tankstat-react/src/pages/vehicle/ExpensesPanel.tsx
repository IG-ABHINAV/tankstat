import { useApolloClient, useMutation } from '@apollo/client/react'
import Button from '@mui/material/Button'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { Pencil, Trash2 } from 'lucide-react'
import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ConfirmDialog } from '../../components/ConfirmDialog.tsx'
import { IconAction } from '../../components/IconAction.tsx'
import { UserChip } from '../../components/UserAvatar.tsx'
import { ExpenseFormDialog } from '../../ExpenseFormDialog.tsx'
import { anyAwaiting } from '../../recognition/review.ts'
import { outbox, usePendingCount } from '../../offline/outbox.ts'
import { undoTrash } from '../../offline/submitChange.ts'
import { KeepButton } from '../../components/KeepButton.tsx'
import { useLogChange } from '../../offline/useLogChange.ts'
import { ReviewBadge } from '../../recognition/ReviewState.tsx'
import {
  DeleteExpenseDocument,
  ExpensesDocument,
  RestoreExpenseDocument,
  UpdateExpenseDocument,
  type DistanceUnit,
  type ExpenseSortField,
  type ExpensesQuery,
  type ExpensesQueryVariables,
} from '../../gql/generated.ts'
import { ServerGrid, type GridColumn } from '../../grid/ServerGrid.tsx'
import { useLeavingRows } from '../../grid/useLeavingRows.ts'
import { useFormat } from '../../i18n/format.ts'
import { ErrorMessage } from '../../messages.tsx'
import { useToast } from '../../toast/toastContext.ts'
import { EXPENSE_QUERIES, useLogMutations } from './useLogMutations.ts'

type Row = ExpensesQuery['expenses'][number]

const refetch = { refetchQueries: EXPENSE_QUERIES, awaitRefetchQueries: true }

export function ExpensesPanel({ vehicle, canLog }: { vehicle: { id: string; units: { distance: DistanceUnit } }; canLog: boolean }) {
  const { t } = useTranslation()
  const format = useFormat()
  const add = useLogMutations(vehicle.id)
  const [updateExpense] = useMutation(UpdateExpenseDocument, refetch)
  const [deleteExpense] = useMutation(DeleteExpenseDocument, { ...refetch, refetchQueries: [...refetch.refetchQueries, 'ExpenseTrash'] })
  const [restoreExpense] = useMutation(RestoreExpenseDocument, { ...refetch, refetchQueries: [...refetch.refetchQueries, 'ExpenseTrash'] })
  const client = useApolloClient()
  usePendingCount(vehicle.id) // the rows' actions follow the changes waiting
  const changes = useLogChange('expenses', vehicle.id)
  const { leaving, leave } = useLeavingRows()
  const { toast, undoable } = useToast()
  const [actionError, setActionError] = useState<unknown>()
  const { units } = vehicle

  const columns = useMemo<GridColumn<Row, ExpensesQueryVariables, ExpenseSortField>[]>(() => {
    const none = t('common.none')
    return [
      {
        id: 'date',
        label: 'columns.date',
        hideable: false,
        mobile: true,
        sortField: 'DATE',
        // Always shown, so it also says when an expense waits for its photos or for someone to check what they showed.
        cell: (r) => (
          <Stack sx={{ alignItems: 'flex-start', gap: 0.5 }}>
            {format.date(r.date)}
            <ReviewBadge state={r.reviewState} />
          </Stack>
        ),
      },
      { id: 'title', label: 'columns.title', hideable: false, mobile: true, sortField: 'TITLE', cell: (r) => r.title },
      { id: 'amount', label: 'columns.amount', include: 'withAmount', mobile: true, sortField: 'AMOUNT', cell: (r) => (r.amount == null || !r.currency ? none : format.money(r.amount, r.currency)) },
      // What was taken off the amount (the amount is what was paid): hidden until wanted, not sortable.
      { id: 'discount', label: 'columns.discount', include: 'withDiscount', defaultHidden: true, cell: (r) => (r.discount == null || !r.currency ? none : format.money(r.discount, r.currency)) },
      { id: 'category', label: 'columns.category', include: 'withCategory', sortField: 'CATEGORY', cell: (r) => r.category ?? none },
      { id: 'odometer', label: 'columns.odometer', include: 'withOdometer', sortField: 'ODOMETER', cell: (r) => (r.odometer == null ? none : format.distance(r.odometer, units.distance)) },
      { id: 'note', label: 'columns.note', include: 'withNote', cell: (r) => r.note || none },
      { id: 'createdBy', label: 'columns.createdBy', include: 'withCreatedBy', sortField: 'CREATED_BY', cell: (r) => (r.createdBy ? <UserChip user={r.createdBy} /> : none) },
      // The recurring expenses a service visit covered when they were marked done together.
      { id: 'schedules', label: 'columns.schedules', include: 'withSchedules', defaultHidden: true, cell: (r) => (r.schedules?.length ? r.schedules.map((s) => s.title).join(', ') : none) },
    ]
  }, [t, format, units.distance])

  async function moveToTrash(row: Row) {
    setActionError(undefined)
    try {
      // Kept on the device while the server is out of reach: the row stays, marked "to be removed", and Undo takes the change back. One
      // added on this device and never sent is simply gone: there is nothing to undo on the server.
      const neverSent = outbox.markOf('expenses', row.id) === 'new'
      const done = await changes.trash(row.id, row.version, () => leave(row.id, () => deleteExpense({ variables: { id: row.id } })))
      if (neverSent) toast(t('offline.discarded'))
      else
        undoable(t('toast.expenseTrashed', { title: row.title }), () =>
          undoTrash(client, 'expenses', row.id, done.queued, () => changes.restore(row.id, row.version + 1, () => restoreExpense({ variables: { id: row.id } }))),
        )
    } catch (e) {
      setActionError(e)
    }
  }

  return (
    <>
      {actionError !== undefined && <ErrorMessage error={actionError} />}
      <ServerGrid
        gridId="expenses"
        caption={t('expenses.title')}
        query={ExpensesDocument}
        variables={{ vehicleId: vehicle.id }}
        select={(d) => ({ rows: d.expenses, total: d.expenseCount })}
        rowKey={(r) => r.id}
        columns={columns}
        defaultSort={{ column: 'date', direction: 'DESC' }}
        emptyText={t('expenses.empty')}
        pollWhile={anyAwaiting}
        leaving={leaving}
        syncState={{ entity: 'expenses', name: (r) => r.title }}
        toolbar={({ total }) => (
          <Stack direction="row" sx={{ alignItems: 'center', gap: 1.5, flexWrap: 'wrap' }}>
            {canLog && <ExpenseFormDialog vehicle={vehicle} trigger={<Button size="large">{t('expenses.add')}</Button>} onSubmit={add.expense} />}
            <Typography variant="body2" aria-live="polite" sx={{ color: 'text.secondary' }}>
              {t('expenses.countLabel', { count: total })}
            </Typography>
          </Stack>
        )}
        actions={(r) =>
          // Waiting on this device to be trashed: Keep takes that back; editing it meanwhile would change nothing.
          outbox.markOf('expenses', r.id) === 'deleted' ? (
            <KeepButton entity="expenses" id={r.id} name={r.title} />
          ) : r.canEdit ? (
            <Stack direction="row" sx={{ gap: 1, justifyContent: 'flex-end' }}>
              <ExpenseFormDialog
                vehicle={vehicle}
                expenseId={r.id}
                trigger={
                  <IconAction size="large" tone="primary" label={t('expenses.editAria', { title: r.title })}>
                    <Pencil size={16} aria-hidden />
                  </IconAction>
                }
                onSubmit={async (input) => void (await changes.update(r.id, r.version, { ...input, id: r.id }, () => updateExpense({ variables: { input: { ...input, id: r.id } } })))}
              />
              <ConfirmDialog
                trigger={
                  <IconAction size="large" tone="error" label={t('expenses.deleteAria', { title: r.title })}>
                    <Trash2 size={16} aria-hidden />
                  </IconAction>
                }
                title={t('expenses.trashTitle')}
                description={t('expenses.trashDescription', { title: r.title })}
                confirmLabel={t('expenses.trashConfirm')}
                onConfirm={() => void moveToTrash(r)}
              />
            </Stack>
          ) : (
            <Typography variant="body2" sx={{ color: 'text.secondary' }}>
              {t('expenses.viewOnly')}
            </Typography>
          )
        }
      />
    </>
  )
}
