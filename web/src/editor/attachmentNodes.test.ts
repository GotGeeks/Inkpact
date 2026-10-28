import { afterEach, describe, expect, it, vi } from 'vitest'
import * as Y from 'yjs'
import { destroyEditors, makeEditor, pressKey } from '../testing/editor'
import { attachmentUrl, isAttachmentId } from './attachmentNodes'
import { FIELD, applyFirstParagraph } from './firstParagraph'
import { sanitizePastedHtml } from './sanitize'

afterEach(() => {
  destroyEditors()
  vi.restoreAllMocks()
})

const ID = '01a17c5f-59ca-7a6b-ab64-550260ea03b6'

/** An editor on a document that a second client, `other`, also has, so content can arrive the way Yjs brings it. */
function sharedEditor() {
  const doc = new Y.Doc()
  applyFirstParagraph(doc)
  const editor = makeEditor({
    doc,
    files: {
      formatSize: (bytes) => `${bytes} B`,
      downloadLabel: (name, size) => `Download ${name}, ${size}`,
    },
  })
  const other = new Y.Doc()
  Y.applyUpdate(other, Y.encodeStateAsUpdate(doc))
  doc.on('update', (update: Uint8Array) => Y.applyUpdate(other, update))
  other.on('update', (update: Uint8Array) => Y.applyUpdate(doc, update))
  return { editor, other }
}

/** What another client puts in the page: an element straight into the shared document, past every paste handler. */
function fromOtherClient(other: Y.Doc, name: string, attributes: Record<string, unknown>) {
  const element = new Y.XmlElement(name)
  for (const [key, value] of Object.entries(attributes)) element.setAttribute(key, value as string)
  other.getXmlFragment(FIELD).insert(1, [element])
}

describe('attachment ids and addresses', () => {
  it('knows an id when it sees one', () => {
    expect(isAttachmentId(ID)).toBe(true)
    expect(isAttachmentId(ID.toUpperCase())).toBe(true)
    for (const bad of [
      '',
      'abc',
      `${ID}/../x`,
      `../${ID}`,
      `${ID}?x=1`,
      ' ' + ID,
      42,
      null,
      undefined,
    ]) {
      expect(isAttachmentId(bad), String(bad)).toBe(false)
    }
  })

  it('makes an address only from an id', () => {
    expect(attachmentUrl(ID)).toBe(`/api/attachments/${ID}`)
    expect(attachmentUrl(ID.toUpperCase())).toBe(`/api/attachments/${ID}`)
    expect(attachmentUrl('https://evil.example/x.png')).toBeNull()
    expect(attachmentUrl('javascript:alert(1)')).toBeNull()
    expect(attachmentUrl('../../api/auth/logout')).toBeNull()
  })
})

describe('the image node', () => {
  it('shows an attachment by its id', () => {
    const { editor } = sharedEditor()
    editor.commands.insertContent({ type: 'image', attrs: { attachmentId: ID, alt: 'A cat' } })

    const image = editor.view.dom.querySelector('img')!
    expect(image.getAttribute('src')).toBe(`/api/attachments/${ID}`)
    expect(image.getAttribute('alt')).toBe('A cat')
    expect(image.getAttribute('data-attachment-id')).toBe(ID)
    expect(image.hasAttribute('style')).toBe(false)
  })

  it.each([
    'https://evil.example/tracker.png',
    'javascript:alert(1)',
    '../../api/auth/logout',
    `${ID}/../../x`,
  ])('renders no address for %s that another client put in the document', (bad) => {
    const { editor, other } = sharedEditor()
    fromOtherClient(other, 'image', { attachmentId: bad, alt: 'x' })

    const image = editor.view.dom.querySelector('img')!
    expect(image).not.toBeNull()
    expect(image.hasAttribute('src')).toBe(false)
    expect(image.getAttribute('data-attachment-id')).toBe('')
  })

  it('copies and pastes as itself, through the paste allow-list', () => {
    const { editor } = sharedEditor()
    editor.commands.insertContent({ type: 'image', attrs: { attachmentId: ID, alt: 'A cat' } })
    const copied = editor.getHTML()

    const target = sharedEditor().editor
    target.commands.setContent(sanitizePastedHtml(copied))

    const image = target.getJSON().content!.find((node) => node.type === 'image')!
    expect(image.attrs).toEqual({ attachmentId: ID, alt: 'A cat' })
  })
})

describe('the file node', () => {
  it('shows the name and size of a file, as a download of the attachment', () => {
    const { editor } = sharedEditor()
    editor.commands.insertContent({
      type: 'attachment',
      attrs: { attachmentId: ID, fileName: 'manual.pdf', size: 1234 },
    })

    const link = editor.view.dom.querySelector('a[data-attachment-id]')!
    expect(link.getAttribute('href')).toBe(`/api/attachments/${ID}`)
    expect(link.getAttribute('download')).toBe('manual.pdf')
    expect(link.getAttribute('aria-label')).toBe('Download manual.pdf, 1234 B')
    expect(link.textContent).toBe('manual.pdf1234 B')
  })

  it('renders no address for an id that is not one', () => {
    const { editor, other } = sharedEditor()
    const paragraph = new Y.XmlElement('paragraph')
    const file = new Y.XmlElement('attachment')
    file.setAttribute('attachmentId', 'javascript:alert(1)')
    file.setAttribute('fileName', 'x.pdf')
    paragraph.insert(0, [file])
    other.getXmlFragment(FIELD).insert(1, [paragraph])

    const link = editor.view.dom.querySelector('a[data-attachment-id]')!
    expect(link.hasAttribute('href')).toBe(false)
    expect(link.hasAttribute('download')).toBe(false)
  })

  it('downloads when clicked, without leaving the page', () => {
    const { editor } = sharedEditor()
    editor.commands.insertContent({
      type: 'attachment',
      attrs: { attachmentId: ID, fileName: 'manual.pdf', size: 1 },
    })
    const clicked: HTMLAnchorElement[] = []
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      clicked.push(this)
    })

    for (const editable of [true, false]) {
      clicked.length = 0
      editor.setEditable(editable)
      const click = new MouseEvent('click', { button: 0, bubbles: true, cancelable: true })
      editor.view.dom.querySelector('a[data-attachment-id] span')!.dispatchEvent(click)

      // Once: the link's own click is cancelled, so the browser does not download it a second time.
      expect(click.defaultPrevented, `editable: ${editable}`).toBe(true)
      expect(clicked).toHaveLength(1)
      expect(clicked[0]!.getAttribute('href')).toBe(`/api/attachments/${ID}`)
      expect(clicked[0]!.download).toBe('manual.pdf')
      expect(clicked[0]!.isConnected).toBe(false)
    }
  })

  it('downloads with Enter when the file is selected', () => {
    const { editor } = sharedEditor()
    editor.commands.insertContent([
      { type: 'text', text: 'see ' },
      { type: 'attachment', attrs: { attachmentId: ID, fileName: 'manual.pdf', size: 1 } },
    ])
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})

    let filePos = -1
    editor.state.doc.descendants((node, pos) => {
      if (node.type.name === 'attachment') filePos = pos
    })

    editor.commands.setNodeSelection(filePos)
    expect(pressKey(editor, 'Enter')).toBe(true)
    expect(click).toHaveBeenCalledOnce()
    expect((click.mock.contexts[0] as HTMLAnchorElement).download).toBe('manual.pdf')
    expect(editor.getJSON().content).toHaveLength(1)

    // With the caret in the text, Enter is a new paragraph, as always.
    editor.commands.setTextSelection(2)
    pressKey(editor, 'Enter')
    expect(click).toHaveBeenCalledOnce()
  })

  it('copies and pastes as itself, through the paste allow-list', () => {
    const { editor } = sharedEditor()
    editor.commands.insertContent({
      type: 'attachment',
      attrs: { attachmentId: ID, fileName: 'manual.pdf', size: 1234 },
    })
    const copied = editor.getHTML()

    const target = sharedEditor().editor
    target.commands.setContent(sanitizePastedHtml(copied))

    const paragraph = target.getJSON().content![0]!
    expect(paragraph.content).toEqual([
      { type: 'attachment', attrs: { attachmentId: ID, fileName: 'manual.pdf', size: 1234 } },
    ])
    // Not a link mark as well: the node takes the element first.
    expect(JSON.stringify(target.getJSON())).not.toContain('"link"')
  })
})
