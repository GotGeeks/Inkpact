import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

// Read from disk: the build's CSS plugin would hand a `?raw` import back as compiled output.
const css = readFileSync(resolve(import.meta.dirname, 'style.css'), 'utf8')

// The sources of every component, read as text: a rule below fails for a class that must not come back.
const sources = import.meta.glob(['./**/*.vue', './**/editorClasses.ts'], {
  query: '?raw',
  import: 'default',
  eager: true,
}) as Record<string, string>

const themes = ['light', 'dark'] as const
type Theme = (typeof themes)[number]

/** The colours that are one value in both themes: the cursors of other people, drawn on the text of either. */
const singleValue = /^presence-/

/** A theme colour by its name in style.css ("on-surface") for one theme: `light-dark(light, dark)`, or one value. */
function token(name: string, theme: Theme): string {
  const pair = new RegExp(
    `--color-${name}: *light-dark\\((#[0-9a-fA-F]{6}), *(#[0-9a-fA-F]{6})\\);`,
  ).exec(css)
  if (pair) return theme === 'light' ? pair[1]! : pair[2]!
  const single = new RegExp(`--color-${name}: *(#[0-9a-fA-F]{6});`).exec(css)
  if (single && singleValue.test(name)) return single[1]!
  throw new Error(`no colour token ${name} with a light and a dark value`)
}

function luminance(hex: string): number {
  const [r, g, b] = [1, 3, 5].map((i) => {
    const v = parseInt(hex.slice(i, i + 2), 16) / 255
    return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4
  }) as [number, number, number]
  return 0.2126 * r + 0.7152 * g + 0.0722 * b
}

/** WCAG's contrast ratio of two colours in one theme. */
function ratio(a: string, b: string, theme: Theme): number {
  const [x, y] = [luminance(token(a, theme)), luminance(token(b, theme))]
  return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05)
}

// The surfaces things are drawn on: the page, the sidebar, menus and dialogs, rows on hover, raised chips.
const surfaces = [
  'surface-canvas',
  'surface-panel',
  'surface-subtle',
  'surface-elevated',
  'surface-container',
  'surface-container-high',
]

describe.each(themes)('contrast of the design tokens, %s theme (WCAG 2.2 AA)', (theme) => {
  it('reads the tokens it checks', () => {
    expect(token('surface-canvas', theme)).toBe(theme === 'light' ? '#ffffff' : '#090a0f')
  })

  describe.each([
    // Text: 4.5:1.
    ['on-surface', 4.5],
    ['on-surface-variant', 4.5],
    ['primary', 4.5],
    ['secondary', 4.5],
    ['error', 4.5],
    // A done to-do's text, which is crossed out rather than faded.
    ['outline', 4.5],
    ['sync-active', 4.5],
    ['sync-offline', 4.5],
    // The focus ring: 3:1 against what is around it (1.4.11).
    ['primary', 3],
  ] as const)('%s', (foreground, minimum) => {
    it.each(surfaces)(`is at least ${minimum}:1 on %s`, (surface) => {
      expect(ratio(foreground, surface, theme)).toBeGreaterThanOrEqual(minimum)
    })
  })

  it.each([
    ['on-primary-container', 'primary-container'],
    ['on-error-container', 'error-container'],
    ['on-surface', 'surface-container-highest'],
  ])('text %s on %s is at least 4.5:1', (foreground, background) => {
    expect(ratio(foreground, background, theme)).toBeGreaterThanOrEqual(4.5)
  })

  it('the edge of a text field is at least 3:1 on the page', () => {
    expect(ratio('outline', 'surface-canvas', theme)).toBeGreaterThanOrEqual(3)
  })
})

describe('the theme tokens', () => {
  it('every colour token has a light and a dark value, except the listed ones', () => {
    const names = [...css.matchAll(/--color-([a-z0-9-]+):/g)].map((m) => m[1]!)
    expect(names.length).toBeGreaterThan(40)
    const single = names.filter(
      (name) => !/light-dark\(/.test(css.match(new RegExp(`--color-${name}:[^;]*;`))![0]),
    )
    expect(single.filter((name) => !singleValue.test(name))).toEqual([])
  })

  it.each(themes)(
    'the browser bar colour of index.html is surface-panel of the %s theme',
    (theme) => {
      // index.html has no stylesheet when the browser reads it, so the value is written there and checked here.
      const html = readFileSync(resolve(import.meta.dirname, '../index.html'), 'utf8')
      const meta = new RegExp(
        `<meta\\s+name="theme-color"\\s+data-scheme="${theme}"\\s+content="(#[0-9a-f]{6})"`,
      )
      expect(meta.exec(html)?.[1]).toBe(token('surface-panel', theme))
    },
  )
})

describe('classes that failed the contrast and must not come back', () => {
  const all = Object.entries(sources)

  it('has found the components', () => {
    expect(all.length).toBeGreaterThan(20)
  })

  it('no focus ring is drawn in primary-container (3:1 on the canvas, less on raised surfaces)', () => {
    const found = all.filter(([, text]) =>
      /focus(-visible|-within)?:(outline|ring|border)-primary-container/.test(text),
    )
    expect(found.map(([file]) => file)).toEqual([])
  })

  it('no focus ring is switched off by outline-none (Tailwind 4 sets the outline style to none, and outline-2 keeps it)', () => {
    // Every quoted string on one line: a class list is one.
    const strings = /(["'`])(?:(?!\1)[^\n])*\1/g
    const found = all.flatMap(([file, text]) =>
      (text.match(strings) ?? [])
        .filter(
          (s) =>
            /(?<![:\w-])outline-(none|hidden)/.test(s) &&
            /focus(-visible)?:(-?outline-2|outline-primary)/.test(s),
        )
        .map((s) => `${file}: ${s.slice(0, 80)}`),
    )
    expect(found).toEqual([])
  })

  it('a text field is not drawn with the border-subtle edge (1.35:1)', () => {
    const field = sources['./components/TextField.vue']!
    expect(field).not.toContain('border-border-subtle')
    expect(field).toContain('border-outline')
  })

  it('a done to-do is not faded with opacity (about 3:1)', () => {
    expect(sources['./editor/editorClasses.ts']!).not.toMatch(/data-checked=true\]>div\]:opacity/)
  })
})
