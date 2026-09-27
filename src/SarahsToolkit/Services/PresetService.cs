using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SarahsToolkit.Models;

namespace SarahsToolkit.Services
{
    public class PresetService
    {
        private readonly string _jsonPath;

        public PresetService()
        {
            _jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "presets.json");
        }

        public List<PresetDefinition> LoadPresets()
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var json = File.ReadAllText(_jsonPath);
            return JsonSerializer.Deserialize<List<PresetDefinition>>(json, opts)
                ?? new List<PresetDefinition>();
        }

        // Measures how much of the preset is not yet active on this PC.
        // Only vendor-neutral tweaks are referenced by presets, so this is
        // meaningful on NVIDIA, AMD and Intel machines alike.
        // "Tweaks" count as pending while not applied; "Revert" entries count
        // as pending while still applied.
        public PresetImpact Evaluate(PresetDefinition preset,
            List<TweakDefinition> tweaks, TweakService tweakService)
        {
            var impact = new PresetImpact();
            foreach (string id in preset.Tweaks ?? Enumerable.Empty<string>())
            {
                var tw = tweaks.FirstOrDefault(t => t.Id == id);
                if (tw == null) continue;
                impact.Total++;
                TweakState state = tweakService.GetState(tw);
                if (state == TweakState.Unknown) continue;
                impact.Measurable++;
                if (state == TweakState.NotApplied) impact.Pending++;
            }
            foreach (string id in ResolveRevertIds(preset, tweaks))
            {
                var tw = tweaks.FirstOrDefault(t => t.Id == id);
                if (tw == null) continue;
                impact.Total++;
                TweakState state = tweakService.GetState(tw);
                if (state == TweakState.Unknown) continue;
                impact.Measurable++;
                if (state == TweakState.Applied) impact.Pending++;
            }
            return impact;
        }

        /// <summary>Expands a preset's revert list (RevertAll = every known tweak).</summary>
        public static IEnumerable<string> ResolveRevertIds(PresetDefinition preset,
            List<TweakDefinition> tweaks)
        {
            if (preset.RevertAll)
                return tweaks.Select(t => t.Id);
            return preset.Revert ?? Enumerable.Empty<string>();
        }
    }
}
