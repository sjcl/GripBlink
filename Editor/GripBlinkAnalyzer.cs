using System.Collections.Generic;
using System.Linq;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Sjcl.GripBlink.Editor
{
    internal enum Severity
    {
        Info,
        Warning,
        Error,
    }

    internal readonly struct Diagnostic
    {
        public readonly Severity Severity;
        public readonly string Text;

        public Diagnostic(Severity severity, string text)
        {
            Severity = severity;
            Text = text;
        }
    }

    /// <summary>1つの閉眼チャンネル（両目、左目、右目のいずれか）</summary>
    internal readonly struct EyeChannel
    {
        public readonly string Label;
        public readonly AnimationClip Clip;
        public readonly GripHand Hand;

        public EyeChannel(string label, AnimationClip clip, GripHand hand)
        {
            Label = label;
            Clip = clip;
            Hand = hand;
        }

        public string GestureParam => Hand == GripHand.Left ? "GestureLeft" : "GestureRight";
        public string WeightParam => Hand == GripHand.Left ? "GestureLeftWeight" : "GestureRightWeight";
    }

    /// <summary>クリップから取り出した1プロパティ分の「開いた値」と「閉じた値」</summary>
    internal readonly struct PoseEntry
    {
        public readonly EditorCurveBinding Binding;
        public readonly float Rest;
        public readonly float Closed;

        public PoseEntry(EditorCurveBinding binding, float rest, float closed)
        {
            Binding = binding;
            Rest = rest;
            Closed = closed;
        }
    }

    /// <summary>
    /// クリップ解析と競合検出。Inspector とビルド処理の両方から使う。
    /// ここでは何も書き換えない（読み取り専用）。
    /// </summary>
    internal static class GripBlinkAnalyzer
    {
        public static VRCAvatarDescriptor FindAvatar(Component c)
        {
            return c == null ? null : c.GetComponentInParent<VRCAvatarDescriptor>(true);
        }

        public static List<EyeChannel> GetChannels(GripBlink gb)
        {
            var list = new List<EyeChannel>();
            if (gb.mode == EyeControlMode.BothEyes)
            {
                list.Add(new EyeChannel("両目", gb.blinkClip, gb.hand));
            }
            else
            {
                list.Add(new EyeChannel("左目", gb.leftEyeClip, gb.leftEyeHand));
                list.Add(new EyeChannel("右目", gb.rightEyeClip, gb.rightEyeHand));
            }

            return list;
        }

        /// <summary>
        /// クリップから閉眼ポーズを取り出す。
        /// 開いた値はアバター上の現在値、閉じた値は各カーブで現在値から最も離れたキーの値（または指定時刻の値）。
        /// 現在値から変化しないプロパティは除外し、表情への影響を最小限にする。
        /// </summary>
        public static List<PoseEntry> ComputePose(AnimationClip clip, GameObject avatarRoot, bool autoPeak,
            float sampleTime, List<Diagnostic> diagnostics, string label)
        {
            var result = new List<PoseEntry>();
            if (clip == null || avatarRoot == null) return result;

            var missing = new List<string>();
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.length == 0) continue;

                if (!AnimationUtility.GetFloatValue(avatarRoot, binding, out var rest))
                {
                    missing.Add(Describe(binding));
                    continue;
                }

                float closed;
                if (autoPeak)
                {
                    closed = curve.keys[0].value;
                    foreach (var key in curve.keys)
                    {
                        if (Mathf.Abs(key.value - rest) > Mathf.Abs(closed - rest)) closed = key.value;
                    }
                }
                else
                {
                    closed = curve.Evaluate(sampleTime);
                }

                if (Mathf.Abs(closed - rest) < 1e-4f) continue;
                result.Add(new PoseEntry(binding, rest, closed));
            }

            if (diagnostics != null)
            {
                if (missing.Count > 0)
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning,
                        $"{label}: クリップ「{clip.name}」の {missing.Count} 件のプロパティがアバター上に見つからないため無視します"
                        + $"（例: {missing[0]}）。アバターのルートから記録されたクリップか確認してください。"));
                }

                if (AnimationUtility.GetObjectReferenceCurveBindings(clip).Length > 0)
                {
                    diagnostics.Add(new Diagnostic(Severity.Info,
                        $"{label}: クリップ「{clip.name}」内のオブジェクト参照カーブ（マテリアル差し替え等）は使用しません。"));
                }

                if (result.Count == 0 && missing.Count == 0)
                {
                    diagnostics.Add(new Diagnostic(Severity.Error,
                        $"{label}: クリップ「{clip.name}」に現在の状態から変化する値がありません。目を閉じるクリップを指定してください。"));
                }
            }

            return result;
        }

        public static string Describe(EditorCurveBinding b)
        {
            var path = string.IsNullOrEmpty(b.path) ? "(root)" : b.path;
            return $"{path}/{b.propertyName}";
        }

        // ------------------------------------------------------------------
        // 競合検出
        // ------------------------------------------------------------------

        /// <summary>FX に入る既存のアニメーター（アバター記述子と他の MA Merge Animator）</summary>
        private readonly struct FxSource
        {
            public readonly string Name;
            public readonly AnimatorController Controller;
            public readonly string PathPrefix;

            public FxSource(string name, AnimatorController controller, string pathPrefix)
            {
                Name = name;
                Controller = controller;
                PathPrefix = pathPrefix;
            }
        }

        private static IEnumerable<FxSource> GetFxSources(VRCAvatarDescriptor desc, GripBlink self)
        {
            if (desc.customizeAnimationLayers && desc.baseAnimationLayers != null)
            {
                foreach (var layer in desc.baseAnimationLayers)
                {
                    if (layer.type == VRCAvatarDescriptor.AnimLayerType.FX && !layer.isDefault
                                                                            && layer.animatorController is AnimatorController ac)
                    {
                        yield return new FxSource($"FX「{ac.name}」", ac, "");
                    }
                }
            }

            foreach (var merge in desc.GetComponentsInChildren<ModularAvatarMergeAnimator>(true))
            {
                if (self != null && merge.gameObject == self.gameObject) continue;
                if (merge.layerType != VRCAvatarDescriptor.AnimLayerType.FX) continue;
                if (!(merge.animator is AnimatorController ac)) continue;

                var prefix = "";
                if (merge.pathMode == MergeAnimatorPathMode.Relative)
                {
                    var rootObj = merge.relativePathRoot?.Get(merge);
                    var t = rootObj != null ? rootObj.transform : merge.transform;
                    prefix = AnimationUtility.CalculateTransformPath(t, desc.transform);
                }

                yield return new FxSource($"MA Merge Animator「{merge.name}」", ac, prefix);
            }
        }

        public static void CheckConflicts(GripBlink gb, VRCAvatarDescriptor desc,
            IReadOnlyList<(EyeChannel channel, List<PoseEntry> pose)> channels, List<Diagnostic> diagnostics)
        {
            if (desc == null) return;

            var ourBindings = new HashSet<EditorCurveBinding>();
            foreach (var (_, pose) in channels)
            {
                foreach (var e in pose) ourBindings.Add(e.Binding);
            }

            if (ourBindings.Count == 0) return;

            CheckEyelids(gb, desc, ourBindings, diagnostics);
            CheckFxLayers(gb, desc, channels, ourBindings, diagnostics);
        }

        private static void CheckEyelids(GripBlink gb, VRCAvatarDescriptor desc, HashSet<EditorCurveBinding> ours,
            List<Diagnostic> diagnostics)
        {
            var eye = desc.customEyeLookSettings;
            if (!desc.enableEyeLook || eye.eyelidType != VRCAvatarDescriptor.EyelidType.Blendshapes) return;
            var smr = eye.eyelidsSkinnedMesh;
            if (smr == null || smr.sharedMesh == null || eye.eyelidsBlendshapes == null) return;

            var path = AnimationUtility.CalculateTransformPath(smr.transform, desc.transform);
            var overlapped = new List<string>();
            foreach (var idx in eye.eyelidsBlendshapes)
            {
                if (idx < 0 || idx >= smr.sharedMesh.blendShapeCount) continue;
                var name = smr.sharedMesh.GetBlendShapeName(idx);
                var binding = EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + name);
                if (ours.Contains(binding) && !overlapped.Contains(name)) overlapped.Add(name);
            }

            if (overlapped.Count == 0) return;

            var names = string.Join("、", overlapped);
            if (gb.suppressAutoBlink)
            {
                diagnostics.Add(new Diagnostic(Severity.Info,
                    $"VRChat 標準の自動まばたきと同じシェイプキー（{names}）を使います。閉眼操作中は自動まばたきを停止します。"));
            }
            else
            {
                diagnostics.Add(new Diagnostic(Severity.Warning,
                    $"VRChat 標準の自動まばたきと同じシェイプキー（{names}）を使います。"
                    + "「自動まばたきを一時停止」がオフのため、操作中に自動まばたきと競合してちらつく可能性があります。"));
            }
        }

        private static void CheckFxLayers(GripBlink gb, VRCAvatarDescriptor desc,
            IReadOnlyList<(EyeChannel channel, List<PoseEntry> pose)> channels, HashSet<EditorCurveBinding> ours,
            List<Diagnostic> diagnostics)
        {
            var overlapLayers = new List<string>();
            var fistLayers = new List<string>();
            var trackingLayers = new List<string>();
            var gestureParams = new HashSet<string>(channels.Select(c => c.channel.GestureParam));

            foreach (var src in GetFxSources(desc, gb))
            {
                foreach (var layer in src.Controller.layers)
                {
                    if (layer.stateMachine == null) continue;
                    var layerName = $"{src.Name} / {layer.name}";

                    var clips = new HashSet<AnimationClip>();
                    var usesFist = false;
                    var controlsEyes = false;

                    WalkStateMachine(layer.stateMachine,
                        state =>
                        {
                            CollectClips(state.motion, clips);
                            if (state.behaviours == null) return;
                            foreach (var b in state.behaviours)
                            {
                                if (b is VRC_AnimatorTrackingControl tc
                                    && tc.trackingEyes != VRC_AnimatorTrackingControl.TrackingType.NoChange)
                                {
                                    controlsEyes = true;
                                }
                            }
                        },
                        transition =>
                        {
                            foreach (var cond in transition.conditions)
                            {
                                if (gestureParams.Contains(cond.parameter)
                                    && cond.mode == AnimatorConditionMode.Equals
                                    && Mathf.Approximately(cond.threshold, 1f))
                                {
                                    usesFist = true;
                                }
                            }
                        });

                    var overlaps = clips.Any(clip => AnimationUtility.GetCurveBindings(clip)
                        .Any(b => ours.Contains(Prefix(b, src.PathPrefix))));

                    if (overlaps)
                    {
                        if (gb.requireFist && usesFist) fistLayers.Add(layerName);
                        else overlapLayers.Add(layerName);
                    }

                    if (controlsEyes && gb.suppressAutoBlink) trackingLayers.Add(layerName);
                }
            }

            if (fistLayers.Count > 0)
            {
                diagnostics.Add(new Diagnostic(Severity.Warning,
                    "次のレイヤーは同じ手の Fist（握り）で目まわりの表情を動かします。握ると既存の表情と GripBlink が同時に発動し、"
                    + "トリガーを引いている間は GripBlink の閉眼が優先されます:\n・" + string.Join("\n・", fistLayers)));
            }

            if (overlapLayers.Count > 0)
            {
                diagnostics.Add(new Diagnostic(Severity.Info,
                    "次のレイヤーも同じシェイプキー等を動かしています。GripBlink の操作中だけ GripBlink が優先され、"
                    + "操作していないときは既存の動作のままです:\n・" + string.Join("\n・", overlapLayers)));
            }

            if (trackingLayers.Count > 0)
            {
                diagnostics.Add(new Diagnostic(Severity.Warning,
                    "次のレイヤーは VRC Animator Tracking Control で目のトラッキングを切り替えています。GripBlink を離したときに"
                    + "目のトラッキングを Tracking に戻すため、それらの設定を上書きする場合があります。"
                    + "気になる場合は「自動まばたきを一時停止」をオフにしてください:\n・" + string.Join("\n・", trackingLayers)));
            }
        }

        private static EditorCurveBinding Prefix(EditorCurveBinding b, string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return b;
            var copy = b;
            copy.path = string.IsNullOrEmpty(b.path) ? prefix : prefix + "/" + b.path;
            return copy;
        }

        public static void CollectClips(Motion motion, HashSet<AnimationClip> clips)
        {
            switch (motion)
            {
                case AnimationClip clip:
                    clips.Add(clip);
                    break;
                case BlendTree tree:
                    foreach (var child in tree.children) CollectClips(child.motion, clips);
                    break;
            }
        }

        public static void WalkStateMachine(AnimatorStateMachine sm, System.Action<AnimatorState> onState,
            System.Action<AnimatorTransitionBase> onTransition)
        {
            var visited = new HashSet<AnimatorStateMachine>();
            Walk(sm);

            void Walk(AnimatorStateMachine m)
            {
                if (m == null || !visited.Add(m)) return;
                foreach (var t in m.anyStateTransitions) onTransition?.Invoke(t);
                foreach (var t in m.entryTransitions) onTransition?.Invoke(t);
                foreach (var cs in m.states)
                {
                    if (cs.state == null) continue;
                    onState?.Invoke(cs.state);
                    foreach (var t in cs.state.transitions) onTransition?.Invoke(t);
                }

                foreach (var child in m.stateMachines)
                {
                    foreach (var t in m.GetStateMachineTransitions(child.stateMachine)) onTransition?.Invoke(t);
                    Walk(child.stateMachine);
                }
            }
        }

        /// <summary>設定全体を検査する。Inspector 表示用</summary>
        public static List<Diagnostic> Diagnose(GripBlink gb)
        {
            var diagnostics = new List<Diagnostic>();
            var desc = FindAvatar(gb);
            if (desc == null)
            {
                diagnostics.Add(new Diagnostic(Severity.Error,
                    "VRC Avatar Descriptor が親に見つかりません。アバターの子として配置してください。"));
                return diagnostics;
            }

            var all = desc.GetComponentsInChildren<GripBlink>(true);
            if (all.Length > 1)
            {
                diagnostics.Add(new Diagnostic(all[0] == gb ? Severity.Warning : Severity.Error,
                    all[0] == gb
                        ? $"このアバターには GripBlink が {all.Length} 個あります。このコンポーネントだけが使われます。不要なものは削除してください。"
                        : $"このアバターには GripBlink が {all.Length} 個あります。このコンポーネントは使われません（「{all[0].name}」が優先されます）。削除してください。"));
            }

            if (!gb.gameObject.activeInHierarchy)
            {
                diagnostics.Add(new Diagnostic(Severity.Info, "GameObject が非アクティブのため、ビルドに含まれません。"));
            }

            var analyzed = new List<(EyeChannel, List<PoseEntry>)>();
            foreach (var ch in GetChannels(gb))
            {
                if (ch.Clip == null)
                {
                    if (gb.mode == EyeControlMode.BothEyes)
                        diagnostics.Add(new Diagnostic(Severity.Error, "閉眼クリップが指定されていません。"));
                    else
                        diagnostics.Add(new Diagnostic(Severity.Warning, $"{ch.Label}のクリップが未指定です（{ch.Label}は動きません）。"));
                    continue;
                }

                var pose = ComputePose(ch.Clip, desc.gameObject, gb.autoDetectPeak, gb.sampleTime, diagnostics,
                    ch.Label);
                analyzed.Add((ch, pose));
                if (pose.Count > 0)
                {
                    var lines = pose.Take(6).Select(e => $"{Describe(e.Binding)}: {e.Rest:0.##} → {e.Closed:0.##}");
                    var more = pose.Count > 6 ? $"\n…ほか {pose.Count - 6} 件" : "";
                    diagnostics.Add(new Diagnostic(Severity.Info,
                        $"{ch.Label}（{(ch.Hand == GripHand.Left ? "左手" : "右手")}）で動かす値:\n" + string.Join("\n", lines) + more));
                }
            }

            if (gb.mode == EyeControlMode.Separate && gb.leftEyeClip != null && gb.leftEyeClip == gb.rightEyeClip)
            {
                diagnostics.Add(new Diagnostic(Severity.Warning, "左目と右目に同じクリップが指定されています。"));
            }

            CheckConflicts(gb, desc, analyzed, diagnostics);
            return diagnostics;
        }
    }
}
