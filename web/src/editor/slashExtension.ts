import { Extension } from '@tiptap/core'
import { PluginKey } from '@tiptap/pm/state'
import { filterCommands, type BlockCommand, type Translate } from './blockCommands'
import { createMenuState, menuSuggestion, type MenuState } from './suggestionMenu'

export const SLASH_LISTBOX_ID = 'slash-menu-listbox'
export function slashOptionId(command: BlockCommand): string {
  return `slash-option-${command.id}`
}

/** What the slash menu shows. The extension writes it, `SlashMenu.vue` reads it. */
export type SlashState = MenuState<BlockCommand>

export function createSlashState(): SlashState {
  return createMenuState<BlockCommand>()
}

const pluginKey = new PluginKey('slashMenu')

/**
 * `/` at the start of a line or after a space opens a menu of blocks. The trigger, the query and the range to replace
 * are TipTap's; this decides what is listed (`suggestionMenu.ts` makes the keys work). Never in a code block or a
 * read-only editor.
 */
export function createSlashExtension(state: SlashState, t: Translate) {
  return Extension.create({
    name: 'slashMenu',
    addProseMirrorPlugins() {
      return [
        menuSuggestion<BlockCommand>({
          editor: this.editor,
          pluginKey,
          state,
          char: '/',
          allowSpaces: false,
          allow: (editorState, range) => !editorState.doc.resolve(range.from).parent.type.spec.code,
          items: (query) => filterCommands(query, t),
          command: (editor, range, command) => command.run(editor, range),
          listboxId: SLASH_LISTBOX_ID,
          optionId: slashOptionId,
        }),
      ]
    },
  })
}
