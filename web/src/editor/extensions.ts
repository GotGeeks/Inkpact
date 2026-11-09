import Collaboration from '@tiptap/extension-collaboration'
import { DragHandle } from '@tiptap/extension-drag-handle'
import { TaskItem, TaskList } from '@tiptap/extension-list'
import { Table, TableCell, TableHeader, TableRow } from '@tiptap/extension-table'
import { Placeholder } from '@tiptap/extensions'
import { mergeAttributes, type Extensions } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import type * as Y from 'yjs'
import { AttachmentFile, AttachmentImage, type AttachmentFileOptions } from './attachmentNodes'
import { AttachmentUpload, type AttachmentUploadOptions } from './attachmentUpload'
import { FIELD } from './firstParagraph'
import { createMoveBlockExtension } from './blockActions'
import type { Translate } from './blockCommands'
import { isAllowedLink } from './links'
import { BrokenLinks, type BrokenLinksOptions } from './brokenLinks'
import { LinkOpening } from './linkOpening'
import { LinkShortcut, type LinkShortcutOptions } from './linkShortcut'
import { PageLinkNormalizer } from './pageLinkNormalizer'
import { createPageLinkExtension, type PageLinkOptions } from './pageLinkExtension'
import { createSlashExtension, type SlashState } from './slashExtension'

/**
 * TipTap's table writes a `style` attribute (a minimum width, and widths on the columns), which the strict policy
 * blocks. Without resizing it needs neither.
 */
const InkpactTable = Table.extend({
  renderHTML({ HTMLAttributes }) {
    return ['table', mergeAttributes(this.options.HTMLAttributes, HTMLAttributes), ['tbody', 0]]
  },
})

export interface EditorExtensionsOptions {
  doc: Y.Doc
  placeholder: string
  /** The label of a task item's checkbox; the node view calls it for every item. */
  checkboxLabel: (checked: boolean) => string
  /** The slash menu; without it (the tests that only need the schema) `/` is just a character. */
  slash?: { state: SlashState; translate: Translate }
  /** The menu of pages that `[[` opens; without it (most tests) `[[` is just two characters. */
  pageLinks?: PageLinkOptions
  /** Told when Alt+Arrow has moved a block. */
  onBlockMoved?: (direction: -1 | 1) => void
  /** The handle next to a block: the element it moves along the page, and which block the mouse is over. */
  dragHandle?: { element: HTMLElement; onHover: (pos: number) => void }
  /** Opens a link that was clicked; without it (a test that only needs the schema) a click on a link does nothing. */
  openLink?: (href: string, newTab: boolean) => void
  /** Ctrl+K on selected text or in a link; without it (most tests) the key is not taken by the editor. */
  linkShortcut?: LinkShortcutOptions
  /** Strikes through a link to a page that is in the trash or not to be found (run B.1); without it (most tests) no link is marked. */
  brokenLinks?: BrokenLinksOptions
  /** How a file in the text reads. */
  files?: AttachmentFileOptions
  /** Adding files from the picker, a paste or a drop; without it (a read-only page, most tests) there is no way in. */
  upload?: AttachmentUploadOptions
  /**
   * Told when the page holds something this schema does not know (a block from a newer version of the app). y-tiptap
   * then shows what it can and writes nothing to the page until it fits again; without this callback it would delete
   * that content from the page for everybody, so it is always passed.
   */
  onInvalidContent?: (error: Error) => void
}

export function createExtensions({
  doc,
  placeholder,
  checkboxLabel,
  slash,
  pageLinks,
  onBlockMoved = () => {},
  dragHandle,
  files,
  upload,
  openLink,
  brokenLinks,
  linkShortcut,
  onInvalidContent = () => {},
}: EditorExtensionsOptions): Extensions {
  return [
    StarterKit.configure({
      // Yjs has its own undo manager, which only undoes what this client did; the ProseMirror one must be off.
      undoRedo: false,
      // Six levels, so an import loses none; the design styles three.
      heading: { levels: [1, 2, 3, 4, 5, 6] },
      link: {
        openOnClick: false,
        autolink: true,
        linkOnPaste: true,
        // The same rule when a link is made, pasted, and rendered: a `javascript:` link that another client put into
        // the document gets no address.
        isAllowedUri: (url) => isAllowedLink(url),
        HTMLAttributes: { rel: 'noopener noreferrer nofollow', target: '_blank' },
      },
    }),
    LinkOpening.configure({ open: openLink }),
    BrokenLinks.configure(brokenLinks ?? {}),
    PageLinkNormalizer,
    LinkShortcut.configure(linkShortcut ?? {}),
    Collaboration.configure({ document: doc, field: FIELD, ySyncOptions: { onInvalidContent } }),
    TaskList,
    TaskItem.configure({
      nested: true,
      a11y: { checkboxLabel: (_node, checked) => checkboxLabel(checked) },
    }),
    InkpactTable.configure({ resizable: false }),
    TableRow,
    TableHeader,
    TableCell,
    // In every editor, so every client's schema knows them; only adding files needs `upload`.
    AttachmentImage,
    files ? AttachmentFile.configure(files) : AttachmentFile,
    ...(upload ? [AttachmentUpload.configure(upload)] : []),
    Placeholder.configure({ placeholder }),
    createMoveBlockExtension({ onMoved: onBlockMoved }),
    ...(dragHandle
      ? [
          DragHandle.configure({
            render: () => dragHandle.element,
            // The plugin also tells where the block is, which its options leave out.
            onNodeChange: (({ pos }: { pos: number }) =>
              dragHandle.onHover(pos)) as unknown as () => void,
          }),
        ]
      : []),
    ...(slash ? [createSlashExtension(slash.state, slash.translate)] : []),
    ...(pageLinks ? [createPageLinkExtension(pageLinks)] : []),
  ]
}
