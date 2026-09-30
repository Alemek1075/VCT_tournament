import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Served by the ASP.NET app under /spa/ in production; in dev the API is proxied to it.
export default defineConfig({
  base: '/spa/',
  plugins: [react()],
  server: {
    port: 5173,
    proxy: { '/api': 'http://localhost:5175' },
  },
  build: { outDir: 'dist' },
})
