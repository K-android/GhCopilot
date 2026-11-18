using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace GhCopilot.Core.Index
{
    /// <summary>
    /// Two-tier component index entry: Tier-1 proxy metadata plus lazily hydrated Tier-2 input/output parameters.
    /// </summary>
    public sealed class ComponentRecord
    {
        [JsonPropertyName("shortId")]
        public string ShortId { get; set; } = string.Empty;

        [JsonPropertyName("componentGuid")]
        public Guid ComponentGuid { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("nickName")]
        public string NickName { get; set; } = string.Empty;

        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        [JsonPropertyName("subCategory")]
        public string SubCategory { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = "native";

        [JsonPropertyName("isCompany")]
        public bool IsCompany { get; set; }

        [JsonPropertyName("isStandaloneParam")]
        public bool IsStandaloneParam { get; set; }

        [JsonPropertyName("isHydrated")]
        public bool IsHydrated { get; set; }

        [JsonPropertyName("inputs")]
        public List<ParamRecord> Inputs { get; set; } = new List<ParamRecord>();

        [JsonPropertyName("outputs")]
        public List<ParamRecord> Outputs { get; set; } = new List<ParamRecord>();

        /// <summary>
        /// Formats a compact prompt line matching Section 5.2 of the GH Copilot brief.
        /// </summary>
        public string FormatPromptContextLine()
        {
            var sb = new StringBuilder();
            sb.Append(ShortId);
            sb.Append(" | ");
            if (IsCompany)
            {
                sb.Append("[COMPANY] ");
            }
            sb.Append(Name);

            sb.Append(" | ");
            if (!string.IsNullOrWhiteSpace(Category) && !string.IsNullOrWhiteSpace(SubCategory))
            {
                sb.Append(Category);
                sb.Append('>');
                sb.Append(SubCategory);
            }
            else if (!string.IsNullOrWhiteSpace(Category))
            {
                sb.Append(Category);
            }
            else
            {
                sb.Append(Source);
            }

            sb.Append(" | in: ");
            if (Inputs.Count == 0)
            {
                sb.Append("none");
            }
            else
            {
                for (int i = 0; i < Inputs.Count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    ParamRecord p = Inputs[i];
                    sb.Append(p.Name);
                    sb.Append('(');
                    sb.Append(p.TypeName);
                    sb.Append(',');
                    sb.Append(p.Access);
                    sb.Append(')');
                }
            }

            sb.Append(" | out: ");
            if (Outputs.Count == 0)
            {
                sb.Append("none");
            }
            else
            {
                for (int i = 0; i < Outputs.Count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    ParamRecord p = Outputs[i];
                    sb.Append(p.Name);
                    sb.Append('(');
                    sb.Append(p.TypeName);
                    if (!string.Equals(p.Access, "item", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append(',');
                        sb.Append(p.Access);
                    }
                    sb.Append(')');
                }
            }

            return sb.ToString();
        }
    }
}
