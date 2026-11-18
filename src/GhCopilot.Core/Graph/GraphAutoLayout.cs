using System;
using System.Collections.Generic;
using System.Linq;

namespace GhCopilot.Core.Graph
{
    /// <summary>
    /// Assigns non-overlapping left-to-right canvas coordinates using topological DAG layering
    /// and barycenter ordering to minimize wire crossings on complex definitions.
    /// </summary>
    public static class GraphAutoLayout
    {
        public static bool NeedsAutoLayout(GraphSpec spec)
        {
            if (spec is null || spec.Nodes.Count <= 1)
            {
                return false;
            }

            bool allOrigin = spec.Nodes.All(n => Math.Abs(n.X) < 1f && Math.Abs(n.Y) < 1f);
            if (allOrigin)
            {
                return true;
            }

            for (int i = 0; i < spec.Nodes.Count; i++)
            {
                for (int j = i + 1; j < spec.Nodes.Count; j++)
                {
                    float dx = Math.Abs(spec.Nodes[i].X - spec.Nodes[j].X);
                    float dy = Math.Abs(spec.Nodes[i].Y - spec.Nodes[j].Y);
                    if (dx < 60f && dy < 60f)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static void ApplyLayeredLayout(
            GraphSpec spec,
            float anchorX = 160f,
            float anchorY = 180f,
            float columnSpacing = 220f,
            float rowSpacing = 95f)
        {
            if (spec is null)
            {
                throw new ArgumentNullException(nameof(spec));
            }

            if (spec.Nodes.Count == 0)
            {
                return;
            }

            var nodesById = new Dictionary<string, NodeSpec>(StringComparer.Ordinal);
            var originalOrder = new Dictionary<string, int>(StringComparer.Ordinal);
            var incoming = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var outgoing = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            for (int i = 0; i < spec.Nodes.Count; i++)
            {
                NodeSpec node = spec.Nodes[i];
                if (!string.IsNullOrWhiteSpace(node.Id) && !nodesById.ContainsKey(node.Id))
                {
                    nodesById[node.Id] = node;
                    originalOrder[node.Id] = i;
                    incoming[node.Id] = new List<string>();
                    outgoing[node.Id] = new List<string>();
                }
            }

            foreach (WireSpec wire in spec.Wires)
            {
                if (nodesById.ContainsKey(wire.FromNode) &&
                    nodesById.ContainsKey(wire.ToNode) &&
                    !string.Equals(wire.FromNode, wire.ToNode, StringComparison.Ordinal))
                {
                    outgoing[wire.FromNode].Add(wire.ToNode);
                    incoming[wire.ToNode].Add(wire.FromNode);
                }
            }

            // Compute topological layer using Kahn-style longest-path propagation, bounded against cycles.
            var layers = new Dictionary<string, int>(StringComparer.Ordinal);
            var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
            var queue = new Queue<string>();

            foreach (string id in nodesById.Keys)
            {
                layers[id] = 0;
                inDegree[id] = incoming[id].Count;
                if (inDegree[id] == 0)
                {
                    queue.Enqueue(id);
                }
            }

            int visitedCount = 0;
            while (queue.Count > 0 && visitedCount <= nodesById.Count * 2)
            {
                string current = queue.Dequeue();
                visitedCount++;
                int nextLayer = layers[current] + 1;

                foreach (string target in outgoing[current])
                {
                    if (nextLayer > layers[target] && nextLayer <= nodesById.Count)
                    {
                        layers[target] = nextLayer;
                    }

                    inDegree[target]--;
                    if (inDegree[target] == 0)
                    {
                        queue.Enqueue(target);
                    }
                }
            }

            int maxLayer = layers.Values.Count > 0 ? layers.Values.Max() : 0;
            var byLayer = new Dictionary<int, List<NodeSpec>>();
            for (int l = 0; l <= maxLayer; l++)
            {
                byLayer[l] = new List<NodeSpec>();
            }

            foreach (NodeSpec node in spec.Nodes)
            {
                int layer = (!string.IsNullOrWhiteSpace(node.Id) && layers.TryGetValue(node.Id, out int l)) ? l : 0;
                byLayer[layer].Add(node);
            }

            // Order layer 0 by original Y (then original index), then each subsequent layer by upstream barycenter Y.
            var assignedY = new Dictionary<string, float>(StringComparer.Ordinal);

            if (byLayer.TryGetValue(0, out List<NodeSpec>? layer0))
            {
                List<NodeSpec> ordered0 = layer0
                    .OrderBy(n => n.Y)
                    .ThenBy(n => originalOrder.TryGetValue(n.Id, out int idx) ? idx : 0)
                    .ToList();

                for (int i = 0; i < ordered0.Count; i++)
                {
                    ordered0[i].X = anchorX;
                    ordered0[i].Y = anchorY + i * rowSpacing;
                    assignedY[ordered0[i].Id] = ordered0[i].Y;
                }
            }

            for (int l = 1; l <= maxLayer; l++)
            {
                List<NodeSpec> layerNodes = byLayer[l];
                if (layerNodes.Count == 0)
                {
                    continue;
                }

                List<NodeSpec> ordered = layerNodes
                    .OrderBy(n => ComputeBarycenterY(n, incoming, assignedY))
                    .ThenBy(n => originalOrder.TryGetValue(n.Id, out int idx) ? idx : 0)
                    .ToList();

                for (int i = 0; i < ordered.Count; i++)
                {
                    ordered[i].X = anchorX + l * columnSpacing;
                    ordered[i].Y = anchorY + i * rowSpacing;
                    assignedY[ordered[i].Id] = ordered[i].Y;
                }
            }
        }

        private static float ComputeBarycenterY(
            NodeSpec node,
            Dictionary<string, List<string>> incoming,
            Dictionary<string, float> assignedY)
        {
            if (string.IsNullOrWhiteSpace(node.Id) || !incoming.TryGetValue(node.Id, out List<string>? parents) || parents.Count == 0)
            {
                return node.Y;
            }

            float sum = 0f;
            int count = 0;
            foreach (string parentId in parents)
            {
                if (assignedY.TryGetValue(parentId, out float py))
                {
                    sum += py;
                    count++;
                }
            }

            return count > 0 ? sum / count : node.Y;
        }
    }
}
