using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace GhCopilot.Core.Graph
{
    /// <summary>
    /// Lightweight, privacy-safe summary of the active Rhino 8 document, viewport camera, visible layers,
    /// object counts, and bounding boxes (contains zero raw mesh/NURBS geometry or instance GUIDs).
    /// </summary>
    public sealed class RhinoViewportSnapshot
    {
        [JsonPropertyName("hasActiveDocument")]
        public bool HasActiveDocument { get; set; }

        [JsonPropertyName("documentName")]
        public string DocumentName { get; set; } = string.Empty;

        [JsonPropertyName("modelUnits")]
        public string ModelUnits { get; set; } = string.Empty;

        [JsonPropertyName("activeViewName")]
        public string ActiveViewName { get; set; } = string.Empty;

        [JsonPropertyName("displayMode")]
        public string DisplayMode { get; set; } = string.Empty;

        [JsonPropertyName("projection")]
        public string Projection { get; set; } = string.Empty;

        [JsonPropertyName("cameraLocation")]
        public string CameraLocation { get; set; } = string.Empty;

        [JsonPropertyName("cameraTarget")]
        public string CameraTarget { get; set; } = string.Empty;

        [JsonPropertyName("cameraDirection")]
        public string CameraDirection { get; set; } = string.Empty;

        [JsonPropertyName("viewportNames")]
        public List<string> ViewportNames { get; set; } = new List<string>();

        [JsonPropertyName("totalVisibleObjects")]
        public int TotalVisibleObjects { get; set; }

        [JsonPropertyName("selectedObjectCount")]
        public int SelectedObjectCount { get; set; }

        [JsonPropertyName("objectCountsByType")]
        public Dictionary<string, int> ObjectCountsByType { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("selectedCountsByType")]
        public Dictionary<string, int> SelectedCountsByType { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("visibleLayers")]
        public List<string> VisibleLayers { get; set; } = new List<string>();

        [JsonPropertyName("sceneBoundingBox")]
        public string SceneBoundingBox { get; set; } = string.Empty;

        [JsonPropertyName("selectedBoundingBox")]
        public string SelectedBoundingBox { get; set; } = string.Empty;

        [JsonPropertyName("sampleObjects")]
        public List<string> SampleObjects { get; set; } = new List<string>();

        public string FormatForPrompt()
        {
            if (!HasActiveDocument)
            {
                return "No active Rhino document.";
            }

            var sb = new StringBuilder();
            string docName = string.IsNullOrWhiteSpace(DocumentName) ? "(untitled)" : DocumentName;
            string units = string.IsNullOrWhiteSpace(ModelUnits) ? "Unknown" : ModelUnits;
            sb.AppendLine($"Document: {docName}, Units: {units}");

            if (!string.IsNullOrWhiteSpace(ActiveViewName) || !string.IsNullOrWhiteSpace(Projection))
            {
                string viewName = string.IsNullOrWhiteSpace(ActiveViewName) ? "Unnamed" : ActiveViewName;
                string proj = string.IsNullOrWhiteSpace(Projection) ? "Unknown" : Projection;
                string mode = string.IsNullOrWhiteSpace(DisplayMode) ? "Default" : DisplayMode;
                sb.AppendLine($"ActiveViewport: {viewName} ({proj}, DisplayMode={mode}), Camera={CameraLocation}, Target={CameraTarget}, Direction={CameraDirection}");
            }

            if (ViewportNames != null && ViewportNames.Count > 0)
            {
                sb.AppendLine($"Viewports: {string.Join(", ", ViewportNames)}");
            }

            string typeBreakdown = FormatCounts(ObjectCountsByType);
            string bounds = string.IsNullOrWhiteSpace(SceneBoundingBox) ? "none" : SceneBoundingBox;
            sb.AppendLine($"VisibleObjects: {TotalVisibleObjects}{(string.IsNullOrEmpty(typeBreakdown) ? string.Empty : $" ({typeBreakdown})")}, SceneBounds: {bounds}");

            string selectedBreakdown = FormatCounts(SelectedCountsByType);
            string selectedBounds = string.IsNullOrWhiteSpace(SelectedBoundingBox) ? "none" : SelectedBoundingBox;
            sb.AppendLine($"SelectedObjects: {SelectedObjectCount}{(string.IsNullOrEmpty(selectedBreakdown) ? string.Empty : $" ({selectedBreakdown})")}, SelectedBounds: {selectedBounds}");

            if (VisibleLayers != null && VisibleLayers.Count > 0)
            {
                sb.AppendLine($"VisibleLayers: {string.Join(", ", VisibleLayers)}");
            }

            if (SampleObjects != null && SampleObjects.Count > 0)
            {
                sb.AppendLine("Objects (up to 25):");
                foreach (string item in SampleObjects.Take(25))
                {
                    sb.AppendLine($"- {item}");
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static string FormatCounts(Dictionary<string, int>? counts)
        {
            if (counts == null || counts.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(", ", counts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"{kv.Key}={kv.Value}"));
        }
    }
}
