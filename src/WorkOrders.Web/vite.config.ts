import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// Production build goes into the API's wwwroot so one port serves both.
export default defineConfig({
  plugins: [react()],
  build: { outDir: '../WorkOrders.Api/wwwroot', emptyOutDir: true },
  server: { port: 32139, host: '127.0.0.1', proxy: { '/api': 'http://127.0.0.1:32130' } },
})
