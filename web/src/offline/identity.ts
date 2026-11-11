const identityKey = 'inkpact.offline.user'

/** This identifies a local partition only. The server still decides who may read or write. */
export function readOfflineIdentity(): string | null {
  try {
    return localStorage.getItem(identityKey)
  } catch (error) {
    console.error('The offline account could not be read', error)
    return null
  }
}

export function writeOfflineIdentity(userId: string | null): boolean {
  try {
    if (userId) localStorage.setItem(identityKey, userId)
    else localStorage.removeItem(identityKey)
    return true
  } catch (error) {
    console.error('The offline account could not be saved', error)
    return false
  }
}

export function isOfflineIdentityEvent(event: StorageEvent): boolean {
  return event.key === identityKey || event.key === null
}
