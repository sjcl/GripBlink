using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Sjcl.GripBlink.Editor
{
    internal enum EyeSide
    {
        Both,
        Left,
        Right,
    }

    /// <summary>
    /// アバターの FX や近くのフォルダから blink 系クリップを探す。
    /// 検出では何も作らない。シェイプキーからのクリップ作成（CreateClipFromShape）は Inspector のボタン操作時だけ行う。
    /// </summary>
    internal static class ClipDetector
    {
        public struct Result
        {
            public AnimationClip Both;
            public AnimationClip Left;
            public AnimationClip Right;

            public bool Any => Both != null || Left != null || Right != null;
        }

        private static readonly Regex EyeKeyword = new Regex(
            @"blink|wink|まばたき|瞬き|ウィンク|ｳｨﾝｸ|目閉じ|目とじ|閉眼|eye_?close|close_?eye|eyes_?close|close_?eyes",
            RegexOptions.IgnoreCase);

        private static readonly Regex LeftMarker = new Regex(@"(?<![a-z])l(?![a-z])|left|左", RegexOptions.IgnoreCase);
        private static readonly Regex RightMarker = new Regex(@"(?<![a-z])r(?![a-z])|right|右", RegexOptions.IgnoreCase);
        private static readonly Regex WinkOnly = new Regex(@"wink|ウィンク|ｳｨﾝｸ", RegexOptions.IgnoreCase);
        private static readonly Regex Negative = new Regex(@"open|開|half|半", RegexOptions.IgnoreCase);

        public static EyeSide? Classify(string name)
        {
            if (string.IsNullOrEmpty(name) || !EyeKeyword.IsMatch(name) || Negative.IsMatch(name)) return null;
            var left = LeftMarker.IsMatch(name);
            var right = RightMarker.IsMatch(name);
            if (left && !right) return EyeSide.Left;
            if (right && !left) return EyeSide.Right;
            if (left) return null; // 両方含むものは判断しない
            // 左右の指定がない wink は片目か判断できないので除外
            return WinkOnly.IsMatch(name) ? (EyeSide?)null : EyeSide.Both;
        }

        public static Result Detect(VRCAvatarDescriptor desc)
        {
            var fxClips = new HashSet<AnimationClip>();
            var folders = new HashSet<string>();

            foreach (var controller in FxControllers(desc))
            {
                foreach (var layer in controller.layers)
                {
                    GripBlinkAnalyzer.WalkStateMachine(layer.stateMachine,
                        s => GripBlinkAnalyzer.CollectClips(s.motion, fxClips), null);
                }

                AddFolder(folders, AssetDatabase.GetAssetPath(controller));
            }

            AddFolder(folders, PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(desc.gameObject));

            var candidates = new Dictionary<AnimationClip, bool>(); // clip → FX で使われているか
            foreach (var clip in fxClips)
            {
                if (clip != null && Classify(clip.name) != null) candidates[clip] = true;
            }

            if (folders.Count > 0)
            {
                foreach (var guid in AssetDatabase.FindAssets("t:AnimationClip", folders.ToArray()))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (Classify(Path.GetFileNameWithoutExtension(path)) == null) continue;
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                    if (clip != null && !candidates.ContainsKey(clip)) candidates[clip] = false;
                }
            }

            var best = new Dictionary<EyeSide, (AnimationClip clip, float score)>();
            foreach (var kv in candidates)
            {
                var side = Classify(kv.Key.name);
                if (side == null) continue;

                var pose = GripBlinkAnalyzer.ComputePose(kv.Key, desc.gameObject, true, 0f, null, "");
                if (pose.Count == 0) continue;

                // 動かす値が多いクリップは表情全体を含んでいる可能性が高いので、値の数に応じて減点する
                // （目の値かどうかは判定していない）。FX で実際に使われているものは加点する
                var score = 100f + (kv.Value ? 50f : 0f) - pose.Count * 3f - kv.Key.name.Length * 0.1f;
                if (!best.TryGetValue(side.Value, out var cur) || score > cur.score) best[side.Value] = (kv.Key, score);
            }

            return new Result
            {
                Both = best.TryGetValue(EyeSide.Both, out var b) ? b.clip : null,
                Left = best.TryGetValue(EyeSide.Left, out var l) ? l.clip : null,
                Right = best.TryGetValue(EyeSide.Right, out var r) ? r.clip : null,
            };
        }

        private static IEnumerable<AnimatorController> FxControllers(VRCAvatarDescriptor desc)
        {
            if (desc.customizeAnimationLayers && desc.baseAnimationLayers != null)
            {
                foreach (var layer in desc.baseAnimationLayers)
                {
                    if (layer.type == VRCAvatarDescriptor.AnimLayerType.FX && !layer.isDefault
                                                                            && layer.animatorController is AnimatorController ac)
                        yield return ac;
                }
            }

            foreach (var merge in desc.GetComponentsInChildren<ModularAvatarMergeAnimator>(true))
            {
                if (merge.layerType == VRCAvatarDescriptor.AnimLayerType.FX
                    && merge.pathMode == MergeAnimatorPathMode.Absolute
                    && merge.animator is AnimatorController ac)
                    yield return ac;
            }
        }

        private static void AddFolder(HashSet<string> folders, string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets/")) return;
            var dir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            // 1つ上の階層まで（Assets 直下は広すぎるので除外）
            var parent = Path.GetDirectoryName(dir)?.Replace('\\', '/');
            var target = !string.IsNullOrEmpty(parent) && parent != "Assets" ? parent : dir;
            if (!string.IsNullOrEmpty(target) && target != "Assets" && AssetDatabase.IsValidFolder(target))
                folders.Add(target);
        }

        public struct ShapeCandidates
        {
            public SkinnedMeshRenderer Renderer;
            public string Both;
            public string Left;
            public string Right;

            public bool Any => Renderer != null && (Both != null || Left != null || Right != null);
        }

        public static ShapeCandidates FindShapes(VRCAvatarDescriptor desc)
        {
            var result = new ShapeCandidates();
            var eye = desc.customEyeLookSettings;
            var smr = eye.eyelidsSkinnedMesh;
            if (smr == null && desc.VisemeSkinnedMesh != null) smr = desc.VisemeSkinnedMesh;
            if (smr == null || smr.sharedMesh == null) return result;

            result.Renderer = smr;
            var mesh = smr.sharedMesh;

            if (eye.eyelidType == VRCAvatarDescriptor.EyelidType.Blendshapes && eye.eyelidsSkinnedMesh == smr
                                                                              && eye.eyelidsBlendshapes != null
                                                                              && eye.eyelidsBlendshapes.Length > 0)
            {
                var idx = eye.eyelidsBlendshapes[0];
                if (idx >= 0 && idx < mesh.blendShapeCount) result.Both = mesh.GetBlendShapeName(idx);
            }

            for (var i = 0; i < mesh.blendShapeCount; i++)
            {
                var name = mesh.GetBlendShapeName(i);
                switch (Classify(name))
                {
                    case EyeSide.Both:
                        if (result.Both == null) result.Both = name;
                        break;
                    case EyeSide.Left:
                        if (result.Left == null) result.Left = name;
                        break;
                    case EyeSide.Right:
                        if (result.Right == null) result.Right = name;
                        break;
                }
            }

            return result;
        }

        public static AnimationClip CreateClipFromShape(VRCAvatarDescriptor desc, SkinnedMeshRenderer smr,
            string shapeName, string suffix)
        {
            const string root = "Assets/GripBlink";
            const string folder = root + "/Generated";
            if (!AssetDatabase.IsValidFolder(root)) AssetDatabase.CreateFolder("Assets", "GripBlink");
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(root, "Generated");

            var safeName = string.Join("_", desc.gameObject.name.Split(Path.GetInvalidFileNameChars()));
            var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{safeName}_{suffix}.anim");

            var clip = new AnimationClip { name = Path.GetFileNameWithoutExtension(path) };
            var binding = EditorCurveBinding.FloatCurve(
                AnimationUtility.CalculateTransformPath(smr.transform, desc.transform),
                typeof(SkinnedMeshRenderer), "blendShape." + shapeName);
            AnimationUtility.SetEditorCurve(clip, binding,
                new AnimationCurve(new Keyframe(0f, 100f), new Keyframe(1f / 60f, 100f)));

            AssetDatabase.CreateAsset(clip, path);
            AssetDatabase.SaveAssets();
            return clip;
        }
    }
}
