using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Sjcl.GripBlink.Editor
{
    [CustomEditor(typeof(GripBlink))]
    internal class GripBlinkEditor : UnityEditor.Editor
    {
        private SerializedProperty _mode, _blinkClip, _hand, _leftEyeClip, _leftEyeHand, _rightEyeClip, _rightEyeHand;
        private SerializedProperty _maxClosure, _deadZone, _requireFist, _suppressAutoBlink, _fadeDuration;
        private SerializedProperty _autoDetectPeak, _sampleTime, _layerPriority;

        private List<Diagnostic> _diagnostics;
        private bool _advanced;
        private string _detectMessage;

        private static readonly GUIContent[] HandLabels = { new GUIContent("左手"), new GUIContent("右手") };

        private void OnEnable()
        {
            _mode = serializedObject.FindProperty(nameof(GripBlink.mode));
            _blinkClip = serializedObject.FindProperty(nameof(GripBlink.blinkClip));
            _hand = serializedObject.FindProperty(nameof(GripBlink.hand));
            _leftEyeClip = serializedObject.FindProperty(nameof(GripBlink.leftEyeClip));
            _leftEyeHand = serializedObject.FindProperty(nameof(GripBlink.leftEyeHand));
            _rightEyeClip = serializedObject.FindProperty(nameof(GripBlink.rightEyeClip));
            _rightEyeHand = serializedObject.FindProperty(nameof(GripBlink.rightEyeHand));
            _maxClosure = serializedObject.FindProperty(nameof(GripBlink.maxClosure));
            _deadZone = serializedObject.FindProperty(nameof(GripBlink.deadZone));
            _requireFist = serializedObject.FindProperty(nameof(GripBlink.requireFist));
            _suppressAutoBlink = serializedObject.FindProperty(nameof(GripBlink.suppressAutoBlink));
            _fadeDuration = serializedObject.FindProperty(nameof(GripBlink.fadeDuration));
            _autoDetectPeak = serializedObject.FindProperty(nameof(GripBlink.autoDetectPeak));
            _sampleTime = serializedObject.FindProperty(nameof(GripBlink.sampleTime));
            _layerPriority = serializedObject.FindProperty(nameof(GripBlink.layerPriority));
            _diagnostics = null;
            Undo.undoRedoPerformed += Invalidate;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= Invalidate;
        }

        private void Invalidate()
        {
            _diagnostics = null;
            Repaint();
        }

        public override void OnInspectorGUI()
        {
            var gb = (GripBlink)target;
            var desc = GripBlinkAnalyzer.FindAvatar(gb);

            serializedObject.Update();
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.HelpBox(
                "トリガーの引き具合（Fist ジェスチャー中の GestureWeight）に応じて目を閉じます。"
                + "VRChat にはグリップの握り具合を表すアナログ値がないため、トリガー量を使います。\n"
                + "アップロード時・Play 時に自動で組み込まれ、元のアバターやアニメーションは変更しません。"
                + "このオブジェクトを削除すれば元に戻ります。",
                MessageType.None);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("対象アバター", desc, typeof(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor), true);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("閉眼アニメーション", EditorStyles.boldLabel);
            _mode.enumValueIndex = GUILayout.Toolbar(_mode.enumValueIndex, new[] { "両目", "左右独立" });

            if ((EyeControlMode)_mode.enumValueIndex == EyeControlMode.BothEyes)
            {
                EditorGUILayout.PropertyField(_blinkClip, new GUIContent("閉眼クリップ"));
                HandPopup(_hand, "入力する手");
            }
            else
            {
                EditorGUILayout.PropertyField(_leftEyeClip, new GUIContent("左目クリップ"));
                HandPopup(_leftEyeHand, "左目の入力");
                EditorGUILayout.PropertyField(_rightEyeClip, new GUIContent("右目クリップ"));
                HandPopup(_rightEyeHand, "右目の入力");
            }

            DrawDetectButtons(gb, desc);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("基本設定", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_maxClosure, new GUIContent("最大閉眼率"));
            EditorGUILayout.PropertyField(_deadZone, new GUIContent("遊び（デッドゾーン）"));
            EditorGUILayout.PropertyField(_requireFist, new GUIContent("Fist 中のみ反応"));
            EditorGUILayout.PropertyField(_suppressAutoBlink, new GUIContent("自動まばたきを一時停止"));
            EditorGUILayout.PropertyField(_fadeDuration, new GUIContent("フェード時間（秒）"));

            _advanced = EditorGUILayout.Foldout(_advanced, "詳細設定", true);
            if (_advanced)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.PropertyField(_autoDetectPeak, new GUIContent("閉眼フレームを自動検出"));
                    using (new EditorGUI.DisabledScope(_autoDetectPeak.boolValue))
                    {
                        EditorGUILayout.PropertyField(_sampleTime, new GUIContent("使用する時刻（秒）"));
                    }

                    EditorGUILayout.PropertyField(_layerPriority, new GUIContent("Layer Priority"));
                }
            }

            if (EditorGUI.EndChangeCheck()) _diagnostics = null;
            serializedObject.ApplyModifiedProperties();

            DrawDiagnostics(gb);
        }

        private static void HandPopup(SerializedProperty prop, string label)
        {
            prop.enumValueIndex = EditorGUILayout.Popup(new GUIContent(label), prop.enumValueIndex, HandLabels);
        }

        private void DrawDetectButtons(GripBlink gb, VRC.SDK3.Avatars.Components.VRCAvatarDescriptor desc)
        {
            if (desc == null) return;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("クリップを自動検出"))
                {
                    serializedObject.ApplyModifiedProperties();
                    var found = ClipDetector.Detect(desc);
                    if (!found.Any)
                    {
                        _detectMessage = "blink 系のクリップが見つかりませんでした。手動で指定するか、シェイプキーから作成してください。";
                    }
                    else
                    {
                        Undo.RecordObject(gb, "GripBlink auto detect");
                        if (found.Both != null) gb.blinkClip = found.Both;
                        if (found.Left != null) gb.leftEyeClip = found.Left;
                        if (found.Right != null) gb.rightEyeClip = found.Right;
                        if (found.Both == null && found.Left != null && found.Right != null)
                            gb.mode = EyeControlMode.Separate;
                        PrefabUtility.RecordPrefabInstancePropertyModifications(gb);
                        _detectMessage = "検出結果: "
                                         + $"両目={Name(found.Both)} / 左目={Name(found.Left)} / 右目={Name(found.Right)}";
                    }

                    serializedObject.Update();
                    _diagnostics = null;
                }

                var shapes = ClipDetector.FindShapes(desc);
                using (new EditorGUI.DisabledScope(!shapes.Any))
                {
                    if (GUILayout.Button(new GUIContent("シェイプキーから作成",
                            "目のメッシュのまばたき系シェイプキーを 100 にするクリップを Assets/GripBlink/Generated に新規作成します")))
                    {
                        CreateFromShapes(gb, desc, shapes);
                    }
                }
            }

            if (!string.IsNullOrEmpty(_detectMessage)) EditorGUILayout.HelpBox(_detectMessage, MessageType.None);
        }

        private void CreateFromShapes(GripBlink gb, VRC.SDK3.Avatars.Components.VRCAvatarDescriptor desc,
            ClipDetector.ShapeCandidates shapes)
        {
            var separate = gb.mode == EyeControlMode.Separate;
            var list = new List<string>();
            if (!separate && shapes.Both != null) list.Add($"両目: {shapes.Both}");
            if ((separate || shapes.Both == null) && shapes.Left != null) list.Add($"左目: {shapes.Left}");
            if ((separate || shapes.Both == null) && shapes.Right != null) list.Add($"右目: {shapes.Right}");
            if (list.Count == 0)
            {
                _detectMessage = separate ? "左右別のまばたきシェイプキーが見つかりませんでした。" : "まばたきシェイプキーが見つかりませんでした。";
                return;
            }

            if (!EditorUtility.DisplayDialog("GripBlink",
                    $"「{shapes.Renderer.name}」の次のシェイプキーからクリップを新規作成します:\n" + string.Join("\n", list)
                    + "\n\n保存先: Assets/GripBlink/Generated", "作成", "キャンセル")) return;

            serializedObject.ApplyModifiedProperties();
            Undo.RecordObject(gb, "GripBlink create clip");
            if (!separate && shapes.Both != null)
            {
                gb.blinkClip = ClipDetector.CreateClipFromShape(desc, shapes.Renderer, shapes.Both, "Blink");
            }
            else
            {
                if (shapes.Left != null) gb.leftEyeClip = ClipDetector.CreateClipFromShape(desc, shapes.Renderer, shapes.Left, "Blink_L");
                if (shapes.Right != null) gb.rightEyeClip = ClipDetector.CreateClipFromShape(desc, shapes.Renderer, shapes.Right, "Blink_R");
                gb.mode = EyeControlMode.Separate;
            }

            PrefabUtility.RecordPrefabInstancePropertyModifications(gb);
            serializedObject.Update();
            _detectMessage = "クリップを作成しました。";
            _diagnostics = null;
        }

        private static string Name(Object o) => o != null ? o.name : "なし";

        private void DrawDiagnostics(GripBlink gb)
        {
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("チェック結果", EditorStyles.boldLabel);
                if (GUILayout.Button("再チェック", GUILayout.Width(80))) _diagnostics = null;
            }

            // FX 全体を走査するため、設定変更時と再チェック時だけ計算する
            if (_diagnostics == null) _diagnostics = GripBlinkAnalyzer.Diagnose(gb);

            if (_diagnostics.Count == 0)
            {
                EditorGUILayout.HelpBox("問題は見つかりませんでした。", MessageType.Info);
                return;
            }

            foreach (var d in _diagnostics)
            {
                var type = d.Severity switch
                {
                    Severity.Error => MessageType.Error,
                    Severity.Warning => MessageType.Warning,
                    _ => MessageType.Info,
                };
                EditorGUILayout.HelpBox(d.Text, type);
            }
        }
    }
}
