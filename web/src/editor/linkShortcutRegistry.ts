/**
 * Ctrl+K (Cmd+K on a Mac) means two things (run B.1b): in the editor, with text selected or the caret in a link, it makes or
 * changes a link; everywhere else it opens search. The app listens for it before any element does, so the editor has to say
 * first whether it wants it: it registers a question here, and the app asks before it opens search.
 *
 * Its own module, with no imports: the app shell reads it, and the editor (TipTap and all) is loaded only on a page.
 */
let wants: (() => boolean) | null = null

export function registerLinkShortcut(question: () => boolean): () => void {
  wants = question
  return () => {
    if (wants === question) wants = null
  }
}

/** True when the editor that has the focus takes this Ctrl+K itself: search is not opened. */
export function editorTakesSearchShortcut(): boolean {
  return wants?.() === true
}
