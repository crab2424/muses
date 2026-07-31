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

            if (!Contacts.TryGetValue(id, out var c))
            {
                var layer = LayerOf(v, null);
                var cell = CellOf(u);
                float at = nowSec();
                c = new Contact { id = id, u = u, v = v, layer = layer, cell = cell, cellF = cellF, layerF = layerF, since = at };
                Contacts[id] = c;
                Occupied.Add(Key(layer, cell));
                Emit(layer, cell, true, at, cellF, layerF);
                return;
            }

            var newLayer = LayerOf(v, c.layer);
            var newCell = CellOf(u);
            c.u = u;
            c.v = v;
            c.cellF = cellF;
            c.layerF = layerF;
            if (newLayer != c.layer || newCell != c.cell)
            {
                ReleaseCell(c.layer, c.cell, id);
                c.layer = newLayer;
                c.cell = newCell;
                Occupied.Add(Key(newLayer, newCell));
                Emit(newLayer, newCell, false, nowSec(), cellF, layerF);
            }
        }

        private void ReleaseCell(Layer layer, int cell, int exceptId)
        {
            foreach (var kv in Contacts)
                if (kv.Key != exceptId && kv.Value.layer == layer && kv.Value.cell == cell) return; // 他の指が占有中
            Occupied.Remove(Key(layer, cell));
        }

        private void Emit(Layer layer, int cell, bool fresh, float at, float cellF, float layerF)
        {
            Ripples.Add((layer, cell, at));
            OnEnter?.Invoke(new EnterEvent { layer = layer, cell = cell, fresh = fresh, at = at, cellF = cellF, layerF = layerF });
        }

        public bool IsOccupied(Layer layer, int cell) => Occupied.Contains(Key(layer, cell));
    }
}
