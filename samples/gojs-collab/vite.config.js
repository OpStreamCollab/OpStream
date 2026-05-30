import { defineConfig } from 'vite';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const sampleDir = path.dirname(fileURLToPath(import.meta.url));

export default defineConfig({
    root: sampleDir,
    server: {
        port: 5173,
        open: '/index.html',
        proxy: {
            '/collab': {
                target: 'http://localhost:50109',
                changeOrigin: true,
                ws: true,
            },
        },
    },
});
