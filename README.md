# muses（音ゲー、名称仮）

音ゲームのプロジェクト。設計の背景は `memory/` 以下を参照。

## リポジトリ構成（2026-07-29 再編）

```
muses/
  web-prototype/   Three.js + TypeScript + Vite によるステージUIプロトタイプ。
                   ステージ幾何（スクリーン空間→3D逆算）の検証は完了・凍結。
                   詳細は web-prototype/README.md。
  unity/           開発本体（移行中）。詳細は unity/README.md。
  memory/          設計メモ・草案・パラメータ (settings.json)。エンジンに依らず有効。
```

## 経緯

1. Three.js プロトタイプでステージUI（地上/空中2層、判定線・判定帯の幾何）を設計・実装・実機検証。
2. iPad Safari での実機計測で 60fps は安定していたが 120fps には届かず、リズムゲームでは
   ProMotion (120Hz) を活かせる環境の判定精度上のメリットが大きいと判断し、Unity へ移行することを決定。
3. 現在は Unity プロジェクトの立ち上げ段階（`unity/README.md` 参照）。

Web プロトタイプはステージ幾何の検証という役割を終えたため凍結（`web-prototype/` 配下）。
実機で確認済みの数式・パラメータは Unity 側の実装で参照・移植する。
