/**
 * A workspace's history policy (run B.5b) as the server has it (`/api/workspaces/{id}/history-policy`): whether new
 * versions are kept, and how long. Every version is kept for `granularHours`; then the newest of each day until
 * `dailyDays`; then the newest of each week until `expiryDays`, after which versions go. `capMiB` limits what all of it
 * may take, the oldest unprotected versions going first; null is no limit.
 */
export interface HistoryPolicy {
  enabled: boolean
  granularHours: number
  dailyDays: number
  expiryDays: number
  capMiB: number | null
}

/** What the workspace's versions take, and how much of that is protected (the page before a restore, for a day). */
export interface HistoryUsage {
  usedBytes: number
  protectedBytes: number
}

/** The server's longest window, in days. */
export const MAXIMUM_DAYS = 3650

/** Why a policy cannot be saved, as a key under `settings.history.invalid`; null when it can. The server's own rules. */
export type PolicyProblem =
  'granular' | 'daily' | 'dailyBeforeGranular' | 'expiry' | 'expiryBeforeDaily' | 'cap'

export function policyProblem(policy: HistoryPolicy): PolicyProblem | null {
  const whole = (n: number) => Number.isSafeInteger(n) && n > 0
  if (!whole(policy.granularHours) || policy.granularHours > MAXIMUM_DAYS * 24) return 'granular'
  if (!whole(policy.dailyDays) || policy.dailyDays > MAXIMUM_DAYS) return 'daily'
  if (policy.dailyDays * 24 < policy.granularHours) return 'dailyBeforeGranular'
  if (!whole(policy.expiryDays) || policy.expiryDays > MAXIMUM_DAYS) return 'expiry'
  if (policy.expiryDays < policy.dailyDays) return 'expiryBeforeDaily'
  if (policy.capMiB !== null && !whole(policy.capMiB)) return 'cap'
  return null
}

/**
 * Whether saving `next` over `current` can remove versions: a shorter window, a cap where there was none, or a lower one.
 * Raising them later does not bring removed versions back, so the person is asked first. Turning history off removes
 * nothing (versions already kept stay, and are thinned as before).
 */
export function removesVersions(current: HistoryPolicy, next: HistoryPolicy): boolean {
  return (
    next.granularHours < current.granularHours ||
    next.dailyDays < current.dailyDays ||
    next.expiryDays < current.expiryDays ||
    (next.capMiB !== null && (current.capMiB === null || next.capMiB < current.capMiB))
  )
}
