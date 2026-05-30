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
            // Same-origin proxy to the OpStream SignalR hub (lets WS upgrades through).
            '/collab': {
                target: 'http://localhost:50109',
                changeOrigin: true,
                ws: true,
            },
        },
    },
});
