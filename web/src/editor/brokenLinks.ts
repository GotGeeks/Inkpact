import { Extension } from '@tiptap/core'
import { Plugin, PluginKey } from '@tiptap/pm/state'
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

function decorationsFor(doc: ProseMirrorNode, options: BrokenLinksOptions): DecorationSet {
  const { stateOf, describe } = options
  if (!stateOf) return DecorationSet.empty

  const found: Decoration[] = []
  doc.descendants((node, pos, parent, index) => {
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
    const next = parent?.maybeChild((index ?? 0) + 1)
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
  return DecorationSet.create(doc, found)
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
          apply: (transaction, previous) =>
            transaction.docChanged || transaction.getMeta(brokenLinksKey)
              ? decorationsFor(transaction.doc, options)
              : previous,
        },
        props: {
          decorations: (state) => brokenLinksKey.getState(state),
        },
      }),
    ]
  },
})
