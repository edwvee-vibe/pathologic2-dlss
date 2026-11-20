using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using InputServices;
using UnityEngine;

namespace Pathologic2Dlss
{
    /// <summary>
    /// Settings window (IMGUI): on/off, input resolution, sharpness, preset, hotkeys.
    /// Values come from the same ConfigEntry objects as the config file and are saved back to it (Plugin.RequestSave).
    /// </summary>
    internal static class Menu
    {
        private const int WindowId = 0x0D1AA;
        private const float BaseWidth = 560f;

        private static readonly (string Name, float Scale)[] ScalePresets =
        {
            ("DLAA", 1f), ("Quality", 0.667f), ("Balanced", 0.58f), ("Performance", 0.5f), ("Ultra Perf.", 0.333f)
        };

        public static bool IsOpen { get; private set; }

        /// <summary>The window is waiting for a key to rebind; the mod's hotkeys are inactive meanwhile.</summary>
        public static bool WaitingForKey => waitingFor != null;

        private static Rect window = new Rect(60, 60, BaseWidth, 10);
        private static bool positioned;
        private static int lastChangeFrame = -1;
        private static ConfigEntry<KeyboardShortcut> waitingFor;
        private static bool draggingScale;
        private static float pendingScale;
        private static bool draggingSharpness;
        private static GUIStyle windowStyle, headerStyle, statusStyle, selectedButton, hintStyle;
        private static Texture2D windowBackground, selectedBackground;

        private static Settings S => Plugin.S;

        public static void Toggle()
        {
            SetOpen(!IsOpen);
        }

        public static void SetOpen(bool open)
        {
            if (IsOpen == open)
            {
                return;
            }
            IsOpen = open;
            lastChangeFrame = Time.frameCount;
            waitingFor = null;
            if (!open)
            {
                CommitScale();
                draggingSharpness = false;
                Plugin.RequestSave();
                RestoreGameCursor();
            }
            else
            {
                ForceCursor();
            }
        }

        /// <summary>While the window is open the cursor must be visible and unlocked, whatever the game decides.</summary>
        public static void ForceCursor()
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        private static void RestoreGameCursor()
        {
            try
            {
                // The game keeps its state in WindowsCursorController; setting it again calls UpdateCursor().
                ICursorController c = CursorService.Instance;
                c.Visible = c.Visible;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Failed to restore the game cursor: " + e.Message);
            }
        }

        // ------------------------------------------------------------ drawing

        public static void Draw()
        {
            if (!IsOpen)
            {
                return;
            }
            EnsureStyles();
            HandleKeyCapture();
            if (!IsOpen)
            {
                return;
            }

            float scale = Mathf.Max(1f, Screen.height / 1080f);
            Matrix4x4 oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

            window.width = BaseWidth;
            if (!positioned)
            {
                positioned = true; // first open: centered near the top, away from corner overlays
                window.x = (Screen.width / scale - BaseWidth) / 2f;
                window.y = Screen.height / scale * 0.12f;
            }
            window = GUILayout.Window(WindowId, window, DrawWindow, $"{Plugin.Name} {Plugin.Version}", windowStyle,
                GUILayout.Width(BaseWidth));
            float maxX = Screen.width / scale - window.width;
            float maxY = Screen.height / scale - window.height;
            window.x = Mathf.Clamp(window.x, 0f, Mathf.Max(0f, maxX));
            window.y = Mathf.Clamp(window.y, 0f, Mathf.Max(0f, maxY));

            GUI.matrix = oldMatrix;

            if (draggingScale && GUIUtility.hotControl == 0)
            {
                CommitScale();
            }
            if (draggingSharpness && GUIUtility.hotControl == 0)
            {
                draggingSharpness = false;
                Plugin.RequestSave();
            }
        }

        private static void DrawWindow(int id)
        {
            GUILayout.Label(DlssPass.Status, statusStyle);
            GUILayout.Space(6);

            // --- on/off
            bool enabled = GUILayout.Toggle(S.Enabled.Value, " Enabled (DLSS instead of the game's TAA)");
            if (enabled != S.Enabled.Value)
            {
                S.Enabled.Value = enabled;
                Plugin.RequestSave();
            }
            GUILayout.Space(8);

            // --- input resolution
            float shownScale = draggingScale ? pendingScale : S.RenderScale.Value;
            GUILayout.Label("Input resolution", headerStyle);
            GUILayout.BeginHorizontal();
            float newScale = GUILayout.HorizontalSlider(shownScale, Upscale.MinScale, 1f, GUILayout.Width(300));
            if (Math.Abs(newScale - shownScale) > 0.0001f)
            {
                pendingScale = Mathf.Round(newScale * 100f) / 100f;
                draggingScale = true;
                shownScale = pendingScale;
            }
            GUILayout.Label("  " + DescribeScale(shownScale));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            foreach ((string name, float value) in ScalePresets)
            {
                bool selected = Math.Abs(shownScale - value) < 0.006f;
                if (GUILayout.Button(name, selected ? selectedButton : GUI.skin.button))
                {
                    draggingScale = false;
                    SetScale(value);
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("The game's \"Render scale\" setting is ignored (treated as 100%) while DLSS is on.", hintStyle);
            GUILayout.Space(8);

            // --- sharpness
            float sharp = S.Sharpness.Value;
            GUILayout.Label("DLSS sharpness", headerStyle);
            GUILayout.BeginHorizontal();
            float newSharp = SliderWithTick(sharp, 0f, 3f, 1f);
            if (Math.Abs(newSharp - sharp) > 0.0001f)
            {
                S.Sharpness.Value = Mathf.Round(newSharp * 100f) / 100f; // applied immediately, saved on release
                draggingSharpness = true;
            }
            GUILayout.Label($"  {S.Sharpness.Value:0.00}" +
                            (Math.Abs(S.Sharpness.Value - 0.45f) < 0.001f ? "  (default)" : ""));
            GUILayout.EndHorizontal();
            GUILayout.Space(8);

            // --- the game's TAA sharpness (used when DLSS is off)
            float taaSharp = S.TaaSharpness.Value;
            bool taaOverride = taaSharp >= 0f;
            float gameTaa = TaaSharpness.GameValue >= 0f ? TaaSharpness.GameValue : 0.25f;
            float shownTaa = taaOverride ? taaSharp : gameTaa;
            GUILayout.Label("Game TAA sharpness (when DLSS is off, F8)", headerStyle);
            GUILayout.BeginHorizontal();
            float newTaa = SliderWithTick(shownTaa, 0f, 3f, 1f);
            if (Math.Abs(newTaa - shownTaa) > 0.0001f)
            {
                S.TaaSharpness.Value = Mathf.Round(newTaa * 100f) / 100f; // applied immediately, saved on release
                draggingSharpness = true;
                taaOverride = true;
                shownTaa = S.TaaSharpness.Value;
            }
            GUILayout.Label($"  {shownTaa:0.00}" +
                            (taaOverride ? "" : "  (game's value)"));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Game's", taaOverride ? GUI.skin.button : selectedButton, GUILayout.Width(80)) && taaOverride)
            {
                S.TaaSharpness.Value = -1f;
                draggingSharpness = false;
                Plugin.RequestSave();
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(8);

            // --- preset
            GUILayout.Label("DLSS model (preset)", headerStyle);
            GUILayout.BeginHorizontal();
            foreach (string preset in PresetChoices())
            {
                bool selected = string.Equals(S.Preset.Value, preset, StringComparison.OrdinalIgnoreCase);
                if (GUILayout.Button(preset, selected ? selectedButton : GUI.skin.button) && !selected)
                {
                    S.Preset.Value = preset;
                    DlssPass.OnConfigReloaded(); // apply the preset and recreate the context
                    Plugin.RequestSave();
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(PresetHint(), hintStyle);
            GUILayout.Space(8);

            // --- hotkeys
            GUILayout.Label("Hotkeys", headerStyle);
            KeyRow("Toggle DLSS / game TAA", S.ToggleKey);
            KeyRow("Open / close this window", S.MenuKey);
            GUILayout.Space(10);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Saved to BepInEx\\config\\pathologic2.dlss.cfg", hintStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.Width(110)))
            {
                SetOpen(false);
            }
            GUILayout.EndHorizontal();

            GUI.DragWindow(new Rect(0, 0, 10000, 30));
        }

        /// <summary>
        /// 300 px horizontal slider with a tick mark at <paramref name="tick"/>, drawn under the thumb.
        /// Thumb position follows Unity's SliderHandler: x + padding.left + t * (width - padding.horizontal - thumbSize).
        /// </summary>
        private static float SliderWithTick(float value, float min, float max, float tick)
        {
            GUIStyle slider = GUI.skin.horizontalSlider;
            GUIStyle thumb = GUI.skin.horizontalSliderThumb;
            Rect r = GUILayoutUtility.GetRect(300f, 300f, slider.fixedHeight > 0 ? slider.fixedHeight : 12f,
                slider.fixedHeight > 0 ? slider.fixedHeight : 12f, slider, GUILayout.Width(300));
            if (Event.current.type == EventType.Repaint)
            {
                float thumbSize = thumb.fixedWidth != 0f ? thumb.fixedWidth : thumb.padding.horizontal;
                float t = (tick - min) / (max - min);
                float x = r.x + slider.padding.left + t * (r.width - slider.padding.horizontal - thumbSize) + thumbSize / 2f;
                Color old = GUI.color;
                GUI.color = new Color(1f, 0.85f, 0.6f, 0.9f);
                GUI.DrawTexture(new Rect(Mathf.Round(x) - 1f, r.y - 4f, 2f, r.height + 8f), Texture2D.whiteTexture);
                GUI.color = old;
            }
            return GUI.HorizontalSlider(r, value, min, max);
        }

        private static void KeyRow(string label, ConfigEntry<KeyboardShortcut> entry)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(260));
            bool waiting = ReferenceEquals(waitingFor, entry);
            string text = waiting ? "press a key... (Esc to cancel)" : entry.Value.ToString();
            if (GUILayout.Button(text, waiting ? selectedButton : GUI.skin.button, GUILayout.Width(250)))
            {
                waitingFor = waiting ? null : entry;
            }
            GUILayout.EndHorizontal();
        }

        private static void HandleKeyCapture()
        {
            Event e = Event.current;
            if (e == null || e.type != EventType.KeyDown || e.keyCode == KeyCode.None)
            {
                return;
            }
            if (waitingFor == null)
            {
                // Fallback path for the mod's hotkeys: while the window is open, Input.ResetInputAxes() may swallow GetKeyDown
                // in Update, but it does not touch IMGUI events. Skip the frame in which Update has just opened/closed the window.
                if (e.keyCode == KeyCode.Escape || (Plugin.Matches(S.MenuKey.Value, e) && lastChangeFrame != Time.frameCount))
                {
                    e.Use();
                    SetOpen(false);
                }
                else if (Plugin.Matches(S.ToggleKey.Value, e))
                {
                    e.Use();
                    Plugin.ToggleDlss(); // guards itself against a second trigger in the same frame
                }
                return;
            }
            if (e.keyCode == KeyCode.Escape)
            {
                waitingFor = null;
                e.Use();
                return;
            }
            if (IsModifier(e.keyCode))
            {
                return; // wait for the main key
            }
            var modifiers = new List<KeyCode>();
            if (e.control) modifiers.Add(KeyCode.LeftControl);
            if (e.shift) modifiers.Add(KeyCode.LeftShift);
            if (e.alt) modifiers.Add(KeyCode.LeftAlt);
            waitingFor.Value = new KeyboardShortcut(e.keyCode, modifiers.ToArray());
            Plugin.Log.LogInfo($"{waitingFor.Definition.Key} = {waitingFor.Value}");
            waitingFor = null;
            e.Use();
            Plugin.RequestSave();
        }

        private static bool IsModifier(KeyCode k)
        {
            return k == KeyCode.LeftControl || k == KeyCode.RightControl || k == KeyCode.LeftShift || k == KeyCode.RightShift ||
                   k == KeyCode.LeftAlt || k == KeyCode.RightAlt || k == KeyCode.LeftCommand || k == KeyCode.RightCommand ||
                   k == KeyCode.AltGr;
        }

        // ------------------------------------------------------------ values

        private static void CommitScale()
        {
            if (!draggingScale)
            {
                return;
            }
            draggingScale = false;
            SetScale(pendingScale);
        }

        private static void SetScale(float value)
        {
            value = Mathf.Clamp(value, Upscale.MinScale, 1f);
            if (Math.Abs(S.RenderScale.Value - value) > 0.0001f)
            {
                S.RenderScale.Value = value;
                Plugin.RequestSave();
            }
        }

        private static string DescribeScale(float s)
        {
            if (s >= 0.999f)
            {
                return $"1.00: DLAA, {Screen.width}x{Screen.height}";
            }
            int w = Mathf.Max(2, Mathf.RoundToInt(Screen.width * s / 2f) * 2);
            int h = Mathf.Max(2, Mathf.RoundToInt(Screen.height * s / 2f) * 2);
            bool snapped = DlssPass.PreviewInputSize(Screen.width, Screen.height, ref w, ref h);
            string mode = "Quality";
            if (s < 0.62f) mode = "Balanced";
            if (s < 0.54f) mode = "Performance";
            if (s < 0.42f) mode = "Ultra Performance";
            return $"{s:0.00}: {w}x{h}, ~{mode}" + (snapped ? " (fitted to DLSS)" : "");
        }

        private static readonly string[] PresetsDlss3 = { "Default", "A", "B", "C", "D", "E", "F" };
        // 310.x (DLSS 4): A-D are gone (NGX falls back to the default K), E/F are the last CNN models, J/K transformer,
        // L/M second-generation transformer. Checked with the NGX indicator on 310.9.1.
        private static readonly string[] PresetsDlss310 = { "Default", "E", "F", "J", "K", "L", "M" };

        private static bool IsDlss310 => DlssPass.DlssDllVersion != null && DlssPass.DlssDllVersion.Major >= 310;

        private static IEnumerable<string> PresetChoices()
        {
            string[] list = IsDlss310 ? PresetsDlss310 : PresetsDlss3;
            string current = S.Preset.Value;
            bool listed = false;
            foreach (string p in list)
            {
                listed |= string.Equals(current, p, StringComparison.OrdinalIgnoreCase);
                yield return p;
            }
            if (!listed && !string.IsNullOrEmpty(current))
            {
                yield return current; // value from the file that is not in the list
            }
        }

        private static string PresetHint()
        {
            if (IsDlss310)
            {
                return $"nvngx_dlss.dll {DlssPass.DlssDllVersionText}: Default = K. E, F: CNN; J, K: transformer; L, M: transformer 2. " +
                       "A-D no longer exist (fall back to K).";
            }
            return $"nvngx_dlss.dll {DlssPass.DlssDllVersionText}: defaults DLAA F, Quality/Balanced/Performance E, Ultra Performance F.";
        }

        // ------------------------------------------------------------ styles

        private static void EnsureStyles()
        {
            if (windowStyle != null)
            {
                return;
            }
            windowBackground = MakeTexture(new Color(0.08f, 0.08f, 0.09f, 0.96f));
            selectedBackground = MakeTexture(new Color(0.55f, 0.12f, 0.10f, 1f));

            windowStyle = new GUIStyle(GUI.skin.window) { padding = new RectOffset(14, 14, 30, 12) };
            windowStyle.normal.background = windowBackground;
            windowStyle.onNormal.background = windowBackground;
            windowStyle.normal.textColor = Color.white;
            windowStyle.onNormal.textColor = Color.white;

            headerStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            headerStyle.normal.textColor = new Color(0.95f, 0.85f, 0.70f);

            statusStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            statusStyle.normal.textColor = new Color(0.70f, 0.90f, 0.70f);

            hintStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = Mathf.Max(10, GUI.skin.label.fontSize - 1) };
            hintStyle.normal.textColor = new Color(0.65f, 0.65f, 0.65f);

            selectedButton = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold };
            selectedButton.normal.background = selectedBackground;
            selectedButton.hover.background = selectedBackground;
            selectedButton.active.background = selectedBackground;
            selectedButton.normal.textColor = Color.white;
            selectedButton.hover.textColor = Color.white;
        }

        private static Texture2D MakeTexture(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }
    }
}
