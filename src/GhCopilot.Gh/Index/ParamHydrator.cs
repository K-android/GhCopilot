using System;
using System.Collections.Generic;
using GhCopilot.Core.Index;
using GhCopilot.Gh.Canvas;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace GhCopilot.Gh.Index
{
    /// <summary>
    /// Lazily instantiates shortlisted proxies (Tier 2) on the UI thread to read input/output parameter schemas.
    /// </summary>
    public static class ParamHydrator
    {
        public static void HydrateShortlist(IEnumerable<ComponentRecord> candidates)
        {
            if (candidates is null)
            {
                throw new ArgumentNullException(nameof(candidates));
            }

            var toHydrate = new List<ComponentRecord>();
            foreach (ComponentRecord record in candidates)
            {
                if (record != null && !record.IsHydrated && record.ComponentGuid != Guid.Empty)
                {
                    toHydrate.Add(record);
                }
            }

            if (toHydrate.Count == 0)
            {
                return;
            }

            UiThread.InvokeOnUi(() =>
            {
                GH_ComponentServer? server = Instances.ComponentServer;
                if (server is null)
                {
                    return;
                }

                foreach (ComponentRecord record in toHydrate)
                {
                    HydrateSingle(server, record);
                }
            });
        }

        private static void HydrateSingle(GH_ComponentServer server, ComponentRecord record)
        {
            IGH_ObjectProxy? proxy = server.EmitObjectProxy(record.ComponentGuid)
                                     ?? UserObjectFolderLoader.TryGetCompanyProxy(record.ComponentGuid);
            if (proxy is null)
            {
                record.IsHydrated = true;
                return;
            }

            IGH_DocumentObject? instance = null;
            try
            {
                instance = proxy.CreateInstance();
                if (instance is null)
                {
                    record.IsHydrated = true;
                    return;
                }

                record.Inputs.Clear();
                record.Outputs.Clear();

                if (instance is IGH_Component component)
                {
                    record.IsStandaloneParam = false;

                    foreach (IGH_Param input in component.Params.Input)
                    {
                        record.Inputs.Add(ToParamRecord(input));
                    }

                    foreach (IGH_Param output in component.Params.Output)
                    {
                        record.Outputs.Add(ToParamRecord(output));
                    }
                }
                else if (instance is GH_NumberSlider)
                {
                    record.IsStandaloneParam = true;
                    record.Outputs.Add(new ParamRecord
                    {
                        Name = "Number",
                        NickName = "N",
                        TypeName = "Number",
                        Access = "item",
                        Optional = false
                    });
                }
                else if (instance is IGH_Param param)
                {
                    record.IsStandaloneParam = true;
                    ParamRecord pr = ToParamRecord(param);
                    record.Inputs.Add(pr);
                    record.Outputs.Add(new ParamRecord
                    {
                        Name = pr.Name,
                        NickName = pr.NickName,
                        TypeName = pr.TypeName,
                        Access = pr.Access,
                        Optional = pr.Optional
                    });
                }

                record.IsHydrated = true;
            }
            catch (Exception)
            {
                // Some third-party components misbehave outside a live document; mark hydrated so we don't retry infinitely.
                record.IsHydrated = true;
            }
            finally
            {
                if (instance is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }

        private static ParamRecord ToParamRecord(IGH_Param param)
        {
            return new ParamRecord
            {
                Name = param.Name ?? string.Empty,
                NickName = param.NickName ?? string.Empty,
                TypeName = string.IsNullOrWhiteSpace(param.TypeName) ? "Generic" : param.TypeName,
                Access = param.Access.ToString().ToLowerInvariant(),
                Optional = param.Optional
            };
        }
    }
}
