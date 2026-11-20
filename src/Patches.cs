using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.PostProcessing;

namespace Pathologic2Dlss
{
    /// <summary>
    /// The hook point is TaaComponent from Post Processing Stack v1 (in the game's Assembly-CSharp).
    /// PostProcessingBehaviour.OnPreCull calls SetProjectionMatrix (jitter), OnRenderImage calls Render (the TAA pass itself).
    /// A prefix returning false replaces the original; true lets the original TAA run as usual.
    /// </summary>
    internal static class Patches
    {
        [HarmonyPatch(typeof(TaaComponent), nameof(TaaComponent.SetProjectionMatrix))]
        [HarmonyPrefix]
        private static bool SetProjectionMatrixPrefix(TaaComponent __instance, Func<Vector2, Matrix4x4> jitteredFunc)
        {
            return !DlssPass.TrySetProjectionMatrix(__instance, jitteredFunc);
        }

        [HarmonyPatch(typeof(TaaComponent), nameof(TaaComponent.Render))]
        [HarmonyPrefix]
        private static bool RenderPrefix(TaaComponent __instance, RenderTexture source, RenderTexture destination)
        {
            if (DlssPass.TryRender(__instance, source, destination))
            {
                return false;
            }
            TaaSharpness.Apply(__instance); // the game's TAA runs: optional sharpness override
            return true;
        }

        [HarmonyPatch(typeof(TaaComponent), nameof(TaaComponent.Render))]
        [HarmonyPostfix]
        private static void RenderPostfix()
        {
            TaaSharpness.Restore();
        }

        [HarmonyPatch(typeof(TaaComponent), nameof(TaaComponent.OnDisable))]
        [HarmonyPostfix]
        private static void OnDisablePostfix(TaaComponent __instance)
        {
            DlssPass.Release(__instance);
        }

        // The behaviour's OnDisable calls TaaComponent.OnDisable only when the model is enabled; release in the other cases too.
        [HarmonyPatch(typeof(PostProcessingBehaviour), "OnDisable")]
        [HarmonyPrefix]
        private static void BehaviourOnDisablePrefix(PostProcessingBehaviour __instance)
        {
            TaaComponent taa = DlssPass.BehaviourTaa(__instance);
            if (taa != null)
            {
                DlssPass.Release(taa);
            }
        }

        // ------------------------------------------------------------ settings window

        // The game sets Cursor.visible/lockState only here (from its own visible/free fields).
        // While the mod window is open the cursor is visible and unlocked; the game's fields stay untouched so they can be restored.
        [HarmonyPatch(typeof(InputServices.WindowsCursorController), "UpdateCursor")]
        [HarmonyPostfix]
        private static void UpdateCursorPostfix()
        {
            if (Menu.IsOpen)
            {
                Menu.ForceCursor();
            }
        }

        // ------------------------------------------------------------ upscaling

        [HarmonyPatch(typeof(DynamicResolution), nameof(DynamicResolution.SetScale))]
        [HarmonyPrefix]
        private static void SetScalePrefix(float value)
        {
            Upscale.NoteGameScale(value);
        }

        [HarmonyPatch(typeof(DynamicResolution), nameof(DynamicResolution.GetTargetTexture))]
        [HarmonyPrefix]
        private static bool GetTargetTexturePrefix(DynamicResolution __instance, ref RenderTexture __result)
        {
            Upscale.OverrideGameScale(__instance);
            if (Upscale.TryGetTarget(out RenderTexture rt))
            {
                __result = rt;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(DynamicResolutionOutputCamera), "OnPreCull")]
        [HarmonyPostfix]
        private static void OutputCameraPostfix(DynamicResolutionOutputCamera __instance)
        {
            Upscale.FixOutputView(__instance);
        }

        /// <summary>
        /// In PostProcessingBehaviour.OnRenderImage:
        ///  - m_RenderTextureFactory.Get(renderTexture) before m_Taa.Render -> DlssPass.TaaDestination(factory, src, this):
        ///    when upscaling, the TAA output (i.e. DLSS) and all following post-processing get full resolution;
        ///  - every load of the destination argument -> DlssPass.FinalDestination(destination, this):
        ///    when upscaling, the final frame goes into a full-size texture instead of the camera's reduced target.
        /// </summary>
        [HarmonyPatch(typeof(PostProcessingBehaviour), "OnRenderImage")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> OnRenderImageTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo factoryGet = AccessTools.Method(typeof(RenderTextureFactory), nameof(RenderTextureFactory.Get), new[] { typeof(RenderTexture) });
            MethodInfo taaRender = AccessTools.Method(typeof(TaaComponent), nameof(TaaComponent.Render));
            MethodInfo taaDestination = AccessTools.Method(typeof(DlssPass), nameof(DlssPass.TaaDestination));
            MethodInfo finalDestination = AccessTools.Method(typeof(DlssPass), nameof(DlssPass.FinalDestination));

            var code = new List<CodeInstruction>(instructions);
            int getIndex = -1;
            for (int i = 0; i < code.Count && getIndex < 0; i++)
            {
                if (!code[i].Calls(factoryGet))
                {
                    continue;
                }
                for (int j = i + 1; j < Math.Min(code.Count, i + 8); j++)
                {
                    if (code[j].Calls(taaRender))
                    {
                        getIndex = i;
                        break;
                    }
                }
            }

            var result = new List<CodeInstruction>(code.Count + 16);
            int destinations = 0;
            for (int i = 0; i < code.Count; i++)
            {
                CodeInstruction ins = code[i];
                if (i == getIndex)
                {
                    // stack: factory, src -> factory, src, this -> TaaDestination(factory, src, this)
                    var self = new CodeInstruction(OpCodes.Ldarg_0);
                    self.labels.AddRange(ins.labels);
                    self.blocks.AddRange(ins.blocks);
                    result.Add(self);
                    result.Add(new CodeInstruction(OpCodes.Call, taaDestination));
                    continue;
                }
                result.Add(ins);
                if (ins.IsLdarg(2))
                {
                    result.Add(new CodeInstruction(OpCodes.Ldarg_0));
                    result.Add(new CodeInstruction(OpCodes.Call, finalDestination));
                    destinations++;
                }
            }

            if (getIndex < 0 || destinations == 0)
            {
                Plugin.Log.LogError($"OnRenderImage: pattern not found (Get before TAA: {getIndex >= 0}, destination: {destinations}); upscaling unavailable.");
                DlssPass.UpscaleUnavailable = true;
                return code;
            }
            Plugin.Log.LogInfo($"OnRenderImage patched: TAA output and {destinations} destination loads.");
            return result;
        }
    }
}
