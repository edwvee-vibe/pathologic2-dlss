using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Pathologic2Dlss
{
    /// <summary>
    /// Debug.Benchmark: once the DLSS context is valid, cycles through the anti-aliasing states and logs the average
    /// frame time of each (and GPU time if FrameTimingManager works). Settings are changed in memory only and restored.
    /// </summary>
    internal static class Benchmark
    {
        private const int WarmupFrames = 120;
        private const int MeasureFrames = 600;

        private sealed class Step
        {
            public string Name;
            public bool Taa;     // the game's TAA
            public bool NoAa;    // neither TAA nor DLSS (plain copy)
            public string Preset;
            public float Sharpness;
        }

        private static List<Step> steps;
        private static int index = -1;
        private static int frame;
        private static double cpuSum, gpuSum, mainSum, renderSum;
        private static int gpuCount;
        private static readonly FrameTiming[] Timings = new FrameTiming[1];
        private static readonly StringBuilder Report = new StringBuilder();
        private static string savedPreset;
        private static float savedSharpness;
        private static bool done;

        public static bool Active => index >= 0 && !done;

        /// <summary>Called every Update by the plugin.</summary>
        public static void Tick()
        {
            if (done || !Plugin.S.Benchmark.Value)
            {
                return;
            }
            if (index < 0)
            {
                if (!DlssPass.HasValidContext)
                {
                    return;
                }
                Start();
            }

            FrameTimingManager.CaptureFrameTimings();
            frame++;
            if (frame > WarmupFrames)
            {
                cpuSum += Time.unscaledDeltaTime * 1000.0;
                if (FrameTimingManager.GetLatestTimings(1, Timings) > 0 && Timings[0].gpuFrameTime > 0)
                {
                    gpuSum += Timings[0].gpuFrameTime;
                    mainSum += Timings[0].cpuMainThreadFrameTime;
                    renderSum += Timings[0].cpuRenderThreadFrameTime;
                    gpuCount++;
                }
            }
            if (frame >= WarmupFrames + MeasureFrames)
            {
                Finish(steps[index]);
                Next();
            }
        }

        private static void Start()
        {
            savedPreset = Plugin.S.Preset.Value;
            savedSharpness = Plugin.S.Sharpness.Value;
            steps = new List<Step>
            {
                new Step { Name = "game TAA", Taa = true },
                new Step { Name = "no AA (plain copy)", NoAa = true },
                new Step { Name = "DLSS E (CNN), no sharpening", Preset = "E", Sharpness = 0f },
                new Step { Name = "DLSS K, no sharpening", Preset = "K", Sharpness = 0f },
                new Step { Name = "DLSS K, sharpening 0.5", Preset = "K", Sharpness = 0.5f },
                new Step { Name = "DLSS M, no sharpening", Preset = "M", Sharpness = 0f },
                new Step { Name = "game TAA (again)", Taa = true },
            };
            Report.Length = 0;
            Report.AppendLine($"Benchmark: {Screen.width}x{Screen.height}, RenderScale {Plugin.S.RenderScale.Value}, vSync {QualitySettings.vSyncCount}, " +
                              $"targetFrameRate {Application.targetFrameRate}, FrameTimingManager {FrameTimingManager.IsFeatureEnabled()}, " +
                              $"warmup {WarmupFrames}, measured {MeasureFrames} frames per step");
            Plugin.Log.LogInfo("Benchmark started.");
            Next();
        }

        private static void Next()
        {
            index++;
            frame = 0;
            cpuSum = gpuSum = mainSum = renderSum = 0;
            gpuCount = 0;
            if (index >= steps.Count)
            {
                Stop();
                return;
            }
            Step s = steps[index];
            DlssPass.UserOff = s.Taa;
            DlssPass.BenchNoAa = s.NoAa;
            if (s.Preset != null)
            {
                Plugin.S.Sharpness.Value = s.Sharpness;
                if (Plugin.S.Preset.Value != s.Preset)
                {
                    Plugin.S.Preset.Value = s.Preset;
                    DlssPass.OnConfigReloaded();
                }
            }
        }

        private static void Finish(Step s)
        {
            double cpu = cpuSum / MeasureFrames;
            string line = $"  {s.Name,-30} frame {cpu,6:0.000} ms ({1000.0 / cpu,5:0} FPS)";
            if (gpuCount > 0)
            {
                line += $", GPU {gpuSum / gpuCount,6:0.000} ms, main thread {mainSum / gpuCount:0.000} ms, render thread {renderSum / gpuCount:0.000} ms";
            }
            Report.AppendLine(line);
        }

        private static void Stop()
        {
            done = true;
            DlssPass.UserOff = false;
            DlssPass.BenchNoAa = false;
            Plugin.S.Sharpness.Value = savedSharpness;
            if (Plugin.S.Preset.Value != savedPreset)
            {
                Plugin.S.Preset.Value = savedPreset;
                DlssPass.OnConfigReloaded();
            }
            Plugin.Log.LogInfo(Report.ToString().TrimEnd());
            Plugin.Log.LogInfo("Benchmark finished.");
        }
    }
}
