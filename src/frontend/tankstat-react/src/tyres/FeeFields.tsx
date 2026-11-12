import { useQuery } from '@apollo/client/react'
import Box from '@mui/material/Box'
import Stack from '@mui/material/Stack'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { CurrencyInput } from '../forms/CurrencyInput.tsx'
import { Field } from '../forms/Field.tsx'
import { FieldAutocomplete } from '../forms/FieldAutocomplete.tsx'
import { FieldInput } from '../forms/FieldInput.tsx'
import { ExpenseCategoriesDocument, LogDefaultsDocument } from '../gql/generated.ts'
import { parseDecimal } from '../i18n/format.ts'
import type { Tyres } from './tyres.ts'

/**
 * What a tyre log line cost (#288): the amount with its currency, as the expense form has them, and the expense's category, always there
 * (the owner's decision 7), prefilled with the vehicle's last tyre cost's category, else *Tyres* in the current language (decision 6).
 * Named `feeAmount`, `feeCurrency` and `feeCategory` in the form (`feeOf` reads them). An amount makes an ordinary expense on the Expenses
 * tab; `linked` says the line has one, which an emptied amount moves to the trash.
 */
export function FeeFields({
  vehicleId,
  tyres,
  label,
  amount,
  currency,
  category,
  linked = false,
  onAmount,
}: {
  vehicleId: string
  tyres: Tyres
  /** *Paid*, *Paid for the swap* or *Price*. */
  label: string
  amount?: number | null
  currency?: string | null
  category?: string | null
  linked?: boolean
  /** Told what is typed in the amount (the set dialog asks *Bought on* once there is a price). */
  onAmount?: (typed: string) => void
}) {
  const { t } = useTranslation()
  const defaults = useQuery(LogDefaultsDocument, { variables: { vehicleId }, fetchPolicy: 'cache-first' })
  const categories = useQuery(ExpenseCategoriesDocument, { variables: { vehicleId }, fetchPolicy: 'cache-first' })
  const [typed, setTyped] = useState(amount == null ? '' : String(amount))
  const usual = currency ?? defaults.data?.logDefaults?.currency ?? ''
  const [code, setCode] = useState(usual)
  const shownCode = code || usual // the vehicle's usual currency once its starting values are in
  const paid = typed.trim() !== ''
  return (
    <Stack sx={{ gap: 1.5 }}>
      <Stack direction="row" sx={{ gap: 1.5, flexWrap: 'wrap' }}>
        <Box sx={{ flex: '2 1 8rem', minWidth: 0 }}>
          <Field
            name="feeAmount"
            label={label}
            hint={[t('tyres.fee.amountHint'), linked ? t('tyres.fee.emptyHint') : ''].filter(Boolean).join(' ')}
            invalid={{ message: t('forms.numberInvalid'), test: (v) => v !== '' && parseDecimal(v) === undefined }}
          >
            <FieldInput
              inputMode="decimal"
              autoComplete="off"
              value={typed}
              onChange={(e) => {
                setTyped(e.target.value)
                onAmount?.(e.target.value)
              }}
            />
          </Field>
        </Box>
        <Box sx={{ flex: '1 1 6rem', minWidth: 0 }}>
          <Field
            name="feeCurrency"
            label={t('expenses.fields.currency')}
            required={paid}
            invalid={{ message: t('errors.money.currencyInvalid'), test: (v) => v !== '' && !/^[A-Za-z]{3}$/.test(v.trim()) }}
          >
            <CurrencyInput value={shownCode} onChange={setCode} preferred={usual} />
          </Field>
        </Box>
      </Stack>
      <Field name="feeCategory" label={t('tyres.fee.category')} hint={t('tyres.fee.categoryHint')}>
        <FieldAutocomplete
          options={categories.data?.expenseCategories ?? []}
          defaultValue={category ?? tyres.expenseCategory ?? t('tyres.fee.defaultCategory')}
          maxLength={60}
          openOnFocus
        />
      </Field>
    </Stack>
  )
}
