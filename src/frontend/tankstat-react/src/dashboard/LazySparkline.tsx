import { lazy, Suspense } from 'react'

const Sparkline = lazy(() => import('./Sparkline.tsx').then((m) => ({ default: m.Sparkline })))

/** The chart library is large: it is only downloaded when a sparkline is actually shown (the space is reserved meanwhile). */
export function LazySparkline(props: { points: { month: string; amount: number }[]; currency?: string | null; height?: number; label?: string }) {
  return (
    <Suspense fallback={<div style={{ height: props.height ?? 36 }} aria-hidden />}>
      <Sparkline {...props} />
    </Suspense>
  )
}
