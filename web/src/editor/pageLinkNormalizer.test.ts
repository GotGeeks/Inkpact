import type { Editor } from '@tiptap/core'
import { afterEach, describe, expect, it } from 'vitest'
import * as Y from 'yjs'
import { applyFirstParagraph, FIELD } from './firstParagraph'
import { destroyEditors, makeEditor } from '../testing/editor'

const ID = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f20'
const OTHER = '8a1c4e52-3b7d-8f06-9e21-5d4c3b2a1f21'

afterEach(destroyEditors)

const origin = () => window.location.origin

/** The address of every link on the page, in order. */
function hrefs(editor: Editor): string[] {
  const found: string[] = []
  editor.state.doc.descendants((node) => {
    const mark = node.marks.find((m) => m.type.name === 'link')
    if (node.isText && mark) found.push(mark.attrs.href as string)
  })
  return found
}

const link = (href: string, text = 'a page') => ({
  type: 'text',
  text,
  marks: [{ type: 'link', attrs: { href } }],
})

describe('a link that this person makes to a page of the app', () => {
  it('is stored as /p/<id> when it comes in as the full address on this origin, as a paste brings it', () => {
    const editor = makeEditor()

    editor.commands.insertContent(link(`${origin()}/p/${ID}`))

    expect(hrefs(editor)).toEqual([`/p/${ID}`])
  })

  it('keeps its text, and the other marks', () => {
    const editor = makeEditor()

    editor.commands.insertContent({
      type: 'text',
      text: 'bold page',
      marks: [{ type: 'bold' }, { type: 'link', attrs: { href: `${origin()}/p/${ID}` } }],
    })

    expect(editor.getText()).toBe('bold page')
    expect(hrefs(editor)).toEqual([`/p/${ID}`])
    expect(
      editor
        .getJSON()
        .content![0]!.content![0]!.marks!.map((m) => m.type)
        .sort(),
    ).toEqual(['bold', 'link'])
  })

  it('is stored as /p/<id> when a mark is put on text that is there (the picker does it), also with a full address', () => {
    const editor = makeEditor()
    editor.commands.insertContent('some words here')
    editor.commands.setTextSelection({ from: 1, to: 5 })

    editor.commands.setLink({ href: `${origin()}/p/${ID}` })

    expect(hrefs(editor)).toEqual([`/p/${ID}`])
  })

  it('is left as it is when it is already /p/<id>', () => {
    const editor = makeEditor()

    editor.commands.insertContent(link(`/p/${ID}`))

    expect(hrefs(editor)).toEqual([`/p/${ID}`])
  })

  it('is left alone when it is the address of another host, or has a query, a fragment or more path', () => {
    const editor = makeEditor()
    const addresses = [
      `https://other.example/p/${ID}`,
      `${origin()}/p/${ID}?x=1`,
      `${origin()}/p/${ID}#top`,
      `${origin()}/p/${ID}/more`,
      'https://example.com/',
    ]

    for (const address of addresses) editor.commands.insertContent(link(address, address))

    expect(hrefs(editor)).toEqual(addresses)
  })

  it('only looks at what changed: a link from before stays as it was when text is typed elsewhere', () => {
    // What an earlier version stored, and another client wrote: a full address.
    const here = new Y.Doc()
    applyFirstParagraph(here)
    const editor = makeEditor({ doc: here })
    const there = new Y.Doc()
    applyFirstParagraph(there)
    Y.applyUpdate(there, Y.encodeStateAsUpdate(here))
    const paragraph = there.getXmlFragment(FIELD).get(0) as Y.XmlElement
    ;(paragraph.get(0) as Y.XmlText).insert(0, 'old', { link: { href: `${origin()}/p/${ID}` } })
    Y.applyUpdate(here, Y.encodeStateAsUpdate(there, Y.encodeStateVector(here)))
    expect(hrefs(editor)).toEqual([`${origin()}/p/${ID}`])

    // This person types in another block, and makes a new link there. (Typing right after the old link would extend that
    // link, which the person is then editing.)
    editor.commands.insertContentAt(editor.state.doc.content.size, {
      type: 'paragraph',
      content: [{ type: 'text', text: 'elsewhere ' }, link(`${origin()}/p/${OTHER}`, 'new')],
    })

    expect(hrefs(editor)).toEqual([`${origin()}/p/${ID}`, `/p/${OTHER}`])
  })
})

describe('a link that comes from another client', () => {
  /** Another client's edit, as Yjs delivers it: written straight into its document, which is then applied here. */
  function fromAnotherClient(href: string) {
    const here = new Y.Doc()
    applyFirstParagraph(here)
    const editor = makeEditor({ doc: here })

    const there = new Y.Doc()
    applyFirstParagraph(there)
    Y.applyUpdate(there, Y.encodeStateAsUpdate(here))
    const paragraph = there.getXmlFragment(FIELD).get(0) as Y.XmlElement
    const text = paragraph.get(0) as Y.XmlText
    text.insert(0, 'theirs', { link: { href } })
    Y.applyUpdate(here, Y.encodeStateAsUpdate(there, Y.encodeStateVector(here)))

    return { editor, here }
  }

  it('is never rewritten: that would write an edit that nobody made, on every client at once', () => {
    const { editor, here } = fromAnotherClient(`${origin()}/p/${ID}`)
    const stored = Y.encodeStateAsUpdate(here)

    expect(editor.getText()).toBe('theirs')
    expect(hrefs(editor)).toEqual([`${origin()}/p/${ID}`])
    // And nothing was written back into the document.
    expect(Y.encodeStateAsUpdate(here)).toEqual(stored)
  })
})
