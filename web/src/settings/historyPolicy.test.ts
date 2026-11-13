import { describe, expect, it } from 'vitest'
import { policyProblem, removesVersions, type HistoryPolicy } from './historyPolicy'

const defaults: HistoryPolicy = {
  enabled: true,
  granularHours: 24,
  dailyDays: 30,
  expiryDays: 365,
  capMiB: null,
}

describe('policyProblem', () => {
  it('takes the defaults and the edges the server takes', () => {
    expect(policyProblem(defaults)).toBeNull()
    expect(policyProblem({ ...defaults, granularHours: 1, dailyDays: 1, expiryDays: 1 })).toBeNull()
    expect(
      policyProblem({ ...defaults, granularHours: 87600, dailyDays: 3650, expiryDays: 3650 }),
    ).toBeNull()
    expect(policyProblem({ ...defaults, granularHours: 48, dailyDays: 2 })).toBeNull()
    expect(policyProblem({ ...defaults, capMiB: 1 })).toBeNull()
  })

  it.each([
    [{ granularHours: 0 }, 'granular'],
    [{ granularHours: 87601 }, 'granular'],
    [{ granularHours: Number.NaN }, 'granular'],
    [{ granularHours: 1.5 }, 'granular'],
    [{ dailyDays: 0 }, 'daily'],
    [{ dailyDays: 3651 }, 'daily'],
    [{ granularHours: 49, dailyDays: 2 }, 'dailyBeforeGranular'],
    [{ expiryDays: 3651 }, 'expiry'],
    [{ expiryDays: Number.NaN }, 'expiry'],
    [{ expiryDays: 29 }, 'expiryBeforeDaily'],
    [{ capMiB: 0 }, 'cap'],
    [{ capMiB: Number.NaN }, 'cap'],
  ] as const)('refuses %o (%s)', (change, problem) => {
    expect(policyProblem({ ...defaults, ...change })).toBe(problem)
  })
})

describe('removesVersions', () => {
  it('is true for a shorter window, a new limit and a lower one', () => {
    expect(removesVersions(defaults, { ...defaults, granularHours: 12 })).toBe(true)
    expect(removesVersions(defaults, { ...defaults, dailyDays: 7 })).toBe(true)
    expect(removesVersions(defaults, { ...defaults, expiryDays: 90 })).toBe(true)
    expect(removesVersions(defaults, { ...defaults, capMiB: 500 })).toBe(true)
    expect(removesVersions({ ...defaults, capMiB: 500 }, { ...defaults, capMiB: 100 })).toBe(true)
  })

  it('is false for longer windows, a higher limit, no limit, and history turned off', () => {
    expect(
      removesVersions(defaults, { ...defaults, granularHours: 48, dailyDays: 60, expiryDays: 730 }),
    ).toBe(false)
    expect(removesVersions({ ...defaults, capMiB: 100 }, { ...defaults, capMiB: 500 })).toBe(false)
    expect(removesVersions({ ...defaults, capMiB: 100 }, defaults)).toBe(false)
    expect(removesVersions(defaults, { ...defaults, enabled: false })).toBe(false)
  })
})
