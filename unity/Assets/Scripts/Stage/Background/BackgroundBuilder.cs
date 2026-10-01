using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Muses.Stage.Background
{
    /// <summary>テーマが上書きするステージの面・線の色（ラボのテーマの stage: {...}）。色は sRGB（Material.SetColor が linear へ変換する）</summary>
    public struct StageTint
    {
        public Color groundFill; public float groundFillAlpha;
        public Color groundLine; public float groundLineAlpha;
        public Color skyFill; public float skyFillAlpha;
        public Color skyLine; public float skyLineAlpha;

        public static Color Hex(string hex) { ColorUtility.TryParseHtmlString(hex, out var c); return c; }
    }

    /// <summary>
    /// テーマ1つ分の実装。ラボのテーマファイル1つ（create(ctx) → object / update({t})）に対応する。
    /// Build でメッシュとマテリアルを作って parts に登録し、Tick で毎フレームの CPU 側の動き（Transform 等）を更新する。
    /// シェーダ内の動きはグローバルの _MusesBgTime を使えばよく、Tick は不要。
    /// </summary>
    public abstract class BackgroundBuilder
    {
        /// <summary>カメラのクリア色（ラボの clearColor）</summary>
        public abstract Color ClearColor { get; }
        /// <summary>ステージの面・線の色。null ならステージは従来の色</summary>
        public virtual StageTint? Tint => null;
        public abstract void Build(BackgroundContext ctx, BackgroundParts parts);
        public virtual void Tick(float t) { }
    }

    /// <summary>
    /// 背景のパーツ（GameObject + Mesh + Material）の登録と破棄。StageBackground が持つ。
    /// </summary>
    public sealed class BackgroundParts
    {
        private readonly Transform root;
        private readonly List<Object> owned = new List<Object>();

        internal BackgroundParts(Transform root) { this.root = root; }

        /// <summary>Resources/StageBackground/{name}.shader のマテリアルを作る</summary>
        public Material NewMaterial(string shaderName)
        {
            var shader = Resources.Load<Shader>("StageBackground/" + shaderName);
            if (shader == null)
            {
                Debug.LogError($"StageBackground: Resources/StageBackground/{shaderName}.shader が見つかりません");
                shader = Shader.Find("Hidden/InternalErrorShader");
            }
            var m = new Material(shader) { name = shaderName, hideFlags = HideFlags.DontSave };
            owned.Add(m);
            return m;
        }

        /// <summary>
        /// パーツを登録する。order はラボの renderOrder（−100〜99）。renderQueue = 2900 + order（ステージ 3000〜 より必ず先）。
        /// 返す GameObject は Tick で動かしてよい（ワールド原点・回転なしで作る）。
        /// </summary>
        public GameObject Add(string name, Mesh mesh, Material material, int order)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            material.renderQueue = 2900 + Mathf.Clamp(order, -100, 99);
            mesh.hideFlags = HideFlags.DontSave;
            if (mesh.bounds.size.x < 1e4f) mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e5f); // カリングさせない
            owned.Add(mesh);
            owned.Add(go);
            return go;
        }

        internal void DestroyAll()
        {
            foreach (var o in owned)
            {
                if (o == null) continue;
                if (Application.isPlaying) Object.Destroy(o); else Object.DestroyImmediate(o);
            }
            owned.Clear();
        }
    }
}
