import { defineConfig } from 'vite';

export default defineConfig({
  // host: true で LAN 上の iPad から http://<MacのIP>:5173 でアクセスできる
  server: { host: true, port: 5173 },
  build: { target: 'es2022' },
});
