# SE素材の置き場（editor-ui-rework-r6.md §5.2）

ノーツ種別ごとのSE(.wav推奨、16bit/44.1kHzまたは48kHz)をここに置き、
`ChartEditorApp` の Inspector で `Se Clips` の各フィールド（Tap / Ex Tap / Slide /
Flick / Tick / Metronome）に割り当てる。

未設定のクリップはフォールバックする（Tap系はTapへ、それも無ければ実行時合成の
クリック音へ）ため、素材が揃っていなくても今までどおり動く。
