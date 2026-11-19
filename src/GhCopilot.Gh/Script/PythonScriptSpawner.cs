using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using GhCopilot.Core.Graph;
using GhCopilot.Core.Index;
using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Parameters.Hints;

namespace GhCopilot.Gh.Script
{
    /// <summary>
    /// Spawns and configures a Rhino 8 Python 3 Script component (or GhPython fallback) with typed inputs, outputs,
    /// and injected Python code using embedded GH_Archive template + ComponentServer proxy + reflection.
    /// </summary>
    public static class PythonScriptSpawner
    {
        private static readonly string[] CodeMemberNames =
        {
            "Code",
            "ScriptSource",
            "Source",
            "ScriptText",
            "Text"
        };

        private static readonly string[] CodeMethodNames =
        {
            "SetScript",
            "SetCode",
            "SetSource"
        };

        public static IGH_DocumentObject Spawn(
            GH_Document doc,
            ScriptSpec script,
            PointF pivot,
            out List<string> warnings)
        {
            if (doc is null) throw new ArgumentNullException(nameof(doc));
            if (script is null) throw new ArgumentNullException(nameof(script));

            warnings = new List<string>();

            IGH_ObjectProxy? proxy = FindPythonScriptProxy();
            if (proxy is null)
            {
                throw new InvalidOperationException("No Python 3 Script (or GhPython) component proxy was found in Grasshopper's ComponentServer.");
            }

            IGH_DocumentObject instance = CreateAndCloneInstance(proxy, warnings);

            if (instance is IGH_Component component)
            {
                ConfigureParameters(component, script, warnings);
            }
            else
            {
                throw new InvalidOperationException($"Script object '{instance.Name}' does not implement IGH_Component; no graph was applied.");
            }

            bool injected = TryInjectCode(instance, script.Code, warnings);
            if (!injected)
            {
                WriteDiagnosticSpikeDump(instance);
                throw new InvalidOperationException("Could not inject Python code. No script was added; inspect %LOCALAPPDATA%\\GhCopilot\\python-component-spike.txt.");
            }

            if (instance.Attributes == null)
            {
                instance.CreateAttributes();
            }

            doc.AddObject(instance, false);

            if (instance.Attributes != null)
            {
                instance.Attributes.Pivot = pivot;
                instance.Attributes.ExpireLayout();
                instance.Attributes.PerformLayout();
            }

            return instance;
        }

        public static IGH_TypeHint ResolveTypeHint(string? typeName)
        {
            string normalized = TypeCompat.NormalizeTypeName(typeName);
            return normalized switch
            {
                "integer" => new GH_IntegerHint_CS(),
                "number" => new GH_DoubleHint_CS(),
                "boolean" => new GH_BooleanHint_CS(),
                "text" => new GH_StringHint_CS(),
                "point" => new GH_Point3dHint(),
                "vector" => new GH_Vector3dHint(),
                "plane" => new GH_PlaneHint(),
                "box" => new GH_BoxHint(),
                "domain" => new GH_IntervalHint(),
                "line" => new GH_LineHint(),
                "circle" => new GH_CircleHint(),
                "arc" => new GH_ArcHint(),
                "polyline" => new GH_PolylineHint(),
                "curve" => new GH_CurveHint(),
                "surface" => new GH_SurfaceHint(),
                "extrusion" => new GH_ExtrusionHint(),
                "brep" => new GH_BrepHint(),
                "subd" => new GH_SubDHint(),
                "mesh" => new GH_MeshHint(),
                "color" or "colour" => new GH_ColorHint(),
                "geometry" => new GH_GeometryBaseHint(),
                _ => new GH_NullHint()
            };
        }

        public static GH_ParamAccess ParseAccess(string? access)
        {
            return access?.Trim().ToLowerInvariant() switch
            {
                "list" => GH_ParamAccess.list,
                "tree" => GH_ParamAccess.tree,
                _ => GH_ParamAccess.item
            };
        }

        private static IGH_ObjectProxy? FindPythonScriptProxy()
        {
            IList<IGH_ObjectProxy>? proxies = Instances.ComponentServer?.ObjectProxies;
            if (proxies is null)
            {
                return null;
            }

            // 1. Prefer Rhino 8's Python 3 Script component (RhinoCodePluginGH).
            IGH_ObjectProxy? python3 = proxies.FirstOrDefault(p =>
                !p.Obsolete &&
                p.Desc != null &&
                (string.Equals(p.Desc.Name, "Python 3 Script", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(p.Desc.Name, "Python 3", StringComparison.OrdinalIgnoreCase) ||
                 (p.Type != null && p.Type.FullName != null &&
                  p.Type.FullName.IndexOf("RhinoCode", StringComparison.OrdinalIgnoreCase) >= 0 &&
                  p.Desc.Name.IndexOf("Python", StringComparison.OrdinalIgnoreCase) >= 0)));

            if (python3 != null)
            {
                return python3;
            }

            // 2. Fallback to any non-obsolete Python Script component (e.g. GhPython).
            return proxies.FirstOrDefault(p =>
                !p.Obsolete &&
                p.Desc != null &&
                (string.Equals(p.Desc.Name, "Python Script", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(p.Desc.Name, "GhPython Script", StringComparison.OrdinalIgnoreCase) ||
                 (p.Desc.Name.IndexOf("Python", StringComparison.OrdinalIgnoreCase) >= 0 &&
                  string.Equals(p.Desc.SubCategory, "Script", StringComparison.OrdinalIgnoreCase))));
        }

        private static IGH_DocumentObject CreateAndCloneInstance(IGH_ObjectProxy proxy, List<string> warnings)
        {
            IGH_DocumentObject raw = proxy.CreateInstance();
            if (raw is null)
            {
                throw new InvalidOperationException($"Proxy '{proxy.Desc.Name}' returned null from CreateInstance().");
            }

            // Verify embedded template resource loads cleanly and perform GH_Archive round-trip clone when supported.
            _ = LoadEmbeddedTemplateXml();

            try
            {
                var archive = new GH_Archive();
                archive.CreateNewRoot(true);
                if (archive.AppendObject(raw, "Object"))
                {
                    string xml = archive.Serialize_Xml();
                    var cloneArchive = new GH_Archive();
                    if (cloneArchive.Deserialize_Xml(xml))
                    {
                        IGH_DocumentObject? clone = proxy.CreateInstance();
                        if (clone != null && cloneArchive.ExtractObject(clone, "Object"))
                        {
                            if (raw is IDisposable disposable)
                            {
                                disposable.Dispose();
                            }
                            return clone;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"GH_Archive clone fallback used direct instance: {ex.Message}");
            }

            return raw;
        }

        private static void ConfigureParameters(IGH_Component component, ScriptSpec script, List<string> warnings)
        {
            IGH_VariableParameterComponent? varComp = component as IGH_VariableParameterComponent;

            if (varComp != null)
            {
                for (int i = component.Params.Input.Count - 1; i >= 0; i--)
                {
                    if (varComp.CanRemoveParameter(GH_ParameterSide.Input, i))
                    {
                        IGH_Param existing = component.Params.Input[i];
                        if (varComp.DestroyParameter(GH_ParameterSide.Input, i))
                        {
                            component.Params.UnregisterInputParameter(existing, true);
                        }
                    }
                }

                for (int i = component.Params.Output.Count - 1; i >= 0; i--)
                {
                    IGH_Param existing = component.Params.Output[i];
                    if (string.Equals(existing.Name, "out", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (varComp.CanRemoveParameter(GH_ParameterSide.Output, i) &&
                        varComp.DestroyParameter(GH_ParameterSide.Output, i))
                    {
                        component.Params.UnregisterOutputParameter(existing, true);
                    }
                }
            }

            // Configure inputs
            foreach (ScriptInputSpec inputSpec in script.Inputs)
            {
                IGH_Param? newInput = null;
                if (varComp != null && varComp.CanInsertParameter(GH_ParameterSide.Input, component.Params.Input.Count))
                {
                    newInput = varComp.CreateParameter(GH_ParameterSide.Input, component.Params.Input.Count);
                }

                newInput ??= new Param_ScriptVariable();
                newInput.Name = inputSpec.Name;
                newInput.NickName = inputSpec.Name;
                newInput.Access = ParseAccess(inputSpec.Access);
                newInput.Optional = true;

                if (newInput is Param_ScriptVariable scriptVar)
                {
                    scriptVar.TypeHint = ResolveTypeHint(inputSpec.Type);
                }

                if (!component.Params.RegisterInputParam(newInput))
                {
                    throw new InvalidOperationException($"Failed to register required script input '{inputSpec.Name}'.");
                }
            }

            // Configure outputs
            foreach (ScriptOutputSpec outputSpec in script.Outputs)
            {
                IGH_Param? existingMatch = component.Params.Output.FirstOrDefault(p =>
                    string.Equals(p.Name, outputSpec.Name, StringComparison.OrdinalIgnoreCase));
                if (existingMatch != null)
                {
                    continue;
                }

                IGH_Param? newOutput = null;
                if (varComp != null && varComp.CanInsertParameter(GH_ParameterSide.Output, component.Params.Output.Count))
                {
                    newOutput = varComp.CreateParameter(GH_ParameterSide.Output, component.Params.Output.Count);
                }

                newOutput ??= new Param_GenericObject();
                newOutput.Name = outputSpec.Name;
                newOutput.NickName = outputSpec.Name;

                if (!component.Params.RegisterOutputParam(newOutput))
                {
                    throw new InvalidOperationException($"Failed to register required script output '{outputSpec.Name}'.");
                }
            }

            varComp?.VariableParameterMaintenance();
            component.Params.OnParametersChanged();
        }

        private static bool TryInjectCode(IGH_DocumentObject instance, string code, List<string> warnings)
        {
            if (TrySetCodeViaReflection(instance, code))
            {
                return true;
            }

            // Try nested script/document object property if RhinoCode stores script inside a wrapper object.
            foreach (string nestedPropName in new[] { "Script", "Document", "CodeObject" })
            {
                PropertyInfo? nestedProp = instance.GetType().GetProperty(nestedPropName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object? nested = nestedProp?.GetValue(instance);
                if (nested != null && TrySetCodeViaReflection(nested, code))
                {
                    return true;
                }
            }

            // Fallback: mutate serialized GH_Archive XML and re-extract.
            try
            {
                var archive = new GH_Archive();
                archive.CreateNewRoot(true);
                if (!archive.AppendObject(instance, "Object"))
                {
                    return false;
                }

                string xml = archive.Serialize_Xml();
                XDocument xdoc = XDocument.Parse(xml);
                bool modified = false;

                foreach (XElement itemEl in xdoc.Descendants("item"))
                {
                    string? itemName = itemEl.Attribute("name")?.Value;
                    if (itemName != null && CodeMemberNames.Contains(itemName, StringComparer.OrdinalIgnoreCase))
                    {
                        itemEl.Value = code;
                        modified = true;
                        break;
                    }
                }

                if (!modified)
                {
                    return false;
                }

                var updatedArchive = new GH_Archive();
                return updatedArchive.Deserialize_Xml(xdoc.ToString(SaveOptions.DisableFormatting)) &&
                       updatedArchive.ExtractObject(instance, "Object");
            }
            catch (Exception ex)
            {
                warnings.Add($"GH_Archive script code injection failed: {ex.Message}");
                return false;
            }
        }

        private static bool TrySetCodeViaReflection(object target, string code)
        {
            Type type = target.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (string propName in CodeMemberNames)
            {
                PropertyInfo? prop = type.GetProperty(propName, flags);
                if (prop != null && prop.CanWrite && prop.PropertyType == typeof(string))
                {
                    prop.SetValue(target, code);
                    return true;
                }
            }

            foreach (string methodName in CodeMethodNames)
            {
                MethodInfo? method = type.GetMethod(methodName, flags, null, new[] { typeof(string) }, null);
                if (method != null)
                {
                    method.Invoke(target, new object[] { code });
                    return true;
                }
            }

            return false;
        }

        private static string LoadEmbeddedTemplateXml()
        {
            Assembly asm = typeof(PythonScriptSpawner).Assembly;
            string? resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("Python3Template.ghx", StringComparison.OrdinalIgnoreCase));

            if (resourceName is null)
            {
                return string.Empty;
            }

            using Stream? stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return string.Empty;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static void WriteDiagnosticSpikeDump(IGH_DocumentObject instance)
        {
            try
            {
                string path = Path.Combine(IndexStore.GetDefaultDataDirectory(), "python-component-spike.txt");
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var sb = new StringBuilder();
                sb.AppendLine($"Type: {instance.GetType().FullName}");
                sb.AppendLine("Properties:");
                foreach (PropertyInfo p in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    sb.AppendLine($"  {p.PropertyType.Name} {p.Name} (CanWrite={p.CanWrite})");
                }

                var archive = new GH_Archive();
                archive.CreateNewRoot(true);
                if (archive.AppendObject(instance, "Object"))
                {
                    sb.AppendLine("Serialized GH_Archive XML:");
                    sb.AppendLine(archive.Serialize_Xml());
                }

                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception)
            {
                // Best-effort spike diagnostic.
            }
        }
    }
}
