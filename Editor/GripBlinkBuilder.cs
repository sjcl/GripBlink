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
    ///            「左右どちらでも」の場合は左右それぞれの Active を持ち、先に操作した手を使う。
    ///            その手を離したとき、もう一方の手が操作中ならそちらの Active に引き継ぐ。
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
                foreach (var hand in ch.Hands)
                {
                    AddParam(controller, addedParams, EyeChannel.WeightParam(hand), AnimatorControllerParameterType.Float);
                    if (gb.requireFist)
                        AddParam(controller, addedParams, EyeChannel.GestureParam(hand), AnimatorControllerParameterType.Int);
                }

                var layerName = gb.mode == EyeControlMode.BothEyes ? LayerPrefix : $"{LayerPrefix} ({ch.Label})";
                controller.AddLayer(BuildEyeLayer(gb, ch, pose, layerName, i, deadZone));
            }

            if (gb.suppressAutoBlink)
            {
                var hands = built.SelectMany(b => b.Item1.Hands).Distinct().OrderBy(h => h).ToList();
                controller.AddLayer(BuildTrackingLayer(gb, hands, deadZone));
            }

            return controller;
        }

        private static void AddParam(AnimatorController controller, HashSet<string> added, string name,
            AnimatorControllerParameterType type)
        {
            if (!added.Add(name)) return;
            controller.AddParameter(new AnimatorControllerParameter { name = name, type = type });
        }

        /// <summary>この手が操作中になる条件（AND）</summary>
        private static List<Cond> ActivateConditions(GripBlink gb, GripHand hand, float deadZone)
        {
            var list = new List<Cond>
                { new Cond(AnimatorConditionMode.Greater, deadZone, EyeChannel.WeightParam(hand)) };
            if (gb.requireFist)
                list.Add(new Cond(AnimatorConditionMode.Equals, 1f, EyeChannel.GestureParam(hand)));
            return list;
        }

        /// <summary>この手が操作をやめる条件（OR。各要素が1つの遷移になる）</summary>
        private static List<Cond> DeactivateConditions(GripBlink gb, GripHand hand, float deadZone)
        {
            var list = new List<Cond>
                { new Cond(AnimatorConditionMode.Less, deadZone + Epsilon, EyeChannel.WeightParam(hand)) };
            if (gb.requireFist)
                list.Add(new Cond(AnimatorConditionMode.NotEqual, 1f, EyeChannel.GestureParam(hand)));
            return list;
        }

        /// <summary>すべての手が操作をやめた条件。各手の「やめる条件（OR）」の直積を、それぞれ1つの遷移にする</summary>
        private static List<List<Cond>> AllDeactivatedCombos(GripBlink gb, IEnumerable<GripHand> hands, float deadZone)
        {
            IEnumerable<List<Cond>> combos = new[] { new List<Cond>() };
            foreach (var hand in hands)
            {
                var options = DeactivateConditions(gb, hand, deadZone);
                combos = combos.SelectMany(prev => options.Select(o => new List<Cond>(prev) { o })).ToList();
            }

            return combos.ToList();
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

            var fade = Mathf.Max(0f, gb.fadeDuration);
            var sm = NewStateMachine(layerName);

            var init = sm.AddState("Init", new Vector3(240, -120));
            init.writeDefaultValues = false;
            init.behaviours = new StateMachineBehaviour[] { MakeLayerControl(layerIndex, 0f, 0f) };

            var idle = sm.AddState("Idle", new Vector3(240, 0));
            idle.writeDefaultValues = false;
            idle.behaviours = new StateMachineBehaviour[] { MakeLayerControl(layerIndex, 0f, fade) };

            sm.defaultState = init;

            // 手ごとに Active ステートを作る。「左右どちらでも」なら左右2つ
            var hands = ch.Hands;
            var actives = new List<(GripHand hand, AnimatorState state)>();
            for (var i = 0; i < hands.Count; i++)
            {
                var hand = hands[i];
                var stateName = hands.Count == 1 ? "Active" : $"Active ({EyeChannel.HandLabel(hand)})";
                var tree = new BlendTree
                {
                    name = $"{layerName} Blend {hand}",
                    blendType = BlendTreeType.Simple1D,
                    blendParameter = EyeChannel.WeightParam(hand),
                    useAutomaticThresholds = false,
                    hideFlags = HideFlags.HideInHierarchy,
                };
                tree.children = new[]
                {
                    new ChildMotion { motion = openClip, threshold = deadZone, timeScale = 1f },
                    new ChildMotion { motion = closedClip, threshold = closedThreshold, timeScale = 1f },
                };

                var active = sm.AddState(stateName, new Vector3(120 + 240 * i, 120));
                active.writeDefaultValues = false;
                active.motion = tree;
                active.behaviours = new StateMachineBehaviour[] { MakeLayerControl(layerIndex, 1f, fade) };
                actives.Add((hand, active));
            }

            // 待機中 → 操作を始めた手の Active（両手同時なら左手が先）
            foreach (var (hand, active) in actives)
            {
                var activate = ActivateConditions(gb, hand, deadZone);
                AddTransition(init, active, activate);
                AddTransition(idle, active, activate);
            }

            foreach (var (hand, active) in actives)
            {
                // 操作中の手を離したとき、もう一方の手が操作中ならそちらへ引き継ぐ（Idle への遷移より先に評価される）
                foreach (var (otherHand, other) in actives)
                {
                    if (other == active) continue;
                    foreach (var release in DeactivateConditions(gb, hand, deadZone))
                    {
                        var conds = new List<Cond> { release };
                        conds.AddRange(ActivateConditions(gb, otherHand, deadZone));
                        AddTransition(active, other, conds, fade);
                    }
                }

                foreach (var release in DeactivateConditions(gb, hand, deadZone))
                    AddTransition(active, idle, new[] { release });
            }

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
        /// いずれかの手が操作中なら Animation、すべての手が操作をやめたら Tracking に戻す。
        /// 初期状態では何も変更しない（既存の Tracking Control を読み込み時に上書きしない）。
        /// </summary>
        private static AnimatorControllerLayer BuildTrackingLayer(GripBlink gb, List<GripHand> hands, float deadZone)
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

            // いずれかの手が操作中 → Active（手ごとに1遷移 = OR）
            foreach (var hand in hands)
            {
                var activate = ActivateConditions(gb, hand, deadZone);
                AddTransition(init, active, activate);
                AddTransition(idle, active, activate);
            }

            // すべての手が操作をやめた → Idle
            foreach (var combo in AllDeactivatedCombos(gb, hands, deadZone)) AddTransition(active, idle, combo);

            return new AnimatorControllerLayer
            {
                name = name,
                stateMachine = sm,
                // モーションを持たないので値には影響しない。ステートマシンビヘイビアだけが目的
                defaultWeight = 1f,
                blendingMode = AnimatorLayerBlendingMode.Override,
            };
        }

        private static void AddTransition(AnimatorState from, AnimatorState to, IEnumerable<Cond> conditions,
            float duration = 0f)
        {
            var t = from.AddTransition(to);
            t.hasExitTime = false;
            t.exitTime = 0f;
            t.hasFixedDuration = true;
            t.duration = duration;
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
