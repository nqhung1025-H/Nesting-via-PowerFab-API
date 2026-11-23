using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Hanlon.PowerFab.Nesting
{
    internal static class GeometryDependency
    {
        [ModuleInitializer]
        internal static void Initialize()
        {
            Assembly assembly = typeof(GeometryDependency).Assembly;
            AssemblyLoadContext? context = AssemblyLoadContext.GetLoadContext(assembly);
            if (context == null) return;
            context.Resolving += (loadContext, name) =>
            {
                if (name.Name != "Clipper2Lib") return null;
                using var resource = assembly.GetManifestResourceStream("Hanlon.PowerFab.Nesting.Clipper2Lib.dll");
                return resource == null ? null : loadContext.LoadFromStream(resource);
            };
        }
    }
}
