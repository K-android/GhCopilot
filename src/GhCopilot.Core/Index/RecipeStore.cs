using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GhCopilot.Core.Graph;

namespace GhCopilot.Core.Index
{
    public sealed class RecipeRecord
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("createdAtUtc")]
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("spec")]
        public GraphSpec Spec { get; set; } = new GraphSpec();
    }

    /// <summary>
    /// Stores and retrieves reusable multi-node Grasshopper recipes saved from canvas selections
    /// for few-shot automation prompts.
    /// </summary>
    public static class RecipeStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static string GetDefaultRecipesPath()
        {
            return Path.Combine(IndexStore.GetDefaultDataDirectory(), "recipes.json");
        }

        public static List<RecipeRecord> LoadAll(string? localFilePath = null, string? sharedLibraryFolder = null)
        {
            var merged = new List<RecipeRecord>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string localPath = string.IsNullOrWhiteSpace(localFilePath) ? GetDefaultRecipesPath() : localFilePath!;
            AppendRecipesFromFile(localPath, merged, seenNames);

            if (!string.IsNullOrWhiteSpace(sharedLibraryFolder))
            {
                try
                {
                    string sharedPath = Path.Combine(sharedLibraryFolder!.Trim(), "recipes.json");
                    AppendRecipesFromFile(sharedPath, merged, seenNames);
                }
                catch (Exception)
                {
                    // Ignore inaccessible shared folder recipes.
                }
            }

            return merged;
        }

        public static void SaveRecipe(RecipeRecord recipe, string? localFilePath = null)
        {
            if (recipe is null) throw new ArgumentNullException(nameof(recipe));
            if (string.IsNullOrWhiteSpace(recipe.Name))
            {
                throw new ArgumentException("Recipe name must not be empty.", nameof(recipe));
            }

            string path = string.IsNullOrWhiteSpace(localFilePath) ? GetDefaultRecipesPath() : localFilePath!;
            List<RecipeRecord> existing = LoadAll(path, sharedLibraryFolder: null);

            existing.RemoveAll(r => string.Equals(r.Name, recipe.Name, StringComparison.OrdinalIgnoreCase));
            existing.Add(recipe);

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(existing, JsonOptions);
            File.WriteAllText(path, json, Encoding.UTF8);
        }

        public static RecipeRecord CreateFromCanvasSnapshot(string name, string description, CanvasSnapshot snapshot)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Recipe name must not be empty.", nameof(name));
            }

            if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));

            List<CanvasNodeSnapshot> selectedNodes = snapshot.Nodes
                .Where(n => n.IsSelected && !string.IsNullOrWhiteSpace(n.ShortId))
                .ToList();

            var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var nodes = new List<NodeSpec>(selectedNodes.Count);

            for (int i = 0; i < selectedNodes.Count; i++)
            {
                CanvasNodeSnapshot src = selectedNodes[i];
                string newId = $"n{i + 1}";
                idMap[src.Id] = newId;

                nodes.Add(new NodeSpec
                {
                    Id = newId,
                    Ref = src.ShortId,
                    X = src.X,
                    Y = src.Y,
                    Settings = src.Settings
                });
            }

            var wires = new List<WireSpec>();
            foreach (CanvasWireSnapshot w in snapshot.Wires)
            {
                if (idMap.TryGetValue(w.FromNode, out string? fromId) &&
                    idMap.TryGetValue(w.ToNode, out string? toId))
                {
                    wires.Add(new WireSpec
                    {
                        FromNode = fromId,
                        FromParam = w.FromParam,
                        ToNode = toId,
                        ToParam = w.ToParam,
                        TargetMapping = w.TargetMapping,
                        Simplify = w.Simplify,
                        Reverse = w.Reverse
                    });
                }
            }

            var spec = new GraphSpec
            {
                Nodes = nodes,
                Wires = wires,
                Explanation = string.IsNullOrWhiteSpace(description) ? name.Trim() : description.Trim()
            };

            if (spec.Nodes.Count > 1)
            {
                GraphAutoLayout.ApplyLayeredLayout(spec);
            }

            return new RecipeRecord
            {
                Name = name.Trim(),
                Description = string.IsNullOrWhiteSpace(description) ? name.Trim() : description.Trim(),
                CreatedAtUtc = DateTime.UtcNow,
                Spec = spec
            };
        }

        public static IReadOnlyList<RecipeRecord> RetrieveMatchingRecipes(
            IReadOnlyList<RecipeRecord> recipes,
            string query,
            int maxRecipes = 2)
        {
            if (recipes is null || recipes.Count == 0 || maxRecipes <= 0)
            {
                return Array.Empty<RecipeRecord>();
            }

            List<string> queryTokens = Retriever.Tokenize(query);
            if (queryTokens.Count == 0)
            {
                return recipes.Take(maxRecipes).ToList();
            }

            var querySet = new HashSet<string>(queryTokens, StringComparer.Ordinal);

            return recipes
                .Select(recipe =>
                {
                    List<string> recipeTokens = Retriever.Tokenize($"{recipe.Name} {recipe.Description} {recipe.Spec?.Explanation}");
                    int overlap = recipeTokens.Count(t => querySet.Contains(t));
                    return (Recipe: recipe, Score: overlap);
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Recipe.CreatedAtUtc)
                .Take(maxRecipes)
                .Select(x => x.Recipe)
                .ToList();
        }

        private static void AppendRecipesFromFile(string path, List<RecipeRecord> target, HashSet<string> seenNames)
        {
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                List<RecipeRecord>? list = JsonSerializer.Deserialize<List<RecipeRecord>>(json, JsonOptions);
                if (list is null)
                {
                    return;
                }

                foreach (RecipeRecord item in list)
                {
                    if (item != null && !string.IsNullOrWhiteSpace(item.Name) && seenNames.Add(item.Name))
                    {
                        target.Add(item);
                    }
                }
            }
            catch (Exception)
            {
                // Ignore malformed recipes file gracefully.
            }
        }
    }
}
