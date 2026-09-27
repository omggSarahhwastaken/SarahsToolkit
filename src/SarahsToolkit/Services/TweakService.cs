using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using SarahsToolkit.Models;

namespace SarahsToolkit.Services
{
    public class TweakService
    {
        private readonly string _jsonPath;

        public TweakService()
        {
            _jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "tweaks.json");
        }

        public List<TweakDefinition> LoadTweaks()
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var json = File.ReadAllText(_jsonPath);
            return JsonSerializer.Deserialize<List<TweakDefinition>>(json, opts) ?? new List<TweakDefinition>();
        }

        public TweakState GetState(TweakDefinition tweak)
        {
            try
            {
                var c = tweak.Check;
                if (c == null) return TweakState.Unknown;
                string actual = ReadValue(c.Hive, c.KeyPath, c.ValueName, c.Kind);
                if (actual == null) return TweakState.NotApplied;
                return string.Equals(actual, c.Expected, StringComparison.OrdinalIgnoreCase)
                    ? TweakState.Applied
                    : TweakState.NotApplied;
            }
            catch
            {
                return TweakState.Unknown;
            }
        }

        /// <summary>Returns the underlying read error when GetState reports Unknown, else "".</summary>
        public string GetStateError(TweakDefinition tweak)
        {
            try
            {
                var c = tweak.Check;
                if (c == null) return "no check defined for this tweak";
                ReadValue(c.Hive, c.KeyPath, c.ValueName, c.Kind);
                return "";
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        public void Apply(TweakDefinition tweak)
        {
            foreach (var op in tweak.Apply) WriteOperation(op);
        }

        public void Revert(TweakDefinition tweak)
        {
            foreach (var op in tweak.Revert) WriteOperation(op);
        }

        private static RegistryKey HiveRoot(string hive)
        {
            return string.Equals(hive, "HKLM", StringComparison.OrdinalIgnoreCase)
                ? Registry.LocalMachine
                : Registry.CurrentUser;
        }

        private static string ReadValue(string hive, string keyPath, string valueName, string kind)
        {
            using (var key = HiveRoot(hive).OpenSubKey(keyPath, false))
            {
                if (key == null) return null;
                string name = valueName == "(Default)" ? "" : valueName;
                object v = key.GetValue(name);
                if (v == null) return null;
                if (string.Equals(kind, "DWord", StringComparison.OrdinalIgnoreCase))
                {
                    // Be tolerant: if the value exists but isn't a clean DWORD (some tools
                    // write these as strings/binary), don't blow up into Unknown — fall back
                    // to the raw form so the toggle stays clickable and Apply can fix it.
                    try { return Convert.ToUInt32(v).ToString(); }
                    catch { return v.ToString(); }
                }
                return v.ToString();
            }
        }

        private static void WriteOperation(RegistryOperation op)
        {
            using (var key = HiveRoot(op.Hive).CreateSubKey(op.KeyPath))
            {
                if (key == null)
                    throw new InvalidOperationException("Cannot open registry key: " + op.KeyPath);
                string name = op.ValueName == "(Default)" ? "" : op.ValueName;
                if (op.Data == null)
                {
                    key.DeleteValue(name, false);
                    return;
                }
                if (string.Equals(op.Kind, "DWord", StringComparison.OrdinalIgnoreCase))
                    key.SetValue(name, unchecked((int)uint.Parse(op.Data)), RegistryValueKind.DWord);
                else
                    key.SetValue(name, op.Data, RegistryValueKind.String);
            }
        }
    }
}
