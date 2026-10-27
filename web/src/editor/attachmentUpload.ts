import { Extension, type Editor } from '@tiptap/core'
import { Plugin } from '@tiptap/pm/state'
import type { EditorView } from '@tiptap/pm/view'
import {
  absolutePositionToRelativePosition,
  relativePositionToAbsolutePosition,
  ySyncPluginKey,
} from '@tiptap/y-tiptap'
import type * as Y from 'yjs'
import { ApiError } from '../api'

/** What the server answers for an upload. */
export interface UploadedAttachment {
  id: string
  fileName: string
  contentType: string
  size: number
  /** An image the server recognised: shown in the page. Anything else is a file to download. */
  inline: boolean
}

export interface AttachmentUploadOptions {
  upload: (file: File) => Promise<UploadedAttachment>
  /** A file could not be added; the others still are. */
  onError: (error: unknown) => void
  /** How many files are on their way, so the page can say so. */
  onBusyChange: (count: number) => void
}

export type PickKind = 'image' | 'file'

/** The four types the server shows in a page; the picker for images offers only these. */
const IMAGE_TYPES = 'image/png,image/jpeg,image/gif,image/webp'

declare module '@tiptap/core' {
  interface Commands<ReturnType> {
    attachmentUpload: {
      /** Opens the file picker; what is chosen is added at the caret. */
      pickFiles: (kind: PickKind) => ReturnType
    }
  }
}

/**
 * Where an upload goes, kept as a Yjs relative position: it stays next to the same text while others edit the page
 * during the upload. (A ProseMirror position would not: a change from another client replaces the whole document.)
 */
export interface Place {
  resolve: () => number | null
}

function placeAt(editor: Editor, pos: number): Place {
  const binding = ySyncPluginKey.getState(editor.state)?.binding as
    | {
        type: Y.XmlFragment
        doc: Y.Doc
        mapping: Parameters<typeof absolutePositionToRelativePosition>[2]
      }
    | undefined
  if (!binding) return { resolve: () => Math.min(pos, editor.state.doc.content.size) }
  const relative = absolutePositionToRelativePosition(pos, binding.type, binding.mapping)
  return {
    resolve: () =>
      relativePositionToAbsolutePosition(binding.doc, binding.type, relative, binding.mapping),
  }
}

/**
 * Uploads the files one after another and puts each where the caret was, after the one before. The caret itself
 * stays where the person has it now: they may have gone on typing elsewhere while the files were on their way.
 */
export async function addFiles(
  editor: Editor,
  options: AttachmentUploadOptions,
  files: readonly File[],
  where: number | Place,
): Promise<void> {
  if (files.length === 0) return
  let place = typeof where === 'number' ? placeAt(editor, where) : where
  let waiting = files.length
  let notAdded = 0
  options.onBusyChange(waiting)
  try {
    for (const [index, file] of files.entries()) {
      try {
        const uploaded = await options.upload(file)
        // The page was closed or became read-only, or the text the files belonged to was deleted: this file and the
        // ones after it have nowhere to go.
        const at = editor.isDestroyed || !editor.isEditable ? null : place.resolve()
        if (at === null) {
          notAdded = files.length - index
          return
        }
        const node = uploaded.inline
          ? { type: 'image', attrs: { attachmentId: uploaded.id, alt: '' } }
          : {
              type: 'attachment',
              attrs: {
                attachmentId: uploaded.id,
                fileName: uploaded.fileName,
                size: uploaded.size,
              },
            }
        // The next file goes right after this one, wherever the insertion put it (an image splits a paragraph, or
        // takes the place of an empty one).
        let after = at
        editor
          .chain()
          .insertContentAt(at, node, { updateSelection: false })
          .command(({ tr }) => {
            after = tr.mapping.map(at, 1)
            return true
          })
          .run()
        place = placeAt(editor, after)
      } catch (error) {
        options.onError(error)
      } finally {
        options.onBusyChange(--waiting)
      }
    }
  } finally {
    if (waiting > 0) options.onBusyChange(0)
    if (notAdded > 0) options.onError(new ApiError(0, 'attachment_not_added'))
  }
}

/** The picker that is open, kept in the page until it answers: iOS Safari sends nothing to an input outside it. */
let openPicker: HTMLInputElement | null = null

function pick(kind: PickKind, chosen: (files: File[]) => void) {
  openPicker?.remove()
  const input = document.createElement('input')
  input.type = 'file'
  input.multiple = true
  input.hidden = true
  if (kind === 'image') input.accept = IMAGE_TYPES
  const done = () => {
    input.remove()
    if (openPicker === input) openPicker = null
  }
  input.addEventListener(
    'change',
    () => {
      const files = [...(input.files ?? [])]
      done()
      chosen(files)
    },
    { once: true },
  )
  input.addEventListener('cancel', done, { once: true })
  document.body.append(input)
  openPicker = input
  input.click()
}

/**
 * Files from the clipboard. A screenshot or a copied file comes as files only. Office and some browsers add a picture
 * of what was copied next to its text, and then the text is what the person meant.
 */
function pastedFiles(event: ClipboardEvent): File[] {
  const data = event.clipboardData
  if (!data || data.files.length === 0) return []
  if (data.getData('text/plain').trim() !== '') return []
  return [...data.files]
}

export const AttachmentUpload = Extension.create<AttachmentUploadOptions>({
  name: 'attachmentUpload',

  addOptions() {
    return {
      upload: () => Promise.reject(new Error('no upload configured')),
      onError: () => {},
      onBusyChange: () => {},
    }
  },

  addCommands() {
    return {
      pickFiles:
        (kind) =>
        ({ editor }) => {
          if (!editor.isEditable) return false
          // Kept as a relative position from now on: the picker can stay open while others edit the page.
          const place = placeAt(editor, editor.state.selection.from)
          pick(kind, (files) => void addFiles(editor, this.options, files, place))
          return true
        },
    }
  },

  addProseMirrorPlugins() {
    const editor = this.editor
    const options = this.options
    return [
      new Plugin({
        props: {
          handlePaste(view: EditorView, event: ClipboardEvent) {
            if (!view.editable) return false
            const files = pastedFiles(event)
            if (files.length === 0) return false
            void addFiles(editor, options, files, view.state.selection.from)
            return true
          },
          handleDrop(view: EditorView, event: DragEvent, _slice, moved: boolean) {
            const files = event.dataTransfer?.files
            if (!view.editable || moved || !files || files.length === 0) return false
            const pos =
              view.posAtCoords({ left: event.clientX, top: event.clientY })?.pos ??
              view.state.selection.from
            void addFiles(editor, options, [...files], pos)
            return true
          },
        },
      }),
    ]
  },
})
