import { test, expect, mvcLogin, unique } from './fixtures'

test.describe('MVC site', () => {
  test('home page lists matches and the navbar pages open', async ({ page }) => {
    await page.goto('/')
    await expect(page.locator('h1').first()).toBeVisible()
    for (const name of ['Matches', 'Events', 'Teams', 'Players', 'Map', 'Live']) {
      await page.locator('nav').getByRole('link', { name, exact: true }).click()
      await expect(page.locator('h1').first()).toBeVisible()
      await expect(page).not.toHaveTitle(/error/i)
    }
  })

  test('teams can be filtered by region and searched', async ({ page }) => {
    await page.goto('/Teams')
    const all = await page.locator('.team-card').count()
    expect(all).toBeGreaterThan(5)

    await page.locator('a.tag', { hasText: 'Pacific' }).click()
    await expect(page).toHaveURL(/region=/)
    const pacific = page.locator('.team-card')
    await expect(pacific.first()).toBeVisible()
    await expect(pacific.first().locator('.muted')).toContainText('Pacific')

    await page.goto('/Teams')
    await page.getByPlaceholder('Search team or tag').fill('sentinels')
    await page.getByRole('button', { name: 'Find' }).click()
    await expect(page.locator('.team-card .name')).toHaveText(['Sentinels'])
  })

  test('team landing page shows the roster', async ({ page }) => {
    await page.goto('/team/sentinels')
    await expect(page.locator('h1')).toHaveText('Sentinels')
    await expect(page.locator('body')).toContainText(/Roster|Players/i)
  })

  test('anonymous user is sent to sign in before creating a team', async ({ page }) => {
    await page.goto('/Teams/Create')
    await expect(page).toHaveURL(/\/account\/login/i)
    await expect(page.locator('h1')).toHaveText('Sign in')
  })

  test('wrong password shows an error', async ({ page }) => {
    await page.goto('/account/login')
    await page.getByLabel('Email').fill('admin@vct.local')
    await page.getByLabel('Password').fill('definitely-wrong')
    await page.getByRole('button', { name: 'Sign in', exact: true }).click()
    await expect(page.locator('.validation-summary-errors')).not.toBeEmpty()
  })

  test('admin creates, edits and deletes a team', async ({ page }) => {
    await mvcLogin(page, '/Teams/Create')
    await expect(page.locator('h1')).toHaveText('New team')

    // server-side validation: empty form comes back with errors
    await page.getByRole('button', { name: 'Save' }).click()
    await expect(page.locator('[data-valmsg-for="Name"]')).not.toBeEmpty()

    const name = unique('E2E Squad')
    await page.locator('#Name').fill(name)
    await page.locator('#Tag').fill('E2E')
    await page.locator('#RegionId').selectOption({ index: 1 })
    await page.locator('#City').fill('Kyiv')
    await page.locator('#Description').fill('Created by Playwright')
    await page.getByRole('button', { name: 'Save' }).click()

    await expect(page).toHaveURL(/\/team\/e2e-squad/)
    await expect(page.locator('h1')).toHaveText(name)

    await page.getByRole('link', { name: 'Edit team' }).click()
    await page.locator('#City').fill('Lviv')
    await page.getByRole('button', { name: 'Save' }).click()
    await expect(page.locator('body')).toContainText('Lviv')

    await page.getByRole('link', { name: 'Edit team' }).click()
    await page.getByRole('link', { name: 'Delete' }).click()
    await expect(page.locator('h1')).toContainText(`Delete ${name}?`)
    await page.getByRole('button', { name: 'Delete' }).click()
    await expect(page).toHaveURL(/\/Teams$/i)

    await page.getByPlaceholder('Search team or tag').fill(name)
    await page.getByRole('button', { name: 'Find' }).click()
    await expect(page.getByText('No teams match.')).toBeVisible()
  })

  test('player form team picker autocompletes after 3 letters', async ({ page }) => {
    await mvcLogin(page, '/Players/Create')
    await page.locator('#TeamId + .select2 .select2-selection').click()
    const box = page.locator('.select2-search__field')
    await box.fill('se')
    await expect(page.locator('.select2-results__message')).toContainText('Type 1 more character')
    await box.fill('sen')
    const option = page.locator('.select2-results__option', { hasText: 'Sentinels' })
    await expect(option).toBeVisible()
    await option.click()
    await expect(page.locator('#TeamId + .select2 .select2-selection__rendered')).toContainText('Sentinels')
  })

  test('full-text search finds a player and forgives typos', async ({ page }) => {
    await page.goto('/search')
    await page.locator('#q').fill('aspas')
    await page.locator('#q').press('Enter')
    await expect(page.locator('#results')).toContainText('Erick Santos')
    await expect(page.locator('#meta')).not.toBeEmpty()

    await page.locator('#q').fill('fnatik')
    await page.locator('#q').press('Enter')
    await expect(page.locator('#results')).toContainText('FNATIC', { ignoreCase: true })
  })

  test('map page draws team markers', async ({ page }) => {
    await page.goto('/Map')
    await expect(page.locator('.leaflet-marker-icon, .leaflet-interactive').first()).toBeVisible()
  })
})
