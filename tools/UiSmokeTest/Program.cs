using System;
using System.IO;
using System.Reflection;
using Eto.Forms;

namespace GhCopilot.UiSmokeTest
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Deliberately never initialize Rhino or instantiate a GH_Document here.
            // Only exercise the shared Eto controls with the host bridge unregistered.
            try
            {
                if (args.Length != 1) throw new ArgumentException("Supply the full path of a built GhCopilot.Rhino.rhp.");
                string pluginPath = Path.GetFullPath(args[0]);
                string pluginDir = Path.GetDirectoryName(pluginPath)!;
                AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
                {
                    string simpleName = new AssemblyName(e.Name).Name!;
                    foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
                        if (loaded.GetName().Name == simpleName) return loaded;
                    string name = simpleName + ".dll";
                    foreach (string dir in new[] { pluginDir, AppContext.BaseDirectory, @"C:\Program Files\Rhino 8\System" })
                    {
                        string path = Path.Combine(dir, name);
                        if (File.Exists(path)) return Assembly.LoadFrom(path);
                    }
                    return null;
                };
                using (var app = new Application(Eto.Platforms.Wpf))
                {
                    Assembly plugin = Assembly.LoadFrom(pluginPath);
                    Console.WriteLine("Harness Eto: " + typeof(Panel).Assembly.FullName + " @ " + typeof(Panel).Assembly.Location);
                    Console.WriteLine("Active platform: " + Eto.Platform.Instance);
                    foreach (AssemblyName reference in plugin.GetReferencedAssemblies())
                        if (reference.Name == "Eto") Console.WriteLine("Plug-in Eto reference: " + reference.FullName);
                    Type panelType = plugin.GetType("GhCopilot.UI.CopilotPanel", true)!;
                    Console.WriteLine("Panel base Eto: " + panelType.BaseType!.Assembly.FullName + " @ " + panelType.BaseType.Assembly.Location);
                    Console.WriteLine("Constructing shared panel with installed Eto WPF...");
                    var panel = (Panel)Activator.CreateInstance(panelType, true)!;
                    using (var form = new Form { Content = panel, ClientSize = new Eto.Drawing.Size(430, 700) })
                    {
                        // Force native WPF construction and layout, not just managed constructors.
                        var native = (System.Windows.Window)form.ControlObject;
                        native.Measure(new System.Windows.Size(430, 700));
                        native.Arrange(new System.Windows.Rect(0, 0, 430, 700));
                        native.UpdateLayout();
                    }
                    Console.WriteLine("PASS: panel construction, native WPF layout, and disposal.");
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }
    }
}