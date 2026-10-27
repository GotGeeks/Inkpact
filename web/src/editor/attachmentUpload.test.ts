import type { Editor } from '@tiptap/core'
import { Slice } from '@tiptap/pm/model'
import { afterEach, describe, expect, it, vi } from 'vitest'
import * as Y from 'yjs'
import { destroyEditors, makeEditor, typeText } from '../testing/editor'
import { addFiles, type AttachmentUploadOptions, type UploadedAttachment } from './attachmentUpload'
import { FIELD, applyFirstParagraph } from './firstParagraph'

afterEach(() => {
  destroyEditors()
  vi.restoreAllMocks()
})

const IMAGE_ID = '01a17c5f-59ca-7a6b-ab64-550260ea03b6'
const FILE_ID = '01a17c5f-59ca-7a6b-ab64-550260ea03b7'

const image = (id = IMAGE_ID): UploadedAttachment => ({
  id,
  fileName: 'cat.png',
  contentType: 'image/png',
  size: 10,
  inline: true,
})
const pdf = (id = FILE_ID): UploadedAttachment => ({
  id,
  fileName: 'manual.pdf',
  contentType: 'application/pdf',
  size: 20,
  inline: false,
})

function options(upload: AttachmentUploadOptions['upload']) {
  return { upload, onError: vi.fn(), onBusyChange: vi.fn() } satisfies AttachmentUploadOptions
}

/** An editor with uploads, on a document a second client also has. */
function setUp(upload: AttachmentUploadOptions['upload'] = async () => image()) {
  const doc = new Y.Doc()
  applyFirstParagraph(doc)
  const upload$ = options(upload)
  const editor = makeEditor({ doc, upload: upload$ })
  const other = new Y.Doc()
  Y.applyUpdate(other, Y.encodeStateAsUpdate(doc))
  doc.on('update', (update: Uint8Array) => Y.applyUpdate(other, update))
  other.on('update', (update: Uint8Array) => Y.applyUpdate(doc, update))
  editor.commands.focus('end')
  return { editor, other, options: upload$ }
}

const file = (name = 'cat.png') => new File(['x'], name)

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (error: unknown) => void
  const promise = new Promise<T>((res, rej) => ((resolve = res), (reject = rej)))
  return { promise, resolve, reject }
}

const types = (editor: Editor) => editor.getJSON().content!.map((node) => node.type)

describe('adding files', () => {
  it('puts an image on its own line, after the text the caret was in', async () => {
    const { editor, options } = setUp()
    typeText(editor, 'hello')

    await addFiles(editor, options, [file()], editor.state.selection.from)

    expect(editor.getJSON().content!.slice(0, 2)).toEqual([
      { type: 'paragraph', content: [{ type: 'text', text: 'hello' }] },
      { type: 'image', attrs: { attachmentId: IMAGE_ID, alt: '' } },
    ])
  })

  it('puts a file in the text, where the caret was', async () => {
    const { editor, options } = setUp(async () => pdf())
    typeText(editor, 'see ')

    await addFiles(editor, options, [file('manual.pdf')], editor.state.selection.from)

    expect(editor.getJSON().content![0]).toEqual({
      type: 'paragraph',
      content: [
        { type: 'text', text: 'see ' },
        { type: 'attachment', attrs: { attachmentId: FILE_ID, fileName: 'manual.pdf', size: 20 } },
      ],
    })
  })

  it('keeps its place while someone else edits the page during the upload', async () => {
    const pending = deferred<UploadedAttachment>()
    const { editor, other, options } = setUp(() => pending.promise)
    typeText(editor, 'hello')

    const adding = addFiles(editor, options, [file('manual.pdf')], editor.state.selection.from)
    // Another person types at the start of the same paragraph: every position after it moves by four.
    const text = (other.getXmlFragment(FIELD).get(0) as Y.XmlElement).get(0) as Y.XmlText
    text.insert(0, 'abc ')
    pending.resolve(pdf())
    await adding

    expect(editor.getJSON().content![0]).toEqual({
      type: 'paragraph',
      content: [
        { type: 'text', text: 'abc hello' },
        { type: 'attachment', attrs: { attachmentId: FILE_ID, fileName: 'manual.pdf', size: 20 } },
      ],
    })
  })

  it('adds several files in the order they were chosen, each after the one before', async () => {
    const answers = [
      pdf('01a17c5f-0000-7000-8000-000000000001'),
      pdf('01a17c5f-0000-7000-8000-000000000002'),
    ]
    const { editor, options } = setUp(async () => answers.shift()!)

    await addFiles(editor, options, [file('a.pdf'), file('b.pdf')], editor.state.selection.from)

    const ids: unknown[] = []
    editor.state.doc.firstChild!.forEach((node) => ids.push(node.attrs.attachmentId))
    expect(ids).toEqual([
      '01a17c5f-0000-7000-8000-000000000001',
      '01a17c5f-0000-7000-8000-000000000002',
    ])
  })

  it('reports a file that could not be added, and still adds the others', async () => {
    const refused = new Error('attachment_too_large')
    const { editor, options } = setUp(async (chosen) => {
      if (chosen.name === 'huge.png') throw refused
      return image()
    })

    await addFiles(
      editor,
      options,
      [file('huge.png'), file('cat.png')],
      editor.state.selection.from,
    )

    expect(options.onError).toHaveBeenCalledExactlyOnceWith(refused)
    expect(types(editor)).toContain('image')
  })

  it('says how many files are on their way, down to none', async () => {
    const { editor, options } = setUp()

    await addFiles(editor, options, [file(), file()], editor.state.selection.from)

    expect(options.onBusyChange.mock.calls.map(([count]) => count)).toEqual([2, 1, 0])
  })

  it('adds nothing to a page that became read-only during the upload, and says so once', async () => {
    const pending = deferred<UploadedAttachment>()
    const { editor, options } = setUp(() => pending.promise)

    const adding = addFiles(editor, options, [file(), file(), file()], editor.state.selection.from)
    editor.setEditable(false)
    pending.resolve(image())
    await adding

    expect(types(editor)).not.toContain('image')
    expect(options.onBusyChange).toHaveBeenLastCalledWith(0)
    expect(options.onError).toHaveBeenCalledExactlyOnceWith(
      expect.objectContaining({ code: 'attachment_not_added' }),
    )
  })

  it('leaves the caret where the person has moved it during the upload', async () => {
    const pending = deferred<UploadedAttachment>()
    const { editor, options } = setUp(() => pending.promise)
    typeText(editor, 'hello world')

    const adding = addFiles(editor, options, [file('manual.pdf')], editor.state.selection.from)
    editor.commands.setTextSelection(3) // back to "he|llo", to type there
    pending.resolve(pdf())
    await adding

    expect(editor.state.selection.from).toBe(3)
    typeText(editor, 'X')
    expect(editor.getJSON().content![0]!.content![0]).toEqual({
      type: 'text',
      text: 'heXllo world',
    })
  })

  it('puts the next image after one that took the place of an empty paragraph', async () => {
    const first = '01a17c5f-0000-7000-8000-000000000001'
    const second = '01a17c5f-0000-7000-8000-000000000002'
    const answers = [image(first), image(second)]
    const { editor, options } = setUp(async () => answers.shift()!)

    await addFiles(editor, options, [file('a.png'), file('b.png')], 1)

    const images: unknown[] = []
    editor.state.doc.forEach((node) => {
      if (node.type.name === 'image') images.push(node.attrs.attachmentId)
    })
    expect(images).toEqual([first, second])
  })

  it('adds nothing to an editor that was closed during the upload', async () => {
    const pending = deferred<UploadedAttachment>()
    const { editor, options } = setUp(() => pending.promise)

    const adding = addFiles(editor, options, [file()], editor.state.selection.from)
    editor.destroy()
    pending.resolve(image())

    await expect(adding).resolves.toBeUndefined()
  })
})

describe('ways in', () => {
  function paste(editor: Editor, files: File[], text = '') {
    const event = {
      clipboardData: { files, getData: (type: string) => (type === 'text/plain' ? text : '') },
      preventDefault: () => {},
    } as unknown as ClipboardEvent
    return editor.view.someProp('handlePaste', (handler) =>
      handler(editor.view, event, Slice.empty),
    )
  }

  it('uploads a pasted screenshot', async () => {
    const { editor, options } = setUp()

    expect(paste(editor, [file('screenshot.png')])).toBe(true)
    await vi.waitFor(() => expect(types(editor)).toContain('image'))
    expect(options.onBusyChange).toHaveBeenLastCalledWith(0)
  })

  it('leaves a paste with text to the editor, even when a picture of it comes along', () => {
    const upload = vi.fn(async () => image())
    const { editor } = setUp(upload)

    expect(paste(editor, [file('rendering.png')], 'a cell from a spreadsheet')).toBeFalsy()
    expect(upload).not.toHaveBeenCalled()
  })

  it('leaves a paste without files to the editor', () => {
    const { editor } = setUp()

    expect(paste(editor, [])).toBeFalsy()
  })

  it('opens the picker with only the image types for an image, and adds what is chosen', async () => {
    const { editor } = setUp()
    const click = vi.spyOn(HTMLInputElement.prototype, 'click').mockImplementation(() => {})

    expect(editor.commands.pickFiles('image')).toBe(true)

    const picker = click.mock.contexts[0] as HTMLInputElement
    expect(picker!.type).toBe('file')
    expect(picker!.multiple).toBe(true)
    expect(picker!.accept).toBe('image/png,image/jpeg,image/gif,image/webp')
    Object.defineProperty(picker, 'files', { value: [file()] })
    picker!.dispatchEvent(new Event('change'))
    await vi.waitFor(() => expect(types(editor)).toContain('image'))
  })

  it('keeps its place while the picker is open and someone else edits the page', async () => {
    const { editor, other } = setUp(async () => pdf())
    const click = vi.spyOn(HTMLInputElement.prototype, 'click').mockImplementation(() => {})
    typeText(editor, 'hello')

    editor.commands.pickFiles('file')
    const text = (other.getXmlFragment(FIELD).get(0) as Y.XmlElement).get(0) as Y.XmlText
    text.insert(0, 'abc ')
    const picker = click.mock.contexts[0] as HTMLInputElement
    Object.defineProperty(picker, 'files', { value: [file('manual.pdf')] })
    picker.dispatchEvent(new Event('change'))

    await vi.waitFor(() =>
      expect(editor.getJSON().content![0]!.content!.map((node) => node.type)).toEqual([
        'text',
        'attachment',
      ]),
    )
    expect(editor.getJSON().content![0]!.content![0]).toEqual({ type: 'text', text: 'abc hello' })
  })

  it('keeps the picker in the page while it is open, and removes it after', () => {
    const { editor } = setUp()
    const click = vi.spyOn(HTMLInputElement.prototype, 'click').mockImplementation(() => {})

    editor.commands.pickFiles('image')
    const chosen = click.mock.contexts[0] as HTMLInputElement
    expect(chosen.isConnected).toBe(true)
    expect(chosen.hidden).toBe(true)
    Object.defineProperty(chosen, 'files', { value: [] })
    chosen.dispatchEvent(new Event('change'))
    expect(chosen.isConnected).toBe(false)

    editor.commands.pickFiles('image')
    const cancelled = click.mock.contexts[1] as HTMLInputElement
    cancelled.dispatchEvent(new Event('cancel'))
    expect(cancelled.isConnected).toBe(false)

    // A picker that never answered is replaced by the next one, not left behind.
    editor.commands.pickFiles('image')
    editor.commands.pickFiles('file')
    expect(document.querySelectorAll('input[type=file]')).toHaveLength(1)
  })

  it('offers every type for a file', () => {
    const { editor } = setUp()
    const click = vi.spyOn(HTMLInputElement.prototype, 'click').mockImplementation(() => {})

    editor.commands.pickFiles('file')

    expect((click.mock.contexts[0] as HTMLInputElement).accept).toBe('')
  })

  it('does not open the picker on a read-only page', () => {
    const { editor } = setUp()
    editor.setEditable(false)
    const click = vi.spyOn(HTMLInputElement.prototype, 'click').mockImplementation(() => {})

    expect(editor.commands.pickFiles('image')).toBe(false)
    expect(click).not.toHaveBeenCalled()
  })
})
