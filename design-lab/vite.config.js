// design-lab 全体を配信するローカルサーバ用（npm --prefix web-prototype exec -- vite design-lab --port 5181）。
// three は各ページの importmap で CDN から読むので、vite には解決させない。
export default {
  resolve: {
    alias: [
      { find: /^three$/, replacement: 'https://cdn.jsdelivr.net/npm/three@0.180.0/build/three.module.js' },
      { find: /^three\/addons\/(.*)$/, replacement: 'https://cdn.jsdelivr.net/npm/three@0.180.0/examples/jsm/$1' },
    ],
  },
};
