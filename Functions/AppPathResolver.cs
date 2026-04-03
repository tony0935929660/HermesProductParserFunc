using System;
using System.IO;

namespace HermesProductParserFunc.Functions
{
    internal static class AppPathResolver
    {
        public static string ResolveAppRoot()
        {
            var scriptRoot = Environment.GetEnvironmentVariable("AzureWebJobsScriptRoot");
            if (!string.IsNullOrWhiteSpace(scriptRoot))
            {
                return Path.GetFullPath(scriptRoot);
            }

            var current = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (current != null)
            {
                if (Directory.GetFiles(current.FullName, "*.csproj").Length > 0)
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (Directory.GetFiles(current.FullName, "*.csproj").Length > 0)
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return Directory.GetCurrentDirectory();
        }

        public static string ResolvePath(string configuredPath, params string[] fallbackSegments)
        {
            var appRoot = ResolveAppRoot();
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                if (Path.IsPathRooted(configuredPath))
                {
                    return Path.GetFullPath(configuredPath);
                }

                return Path.GetFullPath(Path.Combine(appRoot, configuredPath));
            }

            return Path.GetFullPath(Path.Combine(appRoot, Path.Combine(fallbackSegments)));
        }
    }
}