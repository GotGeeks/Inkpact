import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory } from 'vue-router'
import { createAppI18n, type Locale } from '../i18n'
import { createAppRouter } from '../router'
import { useNotificationsStore } from '../stores/notifications'
import { useTreeStore } from '../stores/tree'
import { fakeTree, outline } from '../testing/treeFixtures'
import NoticeToaster from './NoticeToaster.vue'

let wrapper: VueWrapper | null = null

afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

async function toaster(locale: Locale = 'en') {
  const server = fakeTree({ pages: outline(['Notes']), trash: outline(['Old']) })
  const pinia = createPinia()
  setActivePinia(pinia)
  await useTreeStore().init()
  const router = createAppRouter(createMemoryHistory())
  wrapper = mount(NoticeToaster, {
    global: { plugins: [pinia, createAppI18n(locale), router] },
    attachTo: document.body,
  })
  const notices = useNotificationsStore()
  const shown = async () => {
    await flushPromises()
    return [...document.body.querySelectorAll('[data-state="open"]')].map(
      (el) => el.textContent?.trim() ?? '',
    )
  }
  return { server, notices, shown, router }
}

describe('NoticeToaster', () => {
  it('says a page went to the trash, and offers to undo it', async () => {
    const { notices, shown, server } = await toaster()

    notices.post({ type: 'trashed', pageId: 'Notes', title: 'Notes' })
    const texts = await shown()

    expect(texts.join(' ')).toContain('“Notes” was moved to the trash.')
    const undo = [...document.body.querySelectorAll('button')].find(
      (b) => b.textContent?.trim() === 'Undo',
    )!
    undo.click()
    await flushPromises()
    expect(server.writes()).toEqual([
      { method: 'POST', path: '/api/pages/Notes/restore', body: {} },
    ])
  })

  it('says where offline edits that were deleted elsewhere are kept, and opens the copy', async () => {
    const { notices, shown, router } = await toaster()
    const push = vi.spyOn(router, 'push').mockResolvedValue(undefined)

    notices.post({ type: 'editsKept', pageId: 'copy-1', title: 'Plan' })
    const texts = await shown()

    expect(texts.join(' ')).toContain(
      'What you typed offline in “Plan” was removed by a change made elsewhere meanwhile. It is kept in a copy beside it.',
    )
    const open = [...document.body.querySelectorAll('button')].find(
      (b) => b.textContent?.trim() === 'Open the copy',
    )!
    open.click()
    expect(push).toHaveBeenCalledWith({ name: 'page', params: { pageId: 'copy-1' } })
  })

  it('says it in Dutch', async () => {
    const { notices, shown } = await toaster('nl')

    notices.post({ type: 'trashed', pageId: 'Notes', title: 'Notes' })

    expect((await shown()).join(' ')).toContain('“Notes” is naar de prullenbak verplaatst.')
  })

  it('shows "Untitled" for a page with no title', async () => {
    const { notices, shown } = await toaster()

    notices.post({ type: 'trashed', pageId: 'x', title: '' })

    expect((await shown()).join(' ')).toContain('“Untitled” was moved to the trash.')
  })

  it('says where a restored page went', async () => {
    const { notices, shown } = await toaster()

    notices.post({ type: 'restored', pageId: 'Old', title: 'Old', toTopLevel: false })
    notices.post({ type: 'restored', pageId: 'Old', title: 'Old', toTopLevel: true })
    const texts = (await shown()).join(' ')

    expect(texts).toContain('“Old” was restored.')
    expect(texts).toContain(
      '“Old” was restored to the top level, because the page it was in is gone.',
    )
  })

  it('does not offer to undo anything but a trash', async () => {
    const { notices, shown } = await toaster()

    notices.post({ type: 'restored', pageId: 'Old', title: 'Old', toTopLevel: false })
    notices.post({ type: 'error', code: 'move_cycle' })
    await shown()

    expect(
      [...document.body.querySelectorAll('button')].some((b) => b.textContent?.trim() === 'Undo'),
    ).toBe(false)
  })

  it('turns an error code into words, with the wait when there is one', async () => {
    const { notices, shown } = await toaster()

    notices.post({ type: 'error', code: 'move_cycle' })
    notices.post({ type: 'error', code: 'too_many_requests', retryAfterSeconds: 120 })
    const texts = (await shown()).join(' ')

    expect(texts).toContain('A page cannot be moved below itself.')
    expect(texts).toContain('Too many attempts from this device. Try again in 2 minutes.')
  })

  it('can be dismissed, and then is gone from the store', async () => {
    const { notices, shown } = await toaster()
    notices.post({ type: 'error', code: 'move_cycle' })
    await shown()

    ;(document.body.querySelector('button[aria-label="Dismiss"]') as HTMLButtonElement).click()
    await flushPromises()

    expect(notices.notices).toEqual([])
  })
})
