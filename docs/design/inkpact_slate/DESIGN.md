---
name: Inkpact Slate
colors:
  surface: '#13121b'
  surface-dim: '#13121b'
  surface-bright: '#393842'
  surface-container-lowest: '#0e0d16'
  surface-container-low: '#1b1b24'
  surface-container: '#1f1f28'
  surface-container-high: '#2a2933'
  surface-container-highest: '#35343e'
  on-surface: '#e4e1ee'
  on-surface-variant: '#c7c4d8'
  inverse-surface: '#e4e1ee'
  inverse-on-surface: '#302f39'
  outline: '#918fa1'
  outline-variant: '#464555'
  surface-tint: '#c3c0ff'
  primary: '#c3c0ff'
  on-primary: '#1d00a5'
  primary-container: '#4f46e5'
  on-primary-container: '#dad7ff'
  inverse-primary: '#4d44e3'
  secondary: '#89ceff'
  on-secondary: '#00344d'
  secondary-container: '#00a2e6'
  on-secondary-container: '#00344e'
  tertiary: '#ffb695'
  on-tertiary: '#571f00'
  tertiary-container: '#a44100'
  on-tertiary-container: '#ffd2be'
  error: '#ffb4ab'
  on-error: '#690005'
  error-container: '#93000a'
  on-error-container: '#ffdad6'
  primary-fixed: '#e2dfff'
  primary-fixed-dim: '#c3c0ff'
  on-primary-fixed: '#0f0069'
  on-primary-fixed-variant: '#3323cc'
  secondary-fixed: '#c9e6ff'
  secondary-fixed-dim: '#89ceff'
  on-secondary-fixed: '#001e2f'
  on-secondary-fixed-variant: '#004c6e'
  tertiary-fixed: '#ffdbcc'
  tertiary-fixed-dim: '#ffb695'
  on-tertiary-fixed: '#351000'
  on-tertiary-fixed-variant: '#7b2f00'
  background: '#13121b'
  on-background: '#e4e1ee'
  surface-variant: '#35343e'
  surface-canvas: '#090A0F'
  surface-panel: '#11131A'
  surface-subtle: '#181B24'
  surface-elevated: '#222634'
  border-subtle: '#232838'
  border-strong: '#374151'
  sync-active: '#10B981'
  sync-offline: '#F59E0B'
  presence-cursor-1: '#F43F5E'
  presence-cursor-2: '#10B981'
  presence-cursor-3: '#38BDF8'
  presence-cursor-4: '#FBBF24'
typography:
  headline-xl:
    fontFamily: Inter
    fontSize: 2rem
    fontWeight: '700'
    lineHeight: 2.5rem
    letterSpacing: -0.025em
  headline-xl-mobile:
    fontFamily: Inter
    fontSize: 1.75rem
    fontWeight: '700'
    lineHeight: 2.25rem
    letterSpacing: -0.02em
  headline-lg:
    fontFamily: Inter
    fontSize: 1.5rem
    fontWeight: '600'
    lineHeight: 2rem
    letterSpacing: -0.02em
  headline-sm:
    fontFamily: Inter
    fontSize: 1.125rem
    fontWeight: '600'
    lineHeight: 1.625rem
    letterSpacing: -0.015em
  body-lg:
    fontFamily: Inter
    fontSize: 1rem
    fontWeight: '400'
    lineHeight: 1.625rem
    letterSpacing: -0.011em
  body-sm:
    fontFamily: Inter
    fontSize: 0.875rem
    fontWeight: '400'
    lineHeight: 1.375rem
  label-md:
    fontFamily: Inter
    fontSize: 0.8125rem
    fontWeight: '500'
    lineHeight: 1.125rem
    letterSpacing: 0.01em
  label-xs:
    fontFamily: Inter
    fontSize: 0.6875rem
    fontWeight: '600'
    lineHeight: 0.875rem
    letterSpacing: 0.04em
  code-inline:
    fontFamily: JetBrains Mono
    fontSize: 0.875rem
    fontWeight: '400'
    lineHeight: 1.25rem
rounded:
  sm: 0.25rem
  DEFAULT: 0.5rem
  md: 0.75rem
  lg: 1rem
  xl: 1.5rem
  full: 9999px
spacing:
  gutter: 1rem
  margin: 1rem
  gutter-desktop: 1.5rem
  margin-desktop: 2rem
  space-xs: 0.25rem
  space-sm: 0.5rem
  space-md: 0.75rem
  space-lg: 1rem
  space-xl: 1.5rem
---

## Brand & Style

This design system embodies a quiet, dependable, and highly focused environment tailored for knowledge synthesis, collaborative editing, and rapid capture. Drawing visual ergonomics from developer platforms like Linear and local-first instruments like Obsidian, the visual tone treats software as transparent infrastructure: deep, low-glare obsidian dark slate surfaces, deliberate typographic contrast, and zero ornamental noise.

The style operates on high-efficiency utilitarian principles:
- **Calm Density**: Space is calibrated to prioritize maximum reading legibility and uncluttered block manipulation, avoiding decorative gradients or aggressive brand color splashes.
- **Engineered Precision**: Surfaces, block handles, slash menus, and status indicators align strictly to a rationalized modular rhythm.
- **Immediate Feedback**: Micro-affordances—such as real-time collaborator cursors, block selection anchors, and local-first sync indicators—are rendered with surgical clarity to instill trust in continuous background synchronization.

## Colors

The palette is rooted in an ultra-deep charcoal slate (`#090A0F`) that minimizes display glare during prolonged writing sessions. Visual hierarchy relies on progressive tonal layering rather than chromatic saturation:

- **Primary (`#4F46E5`)**: Reserved strictly for high-priority user actions, active keyboard navigation focus rings, and primary selection states within command dialogs.
- **Secondary (`#0EA5E9`)**: Utilized for live collaborator identification, link highlights, and contextual mentions.
- **Surfaces**: Four structured elevation tiers (`surface-canvas` through `surface-elevated`) separate note canvas, collapsible sidebars, floating slash popovers, and elevated modals without introducing visual weight.
- **Named Status Tokens**:
  - `sync-active` (`#10B981`): Conveys real-time, non-blocking operational synchronization.
  - `sync-offline` (`#F59E0B`): Flags disconnected, local-cache fallback operations.
  - `presence-cursor-*`: A reserved high-contrast palette assigned dynamically to peer sessions to ensure collaborator pointers remain identifiable against dark editor blocks.

## Typography

Typography centers exclusively on `Inter` for interface labels, prose, and document headers, using `JetBrains Mono` strictly for code segments, keyboard shortcuts, and document hash metadata.

Rules for hierarchical implementation:
- **Document Page Title**: Kept external to the rich-text block container, rendered via `headline-xl` (`headline-xl-mobile` on touch devices) to enforce clean separation between system metadata and editor blocks.
- **Editor Headings**: Headings (H1–H3) inside the markdown/TipTap engine employ negative tracking (`-0.015em` to `-0.025em`) to anchor long-form prose while preserving vertical reading rhythm.
- **Reading Measure**: Long-form body prose (`body-lg`) must maintain an optimal reading column width bounded between 65ch and 75ch. Line heights are elevated (`1.625rem` on `1rem` text) to eliminate line collisions across inline tags, backlink pills, and code tokens.

## Layout & Spacing

The layout is built upon an adaptive dual-canvas architecture:
- **Desktop (>= 1024px)**: A 3-column modular workbench with a collapsible file navigation sidebar (240px to 280px), a centered fluid writing column with a locked max-width of 720px (`45rem`), and an on-demand slide-out metadata/backlinks drawer (320px). Grid gutters expand to `gutter-desktop` (1.5rem).
- **Mobile (< 1024px)**: Full viewport width editor with edge margins pinned to `margin` (1rem). Navigation tree and document links transition into off-canvas sliding sheets triggered by accessible tap targets.

Block-level gutter discipline:
- Within the editor, every block node reserves a persistent left hover margin of `1.75rem` on desktop for block handles and drag anchors, ensuring handles do not disrupt prose reading alignment.
- On touch viewports, block handles are replaced with an inline floating accessory toolbar anchored immediately above the virtual keyboard.

## Elevation & Depth

Visual depth avoids intense drop shadows, favoring low-contrast borders combined with tonal surface lifts:

1. **Base Editor Canvas (`surface-canvas`)**: Flat, non-elevated ground plane. Contains zero drop shadow.
2. **Fixed Panels & Sidebars (`surface-panel`)**: Offset from the canvas via a single 1px hairline border (`border-subtle`).
3. **Floating Menus & Popovers (`surface-subtle` / `surface-elevated`)**: Slash command menus (`/`), quick switchers (`Cmd/Ctrl+K`), and block context menus leverage a 1px border (`border-subtle`) paired with a restrained ambient shadow: `0 8px 24px -4px rgba(0, 0, 0, 0.45)`.
4. **Presence Cursors & Block Drag Previews**: Cursors sit at maximum z-index with a 1px solid white outline shadow (`0 0 0 1px rgba(0,0,0,0.5)`) to maintain visibility over text, code blocks, and embedded images.

## Shapes

The system uses `roundedness: 2` (base 0.5rem / 8px radius) to maintain modern, tactile UI ergonomics without feeling overly bubble-like or toyish:

- **Interactive Controls (Inputs, Buttons, Tree Items)**: Styled with `rounded` (0.5rem) to ensure a comfortable touch target on mobile viewports while preserving desktop density.
- **Panels & Dialogs**: Flyout menus, modal dialogs, and floating command panels use `rounded-lg` (1rem).
- **Inline Elements & Tags**: Metadata chips, collaborator presence markers, and sync pills utilize fully rounded contours (`rounded-full`) to immediately communicate self-contained, atomic data.
- **Editor Blocks & Code Containers**: Code blocks and quotes use `rounded` (0.5rem) with inset 1px hairline borders.

## Components

### Buttons & Interactive Controls
- **Primary Button**: Solid fill with `primary` (`#4F46E5`), white text, 8px corner radius (`rounded`), height of 36px on desktop and 44px on mobile touch targets. Focus state shows a 2px offset ring in `primary`.
- **Secondary / Ghost Button**: Transparent background, `border-subtle` stroke, subtle hover shift to `surface-elevated`. Active tap states provide instantaneous 98% scale feedback.

### Input Fields & Quick Switcher
- **Search & Filter Bars**: Inset surface (`surface-canvas`), 1px `border-subtle`, resting placeholder in subdued slate. On focus: border shifts directly to `primary` without layout jump.
- **Quick Switcher (`Cmd+K`)**: Modal overlay with centered anchor. Search query is rendered in `body-lg` with monospaced keyboard shortcuts (`label-xs`) aligned to the trailing edge.

### Editor Block Handles & Slash Menus
- **Block Handle**: Notion-style 6-dot drag icon rendered in 16x16px frame with low-contrast resting opacity (20%), transitioning to 100% on block hover. Mobile replaces this with an elevated horizontal action bar.
- **Slash Menu**: Floating popover containing list items with categorized headers (`label-xs` in uppercase). Item hover state triggers `surface-elevated` background and `primary` text highlight.

### Chips, Pills & Presence Indicators
- **Sync Status Pill**: Compact capsule (`rounded-full`) displaying a 6px status indicator circle (`sync-active` or `sync-offline`) accompanied by `label-xs` text.
- **Collaborator Presence**: Real-time peer avatars rendered as 24px circles with overlapping negative horizontal margins (`-space-xs`). Live cursors in the editor render a 1.5px vertical line in their designated presence token alongside a floating collaborator name pill in `label-xs`.

### Lists & Checkboxes
- **Checklists**: Custom 18x18px square inputs with `rounded-sm` corners. When checked, surface transforms to `primary` with a crisp white tick mark, and adjacent prose text automatically applies strikethrough with a 40% opacity fade.