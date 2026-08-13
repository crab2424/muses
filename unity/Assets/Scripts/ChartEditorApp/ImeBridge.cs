using System;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UIElements;

namespace Muses.ChartTool
{
    /// <summary>
    /// editor-ui-rework-r11.md §2 / r12.md §3。テキスト入力の3症状(キャレット点滅・IME・フォント)のうち、
    /// コードで直せる2つ(キャレット点滅・IME)をここにまとめる。ChartEditorAppからuiRoot/imeLayerを
    /// 渡されて生成する。ゲーム本体(GameController等)には一切依存させない
    /// （テキスト入力があるのはエディタだけのため）。
    ///
    /// §2.1 キャレット点滅: UnityEngine.UIElementsModule.dllを走査した結果、blink相当の実装が
    /// 存在しない(TextElement.DrawCaretが常時実線を描くだけ)ことを確認済み。ITextSelection.cursorColor
    /// を直接読み書きする方式だったが、Unity 6.5でこのプロパティが非推奨(--unity-cursor-color USS
    /// カスタムプロパティを使えとの案内、CS0618)になったため、USSクラス(.ime-caret-hidden、
    /// ChartEditorRoot.uss参照)の付け外しでキャレットを透明化する方式に変更した。全種類の
    /// テキスト系入力欄（TextField/IntegerField/FloatField）で共通に効く。
    ///
    /// §2.3/r12 §3.2 IME: New Input SystemのUI向けprovider(InputSystemProvider.cs)がIMECompositionEventを
    /// 未実装(// TODOのまま早期return)であることをパッケージソースで確認済み。旧Input Manager側の
    /// providerには実装があるが、本プロジェクトはactiveInputHandler=1(New Input System専用)のため
    /// 経路が構造的に存在しない。Input System側の公開IME API(Keyboard.SetIMEEnabled等)とUITKの
    /// 公開APIだけで自前ブリッジを組む。
    ///
    /// r11実機確認(composition回数=48・textInput回数=52・最新char='お')で、compositionと
    /// textInputの両方が実際に発火することを確認済み。r12ではその結果を踏まえ、未確定文字列を
    /// TextFieldの表示テキスト自体にインラインで差し込む方式に切り替える(以前は入力欄の外に
    /// 自前オーバーレイLabelを重ねていたため、位置・色・フォントが入力欄と揃わなかった)。
    /// 対象はTextFieldのみ(日本語入力が必要になるのはここだけ)。IntegerField/FloatFieldは
    /// フォーカス時にOS側のIME自体を止める(数値欄で変換窓が出て数値が打てなくなる事故を防ぐ)。
    /// </summary>
    public class ImeBridge : IDisposable
    {
        private const long BlinkIntervalMs = 530;

        private readonly VisualElement uiRoot;
        private readonly VisualElement imeLayer;

        // ---- §2.1 キャレット点滅 ----
        private const string CaretHiddenClass = "ime-caret-hidden";
        private TextElement focusedTextElement;
        private bool blinkVisible = true;
        private IVisualElementScheduledItem blinkSchedule;

        // ---- r12 §3.2 IME(未確定文字列のインライン表示) ----
        private TextField composingField;
        private string baseText = "";
        private int baseCursor;
        private string composition = "";
        private bool awaitingConfirm;
        private readonly StringBuilder pendingConfirm = new();
        private IVisualElementScheduledItem finalizeSchedule;

        // ---- 診断 ----
        public bool DebugOverlayEnabled;
        private Label debugLabel;
        private int compositionEventCount;
        private string lastComposition = "";
        private int textInputEventCount;
        private char lastTextInputChar;
        private Vector2 lastImeScreenPos;
        private string lastKeyDownLog = "(none)";

        public ImeBridge(VisualElement uiRoot, VisualElement imeLayer)
        {
            this.uiRoot = uiRoot;
            this.imeLayer = imeLayer;

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
            finalizeSchedule?.Pause();
            if (focusedTextElement != null) RestoreCursorColor();
        }

        // ================= キャレット点滅 =================

        /// <summary>TextField/IntegerField/FloatField等はフォーカスが内部の編集用要素へ渡る。
        /// その要素自身がTextElementであるか、子孫にUSSクラス"unity-text-element"(実テキスト要素、
        /// editor-ui-rework-r4.md §7続報で既知)を持つTextElementがあればそれを使う。</summary>
        private static TextElement ResolveTextElement(VisualElement target)
        {
            if (target is TextElement direct) return direct;
            return target?.Q<TextElement>(className: "unity-text-element");
        }

        /// <summary>r12 §3.2。フォーカス中の要素からTextField/IntegerField/FloatFieldの
        /// 「外側」の参照を辿る(cursorIndex/selectIndex/valueは内側のTextInputではなく
        /// こちらが持つ公開APIのため)。IME対象はTextFieldのみに絞る(§3.2の設計どおり)。</summary>
        private static VisualElement ResolveOwnerField(VisualElement target)
        {
            var el = target;
            while (el != null)
            {
                if (el is TextField || el is IntegerField || el is FloatField) return el;
                el = el.parent;
            }
            return null;
        }

        private void OnFocusIn(FocusInEvent evt)
        {
            var target = evt.target as VisualElement;
            var textElement = ResolveTextElement(target);
            if (textElement == null) return;

            focusedTextElement = textElement;
            blinkVisible = true;

            var owner = ResolveOwnerField(target);
            if (owner is TextField tf)
            {
                composingField = null; // 前回の変換状態を持ち越さない
                Keyboard.current?.SetIMEEnabled(true);
            }
            else
            {
                // r12 §3.2: 数値欄でIMEが起動すると変換窓が出て数値が打てなくなりうるため止める。
                Keyboard.current?.SetIMEEnabled(false);
            }
        }

        private void OnFocusOut(FocusOutEvent evt)
        {
            if (focusedTextElement == null) return;
            // フォーカスが外れる瞬間に変換の途中だった場合、確定を待たず今の内容で確定させる
            // (取りこぼすと入力中の文字が消える)。
            if (composingField != null) FinalizeComposition();

            RestoreCursorColor();
            focusedTextElement = null;
            Keyboard.current?.SetIMEEnabled(false);
        }

        private void RestoreCursorColor()
        {
            focusedTextElement.RemoveFromClassList(CaretHiddenClass);
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
            lastKeyDownLog = $"character='{evt.character}' keyCode={evt.keyCode}";
            RefreshDebugLabel();

            if (focusedTextElement != null)
            {
                blinkVisible = true;
                ApplyCursorVisibility();
            }

            // r12 §3.2 経路A: 変換中はTextField自身の標準キー処理(KeyDownEventのcharacterを
            // そのまま挿入する経路)へイベントを渡さない。表示はcomposingField.SetValueWithoutNotify
            // 経由でImeBridgeが唯一の権威として更新するため、二重に文字が入るのを防ぐ狙い。
            // 実際にこの経路(KeyDownEvent.character)経由で未確定文字が入力欄へ流れているかは
            // macOS実機でしか確認できない(editor-ui-rework-r12.md §3.2の経路A/B参照、診断表示の
            // KeyDownログで切り分ける)。
            if (composingField != null) evt.StopPropagation();
        }

        private void ApplyCursorVisibility()
        {
            if (blinkVisible)
                focusedTextElement.RemoveFromClassList(CaretHiddenClass);
            else
                focusedTextElement.AddToClassList(CaretHiddenClass);
        }

        // ================= r12 §3.2: IME(未確定文字列のインライン表示) =================

        private void OnImeCompositionChange(IMECompositionString compositionStr)
        {
            compositionEventCount++;
            string s = compositionStr.ToString();
            lastComposition = s;
            RefreshDebugLabel();

            var owner = focusedTextElement != null ? ResolveOwnerField(focusedTextElement) : null;
            if (owner is not TextField field)
            {
                composition = "";
                return;
            }

            if (!string.IsNullOrEmpty(s))
            {
                if (composingField == null)
                {
                    // 変換開始: この時点の確定済みテキスト/キャレット位置を基準として控える。
                    composingField = field;
                    baseText = field.value ?? "";
                    baseCursor = Mathf.Clamp(field.cursorIndex, 0, baseText.Length);
                }
                composition = s;
                string displayText = baseText.Insert(baseCursor, composition);
                // SetValueWithoutNotifyで表示だけ更新する。ここでTextField.valueを直接書き換えると
                // 未確定の途中経過ごとにRegisterValueChangedCallbackが発火し(例: song.artistへの
                // 反映やsongMetaDirtyが変換中ずっと立ってしまう)、確定前に保存対象へ混ざる。
                field.SetValueWithoutNotify(displayText);
                int caret = baseCursor + composition.Length;
                field.cursorIndex = caret;
                field.selectIndex = caret;
                UpdateImeCursorPosition();
            }
            else if (composingField != null)
            {
                // 変換終了(確定 or ESCでの取消)。表示を確定済みテキストへ一旦戻し、
                // 直後に届くはずのonTextInput(確定文字列)を1tick分だけ待って回収する
                // (composition側とtextInput側の到着順序がIME実装依存のため)。
                composingField.SetValueWithoutNotify(baseText);
                composingField.cursorIndex = baseCursor;
                composingField.selectIndex = baseCursor;
                composition = "";
                awaitingConfirm = true;
                pendingConfirm.Clear();
                finalizeSchedule?.Pause();
                // Execute()は既定で「次のスケジューラtickで1回だけ」実行される(Everyを付けなければ
                // 繰り返さない)。composition側とtextInput側の到着順序に関わらず、この1tickの間に
                // 届いたonTextInputをpendingConfirmへ回収してから確定する。
                finalizeSchedule = uiRoot.schedule.Execute(FinalizeComposition);
            }
        }

        private void OnTextInput(char c)
        {
            textInputEventCount++;
            lastTextInputChar = c;
            RefreshDebugLabel();

            // awaitingConfirm中(composition確定直後の1tick)のみ拾う。それ以外(IME非対象の欄への
            // 通常タイプ等)ではImeBridgeは何もしない(UITK自身の標準処理に委ねる)。
            if (awaitingConfirm && composingField != null && !char.IsControl(c))
                pendingConfirm.Append(c);
        }

        private void FinalizeComposition()
        {
            awaitingConfirm = false;
            if (composingField == null) return;

            string confirmed = pendingConfirm.ToString();
            pendingConfirm.Clear();
            string finalText = confirmed.Length == 0 ? baseText : baseText.Insert(baseCursor, confirmed);
            int finalCursor = baseCursor + confirmed.Length;

            // ここだけ通知ありのvalue代入にする(確定した時点で初めてモデルへ反映する、r12 §3.2の狙い)。
            composingField.value = finalText;
            composingField.cursorIndex = finalCursor;
            composingField.selectIndex = finalCursor;

            composingField = null;
            baseText = "";
            baseCursor = 0;
        }

        /// <summary>editor-ui-rework-r11.md §2.3: キャレット位置をOSのIME候補窓へ伝える。
        /// UI ToolkitのランタイムパネルはVisualElement.LocalToWorldで得られるのがパネル空間
        /// (左上原点・y下向き、UI Scale適用前)で、SetIMECursorPositionが期待するのはOSスクリーン座標
        /// (左下原点・y上向き、デバイスピクセル)と見られるため、scaledPixelsPerPointで
        /// デバイスピクセルへ換算してからy軸を反転する。この換算式が実際に正しいかはmacOS実機でしか
        /// 確認できない(コードを読んだだけでは確定できない、と設計時点で明記済み)。</summary>
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

        // ================= 診断オーバーレイ =================

        private void BuildOverlayElements()
        {
            debugLabel = new Label { pickingMode = PickingMode.Ignore };
            debugLabel.AddToClassList("ime-debug-overlay");
            debugLabel.style.display = DisplayStyle.None;
            imeLayer.Add(debugLabel);
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
                $"最新char='{lastTextInputChar}' imeCursorScreenPos={lastImeScreenPos}\n" +
                $"直近KeyDown: {lastKeyDownLog} composing={(composingField != null)}";
        }

        /// <summary>設定モーダルのトグルから呼ぶ。ON時は即座に現在の状態を反映する。</summary>
        public void SetDebugOverlayEnabled(bool enabled)
        {
            DebugOverlayEnabled = enabled;
            RefreshDebugLabel();
        }
    }
}
