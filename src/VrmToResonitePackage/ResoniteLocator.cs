using System.Reflection;
using System.Runtime.InteropServices;

namespace VrmToResonitePackage;

/// <summary>
/// Finds the local Resonite installation and wires up assembly resolution so that
/// FrooxEngine and its dependencies load directly from that folder.
/// The Resonite DLLs are never copied or redistributed with this application.
/// </summary>
internal static class ResoniteLocator
{
    private const string DefaultSteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Resonite";

    public static string Locate(string explicitPath)
    {
        foreach (string candidate in EnumerateCandidates(explicitPath))
        {
            if (candidate != null && IsResoniteDirectory(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        throw new DirectoryNotFoundException("FrooxEngine.dll を含むResoniteフォルダが見つかりません。");
    }

    private static IEnumerable<string> EnumerateCandidates(string explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return explicitPath;
            // Explicit path was given but invalid: don't silently fall back.
            throw new DirectoryNotFoundException($"指定されたパスにResoniteが見つかりません: {explicitPath}");
        }

        string env = Environment.GetEnvironmentVariable("RESONITE_PATH");
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return env;
        }

        // Config file next to the exe, written by the user.
        string configPath = Path.Combine(AppContext.BaseDirectory, "resonite-path.txt");
        if (File.Exists(configPath))
        {
            string fromConfig = File.ReadAllText(configPath).Trim();
            if (fromConfig.Length > 0)
            {
                yield return fromConfig;
            }
        }

        yield return DefaultSteamPath;

        foreach (string library in EnumerateSteamLibraries())
        {
            yield return Path.Combine(library, "steamapps", "common", "Resonite");
        }
    }

    private static IEnumerable<string> EnumerateSteamLibraries()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }
        string steamPath = null;
        try
        {
            steamPath = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        }
        catch
        {
            // Registry unavailable; skip Steam discovery.
        }
        if (string.IsNullOrEmpty(steamPath))
        {
            yield break;
        }
        steamPath = steamPath.Replace('/', '\\');
        yield return steamPath;

        string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
        {
            yield break;
        }
        // Minimal VDF scrape: every "path" "X:\\..." line is a library root.
        foreach (string line in File.ReadLines(vdf))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            int firstQuote = trimmed.IndexOf('"', 6);
            int lastQuote = trimmed.LastIndexOf('"');
            if (firstQuote >= 0 && lastQuote > firstQuote)
            {
                string path = trimmed.Substring(firstQuote + 1, lastQuote - firstQuote - 1).Replace(@"\\", @"\");
                yield return path;
            }
        }
    }

    private static bool IsResoniteDirectory(string path)
    {
        try
        {
            return File.Exists(Path.Combine(path, "FrooxEngine.dll"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Directories inside the Resonite install that may contain native libraries.</summary>
    private static readonly string[] NativeSubdirectories =
    {
        "",
        @"runtimes\win-x64\native",
        @"runtimes\win10-x64\native",
    };

    public static void InstallAssemblyResolver(string resonitePath)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            string name = new AssemblyName(args.Name).Name;
            if (name == null)
            {
                return null;
            }
            string dllPath = Path.Combine(resonitePath, name + ".dll");
            if (File.Exists(dllPath))
            {
                return Assembly.LoadFrom(dllPath);
            }
            return null;
        };

        // Native dependencies (assimp, freetype6, brotli, FreeImage, crunch, ...) live in the
        // Resonite folder and its RID-specific runtimes folders.
        System.Runtime.Loader.AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, libraryName) =>
        {
            foreach (string subdirectory in NativeSubdirectories)
            {
                string directory = Path.Combine(resonitePath, subdirectory);
                foreach (string candidate in new[] { libraryName, libraryName + ".dll" })
                {
                    string fullPath = Path.Combine(directory, candidate);
                    if (File.Exists(fullPath) && NativeLibrary.TryLoad(fullPath, out IntPtr handle))
                    {
                        return handle;
                    }
                }
            }
            return IntPtr.Zero;
        };

        // Legacy fallback for code that uses LoadLibrary/PATH-based lookups directly.
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        string prepend = string.Join(';', NativeSubdirectories.Select(s => Path.Combine(resonitePath, s)));
        Environment.SetEnvironmentVariable("PATH", prepend + ";" + path);
    }
}
