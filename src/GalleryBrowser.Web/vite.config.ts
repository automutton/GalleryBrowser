import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

export default defineConfig({
  plugins: [svelte()],
  base: './',
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (id.endsWith('/src/lib/i18n.ts')) return 'i18n';
          if (id.includes('node_modules/lucide-svelte')) return 'icons';
          if (id.includes('node_modules/@tanstack')) return 'virtualizer';
          if (id.includes('node_modules/svelte')) return 'svelte';
          return undefined;
        }
      }
    }
  }
});
