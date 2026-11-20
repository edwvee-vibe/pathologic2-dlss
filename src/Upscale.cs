using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Pathologic2Dlss
{
    /// <summary>
    /// Rendering below native resolution for DLSS upscaling. Reuses the game's own pipeline (the "Render scale" setting):
    /// cameras with DynamicResolutionCamera take targetTexture from DynamicResolution.GetTargetTexture() every LateUpdate,
    /// and the UI camera (DynamicResolutionOutputCamera) shows the same texture full-screen via a RawImage.
    /// The mod swaps that texture for its own reduced one (LowRt) and displays the full-size frame after DLSS and post-processing (FullRt).
    /// The game's slider is ignored in this mode.
    /// </summary>
    internal static class Upscale
    {
        public const float MinScale = 0.33f;
        private const int KeepAliveFrames = 5;

        private static readonly AccessTools.FieldRef<DynamicResolution, List<Camera>> CamerasRef =
            AccessTools.FieldRefAccess<DynamicResolution, List<Camera>>("cameras");
        private static readonly AccessTools.FieldRef<DynamicResolutionOutputCamera, RawImage> ViewRef =
            AccessTools.FieldRefAccess<DynamicResolutionOutputCamera, RawImage>("view");

        private static readonly AccessTools.FieldRef<DynamicResolution, float> GameScaleRef =
            AccessTools.FieldRefAccess<DynamicResolution, float>("scale");

        private static float gameScale = 1f;
        private static bool gameScaleKnown;
        private static bool gameScaleOverridden;

        /// <summary>Prefix for DynamicResolution.SetScale: remember the value from the game's settings.</summary>
        public static void NoteGameScale(float value)
        {
            gameScale = value;
            gameScaleKnown = true;
        }

        /// <summary>
        /// Called before DynamicResolution.GetTargetTexture: while DLSS runs, the game's "Render scale" is treated as 100%
        /// (DLSS input size comes from the mod's RenderScale only). With DLSS off (F8, Enabled = false, no DLSS) the game's
        /// value is back.
        /// </summary>
        public static void OverrideGameScale(DynamicResolution dr)
        {
            if (dr == null)
            {
                return;
            }
            if (!gameScaleKnown)
            {
                gameScale = GameScaleRef(dr);
                gameScaleKnown = true;
            }
            // Only while DLSS actually processes frames: with the game's anti-aliasing off there is no TAA pass to replace,
            // and the game's scale must keep working.
            bool overrideNow = DlssPass.Running && Time.frameCount - lastDlssFrame <= KeepAliveFrames;
            float want = overrideNow ? 1f : gameScale;
            if (GameScaleRef(dr) != want)
            {
                GameScaleRef(dr) = want;
            }
            if (overrideNow != gameScaleOverridden)
            {
                gameScaleOverridden = overrideNow;
                if (Mathf.Abs(gameScale - 1f) > 0.0001f)
                {
                    Plugin.Log.LogInfo(overrideNow
                        ? $"Game's render scale {gameScale:0.###} is ignored while DLSS is on (treated as 1)."
                        : $"Game's render scale {gameScale:0.###} is back (DLSS off).");
                }
            }
        }

        public static RenderTexture LowRt { get; private set; }
        public static RenderTexture FullRt { get; private set; }
        public static int OutWidth { get; private set; }
        public static int OutHeight { get; private set; }

        private static int lastDlssFrame = -1000;
        private static int fullWrittenFrame = -1000;

        public static float Scale => Mathf.Clamp(Plugin.S.RenderScale.Value, MinScale, 1f);

        /// <summary>The DLSS path ran this frame (we set the jitter).</summary>
        public static void NoteDlssFrame()
        {
            lastDlssFrame = Time.frameCount;
        }

        private static bool Wanted => !DlssPass.UpscaleUnavailable && Scale < 0.999f && Time.frameCount - lastDlssFrame <= KeepAliveFrames;

        /// <summary>Prefix for DynamicResolution.GetTargetTexture: true to return our texture instead of the game's.</summary>
        public static bool TryGetTarget(out RenderTexture rt)
        {
            rt = null;
            if (!Wanted || Screen.width < 16 || Screen.height < 16)
            {
                ReleaseTargets();
                return false;
            }
            float s = Scale;
            int w = Mathf.Max(2, Mathf.RoundToInt(Screen.width * s / 2f) * 2);
            int h = Mathf.Max(2, Mathf.RoundToInt(Screen.height * s / 2f) * 2);
            DlssPass.SnapInputSize(Screen.width, Screen.height, ref w, ref h);
            if (LowRt == null || LowRt.width != w || LowRt.height != h || OutWidth != Screen.width || OutHeight != Screen.height)
            {
                RenderTexture old = LowRt;
                LowRt = new RenderTexture(w, h, 32, RenderTextureFormat.DefaultHDR)
                {
                    name = "DLSS Input",
                    hideFlags = HideFlags.HideAndDontSave
                };
                LowRt.Create();
                OutWidth = Screen.width;
                OutHeight = Screen.height;
                Retarget(old, LowRt);
                Destroy(old);
                Destroy(FullRt);
                FullRt = null;
                Plugin.Log.LogInfo($"DLSS upscaling: render {w}x{h} -> {OutWidth}x{OutHeight} (RenderScale {s:0.###}, actual {(float)w / OutWidth:0.###})");
            }
            rt = LowRt;
            return true;
        }

        /// <summary>Full-size texture the post-processing writes the final frame into.</summary>
        public static RenderTexture GetFullRt()
        {
            if (FullRt == null || FullRt.width != OutWidth || FullRt.height != OutHeight)
            {
                Destroy(FullRt);
                FullRt = new RenderTexture(OutWidth, OutHeight, 0, LowRt != null ? LowRt.format : RenderTextureFormat.DefaultHDR)
                {
                    name = "DLSS Output Frame",
                    hideFlags = HideFlags.HideAndDontSave
                };
                FullRt.Create();
            }
            fullWrittenFrame = Time.frameCount;
            return FullRt;
        }

        /// <summary>Postfix for DynamicResolutionOutputCamera.OnPreCull: show the full-size frame if it was rendered.</summary>
        public static void FixOutputView(DynamicResolutionOutputCamera output)
        {
            if (LowRt == null || FullRt == null || Time.frameCount - fullWrittenFrame > 1)
            {
                return; // the frame went without us (e.g. after MotionTrail/WaveDistortion); the reduced texture stays
            }
            RawImage view = ViewRef(output);
            if (view != null && ReferenceEquals(view.texture, LowRt))
            {
                view.texture = FullRt;
            }
        }

        public static void ReleaseTargets()
        {
            if (LowRt == null && FullRt == null)
            {
                return;
            }
            Retarget(LowRt, null);
            Destroy(LowRt);
            Destroy(FullRt);
            LowRt = null;
            FullRt = null;
            Plugin.Log.LogInfo("DLSS upscaling off, cameras are back at the game's resolution.");
        }

        private static void Retarget(RenderTexture from, RenderTexture to)
        {
            if (from == null)
            {
                return;
            }
            List<Camera> cameras = CamerasRef(DynamicResolution.Instance);
            if (cameras == null)
            {
                return;
            }
            foreach (Camera c in cameras)
            {
                if (c != null && ReferenceEquals(c.targetTexture, from))
                {
                    c.targetTexture = to;
                }
            }
        }

        private static void Destroy(RenderTexture rt)
        {
            if (rt != null)
            {
                rt.Release();
                Object.Destroy(rt);
            }
        }
    }
}
