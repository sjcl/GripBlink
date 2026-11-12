using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using VRC.SDK3.Avatars.Components;

namespace Sjcl.GripBlink.Editor
{
    /// <summary>
    /// Hierarchy 右クリックメニューからの追加と、Prefab のドラッグ＆ドロップ時のチェック。
    /// </summary>
    [InitializeOnLoad]
    internal static class GripBlinkInstaller
    {
        private const string PrefabGuid = "045d27eb49134dd2a17c15cae97b7448";
        private const string MenuPath = "GameObject/GripBlink/GripBlink を追加";
        private const string UndoName = "Add GripBlink";

        /// <summary>
        /// ダイアログの表示。テストから差し替えて、Test Runner の実行中にダイアログで止まらないようにする
        /// </summary>
        internal static Func<string, string, string, string, bool> ShowDialog = EditorUtility.DisplayDialog;

        private static bool _scheduled;
        private static bool _installing;

        static GripBlinkInstaller()
        {
            ObjectChangeEvents.changesPublished += OnChangesPublished;
        }

        [MenuItem(MenuPath, true)]
        internal static bool ValidateAdd()
        {
            return Selection.gameObjects.Any(go => go.GetComponentInParent<VRCAvatarDescriptor>(true) != null);
        }

        [MenuItem(MenuPath, false, 20)]
        private static void Add(MenuCommand command)
        {
            // 複数選択時、Hierarchy のメニューは選択オブジェクトごとに呼ばれるので1回にまとめる
            if (_scheduled) return;
            _scheduled = true;
            EditorApplication.delayCall += () =>
            {
                _scheduled = false;
                AddToSelection();
            };
        }

        internal static void AddToSelection()
        {
            var avatars = Selection.gameObjects
                .Select(go => go.GetComponentInParent<VRCAvatarDescriptor>(true))
                .Where(d => d != null)
                .Distinct()
                .ToList();

            if (avatars.Count == 0)
            {
                ShowDialog("GripBlink",
                    "VRC Avatar Descriptor が付いたアバター（またはその子）を選択してから実行してください。", "OK", "");
                return;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(PrefabGuid));
            if (prefab == null)
            {
                ShowDialog("GripBlink", "GripBlink の Prefab が見つかりません。パッケージを再インストールしてください。", "OK", "");
                return;
            }

            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);

            var created = new List<Object>();
            var skipped = new List<string>();
            _installing = true;
            try
            {
                foreach (var desc in avatars)
                {
                    var existing = desc.GetComponentInChildren<GripBlink>(true);
                    if (existing != null)
                    {
                        skipped.Add(desc.name);
                        created.Add(existing.gameObject);
                        continue;
                    }

                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, desc.transform);
                    instance.name = prefab.name;
                    Undo.RegisterCreatedObjectUndo(instance, UndoName);

                    var gb = instance.GetComponent<GripBlink>();
                    if (gb != null) AutoAssign(gb, desc);
                    created.Add(instance);
                }
            }
            finally
            {
                _installing = false;
            }

            // 選択の変更も同じ Undo グループに入れ、Ctrl+Z 1回で追加を取り消せるようにする
            if (created.Count > 0) Selection.objects = created.ToArray();
            Undo.CollapseUndoOperations(group);
            if (created.Count > 0) EditorGUIUtility.PingObject(created[0]);

            if (skipped.Count > 0)
            {
                ShowDialog("GripBlink",
                    "次のアバターには既に GripBlink があるため追加しませんでした（既存のものを選択しています）:\n"
                    + string.Join("\n", skipped), "OK", "");
            }
        }

        /// <summary>
        /// クリップが1つも設定されていないときだけ、自動検出の結果を設定する。
        /// どれか1つでも設定済みなら、ユーザーが手動で設定したものとみなして何もしない。
        /// </summary>
        internal static bool AutoAssign(GripBlink gb, VRCAvatarDescriptor desc)
        {
            if (gb.blinkClip != null || gb.leftEyeClip != null || gb.rightEyeClip != null) return false;

            var found = ClipDetector.Detect(desc);
            if (!found.Any) return false;

            // 片目分しか見つからない場合は設定せず、ユーザーの判断に任せる
            if (found.Both == null && (found.Left == null || found.Right == null)) return false;

            Undo.RecordObject(gb, "GripBlink auto detect");
            if (found.Both != null)
            {
                gb.mode = EyeControlMode.BothEyes;
                gb.blinkClip = found.Both;
            }
            else
            {
                gb.mode = EyeControlMode.Separate;
            }

            // 左右クリップは見つかれば両目モードでも入れておく（後で左右独立に切り替えたとき用）
            gb.leftEyeClip = found.Left;
            gb.rightEyeClip = found.Right;

            PrefabUtility.RecordPrefabInstancePropertyModifications(gb);
            return true;
        }

        // ドラッグ＆ドロップのほか、複製や貼り付けで追加されたときにも呼ばれる
        private static void OnChangesPublished(ref ObjectChangeEventStream stream)
        {
            if (_installing || EditorApplication.isPlayingOrWillChangePlaymode) return;

            List<GripBlink> added = null;
            for (var i = 0; i < stream.length; i++)
            {
                if (stream.GetEventType(i) != ObjectChangeKind.CreateGameObjectHierarchy) continue;
                stream.GetCreateGameObjectHierarchyEvent(i, out var data);
                if (!(EditorUtility.InstanceIDToObject(data.instanceId) is GameObject go)) continue;

                foreach (var gb in go.GetComponentsInChildren<GripBlink>(true))
                {
                    (added ??= new List<GripBlink>()).Add(gb);
                }
            }

            if (added == null) return;

            // イベント処理中に Hierarchy を変更しないよう、次のフレームで処理する
            EditorApplication.delayCall += () =>
            {
                foreach (var gb in added) CheckDropped(gb);
            };
        }

        internal static void CheckDropped(GripBlink gb)
        {
            if (gb == null) return;

            var desc = GripBlinkAnalyzer.FindAvatar(gb);
            if (desc == null)
            {
                Debug.LogWarning("[GripBlink] GripBlink はアバター（VRC Avatar Descriptor）の子に配置してください。", gb);
                return;
            }

            var others = desc.GetComponentsInChildren<GripBlink>(true).Where(c => c != gb).ToList();
            var go = gb.gameObject;
            var removable = !PrefabUtility.IsPartOfPrefabInstance(go) || PrefabUtility.IsOutermostPrefabInstanceRoot(go);
            if (others.Count > 0 && !removable)
            {
                Debug.LogWarning($"[GripBlink] アバター「{desc.name}」に GripBlink が複数あります。1つだけ残して削除してください。", gb);
                return;
            }

            if (others.Count > 0)
            {
                var remove = ShowDialog("GripBlink",
                    $"アバター「{desc.name}」には既に GripBlink（{others[0].name}）があります。\n"
                    + "1つのアバターに複数置くことはできません。今追加したものを取り消しますか？",
                    "取り消す", "そのままにする");
                if (remove)
                {
                    Undo.DestroyObjectImmediate(go);
                    Selection.activeGameObject = others[0].gameObject;
                    EditorGUIUtility.PingObject(others[0].gameObject);
                }

                return;
            }

            AutoAssign(gb, desc);
        }
    }
}
