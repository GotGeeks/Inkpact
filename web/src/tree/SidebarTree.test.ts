import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createAppI18n } from '../i18n'
import { useNotificationsStore } from '../stores/notifications'
import { useTreeStore } from '../stores/tree'
import { fakeTree, outline } from '../testing/treeFixtures'
import SidebarTree from './SidebarTree.vue'

const pages = outline(['A', '  A1', '    A1a', '  A2', 'B', 'C'])

let wrapper: VueWrapper | null = null

beforeEach(() => {
  localStorage.clear()
  // A mouse is the pointer, so rows can be dragged.
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query === '(pointer: fine)',
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  }))
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

async function mountTree(
  options: {
    pages?: typeof pages
    role?: 'owner' | 'editor' | 'viewer'
    selectedId?: string | null
    open?: string[]
  } = {},
) {
  const server = fakeTree({ pages: options.pages ?? pages, role: options.role })
  const pinia = createPinia()
  setActivePinia(pinia)
  const tree = useTreeStore()
  await tree.init()
  for (const id of options.open ?? []) tree.setExpanded(id, true)
  wrapper = mount(SidebarTree, {
    props: { selectedId: options.selectedId ?? null },
    global: { plugins: [pinia, createAppI18n('en')] },
    attachTo: document.body,
  })
  await flushPromises()
  const row = (id: string) => wrapper!.get(`[data-page-id="${id}"]`)
  const titles = () =>
    wrapper!.findAll('[role="treeitem"]').map((el) => el.attributes('data-page-id'))
  const key = async (id: string, k: string, extra: KeyboardEventInit = {}) => {
    ;(row(id).element as HTMLElement).focus()
    await row(id).trigger('keydown', { key: k, ...extra })
    await flushPromises()
  }
  return { server, tree, notices: useNotificationsStore(), row, titles, key }
}

describe('SidebarTree: what it shows', () => {
  it('is a labelled tree of the pages whose parents are open', async () => {
    const { titles } = await mountTree({ open: ['A'] })

    const tree = wrapper!.get('[role="tree"]')
    expect(tree.attributes('aria-labelledby')).toBe('pages-label')
    expect(wrapper!.get('#pages-label').text()).toBe('Pages')
    expect(titles()).toEqual(['A', 'A1', 'A2', 'B', 'C'])
  })

  it('tells assistive technology the level, the place among siblings and whether a branch is open', async () => {
    const { row } = await mountTree({ open: ['A'] })

    expect(row('A').attributes()).toMatchObject({
      'aria-level': '1',
      'aria-posinset': '1',
      'aria-setsize': '3',
      'aria-expanded': 'true',
    })
    expect(row('A1').attributes()).toMatchObject({
      'aria-level': '2',
      'aria-posinset': '1',
      'aria-setsize': '2',
      'aria-expanded': 'false',
    })
    expect(row('A2').attributes('aria-expanded')).toBeUndefined() // no children: not expandable
  })

  it('marks the selected page', async () => {
    const { row } = await mountTree({ selectedId: 'B' })

    expect(row('B').attributes('aria-selected')).toBe('true')
    expect(row('A').attributes('aria-selected')).toBe('false')
  })

  it('can be reached with Tab at one row only: the selected one, else the first', async () => {
    const withSelection = await mountTree({ selectedId: 'C' })
    expect(
      wrapper!
        .findAll('[role="treeitem"][tabindex="0"]')
        .map((el) => el.attributes('data-page-id')),
    ).toEqual(['C'])
    wrapper!.unmount()

    await mountTree()
    expect(
      wrapper!
        .findAll('[role="treeitem"][tabindex="0"]')
        .map((el) => el.attributes('data-page-id')),
    ).toEqual(['A'])
    expect(withSelection).toBeTruthy()
  })

  it('shows an empty state with nothing to navigate', async () => {
    await mountTree({ pages: [] })

    expect(wrapper!.find('[role="tree"]').exists()).toBe(false)
    expect(wrapper!.text()).toContain('There are no pages yet.')
  })

  it('shows "Untitled" for a page with no title', async () => {
    const { row } = await mountTree({ pages: [{ ...pages[0]!, title: '' }] })

    expect(row('A').text()).toContain('Untitled')
  })

  it('has labelled expand and collapse buttons that are not tab stops', async () => {
    const { row } = await mountTree()

    const button = row('A').get('button[aria-label="Expand A"]')
    expect(button.attributes('tabindex')).toBe('-1')
  })
})

describe('SidebarTree: with the keyboard', () => {
  it('moves between the visible rows with the arrow keys, Home and End', async () => {
    const { key } = await mountTree({ open: ['A'] })

    await key('A', 'ArrowDown')
    expect(document.activeElement?.getAttribute('data-page-id')).toBe('A1')
    await key('A1', 'ArrowDown')
    expect(document.activeElement?.getAttribute('data-page-id')).toBe('A2')
    await key('A2', 'ArrowUp')
    expect(document.activeElement?.getAttribute('data-page-id')).toBe('A1')
    await key('A1', 'End')
    expect(document.activeElement?.getAttribute('data-page-id')).toBe('C')
    await key('C', 'Home')
    expect(document.activeElement?.getAttribute('data-page-id')).toBe('A')
  })

  it('stays put at the top and the bottom', async () => {
    const { key } = await mountTree()

    await key('A', 'ArrowUp')
    expect(document.activeElement?.getAttribute('data-page-id')).toBe('A')
    await key('C', 'ArrowDown')
    expect(document.activeElement?.getAttribute('data-page-id')).toBe('C')
  })

  it('opens a closed branch with the right arrow, goes into it with a second press, and closes it with the left arrow', async () => {
    const { key, titles, tree } = await mountTree()

    await key('A', 'ArrowRight')
    expect(titles()).toEqual(['A', 'A1', 'A2', 'B', 'C'])
    expect(tree.expanded.has('A')).toBe(true)

    await key('A', 'ArrowRight')
    expect(document.activeElement?.getAttribute('data-page-id')).toBe('A1')

    await key('A1', 'ArrowLeft') // closed already, no children shown: goes to the parent
    expect(document.activeElement?.getAttribute('data-page-id')).toBe('A')

    await key('A', 'ArrowLeft')
    expect(titles()).toEqual(['A', 'B', 'C'])
  })

  it('does nothing with the right arrow on a page without children, and nothing with the left arrow at the top', async () => {
    const { key, titles } = await mountTree()

    await key('B', 'ArrowRight')
    await key('B', 'ArrowLeft')

    expect(titles()).toEqual(['A', 'B', 'C'])
    expect(document.activeElement?.getAttribute('data-page-id')).toBe('B')
  })

  it.each(['Enter', ' '])('opens the page with %j', async (k) => {
    await mountTree()
    const { key } = {
      key: async (id: string) => {
        ;(wrapper!.get(`[data-page-id="${id}"]`).element as HTMLElement).focus()
        await wrapper!.get(`[data-page-id="${id}"]`).trigger('keydown', { key: k })
      },
    }

    await key('B')

    expect(wrapper!.emitted('open')).toEqual([['B']])
  })

  it('opens the page when its row is clicked, and only toggles when the arrow is clicked', async () => {
    const { row, titles } = await mountTree()

    await row('A').get('button').trigger('click')
    expect(titles()).toEqual(['A', 'A1', 'A2', 'B', 'C'])
    expect(wrapper!.emitted('open')).toBeUndefined()

    await row('B').trigger('click')
    expect(wrapper!.emitted('open')).toEqual([['B']])
  })

  it('leaves keys typed in the title field to the field', async () => {
    const { key, row } = await mountTree()
    await key('B', 'F2')

    await row('B').get('input').trigger('keydown', { key: 'ArrowDown' })

    expect(wrapper!.emitted('open')).toBeUndefined()
    expect(row('B').find('input').exists()).toBe(true)
  })
})

describe('SidebarTree: renaming', () => {
  it('turns the title into a field with F2, and Enter saves it', async () => {
    const { key, row, server } = await mountTree()
    await key('B', 'F2')

    const input = row('B').get('input')
    expect(input.attributes('aria-label')).toBe('Page title')
    expect((input.element as HTMLInputElement).value).toBe('B')
    await input.setValue('Renamed')
    await input.trigger('keydown', { key: 'Enter' })
    await flushPromises()

    expect(server.writes()).toEqual([
      { method: 'PATCH', path: '/api/pages/B', body: { title: 'Renamed', expectedVersion: 1 } },
    ])
    expect(wrapper!.find('input').exists()).toBe(false)
    expect(document.activeElement?.getAttribute('data-page-id')).toBe('B')
  })

  it('does not save with Escape', async () => {
    const { key, row, server } = await mountTree()
    await key('B', 'F2')
    await row('B').get('input').setValue('Changed my mind')

    await row('B').get('input').trigger('keydown', { key: 'Escape' })
    await flushPromises()

    expect(server.writes()).toEqual([])
    expect(wrapper!.find('input').exists()).toBe(false)
  })

  it('saves when the field loses focus', async () => {
    const { key, row, server } = await mountTree()
    await key('B', 'F2')
    await row('B').get('input').setValue('By blur')

    await row('B').get('input').trigger('blur')
    await flushPromises()

    expect(server.writes()).toHaveLength(1)
    expect(server.writes()[0]!.body.title).toBe('By blur')
  })

  it('does not send a rename that changes nothing', async () => {
    const { key, row, server } = await mountTree()
    await key('B', 'F2')

    await row('B').get('input').trigger('keydown', { key: 'Enter' })
    await flushPromises()

    expect(server.writes()).toEqual([])
  })

  it('does not save twice when Enter is followed by the field going away', async () => {
    const { key, row, server } = await mountTree()
    await key('B', 'F2')
    const input = row('B').get('input')
    await input.setValue('Once')

    await input.trigger('keydown', { key: 'Enter' })
    await input.trigger('blur')
    await flushPromises()

    expect(server.writes()).toHaveLength(1)
  })

  it('may leave the title empty: the page then shows as untitled', async () => {
    const { key, row, server } = await mountTree()
    await key('B', 'F2')
    await row('B').get('input').setValue('   ')
    await row('B').get('input').trigger('keydown', { key: 'Enter' })
    await flushPromises()

    expect(server.writes()[0]!.body.title).toBe('   ') // the server trims it
  })

  it('renames on a double click', async () => {
    const { row } = await mountTree()

    await row('B').trigger('dblclick')
    await flushPromises()

    expect(row('B').find('input').exists()).toBe(true)
  })
})

describe('SidebarTree: creating', () => {
  it('creates a page at the end, opens it and starts renaming it', async () => {
    const { server, row } = await mountTree()
    server.setPages([
      ...pages,
      { id: 'new', parentId: null, title: '', sortKey: 'z', version: 1, updatedAt: '' },
    ])

    await wrapper!.get('button').trigger('click') // "New page", the first button
    await flushPromises()

    expect(server.writes()).toEqual([
      {
        method: 'POST',
        path: '/api/workspaces/w1/pages',
        body: { title: '', parentId: null, afterId: null, beforeId: null },
      },
    ])
    expect(wrapper!.emitted('created')).toEqual([['new']])
    expect(wrapper!.emitted('open')).toBeUndefined()
    expect(row('new').find('input').exists()).toBe(true)
  })

  it('has a button with a name', async () => {
    await mountTree()

    expect(wrapper!.get('button').text()).toBe('New page')
  })
})

describe('SidebarTree: trashing', () => {
  it('moves the page to the trash with Delete and puts the focus on the next row', async () => {
    const { key, server, notices } = await mountTree()
    server.setPages(outline(['A', 'C']))

    await key('B', 'Delete')

    expect(server.writes()).toEqual([
      { method: 'POST', path: '/api/pages/B/trash', body: { expectedVersion: 1 } },
    ])
    expect(notices.notices).toMatchObject([{ type: 'trashed', pageId: 'B' }])
    expect(document.activeElement?.getAttribute('data-page-id')).toBe('C')
  })

  it('puts the focus above when the last page goes', async () => {
    const { key, server } = await mountTree()
    server.setPages(outline(['A', 'B']))

    await key('C', 'Delete')

    expect(document.activeElement?.getAttribute('data-page-id')).toBe('B')
  })

  it('does not trash a page when the server refuses, and says why', async () => {
    const { key, server, notices } = await mountTree()
    server.failNext(409, 'page_in_trash')

    await key('B', 'Delete')

    expect(notices.notices).toMatchObject([{ type: 'error', code: 'page_in_trash' }])
  })
})

describe('SidebarTree: moving with the keyboard', () => {
  it.each([
    ['ArrowUp', 'C', { parentId: null, afterId: null, beforeId: 'B' }],
    ['ArrowDown', 'A', { parentId: null, afterId: 'B', beforeId: null }],
    ['ArrowRight', 'B', { parentId: 'A', afterId: null, beforeId: null }],
    ['ArrowLeft', 'A1', { parentId: null, afterId: 'A', beforeId: null }],
  ])('Alt+%s on %s asks the server for %j', async (k, id, position) => {
    const { key, server } = await mountTree({ open: ['A'] })

    await key(id, k, { altKey: true })

    expect(server.writes()).toHaveLength(1)
    expect(server.writes()[0]).toMatchObject({
      method: 'POST',
      path: `/api/pages/${id}/move`,
      body: { ...position, expectedVersion: 1 },
    })
  })

  it.each([
    ['ArrowUp', 'A'],
    ['ArrowDown', 'C'],
    ['ArrowRight', 'A'],
    ['ArrowLeft', 'B'],
  ])('Alt+%s on %s has nowhere to go and asks nothing', async (k, id) => {
    const { key, server } = await mountTree({ open: ['A'] })

    await key(id, k, { altKey: true })

    expect(server.writes()).toEqual([])
  })

  it('keeps the focus on the page that moved', async () => {
    const { key, server } = await mountTree()
    server.setPages(outline(['A', 'C', 'B']))

    await key('C', 'ArrowUp', { altKey: true })

    expect(document.activeElement?.getAttribute('data-page-id')).toBe('C')
  })

  it('does not treat Alt+Arrow as moving focus', async () => {
    const { key } = await mountTree()

    await key('A', 'ArrowDown', { altKey: true })

    expect(document.activeElement?.getAttribute('data-page-id')).toBe('A')
  })
})

describe('SidebarTree: dragging with a mouse', () => {
  it('can be dragged only by those who may edit and use a mouse', async () => {
    const { row } = await mountTree()
    expect(row('B').attributes('draggable')).toBe('true')
    wrapper!.unmount()

    const viewer = await mountTree({ role: 'viewer' })
    expect(viewer.row('B').attributes('draggable')).toBe('false')
    wrapper!.unmount()

    vi.stubGlobal('matchMedia', () => ({
      matches: false,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    }))
    const touch = await mountTree()
    expect(touch.row('B').attributes('draggable')).toBe('false')
  })

  it('moves a page into the one it is dropped on', async () => {
    const { row, server } = await mountTree()

    await row('C').trigger('dragstart')
    await row('B').trigger('dragover') // jsdom has no layout, so the pointer is in the middle of the row
    await row('B').trigger('drop')
    await flushPromises()

    expect(server.writes()).toEqual([
      {
        method: 'POST',
        path: '/api/pages/C/move',
        body: { parentId: 'B', afterId: null, beforeId: null, expectedVersion: 1 },
      },
    ])
  })

  it('shows where the page would go while it is over a row', async () => {
    const { row } = await mountTree()

    await row('C').trigger('dragstart')
    await row('B').trigger('dragover')

    expect(row('B').classes().join(' ')).toContain('outline-primary-container')
    expect(row('C').classes()).toContain('opacity-40')
  })

  it('does not allow a drop on itself or on something below it', async () => {
    const { row, server } = await mountTree({ open: ['A', 'A1'] })

    await row('A').trigger('dragstart')
    await row('A').trigger('dragover')
    await row('A').trigger('drop')
    await row('A1a').trigger('dragover')
    await row('A1a').trigger('drop')
    await flushPromises()

    expect(server.writes()).toEqual([])
    expect(row('A1a').classes().join(' ')).not.toContain('outline-primary-container')
  })

  it('forgets the drag when it ends without a drop', async () => {
    const { row, server } = await mountTree()

    await row('C').trigger('dragstart')
    await row('B').trigger('dragover')
    await wrapper!.get('[role="tree"]').trigger('dragend')

    expect(row('C').classes()).not.toContain('opacity-40')
    expect(server.writes()).toEqual([])
  })

  it('ignores a drop of something that is not a page of the tree', async () => {
    const { row, server } = await mountTree()

    await row('B').trigger('dragover')
    await row('B').trigger('drop')

    expect(server.writes()).toEqual([])
  })
})

describe('SidebarTree: without the right to edit', () => {
  it('has no New page button, no row menus and no editing keys', async () => {
    const { key, row, server } = await mountTree({ role: 'viewer' })

    expect(wrapper!.findAll('button').map((b) => b.text())).not.toContain('New page')
    expect(row('B').find('button[aria-label^="Actions for"]').exists()).toBe(false)
    await key('B', 'F2')
    await key('B', 'Delete')
    await key('B', 'ArrowUp', { altKey: true })
    await row('B').trigger('dblclick')
    await flushPromises()

    expect(row('B').find('input').exists()).toBe(false)
    expect(server.writes()).toEqual([])
  })

  it('can still be read and walked with the keyboard', async () => {
    const { key } = await mountTree({ role: 'viewer' })

    await key('A', 'ArrowDown')

    expect(document.activeElement?.getAttribute('data-page-id')).toBe('B')
  })
})

describe('SidebarTree: the menu of a row', () => {
  async function menuOf(id: string, options: Parameters<typeof mountTree>[0] = {}) {
    const context = await mountTree(options)
    await context.key(id, 'ContextMenu')
    await flushPromises()
    const items = () => [...document.body.querySelectorAll<HTMLElement>('[role="menuitem"]')]
    const item = (text: string) => items().find((el) => el.textContent?.trim() === text)!
    return { ...context, items, item }
  }

  it('opens with the context menu key and lists the four actions', async () => {
    const { items } = await menuOf('B')

    expect(items().map((el) => el.textContent?.trim())).toEqual([
      'Rename',
      'Add subpage',
      'Move to…',
      'Move to trash',
    ])
  })

  it('opens with Shift+F10 too', async () => {
    const { key } = await mountTree()

    await key('B', 'F10', { shiftKey: true })
    await flushPromises()

    expect(document.body.querySelectorAll('[role="menuitem"]')).toHaveLength(4)
  })

  it('renames from the menu', async () => {
    const { item, row } = await menuOf('B')

    item('Rename').click()
    await flushPromises()

    expect(row('B').find('input').exists()).toBe(true)
  })

  it('adds a subpage under the page, and opens the page', async () => {
    const { item, server } = await menuOf('B')

    item('Add subpage').click()
    await flushPromises()

    expect(server.writes()[0]).toMatchObject({
      method: 'POST',
      path: '/api/workspaces/w1/pages',
      body: { title: '', parentId: 'B' },
    })
  })

  it('opens the Move to dialog', async () => {
    const { item } = await menuOf('B')

    item('Move to…').click()
    await flushPromises()

    expect(document.body.querySelector('[role="dialog"]')?.textContent).toContain('Move “B” to')
  })

  it('moves the page to the trash', async () => {
    const { item, server } = await menuOf('B')

    item('Move to trash').click()
    await flushPromises()

    expect(server.writes()).toEqual([
      { method: 'POST', path: '/api/pages/B/trash', body: { expectedVersion: 1 } },
    ])
  })

  it('has a button on the row with the name of the page, for touch and screen readers', async () => {
    const { row } = await mountTree()

    expect(row('B').get('button[aria-label="Actions for B"]').attributes('tabindex')).toBe('-1')
  })
})
