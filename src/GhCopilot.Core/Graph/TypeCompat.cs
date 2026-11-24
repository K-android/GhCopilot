using System;
using System.Collections.Generic;

namespace GhCopilot.Core.Graph
{
    /// <summary>
    /// Evaluates Grasshopper wire type compatibility using an explicit allow-list and Generic wildcards.
    /// </summary>
    public static class TypeCompat
    {
        private static readonly HashSet<string> Wildcards = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "",
            "generic",
            "generic data",
            "data",
            "object",
            "any",
            "system.object"
        };

        private static readonly HashSet<string> GeometryTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "point",
            "vector",
            "plane",
            "line",
            "circle",
            "arc",
            "polyline",
            "rectangle",
            "ellipse",
            "curve",
            "surface",
            "brep",
            "mesh",
            "box",
            "extrusion",
            "subd",
            "geometry"
        };

        private static readonly Dictionary<string, HashSet<string>> AllowList =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["integer"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "number", "complex", "domain" },
                ["number"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "integer", "complex", "domain" },
                ["complex"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "number", "integer" },
                ["boolean"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "integer", "number" },
                ["text"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "number", "integer", "boolean", "domain", "point", "vector", "plane", "complex", "colour", "color" },
                ["point"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vector", "plane" },
                ["vector"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "point", "line" },
                ["line"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "curve" },
                ["circle"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "curve", "plane", "surface", "brep" },
                ["arc"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "curve", "plane" },
                ["polyline"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "curve", "surface", "brep" },
                ["rectangle"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "curve", "plane", "surface", "brep" },
                ["ellipse"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "curve", "plane", "surface", "brep" },
                ["curve"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "surface", "brep" },
                ["plane"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "circle", "arc", "rectangle" },
                ["surface"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "brep", "mesh" },
                ["box"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "brep", "mesh" },
                ["extrusion"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "brep" },
                ["subd"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "brep", "mesh" },
                ["brep"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mesh" }
            };

        public static bool AreCompatible(string? sourceTypeName, string? targetTypeName)
        {
            string source = NormalizeTypeName(sourceTypeName);
            string target = NormalizeTypeName(targetTypeName);

            if (Wildcards.Contains(source) || Wildcards.Contains(target))
            {
                return true;
            }

            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Grasshopper converts any value to Text when wired into a Text/Panel parameter.
            if (string.Equals(target, "text", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(target, "geometry", StringComparison.OrdinalIgnoreCase) && GeometryTypes.Contains(source))
            {
                return true;
            }

            return AllowList.TryGetValue(source, out HashSet<string>? allowedTargets) && allowedTargets.Contains(target);
        }

        public static string NormalizeTypeName(string? rawTypeName)
        {
            if (string.IsNullOrWhiteSpace(rawTypeName))
            {
                return string.Empty;
            }

            string normalized = rawTypeName!.Trim().ToLowerInvariant();
            if (normalized.EndsWith(" parameter", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(0, normalized.Length - " parameter".Length).Trim();
            }
            else if (normalized.EndsWith(" param", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(0, normalized.Length - " param".Length).Trim();
            }

            return normalized switch
            {
                "int" or "int32" => "integer",
                "double" or "float" or "real" or "num" => "number",
                "string" or "str" => "text",
                "bool" => "boolean",
                "pt" or "point3d" => "point",
                "vec" or "vector3d" => "vector",
                "interval" => "domain",
                "srf" => "surface",
                "crv" => "curve",
                "pln" => "plane",
                "geo" or "geometrybase" => "geometry",
                _ => normalized
            };
        }
    }
}
