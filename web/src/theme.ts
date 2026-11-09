export const themes = ['system', 'light', 'dark'] as const
export type Theme = (typeof themes)[number]

const storageKey = 'inkpact.theme'

export function isTheme(value: unknown): value is Theme {
  return (themes as readonly unknown[]).includes(value)
}

/** The theme the person chose on this device, else System, which follows the system setting (in CSS, with no script). */
export function detectTheme(): Theme {
  try {
    const stored = localStorage.getItem(storageKey)
    if (isTheme(stored)) return stored
  } catch {
    // Storage can be blocked, for example in a private window. The system setting still works.
  }
  return 'system'
}

/**
 * Keeps the choice for next time and sets it on the page: `data-theme` on the root element makes the stylesheet use that
 * colour scheme; System removes it, so the system's setting decides again. The `theme-color` metas of index.html are
 * switched to match.
 */
export function applyTheme(theme: Theme) {
  const root = document.documentElement
  if (theme === 'system') root.removeAttribute('data-theme')
  else root.setAttribute('data-theme', theme)

  // The browser's bar (theme-color) follows too: by the system for System, else the chosen theme's colour everywhere.
  for (const meta of document.querySelectorAll<HTMLMetaElement>('meta[name="theme-color"]')) {
    const scheme = meta.dataset.scheme
    meta.media =
      theme === 'system'
        ? `(prefers-color-scheme: ${scheme})`
        : scheme === theme
          ? 'all'
          : 'not all'
  }

  try {
    if (theme === 'system') localStorage.removeItem(storageKey)
    else localStorage.setItem(storageKey, theme)
  } catch {
    // Not remembered, which is fine.
  }
}
