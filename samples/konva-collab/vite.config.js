import { defineConfig } from 'vite';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const sampleDir = path.dirname(fileURLToPath(import.meta.url));

export default defineConfig({
    root: sampleDir,
    // Keep the symlinked path of the local `opstream-collab` file: dependency so its
    // bare import of @microsoft/signalr resolves against this sample's node_modules.
    resolve: { preserveSymlinks: true },
    server: {
        port: 5174,
        open: '/index.html',
        proxy: {
            '/collab': {
                target: 'http://localhost:5555',
                changeOrigin: true,
                ws: true,
            },
        },
    },
});
