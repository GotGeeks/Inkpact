import { Extension } from '@tiptap/core'
import { Plugin } from '@tiptap/pm/state'
import type { Router } from 'vue-router'
import { isAllowedLink, pageIdOfLink } from './links'

export interface LinkOpeningOptions {
  /** Opens a link's address; `newTab` when the click held Ctrl or Cmd. Without it a click on a link does what the browser does. */
  open?: (href: string, newTab: boolean) => void
}

/** The address of the link a click landed on inside the editor, when the editor allows it (`links.ts`); null otherwise. */
export function clickedLink(root: HTMLElement, target: EventTarget | null): string | null {
  const anchor = target instanceof Element ? target.closest('a[href]') : null
  const href = anchor && root.contains(anchor) ? anchor.getAttribute('href') : null
  return href && isAllowedLink(href) ? href : null
}

/**
 * Opening a link with a click. While the page is being edited, a plain click or tap places the cursor in the link, as in any
 * text (the link menu then offers to open it), and Ctrl or Cmd with the click opens it. On a page that cannot be edited, a
 * click opens it. Either way it is the click event that is taken, so the browser does not also follow the link. Shift with
 * the click is left alone: it extends the selection. Only addresses the editor allows are opened, so what another client
 * put into the document gets no further than the rule in `links.ts`.
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
          handleDOMEvents: {
            click: (view, event) => {
              if (!open || event.button !== 0 || event.shiftKey || event.altKey) return false
              const newTab = event.ctrlKey || event.metaKey
              if (view.editable && !newTab) return false
              const href = clickedLink(view.dom, event.target)
              if (!href) return false
              event.preventDefault()
              open(href, newTab)
              return true
            },
          },
        },
      }),
    ]
  },
})

/** The scheme as a browser reads it (in lowercase, `MAILTO:` included), or null for an address that is not absolute. */
function protocolOf(href: string): string | null {
  try {
    return new URL(href).protocol
  } catch {
    return null
  }
}

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
    } else if (protocolOf(href) === 'mailto:') {
      window.location.assign(href)
    } else {
      window.open(href, '_blank', 'noopener,noreferrer')
    }
  }
}
