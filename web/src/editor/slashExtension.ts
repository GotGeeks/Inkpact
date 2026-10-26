import { Extension, type Editor } from '@tiptap/core'
import { PluginKey } from '@tiptap/pm/state'
import { exitSuggestion, Suggestion } from '@tiptap/suggestion'
import { reactive } from 'vue'
import { filterCommands, type BlockCommand, type Translate } from './blockCommands'

export const SLASH_LISTBOX_ID = 'slash-menu-listbox'
export function slashOptionId(command: BlockCommand): string {
  return `slash-option-${command.id}`
}

/** Where the caret is, in viewport coordinates; a plain object, as a `DOMRect` should not be made reactive. */
export interface AnchorRect {
  left: number
  top: number
  right: number
  bottom: number
}

/** What the slash menu shows. The extension writes it, `SlashMenu.vue` reads it. */
export interface SlashState {
  open: boolean
  query: string
  items: BlockCommand[]
  selected: number
  anchor: AnchorRect | null
  /** Makes the block and removes what was typed; set while the menu is open. */
  choose: (command: BlockCommand) => void
  /** Points at an item, as the mouse does. */
  point: (index: number) => void
}

export function createSlashState(): SlashState {
  const state: SlashState = reactive({
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

const pluginKey = new PluginKey('slashMenu')

/** The editor's element is the combobox of the pattern: it keeps the focus, and names the option that is chosen. */
function describeMenu(dom: HTMLElement, state: SlashState) {
  if (!state.open) {
    dom.removeAttribute('aria-controls')
    dom.removeAttribute('aria-haspopup')
    dom.removeAttribute('aria-activedescendant')
    return
  }
  dom.setAttribute('aria-controls', SLASH_LISTBOX_ID)
  dom.setAttribute('aria-haspopup', 'listbox')
  const chosen = state.items[state.selected]
  if (chosen) dom.setAttribute('aria-activedescendant', slashOptionId(chosen))
  else dom.removeAttribute('aria-activedescendant')
}

/**
 * `/` at the start of a line or after a space opens a menu of blocks. The trigger, the query and the range to replace
 * are TipTap's; this decides what is listed and how the keys work. Never in a code block or a read-only editor.
 */
export function createSlashExtension(state: SlashState, t: Translate) {
  return Extension.create({
    name: 'slashMenu',
    addProseMirrorPlugins() {
      return [
        Suggestion<BlockCommand, BlockCommand>({
          editor: this.editor,
          pluginKey,
          char: '/',
          allowedPrefixes: [' '],
          allowSpaces: false,
          allow: ({ editor, state: editorState, range }) =>
            editor.isEditable && !editorState.doc.resolve(range.from).parent.type.spec.code,
          items: ({ query }) => filterCommands(query, t),
          command: ({ editor, range, props }) => props.run(editor, range),
          render: () => {
            const show = (
              editor: Editor,
              query: string,
              items: BlockCommand[],
              clientRect: (() => DOMRect | null) | null | undefined,
              choose: (command: BlockCommand) => void,
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
