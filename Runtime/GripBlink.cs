using UnityEngine;
using VRC.SDKBase;

namespace Sjcl.GripBlink
{
    public enum GripHand
    {
        Left = 0,
        Right = 1,

        /// <summary>
        /// 左右どちらの手でも操作できる（両手で握る必要はない）。
        /// 先に操作し始めた手が優先され、その手を離すと、もう一方の手が操作中ならそちらに切り替わる。
        /// </summary>
        Either = 2,
    }

    public enum EyeControlMode
    {
        /// <summary>1つのクリップで両目を制御する。入力する手は hand で指定する</summary>
        BothEyes = 0,

        /// <summary>左目用・右目用のクリップを、それぞれに指定した手で制御する</summary>
        Separate = 1,
    }

    /// <summary>
    /// GripBlink の設定コンポーネント。
    /// アバター配下に置くと、NDMF ビルド時に閉眼レイヤーが生成され FX へ非破壊的にマージされる。
    /// このコンポーネント自体はビルド時に削除される（IEditorOnly）。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("GripBlink/GripBlink")]
    public class GripBlink : MonoBehaviour, IEditorOnly
    {
        [Tooltip("両目を1つの入力で動かすか、左右の目を別々の入力で動かすか")]
        public EyeControlMode mode = EyeControlMode.BothEyes;

        // --- 両目モード ---
        [Tooltip("目を閉じるアニメーションクリップ（既存の blink クリップ）。最も目が閉じたフレームが自動で使われます")]
        public AnimationClip blinkClip;

        [Tooltip("閉眼に使う手。「左右どちらでも」は片方の手だけで操作でき、先に操作した手が優先されます")]
        public GripHand hand = GripHand.Either;

        // --- 左右独立モード ---
        [Tooltip("左目だけを閉じるクリップ（wink_L など）")]
        public AnimationClip leftEyeClip;

        [Tooltip("左目を制御する手。「左右どちらでも」は片方の手だけで操作できます")]
        public GripHand leftEyeHand = GripHand.Left;

        [Tooltip("右目だけを閉じるクリップ（wink_R など）")]
        public AnimationClip rightEyeClip;

        [Tooltip("右目を制御する手。「左右どちらでも」は片方の手だけで操作できます")]
        public GripHand rightEyeHand = GripHand.Right;

        // --- 基本設定 ---
        [Range(0.05f, 1f)]
        [Tooltip("入力が最大(1.0)のときの閉眼率")]
        public float maxClosure = 1f;

        [Range(0f, 0.9f)]
        [Tooltip("この値以下の入力は無視します（遊び）。この値を超えると閉眼が始まります")]
        public float deadZone = 0.05f;

        [Tooltip("Fist（握り）ジェスチャー中のときだけ反応します。オフにすると全ジェスチャーでトリガー量に反応します")]
        public bool requireFist = true;

        [Tooltip("閉眼操作中は VRChat 標準の自動まばたき・視線（Eyes & Eyelids トラッキング）を一時停止します")]
        public bool suppressAutoBlink = true;

        [Min(0f)]
        [Tooltip("操作開始・終了時に既存の表情となじませる時間（秒）")]
        public float fadeDuration = 0.1f;

        // --- 詳細 ---
        [Tooltip("オフにすると、下の時刻のフレームを閉眼ポーズとして使います")]
        public bool autoDetectPeak = true;

        [Min(0f)]
        [Tooltip("閉眼ポーズとして使うクリップ内の時刻（秒）")]
        public float sampleTime = 0f;

        [Tooltip("Modular Avatar の Merge Animator の Layer Priority。既存の表情レイヤーより後ろ（大きい値）に置くと、操作中に閉眼が優先されます")]
        public int layerPriority = 100;
    }
}
