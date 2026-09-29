// ローカル確認用（npm --prefix web-prototype exec -- vite design-lab/note-tap）。
// three は index.html の importmap で CDN から読むので、vite には解決させない。
export default {
  resolve: {
    alias: [
      { find: /^three$/, replacement: 'https://cdn.jsdelivr.net/npm/three@0.180.0/build/three.module.js' },
      { find: /^three\/addons\/(.*)$/, replacement: 'https://cdn.jsdelivr.net/npm/three@0.180.0/examples/jsm/$1' },
    ],
  },
};
