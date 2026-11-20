using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Pathologic2Dlss
{
    [BepInPlugin(Guid, Name, Version)]
    [DefaultExecutionOrder(-32000)] // before the game's scripts, to block input while the window is open
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "pathologic2.dlss";
        public const string Name = "Pathologic 2 DLSS";
        public const string Version = "1.1.8";

        internal static ManualLogSource Log;
        internal static Settings S;

        private static Plugin instance;
        private static bool saveRequested;
        private DateTime configStamp;
        private float nextConfigCheck;
        private float nextSweep;
        private static string osdText;
        private static float osdUntil;
        private static GUIStyle osdStyle;

        private void Awake()
        {
            instance = this;
            Log = Logger;
            S = new Settings(Config);
            // The window changes values often (sliders), so we write the file ourselves on mouse release, not on every change.
            Config.SaveOnConfigSet = false;
            try { Config.Save(); } catch (Exception e) { Log.LogWarning("Failed to write config: " + e.Message); }
            configStamp = ConfigStamp();

            try
            {
                new Harmony(Guid).PatchAll(typeof(Patches));
            }
            catch (Exception e)
            {
                Log.LogError("Failed to apply TaaComponent patches, mod disabled: " + e);
                enabled = false;
                return;
            }

            Log.LogInfo($"{Name} {Version}: Enabled={S.Enabled.Value}, RenderScale={S.RenderScale.Value}, Sharpness={S.Sharpness.Value}, Preset={S.Preset.Value}, " +
                        $"Mode={S.Mode.Value}, AutoExposure={S.AutoExposure.Value}, JitterPhases={S.JitterPhases.Value}, " +
                        $"ToggleKey={S.ToggleKey.Value}, MenuKey={S.MenuKey.Value}");

            if (S.MenuOpenOnStart.Value)
            {
                Menu.SetOpen(true);
            }
        }

        private void Update()
        {
            bool menuWasOpen = Menu.IsOpen;
            if (!Menu.WaitingForKey)
            {
                if (IsPressed(S.MenuKey.Value))
                {
                    Menu.Toggle();
                }
                else if (Menu.IsOpen && Input.GetKeyDown(KeyCode.Escape))
                {
                    Menu.SetOpen(false);
                }

                if (IsPressed(S.ToggleKey.Value))
                {
                    ToggleDlss();
                }
            }
            if (menuWasOpen || Menu.IsOpen)
            {
                BlockGameInput(); // also in the frame where the window was closed by a key: Esc/F7 must not reach the game
            }

            if (saveRequested && GUIUtility.hotControl == 0)
            {
                saveRequested = false;
                CommitConfig();
            }

            float now = Time.unscaledTime;
            if (now >= nextConfigCheck)
            {
                nextConfigCheck = now + 1f;
                DateTime stamp = ConfigStamp();
                if (stamp != configStamp)
                {
                    try
                    {
                        Config.Reload();
                        Log.LogInfo($"Config reloaded: Enabled={S.Enabled.Value}, RenderScale={S.RenderScale.Value}, Sharpness={S.Sharpness.Value}, Preset={S.Preset.Value}, " +
                                    $"Mode={S.Mode.Value}, AutoExposure={S.AutoExposure.Value}, JitterPhases={S.JitterPhases.Value}");
                        DlssPass.OnConfigReloaded();
                    }
                    catch (Exception e)
                    {
                        Log.LogWarning("Failed to reload config: " + e.Message);
                    }
                    // Reload() may re-save the file itself, so take the timestamp after it, otherwise we loop.
                    configStamp = ConfigStamp();
                }
            }

            Benchmark.Tick();

            if (now >= nextSweep)
            {
                nextSweep = now + 5f;
                DlssPass.Sweep();
            }
        }

        private void LateUpdate()
        {
            if (Menu.IsOpen)
            {
                BlockGameInput();
            }
        }

        private void OnGUI()
        {
            GUI.depth = -1000;
            if (Menu.IsOpen)
            {
                BlockGameInput();
                Menu.Draw();
            }
            DrawMessage();
        }

        /// <summary>
        /// Own check instead of KeyboardShortcut.IsDown(): that one only counts a press when nothing else is held,
        /// so the mod's keys did not fire while moving (WASD, Shift). Here unrelated keys are ignored.
        /// </summary>
        internal static bool IsPressed(KeyboardShortcut shortcut)
        {
            if (shortcut.MainKey == KeyCode.None || !Input.GetKeyDown(shortcut.MainKey))
            {
                return false;
            }
            foreach (KeyCode modifier in shortcut.Modifiers)
            {
                if (!Input.GetKey(modifier))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Same as IsPressed, but for an IMGUI event (while the window is open, Input may be cleared by ResetInputAxes).</summary>
        internal static bool Matches(KeyboardShortcut shortcut, Event e)
        {
            if (shortcut.MainKey == KeyCode.None || e.keyCode != shortcut.MainKey)
            {
                return false;
            }
            foreach (KeyCode modifier in shortcut.Modifiers)
            {
                bool held;
                switch (modifier)
                {
                    case KeyCode.LeftControl: case KeyCode.RightControl: held = e.control; break;
                    case KeyCode.LeftShift: case KeyCode.RightShift: held = e.shift; break;
                    case KeyCode.LeftAlt: case KeyCode.RightAlt: case KeyCode.AltGr: held = e.alt; break;
                    default: held = Input.GetKey(modifier); break;
                }
                if (!held)
                {
                    return false;
                }
            }
            return true;
        }

        private static int lastToggleFrame = -1;

        /// <summary>Toggle DLSS/TAA (at most once per frame: both Update and OnGUI may catch the key).</summary>
        internal static void ToggleDlss()
        {
            if (lastToggleFrame == Time.frameCount)
            {
                return;
            }
            lastToggleFrame = Time.frameCount;
            bool on = DlssPass.UserToggle();
            float scale = Upscale.Scale;
            ShowMessage(on
                ? (scale < 0.999f ? $"DLSS: on, input {scale:0.###} of screen" : "DLAA: on")
                : "DLSS: off (game TAA)");
        }

        private static void BlockGameInput()
        {
            if (S.BlockGameInput.Value)
            {
                Input.ResetInputAxes();
            }
            if (Menu.IsOpen)
            {
                Menu.ForceCursor();
            }
        }

        /// <summary>Write the config (in Update, when no slider is being dragged). Used by the window.</summary>
        internal static void RequestSave()
        {
            saveRequested = true;
        }

        private void CommitConfig()
        {
            try
            {
                Config.Save();
            }
            catch (Exception e)
            {
                Log.LogWarning("Failed to write config: " + e.Message);
            }
            configStamp = ConfigStamp(); // our own write is no reason to reload the file
        }

        private void DrawMessage()
        {
            if (osdText == null || Time.unscaledTime > osdUntil || !S.ShowMessages.Value)
            {
                return;
            }
            if (osdStyle == null)
            {
                osdStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
                osdStyle.normal.textColor = Color.white;
            }
            var rect = new Rect(24, 24, 900, 40);
            var shadow = new Rect(rect.x + 2, rect.y + 2, rect.width, rect.height);
            Color c = osdStyle.normal.textColor;
            osdStyle.normal.textColor = Color.black;
            GUI.Label(shadow, osdText, osdStyle);
            osdStyle.normal.textColor = c;
            GUI.Label(rect, osdText, osdStyle);
        }

        private void OnDestroy()
        {
            if (saveRequested && instance == this)
            {
                CommitConfig();
            }
            DlssPass.Shutdown();
        }

        internal static void ShowMessage(string text)
        {
            osdText = text;
            osdUntil = Time.unscaledTime + 2.5f;
            Log.LogInfo(text);
        }

        private DateTime ConfigStamp()
        {
            try
            {
                return File.Exists(Config.ConfigFilePath) ? File.GetLastWriteTimeUtc(Config.ConfigFilePath) : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }
    }

    internal enum QualityModeOption
    {
        Auto,
        DLAA,
        MaximumQuality
    }

    internal enum BufferSourceOption
    {
        Auto,
        Global,
        Copy
    }

    internal sealed class Settings
    {
        public readonly ConfigEntry<bool> Enabled;
        public readonly ConfigEntry<KeyboardShortcut> ToggleKey;
        public readonly ConfigEntry<bool> ShowMessages;
        public readonly ConfigEntry<KeyboardShortcut> MenuKey;
        public readonly ConfigEntry<bool> BlockGameInput;
        public readonly ConfigEntry<bool> MenuOpenOnStart;
        public readonly ConfigEntry<bool> Benchmark;
        public readonly ConfigEntry<float> RenderScale;
        public readonly ConfigEntry<float> Sharpness;
        public readonly ConfigEntry<float> TaaSharpness;
        public readonly ConfigEntry<string> Preset;
        public readonly ConfigEntry<bool> AutoExposure;
        public readonly ConfigEntry<int> JitterPhases;
        public readonly ConfigEntry<QualityModeOption> Mode;
        public readonly ConfigEntry<BufferSourceOption> Buffers;
        public readonly ConfigEntry<float> JitterSignX;
        public readonly ConfigEntry<float> JitterSignY;
        public readonly ConfigEntry<float> MotionScaleX;
        public readonly ConfigEntry<float> MotionScaleY;
        public readonly ConfigEntry<bool> InvertXAxis;
        public readonly ConfigEntry<bool> InvertYAxis;
        public readonly ConfigEntry<bool> Verbose;

        public static readonly string[] PresetValues =
        {
            "Default", "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
            "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z"
        };

        public Settings(ConfigFile cfg)
        {
            Enabled = cfg.Bind("General", "Enabled", true,
                "DLSS instead of the game's TAA. false: the game runs its own TAA and the mod does nothing.");
            ToggleKey = cfg.Bind("General", "ToggleKey", new KeyboardShortcut(KeyCode.F8),
                "Key to toggle DLSS / game TAA on the fly (for comparison). The toggle itself is not saved to the config.");
            MenuKey = cfg.Bind("General", "MenuKey", new KeyboardShortcut(KeyCode.F7),
                "Key for the mod's settings window (RenderScale, both sharpness values, Preset and both keys can be changed there). Esc also closes the window.");
            BlockGameInput = cfg.Bind("General", "BlockGameInput", true,
                "While the settings window is open, the game receives no mouse or keyboard input (so clicks do not press game buttons " +
                "under the window and the camera does not turn). Turn off if it interferes with a gamepad.");
            ShowMessages = cfg.Bind("General", "ShowMessages", true,
                "Show a short on-screen message when toggling.");

            RenderScale = cfg.Bind("Image", "RenderScale", 1f, new ConfigDescription(
                "DLSS input resolution as a fraction of the screen resolution per side, from 0.33 to 1. The DLSS mode is picked automatically:\n" +
                "1.0 = DLAA (no upscaling); 0.667 = Quality; 0.58 = Balanced; 0.5 = Performance; 0.333 = Ultra Performance.\n" +
                "Any value between 0.5 and 1 works. DLSS 3.7 has no mode between 0.34 and 0.5 (Ultra Performance " +
                "accepts exactly 1/3), so the mod takes the nearest valid size, see the log. While DLSS is on, the game's \"Render scale\" setting " +
                "is ignored (treated as 100%); with DLSS off (F8) it works again.",
                new AcceptableValueRange<float>(Upscale.MinScale, 1f)));
            Sharpness = cfg.Bind("Image", "Sharpness", 0.45f, new ConfigDescription(
                "Sharpening after DLSS, 0-3. Same scale as the game's TAA sharpness (the game's TAA uses 0.25). 0 = no sharpening.",
                new AcceptableValueRange<float>(0f, 3f)));
            TaaSharpness = cfg.Bind("Image", "TaaSharpness", -1f, new ConfigDescription(
                "Sharpness of the game's own TAA, used when DLSS is off (F8, Enabled = false, no DLSS), 0-3. " +
                "-1 = keep the game's value (0.25). Not saved to the game's settings.",
                new AcceptableValueRange<float>(-1f, 3f)));
            Preset = cfg.Bind("Image", "Preset", "Default", new ConfigDescription(
                "DLSS model (preset): Default lets nvngx_dlss.dll decide, or a letter A-Z. Check with the NGX indicator (dlss-indicator-on.reg).\n" +
                "nvngx_dlss.dll 310.x (DLSS 4): Default = K; E, F = CNN; J, K = transformer; L, M = second-generation transformer; " +
                "A-D were removed and fall back to K.\n" +
                "nvngx_dlss.dll 3.7.x: A-F; Default = F for DLAA and Ultra Performance, E for Quality/Balanced/Performance.\n" +
                "Applied when the DLSS context is (re)created; changed in process memory, files on disk are not touched.",
                new AcceptableValueList<string>(PresetValues)));
            AutoExposure = cfg.Bind("Image", "AutoExposure", true,
                "DLSS auto exposure (flag 0x40). The game's exposure is not passed to DLSS, so it is on by default.");

            JitterPhases = cfg.Bind("Advanced", "JitterPhases", 8, new ConfigDescription(
                "Jitter sequence length (Halton 2,3). NVIDIA recommends at least 8 for DLAA; when upscaling the mod raises it " +
                "to 8 x (output/input)^2, e.g. 18 at 0.667 and 32 at 0.5.",
                new AcceptableValueRange<int>(1, 1024)));
            Mode = cfg.Bind("Advanced", "Mode", QualityModeOption.Auto,
                "DLSS mode passed on creation. Auto = DLAA (5), and if the library rejects it, " +
                "MaximumQuality with equal input and output resolution (effectively DLAA as well).");
            Buffers = cfg.Bind("Advanced", "Buffers", BufferSourceOption.Auto,
                "Where to take depth and motion vectors from. Global = global _CameraDepthTexture/_CameraMotionVectorsTexture, " +
                "Copy = copy into own textures, Auto = Global, or Copy if they are missing.");

            JitterSignX = cfg.Bind("Debug", "JitterSignX", -1f, "Jitter multiplier on X passed to DLSS (HDRP: -1).");
            JitterSignY = cfg.Bind("Debug", "JitterSignY", -1f, "Jitter multiplier on Y (HDRP: -1).");
            MotionScaleX = cfg.Bind("Debug", "MotionScaleX", -1f, "Motion vector multiplier on X, multiplied by width (HDRP: -1).");
            MotionScaleY = cfg.Bind("Debug", "MotionScaleY", -1f, "Motion vector multiplier on Y, multiplied by height (HDRP: -1).");
            InvertXAxis = cfg.Bind("Debug", "InvertXAxis", false, "DLSS invertXAxis field (HDRP: false).");
            InvertYAxis = cfg.Bind("Debug", "InvertYAxis", true, "DLSS invertYAxis field (HDRP: true).");
            Verbose = cfg.Bind("Debug", "Verbose", false, "Verbose log (every recreation, context validation).");
            Benchmark = cfg.Bind("Debug", "Benchmark", false,
                "Once DLSS works, cycle through game TAA / no AA / DLSS presets E, K, M (with and without sharpening), 720 frames each (a few seconds at high FPS), " +
                "and write the average frame and GPU times to the log. Do not touch the game meanwhile. Settings are restored afterwards.");
            MenuOpenOnStart = cfg.Bind("Debug", "MenuOpenOnStart", false, "Open the settings window right at startup (for testing).");
        }
    }
}
