import { useTranslation } from 'react-i18next'
import { useFormat } from '../i18n/format.ts'

/** What a rise means for the figure: spending or fuel going up is `worse`; more distance is neither good nor bad (`neutral`). */
export type Meaning = 'worseUp' | 'neutral'
/** What the change means, its arrow's colour (#228): never alone, the arrow's direction and the words say the same. */
export type Tone = 'bad' | 'good' | 'neutral' | 'same'

/** A trend figure's comparison in words, its arrow and its tone, drawn as its line (`ChangeLine`) or as its arrow alone (`ChangeArrow`). */
export interface ChangeWords {
  sentence: string
  icon: 'up' | 'down' | 'same'
  tone: Tone
}

/** What `useChangeWords` words: a figure's recent and earlier values and how to say them. */
export interface ChangeOf {
  recent: number | null | undefined
  earlier: number | null | undefined
  fuel?: boolean
  than: string
  meaning: Meaning
  size?: (value: number) => string
  wrap?: (sentence: string) => string
  none: string
  noneRecent?: string
}

/**
 * A change in words with an arrow, coloured by what the change means (up is red for spending and fuel, green down; distance is neutral
 * either way) but never by colour alone: "HUF 60,000 (12%) more than the 12 months before", `than` being what the recent window is
 * compared with (`trendPeriod.ts` `comparedWith`). `size` writes the difference in the figure's unit; left out (consumption, `fuel`), the
 * per cent alone says it; `wrap` adds to a comparison (the fill-ups compared). Nothing to compare with yet is said so (`none`), and so is a
 * recent window with nothing in it (`noneRecent`), which is the other way round; both with a dash, in grey.
 */
export function useChangeWords(): (of: ChangeOf) => ChangeWords {
  const { t } = useTranslation()
  const format = useFormat()
  return ({ recent, earlier, fuel = false, than, meaning, size, wrap, none, noneRecent }) => {
    if (recent == null) return { sentence: earlier != null && noneRecent ? noneRecent : none, icon: 'same', tone: 'same' }
    if (earlier == null || earlier === 0) return { sentence: none, icon: 'same', tone: 'same' }
    const diff = recent - earlier
    const percent = format.number(Math.abs(diff) / earlier, { style: 'percent', maximumFractionDigits: 0 })
    const change = size ? `${size(Math.abs(diff))} (${percent})` : percent
    const way = diff > 0 ? 'more' : diff < 0 ? 'less' : 'same'
    const sentence = t(`dashboard.trend.${way}${fuel ? 'Fuel' : ''}`, { change, than })
    return {
      sentence: wrap ? wrap(sentence) : sentence,
      icon: diff > 0 ? 'up' : diff < 0 ? 'down' : 'same',
      tone: diff === 0 ? 'same' : meaning === 'neutral' ? 'neutral' : (diff > 0) === (meaning === 'worseUp') ? 'bad' : 'good',
    }
  }
}
