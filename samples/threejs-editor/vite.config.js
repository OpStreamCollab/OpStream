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
            // Proxy the unmodified three.js editor and its assets from the
            // official CDN, keeping everything same-origin so contentWindow
            // access in main.js still works without a local three.js-dev clone.
            '/three.js-dev': {
                target: 'https://threejs.org',
                changeOrigin: true,
                rewrite: (p) => p.replace(/^\/three\.js-dev/, ''),
            },
            '/collab': {
                target: 'http://localhost:8080',
                changeOrigin: true,
                ws: true,
            },
        },
    },
});
