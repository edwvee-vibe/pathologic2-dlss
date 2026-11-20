using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Pathologic2Dlss
{
    /// <summary>
    /// DLSS model (preset) selection. Unity's managed API does not pass a preset, but the static NGX library inside
    /// NVUnityPlugin.dll sets a default preset in its NVSDK_NGX_D3D11_CreateFeature wrapper (on every feature creation)
    /// if the application has not set one:
    ///
    ///   83 F8 01             cmp eax, 1                      ; GetUI(...) == NVSDK_NGX_Result_Success
    ///   75 06                jne +6
    ///   83 7D 18 00          cmp dword [rbp+18h], 0          ; value == 0 (Default)
    ///   75 15                jne +15h
    ///   41 B8 xx xx xx xx    mov r8d, 8000000Nh              ; default preset (N: 6 = F, 5 = E)
    ///   48 8D 15 rel32       lea rdx, "DLSS.Hint.Render.Preset.&lt;mode&gt;"
    ///   ...                  call SetUI
    ///
    /// 6 modes x 3 APIs (D3D11, D3D12, Vulkan) = 18 sites; 18 more of the same kind are for RayReconstruction and are left alone.
    /// The constant is changed in process memory (the file on disk is not modified) to the value the application itself would set.
    ///
    /// Unity keeps one NGX parameter block for the whole session, so after the first feature the preset parameter already
    /// holds the library default (0x8000000N, non-zero) and the "jne +15h" skips the write. Changing only the constant would
    /// work for the very first context only. So the second jne is replaced with two NOPs as well: the preset is then written
    /// on every feature creation, and a new context picks up a new preset. Once NOPed, the jne stays NOPed; "Default" then
    /// writes the original constant back, which NGX treats as "not set by the application" (high bit) and uses the DLL default.
    /// </summary>
    internal static class PresetPatcher
    {
        private const string ModuleName = "NVUnityPlugin.dll";
        private const string ParamPrefix = "DLSS.Hint.Render.Preset.";
        private const int ExpectedSites = 18;
        private const uint PageExecuteReadWrite = 0x40;
        private const uint ImageScnMemExecute = 0x20000000;

        private static readonly byte[] Head = { 0x83, 0xF8, 0x01, 0x75, 0x06, 0x83, 0x7D, 0x18, 0x00, 0x75, 0x15, 0x41, 0xB8 };
        private static readonly byte[] Lea = { 0x48, 0x8D, 0x15 };
        private const int JneOffset = 9;  // "75 15" inside Head
        private const int ImmOffset = 13; // imm32 of "mov r8d"

        private sealed class Site
        {
            public IntPtr Imm;
            public IntPtr Jne;
            public uint Original;
            public string Param;
        }

        private static List<Site> sites;
        private static bool scanFailed;

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string name);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

        [DllImport("kernel32")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32")]
        private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

        /// <summary>"Default" restores the library's original values; a letter A-Z sets that preset.</summary>
        public static void Apply(string preset)
        {
            try
            {
                if (!EnsureScanned())
                {
                    if (!IsDefault(preset))
                    {
                        Plugin.Log.LogWarning($"Preset {preset} not applied: no sites found in {ModuleName}, keeping the default preset.");
                    }
                    return;
                }

                uint? value = Parse(preset);
                if (preset != null && !IsDefault(preset) && value == null)
                {
                    Plugin.Log.LogWarning($"Unknown preset '{preset}', expected Default or a letter A-Z. Keeping the default preset.");
                }

                int changed = 0;
                foreach (Site s in sites)
                {
                    uint want = value ?? s.Original;
                    if ((uint)Marshal.ReadInt32(s.Imm) != want)
                    {
                        Write(s.Imm, BitConverter.GetBytes(want));
                        changed++;
                    }
                    if (value != null && Marshal.ReadByte(s.Jne) != 0x90)
                    {
                        Write(s.Jne, new byte[] { 0x90, 0x90 }); // always write the preset, see the class comment
                        changed++;
                    }
                }
                if (value == null)
                {
                    if (changed > 0)
                    {
                        Plugin.Log.LogInfo($"DLSS preset: Default, original values restored ({changed} code patches).");
                    }
                }
                else
                {
                    Plugin.Log.LogInfo($"DLSS preset: {preset} (0x{value.Value:X}) set at {sites.Count} sites ({changed} code patches).");
                }
            }
            catch (Exception e)
            {
                scanFailed = true;
                Plugin.Log.LogWarning("Preset not applied: " + e);
            }
        }

        private static bool IsDefault(string preset)
        {
            return string.IsNullOrEmpty(preset) || preset.Equals("Default", StringComparison.OrdinalIgnoreCase);
        }

        private static uint? Parse(string preset)
        {
            if (preset == null || preset.Length != 1)
            {
                return null;
            }
            char c = char.ToUpperInvariant(preset[0]);
            if (c < 'A' || c > 'Z')
            {
                return null;
            }
            return (uint)(c - 'A' + 1); // NVSDK_NGX_DLSS_Hint_Render_Preset_A = 1, ...
        }

        private static bool EnsureScanned()
        {
            if (sites != null)
            {
                return true;
            }
            if (scanFailed)
            {
                return false;
            }
            scanFailed = true;

            IntPtr module = GetModuleHandleW(ModuleName);
            if (module == IntPtr.Zero)
            {
                Plugin.Log.LogWarning($"{ModuleName} is not loaded in the process; nowhere to change the preset.");
                return false;
            }

            int peOffset = Marshal.ReadInt32(module, 0x3C);
            if (Marshal.ReadInt32(module, peOffset) != 0x00004550) // "PE\0\0"
            {
                Plugin.Log.LogWarning($"{ModuleName}: no PE header.");
                return false;
            }
            int sectionCount = (ushort)Marshal.ReadInt16(module, peOffset + 6);
            int optionalSize = (ushort)Marshal.ReadInt16(module, peOffset + 20);
            int optional = peOffset + 24;
            long imageSize = (uint)Marshal.ReadInt32(module, optional + 56); // SizeOfImage (PE32+)
            int sectionTable = optional + optionalSize;

            var found = new List<Site>();
            var skipped = new List<string>();
            for (int i = 0; i < sectionCount; i++)
            {
                int sh = sectionTable + 40 * i;
                int virtualSize = Marshal.ReadInt32(module, sh + 8);
                int virtualAddress = Marshal.ReadInt32(module, sh + 12);
                uint characteristics = (uint)Marshal.ReadInt32(module, sh + 36);
                if ((characteristics & ImageScnMemExecute) == 0 || virtualSize <= 0)
                {
                    continue;
                }

                var code = new byte[virtualSize];
                Marshal.Copy(Offset(module, virtualAddress), code, 0, virtualSize);
                for (int p = 0; p + 24 <= code.Length; p++)
                {
                    if (!Matches(code, p, Head) || !Matches(code, p + 17, Lea))
                    {
                        continue;
                    }
                    long leaEnd = (long)virtualAddress + p + 24;
                    long target = leaEnd + BitConverter.ToInt32(code, p + 20);
                    if (target <= 0 || target >= imageSize - 1)
                    {
                        continue;
                    }
                    string param = ReadAscii(Offset(module, target), 64);
                    long siteRva = (long)virtualAddress + p;
                    uint imm = BitConverter.ToUInt32(code, p + ImmOffset);
                    if (param.StartsWith(ParamPrefix, StringComparison.Ordinal))
                    {
                        found.Add(new Site
                        {
                            Imm = Offset(module, siteRva + ImmOffset),
                            Jne = Offset(module, siteRva + JneOffset),
                            Original = imm,
                            Param = param
                        });
                    }
                    else
                    {
                        skipped.Add(param);
                    }
                }
            }

            if (found.Count != ExpectedSites)
            {
                Plugin.Log.LogWarning($"{ModuleName}: found {found.Count} DLSS preset sites instead of {ExpectedSites} " +
                                      "(different plugin version?); leaving the preset alone.");
                return false;
            }

            var summary = new SortedDictionary<string, string>();
            foreach (Site s in found)
            {
                string mode = s.Param.Substring(ParamPrefix.Length);
                string val = "0x" + s.Original.ToString("X");
                summary[mode] = summary.TryGetValue(mode, out string prev) ? prev + "," + val : val;
            }
            var parts = new List<string>();
            foreach (KeyValuePair<string, string> kv in summary)
            {
                parts.Add(kv.Key + "=" + kv.Value);
            }
            Plugin.Log.LogInfo($"{ModuleName}: found {found.Count} DLSS preset sites (+{skipped.Count} others, left alone). " +
                               "Original values: " + string.Join("; ", parts.ToArray()));

            sites = found;
            scanFailed = false;
            return true;
        }

        private static void Write(IntPtr address, byte[] bytes)
        {
            var size = new UIntPtr((uint)bytes.Length);
            if (!VirtualProtect(address, size, PageExecuteReadWrite, out uint old))
            {
                throw new InvalidOperationException("VirtualProtect: error " + Marshal.GetLastWin32Error());
            }
            try
            {
                Marshal.Copy(bytes, 0, address, bytes.Length);
            }
            finally
            {
                VirtualProtect(address, size, old, out _);
                FlushInstructionCache(GetCurrentProcess(), address, size);
            }
        }

        private static bool Matches(byte[] data, int at, byte[] pattern)
        {
            for (int i = 0; i < pattern.Length; i++)
            {
                if (data[at + i] != pattern[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static IntPtr Offset(IntPtr basePtr, long offset)
        {
            return new IntPtr(basePtr.ToInt64() + offset);
        }

        private static string ReadAscii(IntPtr p, int max)
        {
            var chars = new char[max];
            int n = 0;
            for (; n < max; n++)
            {
                byte b = Marshal.ReadByte(p, n);
                if (b == 0 || b > 0x7E)
                {
                    break;
                }
                chars[n] = (char)b;
            }
            return new string(chars, 0, n);
        }
    }
}
