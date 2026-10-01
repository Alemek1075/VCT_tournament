import { defineConfig, devices } from '@playwright/test'

// QA2: end-to-end tests for the MVC site (C1, F2) and the React SPA (F5).
// Run against docker compose (http://localhost:8080) or any deployed URL via BASE_URL.
// Locally without the Playwright chromium download you can use Edge: PW_CHANNEL=msedge
const baseURL = process.env.BASE_URL ?? 'http://localhost:8080'

export default defineConfig({
  testDir: './tests',
  timeout: 45_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: [
    ['list'],
    ...(process.env.CI ? [['github'] as const] : []), // failures show up as annotations on the run
    ['monocart-reporter', {
      name: 'VCT Hub E2E',
      outputFile: './report/index.html',
      // client-side JS coverage collected from Chromium (see fixtures.ts)
      coverage: {
        outputDir: './report/coverage',
        entryFilter: (e: { url: string }) => /\/js\/[^/]+\.js|\/spa\/assets\/.+\.js/.test(e.url) && !e.url.includes('.min.js'),
        sourceFilter: (path: string) => !path.includes('node_modules') && !path.includes('vite/'),
        reports: ['v8', 'console-summary', ['json-summary', { file: 'coverage-summary.json' }]],
      },
    }],
  ],
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'], channel: process.env.PW_CHANNEL || undefined } },
  ],
})
