import { defineConfig } from 'vite';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const sampleDir = path.dirname(fileURLToPath(import.meta.url));

export default defineConfig({
    root: sampleDir,
    resolve: {
        preserveSymlinks: true,
        alias: {
            // Point directly at the source so the build works even if the
            // node_modules symlink is stale (Windows symlinks need admin rights).
            'opstream-collab': path.resolve(sampleDir, '../../packages/opstream-collab-js/src/index.js'),
            // Ensure @microsoft/signalr resolves from this sample's node_modules
            // (not from the package source directory, which has no node_modules).
            '@microsoft/signalr': path.resolve(sampleDir, 'node_modules/@microsoft/signalr'),
        },
    },
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
