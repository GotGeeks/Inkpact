import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createAppI18n } from '../i18n'
import { useTreeStore } from '../stores/tree'
import { fakeTree, outline } from '../testing/treeFixtures'
import MovePageDialog from './MovePageDialog.vue'

const pages = outline(['A', '  A1', '    A1a', '  A2', 'B', 'C'])

let wrapper: VueWrapper | null = null

afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

async function open(pageId: string | null) {
  const server = fakeTree({ pages })
  const pinia = createPinia()
  setActivePinia(pinia)
  await useTreeStore().init()
  wrapper = mount(MovePageDialog, {
    props: { pageId },
    global: { plugins: [pinia, createAppI18n('en')] },
    attachTo: document.body,
  })
  await flushPromises()
  const options = () => [
    ...document.body.querySelectorAll<HTMLButtonElement>('[role="dialog"] ul button'),
  ]
  return { server, options }
}

describe('MovePageDialog', () => {
  it('is closed without a page', async () => {
    await open(null)

    expect(document.body.querySelector('[role="dialog"]')).toBeNull()
  })

  it('names the page and lists the top level and every page it could go under, indented, without itself or what is below it', async () => {
    const { options } = await open('A1')

    expect(document.body.querySelector('[role="dialog"]')?.textContent).toContain('Move “A1” to')
    expect(options().map((button) => button.textContent?.trim())).toEqual([
      'Top level',
      'A',
      'A2',
      'B',
      'C',
    ])
  })

  it('leaves out a whole branch for its top page', async () => {
    const { options } = await open('A')

    expect(options().map((button) => button.textContent?.trim())).toEqual(['Top level', 'B', 'C'])
  })

  it('moves the page to the chosen one, as its last child, and closes', async () => {
    const { options, server } = await open('A1')

    options()
      .find((button) => button.textContent?.trim() === 'B')!
      .click()
    await flushPromises()

    expect(server.writes()).toEqual([
      {
        method: 'POST',
        path: '/api/pages/A1/move',
        body: { parentId: 'B', afterId: null, beforeId: null, expectedVersion: 1 },
      },
    ])
    expect(wrapper!.emitted('close')).toHaveLength(1)
  })

  it('moves a page to the top level', async () => {
    const { options, server } = await open('A1')

    options()[0]!.click()
    await flushPromises()

    expect(server.writes()[0]).toMatchObject({
      path: '/api/pages/A1/move',
      body: { parentId: null },
    })
  })

  it('does not ask the server to move a page to where it already is', async () => {
    const { options, server } = await open('C')

    options()[0]!.click() // C is already the last page at the top level
    await flushPromises()

    expect(server.writes()).toEqual([])
    expect(wrapper!.emitted('close')).toHaveLength(1)
  })

  it('closes without moving on Cancel', async () => {
    const { server } = await open('A1')

    ;[...document.body.querySelectorAll<HTMLButtonElement>('[role="dialog"] button')]
      .find((b) => b.textContent?.trim() === 'Cancel')!
      .click()
    await flushPromises()

    expect(server.writes()).toEqual([])
    expect(wrapper!.emitted('close')).toHaveLength(1)
  })

  it('closes with Escape', async () => {
    await open('A1')

    document.body
      .querySelector('[role="dialog"]')!
      .dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
    await flushPromises()

    expect(wrapper!.emitted('close')).toBeDefined()
  })
})
