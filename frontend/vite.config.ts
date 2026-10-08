import { defineConfig } from 'vitest/config';
import tailwindcss from '@tailwindcss/vite';
export default defineConfig({
  plugins: [tailwindcss()],
  base: './',
  cacheDir: '../.cache/vite',
  build: {
    outDir: '../.cache/frontend', emptyOutDir: true,
    // ponytail: Rollup tree shaking stalls this bundle; restore it after a bounded build proves the stall fixed.
    rollupOptions: { treeshake: false },
  },
  test: { include: ['tests/**/*.test.ts'], cache: false },
});
