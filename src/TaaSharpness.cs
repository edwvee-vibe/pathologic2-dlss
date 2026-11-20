using UnityEngine.PostProcessing;

namespace Pathologic2Dlss
{
    /// <summary>
    /// Sharpness of the game's own TAA (when it runs instead of DLSS: F8, Enabled = false, no DLSS).
    /// TaaComponent.Render reads model.settings.taaSettings.sharpen into _SharpenParameters. The model may be a shared
    /// profile asset, so the value is replaced only for the duration of Render and restored right after it.
    /// </summary>
    internal static class TaaSharpness
    {
        /// <summary>The game's own value, as last seen in the TAA model (-1 until TAA has run once).</summary>
        public static float GameValue { get; private set; } = -1f;

        private static AntialiasingModel patchedModel;
        private static float savedValue;

        /// <summary>Configured override, or a negative number for "use the game's value".</summary>
        public static float Override => Plugin.S.TaaSharpness.Value;

        /// <summary>Prefix part: called right before the original TaaComponent.Render runs.</summary>
        public static void Apply(TaaComponent taa)
        {
            patchedModel = null;
            AntialiasingModel model = taa?.model;
            if (model == null)
            {
                return;
            }
            AntialiasingModel.Settings settings = model.settings;
            GameValue = settings.taaSettings.sharpen;
            float want = Override;
            if (want < 0f || want == settings.taaSettings.sharpen)
            {
                return;
            }
            savedValue = settings.taaSettings.sharpen;
            settings.taaSettings.sharpen = want > 3f ? 3f : want;
            model.settings = settings;
            patchedModel = model;
        }

        /// <summary>Postfix part: put the game's value back.</summary>
        public static void Restore()
        {
            if (patchedModel == null)
            {
                return;
            }
            AntialiasingModel.Settings settings = patchedModel.settings;
            settings.taaSettings.sharpen = savedValue;
            patchedModel.settings = settings;
            patchedModel = null;
        }
    }
}
