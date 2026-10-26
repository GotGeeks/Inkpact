import Collaboration from '@tiptap/extension-collaboration'
import { TaskItem, TaskList } from '@tiptap/extension-list'
import { Table, TableCell, TableHeader, TableRow } from '@tiptap/extension-table'
import { Placeholder } from '@tiptap/extensions'
import { mergeAttributes, type Extensions } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import type * as Y from 'yjs'
import { FIELD } from './firstParagraph'
import type { Translate } from './blockCommands'
import { isAllowedLink } from './links'
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
}

export function createExtensions({
  doc,
  placeholder,
  checkboxLabel,
  slash,
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
    Collaboration.configure({ document: doc, field: FIELD }),
    TaskList,
    TaskItem.configure({
      nested: true,
      a11y: { checkboxLabel: (_node, checked) => checkboxLabel(checked) },
    }),
    InkpactTable.configure({ resizable: false }),
    TableRow,
    TableHeader,
    TableCell,
    Placeholder.configure({ placeholder }),
    ...(slash ? [createSlashExtension(slash.state, slash.translate)] : []),
  ]
}
