import { Extension } from '@tiptap/core'
import { Plugin, type Transaction } from '@tiptap/pm/state'
import { ySyncPluginKey } from '@tiptap/y-tiptap'
import { changedRanges } from './changedRanges'
import { normalizeLinkHref } from './links'

/** Whether a transaction is another client's change arriving through Yjs, not something this person did. */
function isRemote(transaction: Transaction): boolean {
  return transaction.getMeta(ySyncPluginKey)?.isChangeOrigin === true
}

/**
 * A link that this person makes (a paste, a typed address that becomes a link, the picker) to a page of this app is stored
 * as `/p/<id>`, whichever way it was written: the address bar and the clipboard have the full address, which the index of
 * links does not count and which would stop working with another host name (run B.1b). Only what a local transaction
 * changed is looked at, and never a change that came from another client: rewriting those would write edits that nobody
 * made, on every client at once. A link that was pasted before this stays as it was.
 */
export const PageLinkNormalizer = Extension.create({
  name: 'pageLinkNormalizer',

  addProseMirrorPlugins() {
    return [
      new Plugin({
        appendTransaction: (transactions, _before, state) => {
          const local = transactions.filter(
            (transaction) => transaction.docChanged && !isRemote(transaction),
          )
          if (local.length === 0) return null
          const linkType = state.schema.marks.link
          if (!linkType) return null

          // What each changed, in the document as it is now.
          let ranges: [number, number][] = []
          for (const transaction of transactions) {
            ranges = ranges.map(([from, to]) => [
              transaction.mapping.map(from, -1),
              transaction.mapping.map(to, 1),
            ])
            if (local.includes(transaction)) ranges.push(...changedRanges(transaction))
          }

          const tr = state.tr
          let changed = false
          for (const [from, to] of ranges) {
            const size = state.doc.content.size
            state.doc.nodesBetween(
              Math.max(0, Math.min(from, size)),
              Math.max(0, Math.min(to, size)),
              (node, pos) => {
                if (!node.isText) return
                const mark = node.marks.find((candidate) => candidate.type === linkType)
                const href = mark?.attrs.href as unknown
                if (!mark || typeof href !== 'string') return
                const normalized = normalizeLinkHref(href)
                if (normalized === href) return
                tr.removeMark(pos, pos + node.nodeSize, linkType)
                tr.addMark(
                  pos,
                  pos + node.nodeSize,
                  linkType.create({ ...mark.attrs, href: normalized }),
                )
                changed = true
              },
            )
          }
          return changed ? tr : null
        },
      }),
    ]
  },
})
