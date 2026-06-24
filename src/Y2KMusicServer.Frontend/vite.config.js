import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
// During `npm run dev`, requests to /api and /hub are proxied to
// the running Kestrel host (default :8765) so the frontend can be
// developed against a live service without CORS. The production
// build is served by Kestrel directly from wwwroot/, so no proxy
// is in play in production.
export default defineConfig({
    plugins: [react()],
    server: {
        port: 5173,
        proxy: {
            '/api': { target: 'http://localhost:8765', changeOrigin: true },
            '/hub': { target: 'http://localhost:8765', changeOrigin: true, ws: true },
            '/stream': { target: 'http://localhost:8765', changeOrigin: true }
        }
    },
    build: {
        outDir: 'dist',
        emptyOutDir: true
    }
});
