# muses ノーツの手触り r3（2026-10-08）

gameplay-feel-r2 の後、ユーザーから出たノーツ関連の指摘5件。仕様は note-spec rev.10 注記。

| # | 指摘 | 結論 |
|---|---|---|
| 1 | Riser/Diver の判定を緩めたい | PERFECT+ 窓 ±50ms 拡張（下位ティアも外側へ）、横余白 0.5→1.0 セル |
| 2 | Tap + Riser の配置で Tap の判定枠を上下両方に | Tap を両層で取れる（`dualLayer`）、縦連も両層が相手 |
| 3 | 空中ノーツが近くで急に厚みを増す | **シェーダの不具合**。判定線より手前で層の奥行き再マップが外れていた |
| 4 | ノーツの縦幅と判定線の基準 | 質問への回答のみ（Tap 系は中央線、Slide は始点の辺） |
| 5 | Slide 始点を Ex Tap と同じに | 枠内更新駆動・窓内全 PERFECT+、押しっぱなしでは拾わない |

## 1. Riser/Diver
### 1.1 現行の成立条件（ユーザーの確認事項への回答）
- **スカラー**: 指定方向の**垂直成分**（画面 v のみ。横成分は無視）。直近 `flickWindowMs`(120ms) の履歴で、
  一番「後ろ」の点から現在までの指定方向の変位が `riserDistanceV`（判定線間の半分×`riserReachFrac`、画面高の約18%）以上。
  斜めに擦っても垂直成分が閾値を超えれば成立する（ユーザーの希望「2」と同じ。変更なし）。
- **判定面**: 移動の**途中で一度でも**判定域（元の層の layerF ±0.5、横はセル範囲 ± `riserMarginCells`）を通れば良い。
  始点が入っている必要も、全体が入っている必要もない。
### 1.2 変更（ユーザー判断）
- `riserWindowExtendMs = 50`: 実効ずれ `eff = sign(d)·max(0, |d| − ext)`、`d = diff ≤ 0 ? diff : max(0, diff − riserLateShiftMs)`。
  |eff| で通常のティア表を引く。窓は [t − 150ms, t + 200ms]。
- `riserMarginCells` 0.5 → 1.0（シーンには未シリアライズなのでフィールド初期化子が効く）。
- Judge.Update の先読みを Riser だけ ext ぶん広げた（他種別は従来どおり rawWin から）。

## 2. Riser と重なる Tap（`NoteRuntime.dualLayer`）
- 対象: 同時刻・同一層・セル交差の Riser/Diver を持つ Tap/Ex Tap（ロード時 precompute）。
- 地上・空中どちらのパネルの接触でも、同じセルなら候補になる。同時刻まとめ解決でも層を問わない。
- 縦連（§6.2）: 層の一致判定を「層集合の交差」に変更。dualLayer の Tap は両層 → 直前の空中 Tap の遅い側も中点で切れる。
- Riser 成立時の合成 EnterEvent（`EnterEvent.fromHandoff`）では両層扱いしない（擦るだけで Tap が取れないように）。
- 既知の限界: Riser 自身の判定域は元の層のまま。空中側から触れて上に擦ると、判定域を通らず Riser が成立しないことがある。

## 3. 空中ノーツの厚み（シェーダ不具合）
- `MusesRemapDepth` が d0 < zJudge（判定線より手前）を恒等にしていた。空中は判定線を越えた瞬間に再マップと
  `_SkyThicknessMul` 補正が外れ、画面上の厚みが地上比 1.02 → 1.44（判定時刻）→ 4.5 倍（約50ms後）に膨らむ（数値で確認）。
  Tap の手前の面は中心より先に判定線を越えるため、判定直前から膨らみ始める。
- 修正: 手前側も同じ式で外挿（pg < 0）。0.5·zJudge より手前は値が連続する傾き1の直線（極・符号反転の防止、手前端フェードより十分手前）。
  修正後は判定線の手前側でも地上比 1.02〜1.03。
- 判定線の奥側で地上・空中とも手前ほど厚くなるのはワールド固定厚みの遠近（従来どおり）。

## 4. ノーツの縦幅と判定線
- Tap / Ex Tap / Flick: 厚みはノーツ時刻の前後に対称で、**中央線**が判定時刻に判定線と重なる。
- Slide: 帯は始点時刻から始まるので**始点の辺**が基準。Riser: 壁（厚みなし）がノーツ時刻。

## 5. Slide 始点
- OnEnter の候補に Slide 始点（Pending/Active かつ未判定）を追加。窓は素の ±100ms、包含は連続座標。
- 確定は常に PERFECT+（EARLY/LATE なし）。窓を過ぎても来なければ UpdateSlide で MISS。
- AutoplayDriver は始点時刻を跨いだフレームで OnEnter を1回呼ぶ。

## 6. 検証
- `dotnet build Muses.csproj` エラー0。JudgeSmokeTest（dotnet スクラッチ）**35/35 PASS**（r3 で9ケース追加、既存の Slide 始点・Riser のティア値を新仕様に更新）。
- Autoplay（Tap+Riser 同時・連続する空中 Slide・地上 Slide）で全 PERFECT+。
- **未確認**: Unity 上の空中ノーツの見た目（シェーダ）、実機での手触り。
