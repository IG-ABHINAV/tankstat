import { useApolloClient } from '@apollo/client/react'
import Card from '@mui/material/Card'
import Stack from '@mui/material/Stack'
import Typography from '@mui/material/Typography'
import { useId, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import Button from '@mui/material/Button'
import { ConfirmDialog } from '../components/ConfirmDialog.tsx'
import { Loading } from '../components/Loading.tsx'
import type { DistanceUnit, ParkedChangeFieldsFragment, VolumeUnit } from '../gql/generated.ts'
import { usePageTitle } from '../hooks/usePageTitle.ts'
import { useFormat } from '../i18n/format.ts'
import { discardChange } from '../offline/submitChange.ts'
import { situationOf } from '../offline/conflicts.ts'
import { fromParked } from '../offline/syncKinds.ts'
import { useParkedChanges } from '../offline/useParkedChanges.ts'
import { useConnectivity } from '../offline/useConnectivity.ts'
import { usePushState } from '../offline/usePushState.ts'
import { useDescribed, useWaitingChanges, type WaitingChange } from '../offline/waitingChanges.ts'
import { useReasonText } from '../i18n/errors.ts'
import { canEdit } from '../offline/changes.ts'
import { EditChange } from './sync/EditChange.tsx'
import { FoundOnDevice } from './sync/FoundOnDevice.tsx'
import { ParkedAccount, ParkedActions } from './sync/ParkedDecision.tsx'
import type { Units } from './sync/useFieldText.ts'

/**
 * Waiting to sync: the changes made on this device that have not reached the server, per vehicle, in the order they were made, each with
 * what it does, its values and when it was saved. A change can be taken back (it is never sent); a vehicle added here takes its entries
 * along.
 */
export function SyncPage() {
  const { t } = useTranslation()
  const { dateTime } = useFormat()
  usePageTitle(t('sync.title'))
  const groups = useWaitingChanges()
  const { reachable } = useConnectivity()
  const { engine, state } = usePushState()
  const heading = useRef<HTMLHeadingElement>(null)
  const [status, setStatus] = useState('')
  const last = state.last
  const { parked } = useParkedChanges()
  const parkedById = new Map(parked?.map((p) => [p.id, p]) ?? [])
  const notApplied = useDescribed(parked?.flatMap((p) => fromParked(p) ?? []) ?? [], parked)
  const resolved = (message: string) => {
    setStatus(message)
    heading.current?.focus() // the item may be gone, its buttons with it
  }

  return (
    <section aria-labelledby="page-title">
      <Typography id="page-title" ref={heading} tabIndex={-1} component="h1" variant="h3" sx={{ mb: 1 }}>
        {t('sync.title')}
      </Typography>
      <Typography variant="body2" sx={{ color: 'text.secondary', mb: 2 }}>
        {t('sync.description')}
      </Typography>
      <Stack direction="row" sx={{ gap: 1.5, alignItems: 'center', flexWrap: 'wrap', mb: 2 }}>
        {engine && (
          <Button size="large" loading={state.status === 'syncing'} disabled={!reachable || !groups?.length} onClick={() => void engine.run()}>
            {t('sync.syncNow')}
          </Button>
        )}
        <Typography variant="body2" role="status">
          {state.status === 'syncing'
            ? t('sync.syncing')
            : last
              ? last.interrupted
                ? t('sync.interrupted')
                : t('sync.lastSync', { time: dateTime(new Date(last.at).toISOString()), applied: last.applied, parked: last.parked.length })
              : ''}
          {state.status !== 'syncing' && last && last.photosWaiting > 0 && ` ${t('sync.photosWaiting', { count: last.photosWaiting })}`}
          {state.status !== 'syncing' && last && last.photosLeftOut > 0 && ` ${t('sync.photosLeftOut', { count: last.photosLeftOut })}`}
        </Typography>
      </Stack>
      {!groups && <Loading />}
      {groups?.length === 0 && <Typography>{t('sync.empty')}</Typography>}
      <Stack sx={{ gap: 3 }}>
        {groups?.map((group) => (
          <Group
            key={group.vehicleId}
            name={group.vehicleName ?? t('sync.unknownVehicle')}
            units={{ distance: group.distanceUnit as DistanceUnit | null, volume: group.volumeUnit as VolumeUnit | null }}
            items={group.items}
            editable={state.status !== 'syncing'}
            onEdited={() => setStatus(t('sync.edited'))}
            onRemoved={() => {
              setStatus(t('sync.removed'))
              heading.current?.focus() // the button that had the focus is gone
            }}
          />
        ))}
      </Stack>
      <Typography variant="body2" role="status" sx={{ mt: 2 }}>
        {status}
      </Typography>
      <FoundOnDevice onDone={resolved} />
      {!reachable ? (
        <Typography variant="body2" sx={{ color: 'text.secondary', mt: 3 }}>
          {t('sync.parkedOffline')}
        </Typography>
      ) : (
        parked && parked.length > 0 && (
          <Stack component="section" aria-labelledby="not-applied" sx={{ gap: 1.5, mt: 3 }}>
            <div>
              <Typography id="not-applied" component="h2" variant="h5">
                {t('sync.notApplied')}
              </Typography>
              <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                {t('sync.notAppliedHint')}
              </Typography>
            </div>
            {notApplied?.map((group) => {
              // The server names the vehicle (the device may never have downloaded it).
              const vehicle = group.items.map((i) => parkedById.get(i.change.id)?.vehicle).find(Boolean)
              return (
                <Group
                  key={group.vehicleId}
                  name={vehicle?.name ?? group.vehicleName ?? t('sync.unknownVehicle')}
                  units={{
                    distance: (vehicle?.distanceUnit ?? group.distanceUnit) as DistanceUnit | null,
                    volume: (vehicle?.volumeUnit ?? group.volumeUnit) as VolumeUnit | null,
                  }}
                  items={group.items}
                  parked={parkedById}
                  onResolved={resolved}
                />
              )
            })}
          </Stack>
        )
      )}
    </section>
  )
}

function Group({
  name,
  units,
  items,
  editable = true,
  onEdited,
  onRemoved,
  parked,
  onResolved,
}: {
  name: string
  units: Units
  items: WaitingChange[]
  /** False while a sync runs: what is being sent is not edited meanwhile. */
  editable?: boolean
  onEdited?: () => void
  onRemoved?: () => void
  /** Changes the server parked: listed with why, who sent them, and Apply anyway / Discard instead of Remove. */
  parked?: Map<string, Parked>
  onResolved?: (message: string) => void
}) {
  const { t } = useTranslation()
  const id = useId()
  return (
    <Stack component="section" aria-labelledby={id} sx={{ gap: 1 }}>
      <Typography component="h2" variant="h5" id={id}>
        {name}{' '}
        <Typography component="span" variant="body2" sx={{ color: 'text.secondary' }}>
          {t('sync.groupCount', { count: items.length })}
        </Typography>
      </Typography>
      <Stack component="ul" sx={{ gap: 1, listStyle: 'none', p: 0, m: 0 }}>
        {items.map((item) => (
          <Item key={item.change.id} item={item} units={units} editable={editable} onEdited={onEdited} onRemoved={onRemoved} parked={parked?.get(item.change.id)} onResolved={onResolved} />
        ))}
      </Stack>
    </Stack>
  )
}

function Item({
  item,
  units,
  editable,
  onEdited,
  onRemoved,
  parked,
  onResolved,
}: {
  item: WaitingChange
  units: Units
  editable: boolean
  onEdited?: () => void
  onRemoved?: () => void
  parked?: Parked
  onResolved?: (message: string) => void
}) {
  const { t } = useTranslation()
  const reasonText = useReasonText()
  const format = useFormat()
  const client = useApolloClient()
  const { change } = item
  // Every entity and action a change can have has its text (a schedule is deleted, not trashed; only schedules are marked done).
  const kind: string = t(`sync.kinds.${change.entity}.${change.action}` as 'sync.kinds.refuelings.add')

  // What the change carries (or what it changes), in a line: "1 Sep 2026 · 40 L · €60.00", "Oil change · €35.00", "Golf".
  const money = (amount: number | null | undefined) => (amount != null && item.currency ? format.money(amount, item.currency) : null)
  const parts = [
    item.date ? format.date(item.date) : null,
    item.name ?? item.title ?? null,
    item.titles?.filter(Boolean).length ? format.list(item.titles.filter(Boolean)) : null,
    item.volume != null && units.volume ? format.volume(item.volume, units.volume) : null,
    money(item.totalCost) ?? money(item.amount),
  ].filter((p): p is string => !!p)
  const label = parts.length ? `${kind}, ${parts.join(', ')}` : kind
  // An edit of what is in the trash now, or gone, is not applied as edited: it comes back with its change, or is discarded.
  const editableAsParked = (p: Parked) => !['trashedMeanwhile', 'gone'].includes(situationOf(change, p.current, p.reason?.key) ?? '')

  return (
    <li>
      <Card sx={{ p: 1.5 }}>
        <Stack direction="row" sx={{ gap: 1.5, alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap' }}>
          <div>
            <Typography component="h3" variant="subtitle1">
              {kind}
            </Typography>
            {parts.length > 0 && <Typography variant="body2">{parts.join(' · ')}</Typography>}
            {parked?.reason && (
              // The same words as the refusal would have had online.
              <Typography variant="body2" sx={{ color: 'warning.main' }}>
                {reasonText(parked.reason)}
              </Typography>
            )}
            {parked && <ParkedAccount parked={parked} change={change} units={units} label={label} />}
            <Typography variant="caption" sx={{ color: 'text.secondary' }}>
              {parked
                ? t('sync.sentBy', { name: parked.submittedBy?.displayName ?? t('sync.someone'), time: format.dateTime(parked.receivedAt) })
                : t('sync.savedAt', { time: format.dateTime(new Date(change.createdAt).toISOString()) })}
            </Typography>
          </div>
          <Stack direction="row" sx={{ gap: 1, flexWrap: 'wrap' }}>
          {canEdit(change) && units.distance && (parked ? parked.canResolve && onResolved && editableAsParked(parked) : onEdited) && (
            <EditChange
              change={change}
              units={{ distance: units.distance, volume: units.volume ?? 'LITERS' }}
              label={label}
              parked={parked}
              disabled={!editable}
              onDone={(message) => (parked ? onResolved!(message) : onEdited!())}
            />
          )}
          {parked && onResolved && <ParkedActions parked={parked} change={change} label={label} onResolved={onResolved} />}
          {onRemoved && (
          <ConfirmDialog
            trigger={
              <Button variant="soft" color="error" size="large" aria-label={t('sync.removeAria', { change: label })}>
                {t('sync.remove')}
              </Button>
            }
            title={t('sync.removeTitle')}
            description={change.entity === 'vehicles' && change.action === 'add' ? t('sync.removeVehicleDescription') : t('sync.removeDescription')}
            confirmLabel={t('sync.remove')}
            onConfirm={() => void discardChange(client, change.id).then(onRemoved)}
          />
          )}
          </Stack>
        </Stack>
      </Card>
    </li>
  )
}

type Parked = ParkedChangeFieldsFragment


