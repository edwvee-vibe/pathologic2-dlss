# Pathologic 2 DLSS

A BepInEx 5 plugin for Pathologic 2 (2.1, Unity 2022.3, D3D11) that replaces the TAA pass of Post Processing Stack v1
with DLSS, either at native resolution (DLAA) or upscaling from a lower one, followed by its own sharpening.

## Purpose
The main goal is DLAA: a sharper, cleaner image than the game's TAA at native resolution. Upscaling (DLSS) is
available too, but it is a secondary feature.

If DLSS is not available (no NVIDIA RTX GPU, missing DLLs, an old driver), the game's own TAA keeps running and the mod
still adds what the game lacks: a sharpness slider for that TAA.

## What to expect
Tested by the author on an RTX 4080 with a Ryzen 7 9800X3D at 2560x1440:

- **DLAA costs a noticeable share of the frame rate.** It adds a fixed amount of GPU time per frame, about 0.4 ms for
  model E, 0.7-0.8 ms for K and 2.4-2.6 ms for M. On a heavy street at about 150 FPS that is roughly 5%, 8% and 25% of the frame rate.
  The game's TAA costs about 0.1-0.2 ms.
- **DLSS upscaling did not pay off.** At the same frame rate, lowering the game's "Render scale" with its TAA
  looked better than DLSS upscaling.
- **DLAA/DLSS still render vegetation and hair detail better** than TAA, make wet pavement much more stable
  (no shimmering), and DLAA gives a more natural sharpness to the whole image.
- **Avoid preset E in the rain.** The CNN model E smooths away most of the rain and the splashes on the ground.
  K keeps much more of them, although some small splashes still disappear.

`Debug.Benchmark` measures these numbers on your own system (see the settings table).

## Requirements
- **Pathologic 2** for Windows. Tested on game version 2.1.31648 (Unity 2022.3.62).
- **BepInEx 5, 64-bit (x64)**. BepInEx 6 and the x86 build are not supported. Tested with BepInEx 5.4.23.
- **Anti-aliasing enabled** in the game's graphics settings: the mod works on the game's TAA pass.
- **For DLSS/DLAA:** an NVIDIA RTX GPU and two native libraries next to `Pathologic.exe` (see below).
  Without them the mod still loads and only the TAA sharpness slider works.

### Native libraries for DLSS
The game ships Unity's NVIDIA module (`UnityEngine.NVIDIAModule.dll`) but not its native part. The mod does not include
these files; copy them into the game folder, next to `Pathologic.exe`:

| File | Tested version | Where to get it |
|---|---|---|
| `NVUnityPlugin.dll` | the build shipped with Unity 6000.2.6f2: 1,464,240 bytes, built 2025-04-25, signed by Unity Technologies SF, NGX SDK 3.7.20, SHA-256 `FB1C32303372259D143638D81513525EC0E19AF3659155BA9703329C15065813` | taken from **Pathologic 3** (game folder, next to its exe). The same plugin ships with the Unity Editor (free, installed through Unity Hub): search the editor installation for `NVUnityPlugin.dll`. |
| `nvngx_dlss.dll` | 310.9.1 (DLSS 4; 3.7.x works too, with presets A-F only) | NVIDIA's DLSS SDK on GitHub: [NVIDIA/DLSS](https://github.com/NVIDIA/DLSS), `lib/Windows_x86_64/rel/`. Pathologic 3 ships 3.7.20. |

The preset switch patches `NVUnityPlugin.dll` in memory and is tied to the build above. With another build DLSS still
works, but if the patch pattern is not found the preset setting is ignored (the log says so).

## Installation
1. Install BepInEx 5 x64 if you don't have it yet:
   - Download `BepInEx_win_x64_5.4.x.x.zip` from the
     [BepInEx releases page](https://github.com/BepInEx/BepInEx/releases). Pick the latest 5.4 release, not 6.x.
   - Extract it into the game folder, next to `Pathologic.exe`. For Steam that is usually
     `C:\Program Files (x86)\Steam\steamapps\common\Pathologic\`.
   - Start the game once and quit. BepInEx creates its `BepInEx\plugins` and `BepInEx\config` folders on that first run.
2. Download `Pathologic2Dlss-<version>.zip` from the [Releases](../../releases) page of this repository and extract it
   into the game folder. The plugin ends up in `BepInEx\plugins\pathologic2-dlss\Pathologic2Dlss.dll`.
3. For DLSS, put `NVUnityPlugin.dll` and `nvngx_dlss.dll` next to `Pathologic.exe` (see above).
4. Make sure anti-aliasing is on in the game's graphics settings, then start the game. `BepInEx\LogOutput.log` should
   contain a line starting with `Pathologic 2 DLSS` and, with the libraries in place, `DLSS available`.

The mod doesn't modify game files. To uninstall it, delete `BepInEx\plugins\pathologic2-dlss` (and the two DLLs if you
added them).

## Building from source
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1      # -> dist\Pathologic2Dlss.dll and dist\Pathologic2Dlss-<version>.zip
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1    # -> <Game>\BepInEx\plugins\pathologic2-dlss\
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -Uninstall
```
Both scripts accept `-GameDir`. The build references the game's managed assemblies and BepInEx from the game folder.
`build.ps1 -Bundle` also makes `Pathologic2Dlss-<version>-with-dlls.zip` with both native libraries taken from the game folder,
plus `third-party/THIRD-PARTY-NOTICES.md` and the NVIDIA license.

## Usage
Press **F8** in game to switch between DLSS and the game's TAA for comparison (a short message shows the mode).

### Settings window (F7)
On/off, input resolution (a slider plus DLAA / Quality / Balanced / Performance / Ultra Perf. buttons), DLSS sharpness, game TAA sharpness,
DLSS model and both hotkeys (click the button, then press the new key; Esc cancels). The status line at the top shows
what is running right now (input -> output, mode, model). Everything applies immediately and is written to the config;
sliders apply when the mouse button is released. While the window is open the game receives no mouse or keyboard input
(`BlockGameInput`) and the cursor is visible. Close it with F7, Esc or the Close button.

### Settings: `BepInEx\config\pathologic2.dlss.cfg`
The file is re-read once per second, so edits apply without restarting the game.

| Setting | Default | Meaning |
|---|---|---|
| `General.Enabled` | true | DLSS instead of TAA |
| `General.ToggleKey` | F8 | toggle DLSS/TAA on the fly; the state is not written to the config |
| `General.MenuKey` | F7 | settings window |
| `General.BlockGameInput` | true | block game input while the window is open |
| `General.ShowMessages` | true | on-screen message when toggling |
| `Image.RenderScale` | 1.0 | DLSS input resolution as a fraction of the screen: 1.0 is DLAA, 0.667 Quality, 0.58 Balanced, 0.5 Performance, 0.333 Ultra Performance. The DLSS mode is picked automatically. The game's "Render scale" slider is ignored (treated as 100%) while DLSS is on |
| `Image.Sharpness` | 0.45 | DLSS sharpness from 0 to 3, same scale as the game's TAA (the game's TAA uses 0.25) |
| `Image.TaaSharpness` | -1 | sharpness of the game's own TAA when DLSS is off (F8, `Enabled = false`), 0 to 3; -1 keeps the game's value (0.25) |
| `Image.Preset` | Default | DLSS model: Default or a letter A-Z. `nvngx_dlss.dll` 310.x: E, F (CNN), J, K (transformer), L, M (transformer 2); Default = K, A-D were removed and fall back to K. 3.7.x: A-F |
| `Image.AutoExposure` | true | DLSS auto exposure |
| `Advanced.JitterPhases` | 8 | jitter sequence length |
| `Advanced.Mode` | Auto | DLAA (5), falling back to MaximumQuality if DLAA cannot be created |
| `Advanced.Buffers` | Auto | depth and motion vectors: Global (global textures), Copy (own copies) or Auto |
| `Debug.Benchmark` | false | once DLSS works, cycle through game TAA / no AA / DLSS E, K, M (720 frames each) and write the frame times to `BepInEx\LogOutput.log`; runs once per game launch, can be switched on in-game by editing the file |
| `Debug.*` | as in HDRP | jitter and motion vector signs, invertX/YAxis, verbose log |

### NVIDIA indicator
`dlss-indicator-on.reg` enables the stock NGX overlay in the screen corner (version, mode, preset),
`dlss-indicator-off.reg` disables it. Both files write to HKLM, so administrator rights are required.

## How it works
- `TaaComponent.SetProjectionMatrix`: own Halton(2,3) jitter, centered in [-0.5, 0.5).
- `TaaComponent.Render`: `ExecuteDLSS` with the same parameters as HDRP (`mvScale = -size`, `jitter = -jitter`,
  `invertYAxis = 1`), output into an own UAV texture, then sharpening into `destination`.
- Sharpening uses the game's TAA shader (`Hidden/Post FX/Temporal Anti-aliasing`): the history blend weights and `_Jitter`
  are zero, so only its sharpening filter remains. DLSS 3.7 itself ignores the sharpness parameter.
- Upscaling (`RenderScale < 1`) reuses the game's pipeline for its "Render scale" setting: the mod replaces
  `DynamicResolution.GetTargetTexture` with its own reduced texture, and the sky and world cameras render into it. A transpiler
  on `PostProcessingBehaviour.OnRenderImage` gives DLSS a full-size target, so all post-processing after it also runs at full
  resolution. The final frame goes into a separate full-size texture, and the `RawImage` on the UI camera shows it instead of the reduced one.
  The input size is fitted to the `GetOptimalSettings` ranges: in DLSS 3.7, Ultra Performance accepts exactly 1/3 only,
  and there is no valid size between 0.34 and 0.5.
- Preset: 18 `mov r8d, 8000000Nh` constants (6 modes x D3D11/D3D12/Vulkan) that NGX uses to set the default preset are
  patched in the memory of `NVUnityPlugin.dll`, and the `jne` that skips the write when the parameter is already set is replaced with NOPs
  (Unity reuses one NGX parameter block, so without it only the first context would get the preset). The file on disk is not changed.
  If the number of matches is not 18, the preset is left alone.
- If anything goes wrong (missing DLL, wrong GPU, invalid context, exception), one line is written to the log and the game
  stays on its own TAA.

## Limitations
- While DLSS processes frames, the mod sets the `scale` field of `DynamicResolution` to 1 (the game's "Render scale" is treated as 100%)
  and puts the game's value back when DLSS is off (F8, `Enabled = false`, no DLSS, or the game's anti-aliasing is off).
- When upscaling, the texture mip bias is not adjusted, so textures are slightly softer than at native resolution. Bloom, DOF
  and grain are partly computed at the input resolution.
- If effects after post-processing are active (`MotionTrail`, `WaveDistortion`), those frames keep the reduced final image, stretched.
- The preset patch is tied to this build of `NVUnityPlugin.dll` (pattern search plus the parameter string).
- A preset override in the driver profile (NVIDIA App) takes priority over the mod's setting.

## License
MIT, see [LICENSE](LICENSE). `NVUnityPlugin.dll` and `nvngx_dlss.dll` are not part of this project and are covered by
the licenses of Unity Technologies and NVIDIA.
