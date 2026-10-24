import type { Awareness } from 'y-protocols/awareness'

export interface PresenceUser {
  name: string
  color: string
}

export interface PresenceGroup extends PresenceUser {
  tabs: number
}

/** One entry per user name, however many tabs or awareness clients that user has open. */
export function groupPresence(states: Map<number, Record<string, unknown>>): PresenceGroup[] {
  const groups = new Map<string, PresenceGroup>()
  for (const state of states.values()) {
    const user = state.user as PresenceUser | undefined
    if (!user?.name) continue
    const group = groups.get(user.name)
    if (group) group.tabs++
    else groups.set(user.name, { ...user, tabs: 1 })
  }
  return [...groups.values()].sort((a, b) => a.name.localeCompare(b.name))
}

export function renderPresence(list: HTMLElement, awareness: Awareness) {
  const render = () => {
    list.replaceChildren(
      ...groupPresence(awareness.getStates()).map((group) => {
        const item = document.createElement('li')
        item.textContent = group.tabs > 1 ? `${group.name} (${group.tabs} tabs)` : group.name
        // Spike only: CSP is relaxed; the real app uses theme tokens.
        item.style.borderColor = group.color
        return item
      }),
    )
  }
  awareness.on('change', render)
  render()
}
