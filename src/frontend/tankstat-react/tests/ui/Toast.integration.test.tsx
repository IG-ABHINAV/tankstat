import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import Button from '@mui/material/Button'
import { expect, it, vi } from 'vitest'
import { ThemeRoot } from '../../src/theme/ThemeRoot.tsx'
import { useToast } from '../../src/toast/toastContext.ts'

function Buttons({ undo }: { undo: () => Promise<unknown> }) {
  const { toast, undoable } = useToast()
  return (
    <>
      <Button onClick={() => toast('Saved.')}>Save</Button>
      <Button onClick={() => undoable('Moved to the trash.', undo)}>Trash</Button>
    </>
  )
}

const show = (undo: () => Promise<unknown>) =>
  render(
    <ThemeRoot instant>
      <Buttons undo={undo} />
    </ThemeRoot>,
  )

it('says a short message politely (a status, not an alert)', async () => {
  const ui = userEvent.setup()
  show(async () => undefined)

  await ui.click(screen.getByRole('button', { name: 'Save' }))

  expect(await screen.findByRole('status')).toHaveTextContent('Saved.')
  expect(screen.queryByRole('alert')).not.toBeInTheDocument()
})

it('Undo runs the undo and the message goes', async () => {
  const ui = userEvent.setup()
  const undo = vi.fn(async () => undefined)
  show(undo)

  await ui.click(screen.getByRole('button', { name: 'Trash' }))
  expect(await screen.findByRole('status')).toHaveTextContent('Moved to the trash.')
  await ui.click(screen.getByRole('button', { name: 'Undo' }))

  expect(undo).toHaveBeenCalledOnce()
  await waitFor(() => expect(screen.queryByText('Moved to the trash.')).not.toBeInTheDocument())
})

it('Undo tapped twice while the message goes runs the undo once (#243)', async () => {
  const undo = vi.fn(async () => undefined)
  show(undo)

  fireEvent.click(screen.getByRole('button', { name: 'Trash' }))
  // Two clicks with nothing awaited between them: a double tap (user-event waits between its clicks). The button stays while it fades out.
  fireEvent.click(await screen.findByRole('button', { name: 'Undo' }))
  fireEvent.click(screen.getByRole('button', { name: 'Undo' }))

  expect(undo).toHaveBeenCalledOnce()
})

it('Enter twice on Undo runs the undo once (#243)', async () => {
  // The two presses with no pause between them: the tests' theme has no transitions, so the message is gone a timer tick after the first
  // (where a browser fades it out for a moment), and a second press after that tick met the page instead (a race that load decided).
  const ui = userEvent.setup({ delay: null })
  const undo = vi.fn(async () => undefined)
  show(undo)

  await ui.click(screen.getByRole('button', { name: 'Trash' }))
  const button = await screen.findByRole('button', { name: 'Undo' })
  const keys = vi.fn()
  button.addEventListener('keydown', keys)
  button.focus()
  await ui.keyboard('{Enter}{Enter}')

  // Both reached the button (it stays while the message goes), else this would pass without the fix.
  expect(keys).toHaveBeenCalledTimes(2)
  expect(undo).toHaveBeenCalledOnce()
})

it('lets a tap on the message through to the page, only Undo takes one (#239)', async () => {
  const ui = userEvent.setup()
  show(async () => undefined)

  await ui.click(screen.getByRole('button', { name: 'Trash' }))
  const message = await screen.findByText('Moved to the trash.')

  // jsdom does not hit-test: user-event refuses a click on an element the pointer cannot reach.
  await expect(ui.click(message)).rejects.toThrow(/pointer-events/)
  expect(getComputedStyle(screen.getByRole('button', { name: 'Undo' })).pointerEvents).toBe('auto')
})

it('messages come one after the other', async () => {
  const ui = userEvent.setup()
  show(async () => undefined)

  await ui.click(screen.getByRole('button', { name: 'Trash' }))
  await ui.click(screen.getByRole('button', { name: 'Save' })) // queued behind the first
  expect(await screen.findByText('Moved to the trash.')).toBeInTheDocument()
  expect(screen.queryByText('Saved.')).not.toBeInTheDocument()

  await ui.click(screen.getByRole('button', { name: 'Undo' }))
  expect(await screen.findByText('Saved.')).toBeInTheDocument()
})

it('a failed undo says so at once, before anything still waiting', async () => {
  const ui = userEvent.setup()
  show(async () => {
    throw new Error('offline')
  })

  await ui.click(screen.getByRole('button', { name: 'Trash' }))
  await ui.click(screen.getByRole('button', { name: 'Save' }))
  await ui.click(await screen.findByRole('button', { name: 'Undo' }))

  expect(await screen.findByText('It could not be put back. Find it in the trash.')).toBeInTheDocument()
  expect(screen.getByRole('status')).toHaveTextContent('It could not be put back')
})

it('an error with nothing else showing shows at once', async () => {
  const ui = userEvent.setup()
  function Fail() {
    const { toast } = useToast()
    return <Button onClick={() => toast({ message: 'Something went wrong.', severity: 'error' })}>Fail</Button>
  }
  render(
    <ThemeRoot instant>
      <Fail />
    </ThemeRoot>,
  )

  await ui.click(screen.getByRole('button', { name: 'Fail' }))

  expect(await screen.findByRole('status')).toHaveTextContent('Something went wrong.')
})
