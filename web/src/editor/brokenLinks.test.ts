import type { JSONContent } from '@tiptap/core'
import { afterEach, describe, expect, it } from 'vitest'
import { destroyEditors, makeEditor } from '../testing/editor'
import { refreshBrokenLinks, type LinkTarget } from './brokenLinks'

const GONE = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f01'
const TRASHED = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f02'
const FINE = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f03'

afterEach(destroyEditors)

const link = (href: string, text: string, extra: { type: string }[] = []): JSONContent => ({
  type: 'text',
  text,
  marks: [{ type: 'link', attrs: { href } }, ...extra],
})

/** An editor whose pages are as `states` say, with this content; `states` can be changed and the links looked at again. */
function editorWith(content: JSONContent[], states: Record<string, LinkTarget>) {
  const editor = makeEditor({
    brokenLinks: {
      stateOf: (pageId) => states[pageId] ?? 'live',
      describe: (state) => (state === 'trash' ? 'It is in the trash' : 'It is not available'),
    },
  })
  editor.commands.setContent({ type: 'doc', content: [{ type: 'paragraph', content }] })
  return editor
}

const marked = (editor: ReturnType<typeof makeEditor>) =>
  [...editor.view.dom.querySelectorAll('[data-broken-link]')].map((element) => ({
    state: element.getAttribute('data-broken-link'),
    text: element.textContent,
    title: element.getAttribute('title'),
    struck: element.classList.contains('line-through'),
  }))

const notes = (editor: ReturnType<typeof makeEditor>) =>
  [...editor.view.dom.querySelectorAll('.sr-only')].map((element) => element.textContent)

describe('a link to a page that is not there', () => {
  it('is struck through with the reason, whether the page is in the trash or cannot be found', () => {
    const editor = editorWith(
      [
        link(`/p/${TRASHED}`, 'old plan'),
        { type: 'text', text: ' and ' },
        link(`/p/${GONE}`, 'lost'),
      ],
      { [TRASHED]: 'trash', [GONE]: 'unavailable' },
    )

    expect(marked(editor)).toEqual([
      { state: 'trash', text: 'old plan', title: 'It is in the trash', struck: true },
      { state: 'unavailable', text: 'lost', title: 'It is not available', struck: true },
    ])
  })

  it('says the reason to a screen reader, once after the link', () => {
    const editor = editorWith([link(`/p/${TRASHED}`, 'old plan')], { [TRASHED]: 'trash' })

    expect(notes(editor)).toEqual([' (It is in the trash)'])
  })

  it('says it once also when the link has some bold in it, which makes it several pieces of text', () => {
    const editor = editorWith(
      [link(`/p/${TRASHED}`, 'old '), link(`/p/${TRASHED}`, 'plan', [{ type: 'bold' }])],
      { [TRASHED]: 'trash' },
    )

    expect(marked(editor)).toHaveLength(2)
    expect(notes(editor)).toHaveLength(1)
  })

  it('stays in the document as it was, and is whole again when the page is back', () => {
    const states: Record<string, LinkTarget> = { [TRASHED]: 'trash' }
    const editor = editorWith([link(`/p/${TRASHED}`, 'old plan')], states)
    const before = JSON.stringify(editor.getJSON())
    expect(marked(editor)).toHaveLength(1)

    states[TRASHED] = 'live'
    refreshBrokenLinks(editor.view)

    expect(marked(editor)).toEqual([])
    expect(notes(editor)).toEqual([])
    expect(JSON.stringify(editor.getJSON())).toBe(before)
  })

  it('is marked when the page goes to the trash after the page was opened', () => {
    const states: Record<string, LinkTarget> = {}
    const editor = editorWith([link(`/p/${FINE}`, 'a page')], states)
    expect(marked(editor)).toEqual([])

    states[FINE] = 'trash'
    refreshBrokenLinks(editor.view)

    expect(marked(editor).map((m) => m.state)).toEqual(['trash'])
  })

  it('is found by its full address on this origin as well', () => {
    const editor = editorWith([link(`${window.location.origin}/p/${GONE}`, 'lost')], {
      [GONE]: 'unavailable',
    })

    expect(marked(editor).map((m) => m.state)).toEqual(['unavailable'])
  })

  it('is marked in text that is typed after the page was opened', () => {
    const editor = editorWith([{ type: 'text', text: 'start' }], { [GONE]: 'unavailable' })

    editor.commands.insertContent(link(`/p/${GONE}`, 'lost'))

    expect(marked(editor).map((m) => m.text)).toEqual(['lost'])
  })
})

describe('a change to the document', () => {
  function twoParagraphs(
    states: Record<string, LinkTarget>,
    stateOf = (id: string) => states[id] ?? 'live',
  ) {
    const editor = makeEditor({ brokenLinks: { stateOf, describe: () => 'gone' } })
    editor.commands.setContent({
      type: 'doc',
      content: [
        { type: 'paragraph', content: [link(`/p/${GONE}`, 'first')] },
        {
          type: 'paragraph',
          content: [{ type: 'text', text: 'plain ' }, link(`/p/${TRASHED}`, 'second')],
        },
      ],
    })
    return editor
  }

  it('keeps the marks of the blocks it did not touch, and moves them along', () => {
    const editor = twoParagraphs({ [GONE]: 'unavailable', [TRASHED]: 'trash' })

    // A new block above both: everything moves, nothing about the links changes.
    editor.commands.insertContentAt(0, {
      type: 'paragraph',
      content: [{ type: 'text', text: 'new' }],
    })

    expect(marked(editor).map((m) => [m.text, m.state])).toEqual([
      ['first', 'unavailable'],
      ['second', 'trash'],
    ])
    expect(notes(editor)).toHaveLength(2)
  })

  it('takes away the mark of a link that was removed, and of its text when that goes', () => {
    const editor = twoParagraphs({ [GONE]: 'unavailable', [TRASHED]: 'trash' })

    editor.chain().setTextSelection({ from: 1, to: 6 }).unsetLink().run()

    expect(marked(editor).map((m) => m.text)).toEqual(['second'])
    expect(notes(editor)).toHaveLength(1)
  })

  it('does not look up the pages of the links outside what changed', () => {
    const asked: string[] = []
    const editor = twoParagraphs({}, (id) => {
      asked.push(id)
      return 'live'
    })
    asked.length = 0

    // Typing into the first paragraph: only its link is looked at again.
    editor.commands.insertContentAt(1, 'x')

    expect(asked).toEqual([GONE])
  })
})

describe('what is not marked', () => {
  it('a link to a page that is there, a link to the web and plain text', () => {
    const editor = editorWith(
      [
        link(`/p/${FINE}`, 'fine'),
        link('https://example.com/', 'web'),
        { type: 'text', text: `/p/${GONE}` },
      ],
      { [GONE]: 'unavailable' },
    )

    expect(marked(editor)).toEqual([])
    expect(notes(editor)).toEqual([])
  })

  it('anything, in an editor that was not told how to look a page up', () => {
    const editor = makeEditor()
    editor.commands.setContent({
      type: 'doc',
      content: [{ type: 'paragraph', content: [link(`/p/${GONE}`, 'lost')] }],
    })

    expect(marked(editor)).toEqual([])
  })
})
