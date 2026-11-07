import { Extension } from '@tiptap/core'
import { Plugin, PluginKey, type Transaction } from '@tiptap/pm/state'
import type { Node as ProseMirrorNode } from '@tiptap/pm/model'
import { Decoration, DecorationSet, type EditorView } from '@tiptap/pm/view'
import { pageIdOfLink } from './links'

/** How the page a link points to is now: there, in the trash, or not to be found (gone, under a page in the trash, or another workspace's). */
export type LinkTarget = 'live' | 'trash' | 'unavailable'

export interface BrokenLinksOptions {
  /** What a page of the app is now. Without it no link is marked. */
  stateOf?: (pageId: string) => LinkTarget
  /** The reason in words, for the title and for a screen reader. */
  describe?: (state: Exclude<LinkTarget, 'live'>) => string
}

export const brokenLinksKey = new PluginKey<DecorationSet>('brokenLinks')

/** Marks the links of the page again, for a change that is not in the document: the tree changed, so the page a link points to did. */
export function refreshBrokenLinks(view: EditorView) {
  view.dispatch(view.state.tr.setMeta(brokenLinksKey, 'refresh'))
}

const BROKEN = 'line-through text-on-surface-variant'

function screenReaderNote(text: string): HTMLElement {
  const note = document.createElement('span')
  note.className = 'sr-only'
  note.textContent = ` (${text})`
  return note
}

/** The decorations of the broken links in `from`..`to` of the document. */
function scan(
  doc: ProseMirrorNode,
  from: number,
  to: number,
  options: BrokenLinksOptions,
): Decoration[] {
  const { stateOf, describe } = options
  if (!stateOf) return []

  const found: Decoration[] = []
  doc.nodesBetween(from, to, (node, pos, parent, index) => {
    if (!node.isText) return
    const href = node.marks.find((mark) => mark.type.name === 'link')?.attrs.href as unknown
    const pageId = typeof href === 'string' ? pageIdOfLink(href) : null
    if (!pageId) return
    const state = stateOf(pageId)
    if (state === 'live') return

    const reason = describe?.(state) ?? ''
    found.push(
      Decoration.inline(pos, pos + node.nodeSize, {
        class: BROKEN,
        'data-broken-link': state,
        ...(reason ? { title: reason } : {}),
      }),
    )
    // Said once, after the last piece of the link (a link with some bold in it is several text nodes).
    const next = parent?.maybeChild(index + 1)
    const continues = next?.marks.some(
      (mark) => mark.type.name === 'link' && mark.attrs.href === href,
    )
    if (reason && !continues) {
      found.push(
        Decoration.widget(pos + node.nodeSize, () => screenReaderNote(reason), {
          side: 1,
          key: `broken-${state}-${pageId}`,
        }),
      )
    }
  })
  return found
}

function decorationsFor(doc: ProseMirrorNode, options: BrokenLinksOptions): DecorationSet {
  return options.stateOf
    ? DecorationSet.create(doc, scan(doc, 0, doc.content.size, options))
    : DecorationSet.empty
}

/** The whole top-level block around a position: a link and the piece after it are always in one. */
function blockAround(doc: ProseMirrorNode, from: number, to: number): [number, number] {
  const $from = doc.resolve(Math.min(Math.max(from, 0), doc.content.size))
  const $to = doc.resolve(Math.min(Math.max(to, 0), doc.content.size))
  return [$from.depth > 0 ? $from.before(1) : $from.pos, $to.depth > 0 ? $to.after(1) : $to.pos]
}

/**
 * The decorations after a change to the document: those of before, moved along with it, and the blocks that the change
 * touched looked at again. Typing into a long page with many links costs what the paragraph is long, not the page.
 */
function decorationsAfter(
  transaction: Transaction,
  previous: DecorationSet,
  options: BrokenLinksOptions,
): DecorationSet {
  if (!options.stateOf) return DecorationSet.empty
  const doc = transaction.doc
  // The blocks to look at again, each once, by their range in the document as it is now.
  const blocks = new Map<string, [number, number]>()
  const rescan = (start: number, end: number) => {
    const [from, to] = blockAround(doc, start, end)
    blocks.set(`${from}:${to}`, [from, to])
  }
  transaction.steps.forEach((step, index) => {
    // What the step replaced, in the document as it is now.
    const after = transaction.mapping.slice(index + 1)
    step.getMap().forEach((_oldStart, _oldEnd, newStart, newEnd) => {
      rescan(after.map(newStart, -1), after.map(newEnd, 1))
    })
    // A step that changes marks or attributes moves nothing, so its map is empty: its own range says where it was.
    const { from, to } = step as unknown as { from?: unknown; to?: unknown }
    if (typeof from === 'number' && typeof to === 'number') {
      const through = transaction.mapping.slice(index)
      rescan(through.map(from, -1), through.map(to, 1))
    }
  })

  let set = previous.map(transaction.mapping, doc)
  for (const [from, to] of blocks.values()) {
    set = set.remove(set.find(from, to)).add(doc, scan(doc, from, to, options))
  }
  return set
}

/**
 * A link to a page that is in the trash or not to be found stays where it is, struck through, with the reason (run B.1).
 * Only a decoration: nothing is written into the document, so the link is whole again the moment the page is restored, and
 * a client that cannot see the page (another workspace) does not change what the others see. The page is looked up when the
 * document changes and when `refreshBrokenLinks` says the pages did.
 */
export const BrokenLinks = Extension.create<BrokenLinksOptions>({
  name: 'brokenLinks',

  addOptions() {
    return { stateOf: undefined, describe: undefined }
  },

  addProseMirrorPlugins() {
    const options = this.options
    return [
      new Plugin<DecorationSet>({
        key: brokenLinksKey,
        state: {
          init: (_config, state) => decorationsFor(state.doc, options),
          apply: (transaction, previous) => {
            // The pages changed (the tree): every link may have. Otherwise only what the change touched.
            if (transaction.getMeta(brokenLinksKey)) return decorationsFor(transaction.doc, options)
            return transaction.docChanged
              ? decorationsAfter(transaction, previous, options)
              : previous
          },
        },
        props: {
          decorations: (state) => brokenLinksKey.getState(state),
        },
      }),
    ]
  },
})
