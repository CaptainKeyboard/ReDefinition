using System;
using System.IO;
using System.Reflection;

namespace Surface
{
    // Surface --dll <ReDefinition.dll> --repo <folder> --out <file>: the surface of that build (SurfaceBuilder).
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string dll = Argument(args, "--dll"), repo = Argument(args, "--repo"), output = Argument(args, "--out");
            if (dll == null || repo == null || output == null)
            {
                Console.Error.WriteLine("Surface --dll <ReDefinition.dll> --repo <folder> --out <file>");
                return 2;
            }
            string managed = Environment.GetEnvironmentVariable("KspManaged")
                             ?? @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program\KSP_x64_Data\Managed";
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                string name = new AssemblyName(e.Name).Name + ".dll";
                string file = Path.Combine(managed, name);
                if (File.Exists(file)) return Assembly.LoadFrom(file);
                file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dll)), name);
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
            var lines = SurfaceBuilder.Build(Assembly.LoadFrom(Path.GetFullPath(dll)), Path.GetFullPath(repo));
            File.WriteAllText(output, SurfaceBuilder.Text(lines), new System.Text.UTF8Encoding(false));
            Console.WriteLine(lines.Count + " lines into " + output);
            return 0;
        }

        private static string Argument(string[] args, string name)
        {
            int at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        }
    }
}
