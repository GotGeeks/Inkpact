import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createAppI18n } from '../i18n'
import { destroyEditors, makeEditor } from '../testing/editor'
import LinkMenu from './LinkMenu.vue'

const ID = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f00'

let wrapper: VueWrapper | null = null
afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  destroyEditors()
})

/** An editor with the caret inside a link to `href`, and the menu for it. */
async function menuFor(href: string, locale: 'en' | 'nl' = 'en') {
  const editor = makeEditor()
  editor.commands.insertContent([
    { type: 'text', text: 'before ' },
    { type: 'text', text: 'the link', marks: [{ type: 'link', attrs: { href } }] },
  ])
  editor.commands.setTextSelection(10)
  const open = vi.fn()
  wrapper = mount(LinkMenu, {
    props: { editor, open },
    attachTo: document.body,
    global: { plugins: [createAppI18n(locale)] },
  })
  await flushPromises()
  return { editor, open }
}

describe('LinkMenu', () => {
  it('opens the page a page link points to', async () => {
    const { open } = await menuFor(`/p/${ID}`)

    const button = wrapper!.get('[data-testid="link-open"]')
    expect(button.text()).toBe('Open page')
    await button.trigger('click')

    expect(open).toHaveBeenCalledWith(`/p/${ID}`, false)
  })

  it('opens a web link, in Dutch too', async () => {
    const { open } = await menuFor('https://example.com/', 'nl')

    const button = wrapper!.get('[data-testid="link-open"]')
    expect(button.text()).toBe('Link openen')
    expect(wrapper!.get('[data-testid="link-menu"]').attributes('aria-label')).toBe('Link')
    await button.trigger('click')

    expect(open).toHaveBeenCalledWith('https://example.com/', false)
  })

  it('offers nothing for an address the editor does not allow', async () => {
    // What another client could put in the document: the mark's address as stored, not as checked on the way in.
    await menuFor('javascript:alert(1)')

    expect(wrapper!.find('[data-testid="link-open"]').exists()).toBe(false)
  })

  it('keeps the focus in the text when the button is pressed', async () => {
    await menuFor(`/p/${ID}`)
    const press = new Event('mousedown', { bubbles: true, cancelable: true })

    wrapper!.get('[data-testid="link-open"]').element.dispatchEvent(press)

    expect(press.defaultPrevented).toBe(true)
  })
})
