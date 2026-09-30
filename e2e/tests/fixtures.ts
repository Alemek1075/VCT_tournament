import { test as base, expect, type Page } from '@playwright/test'
import { addCoverageReport } from 'monocart-reporter'

export const admin = {
  email: process.env.E2E_ADMIN_EMAIL ?? 'admin@vct.local',
  password: process.env.E2E_ADMIN_PASSWORD ?? 'local-admin-123',
}

// Every test records V8 JS coverage of the page; monocart merges it into one report.
export const test = base.extend<{ coverage: void }>({
  coverage: [async ({ page }, use, testInfo) => {
    await page.coverage.startJSCoverage({ resetOnNavigation: false })
    await use()
    await addCoverageReport(await page.coverage.stopJSCoverage(), testInfo)
  }, { auto: true }],
})

export { expect }

/** Sign in on the MVC site with the seeded admin (cookie auth). */
export async function mvcLogin(page: Page, returnUrl = '/') {
  await page.goto(`/account/login?returnUrl=${encodeURIComponent(returnUrl)}`)
  await page.getByLabel('Email').fill(admin.email)
  await page.getByLabel('Password').fill(admin.password)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await expect(page.getByRole('link', { name: 'Sign in' })).toHaveCount(0)
}

export const unique = (p: string) => `${p} ${Date.now().toString(36).slice(-5)}`
