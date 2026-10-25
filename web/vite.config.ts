import tailwindcss from '@tailwindcss/vite'
import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vitest/config'

export default defineConfig({
  plugins: [vue(), tailwindcss()],
  define: {
    // Keep the build free of code paths the app does not use, and of devtools hooks.
    __VUE_I18N_LEGACY_API__: false,
    __VUE_I18N_FULL_INSTALL__: false,
    __INTLIFY_PROD_DEVTOOLS__: false,
    __VUE_PROD_DEVTOOLS__: false,
  },
  server: {
    // `pnpm dev` talks to `dotnet run --project server/src/Inkpact.Api`. The dev server has no CSP
    // (Vite injects styles for hot reload), so the Playwright suite tests the built bundle instead.
    proxy: { '/api': 'http://localhost:5036' },
  },
  test: {
    environment: 'jsdom',
    exclude: ['e2e/**', 'node_modules/**'],
  },
})
