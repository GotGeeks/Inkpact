import { describe, expect, it } from 'vitest'
import { availableCommands, findCommands } from './commands'

const labels: Record<string, string> = {
  'palette.commands.newPage': 'Create new page',
  'palette.commands.favoriteAdd': 'Add to favorites',
  'palette.commands.favoriteRemove': 'Remove from favorites',
  'palette.commands.home': 'Go to start',
  'palette.commands.tags': 'Open tags',
  'palette.commands.trash': 'Open trash',
  'palette.commands.import': 'Import from Joplin',
  'palette.commands.shortcuts': 'Keyboard shortcuts',
}
const translate = (key: string) => labels[key] ?? key

describe('availableCommands', () => {
  it('only offers commands allowed by the current workspace and page', () => {
    expect(
      availableCommands({
        canEdit: false,
        hasWorkspace: true,
        hasLiveCurrentPage: false,
        favoritesReady: false,
        currentPageIsFavorite: false,
      }).map((command) => command.id),
    ).toEqual(['home', 'tags', 'trash', 'shortcuts'])
  })

  it('uses the right favorite command and exposes the new-page shortcut', () => {
    const commands = availableCommands({
      canEdit: true,
      hasWorkspace: true,
      hasLiveCurrentPage: true,
      favoritesReady: true,
      currentPageIsFavorite: true,
    })
    expect(commands.find((command) => command.id === 'newPage')?.shortcut).toBe('newPage')
    expect(commands.find((command) => command.id === 'toggleFavorite')?.labelKey).toBe(
      'palette.commands.favoriteRemove',
    )
  })
})

describe('findCommands', () => {
  const commands = availableCommands({
    canEdit: true,
    hasWorkspace: true,
    hasLiveCurrentPage: false,
    favoritesReady: false,
    currentPageIsFavorite: false,
  })

  it('filters by translated labels, ignoring case and accents', () => {
    const translated = commands.map((command) =>
      command.id === 'newPage' ? { ...command, labelKey: 'palette.commands.cafe' } : command,
    )
    expect(
      findCommands(translated, 'CAFE', (key) =>
        key === 'palette.commands.cafe' ? 'Café notes' : translate(key),
      ).map((command) => command.id),
    ).toEqual(['newPage'])
  })

  it('shows all commands with an empty query and none for an unmatched query', () => {
    expect(findCommands(commands, '', translate)).toHaveLength(commands.length)
    expect(findCommands(commands, 'unavailable', translate)).toEqual([])
  })
})
