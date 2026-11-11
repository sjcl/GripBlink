using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Sjcl.GripBlink.Editor
{
    /// <summary>
    /// GripBlink の設定から、FX にマージする AnimatorController をメモリ上に生成する。
    /// 既存のクリップやコントローラーは読むだけで変更しない。
    ///
    /// 各チャンネル（両目 / 左目 / 右目）ごとに1レイヤー:
    ///   Init   : 初期状態。レイヤーウェイトを即座に 0 にする
    ///   Idle   : モーションなし、レイヤーウェイト → 0（既存の表情に一切影響しない）
    ///   Active : GestureXWeight で「開いたポーズ」と「閉じたポーズ」をブレンド、レイヤーウェイト → 1
    /// 自動まばたきの一時停止が有効なら、さらに目のトラッキング切り替え専用レイヤーを1つ追加する
    /// （いずれかのチャンネルが Active の間は Animation、すべて Idle になったら Tracking）。
    /// </summary>
    internal static class GripBlinkBuilder
    {
        public const string LayerPrefix = "GripBlink";
        private const float Epsilon = 0.0001f;

        private readonly struct Cond
        {
            public readonly AnimatorConditionMode Mode;
            public readonly float Threshold;
            public readonly string Param;

            public Cond(AnimatorConditionMode mode, float threshold, string param)
            {
                Mode = mode;
                Threshold = threshold;
                Param = param;
            }
        }

        public static AnimatorController Build(GripBlink gb, GameObject avatarRoot, List<Diagnostic> diagnostics)
        {
            var built = new List<(EyeChannel, List<PoseEntry>)>();
            foreach (var ch in GripBlinkAnalyzer.GetChannels(gb))
            {
                if (ch.Clip == null) continue;
                var pose = GripBlinkAnalyzer.ComputePose(ch.Clip, avatarRoot, gb.autoDetectPeak, gb.sampleTime,
                    diagnostics, ch.Label);
                if (pose.Count > 0) built.Add((ch, pose));
            }

            if (built.Count == 0) return null;

            var controller = new AnimatorController { name = "GripBlink (generated)" };
            var addedParams = new HashSet<string>();
            var deadZone = Mathf.Clamp(gb.deadZone, 0f, 0.9f);

            for (var i = 0; i < built.Count; i++)
            {
                var (ch, pose) = built[i];
                AddParam(controller, addedParams, ch.WeightParam, AnimatorControllerParameterType.Float);
                if (gb.requireFist) AddParam(controller, addedParams, ch.GestureParam, AnimatorControllerParameterType.Int);

                var layerName = gb.mode == EyeControlMode.BothEyes ? LayerPrefix : $"{LayerPrefix} ({ch.Label})";
                controller.AddLayer(BuildEyeLayer(gb, ch, pose, layerName, i, deadZone));
            }

            if (gb.suppressAutoBlink)
            {
                controller.AddLayer(BuildTrackingLayer(gb, built.Select(b => b.Item1).ToList(), deadZone));
            }

            return controller;
        }

        private static void AddParam(AnimatorController controller, HashSet<string> added, string name,
            AnimatorControllerParameterType type)
        {
            if (!added.Add(name)) return;
            controller.AddParameter(new AnimatorControllerParameter { name = name, type = type });
        }

        /// <summary>このチャンネルが操作中になる条件（AND）</summary>
        private static List<Cond> ActivateConditions(GripBlink gb, EyeChannel ch, float deadZone)
        {
            var list = new List<Cond> { new Cond(AnimatorConditionMode.Greater, deadZone, ch.WeightParam) };
            if (gb.requireFist) list.Add(new Cond(AnimatorConditionMode.Equals, 1f, ch.GestureParam));
            return list;
        }

        /// <summary>このチャンネルが操作をやめる条件（OR。各要素が1つの遷移になる）</summary>
        private static List<Cond> DeactivateConditions(GripBlink gb, EyeChannel ch, float deadZone)
        {
            var list = new List<Cond> { new Cond(AnimatorConditionMode.Less, deadZone + Epsilon, ch.WeightParam) };
            if (gb.requireFist) list.Add(new Cond(AnimatorConditionMode.NotEqual, 1f, ch.GestureParam));
            return list;
        }

        private static AnimatorStateMachine NewStateMachine(string name)
        {
            return new AnimatorStateMachine
            {
                name = name,
                hideFlags = HideFlags.HideInHierarchy,
                entryPosition = new Vector3(0, 0),
                anyStatePosition = new Vector3(0, 60),
                exitPosition = new Vector3(0, 120),
            };
        }

        private static AnimatorControllerLayer BuildEyeLayer(GripBlink gb, EyeChannel ch, List<PoseEntry> pose,
            string layerName, int layerIndex, float deadZone)
        {
            var clipName = ch.Clip.name;
            var openClip = MakeConstantClip($"GripBlink_Open_{clipName}", pose, false);
            var closedClip = MakeConstantClip($"GripBlink_Closed_{clipName}", pose, true);

            // 入力 w に対する閉眼率: (w - deadZone) / (1 - deadZone) * maxClosure
            // → 閉じたポーズのしきい値を deadZone + (1 - deadZone) / maxClosure に置く
            var maxClosure = Mathf.Clamp(gb.maxClosure, 0.05f, 1f);
            var closedThreshold = deadZone + (1f - deadZone) / maxClosure;

            var tree = new BlendTree
            {
                name = $"{layerName} Blend",
                blendType = BlendTreeType.Simple1D,
                blendParameter = ch.WeightParam,
                useAutomaticThresholds = false,
                hideFlags = HideFlags.HideInHierarchy,
            };
            tree.children = new[]
            {
                new ChildMotion { motion = openClip, threshold = deadZone, timeScale = 1f },
                new ChildMotion { motion = closedClip, threshold = closedThreshold, timeScale = 1f },
            };

            var fade = Mathf.Max(0f, gb.fadeDuration);
            var sm = NewStateMachine(layerName);

            var init = sm.AddState("Init", new Vector3(240, -120));
            init.writeDefaultValues = false;
            init.behaviours = new StateMachineBehaviour[] { MakeLayerControl(layerIndex, 0f, 0f) };

            var idle = sm.AddState("Idle", new Vector3(240, 0));
            idle.writeDefaultValues = false;
            idle.behaviours = new StateMachineBehaviour[] { MakeLayerControl(layerIndex, 0f, fade) };

            var active = sm.AddState("Active", new Vector3(240, 120));
            active.writeDefaultValues = false;
            active.motion = tree;
            active.behaviours = new StateMachineBehaviour[] { MakeLayerControl(layerIndex, 1f, fade) };

            sm.defaultState = init;

            var activate = ActivateConditions(gb, ch, deadZone);
            AddTransition(init, active, activate);
            AddTransition(idle, active, activate);
            foreach (var c in DeactivateConditions(gb, ch, deadZone)) AddTransition(active, idle, new[] { c });

            return new AnimatorControllerLayer
            {
                name = layerName,
                stateMachine = sm,
                // ここで 0 にしてもマージ時に変わることがあるため、実際には Init の Layer Control が即座に 0 にする
                defaultWeight = 0f,
                blendingMode = AnimatorLayerBlendingMode.Override,
            };
        }

        /// <summary>
        /// 目のトラッキング切り替え専用レイヤー。アニメーションは持たない。
        /// いずれかのチャンネルが操作中なら Animation、すべて操作をやめたら Tracking に戻す。
        /// 初期状態では何も変更しない（既存の Tracking Control を読み込み時に上書きしない）。
        /// </summary>
        private static AnimatorControllerLayer BuildTrackingLayer(GripBlink gb, List<EyeChannel> channels,
            float deadZone)
        {
            var name = $"{LayerPrefix} (Eye Tracking)";
            var sm = NewStateMachine(name);

            var init = sm.AddState("Init", new Vector3(240, -120));
            init.writeDefaultValues = false;

            var idle = sm.AddState("Idle", new Vector3(240, 0));
            idle.writeDefaultValues = false;
            idle.behaviours = new StateMachineBehaviour[]
                { MakeTrackingControl(VRC_AnimatorTrackingControl.TrackingType.Tracking) };

            var active = sm.AddState("Active", new Vector3(240, 120));
            active.writeDefaultValues = false;
            active.behaviours = new StateMachineBehaviour[]
                { MakeTrackingControl(VRC_AnimatorTrackingControl.TrackingType.Animation) };

            sm.defaultState = init;

            // いずれかが操作中 → Active（チャンネルごとに1遷移 = OR）
            foreach (var ch in channels)
            {
                var activate = ActivateConditions(gb, ch, deadZone);
                AddTransition(init, active, activate);
                AddTransition(idle, active, activate);
            }

            // すべてが操作をやめた → Idle。各チャンネルの「やめる条件（OR）」の直積を遷移にする
            IEnumerable<List<Cond>> combos = new[] { new List<Cond>() };
            foreach (var ch in channels)
            {
                var options = DeactivateConditions(gb, ch, deadZone);
                combos = combos.SelectMany(prev => options.Select(o => new List<Cond>(prev) { o })).ToList();
            }

            foreach (var combo in combos) AddTransition(active, idle, combo);

            return new AnimatorControllerLayer
            {
                name = name,
                stateMachine = sm,
                // モーションを持たないので値には影響しない。ステートマシンビヘイビアだけが目的
                defaultWeight = 1f,
                blendingMode = AnimatorLayerBlendingMode.Override,
            };
        }

        private static void AddTransition(AnimatorState from, AnimatorState to, IEnumerable<Cond> conditions)
        {
            var t = from.AddTransition(to);
            t.hasExitTime = false;
            t.exitTime = 0f;
            t.hasFixedDuration = true;
            t.duration = 0f;
            t.offset = 0f;
            t.canTransitionToSelf = false;
            t.interruptionSource = TransitionInterruptionSource.None;
            foreach (var c in conditions) t.AddCondition(c.Mode, c.Threshold, c.Param);
        }

        private static VRCAnimatorLayerControl MakeLayerControl(int layerIndex, float goalWeight, float duration)
        {
            var layerControl = ScriptableObject.CreateInstance<VRCAnimatorLayerControl>();
            layerControl.hideFlags = HideFlags.HideInHierarchy;
            // Merge Animator は同一コントローラー内のレイヤー番号をマージ後の番号に補正する
            layerControl.playable = VRC_AnimatorLayerControl.BlendableLayer.FX;
            layerControl.layer = layerIndex;
            layerControl.goalWeight = goalWeight;
            layerControl.blendDuration = duration;
            layerControl.debugString = "GripBlink";
            return layerControl;
        }

        private static VRCAnimatorTrackingControl MakeTrackingControl(VRC_AnimatorTrackingControl.TrackingType eyes)
        {
            const VRC_AnimatorTrackingControl.TrackingType keep = VRC_AnimatorTrackingControl.TrackingType.NoChange;
            var tracking = ScriptableObject.CreateInstance<VRCAnimatorTrackingControl>();
            tracking.hideFlags = HideFlags.HideInHierarchy;
            tracking.trackingHead = keep;
            tracking.trackingLeftHand = keep;
            tracking.trackingRightHand = keep;
            tracking.trackingHip = keep;
            tracking.trackingLeftFoot = keep;
            tracking.trackingRightFoot = keep;
            tracking.trackingLeftFingers = keep;
            tracking.trackingRightFingers = keep;
            tracking.trackingMouth = keep;
            tracking.trackingEyes = eyes;
            tracking.debugString = "GripBlink";
            return tracking;
        }

        private static AnimationClip MakeConstantClip(string name, List<PoseEntry> pose, bool closed)
        {
            var clip = new AnimationClip { name = name };
            foreach (var e in pose)
            {
                var v = closed ? e.Closed : e.Rest;
                var curve = new AnimationCurve(new Keyframe(0f, v), new Keyframe(1f / 60f, v));
                AnimationUtility.SetEditorCurve(clip, e.Binding, curve);
            }

            return clip;
        }
    }
}
