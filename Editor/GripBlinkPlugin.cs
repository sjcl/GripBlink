using System.Collections.Generic;
using nadena.dev.modular_avatar.core;
using nadena.dev.ndmf;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

[assembly: ExportsPlugin(typeof(Sjcl.GripBlink.Editor.GripBlinkPlugin))]

namespace Sjcl.GripBlink.Editor
{
    /// <summary>
    /// ビルド時（アップロード時・Play 時）に GripBlink の設定から閉眼レイヤーを生成し、
    /// Modular Avatar の Merge Animator で FX に追加する。ビルド用に複製されたアバターだけを変更する。
    /// </summary>
    public class GripBlinkPlugin : Plugin<GripBlinkPlugin>
    {
        public override string QualifiedName => "com.sjcl.gripblink";
        public override string DisplayName => "GripBlink";

        protected override void Configure()
        {
            InPhase(BuildPhase.Generating)
                .BeforePlugin("nadena.dev.modular-avatar")
                .Run("Generate GripBlink layer", Execute);
        }

        private static void Execute(BuildContext ctx)
        {
            var components = ctx.AvatarRootObject.GetComponentsInChildren<GripBlink>(true);
            if (components.Length == 0) return;

            GripBlink primary = null;
            foreach (var c in components)
            {
                if (!c.gameObject.activeInHierarchy) continue;
                if (primary == null)
                {
                    primary = c;
                }
                else
                {
                    Debug.LogWarning($"[GripBlink] アバター「{ctx.AvatarRootObject.name}」に GripBlink が複数あります。"
                                     + $"「{primary.name}」のみ使用し、「{c.name}」は無視します。", c);
                }
            }

            if (primary != null) Apply(ctx, primary);

            foreach (var c in components) Object.DestroyImmediate(c);
        }

        private static void Apply(BuildContext ctx, GripBlink gb)
        {
            var diagnostics = new List<Diagnostic>();
            var controller = GripBlinkBuilder.Build(gb, ctx.AvatarRootObject, diagnostics);

            foreach (var d in diagnostics)
            {
                var msg = $"[GripBlink] {d.Text}";
                if (d.Severity == Severity.Info) Debug.Log(msg);
                else Debug.LogWarning(msg);
            }

            if (controller == null)
            {
                Debug.LogWarning($"[GripBlink] 有効な閉眼クリップがないため、「{ctx.AvatarRootObject.name}」には何も追加しませんでした。");
                return;
            }

            var merge = gb.gameObject.AddComponent<ModularAvatarMergeAnimator>();
            merge.animator = controller;
            merge.layerType = VRCAvatarDescriptor.AnimLayerType.FX;
            merge.pathMode = MergeAnimatorPathMode.Absolute;
            merge.matchAvatarWriteDefaults = true;
            merge.deleteAttachedAnimator = false;
            merge.layerPriority = gb.layerPriority;
        }
    }
}
