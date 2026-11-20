using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.NVIDIA;
using UnityEngine.PostProcessing;
using UnityEngine.Rendering;

namespace Pathologic2Dlss
{
    /// <summary>
    /// DLSS instead of the TAA pass: at native resolution (DLAA) or upscaling (see Upscale). Execution parameters follow DLSSPass
    /// from HDRP (Unity.RenderPipelines.HighDefinition.Runtime): mvScale = -size, jitter = -jitter in pixels, invertYAxis = 1,
    /// subrect = whole input, flags IsHDR | MVLowRes | DepthInverted.
    /// </summary>
    internal static class DlssPass
    {
        private const uint ExpectedDeviceVersion = 4u; // HDRP checks the same value
        private const DLSSQuality QualityDlaa = (DLSSQuality)5; // NVSDK_NGX_PerfQuality_Value_DLAA, missing from Unity's enum
        private const DLSSFeatureFlags FlagAutoExposure = (DLSSFeatureFlags)0x40; // NVSDK_NGX_DLSS_Feature_Flags_AutoExposure
        private const int ValidationTimeoutFrames = 120;

        private enum DeviceState { NotTried, Ready, Failed }

        private sealed class View
        {
            public Camera Camera;
            public DLSSContext Context;
            public int Width, Height;         // DLSS input
            public int OutWidth, OutHeight;   // DLSS output
            public bool UpscaleThisFrame;     // the camera renders into the reduced texture Upscale.LowRt
            public int UpscaledFrame = -1;    // frame in which the DLSS output was full-size
            public string UpscaleKey;
            public DLSSQuality UpscaleQuality;
            public DLSSQuality Quality;
            public DLSSFeatureFlags Flags;
            public int Generation;
            public bool ForceMaxQuality;
            public int CreatedFrame;
            public bool Validated;
            public uint Slot;

            public RenderTexture Output, DepthCopy, MotionCopy;

            public int SampleIndex;
            public Vector2 JitterPixels;
            public bool JitteredThisFrame;
            public bool WasActive;
            public bool Reset = true;
            public bool BuffersLogged;
            public int LastRenderFrame = -1000;
        }

        private static DeviceState deviceState = DeviceState.NotTried;
        private static GraphicsDevice device;
        private static GraphicsDeviceDebugView debugView;
        private static CommandBuffer cmd;
        private static Material sharpenMaterial;
        private static bool sharpenMissing;

        private static bool userOff;
        private static bool disabledByError;
        private static int generation;
        private static string appliedPreset = "Default";

        private static readonly Dictionary<TaaComponent, View> Views = new Dictionary<TaaComponent, View>();
        private static readonly List<TaaComponent> Dead = new List<TaaComponent>();

        private static readonly int DepthTexId = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int MotionTexId = Shader.PropertyToID("_CameraMotionVectorsTexture");
        private static readonly int JitterId = Shader.PropertyToID("_Jitter");
        private static readonly int SharpenId = Shader.PropertyToID("_SharpenParameters");
        private static readonly int FinalBlendId = Shader.PropertyToID("_FinalBlendParameters");
        private static readonly int HistoryId = Shader.PropertyToID("_HistoryTex");

        /// <summary>The OnRenderImage transpiler did not find its pattern; upscaling stays off.</summary>
        public static bool UpscaleUnavailable;

        public static readonly AccessTools.FieldRef<PostProcessingBehaviour, TaaComponent> BehaviourTaa =
            AccessTools.FieldRefAccess<PostProcessingBehaviour, TaaComponent>("m_Taa");

        private static readonly AccessTools.FieldRef<TaaComponent, bool> ResetHistoryRef =
            AccessTools.FieldRefAccess<TaaComponent, bool>("m_ResetHistory");
        private static readonly Action<TaaComponent, Vector2> SetJitterVector =
            AccessTools.MethodDelegate<Action<TaaComponent, Vector2>>(AccessTools.PropertySetter(typeof(TaaComponent), "jitterVector"));
        private static readonly Func<TaaComponent, Vector2, Matrix4x4> PerspectiveMatrix =
            AccessTools.MethodDelegate<Func<TaaComponent, Vector2, Matrix4x4>>(AccessTools.Method(typeof(TaaComponent), "GetPerspectiveProjectionMatrix"));
        private static readonly Func<TaaComponent, Vector2, Matrix4x4> OrthographicMatrix =
            AccessTools.MethodDelegate<Func<TaaComponent, Vector2, Matrix4x4>>(AccessTools.Method(typeof(TaaComponent), "GetOrthographicProjectionMatrix"));
        private static readonly PropertyInfo FeatureSlotProperty =
            typeof(DLSSContext).GetProperty("featureSlot", BindingFlags.Instance | BindingFlags.NonPublic);

        private static Settings S => Plugin.S;

        private static bool Wanted => S.Enabled.Value && !userOff && !disabledByError;

        /// <summary>DLSS is switched on and the NGX device works: the game's own "Render scale" is overridden to 100%.</summary>
        public static bool Running => Wanted && deviceState == DeviceState.Ready;

        // ---------------------------------------------------------------- control

        /// <summary>Benchmark only: the game's TAA instead of DLSS (same as the toggle key).</summary>
        public static bool UserOff { get => userOff; set => userOff = value; }

        /// <summary>Benchmark only: neither TAA nor DLSS, the frame is just copied.</summary>
        public static bool BenchNoAa;

        public static bool HasValidContext
        {
            get
            {
                foreach (View v in Views.Values)
                {
                    if (v.Validated && v.Context != null)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        public static bool UserToggle()
        {
            userOff = !userOff;
            if (!userOff && !S.Enabled.Value)
            {
                Plugin.Log.LogWarning("Enabled=false in the config: the toggle key will not turn DLSS on.");
            }
            return Wanted;
        }

        public static void OnConfigReloaded()
        {
            disabledByError = false; // a config edit is a reason to try again
            if (deviceState == DeviceState.Ready && S.Preset.Value != appliedPreset)
            {
                ApplyPreset();
            }
            generation++; // recreate contexts: flags, mode or preset may have changed
            foreach (View v in Views.Values)
            {
                v.ForceMaxQuality = false;
            }
        }

        public static void Sweep()
        {
            foreach (KeyValuePair<TaaComponent, View> kv in Views)
            {
                if (kv.Value.Camera == null)
                {
                    Dead.Add(kv.Key);
                }
            }
            foreach (TaaComponent taa in Dead)
            {
                Release(taa);
            }
            Dead.Clear();
        }

        public static void Release(TaaComponent taa)
        {
            if (taa == null || !Views.TryGetValue(taa, out View v))
            {
                return;
            }
            Views.Remove(taa);
            ReleaseView(v);
            Verbose("DLSS context released");
        }

        public static void Shutdown()
        {
            foreach (View v in Views.Values)
            {
                ReleaseView(v);
            }
            Views.Clear();
            if (device != null && debugView != null)
            {
                try { device.DeleteDebugView(debugView); } catch { }
                debugView = null;
            }
        }

        // ---------------------------------------------------------------- jitter (OnPreCull)

        public static bool TrySetProjectionMatrix(TaaComponent taa, Func<Vector2, Matrix4x4> jitteredFunc)
        {
            try
            {
                View v = GetView(taa);
                if (!Wanted || !EnsureDevice())
                {
                    if (v.WasActive)
                    {
                        // Back to the game's TAA: its history is stale.
                        ResetHistoryRef(taa) = true;
                        v.WasActive = false;
                    }
                    v.JitteredThisFrame = false;
                    v.UpscaleThisFrame = false;
                    return false;
                }
                if (!v.WasActive)
                {
                    v.WasActive = true;
                    v.Reset = true;
                }

                PostProcessingContext ctx = taa.context;
                Camera cam = ctx.camera;
                v.Camera = cam;
                Upscale.NoteDlssFrame();
                RenderTexture lowRt = Upscale.LowRt;
                v.UpscaleThisFrame = lowRt != null && ReferenceEquals(cam.targetTexture, lowRt) && Upscale.OutWidth > 0;

                // NVIDIA: at least 8 x (output / input)^2 phases.
                int phases = Mathf.Clamp(S.JitterPhases.Value, 1, 1024);
                if (v.UpscaleThisFrame)
                {
                    float ratio = (float)Upscale.OutWidth / Mathf.Max(1, ctx.width);
                    phases = Mathf.Clamp(Mathf.Max(phases, Mathf.CeilToInt(8f * ratio * ratio)), 1, 1024);
                }
                if (v.SampleIndex >= phases)
                {
                    v.SampleIndex = 0;
                }
                int index = v.SampleIndex + 1; // Halton(0) = 0, start at 1 like HDRP
                v.SampleIndex++;
                var jitter = new Vector2(Halton(index, 2) - 0.5f, Halton(index, 3) - 0.5f); // in pixels, [-0.5, 0.5)

                cam.nonJitteredProjectionMatrix = cam.projectionMatrix;
                if (jitteredFunc != null)
                {
                    cam.projectionMatrix = jitteredFunc(jitter);
                }
                else
                {
                    cam.projectionMatrix = cam.orthographic ? OrthographicMatrix(taa, jitter) : PerspectiveMatrix(taa, jitter);
                }
                cam.useJitteredProjectionMatrixForTransparentRendering = true;

                // jitterVector is read by DepthOfField; in screen fractions, as in the original.
                SetJitterVector(taa, new Vector2(jitter.x / ctx.width, jitter.y / ctx.height));
                v.JitterPixels = jitter;
                v.JitteredThisFrame = true;
                return true;
            }
            catch (Exception e)
            {
                Fail("Error while setting jitter", e);
                return false;
            }
        }

        // ---------------------------------------------------------------- pass (OnRenderImage)

        /// <summary>Called from the OnRenderImage transpiler: the texture the TAA pass (DLSS in our case) writes to.</summary>
        public static RenderTexture TaaDestination(RenderTextureFactory factory, RenderTexture source, PostProcessingBehaviour behaviour)
        {
            TaaComponent taa = behaviour != null ? BehaviourTaa(behaviour) : null;
            if (taa != null && Views.TryGetValue(taa, out View v) && v.JitteredThisFrame && v.UpscaleThisFrame &&
                Upscale.OutWidth > 0 && (Upscale.OutWidth != source.width || Upscale.OutHeight != source.height))
            {
                return factory.Get(Upscale.OutWidth, Upscale.OutHeight, 0, source.format,
                    source.sRGB ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear, source.filterMode, source.wrapMode);
            }
            return factory.Get(source);
        }

        /// <summary>Called from the OnRenderImage transpiler: where to write the final frame. When upscaling, the full-size Upscale.FullRt.</summary>
        public static RenderTexture FinalDestination(RenderTexture destination, PostProcessingBehaviour behaviour)
        {
            RenderTexture lowRt = Upscale.LowRt;
            if (destination == null || lowRt == null || !ReferenceEquals(destination, lowRt) || behaviour == null)
            {
                return destination;
            }
            TaaComponent taa = BehaviourTaa(behaviour);
            if (taa == null || !Views.TryGetValue(taa, out View v) || v.UpscaledFrame != Time.frameCount)
            {
                return destination;
            }
            return Upscale.GetFullRt();
        }

        public static bool TryRender(TaaComponent taa, RenderTexture source, RenderTexture destination)
        {
            bool upscale = destination.width != source.width || destination.height != source.height;
            if (!Views.TryGetValue(taa, out View v) || !v.JitteredThisFrame)
            {
                return Fallback(source, destination, upscale);
            }
            v.JitteredThisFrame = false;
            if (BenchNoAa)
            {
                Graphics.Blit(source, destination);
                return true;
            }
            if (upscale)
            {
                v.UpscaledFrame = Time.frameCount; // post-processing continues at full resolution, so the final frame goes there too
            }
            if (!Wanted || deviceState != DeviceState.Ready)
            {
                return Fallback(source, destination, upscale);
            }

            try
            {
                if (cmd == null)
                {
                    cmd = new CommandBuffer { name = "DLSS" };
                }
                cmd.Clear();

                if (!ResolveBuffers(v, source, out Texture depth, out Texture motion))
                {
                    return Fallback(source, destination, upscale);
                }

                if (v.Context != null && !v.Validated && !CheckValidity(v))
                {
                    // context rejected: TAA (or stretching) for this frame, a retry or fallback from the next one
                    return Fallback(source, destination, upscale);
                }

                EnsureContext(v, source, destination.width, destination.height);
                if (v.Context == null)
                {
                    return Fallback(source, destination, upscale);
                }
                EnsureOutput(v, source.format, destination.width, destination.height);

                bool gameReset = ResetHistoryRef(taa);
                ref DLSSCommandExecutionData exec = ref v.Context.executeData;
                exec.reset = (v.Reset || gameReset) ? 1 : 0;
                exec.sharpness = 0f; // DLSS 3.7 ignores sharpness; we sharpen ourselves
                exec.mvScaleX = S.MotionScaleX.Value * v.Width;
                exec.mvScaleY = S.MotionScaleY.Value * v.Height;
                exec.jitterOffsetX = S.JitterSignX.Value * v.JitterPixels.x;
                exec.jitterOffsetY = S.JitterSignY.Value * v.JitterPixels.y;
                exec.preExposure = 1f;
                exec.subrectOffsetX = 0u;
                exec.subrectOffsetY = 0u;
                exec.subrectWidth = (uint)v.Width;
                exec.subrectHeight = (uint)v.Height;
                exec.invertXAxis = S.InvertXAxis.Value ? 1u : 0u;
                exec.invertYAxis = S.InvertYAxis.Value ? 1u : 0u;

                var textures = new DLSSTextureTable
                {
                    colorInput = source,
                    colorOutput = v.Output,
                    depth = depth,
                    motionVectors = motion
                };
                device.ExecuteDLSS(cmd, v.Context, in textures);
                Graphics.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                v.Reset = false;
                ResetHistoryRef(taa) = false;

                if (!v.Validated)
                {
                    // Until the context is confirmed, the DLSS output may be empty; show the game's TAA (or stretching).
                    return Fallback(source, destination, upscale);
                }

                Sharpen(v.Output, destination, v.Camera != null && v.Camera.orthographic);
                v.LastRenderFrame = Time.frameCount;
                return true;
            }
            catch (Exception e)
            {
                Fail("Error in the DLSS pass", e);
                return Fallback(source, destination, upscale);
            }
        }

        /// <summary>
        /// false: let the game's TAA run. But when upscaling, input and output differ in size and TAA cannot handle that,
        /// so we just stretch the frame and skip TAA.
        /// </summary>
        private static bool Fallback(RenderTexture source, RenderTexture destination, bool upscale)
        {
            if (!upscale)
            {
                return false;
            }
            Graphics.Blit(source, destination);
            return true;
        }

        // ---------------------------------------------------------------- device

        private static bool EnsureDevice()
        {
            if (deviceState == DeviceState.Ready)
            {
                return true;
            }
            if (deviceState == DeviceState.Failed)
            {
                return false;
            }
            deviceState = DeviceState.Failed;
            try
            {
                Plugin.Log.LogInfo($"GPU: {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceVendor}), API {SystemInfo.graphicsDeviceType}");
                if (SystemInfo.graphicsDeviceVendor.IndexOf("nvidia", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    Plugin.Log.LogWarning("GPU is not NVIDIA; DLSS unavailable, keeping the game's TAA.");
                    return false;
                }
                if (!NVUnityPlugin.IsLoaded() && !NVUnityPlugin.Load())
                {
                    Plugin.Log.LogWarning("NVUnityPlugin.dll failed to load (missing next to Pathologic.exe?); keeping the game's TAA.");
                    return false;
                }
                uint version = GraphicsDevice.version;
                if (version != ExpectedDeviceVersion)
                {
                    Plugin.Log.LogWarning($"NVUnityPlugin version {version}, expected {ExpectedDeviceVersion}. Trying anyway.");
                }
                device = GraphicsDevice.device ?? GraphicsDevice.CreateGraphicsDevice();
                if (device == null)
                {
                    Plugin.Log.LogWarning("GraphicsDevice.CreateGraphicsDevice() returned null (NGX failed to initialize); keeping the game's TAA.");
                    return false;
                }
                if (!device.IsFeatureAvailable(GraphicsDeviceFeature.DLSS))
                {
                    Plugin.Log.LogWarning("DLSS unavailable: no nvngx_dlss.dll next to Pathologic.exe, an old driver or an unsupported GPU; keeping the game's TAA.");
                    return false;
                }
                debugView = device.CreateDebugView();
                device.UpdateDebugView(debugView);
                NgxVersion = debugView.ngxVersion;
                Plugin.Log.LogInfo($"DLSS available: NVUnityPlugin device version {version}, NGX SDK {FormatNgxVersion(debugView.ngxVersion)}, " +
                                   $"nvngx_dlss.dll {DlssDllVersionText} ({dlssDllPath ?? "not found"})");
                deviceState = DeviceState.Ready;
                ApplyPreset();
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("DLSS unavailable (" + e.GetType().Name + ": " + e.Message + "); keeping the game's TAA.");
                return false;
            }
        }

        private static void ApplyPreset()
        {
            string preset = S.Preset.Value;
            PresetPatcher.Apply(preset);
            appliedPreset = preset;
        }

        // ---------------------------------------------------------------- DLSS context

        private static void EnsureContext(View v, RenderTexture source, int outWidth, int outHeight)
        {
            bool upscale = outWidth != source.width || outHeight != source.height;
            DLSSQuality quality = upscale ? UpscaleQuality(v, source.width, source.height, outWidth, outHeight) : DesiredQuality(v);
            DLSSFeatureFlags flags = DLSSFeatureFlags.MVLowRes;
            if (IsHdr(source.format))
            {
                flags |= DLSSFeatureFlags.IsHDR;
            }
            if (SystemInfo.usesReversedZBuffer)
            {
                flags |= DLSSFeatureFlags.DepthInverted;
            }
            if (S.AutoExposure.Value)
            {
                flags |= FlagAutoExposure;
            }

            if (v.Context != null && v.Width == source.width && v.Height == source.height && v.OutWidth == outWidth &&
                v.OutHeight == outHeight && v.Quality == quality && v.Flags == flags && v.Generation == generation)
            {
                return;
            }

            if (v.Context != null)
            {
                device.DestroyFeature(cmd, v.Context);
                v.Context = null;
            }

            var init = new DLSSCommandInitializationData
            {
                inputRTWidth = (uint)source.width,
                inputRTHeight = (uint)source.height,
                outputRTWidth = (uint)outWidth,
                outputRTHeight = (uint)outHeight,
                quality = quality,
                featureFlags = flags
            };
            v.Context = device.CreateFeature(cmd, in init);
            v.Width = source.width;
            v.Height = source.height;
            v.OutWidth = outWidth;
            v.OutHeight = outHeight;
            v.Quality = quality;
            v.Flags = flags;
            v.Generation = generation;
            v.CreatedFrame = Time.frameCount;
            v.Validated = false;
            v.Reset = true;
            v.Slot = v.Context != null && FeatureSlotProperty != null ? (uint)FeatureSlotProperty.GetValue(v.Context, null) : uint.MaxValue;

            Plugin.Log.LogInfo($"Creating DLSS context: {source.width}x{source.height} -> {outWidth}x{outHeight}, mode {QualityName(quality)}, " +
                               $"flags 0x{(int)flags:X} ({flags}), preset {appliedPreset}, slot {v.Slot}" +
                               (v.Context == null ? "; CreateFeature returned null" : ""));
        }

        /// <summary>
        /// DLSS mode for upscaling: among the modes whose valid input range (GetOptimalSettings) contains our input,
        /// take the one with the closest "native" resolution. The mode affects the default preset and the size checks in NGX.
        /// </summary>
        private static DLSSQuality UpscaleQuality(View v, int inW, int inH, int outW, int outH)
        {
            string key = $"{inW}x{inH}>{outW}x{outH}";
            if (v.UpscaleKey == key)
            {
                return v.UpscaleQuality;
            }
            DLSSQuality[] modes =
            {
                (DLSSQuality)4, // UltraQuality, missing from Unity's enum
                DLSSQuality.MaximumQuality, DLSSQuality.Balanced, DLSSQuality.MaximumPerformance, DLSSQuality.UltraPerformance
            };
            DLSSQuality best = DLSSQuality.MaximumQuality;
            float bestDiff = float.MaxValue;
            bool bestFits = false;
            var ranges = new List<string>();
            foreach (DLSSQuality q in modes)
            {
                if (!device.GetOptimalSettings((uint)outW, (uint)outH, q, out OptimalDLSSSettingsData o) || o.outRenderWidth == 0)
                {
                    continue;
                }
                ranges.Add($"{QualityName(q)} {o.outRenderWidth}x{o.outRenderHeight} [{o.minWidth}x{o.minHeight}..{o.maxWidth}x{o.maxHeight}]");
                bool fits = inW >= o.minWidth && inW <= o.maxWidth && inH >= o.minHeight && inH <= o.maxHeight;
                float diff = Mathf.Abs((float)o.outRenderWidth - inW);
                if ((fits && (!bestFits || diff < bestDiff)) || (!bestFits && !fits && diff < bestDiff))
                {
                    best = q;
                    bestDiff = diff;
                    bestFits = fits;
                }
            }
            Plugin.Log.LogInfo($"DLSS modes for output {outW}x{outH}: {string.Join("; ", ranges.ToArray())}. " +
                               $"Input {inW}x{inH} -> {QualityName(best)}" + (bestFits ? "" : " (input outside all ranges!)"));
            v.UpscaleKey = key;
            v.UpscaleQuality = best;
            return best;
        }

        private static string snapKey;
        private static int snapW, snapH;

        /// <summary>
        /// Fits the input to the NGX mode ranges (GetOptimalSettings). In DLSS 3.7 at 2560x1440, Quality/Balanced/Performance
        /// accept 1280x720..2560x1440 while Ultra Performance accepts exactly 853x480 only; there is a gap in between.
        /// </summary>
        public static void SnapInputSize(int outW, int outH, ref int w, ref int h)
        {
            string key = $"{outW}x{outH}:{w}x{h}";
            if (key == snapKey)
            {
                w = snapW;
                h = snapH;
                return;
            }
            int bestW = w, bestH = h;
            if (Snap(outW, outH, ref bestW, ref bestH))
            {
                Plugin.Log.LogWarning($"Input {w}x{h} is outside the DLSS ranges for {outW}x{outH}; using the nearest valid {bestW}x{bestH}.");
            }
            snapKey = key;
            snapW = bestW;
            snapH = bestH;
            w = bestW;
            h = bestH;
        }

        /// <summary>For the settings window: the resulting input size, without caching or logging. true if the size was adjusted.</summary>
        public static bool PreviewInputSize(int outW, int outH, ref int w, ref int h)
        {
            return Snap(outW, outH, ref w, ref h);
        }

        private static bool Snap(int outW, int outH, ref int w, ref int h)
        {
            if (deviceState != DeviceState.Ready)
            {
                return false;
            }
            int bestW = w, bestH = h;
            DLSSQuality[] modes = { DLSSQuality.MaximumQuality, DLSSQuality.Balanced, DLSSQuality.MaximumPerformance, DLSSQuality.UltraPerformance };
            long bestDist = long.MaxValue;
            foreach (DLSSQuality q in modes)
            {
                if (!device.GetOptimalSettings((uint)outW, (uint)outH, q, out OptimalDLSSSettingsData o) || o.maxWidth == 0 || o.minWidth == 0)
                {
                    continue;
                }
                int cw = Mathf.Clamp(w, (int)o.minWidth, (int)o.maxWidth);
                int ch = Mathf.Clamp(h, (int)o.minHeight, (int)o.maxHeight);
                long dist = Math.Abs((long)cw - w) + Math.Abs((long)ch - h);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestW = cw;
                    bestH = ch;
                }
            }
            bool snapped = bestW != w || bestH != h;
            w = bestW;
            h = bestH;
            return snapped;
        }

        /// <summary>Status line for the settings window.</summary>
        public static string Status
        {
            get
            {
                if (!S.Enabled.Value)
                {
                    return "Off: the game's TAA is running.";
                }
                if (userOff)
                {
                    return $"Turned off with {S.ToggleKey.Value}: the game's TAA is running.";
                }
                if (deviceState == DeviceState.Failed)
                {
                    return "DLSS unavailable (details in BepInEx\\LogOutput.log): the game's TAA is running.";
                }
                if (disabledByError)
                {
                    return "DLSS error (see BepInEx\\LogOutput.log): the game's TAA is running. Any settings change triggers a retry.";
                }
                View best = null;
                foreach (View v in Views.Values)
                {
                    if (v.Context != null && v.Validated && (best == null || v.LastRenderFrame > best.LastRenderFrame))
                    {
                        best = v;
                    }
                }
                if (best == null || Time.frameCount - best.LastRenderFrame > 30)
                {
                    return "DLSS is ready but there are no TAA frames. Is anti-aliasing enabled in the game settings?";
                }
                return $"DLSS running: {best.Width}x{best.Height} -> {best.OutWidth}x{best.OutHeight}, {PrettyMode(best.Quality)}, " +
                       $"model {appliedPreset}, nvngx_dlss.dll {DlssDllVersionText}";
            }
        }

        /// <summary>NGX SDK version NVUnityPlugin was built with (3.7.20), not the version of nvngx_dlss.dll.</summary>
        public static uint NgxVersion { get; private set; }

        private static Version dlssDllVersion;
        private static string dlssDllPath;
        private static bool dlssDllVersionRead;
        private static bool dlssDllFromModule;

        /// <summary>
        /// Version of nvngx_dlss.dll: the loaded module if NGX has loaded it, otherwise the file next to Pathologic.exe.
        /// The set of presets depends on it (310.x: A-D removed, J-M added).
        /// </summary>
        public static Version DlssDllVersion
        {
            get
            {
                if (!dlssDllVersionRead)
                {
                    dlssDllVersionRead = true;
                    dlssDllFromModule = ReadDlssDllVersion(out dlssDllVersion, out dlssDllPath);
                }
                return dlssDllVersion;
            }
        }

        /// <summary>Called once a context is valid: NGX has loaded nvngx_dlss.dll by then.</summary>
        private static void RecheckDlssDllVersion()
        {
            if (dlssDllFromModule)
            {
                return;
            }
            dlssDllVersionRead = false;
            Version before = dlssDllVersion;
            if (DlssDllVersion != null && !DlssDllVersion.Equals(before) || dlssDllFromModule)
            {
                Plugin.Log.LogInfo($"nvngx_dlss.dll in use: {DlssDllVersionText} ({dlssDllPath})");
            }
            dlssDllFromModule = true; // do not enumerate modules again even if it was not found
        }

        public static string DlssDllVersionText => DlssDllVersion != null ? DlssDllVersion.ToString(3) : "?";

        private static bool ReadDlssDllVersion(out Version version, out string path)
        {
            version = null;
            path = null;
            try
            {
                foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
                {
                    if (string.Equals(m.ModuleName, "nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        path = m.FileName;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("Process modules: " + e.Message);
            }
            bool loaded = path != null;
            if (!loaded)
            {
                string file = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath) ?? "", "nvngx_dlss.dll");
                if (System.IO.File.Exists(file))
                {
                    path = file;
                }
            }
            if (path == null)
            {
                return false;
            }
            try
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("nvngx_dlss.dll version: " + e.Message);
            }
            // The file next to the exe is only a guess (the driver may load its own copy); see RecheckDlssDllVersion.
            return loaded;
        }

        private static string PrettyMode(DLSSQuality q)
        {
            switch ((int)q)
            {
                case 5: return "DLAA";
                case 4: return "Ultra Quality";
                case 2: return "Quality";
                case 1: return "Balanced";
                case 0: return "Performance";
                case 3: return "Ultra Performance";
                default: return q.ToString();
            }
        }

        private static DLSSQuality DesiredQuality(View v)
        {
            switch (S.Mode.Value)
            {
                case QualityModeOption.DLAA:
                    return QualityDlaa;
                case QualityModeOption.MaximumQuality:
                    return DLSSQuality.MaximumQuality;
                default:
                    return v.ForceMaxQuality ? DLSSQuality.MaximumQuality : QualityDlaa;
            }
        }

        /// <summary>Self-check via the NVUnityPlugin debug view. false if the context was rejected.</summary>
        private static bool CheckValidity(View v)
        {
            if (debugView == null || v.Slot == uint.MaxValue)
            {
                v.Validated = true;
                return true;
            }
            device.UpdateDebugView(debugView);
            foreach (DLSSDebugFeatureInfos info in debugView.dlssFeatureInfos)
            {
                if (info.featureSlot != v.Slot)
                {
                    continue;
                }
                DLSSCommandInitializationData init = info.initData;
                if (info.validFeature)
                {
                    v.Validated = true;
                    Plugin.Log.LogInfo($"DLSS context is valid (validFeature = true): {init.inputRTWidth}x{init.inputRTHeight} -> " +
                                       $"{init.outputRTWidth}x{init.outputRTHeight}, mode {QualityName(init.quality)}, flags 0x{(int)init.featureFlags:X}, " +
                                       $"after {Time.frameCount - v.CreatedFrame} frame(s)");
                    RecheckDlssDllVersion();
                    return true;
                }
                if (Time.frameCount - v.CreatedFrame < 3)
                {
                    return true; // the render thread may not have created the feature yet
                }
                OnInvalid(v, "validFeature = false");
                return false;
            }
            if (Time.frameCount - v.CreatedFrame > ValidationTimeoutFrames)
            {
                v.Validated = true;
                Plugin.Log.LogWarning($"Slot {v.Slot} not found in the NVUnityPlugin debug view within {ValidationTimeoutFrames} frames; " +
                                      "could not validate the context, assuming it works.");
            }
            return true;
        }

        private static void OnInvalid(View v, string why)
        {
            device.DestroyFeature(cmd, v.Context);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            v.Context = null;

            if (S.Mode.Value == QualityModeOption.Auto && !v.ForceMaxQuality)
            {
                v.ForceMaxQuality = true;
                Plugin.Log.LogWarning($"DLSS context in DLAA (5) mode was not created ({why}). Trying MaximumQuality at the same resolution.");
            }
            else
            {
                disabledByError = true;
                Plugin.Log.LogError($"DLSS context was not created ({why}, mode {QualityName(v.Quality)}). DLSS is off, the game's TAA is running. " +
                                    "It will be retried after a config edit.");
            }
        }

        // ---------------------------------------------------------------- textures

        private static bool ResolveBuffers(View v, RenderTexture source, out Texture depth, out Texture motion)
        {
            BufferSourceOption mode = S.Buffers.Value;
            depth = null;
            motion = null;
            string why = null;
            if (mode != BufferSourceOption.Copy)
            {
                depth = Shader.GetGlobalTexture(DepthTexId);
                motion = Shader.GetGlobalTexture(MotionTexId);
                if (depth == null || motion == null)
                {
                    why = $"global textures: depth {(depth == null ? "missing" : "present")}, motion vectors {(motion == null ? "missing" : "present")}";
                }
                else if (depth.width != source.width || depth.height != source.height || motion.width != source.width || motion.height != source.height)
                {
                    why = $"sizes differ: color {source.width}x{source.height}, depth {depth.width}x{depth.height}, motion vectors {motion.width}x{motion.height}";
                }
                else
                {
                    LogBuffersOnce(v, source, depth, motion, "global");
                    return true;
                }
                if (mode == BufferSourceOption.Global)
                {
                    if (!v.BuffersLogged)
                    {
                        v.BuffersLogged = true;
                        Plugin.Log.LogWarning("Buffers=Global, but " + why + ". Keeping the game's TAA (try Buffers=Auto or Copy).");
                    }
                    return false;
                }
            }

            v.DepthCopy = EnsureRt(v.DepthCopy, source.width, source.height, RenderTextureFormat.RFloat, false, "DLSS Depth");
            v.MotionCopy = EnsureRt(v.MotionCopy, source.width, source.height, RenderTextureFormat.RGHalf, false, "DLSS Motion");
            cmd.Blit(BuiltinRenderTextureType.Depth, v.DepthCopy);
            cmd.Blit(BuiltinRenderTextureType.MotionVectors, v.MotionCopy);
            depth = v.DepthCopy;
            motion = v.MotionCopy;
            LogBuffersOnce(v, source, depth, motion, "copies" + (why != null ? " (" + why + ")" : ""));
            return true;
        }

        private static void LogBuffersOnce(View v, RenderTexture source, Texture depth, Texture motion, string kind)
        {
            if (v.BuffersLogged)
            {
                return;
            }
            v.BuffersLogged = true;
            Plugin.Log.LogInfo($"Buffers ({kind}): color {Describe(source)}, depth {Describe(depth)}, motion vectors {Describe(motion)}; " +
                               $"reversed Z {SystemInfo.usesReversedZBuffer}");
        }

        private static void EnsureOutput(View v, RenderTextureFormat format, int width, int height)
        {
            if (v.Output != null && v.Output.width == width && v.Output.height == height &&
                (v.Output.format == format || v.Output.format == RenderTextureFormat.ARGBHalf))
            {
                return;
            }
            v.Output = EnsureRt(v.Output, width, height, format, true, "DLSS Output");
            if (!v.Output.IsCreated())
            {
                Plugin.Log.LogWarning($"Failed to create a UAV output texture in format {format}; using ARGBHalf.");
                v.Output = EnsureRt(v.Output, width, height, RenderTextureFormat.ARGBHalf, true, "DLSS Output");
            }
        }

        private static RenderTexture EnsureRt(RenderTexture rt, int w, int h, RenderTextureFormat format, bool uav, string name)
        {
            if (rt != null && rt.width == w && rt.height == h && rt.format == format)
            {
                return rt;
            }
            DestroyRt(rt);
            rt = new RenderTexture(w, h, 0, format, RenderTextureReadWrite.Linear)
            {
                name = name,
                enableRandomWrite = uav,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                hideFlags = HideFlags.HideAndDontSave
            };
            rt.Create();
            return rt;
        }

        private static void DestroyRt(RenderTexture rt)
        {
            if (rt != null)
            {
                rt.Release();
                UnityEngine.Object.Destroy(rt);
            }
        }

        // ---------------------------------------------------------------- sharpening

        /// <summary>
        /// The game's TAA shader with zero blend weights and zero _Jitter: no history is blended in,
        /// only its sharpening filter remains (_SharpenParameters.x, same scale as TAA).
        /// </summary>
        private static void Sharpen(RenderTexture src, RenderTexture dst, bool orthographic)
        {
            float sharpness = Mathf.Clamp(S.Sharpness.Value, 0f, 3f);
            if (sharpness <= 0.0001f || !EnsureSharpenMaterial())
            {
                Graphics.Blit(src, dst);
                return;
            }
            sharpenMaterial.shaderKeywords = null;
            sharpenMaterial.SetVector(JitterId, Vector4.zero);
            sharpenMaterial.SetVector(SharpenId, new Vector4(sharpness, 0f, 0f, 0f));
            sharpenMaterial.SetVector(FinalBlendId, new Vector4(0f, 0f, 6000f, 0f));
            sharpenMaterial.SetTexture(HistoryId, src);
            Graphics.Blit(src, dst, sharpenMaterial, orthographic ? 1 : 0);
        }

        private static bool EnsureSharpenMaterial()
        {
            if (sharpenMaterial != null)
            {
                return true;
            }
            if (sharpenMissing)
            {
                return false;
            }
            Shader shader = Shader.Find("Hidden/Post FX/Temporal Anti-aliasing");
            if (shader == null || !shader.isSupported)
            {
                sharpenMissing = true;
                Plugin.Log.LogWarning("Shader 'Hidden/Post FX/Temporal Anti-aliasing' not found; sharpening is not applied.");
                return false;
            }
            sharpenMaterial = new Material(shader) { name = "DLSS Sharpen", hideFlags = HideFlags.HideAndDontSave };
            return true;
        }

        // ---------------------------------------------------------------- helpers

        private static View GetView(TaaComponent taa)
        {
            if (!Views.TryGetValue(taa, out View v))
            {
                v = new View();
                Views.Add(taa, v);
            }
            return v;
        }

        private static void ReleaseView(View v)
        {
            if (v.Context != null && device != null)
            {
                try
                {
                    if (cmd == null)
                    {
                        cmd = new CommandBuffer { name = "DLSS" };
                    }
                    cmd.Clear();
                    device.DestroyFeature(cmd, v.Context);
                    Graphics.ExecuteCommandBuffer(cmd);
                    cmd.Clear();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("DestroyFeature: " + e.Message);
                }
            }
            v.Context = null;
            DestroyRt(v.Output);
            DestroyRt(v.DepthCopy);
            DestroyRt(v.MotionCopy);
            v.Output = v.DepthCopy = v.MotionCopy = null;
        }

        private static void Fail(string what, Exception e)
        {
            disabledByError = true;
            Plugin.Log.LogError(what + ": " + e + "\nDLSS is off, the game's TAA is running. It will be retried after a config edit.");
        }

        private static void Verbose(string text)
        {
            if (S.Verbose.Value)
            {
                Plugin.Log.LogInfo(text);
            }
        }

        private static float Halton(int index, int radix)
        {
            float result = 0f;
            float fraction = 1f / radix;
            while (index > 0)
            {
                result += (index % radix) * fraction;
                index /= radix;
                fraction /= radix;
            }
            return result;
        }

        private static bool IsHdr(RenderTextureFormat f)
        {
            switch (f)
            {
                case RenderTextureFormat.ARGBHalf:
                case RenderTextureFormat.ARGBFloat:
                case RenderTextureFormat.RGB111110Float:
                case RenderTextureFormat.DefaultHDR:
                    return true;
                default:
                    return false;
            }
        }

        private static string Describe(Texture t)
        {
            if (t == null)
            {
                return "none";
            }
            var rt = t as RenderTexture;
            return rt != null
                ? $"'{rt.name}' {rt.width}x{rt.height} {rt.format}/{rt.graphicsFormat}{(rt.antiAliasing > 1 ? " MSAA" + rt.antiAliasing : "")}"
                : $"'{t.name}' {t.width}x{t.height} {t.graphicsFormat}";
        }

        private static string QualityName(DLSSQuality q)
        {
            return (int)q == 5 ? "DLAA(5)" : q + "(" + (int)q + ")";
        }

        private static string FormatNgxVersion(uint v)
        {
            return $"{v >> 16}.{(v >> 8) & 0xFF}.{v & 0xFF}"; // 0x30714 -> 3.7.20
        }
    }
}
