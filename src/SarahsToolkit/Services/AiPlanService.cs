using System;
using System.Collections.Generic;
using System.Linq;
using SarahsToolkit.Models;

namespace SarahsToolkit.Services
{
    public class AiPlanItem
    {
        public TweakDefinition Tweak { get; set; }
        public string Reason { get; set; }
    }

    public class AiPlan
    {
        public List<AiPlanItem> Items { get; set; } = new List<AiPlanItem>();
        // Plain notes, not tweaks — a pending reboot, or power-hungry
        // tweaks that are already on when the user asked for battery life.
        public List<string> Notes { get; set; } = new List<string>();
    }

    // Builds a per-system tweak plan from the request text and the PC's
    // own specs. No model, no network — just keyword intent plus spec
    // rules over the tweak catalog. Every reason is grounded in the
    // tweak's own description, so the plan never promises something a
    // tweak doesn't do. Tweaks already applied never show up again.
    public class AiPlanService
    {
        private const int MaxItems = 12;

        // The Battery Saver preset's own lists, by id: these three are
        // what actually saves battery; the revert list is the set of
        // tweaks that trade battery for speed. The Power tweaks are all
        // performance tweaks, so a battery plan must never include them.
        private static readonly string[] BatteryApplyIds =
            { "privacy.backgroundApps", "perf.transparency", "perf.animations" };
        private static readonly string[] BatteryCostIds =
            { "perf.powerThrottling", "perf.sysMain", "perf.prefetch", "perf.lastAccess",
              "gaming.hwScheduling", "power.coreParking", "power.pcieAspm", "power.usbSuspend",
              "power.wifiPerf", "power.ultimatePerf" };

        public AiPlan BuildPlan(string request, List<TweakDefinition> tweaks,
            TweakService tweakService, DashboardSnapshot snap, bool hasBattery)
        {
            string text = (request ?? "").ToLowerInvariant();
            var plan = new AiPlan();
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Intent from keywords. "Battery life" (and friends) is its own
            // intent: bare "battery"/"power"/"laptop" means the Power
            // category, but asking to *save* battery needs the opposite —
            // the Battery Saver set, never the performance power tweaks.
            bool batterySaving = (HasWord(text, "batter") &&
                    HasWord(text, "life", "longer", "save", "last", "extend", "drain", "saver"))
                || text.Contains("save power") || text.Contains("battery saver");
            bool gaming = HasWord(text, "game", "gaming", "fps", "play");
            bool power = !batterySaving && HasWord(text, "battery", "power", "laptop");
            bool boot = HasWord(text, "boot", "startup", "start");
            bool privacy = HasWord(text, "private", "privacy", "telemetry", "ads", "track");
            bool network = HasWord(text, "lag", "network", "ping", "internet", "wifi");
            bool performance = HasWord(text, "fast", "faster", "speed", "performance", "optimize", "optimise", "slow", "snappy", "smooth");

            // Goal categories, in the order the plan prefers them. The
            // "Start" category is Start-menu tweaks, not startup, so a
            // faster-boot request maps to Performance (startup delay,
            // SysMain, prefetcher) instead of it. "Ping" also pulls in
            // Gaming: the catalog's ping tweak (remove network throttling
            // for gaming) lives there, not in Network.
            var goalCats = new List<string>();
            string goalLabel = "a faster PC";
            if (batterySaving)
            {
                goalLabel = "longer battery";
            }
            else
            {
                if (gaming) { AddCats(goalCats, "Gaming", "Performance"); goalLabel = "gaming"; }
                if (power) { AddCats(goalCats, "Power"); goalLabel = "more power"; }
                if (boot) { AddCats(goalCats, "Performance"); goalLabel = "a faster boot"; }
                if (privacy) { AddCats(goalCats, "Privacy"); goalLabel = "more privacy"; }
                if (network) { AddCats(goalCats, "Network", "Gaming"); goalLabel = "less lag"; }
                if (performance) { AddCats(goalCats, "Performance", "Gaming"); goalLabel = "a faster PC"; }
                if (goalCats.Count == 0) { AddCats(goalCats, "Performance", "Gaming"); }
            }

            // 1. Goal matches, in catalog order.
            if (batterySaving)
            {
                foreach (string id in BatteryApplyIds)
                    TryAdd(plan, added, tweaks, tweakService, id,
                        "You asked for longer battery — " + ReasonFor(tweaks, id), true);
            }
            else
            {
                foreach (var tw in tweaks)
                {
                    if (plan.Items.Count >= MaxItems) break;
                    if (!goalCats.Contains(tw.Category, StringComparer.OrdinalIgnoreCase)) continue;
                    TryAdd(plan, added, tweaks, tweakService, tw.Id,
                        "You asked for " + goalLabel + " — " + ReasonFor(tweaks, tw.Id), true);
                }
            }

            // 2. Spec-rule matches, only if slots are left. Never for a
            // battery plan: the low-RAM and disk rules would pull in
            // tweaks the Battery Saver preset reverts.
            if (!batterySaving && plan.Items.Count < MaxItems)
            {
                // Low RAM: SysMain preloads apps into memory, and the
                // prefetcher logs access patterns to preload them later.
                if (snap != null && snap.RamTotalGb > 0 && snap.RamTotalGb <= 16)
                {
                    string ram = "Your " + snap.RamTotalGb.ToString("0") + " GB of RAM";
                    TryAdd(plan, added, tweaks, tweakService, "perf.sysMain",
                        ram + " — " + ReasonFor(tweaks, "perf.sysMain"), false);
                    TryAdd(plan, added, tweaks, tweakService, "perf.prefetch",
                        ram + " — " + ReasonFor(tweaks, "perf.prefetch"), false);
                }
                // Nearly full disk: hibernation's file is RAM-sized, so
                // removing it is the biggest single free-space win here.
                // (The tweak's own description quotes a stale "16 GB on
                // this PC", so the reason is built from the real numbers.)
                if (snap != null && snap.DiskFreeGb > 0 && snap.DiskFreeGb < 64)
                {
                    TryAdd(plan, added, tweaks, tweakService, "perf.hibernate",
                        "Only " + snap.DiskFreeGb.ToString("0") + " GB free on your disk — removing " +
                        "hiberfil.sys frees roughly your RAM's worth of space (" +
                        snap.RamTotalGb.ToString("0") + " GB).", false);
                }
                // Laptop: the Power tweaks are performance tweaks, so they
                // fill leftover slots on a machine with a battery. (No
                // Power tweak is laptop-only — core parking, USB suspend
                // and the rest apply to desktops too — so there is nothing
                // to skip on a desktop.)
                if (hasBattery)
                {
                    foreach (var tw in tweaks)
                    {
                        if (plan.Items.Count >= MaxItems) break;
                        if (!string.Equals(tw.Category, "Power", StringComparison.OrdinalIgnoreCase)) continue;
                        TryAdd(plan, added, tweaks, tweakService, tw.Id,
                            "You're on a laptop — " + ReasonFor(tweaks, tw.Id), false);
                    }
                }
            }

            // 3. Recommended tweaks fill any slots still open.
            if (plan.Items.Count < MaxItems)
            {
                foreach (var tw in tweaks)
                {
                    if (plan.Items.Count >= MaxItems) break;
                    if (!tw.Recommended) continue;
                    TryAdd(plan, added, tweaks, tweakService, tw.Id,
                        "Recommended — " + ReasonFor(tweaks, tw.Id), false);
                }
            }

            // Notes, not tweaks.
            if (snap != null && snap.PendingReboot)
                plan.Notes.Add("Windows has a reboot pending — restart first so earlier changes finish before you judge this plan.");
            if (batterySaving)
            {
                var onNow = new List<string>();
                foreach (string id in BatteryCostIds)
                {
                    var tw = tweaks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (tw == null) continue;
                    if (tweakService.GetState(tw) == TweakState.Applied) onNow.Add(tw.Name);
                }
                if (onNow.Count > 0)
                {
                    string names = onNow.Count <= 3
                        ? string.Join(", ", onNow)
                        : string.Join(", ", onNow.Take(3)) + ", and " + (onNow.Count - 3) + " more";
                    plan.Notes.Add("Already on and using extra battery: " + names +
                        ". Revert them from the Presets page for the full saving.");
                }
            }
            return plan;
        }

        // Adds a tweak to the plan unless it is already there, already
        // applied, or (when Unknown) not a goal match. Unknown means the
        // state couldn't be read — it is never treated as applied.
        private static void TryAdd(AiPlan plan, HashSet<string> added,
            List<TweakDefinition> tweaks, TweakService tweakService,
            string id, string reason, bool isGoal)
        {
            if (plan.Items.Count >= MaxItems) return;
            if (!added.Add(id)) return;
            var tw = tweaks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            if (tw == null) return;
            var state = tweakService.GetState(tw);
            if (state == TweakState.Applied) return;
            if (state == TweakState.Unknown && !isGoal) return;
            plan.Items.Add(new AiPlanItem { Tweak = tw, Reason = reason });
        }

        // The tweak's own description, first sentence, lowercased so it
        // reads after the "You asked for … — " lead-in.
        private static string ReasonFor(List<TweakDefinition> tweaks, string id)
        {
            var tw = tweaks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            if (tw == null || string.IsNullOrWhiteSpace(tw.Description)) return "";
            string first = FirstSentence(tw.Description.Trim());
            if (first.Length == 0) return "";
            return char.ToLowerInvariant(first[0]) + first.Substring(1);
        }

        // First sentence only. The split needs a capital after ". " so
        // "e.g. photo.jpg" doesn't cut a sentence in half.
        private static string FirstSentence(string description)
        {
            for (int i = 0; i + 2 < description.Length; i++)
            {
                if (description[i] == '.' && description[i + 1] == ' ' &&
                    char.IsUpper(description[i + 2]))
                    return description.Substring(0, i + 1);
            }
            return description;
        }

        private static void AddCats(List<string> cats, params string[] names)
        {
            foreach (string n in names)
                if (!cats.Contains(n, StringComparer.OrdinalIgnoreCase))
                    cats.Add(n);
        }

        // Word match, not substring: "display" must not match "play",
        // but "games" and "playing" still match "game" and "play".
        private static bool HasWord(string text, params string[] words)
        {
            var parts = text.Split(
                new[] { ' ', ',', '.', '!', '?', ';', ':', '/', '\'', '"', '(', ')', '-', '_' },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (string p in parts)
                foreach (string w in words)
                    if (p == w || p.StartsWith(w, StringComparison.Ordinal))
                        return true;
            return false;
        }
    }
}
