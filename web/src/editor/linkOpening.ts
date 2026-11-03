import { Extension } from '@tiptap/core'
import { Plugin } from '@tiptap/pm/state'
import type { Router } from 'vue-router'
import { isAllowedLink, pageIdOfLink } from './links'

export interface LinkOpeningOptions {
  /** Opens a link's address: `newTab` when the click held Ctrl, Cmd or Shift. Without it a click on a link only places the cursor. */
  open?: (href: string, newTab: boolean) => void
}

/**
 * A click on a link opens it. The link mark does not do that itself (`openOnClick` is off, since a mark that is opened by a
 * click is also a mark nobody can place the cursor in), so this does, for the addresses the editor allows only: what a
 * client other than this one put into the document gets no further than the rule in `links.ts`.
 */
export const LinkOpening = Extension.create<LinkOpeningOptions>({
  name: 'linkOpening',

  addOptions() {
    return { open: undefined }
  },

  addProseMirrorPlugins() {
    const open = this.options.open
    return [
      new Plugin({
        props: {
          handleClick: (_view, _pos, event) => {
            if (!open || event.button !== 0) return false
            const anchor = (event.target as HTMLElement | null)?.closest?.('a[href]')
            const href = anchor?.getAttribute('href')
            if (!href || !isAllowedLink(href)) return false
            event.preventDefault()
            open(href, event.ctrlKey || event.metaKey || event.shiftKey)
            return true
          },
        },
      }),
    ]
  },
})

/**
 * What opening a link does here: a page of this app goes through the router in the same tab (or opens in a new one when
 * asked), a mail address goes to the mail program, anything else opens in a new tab without a way back to this one.
 */
export function openLinkIn(router: Pick<Router, 'push' | 'resolve'>) {
  return (href: string, newTab: boolean) => {
    const pageId = pageIdOfLink(href)
    if (pageId) {
      const route = { name: 'page', params: { pageId } }
      if (newTab) window.open(router.resolve(route).href, '_blank', 'noopener,noreferrer')
      else void router.push(route)
    } else if (href.startsWith('mailto:')) {
      window.location.assign(href)
    } else {
      window.open(href, '_blank', 'noopener,noreferrer')
    }
  }
}
