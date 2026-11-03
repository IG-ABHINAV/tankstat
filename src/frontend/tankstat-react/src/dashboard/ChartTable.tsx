import TableBody from '@mui/material/TableBody'
import TableCell from '@mui/material/TableCell'
import TableHead from '@mui/material/TableHead'
import TableRow from '@mui/material/TableRow'
import { Table2 } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { IconAction } from '../components/IconAction.tsx'
import { SurfaceTable } from '../components/SurfaceTable.tsx'
import { DialogButtons, DialogCancel, DialogFrame } from '../dialogs/DialogFrame.tsx'
import { DialogTrigger } from '../dialogs/DialogTrigger.tsx'
import { useDialogState } from '../dialogs/useDialogState.ts'
import type { DistanceUnit, VolumeUnit } from '../gql/generated.ts'
import { seriesId, type ChartData, type ChartRecipe } from './chartFormat.ts'
import { noData, useSeriesText } from './seriesText.ts'

type Units = { distance: DistanceUnit; volume: VolumeUnit }

/** A chart's numbers as a table (what a screen reader reads): a row per period (or category), a column per series, named by the chart. */
export function ChartTable({ data, recipe, units, title }: { data: ChartData; recipe: Pick<ChartRecipe, 'metric' | 'grouping'>; units: Units; title: string }) {
  const { t } = useTranslation()
  const { name, show, rows } = useSeriesText(data, recipe, units)
  return (
    <SurfaceTable caption={title}>
      <TableHead>
        <TableRow>
          <TableCell>{recipe.grouping === 'CATEGORY' ? t('dashboard.table.category') : t('dashboard.table.period')}</TableCell>
          {data.series.map((s) => (
            <TableCell key={seriesId(s)} align="right">
              {name(s)}
            </TableCell>
          ))}
        </TableRow>
      </TableHead>
      <TableBody>
        {rows.map((row) => (
          <TableRow key={String(row.key)}>
            <TableCell component="th" scope="row">
              {String(row.label)}
            </TableCell>
            {data.series.map((s) => (
              <TableCell key={seriesId(s)} align="right">
                {show(typeof row[seriesId(s)] === 'number' ? (row[seriesId(s)] as number) : null, s)}
              </TableCell>
            ))}
          </TableRow>
        ))}
      </TableBody>
    </SurfaceTable>
  )
}

/**
 * The button in a chart card's header that opens its table in a dialog (#228): named with the chart's title, so a page of charts has no
 * two buttons alike; nothing when there is nothing to list.
 */
export function ChartTableDialog({ data, recipe, units, title }: { data: ChartData; recipe: Pick<ChartRecipe, 'metric' | 'grouping'>; units: Units; title: string }) {
  const { t } = useTranslation()
  const [open, setOpen] = useDialogState()
  if (noData(data)) return null
  return (
    <>
      <DialogTrigger
        open={open}
        onOpen={() => setOpen(true)}
        trigger={
          <IconAction size="large" label={t('dashboard.tableButton', { title })}>
            <Table2 size={16} aria-hidden />
          </IconAction>
        }
      />
      <DialogFrame open={open} onClose={() => setOpen(false)} maxWidth={720} title={title} description={t('dashboard.tableDescription')}>
        {open && <ChartTable data={data} recipe={recipe} units={units} title={title} />}
        <DialogButtons>
          <DialogCancel label={t('common.close')} />
        </DialogButtons>
      </DialogFrame>
    </>
  )
}
