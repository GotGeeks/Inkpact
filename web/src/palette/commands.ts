import { fold } from '../editor/pageSearch'

export type PaletteCommandId =
  'newPage' | 'toggleFavorite' | 'home' | 'tags' | 'trash' | 'import' | 'shortcuts'

export interface PaletteCommand {
  id: PaletteCommandId
  labelKey: string
  descriptionKey: string
  shortcut?: 'newPage'
}

export interface PaletteCommandContext {
  canEdit: boolean
  hasWorkspace: boolean
  hasLiveCurrentPage: boolean
  favoritesReady: boolean
  currentPageIsFavorite: boolean
}

const definitions: Record<PaletteCommandId, Omit<PaletteCommand, 'id'>> = {
  newPage: {
    labelKey: 'palette.commands.newPage',
    descriptionKey: 'palette.commandDescriptions.newPage',
    shortcut: 'newPage',
  },
  toggleFavorite: {
    labelKey: 'palette.commands.favoriteAdd',
    descriptionKey: 'palette.commandDescriptions.favorite',
  },
  home: {
    labelKey: 'palette.commands.home',
    descriptionKey: 'palette.commandDescriptions.home',
  },
  tags: {
    labelKey: 'palette.commands.tags',
    descriptionKey: 'palette.commandDescriptions.tags',
  },
  trash: {
    labelKey: 'palette.commands.trash',
    descriptionKey: 'palette.commandDescriptions.trash',
  },
  import: {
    labelKey: 'palette.commands.import',
    descriptionKey: 'palette.commandDescriptions.import',
  },
  shortcuts: {
    labelKey: 'palette.commands.shortcuts',
    descriptionKey: 'palette.commandDescriptions.shortcuts',
  },
}

export function availableCommands(context: PaletteCommandContext): PaletteCommand[] {
  const ids: PaletteCommandId[] = []
  if (context.canEdit) ids.push('newPage')
  if (context.favoritesReady && context.hasLiveCurrentPage) ids.push('toggleFavorite')
  if (context.hasWorkspace) ids.push('home', 'tags', 'trash')
  if (context.canEdit) ids.push('import')
  if (context.hasWorkspace) ids.push('shortcuts')

  return ids.map((id) => ({
    id,
    ...definitions[id],
    ...(id === 'toggleFavorite' && context.currentPageIsFavorite
      ? { labelKey: 'palette.commands.favoriteRemove' }
      : {}),
  }))
}

export function findCommands(
  commands: readonly PaletteCommand[],
  query: string,
  translate: (key: string) => string,
): PaletteCommand[] {
  const wanted = fold(query.trim())
  if (!wanted) return [...commands]
  return commands.filter((command) => fold(translate(command.labelKey)).includes(wanted))
}
