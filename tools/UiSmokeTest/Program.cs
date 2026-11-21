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
                    Type panelType = plugin.GetType("GhCopilot.Rhino.UI.CopilotDockPanel", true)!;
                    Console.WriteLine("Panel base Eto: " + panelType.BaseType!.Assembly.FullName + " @ " + panelType.BaseType.Assembly.Location);
                    Console.WriteLine("Constructing CopilotDockPanel with installed Eto WPF...");
                    var panel = (Panel)Activator.CreateInstance(panelType, true)!;
                    using (var form = new Form { Content = panel, ClientSize = new Eto.Drawing.Size(430, 700) })
                    {
                        // Force native WPF construction and layout across dock sizes, including narrow/unbounded measure.
                        var native = (System.Windows.Window)form.ControlObject;
                        native.Measure(new System.Windows.Size(430, 700));
                        native.Arrange(new System.Windows.Rect(0, 0, 430, 700));
                        native.UpdateLayout();

                        var wpfContent = (System.Windows.UIElement)panel.ControlObject;
                        wpfContent.Measure(new System.Windows.Size(280, double.PositiveInfinity));
                        wpfContent.Arrange(new System.Windows.Rect(0, 0, 280, 320));
                        wpfContent.UpdateLayout();

                        // Also exercise switching to Settings tab and back under native WPF layout.
                        if (panel.Content != null)
                        {
                            MethodInfo? selectTab = panel.Content.GetType().GetMethod("SelectTab", BindingFlags.Instance | BindingFlags.Public);
                            if (selectTab != null)
                            {
                                selectTab.Invoke(panel.Content, new object[] { true });
                                native.UpdateLayout();
                                wpfContent.Measure(new System.Windows.Size(340, 560));
                                wpfContent.Arrange(new System.Windows.Rect(0, 0, 340, 560));
                                wpfContent.UpdateLayout();

                                selectTab.Invoke(panel.Content, new object[] { false });
                                native.UpdateLayout();
                            }
                        }
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