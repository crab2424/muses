# muses 手触り改善 r2（2026-09-28）

gameplay-feel-r1 の実装後にユーザーがプレイして出た指摘・保留課題7件への設計。
判断が要る点（空中Tapの重なり帯・Riser/Diver判定・リザルトの粒度）はユーザーに確認済み。

| # | 指摘 | 結論 | 節 |
|---|---|---|---|
| 1 | Slide始点にも触れたらコンボが入るように | **変更なし**。始点は占有駆動のコンボ点として残っている（r1 §2）ことを確認。見た目も現状維持（ユーザー判断） | §1 |
| 2 | 判定に EARLY/LATE を出したい | PERFECT / GOOD のとき判定名の下に小さく出す。リザルトにも件数 | §2 |
| 3 | 空中Tapを取りこぼす | 空中Tapの判定枠を下へ `skyTapExtendV` 広げる。重なり帯は **1タッチ1ノーツ** | §3 |
| 4 | Riser/Diver で GOOD が多い | 原因2つを特定。**縦連なし・窓内最良・対称ティア＋遅い側延長・救済GOOD廃止・擦りの途中で枠内判定** | §4 |
| 5 | 判定演出を layerF 0〜1 の高さに追従 | 判定線の奥行きでの透視投影から画面 v を厳密に求める | §5 |
| 6 | リザルトに種別ごとの判定一覧 | 種別×層（Riser/Diver は別行）、0件の行は出さない | §6 |
| 7 | 曲終了→タイトル→曲を切り替えて開始で「準備中」フリーズ | **原因特定**: `Rechart` が旧ノーツで `Judge.Reset` を呼び新メッシュを範囲外書き込み | §7 |

---

## 1. Slide 始点（変更なし）

r1 §2 で「始点判定を廃止」と書いたが、廃止したのは **Tap と同じ枠内更新（叩く）駆動** であって、
始点は **占有駆動・時間対称のコンボ点として残っている**（`Judge.UpdateSlide` の `startResolved`、
スコア分母 N も `comboTimes.Count + 1`）。t0 ±100ms に帯の中に指があれば判定され、コンボが入る。
ユーザーに説明し、見た目（頭を描くか）も含めて現状維持で合意。

## 2. EARLY / LATE 表示

- `HitFlash` に `ms`（入力時刻 − ノーツ時刻、**正 = 遅い**。`Score.lastMs` と同じ符号）を持たせる。
- 判定名ポップアップの下に小さく `EARLY`（水色 `#60a5fa`）/ `LATE`（赤 `#f87171`）。
  - 出すのは **PERFECT と GOOD のときだけ**。PERFECT+ と MISS には出さない。
  - Slide のコンボ点にも出す（押し直しが遅れた＝LATE、早く離した＝EARLY が読める）。
  - 早い/遅いが意味を持たない判定（Flick の §4.4 救済 GOOD）は `ms = NaN` とし、表示しない。
- リザルト（§6）にも行ごとの EARLY / LATE 件数を出す（同じ規則: PERFECT と GOOD のみ数える）。

## 3. 空中 Tap の判定枠を下へ広げる（重なり帯）

### 3.1 原因
実シーンの値（`vSkyJudge 0.18 / vSplit −0.1 / vGroundJudge −0.55`）では、
空中判定線から層境界までが 0.28、地上判定線から層境界までが 0.45。**空中側だけ判定線の手前が狭い**。

### 3.2 仕様（note-spec §0.1 の追補、rev.9）
- `skyTapExtendV`（NDC、既定 **0.15**）: 地上パネルのうち `v > vSplit − skyTapExtendV` の帯（重なり帯）にある接触は、
  **地上のまま、空中 Tap の候補にもなる**（既定値で空中側の手前は 0.43 ≒ 地上側 0.45）。
- 対象は **離散判定の Tap / Ex Tap だけ**。Slide / Flick / Riser の連続座標判定（layerF）は変えない。
- **1タッチ1ノーツ**（ユーザー判断）: 重なり帯の枠内更新1回につき、地上・空中の両方の候補から
  **|dt| 最小の1ノーツ**を選ぶ。|dt| が同じならその接触が本来いる層（vSplit 基準の層）を優先。
  同時刻まとめ解決（§6.4）は**選ばれた層の中だけ**で行う。地上と空中の同時押しは従来どおり2本の指が要る。
  - **Why**: 両層とも取ると1本の指で地上＋空中の同時押しが取れてしまう（rev.3 で層間共有帯を見送った理由そのもの）。
    候補を1つに絞れば、誤爆は「近い時刻に両層のノーツがあるとき、近い方を取る」に限られる。
- 枠内更新の発生: 新規接触に加え、**重なり帯への出入り**も枠内更新として発行する（バンド境界と同じ扱い。擦り対応）。
  重なり帯の境界にも `splitHysteresis` を入れる。
- セルのハイライト: 重なり帯の接触は空中側のセルも光らせる（空中 Tap が取れる位置だと分かるように）。

### 3.3 実装
- `TouchInputManager`: `Contact.skyReach`（重なり帯を含めて空中候補か）を追加、`EnterEvent.skyReach` に載せる。
  `IsOccupied(Sky, cell)` は重なり帯の接触も数える。
- `Judge.OnEnter`: 候補の層判定を `e.layer` 一致 → 「`e.layer` 一致 または（`e.skyReach` かつ 空中ノーツ）」に。
  best 選択後、同時刻まとめ解決は best の層で行う。
- `StageConfig.skyTapExtendV`（フィールド初期化子付き、シーンの既存値は 0 にならない）。

## 4. Riser / Diver の判定（note-spec §4.6 の改訂、rev.9）

### 4.1 GOOD が多い原因（コードで確認した事実）
1. **判定域の端と成立距離が一致していた**: 成立条件は「**いま**の指が元の層の判定域（|layerF − 0| ≤ 0.5）にいる」∧
   「直近 flickWindowMs に上へ riserDistanceV 以上動いた」。riserDistanceV は riserReachFrac=1 で
   「判定線 → layerF 0.5」の距離そのもの。つまり**判定線ちょうどから擦り始めると、距離が足りた瞬間に判定域の上端**、
   判定線より上から擦り始めると**距離が足りる前に判定域を出る** → 成立しないまま窓が閉じ、
   §4.4 の救済（枠内に触れてはいた）で **GOOD**。Diver も上下対称に同じ。
2. **判定時刻が「距離を稼ぎ切った瞬間」**: 画面の約18%（iPad で約3cm）を動かすのに 60〜100ms かかるので、
   ノーツ時刻ちょうどに擦り始めても判定上は遅押しになる。
- さらに、Riser 自身の窓の遅い側が縦連（同層・セル交差の次ノーツ）で削られうる実装になっていた。

### 4.2 新仕様（ユーザー判断）
- **縦連判定を受けない**（前後とも窓を削られない）。他ノーツにとっての縦連の境界にはなる（Ex Tap と同じ非対称。対象集合 T には残す）。
- **反応（reaction）** の定義: フレーム時刻 s で、ある接触 c について
  - 直近 flickWindowMs の履歴の中で、**指定方向への最大変位**（履歴中で一番「後ろ」の点から現在まで）が riserDistanceV 以上、かつ
  - **履歴のどこかで元の層の判定域の中を通った**（layerF 半径 `layerJudgeRadius`、横は `riserMarginCells` 余白。
    layerF は [0,1] クランプ・handoff 読み替えは従来どおり）。
  → 4.1-1 の修正: 判定線より上から擦り始めても、途中で判定域を通っていれば成立する。
- **窓内最良**: 窓 `[t − 100ms, t + 100ms + riserLateShiftMs]` の中の反応サンプルのうち、**最も良いもの**でティアを決める
  （Slide コンボ点 §2.4 と同じ時間対称・サンプル方式）。早 GOOD の反応があっても即確定せず、その後 PERFECT+ の反応があればそちらを採る。
- **対称のティア＋遅い側の延長**: 早い側は通常ティア（PERFECT+ 33.33 / PERFECT 66.67 / GOOD 100）。
  遅い側は全境界を `riserLateShiftMs`（既定 **50ms**）だけ後ろへずらす（PERFECT+ ≤ +83.33 / PERFECT ≤ +116.67 / GOOD ≤ +150）。
  実装上は「実効ずれ」 `eff = diff ≤ 0 ? diff : max(0, diff − shift)` の |eff| で通常ティア表を引く（|eff| 最小が最良）。
  → 4.1-2 の対策（擦り切るまでの時間を遅い側で吸収）。Flick の「早い側は全て PERFECT+」は Riser には適用しなくなる。
- **早期確定**: now ≥ t で、既知の最良 |eff| ≤ effLate(now − t)（= max(0, now − t − shift)）なら確定
  （以降のサンプルは必ずそれ以上の |eff| になるため、待っても結果は同じ）。窓の終わりで反応が無ければ MISS。
- **救済 GOOD を廃止**（Riser/Diver のみ。Flick の §4.4 は現状維持）: 窓の中で一度も反応しなければ MISS。
- **消費**: 確定させた反応を出した接触の履歴をクリアする（1回の擦りで成立させられる Riser は1つまで、従来どおり）。
- **handoff と巻き込み**: 確定時に handoff を記録し、行き先層の合成 EnterEvent を発火するのは従来どおり。
  ただし合成イベントの判定時刻は**実効ずれで補正した時刻** `t + eff`（最良サンプルの時刻から遅い側の延長分を引いたもの）にする。
  → Riser と同時刻の行き先層 Tap が「擦り切るまでの時間」ぶん遅押し扱いになる副作用を消す。
  - 既知の限界: Riser が遅い GOOD（+100ms 超）で確定した場合、同時刻の行き先層 Tap は既に窓切れで MISS になっていることがある。

### 4.3 パラメータ
| 名前 | 既定 | 意味 |
|---|---|---|
| `riserLateShiftMs` | 50 | Riser/Diver の遅い側のティア境界を後ろへずらす量 |
（`riserReachFrac` / `flickWindowMs` / `riserMarginCells` は据え置き）

### 4.4 Flick との違い（将来の検討事項）
Flick は従来どおり「早い側は全て PERFECT+・最初の成立で即確定・移動なしで枠内更新ありなら GOOD」。
ユーザーは Riser の新仕様を「Flick や Slide 中継点と似ている」と捉えているので、Flick も揃えるかは実機後に確認する。

## 5. 判定演出の高さを layerF に追従

- `HitFlash.layerF`（演出を出す layerF）を追加。Tap/ExTap/Flick は層にスナップした 0/1（描画もスナップしている）、
  Slide のコンボ点は `ChartMath.At(n, tp).layerF`（連続）、Riser は始点の layerF。
- 画面 v は判定線の奥行き z_J で高さ `layerF × skyHeight` の点を投影して求める（`StageDerive.JudgeLineV`）:
  `v = tan(θ − atan((yCam − layerF·skyHeight) / z_J)) / tan(φ/2)`。layerF=0/1 で vGroundJudge/vSkyJudge に一致。
  横位置は判定線上では層によらず `u = cellU` なので従来どおり。
- ポップアップの使い回し判定は「同じ層」→「layerF の差 < 0.1」に。GOOD/MISS の矩形フラッシュも同じ高さを使う。

## 6. リザルトの種別ごとの判定一覧

- 行: Tap 地上 / Tap 空中 / Ex Tap 地上 / Ex Tap 空中 / Slide 地上 / Slide 空中 / Flick 地上 / Flick 空中 / Riser / Diver。
  層は判定点の layerF > 0.5 で空中（Slide はコンボ点ごと）。**0件の行は出さない**。
- 列: PERFECT+ / PERFECT / GOOD / MISS / EARLY / LATE（EARLY/LATE は §2 と同じ規則）。
- `Score.byCategory[ResultCategory]` に Judge が判定のたび加算する（既存の合計カウンタはそのまま）。

## 7. 「準備中」フリーズ

- 原因: `GameController.Rechart()` が `noteView.Build(新譜面)` の後に `judge.Reset()` → `judge.Prepare(新ノーツ)` の順で呼んでいた。
  `Reset()`（= `Seek(0)`）の時点で Judge は**前の曲のノーツ**を持っており、その頂点範囲で**新しいメッシュ**の
  alpha 配列に書き込む。新しい譜面の頂点数が少ないと `IndexOutOfRangeException` → ロードのコルーチンが
  「準備中…」のまま止まる。同じ譜面のリトライ・起動直後の1曲目では起きない（報告の再現条件と一致）。
- 修正: `Prepare` → `Reset` の順に。加えて `AppController.LoadAndStart` の後半を try/catch し、
  例外時はタイトルへ戻してエラーを表示する（今後別の原因で落ちても固まらない）。

## 8. 検証
- Judge: `JudgeSmokeTest` に追加（重なり帯の1タッチ1ノーツ、Riser の判定線より上からの擦り、窓内最良、
  救済廃止、縦連で窓が削られない）。Unity 外の dotnet スクラッチでも確認する。
- 見た目（EARLY/LATE・演出の高さ・リザルト表）はユーザーの Editor / 実機確認。

## 9. 実機で決める値
`skyTapExtendV`（0.15）、`riserLateShiftMs`（50）、EARLY/LATE の文字の大きさ・色。

## 10. 実装状況（2026-09-28、未コミット）
§2〜§7 を実装済み。実装中に詰めた点（上の節より優先）:
- **Riser の反応は「条件の立ち上がり」だけを記録する**（接触ごと、`NoteRuntime.riserReacting`）。
  満たしている間を毎フレーム数えると、擦り切った後に指を止めていても履歴（flickWindowMs=120ms）が残る間は
  反応し続け、早く擦っても PERFECT+ 窓まで反応が伸びて早 GOOD が実質出なくなるため（ユーザー仕様と矛盾）。
- **早期確定はティアで比べる**: 既知の最良のティア ≤ 今後のサンプルで取りうる最良のティア なら確定。
  ずれの大小で比べると、PERFECT+ 内で数ms早いだけの反応でも +50ms 以上待つことになるため。結果のティアは待った場合と同じ。
- Riser の `ms`（HUD・EARLY/LATE）は**実効ずれ**で記録する（ティアと表示が食い違わないように）。
- **ついでに発見・修正**: `AutoplayDriver` が Riser を扱っておらず、譜面エディタのオートプレイで Riser が常に MISS だった → 合成入力を追加。

検証:
- `dotnet build`（`Muses.csproj` / `Muses.ChartTool.csproj`）ともエラー0。
- `JudgeSmokeTest` を Unity 外の dotnet スクラッチ（UnityEngine スタブ）で実行し **26/26 PASS**（r2 で11ケース追加）。
- Autoplay（Tap・Riser・同時刻の空中 Tap・Diver）で全 PERFECT+ を確認。
- **未確認**: Unity 上の表示（EARLY/LATE の位置・大きさ、中間層の演出位置、リザルト表のレイアウト）、実機での重なり帯と Riser の手触り。

### 確認してほしい点
1. 曲終了 → タイトル → 曲を切り替えて START で固まらないこと（§7）。
2. 空中 Tap の取りこぼしが減ったか、地上 Tap の誤爆が増えていないか（`skyTapExtendV` 0.15）。
3. Riser/Diver の GOOD が減ったか、早い/遅いの感触（`riserLateShiftMs` 50）。
4. EARLY/LATE・演出の高さ（層の中間を通る Slide）・リザルト表の見た目。
