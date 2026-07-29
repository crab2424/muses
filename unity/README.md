# muses（Unity 移行後・プレースホルダ）

2026-07-29、Web プロトタイプ（`../web-prototype/`）の実機 fps 計測（Safari で 120fps に届かず、
リズムゲームの判定精度を優先して 120Hz を活かせる環境が必要と判断）を受けて Unity へ移行することを決定。
経緯は `../memory/muses-platform-decisions.md` を参照。

## このフォルダについて
現時点ではプロジェクト未作成の**プレースホルダ**。`.gitignore` だけ先に置いてある
（Unity が生成する `Library/` `Temp/` `Obj/` 等を最初から除外するため）。

## プロジェクトの作り方
1. Unity Hub を開く（GitHub とは連携済みとのことなので、Unity Hub 側でこのローカルリポジトリを
   参照するプロジェクトとして追加する）。
2. 「New project」→ テンプレートは **3D (URP)** を推奨（このステージは半透明の面を多用するため、
   Built-in より URP のほうがブレンディングやポストエフェクトの制御がしやすい）。
3. 保存先をこのフォルダ（`muses/unity/`）に指定してプロジェクトを作成する。
   Unity が `Assets/` `Packages/` `ProjectSettings/` などをここに生成する。
4. ビルドターゲットを **iOS** に切り替える（File > Build Settings）。

## 移植する設計資産
- `../web-prototype/src/derive.ts`: スクリーン空間 (NDC) → 3D パラメータの逆算式。
  実機検証済みの数式なのでそのまま C# に移植してよい。
- `../web-prototype/src/config.ts` の `DEFAULT_CONFIG` と `../memory/settings.json`:
  実機でチューニング済みのパラメータ値。Unity 側の初期値として使う。
- `../memory/muses-stage-ui-design.md`: 設計の正本。エンジンが変わっても数式・設計判断は不変。

## デプロイ
開発中は Xcode 経由で USB 接続実機に直接ビルド（無料 Apple ID でも7日間有効な署名で可）。
複数端末での確認や継続運用が必要になった段階で Apple Developer Program（年99ドル）へ登録し、
TestFlight 配布に切り替える。現時点（開発初期）では TestFlight は不要。
