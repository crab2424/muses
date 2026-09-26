using UnityEngine;
using UnityEngine.UIElements;

namespace Muses.ChartTool
{
    /// <summary>
    /// editor-ui-rework-r14.md §1.3。トランスポートボタンのアイコン。以前は `|◀ ■ ▶ ❙❙ ▶|` を文字で
    /// 出していたが、Noto Sans JPでは▶◀■が全角幅かつ大きさ・左右の余白が不揃いで、❙はグリフ自体が無い。
    /// 画像アセットを増やさずに済むよう、Painter2Dで図形として描く。色は親ボタンの文字色に従う
    /// （無効時の見た目もUSSのままになる）。
    /// </summary>
    public class TransportIcon : VisualElement
    {
        public enum Kind { ToStart, Stop, Play, Pause, ToEnd }

        private Kind kind;
        public Kind IconKind
        {
            get => kind;
            set
            {
                if (kind == value) return;
                kind = value;
                MarkDirtyRepaint();
            }
        }

        private const float Size = 10f;

        public TransportIcon(Kind kind)
        {
            this.kind = kind;
            pickingMode = PickingMode.Ignore;
            style.width = Size;
            style.height = Size;
            style.alignSelf = Align.Center;
            generateVisualContent += Draw;
            // 親ボタンのホバー/無効化で継承される文字色が変わったら描き直す。
            RegisterCallback<CustomStyleResolvedEvent>(_ => MarkDirtyRepaint());
        }

        private void Draw(MeshGenerationContext mgc)
        {
            var p = mgc.painter2D;
            p.fillColor = resolvedStyle.color;
            const float s = Size;
            const float bar = 2f;
            switch (kind)
            {
                case Kind.Play:
                    Triangle(p, new Vector2(1f, 0f), new Vector2(1f, s), new Vector2(s, s * 0.5f));
                    break;
                case Kind.Pause:
                    Rect(p, 1.5f, 0f, 2.5f, s);
                    Rect(p, s - 4f, 0f, 2.5f, s);
                    break;
                case Kind.Stop:
                    Rect(p, 1f, 1f, s - 2f, s - 2f);
                    break;
                case Kind.ToStart:
                    Rect(p, 0f, 0f, bar, s);
                    Triangle(p, new Vector2(s, 0f), new Vector2(s, s), new Vector2(bar + 0.5f, s * 0.5f));
                    break;
                case Kind.ToEnd:
                    Triangle(p, new Vector2(0f, 0f), new Vector2(0f, s), new Vector2(s - bar - 0.5f, s * 0.5f));
                    Rect(p, s - bar, 0f, bar, s);
                    break;
            }
        }

        private static void Triangle(Painter2D p, Vector2 a, Vector2 b, Vector2 c)
        {
            p.BeginPath();
            p.MoveTo(a);
            p.LineTo(b);
            p.LineTo(c);
            p.ClosePath();
            p.Fill();
        }

        private static void Rect(Painter2D p, float x, float y, float w, float h)
        {
            p.BeginPath();
            p.MoveTo(new Vector2(x, y));
            p.LineTo(new Vector2(x + w, y));
            p.LineTo(new Vector2(x + w, y + h));
            p.LineTo(new Vector2(x, y + h));
            p.ClosePath();
            p.Fill();
        }
    }
}
