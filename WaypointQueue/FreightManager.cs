using HarmonyLib;
using Model.Ops;
using System;
using System.Collections.Generic;
using System.Linq;
using WaypointQueue.UUM;

namespace WaypointQueue
{
    [HarmonyPatch(typeof(OpsController))]
    internal static class OpsControllerPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch("RebuildCollections")]
        private static void RebuildCollectionsPostfix()
        {
            Loader.LogDebug("[FreightManager] OpsController.RebuildCollections – rebuilding destination list.");
            FreightManager.RebuildFromOps();
        }
    }

    internal static class FreightManager
    {
        // (Id, Label) where:
        //   Id    = OpsCarPosition.Identifier (track identifier)
        //   Label = OpsCarPosition.DisplayName or Identifier as a fallback
        private static readonly List<(string Id, string Label)> _destinations = new();
        public static IReadOnlyList<(string Id, string Label)> Destinations => _destinations;

        public static void RebuildFromOps()
        {
            var ops = OpsController.Shared;
            if (ops == null)
            {
                Loader.LogDebug("[FreightManager] OpsController.Shared is null; cannot rebuild destinations.");
                return;
            }

            _destinations.Clear();

            // Only include industries that are not progression-disabled
            foreach (var industry in ops.AllIndustries.Where(i => !i.ProgressionDisabled))
            {
                foreach (var ic in industry.Components)
                {
                    // IndustryComponent -> OpsCarPosition implicit conversion
                    OpsCarPosition pos = ic;

                    if (string.IsNullOrEmpty(pos.Identifier))
                        continue;

                    if (pos.Identifier.EndsWith(".formula", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string label = string.IsNullOrEmpty(pos.DisplayName)
                        ? pos.Identifier
                        : pos.DisplayName;

                    _destinations.Add((pos.Identifier, label));
                }
            }

            // Deduplicate by Id (keep first) and sort by label
            var deduped = _destinations
                .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(d => d.Label, StringComparer.InvariantCultureIgnoreCase)
                .ToList();

            _destinations.Clear();
            _destinations.AddRange(deduped);

            Loader.LogDebug($"[FreightManager] Built {_destinations.Count} destinations.");
        }

        public static IReadOnlyCollection<string> GetOpsIdsForLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return Array.Empty<string>();

            string norm = label.Trim();

            // There may be multiple entries with the same label but different Ids.
            var ids = _destinations
                .Where(d => string.Equals(d.Label, norm, StringComparison.InvariantCultureIgnoreCase))
                .Select(d => d.Id)
                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                .ToList();

            return ids;
        }
    }
}
