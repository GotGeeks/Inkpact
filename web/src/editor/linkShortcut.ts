import { Extension } from '@tiptap/core'
import { Plugin } from '@tiptap/pm/state'
import { isSearchShortcut } from '../search/shortcut'

export interface LinkShortcutOptions {
  /** Opens the picker for the selection or the link the caret is in; false when there is nothing to link. */
  open?: () => boolean
}

/** The editor's side: the key is taken here, in the text, so that nothing else (the browser's own Ctrl+K included) acts on it. */
export const LinkShortcut = Extension.create<LinkShortcutOptions>({
  name: 'linkShortcut',

  addOptions() {
    return { open: undefined }
  },

  addProseMirrorPlugins() {
    const open = this.options.open
    return [
      new Plugin({
        props: {
          handleKeyDown: (_view, event) => {
            if (!open || !isSearchShortcut(event)) return false
            if (!open()) return false
            event.preventDefault()
            return true
          },
        },
      }),
    ]
  },
})
