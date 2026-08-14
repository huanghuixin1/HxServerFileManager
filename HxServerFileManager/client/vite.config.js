import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// 构建产物直接输出到后端的 wwwroot，由 Kestrel 静态托管
export default defineConfig({
  plugins: [vue()],
  base: './',
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    // 开发模式下把 /api 与 SSE 日志流代理到 Kestrel 后端
    proxy: {
      '/api': {
        target: 'http://localhost:5101',
        changeOrigin: true,
      },
    },
  },
})
