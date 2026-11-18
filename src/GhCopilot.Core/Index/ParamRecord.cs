using System.Text.Json.Serialization;

namespace GhCopilot.Core.Index
{
    /// <summary>
    /// Tier-2 parameter metadata hydrated on demand for shortlisted Grasshopper components.
    /// </summary>
    public sealed class ParamRecord
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("nickName")]
        public string NickName { get; set; } = string.Empty;

        [JsonPropertyName("typeName")]
        public string TypeName { get; set; } = string.Empty;

        [JsonPropertyName("access")]
        public string Access { get; set; } = "item";

        [JsonPropertyName("optional")]
        public bool Optional { get; set; }
    }
}
