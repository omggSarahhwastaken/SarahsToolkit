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
                    return Convert.ToUInt32(v).ToString();
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
