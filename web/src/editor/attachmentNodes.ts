import { Node } from '@tiptap/core'
import { NodeSelection, Plugin } from '@tiptap/pm/state'

const ATTACHMENT_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export function isAttachmentId(value: unknown): value is string {
  return typeof value === 'string' && ATTACHMENT_ID.test(value)
}

/**
 * The address of an attachment, or null when the id is not one. The document holds only the id, and it comes from
 * other clients through Yjs without passing any paste handler, so it is checked every time it becomes an address:
 * nothing in a document can point anywhere but at the app's own attachments.
 */
export function attachmentUrl(id: unknown): string | null {
  return isAttachmentId(id) ? `/api/attachments/${id.toLowerCase()}` : null
}

/**
 * An image on its own line, shown from an attachment. Not TipTap's image extension: that one stores an address, and an
 * address in a shared document is one more thing someone could point elsewhere.
 */
export const AttachmentImage = Node.create({
  name: 'image',
  group: 'block',
  atom: true,
  draggable: true,

  addAttributes() {
    return {
      attachmentId: {
        default: null,
        parseHTML: (element) => element.getAttribute('data-attachment-id'),
        renderHTML: () => ({}),
      },
      alt: {
        default: '',
        parseHTML: (element) => element.getAttribute('alt') ?? '',
        renderHTML: () => ({}),
      },
    }
  },

  parseHTML() {
    return [{ tag: 'img[data-attachment-id]' }]
  },

  renderHTML({ node }) {
    const src = attachmentUrl(node.attrs.attachmentId)
    // A bad id makes an image without an address: the browser shows the description, and nothing is loaded.
    return [
      'img',
      {
        'data-attachment-id': src ? node.attrs.attachmentId : '',
        alt: node.attrs.alt ?? '',
        ...(src ? { src } : {}),
        loading: 'lazy',
        class: 'my-space-md block h-auto max-w-full rounded-md',
      },
    ]
  },
})

export interface AttachmentFileOptions {
  /** The size as people read it, in the language of the page. */
  formatSize: (bytes: number) => string
  /** What a screen reader says for the link. */
  downloadLabel: (fileName: string, size: string) => string
}

/** A file in the text, shown as its name and size; clicking it downloads it. */
export const AttachmentFile = Node.create<AttachmentFileOptions>({
  name: 'attachment',
  group: 'inline',
  inline: true,
  atom: true,
  draggable: true,

  addOptions() {
    return { formatSize: (bytes) => String(bytes), downloadLabel: (fileName) => fileName }
  },

  addAttributes() {
    return {
      attachmentId: {
        default: null,
        parseHTML: (element) => element.getAttribute('data-attachment-id'),
        renderHTML: () => ({}),
      },
      fileName: {
        default: '',
        parseHTML: (element) => element.getAttribute('data-file-name') ?? element.textContent ?? '',
        renderHTML: () => ({}),
      },
      size: {
        default: 0,
        parseHTML: (element) => Number(element.getAttribute('data-size')) || 0,
        renderHTML: () => ({}),
      },
    }
  },

  parseHTML() {
    // Before the link mark, which would otherwise take the element as a link.
    return [{ tag: 'a[data-attachment-id]', priority: 1000 }]
  },

  renderHTML({ node }) {
    const href = attachmentUrl(node.attrs.attachmentId)
    const fileName = String(node.attrs.fileName ?? '')
    const size = this.options.formatSize(Number(node.attrs.size) || 0)
    return [
      'a',
      {
        'data-attachment-id': href ? node.attrs.attachmentId : '',
        'data-file-name': fileName,
        'data-size': String(node.attrs.size ?? 0),
        ...(href ? { href, download: fileName } : {}),
        'aria-label': this.options.downloadLabel(fileName, size),
        class:
          'inline-flex items-baseline gap-space-xs rounded-sm border border-border-subtle bg-surface-subtle px-space-xs no-underline',
      },
      ['span', { class: 'text-on-surface' }, fileName],
      ['span', { class: 'text-body-sm text-on-surface-variant' }, size],
    ]
  },

  addKeyboardShortcuts() {
    return {
      // The file is selected as a whole (arrow keys reach it); Enter downloads it, as a click does.
      Enter: ({ editor }) => {
        const { selection } = editor.state
        if (!(selection instanceof NodeSelection) || selection.node.type.name !== this.name)
          return false
        return download(selection.node.attrs)
      },
    }
  },

  addProseMirrorPlugins() {
    return [
      new Plugin({
        props: {
          handleDOMEvents: {
            // One way to download for every page: the link's own click is cancelled (in an editable page it may or
            // may not follow it, in a read-only one it would, next to this), and a detached link is clicked instead.
            click(view, event) {
              const target = event.target instanceof Element ? event.target : null
              const link = target?.closest('a[data-attachment-id]')
              if (!link || !view.dom.contains(link) || event.button !== 0) return false
              event.preventDefault()
              download({
                attachmentId: link.getAttribute('data-attachment-id'),
                fileName: link.getAttribute('data-file-name'),
              })
              return true
            },
          },
        },
      }),
    ]
  },
})

/** Downloads through a link that is not in the page, so a failed download never sends the page away. */
function download(attrs: { attachmentId?: unknown; fileName?: unknown }): boolean {
  const href = attachmentUrl(attrs.attachmentId)
  if (!href) return false
  const link = document.createElement('a')
  link.href = href
  link.download = String(attrs.fileName ?? '')
  link.click()
  return true
}
