import { useApolloClient, useMutation } from '@apollo/client/react'
import Box from '@mui/material/Box'
import Button from '@mui/material/Button'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { useCallback, useMemo, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router'
import { ConfirmDialog } from '../components/ConfirmDialog.tsx'
import { TabbedPanels } from '../components/TabbedPanels.tsx'
import { UserChip } from '../components/UserAvatar.tsx'
import { VehiclePicture } from '../components/VehiclePicture.tsx'
import {
  EmptyExpenseTrashDocument,
  EmptyRefuelingTrashDocument,
  ExpenseTrashDocument,
  RestoreExpenseDocument,
  EmptyTrashDocument,
  RefuelingTrashDocument,
  RestoreRefuelingDocument,
  RestoreVehicleDocument,
  TrashDocument,
  EmptyTyreTrashDocument,
  RestoreTyreSetDocument,
  RestoreTyreEntryDocument,
  TyreTrashDocument,
  type TyreTrashQuery,
  type TyreTrashQueryVariables,
  type TyreTrashSortField,
  type ExpenseSortField,
  type ExpenseTrashQuery,
  type ExpenseTrashQueryVariables,
  type RefuelingSortField,
  type RefuelingTrashQuery,
  type RefuelingTrashQueryVariables,
  type TrashQuery,
  type TrashQueryVariables,
  type VehicleSortField,
} from '../gql/generated.ts'
import { ServerGrid, type GridColumn } from '../grid/ServerGrid.tsx'
import { usePageTitle } from '../hooks/usePageTitle.ts'
import { useFormat } from '../i18n/format.ts'
import { ErrorMessage, SuccessMessage } from '../messages.tsx'
import type { LogEntity } from '../offline/changes.ts'
import { submitChange } from '../offline/submitChange.ts'
import { uuidV4 } from '../offline/uuid.ts'

type VehicleRow = TrashQuery['trash'][number]
type LogRow = RefuelingTrashQuery['refuelingTrash'][number]
type ExpenseRow = ExpenseTrashQuery['expenseTrash'][number]
type TyreRow = TyreTrashQuery['tyreTrash'][number]

const refetch = { refetchQueries: ['Vehicles', 'Trash', 'Refuelings', 'RefuelingTrash', 'Expenses', 'ExpenseTrash', 'VehicleDetails'], awaitRefetchQueries: true }

export function TrashPage() {
  const { t } = useTranslation()
  usePageTitle(t('trash.title'))
  const [params, setParams] = useSearchParams()
  const requested = params.get('tab')
  const tab = requested === 'refuelings' || requested === 'expenses' || requested === 'tyres' ? requested : 'vehicles'
  const [actionError, setActionError] = useState<unknown>()
  const [notice, setNotice] = useState<ReactNode>()

  async function run(action: () => Promise<unknown>) {
    setActionError(undefined)
    setNotice(undefined)
    try {
      await action()
    } catch (e) {
      setActionError(e)
    }
  }

  return (
    <section aria-labelledby="page-title">
      <Typography id="page-title" component="h1" variant="h3" sx={{ mb: 1 }}>
        {t('trash.title')}
      </Typography>
      {actionError !== undefined && <ErrorMessage error={actionError} />}
      {notice && <SuccessMessage>{notice}</SuccessMessage>}
      <TabbedPanels
        label={t('trash.title')}
        value={tab}
        onChange={(value) => {
          setNotice(undefined)
          setParams(value === 'vehicles' ? {} : { tab: value }, { replace: true })
        }}
        tabs={[
          { value: 'vehicles', label: t('trash.tabs.vehicles'), content: () => <VehicleTrash run={run} setNotice={setNotice} /> },
          { value: 'refuelings', label: t('trash.tabs.refuelings'), content: () => <RefuelingTrash run={run} setNotice={setNotice} /> },
          { value: 'expenses', label: t('trash.tabs.expenses'), content: () => <ExpenseTrash run={run} setNotice={setNotice} /> },
          { value: 'tyres', label: t('trash.tabs.tyres'), content: () => <TyreTrash run={run} setNotice={setNotice} /> },
        ]}
      />
    </section>
  )
}

interface Shared {
  run: (action: () => Promise<unknown>) => Promise<void>
  setNotice: (notice: ReactNode) => void
}

/** "Empty trash" removes only what the person may delete for good; the rest stays and is explained. */
function EmptyButton({ total, deletable, title, description, onConfirm }: { total: number; deletable: number; title: string; description: string; onConfirm: () => void }) {
  const { t } = useTranslation()
  return (
    <Box>
      <ConfirmDialog
        trigger={
          <Button size="large" color="error" variant="soft" disabled={deletable === 0}>
            {t('trash.emptyAction')}
          </Button>
        }
        title={title}
        description={deletable < total ? t('trash.emptyPartial', { count: deletable }) : description}
        confirmLabel={t('trash.emptyAction')}
        onConfirm={onConfirm}
      />
    </Box>
  )
}

function VehicleTrash({ run, setNotice }: Shared) {
  const { t } = useTranslation()
  const { dateTime } = useFormat()
  const [restore] = useMutation(RestoreVehicleDocument, refetch)
  const [emptyTrash] = useMutation(EmptyTrashDocument, refetch)

  const columns = useMemo<GridColumn<VehicleRow, TrashQueryVariables, VehicleSortField>[]>(() => {
    const none = t('common.none')
    return [
      {
        id: 'name',
        label: 'columns.name',
        hideable: false,
        mobile: true,
        sortField: 'NAME',
        cell: (r) => (
          <Stack component="span" direction="row" sx={{ display: 'inline-flex', alignItems: 'center', gap: 1.5 }}>
            <VehiclePicture url={r.pictureUrl} name={r.name} width={40} />
            {r.name}
          </Stack>
        ),
      },
      { id: 'licensePlate', label: 'columns.licensePlate', include: 'withLicensePlate', sortField: 'LICENSE_PLATE', cell: (r) => r.licensePlate ?? none },
      { id: 'fuelType', label: 'columns.fuelType', include: 'withFuelType', sortField: 'FUEL_TYPE', cell: (r) => (r.fuelType ? t(`fuel.${r.fuelType}`) : none) },
      { id: 'owner', label: 'columns.owner', include: 'withOwner', sortField: 'OWNER', cell: (r) => (r.owner ? <UserChip user={r.owner} /> : none) },
      { id: 'deletedAt', label: 'columns.deletedAt', include: 'withDeletedAt', mobile: true, sortField: 'DELETED_AT', cell: (r) => (r.deletedAt ? dateTime(r.deletedAt) : none) },
    ]
  }, [t, dateTime])

  return (
    <ServerGrid
      gridId="trash"
      caption={t('trash.tabs.vehicles')}
      query={TrashDocument}
      select={(d) => ({ rows: d.trash, total: d.trashCount })}
      rowKey={(r) => r.id}
      columns={columns}
      defaultSort={{ column: 'deletedAt', direction: 'DESC' }}
      emptyText={t('trash.empty')}
      toolbar={({ total, data }) => (
        <EmptyButton
          total={total}
          deletable={data?.trashDeletableCount ?? 0}
          title={t('trash.emptyTitle')}
          description={t('trash.emptyDescription', { count: total })}
          onConfirm={() => void run(async () => setNotice(t('trash.emptied', { count: (await emptyTrash()).data?.emptyTrash ?? 0 })))}
        />
      )}
      actions={(v) => (
        <Button variant="soft" aria-label={t('trash.restoreAria', { name: v.name })} onClick={() => void run(() => restore({ variables: { id: v.id } }))}>
          {t('trash.restore')}
        </Button>
      )}
    />
  )
}

function RefuelingTrash({ run, setNotice }: Shared) {
  const { t } = useTranslation()
  const format = useFormat()
  const [restore] = useMutation(RestoreRefuelingDocument, refetch)
  const restoreLog = useRestoreLog()
  const [emptyTrash] = useMutation(EmptyRefuelingTrashDocument, refetch)

  const columns = useMemo<GridColumn<LogRow, RefuelingTrashQueryVariables, RefuelingSortField>[]>(() => {
    const none = t('common.none')
    return [
      { id: 'date', label: 'columns.date', hideable: false, mobile: true, sortField: 'DATE', cell: (r) => format.date(r.date) },
      { id: 'vehicle', label: 'columns.vehicle', mobile: true, sortField: 'VEHICLE', cell: (r) => r.vehicle?.name ?? none },
      { id: 'volume', label: 'columns.volume', include: 'withVolume', sortField: 'VOLUME', cell: (r) => (r.volume == null || !r.vehicle ? none : format.volume(r.volume, r.vehicle.units.volume)) },
      { id: 'cost', label: 'columns.cost', include: 'withCost', sortField: 'TOTAL_COST', cell: (r) => (r.totalCost == null || !r.currency ? none : format.money(r.totalCost, r.currency)) },
      { id: 'odometer', label: 'columns.odometer', include: 'withOdometer', sortField: 'ODOMETER', cell: (r) => (r.odometer == null || !r.vehicle ? none : format.distance(r.odometer, r.vehicle.units.distance)) },
      { id: 'createdBy', label: 'columns.createdBy', include: 'withCreatedBy', sortField: 'CREATED_BY', cell: (r) => (r.createdBy ? <UserChip user={r.createdBy} /> : none) },
      { id: 'deletedAt', label: 'columns.deletedAt', include: 'withDeletedAt', mobile: true, sortField: 'DELETED_AT', cell: (r) => (r.deletedAt ? format.dateTime(r.deletedAt) : none) },
    ]
  }, [t, format])

  return (
    <ServerGrid
      gridId="refueling-trash"
      caption={t('trash.tabs.refuelings')}
      query={RefuelingTrashDocument}
      select={(d) => ({ rows: d.refuelingTrash, total: d.refuelingTrashCount })}
      rowKey={(r) => r.id}
      columns={columns}
      defaultSort={{ column: 'deletedAt', direction: 'DESC' }}
      emptyText={t('trash.refuelingsEmpty')}
      toolbar={({ total, data }) => (
        <EmptyButton
          total={total}
          deletable={data?.refuelingTrashDeletableCount ?? 0}
          title={t('trash.emptyTitle')}
          description={t('trash.refuelingDescription', { count: total })}
          onConfirm={() => void run(async () => setNotice(t('trash.emptiedRefuelings', { count: (await emptyTrash()).data?.emptyRefuelingTrash ?? 0 })))}
        />
      )}
      actions={(r) => (
        <Button variant="soft" aria-label={t('trash.restoreAria', { name: format.date(r.date) })} onClick={() => void run(() => restoreLog('refuelings', r, () => restore({ variables: { id: r.id } })))}>
          {t('trash.restore')}
        </Button>
      )}
    />
  )
}

function ExpenseTrash({ run, setNotice }: Shared) {
  const { t } = useTranslation()
  const format = useFormat()
  const [restore] = useMutation(RestoreExpenseDocument, refetch)
  const restoreLog = useRestoreLog()
  const [emptyTrash] = useMutation(EmptyExpenseTrashDocument, refetch)

  const columns = useMemo<GridColumn<ExpenseRow, ExpenseTrashQueryVariables, ExpenseSortField>[]>(() => {
    const none = t('common.none')
    return [
      { id: 'date', label: 'columns.date', hideable: false, mobile: true, sortField: 'DATE', cell: (r) => format.date(r.date) },
      { id: 'title', label: 'columns.title', hideable: false, mobile: true, sortField: 'TITLE', cell: (r) => r.title },
      { id: 'vehicle', label: 'columns.vehicle', sortField: 'VEHICLE', cell: (r) => r.vehicle?.name ?? none },
      { id: 'amount', label: 'columns.amount', include: 'withAmount', sortField: 'AMOUNT', cell: (r) => (r.amount == null || !r.currency ? none : format.money(r.amount, r.currency)) },
      { id: 'category', label: 'columns.category', include: 'withCategory', sortField: 'CATEGORY', cell: (r) => r.category ?? none },
      { id: 'createdBy', label: 'columns.createdBy', include: 'withCreatedBy', sortField: 'CREATED_BY', cell: (r) => (r.createdBy ? <UserChip user={r.createdBy} /> : none) },
      { id: 'deletedAt', label: 'columns.deletedAt', include: 'withDeletedAt', mobile: true, sortField: 'DELETED_AT', cell: (r) => (r.deletedAt ? format.dateTime(r.deletedAt) : none) },
    ]
  }, [t, format])

  return (
    <ServerGrid
      gridId="expense-trash"
      caption={t('trash.tabs.expenses')}
      query={ExpenseTrashDocument}
      select={(d) => ({ rows: d.expenseTrash, total: d.expenseTrashCount })}
      rowKey={(r) => r.id}
      columns={columns}
      defaultSort={{ column: 'deletedAt', direction: 'DESC' }}
      emptyText={t('trash.expensesEmpty')}
      toolbar={({ total, data }) => (
        <EmptyButton
          total={total}
          deletable={data?.expenseTrashDeletableCount ?? 0}
          title={t('trash.emptyTitle')}
          description={t('trash.expenseDescription', { count: total })}
          onConfirm={() => void run(async () => setNotice(t('trash.emptiedExpenses', { count: (await emptyTrash()).data?.emptyExpenseTrash ?? 0 })))}
        />
      )}
      actions={(r) => (
        <Button variant="soft" aria-label={t('trash.restoreAria', { name: r.title })} onClick={() => void run(() => restoreLog('expenses', r, () => restore({ variables: { id: r.id } })))}>
          {t('trash.restore')}
        </Button>
      )}
    />
  )
}

/**
 * Tyre sets and tyre log lines in the trash (#287, #288): a set comes back with its installations and lines, a line alone (a swap's with its
 * installation); a restore that no longer fits the history is refused with the server's reason (`tyres.restoreConflict`), shown in the
 * page's alert. Online only.
 */
function TyreTrash({ run, setNotice }: Shared) {
  const { t } = useTranslation()
  const format = useFormat()
  const tyreRefetch = { refetchQueries: ['TyreTrash', 'VehicleTyres'], awaitRefetchQueries: true }
  const [restoreSet] = useMutation(RestoreTyreSetDocument, tyreRefetch)
  const [restoreEntry] = useMutation(RestoreTyreEntryDocument, tyreRefetch)
  const [emptyTrash] = useMutation(EmptyTyreTrashDocument, tyreRefetch)
  const what = useCallback(
    (r: TyreRow) =>
      r.kind === 'SET'
        ? t('trash.tyres.set', { name: r.setName })
        : r.setName
          ? t('trash.tyres.entry', { kind: t(`tyres.entries.kinds.${r.entryKind ?? 'OTHER'}`), set: r.setName })
          : t('trash.tyres.entryNoSet', { kind: t(`tyres.entries.kinds.${r.entryKind ?? 'OTHER'}`) }),
    [t],
  )

  const columns = useMemo<GridColumn<TyreRow, TyreTrashQueryVariables, TyreTrashSortField>[]>(() => {
    const none = t('common.none')
    return [
      { id: 'what', label: 'trash.tyres.what', hideable: false, mobile: true, cell: what },
      {
        id: 'when',
        label: 'trash.tyres.when',
        cell: (r) =>
          r.kind === 'SET'
            ? t('trash.tyres.withInstallations', { count: r.installationCount })
            : !r.day
              ? none
              : r.entryKind !== 'SWAP' && r.entryKind !== 'POSITIONS_CHANGED'
                ? t('trash.tyres.day', { day: format.date(r.day) })
                : r.until
                  ? t('trash.tyres.swapUntil', { day: format.date(r.day), until: format.date(r.until) })
                  : t('trash.tyres.swapNow', { day: format.date(r.day) }),
      },
      { id: 'vehicle', label: 'columns.vehicle', cell: (r) => r.vehicle?.name ?? none },
      { id: 'deletedAt', label: 'columns.deletedAt', mobile: true, sortField: 'DELETED_AT', cell: (r) => format.dateTime(r.deletedAt) },
    ]
  }, [t, format, what])

  return (
    <ServerGrid
      gridId="tyre-trash"
      caption={t('trash.tabs.tyres')}
      query={TyreTrashDocument}
      select={(d) => ({ rows: d.tyreTrash, total: d.tyreTrashCount })}
      rowKey={(r) => r.id}
      columns={columns}
      defaultSort={{ column: 'deletedAt', direction: 'DESC' }}
      emptyText={t('trash.tyresEmpty')}
      toolbar={({ total, data }) => (
        <EmptyButton
          total={total}
          deletable={data?.tyreTrashDeletableCount ?? 0}
          title={t('trash.emptyTitle')}
          description={t('trash.tyreDescription', { count: total })}
          onConfirm={() => void run(async () => setNotice(t('trash.emptiedTyres', { count: (await emptyTrash()).data?.emptyTyreTrash ?? 0 })))}
        />
      )}
      actions={(r) => (
        <Button
          variant="soft"
          aria-label={t('trash.restoreAria', { name: what(r) })}
          onClick={() => void run(() => (r.kind === 'SET' ? restoreSet({ variables: { id: r.id } }) : restoreEntry({ variables: { id: r.id } })))}
        >
          {t('trash.restore')}
        </Button>
      )}
    />
  )
}

/** Restoring a log goes through `submitChange`: kept on the device while the server is out of reach, like every change of a log. */
function useRestoreLog() {
  const client = useApolloClient()
  return <T,>(entity: LogEntity, row: { id: string; version: number; vehicle: { id: string } | null }, send: () => Promise<T>) =>
    row.vehicle
      ? submitChange(client, { id: uuidV4(), entity, action: 'restore', vehicleId: row.vehicle.id, targetId: row.id, expectedVersion: row.version }, send)
      : send() // a vehicle that is gone: nothing to keep it for
}
