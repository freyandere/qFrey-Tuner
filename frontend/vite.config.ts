import { defineConfig } from 'vitest/config';
import tailwindcss from '@tailwindcss/vite';
export default defineConfig({
  plugins: [tailwindcss()],
  base: './',
  cacheDir: '../.cache/vite',
  build: { outDir: '../.cache/frontend', emptyOutDir: true },
  test: { include: ['tests/**/*.test.ts'], cache: false },
});
