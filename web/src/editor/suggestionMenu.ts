import type { Editor, Range } from '@tiptap/core'
import type { EditorState, PluginKey } from '@tiptap/pm/state'
import { exitSuggestion, Suggestion } from '@tiptap/suggestion'
import { reactive } from 'vue'

/** Where the caret is, in viewport coordinates; a plain object, as a `DOMRect` should not be made reactive. */
export interface AnchorRect {
  left: number
  top: number
  right: number
  bottom: number
}

/** What a menu at the caret shows. The extension writes it, the menu's component reads it. */
export interface MenuState<T> {
  open: boolean
  query: string
  items: T[]
  selected: number
  anchor: AnchorRect | null
  /** Carries out the item and removes what was typed; set while the menu is open. */
  choose: (item: T) => void
  /** Points at an item, as the mouse does. */
  point: (index: number) => void
}

export function createMenuState<T>(): MenuState<T> {
  const state = reactive({
    open: false,
    query: '',
    items: [],
    selected: 0,
    anchor: null,
    choose: () => {},
    point: (index: number) => {
      state.selected = index
    },
  }) as MenuState<T>
  return state
}

export interface MenuSuggestionOptions<T> {
  editor: Editor
  pluginKey: PluginKey
  state: MenuState<T>
  /** What opens the menu, at the start of a line or after a space. */
  char: string
  /** The query goes on across spaces (titles have them) until the caret leaves or the menu is closed. */
  allowSpaces: boolean
  /** Whether the menu may open for `range` (the trigger and the query); it never does in a read-only editor. */
  allow: (state: EditorState, range: Range) => boolean
  items: (query: string) => T[]
  /** Carries out the chosen item in place of `range`. */
  command: (editor: Editor, range: Range, item: T) => void
  /** The id of the listbox and of each option, which the editor's element names as the combobox of the pattern. */
  listboxId: string
  optionId: (item: T) => string
}

/** The editor's element is the combobox of the pattern: it keeps the focus, and names the option that is chosen. */
function describeMenu<T>(dom: HTMLElement, options: MenuSuggestionOptions<T>) {
  const { state } = options
  if (!state.open) {
    dom.removeAttribute('aria-controls')
    dom.removeAttribute('aria-haspopup')
    dom.removeAttribute('aria-activedescendant')
    return
  }
  dom.setAttribute('aria-controls', options.listboxId)
  dom.setAttribute('aria-haspopup', 'listbox')
  const chosen = state.items[state.selected]
  if (chosen) dom.setAttribute('aria-activedescendant', options.optionId(chosen))
  else dom.removeAttribute('aria-activedescendant')
}

/**
 * A menu at the caret, opened by typing `char`: the slash menu and the menu of pages. The trigger, the query and the range to
 * replace are TipTap's; this keeps `state` up to date and makes the keys work. The arrow keys move through the list, Enter
 * takes the chosen item (with nothing listed it is a line break, as anywhere in the text: the menu stays out of the way), and
 * Tab closes the menu and does what it does in the page.
 */
export function menuSuggestion<T>(options: MenuSuggestionOptions<T>) {
  const { state, pluginKey } = options

  const show = (
    editor: Editor,
    query: string,
    items: T[],
    clientRect: (() => DOMRect | null) | null | undefined,
    choose: (item: T) => void,
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
      state.anchor = { left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom }
    }
    describeMenu(editor.view.dom, options)
  }

  return Suggestion<T, T>({
    editor: options.editor,
    pluginKey,
    char: options.char,
    allowedPrefixes: [' '],
    allowSpaces: options.allowSpaces,
    allow: ({ editor, state: editorState, range }) =>
      editor.isEditable && options.allow(editorState, range),
    items: ({ query }) => options.items(query),
    command: ({ editor, range, props }) => options.command(editor, range, props),
    render: () => ({
      onStart: (props) =>
        show(props.editor, props.query, props.items, props.clientRect, props.command),
      onUpdate: (props) =>
        show(props.editor, props.query, props.items, props.clientRect, props.command),
      onExit: (props) => {
        state.open = false
        state.query = ''
        state.items = []
        state.selected = 0
        describeMenu(props.editor.view.dom, options)
      },
      onKeyDown: ({ event, view }) => {
        const count = state.items.length
        switch (event.key) {
          case 'ArrowDown':
            if (!count) return false
            state.selected = (state.selected + 1) % count
            describeMenu(view.dom, options)
            return true
          case 'ArrowUp':
            if (!count) return false
            state.selected = (state.selected - 1 + count) % count
            describeMenu(view.dom, options)
            return true
          case 'Enter': {
            const chosen = state.items[state.selected]
            if (chosen === undefined) return false
            state.choose(chosen)
            return true
          }
          case 'Tab':
            exitSuggestion(view, pluginKey)
            return false
          default:
            return false
        }
      },
    }),
  })
}
