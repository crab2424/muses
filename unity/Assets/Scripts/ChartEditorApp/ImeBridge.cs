using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UIElements;

namespace Muses.ChartTool
{
    /// <summary>
    /// editor-ui-rework-r11.md §2。テキスト入力の3症状(キャレット点滅・IME・フォント)のうち、
    /// コードで直せる2つ(キャレット点滅・IME)をここにまとめる。ChartEditorAppからuiRoot/overlayLayerを
    /// 渡されて生成する。ゲーム本体(GameController等)には一切依存させない
    /// （テキスト入力があるのはエディタだけのため）。
    ///
    /// §2.1 キャレット点滅: UnityEngine.UIElementsModule.dllを走査した結果、blink相当の実装が
    /// 存在しない(TextElement.DrawCaretが常時実線を描くだけ)ことを確認済み。公開API
    /// (TextInputBaseField&lt;T&gt;.textSelection / TextElement.selection、いずれもITextSelection)
    /// のcursorColorをスケジューラで周期的にトグルして自前で再現する。
    ///
    /// §2.3 IME: New Input SystemのUI向けprovider(InputSystemProvider.cs)がIMECompositionEventを
    /// 未実装(// TODOのまま早期return)であることをパッケージソースで確認済み。旧Input Manager側の
    /// providerには実装があるが、本プロジェクトはactiveInputHandler=1(New Input System専用)のため
    /// 経路が構造的に存在しない。Input System側の公開IME API(Keyboard.SetIMEEnabled等)とUITKの
    /// 公開APIだけで自前ブリッジを組む。macOSで実際にIMEの候補窓が動くかはUnityのネイティブ実装
    /// 依存でコードからは確定できないため、まず診断オーバーレイで実機確認できる形にしてある
    /// (DebugOverlayEnabled)。
    /// </summary>
    public class ImeBridge : IDisposable
    {
        private const long BlinkIntervalMs = 530;

        private readonly VisualElement uiRoot;
        private readonly VisualElement overlayLayer;

        // ---- §2.1 キャレット点滅 ----
        private TextElement focusedTextElement;
        private Color focusedOriginalCursorColor;
        private bool blinkVisible = true;
        private IVisualElementScheduledItem blinkSchedule;

        // ---- §2.3 IME ----
        public bool DebugOverlayEnabled;
        private bool imeEnabledForFocus;
        private Label debugLabel;
        private Label compositionOverlay;
        private int compositionEventCount;
        private string lastComposition = "";
        private int textInputEventCount;
        private char lastTextInputChar;
        private Vector2 lastImeScreenPos;

        public ImeBridge(VisualElement uiRoot, VisualElement overlayLayer)
        {
            this.uiRoot = uiRoot;
            this.overlayLayer = overlayLayer;

            uiRoot.RegisterCallback<FocusInEvent>(OnFocusIn, TrickleDown.TrickleDown);
            uiRoot.RegisterCallback<FocusOutEvent>(OnFocusOut, TrickleDown.TrickleDown);
            uiRoot.RegisterCallback<KeyDownEvent>(OnAnyKeyDown, TrickleDown.TrickleDown);

            if (Keyboard.current != null)
            {
                Keyboard.current.onIMECompositionChange += OnImeCompositionChange;
                Keyboard.current.onTextInput += OnTextInput;
            }

            blinkSchedule = uiRoot.schedule.Execute(TickBlink).Every(BlinkIntervalMs);

            BuildOverlayElements();
        }

        public void Dispose()
        {
            if (Keyboard.current != null)
            {
                Keyboard.current.onIMECompositionChange -= OnImeCompositionChange;
                Keyboard.current.onTextInput -= OnTextInput;
            }
            blinkSchedule?.Pause();
            if (focusedTextElement != null) RestoreCursorColor();
        }

        // ================= キャレット点滅 =================

        /// <summary>TextField/IntegerField/FloatField等はフォーカスが内部の編集用要素へ渡る。
        /// その要素自身がTextElementであるか、子孫にUSSクラス"unity-text-element"(実テキスト要素、
        /// editor-ui-rework-r4.md §7続報で既知)を持つTextElementがあればそれを使う。
        /// リフレクションを使わずに済む(TextField/IntegerField/FloatFieldはTextInputBaseField&lt;T&gt;の
        /// 閉じた総称型でそれぞれ別の型になるため、共通の非総称インターフェースが無いとリフレクション
        /// 必須になるところだった)。</summary>
        private static TextElement ResolveTextElement(VisualElement target)
        {
            if (target is TextElement direct) return direct;
            return target?.Q<TextElement>(className: "unity-text-element");
        }

        private void OnFocusIn(FocusInEvent evt)
        {
            var textElement = ResolveTextElement(evt.target as VisualElement);
            if (textElement == null) return;

            focusedTextElement = textElement;
            focusedOriginalCursorColor = textElement.selection.cursorColor;
            blinkVisible = true;

            imeEnabledForFocus = true;
            Keyboard.current?.SetIMEEnabled(true);
        }

        private void OnFocusOut(FocusOutEvent evt)
        {
            if (focusedTextElement == null) return;
            RestoreCursorColor();
            focusedTextElement = null;

            if (imeEnabledForFocus)
            {
                imeEnabledForFocus = false;
                Keyboard.current?.SetIMEEnabled(false);
            }
            HideCompositionOverlay();
        }

        private void RestoreCursorColor()
        {
            focusedTextElement.selection.cursorColor = focusedOriginalCursorColor;
            focusedTextElement.MarkDirtyRepaint();
        }

        private void TickBlink()
        {
            if (focusedTextElement == null) return;
            blinkVisible = !blinkVisible;
            ApplyCursorVisibility();
        }

        /// <summary>打鍵直後は必ず表示状態に戻す(点滅の谷で消えたまま打鍵し続けると
        /// 打っている位置が分からず不快なため)。</summary>
        private void OnAnyKeyDown(KeyDownEvent evt)
        {
            if (focusedTextElement == null) return;
            blinkVisible = true;
            ApplyCursorVisibility();
        }

        private void ApplyCursorVisibility()
        {
            var c = focusedOriginalCursorColor;
            focusedTextElement.selection.cursorColor = blinkVisible ? c : new Color(c.r, c.g, c.b, 0f);
            focusedTextElement.MarkDirtyRepaint();
        }

        // ================= IME =================

        private void OnImeCompositionChange(IMECompositionString composition)
        {
            compositionEventCount++;
            lastComposition = composition.ToString();

            if (focusedTextElement != null && !string.IsNullOrEmpty(lastComposition))
            {
                UpdateImeCursorPosition();
                ShowCompositionOverlay(lastComposition);
            }
            else
            {
                HideCompositionOverlay();
            }
            RefreshDebugLabel();
        }

        private void OnTextInput(char c)
        {
            textInputEventCount++;
            lastTextInputChar = c;
            RefreshDebugLabel();
        }

        /// <summary>editor-ui-rework-r11.md §2.3: キャレット位置をOSのIME候補窓へ伝える。
        /// UI ToolkitのランタイムパネルはVisualElement.LocalToWorldで得られるのがパネル空間
        /// (左上原点・y下向き、UI Scale適用前)で、SetIMECursorPositionが期待するのはOSスクリーン座標
        /// (左下原点・y上向き、デバイスピクセル)と見られるため、scaledPixelsPerPointで
        /// デバイスピクセルへ換算してからy軸を反転する。この換算式が実際に正しいかはmacOS実機でしか
        /// 確認できない(コードを読んだだけでは確定できない、と設計時点で明記済み)。
        /// 診断オーバーレイ(DebugOverlayEnabled)が最後に計算した値を表示するので、
        /// 候補窓の実際の位置とずれていたらそこから補正する。</summary>
        private void UpdateImeCursorPosition()
        {
            if (focusedTextElement == null || Keyboard.current == null) return;
            Vector2 localCursor = focusedTextElement.selection.cursorPosition;
            Vector2 panelPos = focusedTextElement.LocalToWorld(localCursor);
            float scale = Mathf.Max(0.01f, focusedTextElement.scaledPixelsPerPoint);
            var screenPos = new Vector2(panelPos.x * scale, Screen.height - panelPos.y * scale);
            lastImeScreenPos = screenPos;
            Keyboard.current.SetIMECursorPosition(screenPos);
        }

        // ================= オーバーレイ(変換中文字列の表示 / 診断) =================

        private void BuildOverlayElements()
        {
            compositionOverlay = new Label { pickingMode = PickingMode.Ignore };
            compositionOverlay.AddToClassList("ime-composition-overlay");
            compositionOverlay.style.display = DisplayStyle.None;
            overlayLayer.Add(compositionOverlay);

            debugLabel = new Label { pickingMode = PickingMode.Ignore };
            debugLabel.AddToClassList("ime-debug-overlay");
            debugLabel.style.display = DisplayStyle.None;
            overlayLayer.Add(debugLabel);
        }

        /// <summary>UITKのテキストに下線スタイルは無いため、border-bottomで代用する
        /// (設計メモどおり)。</summary>
        private void ShowCompositionOverlay(string text)
        {
            if (focusedTextElement == null) return;
            Vector2 localCursor = focusedTextElement.selection.cursorPosition;
            Vector2 panelPos = focusedTextElement.LocalToWorld(localCursor);
            Vector2 localInOverlay = overlayLayer.WorldToLocal(panelPos);

            compositionOverlay.text = text;
            compositionOverlay.style.display = DisplayStyle.Flex;
            compositionOverlay.style.left = localInOverlay.x;
            compositionOverlay.style.top = localInOverlay.y - 16f;
        }

        private void HideCompositionOverlay()
        {
            compositionOverlay.style.display = DisplayStyle.None;
        }

        private void RefreshDebugLabel()
        {
            if (!DebugOverlayEnabled)
            {
                debugLabel.style.display = DisplayStyle.None;
                return;
            }
            debugLabel.style.display = DisplayStyle.Flex;
            bool imeSelected = Keyboard.current?.imeSelected.isPressed ?? false;
            debugLabel.text =
                $"IME診断: imeSelected={imeSelected} composition回数={compositionEventCount} " +
                $"最新composition=\"{lastComposition}\" textInput回数={textInputEventCount} " +
                $"最新char='{lastTextInputChar}' imeCursorScreenPos={lastImeScreenPos}";
        }

        /// <summary>設定モーダルのトグルから呼ぶ。ON時は即座に現在の状態を反映する。</summary>
        public void SetDebugOverlayEnabled(bool enabled)
        {
            DebugOverlayEnabled = enabled;
            RefreshDebugLabel();
        }
    }
}
