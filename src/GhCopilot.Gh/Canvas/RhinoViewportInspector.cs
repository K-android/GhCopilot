using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GhCopilot.Core.Graph;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace GhCopilot.Gh.Canvas
{
    /// <summary>
    /// Reads privacy-safe scene & viewport metadata (active camera, display mode, units, visible layers,
    /// object counts, and bounding boxes) from RhinoDoc.ActiveDoc on the Rhino UI thread.
    /// </summary>
    public static class RhinoViewportInspector
    {
        private const int MaxObjectsToScan = 2000;
        private const int MaxSampleObjects = 25;
        private const int MaxVisibleLayers = 20;

        public static RhinoViewportSnapshot InspectActiveDoc()
        {
            var snapshot = new RhinoViewportSnapshot();
            try
            {
                RhinoDoc? doc = RhinoDoc.ActiveDoc;
                if (doc == null)
                {
                    return snapshot;
                }

                snapshot.HasActiveDocument = true;
                snapshot.DocumentName = string.IsNullOrWhiteSpace(doc.Name) ? "(untitled)" : doc.Name;
                snapshot.ModelUnits = doc.ModelUnitSystem.ToString();

                RhinoView? activeView = doc.Views?.ActiveView;
                RhinoViewport? vp = activeView?.ActiveViewport;
                if (vp != null)
                {
                    snapshot.ActiveViewName = vp.Name ?? string.Empty;
                    snapshot.DisplayMode = vp.DisplayMode?.EnglishName ?? vp.DisplayMode?.LocalName ?? "Default";
                    snapshot.Projection = vp.IsPerspectiveProjection
                        ? (vp.IsTwoPointPerspectiveProjection ? "TwoPointPerspective" : "Perspective")
                        : (vp.IsParallelProjection ? "Parallel" : "Unknown");
                    snapshot.CameraLocation = FormatPoint(vp.CameraLocation);
                    snapshot.CameraTarget = FormatPoint(vp.CameraTarget);
                    snapshot.CameraDirection = FormatVector(vp.CameraDirection);
                }

                RhinoView[]? views = doc.Views?.GetViewList(ViewTypeFilter.Model);
                if (views != null)
                {
                    foreach (RhinoView view in views)
                    {
                        string? viewName = view?.ActiveViewport?.Name;
                        if (!string.IsNullOrWhiteSpace(viewName) && !snapshot.ViewportNames.Contains(viewName!))
                        {
                            snapshot.ViewportNames.Add(viewName!);
                        }
                    }
                }

                BoundingBox sceneBounds = BoundingBox.Unset;
                BoundingBox selectedBounds = BoundingBox.Unset;
                var layerObjectCounts = new Dictionary<int, int>();
                var selectedSamples = new List<string>();
                var otherSamples = new List<string>();

                int scanned = 0;
                foreach (RhinoObject obj in doc.Objects)
                {
                    if (scanned >= MaxObjectsToScan) break;
                    if (obj == null || obj.IsDeleted || obj.IsHidden) continue;

                    Layer? layer = doc.Layers?.FindIndex(obj.Attributes.LayerIndex);
                    if (layer != null && !layer.IsVisible) continue;

                    scanned++;
                    snapshot.TotalVisibleObjects++;

                    string typeName = obj.ObjectType.ToString();
                    snapshot.ObjectCountsByType[typeName] =
                        snapshot.ObjectCountsByType.TryGetValue(typeName, out int existingCount) ? existingCount + 1 : 1;

                    if (layerObjectCounts.TryGetValue(obj.Attributes.LayerIndex, out int lCount))
                    {
                        layerObjectCounts[obj.Attributes.LayerIndex] = lCount + 1;
                    }
                    else
                    {
                        layerObjectCounts[obj.Attributes.LayerIndex] = 1;
                    }

                    BoundingBox objBounds = obj.Geometry != null ? obj.Geometry.GetBoundingBox(false) : BoundingBox.Unset;
                    if (objBounds.IsValid)
                    {
                        sceneBounds = sceneBounds.IsValid ? BoundingBox.Union(sceneBounds, objBounds) : objBounds;
                    }

                    bool isSelected = obj.IsSelected(false) != 0;
                    if (isSelected)
                    {
                        snapshot.SelectedObjectCount++;
                        snapshot.SelectedCountsByType[typeName] =
                            snapshot.SelectedCountsByType.TryGetValue(typeName, out int sCount) ? sCount + 1 : 1;
                        if (objBounds.IsValid)
                        {
                            selectedBounds = selectedBounds.IsValid ? BoundingBox.Union(selectedBounds, objBounds) : objBounds;
                        }
                    }

                    string summary = FormatObjectSample(typeName, layer?.FullPath ?? layer?.Name, obj.Name, objBounds, isSelected);
                    if (isSelected && selectedSamples.Count < MaxSampleObjects)
                    {
                        selectedSamples.Add(summary);
                    }
                    else if (!isSelected && otherSamples.Count < MaxSampleObjects)
                    {
                        otherSamples.Add(summary);
                    }
                }

                snapshot.SceneBoundingBox = FormatBoundingBox(sceneBounds);
                snapshot.SelectedBoundingBox = FormatBoundingBox(selectedBounds);

                foreach (string s in selectedSamples)
                {
                    if (snapshot.SampleObjects.Count >= MaxSampleObjects) break;
                    snapshot.SampleObjects.Add(s);
                }

                foreach (string s in otherSamples)
                {
                    if (snapshot.SampleObjects.Count >= MaxSampleObjects) break;
                    snapshot.SampleObjects.Add(s);
                }

                if (doc.Layers != null)
                {
                    foreach (Layer layer in doc.Layers)
                    {
                        if (layer == null || layer.IsDeleted || !layer.IsVisible) continue;
                        if (snapshot.VisibleLayers.Count >= MaxVisibleLayers) break;
                        string layerName = string.IsNullOrWhiteSpace(layer.FullPath) ? layer.Name : layer.FullPath;
                        int count = layerObjectCounts.TryGetValue(layer.Index, out int cnt) ? cnt : 0;
                        snapshot.VisibleLayers.Add($"{layerName} ({count})");
                    }
                }
            }
            catch (Exception)
            {
                // Best-effort inspection: never fail prompt generation if RhinoDoc/Views throws mid-edit.
            }

            return snapshot;
        }

        private static string FormatObjectSample(string typeName, string? layerName, string? objectName, BoundingBox bounds, bool isSelected)
        {
            var tags = new List<string>();
            if (isSelected) tags.Add("[Selected]");
            tags.Add(typeName);
            if (!string.IsNullOrWhiteSpace(objectName)) tags.Add($"\"{objectName}\"");
            if (!string.IsNullOrWhiteSpace(layerName)) tags.Add($"layer={layerName}");
            if (bounds.IsValid) tags.Add($"bounds={FormatBoundingBox(bounds)}");
            return string.Join(" | ", tags);
        }

        private static string FormatPoint(Point3d pt) =>
            $"({pt.X.ToString("F2", CultureInfo.InvariantCulture)}, {pt.Y.ToString("F2", CultureInfo.InvariantCulture)}, {pt.Z.ToString("F2", CultureInfo.InvariantCulture)})";

        private static string FormatVector(Vector3d v) =>
            $"({v.X.ToString("F2", CultureInfo.InvariantCulture)}, {v.Y.ToString("F2", CultureInfo.InvariantCulture)}, {v.Z.ToString("F2", CultureInfo.InvariantCulture)})";

        private static string FormatBoundingBox(BoundingBox box)
        {
            if (!box.IsValid) return "none";
            Vector3d diag = box.Diagonal;
            return $"{FormatPoint(box.Min)}..{FormatPoint(box.Max)} (size {diag.X.ToString("F2", CultureInfo.InvariantCulture)}x{diag.Y.ToString("F2", CultureInfo.InvariantCulture)}x{diag.Z.ToString("F2", CultureInfo.InvariantCulture)})";
        }
    }
}
