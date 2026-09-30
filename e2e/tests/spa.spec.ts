import { test, expect, admin, unique } from './fixtures'
import type { Page } from '@playwright/test'

async function spaLogin(page: Page) {
  await page.goto('/spa/#/login')
  await page.getByLabel('Email').fill(admin.email)
  await page.getByLabel('Password').fill(admin.password)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible()
}

test.describe('React SPA', () => {
  test('matches tabs switch between upcoming and results', async ({ page }) => {
    await page.goto('/spa/')
    await expect(page.locator('h1')).toHaveText('Matches')
    await page.getByRole('button', { name: 'Results' }).click()
    await expect(page.getByRole('button', { name: 'Results' })).toHaveClass(/on/)
    await expect(page.locator('.match').first()).toBeVisible()
  })

  test('teams page pages through the API with nextLink', async ({ page }) => {
    await page.goto('/spa/#/teams')
    const cards = page.locator('.card')
    await expect(cards.first()).toBeVisible()
    const before = await cards.count()
    const more = page.getByRole('button', { name: /Load more/ })
    await expect(more).toBeVisible()
    await more.click()
    await expect.poll(() => cards.count()).toBeGreaterThan(before)
  })

  test('teams search filters the list', async ({ page }) => {
    await page.goto('/spa/#/teams')
    await page.getByPlaceholder('Search name or tag').fill('paper')
    await expect(page.locator('.card')).toHaveCount(1)
    await expect(page.locator('.card')).toContainText('Paper Rex')
  })

  test('players table sorts by ACS', async ({ page }) => {
    await page.goto('/spa/#/players')
    await page.getByRole('columnheader', { name: 'ACS' }).click()
    await expect(page.getByRole('columnheader', { name: 'ACS' })).toHaveClass(/on/)
    const acs = page.locator('tbody tr td:nth-child(5)')
    await expect(acs.first()).toBeVisible()
    const values = (await acs.allTextContents()).slice(0, 10).map(Number)
    expect(values).toEqual([...values].sort((a, b) => b - a))
  })

  test('editing needs a sign in', async ({ page }) => {
    await page.goto('/spa/#/teams/new')
    await expect(page.getByText('You need to')).toBeVisible()
  })

  test('signed in user creates, edits and deletes a team through the API', async ({ page }) => {
    page.on('dialog', d => d.accept())
    await spaLogin(page)

    await page.goto('/spa/#/teams/new')
    // API validation errors (problem+json) are shown next to the fields
    await page.getByRole('button', { name: 'Save' }).click()
    await expect(page.locator('.err').first()).toBeVisible()

    const name = unique('SPA Squad')
    await page.getByRole('textbox', { name: /^Name/ }).fill(name)
    await page.getByRole('textbox', { name: /^Tag/ }).fill('SPA')
    await page.getByRole('textbox', { name: /^City/ }).fill('Odesa')
    await page.getByRole('button', { name: 'Save' }).click()
    await expect(page.locator('h1')).toHaveText(name)

    await page.getByRole('link', { name: 'Edit' }).click()
    await page.getByRole('textbox', { name: /^City/ }).fill('Kharkiv')
    await page.getByRole('button', { name: 'Save' }).click()
    await expect(page.locator('.team-head')).toContainText('Kharkiv')

    await page.getByRole('button', { name: 'Delete' }).click()
    await expect(page).toHaveURL(/#\/teams$/)
    await page.getByPlaceholder('Search name or tag').fill(name)
    await expect(page.locator('.card')).toHaveCount(0)
  })
})
