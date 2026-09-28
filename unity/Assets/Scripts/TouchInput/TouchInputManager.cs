using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using Muses.Stage;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

namespace Muses.TouchInput
{
    /// <summary>
    /// 移植元: web-prototype/src/input.ts の InputManager。
    ///
    /// ブラウザの pointerdown/move/up イベントの代わりに、Unity では毎フレームのポーリングで
    /// 差分（新規接触／セル移動）を検出する（「入力範囲内の新規接触でヒット」というモデル自体は同じ）。
    /// Input System パッケージ（Touchscreen + Mouse）を使用。Editor確認用にマウスも拾う。
    ///
    /// 入力モデル（設計メモ「入力モデル」節そのまま）:
    ///   layer = (v > y_split) ? 1 : 0
    ///   cell  = clamp( floor( (u + U) * cells / (2U) ), 0, cells-1 )
    /// 層の境界は1本のみ。押下中のポインタにのみヒステリシスを入れる。セル境界は垂直。
    /// </summary>
    public class TouchInputManager : MonoBehaviour
    {
        [SerializeField] private StageController stageController;

        public Dictionary<int, Contact> Contacts { get; } = new();
        public HashSet<int> Occupied { get; } = new();
        public Action<EnterEvent> OnEnter;
        public List<(Layer layer, int cell, float born)> Ripples { get; } = new();

        private readonly List<float> eventTimes = new();

        /// <summary>直近1秒間のイベント数。入力のポーリングレート実測用</summary>
        public int EventRate
        {
            get
            {
                float now = Time.realtimeSinceStartup;
                while (eventTimes.Count > 0 && now - eventTimes[0] > 1f) eventTimes.RemoveAt(0);
                return eventTimes.Count;
            }
        }

        private Func<float> nowSec = () => 0f;

        public void Init(Func<float> nowSecProvider) => nowSec = nowSecProvider;

        private void OnEnable() => EnhancedTouchSupport.Enable();
        private void OnDisable() => EnhancedTouchSupport.Disable();

        private int Key(Layer layer, int cell) => (int)layer * stageController.Config.cells + cell;

        private static (float u, float v) Ndc(Vector2 screenPos)
        {
            float u = screenPos.x / Screen.width * 2f - 1f;
            float v = screenPos.y / Screen.height * 2f - 1f;
            return (u, v);
        }

        private int CellOf(float u)
        {
            var cfg = stageController.Config;
            float uu = cfg.U;
            int n = cfg.cells;
            int c = Mathf.FloorToInt((u + uu) * n / (2f * uu));
            return Mathf.Clamp(c, 0, n - 1);
        }

        /// <summary>note-spec.md §0.2。連続座標 cellF（u の線形写像、クランプなし）</summary>
        private float CellFOf(float u)
        {
            var cfg = stageController.Config;
            return (u + cfg.U) * cfg.cells / (2f * cfg.U);
        }

        /// <summary>
        /// note-spec.md §0.2。連続座標 layerF。判定線の画面位置(vGroundJudge/vSkyJudge)を
        /// 0/1 とする線形逆変換。離散判定(LayerOf、vSplit基準)とは別の量で、Slide/Flickの包含判定専用。
        /// </summary>
        private float LayerFOf(float v)
        {
            var cfg = stageController.Config;
            return (v - cfg.vGroundJudge) / (cfg.vSkyJudge - cfg.vGroundJudge);
        }

        private Layer LayerOf(float v, Layer? prev)
        {
            var cfg = stageController.Config;
            float s = cfg.vSplit;
            if (prev == null) return v > s ? Layer.Sky : Layer.Ground;
            float h = cfg.splitHysteresis;
            if (prev == Layer.Sky) return v > s - h ? Layer.Sky : Layer.Ground;
            return v > s + h ? Layer.Sky : Layer.Ground;
        }

        /// <summary>
        /// gameplay-feel-r2.md §3。地上パネル上端の重なり帯（v &gt; vSplit − skyTapExtendV）にいるか。
        /// 空中の接触では false。境界には層境界と同じヒステリシスを入れる（押下中のみ）。
        /// </summary>
        private bool SkyReachOf(Layer layer, float v, bool? prev)
        {
            var cfg = stageController.Config;
            if (layer == Layer.Sky || cfg.skyTapExtendV <= 0f) return false;
            float edge = cfg.vSplit - cfg.skyTapExtendV;
            if (prev == null) return v > edge;
            float h = cfg.splitHysteresis;
            return prev.Value ? v > edge - h : v > edge + h;
        }

        /// <summary>
        /// note-spec.md §0.1。層内の v 方向バンド分割（bandsPerLayer=2 確定）。
        /// 判定側はbandを無視するが、バンド境界をまたいだだけで同じセルへの枠内更新を発生させる
        /// （擦りで縦連を処理するため）。
        /// </summary>
        private int BandOf(Layer layer, float v)
        {
            var cfg = stageController.Config;
            float boundary = layer == Layer.Sky ? cfg.vBandSky : cfg.vBandGround;
            return v > boundary ? 1 : 0;
        }

        private void Update()
        {
            if (stageController == null) return;
            var active = new HashSet<int>();

            foreach (var t in Touch.activeTouches)
            {
                if (t.phase == UnityEngine.InputSystem.TouchPhase.Ended ||
                    t.phase == UnityEngine.InputSystem.TouchPhase.Canceled) continue;
                active.Add(t.finger.index);
                Feed(t.finger.index, t.screenPosition);
            }

            // Editor確認用のマウス入力
            if (Mouse.current != null && Mouse.current.leftButton.isPressed)
            {
                const int mouseId = -1;
                active.Add(mouseId);
                Feed(mouseId, Mouse.current.position.ReadValue());
            }

            var toRemove = new List<int>();
            foreach (var kv in Contacts)
                if (!active.Contains(kv.Key)) toRemove.Add(kv.Key);
            foreach (var id in toRemove)
            {
                var c = Contacts[id];
                Contacts.Remove(id);
                ReleaseCell(c.layer, c.cell, id);
            }
        }

        private void Feed(int id, Vector2 screenPos)
        {
            var (u, v) = Ndc(screenPos);
            eventTimes.Add(Time.realtimeSinceStartup);
            // note-spec.md §0.2: 連続座標は離散セル/層の変化の有無に関わらず毎フレーム更新する
            // （Slide/Flick の連続包含判定は境界またぎイベントに依存しないため）。
            float cellF = CellFOf(u);
            float layerF = LayerFOf(v);
            float at = nowSec();

            if (!Contacts.TryGetValue(id, out var c))
            {
                var layer = LayerOf(v, null);
                var cell = CellOf(u);
                var band = BandOf(layer, v);
                c = new Contact
                {
                    id = id, u = u, v = v, layer = layer, cell = cell, band = band,
                    skyReach = SkyReachOf(layer, v, null),
                    cellF = cellF, layerF = layerF, since = at,
                };
                Contacts[id] = c;
                Occupied.Add(Key(layer, cell));
                PushHistory(c, at);
                Emit(layer, cell, true, at, cellF, layerF, c.skyReach);
                return;
            }

            var newLayer = LayerOf(v, c.layer);
            var newCell = CellOf(u);
            var newBand = BandOf(newLayer, v);
            // 層が変わったら前の値は引き継がない（空中→地上に入った直後はヒステリシス無しで判定する）
            var newSkyReach = SkyReachOf(newLayer, v, newLayer == c.layer ? c.skyReach : null);
            bool cellOrLayerChanged = newLayer != c.layer || newCell != c.cell;
            bool bandChanged = newBand != c.band;
            // gameplay-feel-r2.md §3: 重なり帯への出入りもバンド境界と同じく枠内更新にする（擦り対応）
            bool skyReachChanged = newSkyReach != c.skyReach;
            c.u = u;
            c.v = v;
            c.cellF = cellF;
            c.layerF = layerF;
            c.skyReach = newSkyReach;
            PushHistory(c, at); // note-spec.md §4.1: Flickの移動履歴は境界またぎに関わらず毎フレーム積む

            if (cellOrLayerChanged)
            {
                ReleaseCell(c.layer, c.cell, id);
                c.layer = newLayer;
                c.cell = newCell;
            }
            if (cellOrLayerChanged || bandChanged || skyReachChanged)
            {
                c.band = newBand;
                if (cellOrLayerChanged) Occupied.Add(Key(newLayer, newCell));
                // バンド境界のみをまたいだ場合も同じセルへの枠内更新として発行する（§0.1の擦り対応）。
                Emit(newLayer, newCell, false, at, cellF, layerF, newSkyReach);
            }
        }

        /// <summary>note-spec.md §4.1。Flick用の移動履歴リングバッファ。flickWindowMsより古い分は間引く。</summary>
        private void PushHistory(Contact c, float at)
        {
            c.history.Add((c.u, c.v, at));
            float horizon = at - stageController.Config.flickWindowMs / 1000f;
            while (c.history.Count > 0 && c.history[0].t < horizon)
                c.history.RemoveAt(0);
        }

        private void ReleaseCell(Layer layer, int cell, int exceptId)
        {
            foreach (var kv in Contacts)
                if (kv.Key != exceptId && kv.Value.layer == layer && kv.Value.cell == cell) return; // 他の指が占有中
            Occupied.Remove(Key(layer, cell));
        }

        private void Emit(Layer layer, int cell, bool fresh, float at, float cellF, float layerF, bool skyReach)
        {
            Ripples.Add((layer, cell, at));
            OnEnter?.Invoke(new EnterEvent
            {
                layer = layer, cell = cell, fresh = fresh, at = at, cellF = cellF, layerF = layerF, skyReach = skyReach,
            });
        }

        /// <summary>セルのハイライト用。gameplay-feel-r2.md §3: 重なり帯の接触は空中側のセルも占有中として返す
        /// （空中 Tap が取れる位置だと分かるように）。接触は高々10本程度なので線形に数える。</summary>
        public bool IsOccupied(Layer layer, int cell)
        {
            if (Occupied.Contains(Key(layer, cell))) return true;
            if (layer != Layer.Sky) return false;
            foreach (var c in Contacts.Values)
                if (c.skyReach && c.cell == cell) return true;
            return false;
        }
    }
}
