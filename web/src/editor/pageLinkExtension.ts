import { Extension, type Editor, type Range } from '@tiptap/core'
import { PluginKey } from '@tiptap/pm/state'
import { exitSuggestion, Suggestion } from '@tiptap/suggestion'
import { reactive } from 'vue'
import type { PageNode } from '../tree/types'
import { pageHref } from './links'
import { findPages, type PageChoice } from './pageSearch'
import type { AnchorRect } from './slashExtension'

export const PAGE_MENU_LISTBOX_ID = 'page-link-menu-listbox'
export function pageOptionId(choice: PageChoice): string {
  return `page-link-option-${choice.id}`
}

/** What the page menu shows. The extension writes it, `PageLinkMenu.vue` reads it. */
export interface PageMenuState {
  open: boolean
  query: string
  items: PageChoice[]
  selected: number
  anchor: AnchorRect | null
  /** Makes the link and removes what was typed; set while the menu is open. */
  choose: (choice: PageChoice) => void
  /** Points at an item, as the mouse does. */
  point: (index: number) => void
}

export function createPageMenuState(): PageMenuState {
  const state: PageMenuState = reactive({
    open: false,
    query: '',
    items: [],
    selected: 0,
    anchor: null,
    choose: () => {},
    point: (index: number) => {
      state.selected = index
    },
  })
  return state
}

const pluginKey = new PluginKey('pageLinkMenu')

/** The editor's element is the combobox of the pattern, as for the slash menu: it keeps the focus and names the chosen option. */
function describeMenu(dom: HTMLElement, state: PageMenuState) {
  if (!state.open) {
    dom.removeAttribute('aria-controls')
    dom.removeAttribute('aria-haspopup')
    dom.removeAttribute('aria-activedescendant')
    return
  }
  dom.setAttribute('aria-controls', PAGE_MENU_LISTBOX_ID)
  dom.setAttribute('aria-haspopup', 'listbox')
  const chosen = state.items[state.selected]
  if (chosen) dom.setAttribute('aria-activedescendant', pageOptionId(chosen))
  else dom.removeAttribute('aria-activedescendant')
}

/**
 * Puts a link to a page where `range` is: the page's title as text, with a link mark whose address is `/p/<id>`, and the caret
 * after it. TipTap's link mark continues when typing goes on at its end (it is inclusive while autolink is on), which a link
 * that was just chosen must not do: the link mark is switched off for what comes next.
 */
export function insertPageLink(editor: Editor, range: Range, choice: PageChoice, untitled: string) {
  editor
    .chain()
    .focus()
    .insertContentAt(range, {
      type: 'text',
      text: choice.title || untitled,
      marks: [{ type: 'link', attrs: { href: pageHref(choice.id) } }],
    })
    .unsetMark('link')
    .run()
}

export interface PageLinkOptions {
  state: PageMenuState
  /** The live pages of the workspace, in the order of the sidebar: read for every query, so a page made a moment ago is found. */
  pages: () => readonly PageNode[]
  /** The page this editor is on: never offered. */
  currentPageId: () => string
  /** What a page without a title is called. */
  untitled: () => string
}

/**
 * `[[` at the start of a line or after a space opens a menu of the pages whose title contains what is typed after it
 * (titles have spaces, so the query goes on across them until the caret leaves, a `]` is typed or Escape is pressed).
 * Choosing one replaces `[[` and the query with a link to it. Never in a code block or a read-only editor. The trigger, the
 * query and the range to replace are TipTap's, as for the slash menu.
 */
export function createPageLinkExtension({
  state,
  pages,
  currentPageId,
  untitled,
}: PageLinkOptions) {
  return Extension.create({
    name: 'pageLinkMenu',
    addProseMirrorPlugins() {
      return [
        Suggestion<PageChoice, PageChoice>({
          editor: this.editor,
          pluginKey,
          char: '[[',
          allowedPrefixes: [' '],
          allowSpaces: true,
          allow: ({ editor, state: editorState, range }) =>
            editor.isEditable &&
            !editorState.doc.resolve(range.from).parent.type.spec.code &&
            // "[[plan]]" is finished: the second bracket closes what was begun.
            !editorState.doc.textBetween(range.from, range.to, ' ').includes(']'),
          items: ({ query }) => findPages(pages(), query, currentPageId()),
          command: ({ editor, range, props }) => insertPageLink(editor, range, props, untitled()),
          render: () => {
            const show = (
              editor: Editor,
              query: string,
              items: PageChoice[],
              clientRect: (() => DOMRect | null) | null | undefined,
              choose: (choice: PageChoice) => void,
            ) => {
              // A new query starts at the top of the list; the same query keeps what was chosen.
              state.selected =
                query === state.query && state.open
                  ? Math.min(state.selected, Math.max(items.length - 1, 0))
                  : 0
              state.open = true
              state.query = query
              state.items = items
              state.choose = choose
              const rect = clientRect?.()
              if (rect) {
                state.anchor = {
                  left: rect.left,
                  top: rect.top,
                  right: rect.right,
                  bottom: rect.bottom,
                }
              }
              describeMenu(editor.view.dom, state)
            }

            return {
              onStart: (props) =>
                show(props.editor, props.query, props.items, props.clientRect, props.command),
              onUpdate: (props) =>
                show(props.editor, props.query, props.items, props.clientRect, props.command),
              onExit: (props) => {
                state.open = false
                state.query = ''
                state.items = []
                state.selected = 0
                describeMenu(props.editor.view.dom, state)
              },
              onKeyDown: ({ event, view }) => {
                const count = state.items.length
                switch (event.key) {
                  case 'ArrowDown':
                    if (!count) return false
                    state.selected = (state.selected + 1) % count
                    describeMenu(view.dom, state)
                    return true
                  case 'ArrowUp':
                    if (!count) return false
                    state.selected = (state.selected - 1 + count) % count
                    describeMenu(view.dom, state)
                    return true
                  case 'Enter': {
                    const chosen = state.items[state.selected]
                    // With nothing found Enter is a line break, as anywhere in the text: the menu stays out of the way.
                    if (!chosen) return false
                    state.choose(chosen)
                    return true
                  }
                  case 'Tab':
                    // Closes the menu and lets the key do what it does in the page: move on.
                    exitSuggestion(view, pluginKey)
                    return false
                  default:
                    return false
                }
              },
            }
          },
        }),
      ]
    },
  })
}
