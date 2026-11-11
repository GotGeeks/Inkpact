import { Extension, type Editor, type Range } from '@tiptap/core'
import { PluginKey } from '@tiptap/pm/state'
import { exitSuggestion } from '@tiptap/suggestion'
import type { PageNode } from '../tree/types'
import { inCode } from './linkRange'
import { pageHref } from './links'
import { findPages, type PageChoice } from './pageSearch'
import { createMenuState, menuSuggestion, type MenuState } from './suggestionMenu'

export const PAGE_MENU_LISTBOX_ID = 'page-link-menu-listbox'
export function pageOptionId(choice: PageChoice): string {
  return `page-link-option-${choice.id}`
}

/** What the page menu shows. The extension writes it, `PageLinkMenu.vue` reads it. */
export type PageMenuState = MenuState<PageChoice>

export function createPageMenuState(): PageMenuState {
  return createMenuState<PageChoice>()
}

const pluginKey = new PluginKey('pageLinkMenu')

export function closePageMenu(editor: Editor) {
  exitSuggestion(editor.view, pluginKey)
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
  available?: () => boolean
}

/**
 * `[[` at the start of a line or after a space opens a menu of the pages whose title contains what is typed after it
 * (titles have spaces, so the query goes on across them until the caret leaves, a `]` is typed or Escape is pressed).
 * Choosing one replaces `[[` and the query with a link to it. Never in code (a block, or inline code, where a link cannot go)
 * or a read-only editor. The trigger, the query and the range to replace are TipTap's, and the keys work as in the slash
 * menu (`suggestionMenu.ts`).
 */
export function createPageLinkExtension({
  state,
  pages,
  currentPageId,
  untitled,
  available,
}: PageLinkOptions) {
  return Extension.create({
    name: 'pageLinkMenu',
    addProseMirrorPlugins() {
      return [
        menuSuggestion<PageChoice>({
          editor: this.editor,
          pluginKey,
          state,
          char: '[[',
          allowSpaces: true,
          allow: (editorState, range) =>
            available?.() !== false &&
            !inCode(editorState, range.from, range.to) &&
            // "[[plan]]" is finished: the second bracket closes what was begun.
            !editorState.doc.textBetween(range.from, range.to, ' ').includes(']'),
          items: (query) => findPages(pages(), query, currentPageId()),
          command: (editor, range, choice) => insertPageLink(editor, range, choice, untitled()),
          listboxId: PAGE_MENU_LISTBOX_ID,
          optionId: pageOptionId,
        }),
      ]
    },
  })
}
