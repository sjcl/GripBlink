using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using NUnit.Framework;
using Sjcl.GripBlink.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Sjcl.GripBlink.Tests.Editor
{
    public class GripBlinkTests
    {
        private const string TempFolder = "Assets/__GripBlinkTests";
        private const string PrefabGuid = "045d27eb49134dd2a17c15cae97b7448";
        private const string TrackingLayerName = "GripBlink (Eye Tracking)";

        private readonly List<GameObject> _objects = new List<GameObject>();
        private Func<string, string, string, string, bool> _originalDialog;
        private readonly List<string> _dialogs = new List<string>();
        private bool _dialogAnswer;

        private sealed class TestAvatar
        {
            public GameObject Root;
            public VRCAvatarDescriptor Descriptor;
            public AnimationClip Blink, WinkL, WinkR;
            public string FxPath;
        }

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.CreateFolder("Assets", "__GripBlinkTests");
            _originalDialog = GripBlinkInstaller.ShowDialog;
            _dialogs.Clear();
            _dialogAnswer = false;
            GripBlinkInstaller.ShowDialog = (title, message, ok, cancel) =>
            {
                _dialogs.Add(message);
                return _dialogAnswer;
            };
        }

        [TearDown]
        public void TearDown()
        {
            GripBlinkInstaller.ShowDialog = _originalDialog;
            Selection.activeObject = null;
            foreach (var go in _objects)
            {
                if (go != null) Object.DestroyImmediate(go);
            }

            _objects.Clear();
            AvatarProcessor.CleanTemporaryAssets();
            AssetDatabase.DeleteAsset(TempFolder);
        }

        // ------------------------------------------------------------------
        // クリップ検出・初期値・診断
        // ------------------------------------------------------------------

        [TestCase("Blink", "Both")]
        [TestCase("eye_close", "Both")]
        [TestCase("blink_L", "Left")]
        [TestCase("Wink_Right", "Right")]
        [TestCase("まばたき左", "Left")]
        [TestCase("eye_close_R", "Right")]
        public void Classify_RecognizesBlinkNames(string name, string expected)
        {
            // EyeSide は internal なので、public なテストメソッドの引数には文字列で渡す
            Assert.That(ClipDetector.Classify(name)?.ToString(), Is.EqualTo(expected));
        }

        [TestCase("wink")] // 左右の指定がない wink は片目か判断できない
        [TestCase("mouth_close")]
        [TestCase("blink_open")]
        public void Classify_RejectsOtherNames(string name)
        {
            Assert.That(ClipDetector.Classify(name), Is.Null);
        }

        [Test]
        public void Prefab_DefaultsToEitherHand()
        {
            var gb = LoadPrefab().GetComponent<GripBlink>();
            Assert.That(gb, Is.Not.Null);
            Assert.That(gb.hand, Is.EqualTo(GripHand.Either));
            Assert.That(gb.leftEyeHand, Is.EqualTo(GripHand.Left));
            Assert.That(gb.rightEyeHand, Is.EqualTo(GripHand.Right));
        }

        [Test]
        public void AutoAssign_PicksBlinkClipUsedInFx()
        {
            var avatar = MakeAvatar("AutoAssign");
            var gb = AddPrefab(avatar);

            Assert.That(GripBlinkInstaller.AutoAssign(gb, avatar.Descriptor), Is.True);
            Assert.That(gb.mode, Is.EqualTo(EyeControlMode.BothEyes));
            Assert.That(gb.blinkClip, Is.EqualTo(avatar.Blink));
        }

        [Test]
        public void AutoAssign_KeepsManualSettings()
        {
            var avatar = MakeAvatar("AutoAssignManual");
            var gb = AddPrefab(avatar);
            gb.leftEyeClip = avatar.WinkL;

            Assert.That(GripBlinkInstaller.AutoAssign(gb, avatar.Descriptor), Is.False);
            Assert.That(gb.blinkClip, Is.Null);
        }

        [Test]
        public void Diagnose_ReportsAutoBlinkAndSameHandFist()
        {
            var avatar = MakeAvatar("Diagnose");
            var gb = AddPrefab(avatar);
            gb.blinkClip = avatar.Blink;
            gb.hand = GripHand.Left;

            var diagnostics = GripBlinkAnalyzer.Diagnose(gb);
            Assert.That(diagnostics.Any(d => d.Severity == Severity.Info && d.Text.Contains("自動まばたき")),
                "自動まばたきと同じシェイプキーを使うことが表示される");
            Assert.That(diagnostics.Any(d => d.Severity == Severity.Warning && d.Text.Contains("Fist")),
                "同じ手の Fist で目を動かす既存レイヤーが警告される");
        }

        // ------------------------------------------------------------------
        // ビルド結果
        // ------------------------------------------------------------------

        [Test]
        public void Build_SingleHand()
        {
            var avatar = MakeAvatar("SingleHand");
            var gb = AddPrefab(avatar);
            gb.blinkClip = avatar.Blink;
            gb.hand = GripHand.Left;
            gb.maxClosure = 0.8f;

            var built = Build(avatar);
            VerifyBuild(built, avatar, eyeLayers: 1, activesPerLayer: 1, maxClosure: 0.8f, deadZone: 0f, trackingHands: 1);

            var animator = PrepareAnimator(built);
            var li = animator.GetLayerIndex("GripBlink");
            // Fist の間はトリガーが 0 でも Active（開いたポーズ）のまま
            foreach (var (gesture, weight, active) in new[] { (1, 1f, true), (1, 0.5f, true), (1, 0f, true), (2, 1f, false) })
            {
                SetHand(animator, "Left", gesture, weight);
                Step(animator, li, 2);
                var state = animator.GetCurrentAnimatorStateInfo(li);
                Assert.That(state.IsName(active ? "Active" : "Idle"), Is.True, $"gesture={gesture} weight={weight}");
                if (active)
                {
                    var expected = 100f * weight * 0.8f;
                    Assert.That(EyeClose(built), Is.EqualTo(expected).Within(1.5f), $"gesture={gesture} weight={weight}");
                }
            }
        }

        [Test]
        public void Build_EitherHand_HandsOffToOtherHand()
        {
            var avatar = MakeAvatar("EitherHand");
            var gb = AddPrefab(avatar);
            gb.blinkClip = avatar.Blink; // 手は初期値（左右どちらでも）

            var built = Build(avatar);
            VerifyBuild(built, avatar, eyeLayers: 1, activesPerLayer: 2, maxClosure: 1f, deadZone: 0f, trackingHands: 2);

            var animator = PrepareAnimator(built);
            var li = animator.GetLayerIndex("GripBlink");
            var steps = new (int gL, float wL, int gR, float wR, string state, float value, string why)[]
            {
                (0, 0f, 1, 0.5f, "Active (右手)", 50f, "右手だけで操作できる"),
                (1, 1f, 1, 0.5f, "Active (右手)", 50f, "後から握った左手より右手が優先"),
                (1, 1f, 0, 0f, "Active (左手)", 100f, "右手を離すと左手に引き継ぐ"),
                (1, 0.3f, 0, 0f, "Active (左手)", 30f, "引き継いだ左手の量に追従する"),
                (0, 0f, 0, 0f, "Idle", 0f, "両手を離すと Idle"),
                (1, 1f, 0, 0f, "Active (左手)", 100f, "左手だけで操作できる"),
                (1, 0f, 0, 0f, "Active (左手)", 0f, "握っているだけなら開いたポーズのまま"),
                (1, 0f, 1, 0.6f, "Active (右手)", 60f, "左手が握っているだけなら右手で操作できる"),
                (1, 0f, 0, 0f, "Active (左手)", 0f, "右手が Fist をやめても、左手が握っていれば開いたポーズを保つ"),
                (0, 0f, 0, 0f, "Idle", 0f, "両手を離すと Idle"),
            };
            foreach (var s in steps)
            {
                SetHand(animator, "Left", s.gL, s.wL);
                SetHand(animator, "Right", s.gR, s.wR);
                Step(animator, li, 6);
                Assert.That(animator.GetCurrentAnimatorStateInfo(li).IsName(s.state), Is.True, s.why);
                Assert.That(EyeClose(built), Is.EqualTo(s.value).Within(1.5f), s.why);
            }
        }

        [Test]
        public void Build_FistWithoutTrigger_KeepsEyesOpenOverAvatarFistFace()
        {
            var avatar = MakeAvatar("FistFace");
            AddFistFaceLayer(avatar);
            var gb = AddPrefab(avatar);
            gb.blinkClip = avatar.Blink;
            gb.hand = GripHand.Left;

            var built = Build(avatar);
            var animator = PrepareAnimator(built);
            var li = animator.GetLayerIndex("GripBlink");
            foreach (var (gesture, weight, expected, why) in new[]
                     {
                         (1, 1f, 100f, "トリガーを引くと閉じる"),
                         (1, 0.5f, 50f, "トリガー量に追従する"),
                         (1, 0f, 0f, "トリガーを戻すと、アバターの Fist の表情より GripBlink の開いたポーズが優先される"),
                     })
            {
                SetHand(animator, "Left", gesture, weight);
                StepWithLayerControl(animator, li, 6);
                Assert.That(EyeClose(built), Is.EqualTo(expected).Within(1.5f), why);
            }
        }

        [Test]
        public void Build_SeparateEyes()
        {
            var avatar = MakeAvatar("Separate");
            var gb = AddPrefab(avatar);
            gb.mode = EyeControlMode.Separate;
            gb.leftEyeClip = avatar.WinkL;
            gb.rightEyeClip = avatar.WinkR;
            gb.deadZone = 0.1f;

            var built = Build(avatar);
            VerifyBuild(built, avatar, eyeLayers: 2, activesPerLayer: 1, maxClosure: 1f, deadZone: 0.1f, trackingHands: 2);
        }

        [Test]
        public void Build_WithoutSuppressAutoBlink_HasNoTrackingLayer()
        {
            var avatar = MakeAvatar("NoTracking");
            var gb = AddPrefab(avatar);
            gb.blinkClip = avatar.Blink;
            gb.suppressAutoBlink = false;

            var fx = Fx(Build(avatar));
            Assert.That(fx.layers.Any(l => l.name == TrackingLayerName), Is.False);
            Assert.That(fx.layers.Count(l => l.name.StartsWith("GripBlink")), Is.EqualTo(1));
        }

        // ------------------------------------------------------------------
        // インストーラー
        // ------------------------------------------------------------------

        [Test]
        public void MenuAdd_AddsUnderAvatarRoot_AndOneUndoRemovesIt()
        {
            var avatar = MakeAvatar("MenuAdd");
            Selection.activeGameObject = avatar.Root.transform.Find("Body").gameObject; // アバターの子を選択
            GripBlinkInstaller.AddToSelection();

            var found = avatar.Root.GetComponentsInChildren<GripBlink>(true);
            Assert.That(found.Length, Is.EqualTo(1));
            Assert.That(found[0].transform.parent, Is.EqualTo(avatar.Root.transform));
            Assert.That(PrefabUtility.IsPartOfPrefabInstance(found[0]), Is.True);
            Assert.That(found[0].blinkClip, Is.EqualTo(avatar.Blink), "クリップが自動設定される");

            Undo.PerformUndo();
            Assert.That(avatar.Root.GetComponentsInChildren<GripBlink>(true).Length, Is.EqualTo(0));
            Undo.PerformRedo();
            Assert.That(avatar.Root.GetComponentsInChildren<GripBlink>(true).Length, Is.EqualTo(1));
        }

        [Test]
        public void MenuAdd_SkipsAvatarThatAlreadyHasOne()
        {
            var avatar = MakeAvatar("MenuAddTwice");
            Selection.activeGameObject = avatar.Root;
            GripBlinkInstaller.AddToSelection();
            Selection.activeGameObject = avatar.Root;
            GripBlinkInstaller.AddToSelection();

            Assert.That(avatar.Root.GetComponentsInChildren<GripBlink>(true).Length, Is.EqualTo(1));
            Assert.That(_dialogs.Count, Is.EqualTo(1), "追加しなかったことを知らせる");
        }

        [Test]
        public void MenuAdd_DisabledOutsideAvatar()
        {
            var outside = Track((GameObject)PrefabUtility.InstantiatePrefab(LoadPrefab()));
            Selection.activeGameObject = outside;
            Assert.That(GripBlinkInstaller.ValidateAdd(), Is.False);
        }

        [TestCase(false, 2, TestName = "Drop_Duplicate_KeepWhenDeclined")]
        [TestCase(true, 1, TestName = "Drop_Duplicate_RemoveWhenAccepted")]
        public void Drop_Duplicate(bool remove, int expectedCount)
        {
            var avatar = MakeAvatar("Drop");
            var first = AddPrefab(avatar);
            var dropped = AddPrefab(avatar);
            _dialogAnswer = remove;

            GripBlinkInstaller.CheckDropped(dropped);

            Assert.That(_dialogs.Count, Is.EqualTo(1), "重複を確認する");
            Assert.That(avatar.Root.GetComponentsInChildren<GripBlink>(true).Length, Is.EqualTo(expectedCount));
            if (remove)
            {
                Assert.That(Selection.activeGameObject, Is.EqualTo(first.gameObject), "既存のものを選択する");
            }
            else
            {
                Assert.That(dropped.blinkClip, Is.Null, "重複したものにはクリップを自動設定しない");
            }
        }

        // ------------------------------------------------------------------
        // ヘルパー
        // ------------------------------------------------------------------

        private GameObject Track(GameObject go)
        {
            _objects.Add(go);
            return go;
        }

        private static GameObject LoadPrefab()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(PrefabGuid));
            Assert.That(prefab, Is.Not.Null, "GripBlink.prefab");
            return prefab;
        }

        private static GripBlink AddPrefab(TestAvatar avatar)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(LoadPrefab(), avatar.Root.transform);
            return go.GetComponent<GripBlink>();
        }

        /// <summary>
        /// eye_close / blink_L / blink_R / smile のシェイプキーを持つアバター。
        /// FX には「左手の Fist で Blink を再生する」既存レイヤーがある。
        /// </summary>
        private TestAvatar MakeAvatar(string name)
        {
            var root = Track(new GameObject(name));
            root.AddComponent<Animator>();
            var desc = root.AddComponent<VRCAvatarDescriptor>();

            var body = new GameObject("Body");
            body.transform.SetParent(root.transform);
            var smr = body.AddComponent<SkinnedMeshRenderer>();
            var mesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.up, Vector3.right },
                triangles = new[] { 0, 1, 2 },
            };
            var delta = new[] { Vector3.one, Vector3.one, Vector3.one };
            foreach (var shape in new[] { "eye_close", "blink_L", "blink_R", "smile" })
                mesh.AddBlendShapeFrame(shape, 100, delta, null, null);
            smr.sharedMesh = mesh;
            smr.SetBlendShapeWeight(3, 10f);

            desc.enableEyeLook = true;
            desc.customEyeLookSettings.eyelidType = VRCAvatarDescriptor.EyelidType.Blendshapes;
            desc.customEyeLookSettings.eyelidsSkinnedMesh = smr;
            desc.customEyeLookSettings.eyelidsBlendshapes = new[] { 0, -1, -1 };

            // smile はクリップに含まれるが現在値（10）から変化しないので、閉眼ポーズから除外されるはず
            var blink = new AnimationClip { name = "Blink" };
            AnimationUtility.SetEditorCurve(blink, ShapeBinding("eye_close"),
                new AnimationCurve(new Keyframe(0, 0), new Keyframe(0.1f, 100), new Keyframe(0.2f, 0)));
            AnimationUtility.SetEditorCurve(blink, ShapeBinding("smile"),
                new AnimationCurve(new Keyframe(0, 10), new Keyframe(0.2f, 10)));
            var winkL = new AnimationClip { name = "wink_L" };
            AnimationUtility.SetEditorCurve(winkL, ShapeBinding("blink_L"), AnimationCurve.Constant(0, 0, 100));
            var winkR = new AnimationClip { name = "wink_R" };
            AnimationUtility.SetEditorCurve(winkR, ShapeBinding("blink_R"), AnimationCurve.Constant(0, 0, 100));

            var fxPath = AssetDatabase.GenerateUniqueAssetPath($"{TempFolder}/{name}_FX.controller");
            var fx = new AnimatorController { name = name + "_FX" };
            AssetDatabase.CreateAsset(fx, fxPath);
            fx.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
            fx.AddLayer("Base");
            fx.AddLayer("Left Hand");
            var sm = fx.layers[1].stateMachine;
            var idle = sm.AddState("Idle");
            var fist = sm.AddState("Fist");
            fist.motion = blink;
            idle.AddTransition(fist).AddCondition(AnimatorConditionMode.Equals, 1, "GestureLeft");
            AssetDatabase.AddObjectToAsset(blink, fx);
            AssetDatabase.AddObjectToAsset(winkL, fx);
            AssetDatabase.AddObjectToAsset(winkR, fx);
            AssetDatabase.SaveAssets();

            desc.customizeAnimationLayers = true;
            desc.baseAnimationLayers = new[]
            {
                DefaultLayer(VRCAvatarDescriptor.AnimLayerType.Base),
                DefaultLayer(VRCAvatarDescriptor.AnimLayerType.Additive),
                DefaultLayer(VRCAvatarDescriptor.AnimLayerType.Gesture),
                DefaultLayer(VRCAvatarDescriptor.AnimLayerType.Action),
                new VRCAvatarDescriptor.CustomAnimLayer
                    { type = VRCAvatarDescriptor.AnimLayerType.FX, isDefault = false, animatorController = fx },
            };
            desc.specialAnimationLayers = new VRCAvatarDescriptor.CustomAnimLayer[0];

            return new TestAvatar
            {
                Root = root, Descriptor = desc, Blink = blink, WinkL = winkL, WinkR = winkR, FxPath = fxPath,
            };
        }

        private static EditorCurveBinding ShapeBinding(string shape) =>
            EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape." + shape);

        private static VRCAvatarDescriptor.CustomAnimLayer DefaultLayer(VRCAvatarDescriptor.AnimLayerType type) =>
            new VRCAvatarDescriptor.CustomAnimLayer { type = type, isDefault = true };

        /// <summary>NDMF ビルドを実行する。Prefab インスタンスを含むアバターは処理できないため複製してから渡す</summary>
        private GameObject Build(TestAvatar avatar)
        {
            var clone = Track(Object.Instantiate(avatar.Root));
            AvatarProcessor.ProcessAvatar(clone);
            return clone;
        }

        private static AnimatorController Fx(GameObject built) =>
            (AnimatorController)built.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers
                .First(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).animatorController;

        private static void VerifyBuild(GameObject built, TestAvatar avatar, int eyeLayers, int activesPerLayer,
            float maxClosure, float deadZone, int trackingHands)
        {
            Assert.That(built.GetComponentInChildren<GripBlink>(true), Is.Null, "ビルド後はコンポーネントが削除される");

            var fx = Fx(built);
            Assert.That(AssetDatabase.GetAssetPath(fx), Is.Not.EqualTo(avatar.FxPath), "FX は複製されている");
            var original = AssetDatabase.LoadAssetAtPath<AnimatorController>(avatar.FxPath);
            Assert.That(original.layers.Length, Is.EqualTo(2), "元の FX は変更されない");

            var layers = fx.layers;
            Assert.That(layers.Any(l => l.name == "Left Hand"), Is.True, "既存の FX レイヤーは残る");

            var tracking = layers.FirstOrDefault(l => l.name == TrackingLayerName);
            Assert.That(tracking, Is.Not.Null, "トラッキング切り替えレイヤー");
            foreach (var s in tracking.stateMachine.states.Select(x => x.state))
            {
                Assert.That(s.motion, Is.Null, $"Tracking/{s.name} はモーションを持たない");
                Assert.That(s.behaviours.OfType<VRCAnimatorLayerControl>(), Is.Empty, $"Tracking/{s.name}");
            }

            var trackingActive = tracking.stateMachine.states.Select(x => x.state).First(s => s.name == "Active");
            // 手ごとの「やめる条件」（Weight 以下 / Fist 以外）の直積
            Assert.That(trackingActive.transitions.Length, Is.EqualTo(trackingHands == 1 ? 2 : 4),
                "Active → Idle の遷移数");

            var eye = layers.Select((l, i) => (layer: l, index: i))
                .Where(x => x.layer.name.StartsWith("GripBlink") && x.layer.name != TrackingLayerName).ToList();
            Assert.That(eye.Count, Is.EqualTo(eyeLayers), "閉眼レイヤー数");

            var closedThreshold = deadZone + (1 - deadZone) / maxClosure;
            foreach (var (layer, index) in eye)
            {
                Assert.That(layer.stateMachine.defaultState.name, Is.EqualTo("Init"), layer.name);
                var states = layer.stateMachine.states.Select(s => s.state).ToList();
                foreach (var s in states)
                {
                    var lc = s.behaviours.OfType<VRCAnimatorLayerControl>().FirstOrDefault();
                    Assert.That(lc, Is.Not.Null, $"{layer.name}/{s.name} Layer Control");
                    Assert.That(lc.layer, Is.EqualTo(index), $"{layer.name}/{s.name} はマージ後のレイヤー番号を指す");
                    Assert.That(s.behaviours.OfType<VRCAnimatorTrackingControl>(), Is.Empty,
                        $"{layer.name}/{s.name} に Tracking Control を置かない");
                }

                var actives = states.Where(s => s.name.StartsWith("Active")).ToList();
                Assert.That(actives.Count, Is.EqualTo(activesPerLayer), $"{layer.name} の Active ステート数");
                foreach (var active in actives)
                {
                    var tree = active.motion as BlendTree;
                    Assert.That(tree, Is.Not.Null, $"{layer.name}/{active.name} Blend Tree");
                    Assert.That(tree.children.Length, Is.EqualTo(2));
                    Assert.That(tree.children[0].threshold, Is.EqualTo(deadZone).Within(1e-5f), "開いたポーズのしきい値");
                    Assert.That(tree.children[1].threshold, Is.EqualTo(closedThreshold).Within(1e-5f), "閉じたポーズのしきい値");

                    var closed = (AnimationClip)tree.children[1].motion;
                    var bindings = AnimationUtility.GetCurveBindings(closed);
                    Assert.That(bindings, Is.Not.Empty);
                    Assert.That(bindings.Any(b => b.propertyName == "blendShape.smile"), Is.False, "変化しない値は使わない");
                    foreach (var b in bindings)
                        Assert.That(AnimationUtility.GetEditorCurve(closed, b).keys[0].value, Is.EqualTo(100f).Within(0.01f),
                            "閉じた値はクリップ内の最大値");
                }
            }
        }

        private static Animator PrepareAnimator(GameObject built)
        {
            var animator = built.GetComponent<Animator>();
            animator.runtimeAnimatorController = Fx(built);
            animator.Rebind();
            return animator;
        }

        private static void SetHand(Animator animator, string side, int gesture, float weight)
        {
            animator.SetInteger("Gesture" + side, gesture);
            animator.SetFloat($"Gesture{side}Weight", weight);
        }

        /// <summary>
        /// VRChat の State Behaviour は Unity 上では動かないため、レイヤーウェイトを直接 1 にして出力値を確かめる
        /// </summary>
        private static void Step(Animator animator, int layerIndex, int frames)
        {
            animator.SetLayerWeight(layerIndex, 1f);
            for (var i = 0; i < frames; i++) animator.Update(0.05f);
        }

        /// <summary>
        /// Layer Control の代わりに、ステートに合わせてレイヤーウェイトを切り替える（Active なら 1、それ以外は 0）。
        /// GripBlink が止まったときに下のレイヤーの表情が見えるかを確かめるため
        /// </summary>
        private static void StepWithLayerControl(Animator animator, int layerIndex, int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                var active = animator.GetCurrentAnimatorStateInfo(layerIndex).IsName("Active");
                animator.SetLayerWeight(layerIndex, active ? 1f : 0f);
                animator.Update(0.05f);
            }
        }

        /// <summary>左手の Fist で目を閉じたままにする、アバター自身の表情レイヤーを追加する</summary>
        private static void AddFistFaceLayer(TestAvatar avatar)
        {
            var fx = AssetDatabase.LoadAssetAtPath<AnimatorController>(avatar.FxPath);
            var face = new AnimationClip { name = "FistFace" };
            AnimationUtility.SetEditorCurve(face, ShapeBinding("eye_close"), AnimationCurve.Constant(0, 1f / 60f, 100));
            AssetDatabase.AddObjectToAsset(face, fx);
            fx.AddLayer("Fist Face");
            var sm = fx.layers[fx.layers.Length - 1].stateMachine;
            var idle = sm.AddState("Idle");
            var fist = sm.AddState("Fist");
            fist.motion = face;
            idle.AddTransition(fist).AddCondition(AnimatorConditionMode.Equals, 1, "GestureLeft");
            fist.AddTransition(idle).AddCondition(AnimatorConditionMode.NotEqual, 1, "GestureLeft");
            var layers = fx.layers;
            layers[layers.Length - 1].defaultWeight = 1f; // AddLayer で追加したレイヤーはウェイト 0 になる
            fx.layers = layers;
            AssetDatabase.SaveAssets();
        }

        private static float EyeClose(GameObject built)
        {
            var smr = built.GetComponentInChildren<SkinnedMeshRenderer>();
            return smr.GetBlendShapeWeight(smr.sharedMesh.GetBlendShapeIndex("eye_close"));
        }
    }
}
