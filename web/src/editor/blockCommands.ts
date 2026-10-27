import type { ChainedCommands, Editor, Range } from '@tiptap/core'
import type AppIcon from '../components/AppIcon.vue'

type IconName = InstanceType<typeof AppIcon>['$props']['name']
/** vue-i18n's `t`. Every call below spells its key out, so the lint rule that finds unused translations can see it. */
export type Translate = (key: string) => string

export type BlockCategory = 'basic' | 'lists' | 'blocks'

export interface BlockCommand {
  id: string
  category: BlockCategory
  icon: IconName
  label: (t: Translate) => string
  description: (t: Translate) => string
  /** Words that find the block, separated by spaces, in the language of the person. */
  keywords: (t: Translate) => string
  /** Makes the block at the caret, after removing `range` (what was typed to find it) when there is one. */
  run: (editor: Editor, range?: Range) => void
  /** Adds something new rather than changing the block, so it is not offered under "Turn into". */
  insertOnly?: boolean
}

/** The chain every command starts with: focused, and the typed `/query` gone. */
function begin(editor: Editor, range?: Range): ChainedCommands {
  const chain = editor.chain().focus()
  return range ? chain.deleteRange(range) : chain
}

/**
 * The blocks a person can insert, in the order of the menu. The slash menu and the toolbar read this list, so a new
 * block is added once. Each makes the node its Markdown shortcut makes.
 */
export const blockCommands: readonly BlockCommand[] = [
  {
    id: 'text',
    category: 'basic',
    icon: 'text',
    label: (t) => t('editor.blocks.text.label'),
    description: (t) => t('editor.blocks.text.description'),
    keywords: (t) => t('editor.blocks.text.keywords'),
    run: (editor, range) => void begin(editor, range).setParagraph().run(),
  },
  {
    id: 'heading1',
    category: 'basic',
    icon: 'heading',
    label: (t) => t('editor.blocks.heading1.label'),
    description: (t) => t('editor.blocks.heading1.description'),
    keywords: (t) => t('editor.blocks.heading1.keywords'),
    run: (editor, range) => void begin(editor, range).setHeading({ level: 1 }).run(),
  },
  {
    id: 'heading2',
    category: 'basic',
    icon: 'heading',
    label: (t) => t('editor.blocks.heading2.label'),
    description: (t) => t('editor.blocks.heading2.description'),
    keywords: (t) => t('editor.blocks.heading2.keywords'),
    run: (editor, range) => void begin(editor, range).setHeading({ level: 2 }).run(),
  },
  {
    id: 'heading3',
    category: 'basic',
    icon: 'heading',
    label: (t) => t('editor.blocks.heading3.label'),
    description: (t) => t('editor.blocks.heading3.description'),
    keywords: (t) => t('editor.blocks.heading3.keywords'),
    run: (editor, range) => void begin(editor, range).setHeading({ level: 3 }).run(),
  },
  {
    id: 'bulletList',
    category: 'lists',
    icon: 'bulletList',
    label: (t) => t('editor.blocks.bulletList.label'),
    description: (t) => t('editor.blocks.bulletList.description'),
    keywords: (t) => t('editor.blocks.bulletList.keywords'),
    run: (editor, range) => void begin(editor, range).toggleBulletList().run(),
  },
  {
    id: 'numberedList',
    category: 'lists',
    icon: 'numberedList',
    label: (t) => t('editor.blocks.numberedList.label'),
    description: (t) => t('editor.blocks.numberedList.description'),
    keywords: (t) => t('editor.blocks.numberedList.keywords'),
    run: (editor, range) => void begin(editor, range).toggleOrderedList().run(),
  },
  {
    id: 'checklist',
    category: 'lists',
    icon: 'checklist',
    label: (t) => t('editor.blocks.checklist.label'),
    description: (t) => t('editor.blocks.checklist.description'),
    keywords: (t) => t('editor.blocks.checklist.keywords'),
    run: (editor, range) => void begin(editor, range).toggleTaskList().run(),
  },
  {
    id: 'quote',
    category: 'blocks',
    icon: 'quote',
    label: (t) => t('editor.blocks.quote.label'),
    description: (t) => t('editor.blocks.quote.description'),
    keywords: (t) => t('editor.blocks.quote.keywords'),
    run: (editor, range) => void begin(editor, range).toggleBlockquote().run(),
  },
  {
    id: 'codeBlock',
    category: 'blocks',
    icon: 'code',
    label: (t) => t('editor.blocks.codeBlock.label'),
    description: (t) => t('editor.blocks.codeBlock.description'),
    keywords: (t) => t('editor.blocks.codeBlock.keywords'),
    run: (editor, range) => void begin(editor, range).setCodeBlock().run(),
  },
  {
    id: 'table',
    category: 'blocks',
    icon: 'table',
    label: (t) => t('editor.blocks.table.label'),
    description: (t) => t('editor.blocks.table.description'),
    keywords: (t) => t('editor.blocks.table.keywords'),
    run: (editor, range) =>
      void begin(editor, range).insertTable({ rows: 3, cols: 3, withHeaderRow: true }).run(),
  },
  {
    id: 'divider',
    category: 'blocks',
    icon: 'divider',
    label: (t) => t('editor.blocks.divider.label'),
    description: (t) => t('editor.blocks.divider.description'),
    keywords: (t) => t('editor.blocks.divider.keywords'),
    run: (editor, range) => void begin(editor, range).setHorizontalRule().run(),
  },
  {
    id: 'image',
    category: 'blocks',
    icon: 'image',
    label: (t) => t('editor.blocks.image.label'),
    description: (t) => t('editor.blocks.image.description'),
    keywords: (t) => t('editor.blocks.image.keywords'),
    run: (editor, range) => pickFiles(editor, range, 'image'),
    insertOnly: true,
  },
  {
    id: 'file',
    category: 'blocks',
    icon: 'file',
    label: (t) => t('editor.blocks.file.label'),
    description: (t) => t('editor.blocks.file.description'),
    keywords: (t) => t('editor.blocks.file.keywords'),
    run: (editor, range) => pickFiles(editor, range, 'file'),
    insertOnly: true,
  },
]

/** Opens the file picker; the file goes in once it is uploaded. Nothing happens in an editor that cannot upload. */
function pickFiles(editor: Editor, range: Range | undefined, kind: 'image' | 'file') {
  begin(editor, range).run()
  const commands: Partial<Editor['commands']> = editor.commands
  commands.pickFiles?.(kind)
}

/** Compares without case and accents: "kop" finds "Kop", "tekst" finds "Téxt". */
const collator = new Intl.Collator(undefined, { sensitivity: 'base' })

function startsWith(word: string, query: string): boolean {
  return word.length >= query.length && collator.compare(word.slice(0, query.length), query) === 0
}

/**
 * The commands whose label or search words have a word that starts with what was typed, in the order of the list.
 * Nothing typed yet gives them all.
 */
export function filterCommands(query: string, t: Translate): BlockCommand[] {
  const typed = query.trim()
  if (!typed) return [...blockCommands]
  return blockCommands.filter((command) => {
    const words = `${command.label(t)} ${command.keywords(t)}`.split(/\s+/)
    return words.some((word) => startsWith(word, typed))
  })
}
