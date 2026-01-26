using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Autodesk.DesignScript.Geometry;
using Autodesk.DesignScript.Runtime;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Shared loader for ProtoGeometry.dll and LibG dependencies.
    /// Consolidates the duplicate loading logic from ExportGeometryToSMB and LoadGeometryFromExchange.
    /// </summary>
    [IsVisibleInDynamoLibrary(false)]
    internal static class ProtoGeometryLoader
    {
        // Cached paths and assembly references
        private static string _packageRootDir = null;
        private static string _packageLibgDir = null;
        private static string _protoGeometryPath = null;
        private static Assembly _protoGeometryAssembly = null;
        private static Type _geometryType = null;
        private static bool _dependenciesLoaded = false;

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        /// <summary>
        /// Gets the package root directory by looking for RootDir or libg_231_0_0 folders.
        /// Searches relative to assembly location in both package and development build structures.
        /// </summary>
        public static string GetPackageRootDirectory()
        {
            if (_packageRootDir != null)
                return _packageRootDir;

            try
            {
                // Get the assembly location
                var assemblyLocation = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(assemblyLocation))
                {
                    // Fallback: use CodeBase if Location is empty
                    var codeBase = Assembly.GetExecutingAssembly().CodeBase;
                    if (!string.IsNullOrEmpty(codeBase))
                    {
                        var uri = new Uri(codeBase);
                        assemblyLocation = uri.LocalPath;
                    }
                }

                if (string.IsNullOrEmpty(assemblyLocation))
                    return null;

                var assemblyDir = Path.GetDirectoryName(assemblyLocation);

                // First, check if RootDir or libg_231_0_0 exist in the same directory (development build)
                var rootDirInAssemblyDir = Path.Combine(assemblyDir, "RootDir");
                var libgDirInAssemblyDir = Path.Combine(assemblyDir, "libg_231_0_0");
                if (Directory.Exists(rootDirInAssemblyDir) || Directory.Exists(libgDirInAssemblyDir))
                {
                    _packageRootDir = assemblyDir;
                    return _packageRootDir;
                }

                // Second, go up from bin/ to package root (Dynamo package structure)
                var packageRoot = Path.GetDirectoryName(assemblyDir);

                // Verify by checking for RootDir or libg_231_0_0 folders
                var rootDirPath = Path.Combine(packageRoot, "RootDir");
                var libgDirPath = Path.Combine(packageRoot, "libg_231_0_0");

                if (Directory.Exists(rootDirPath) || Directory.Exists(libgDirPath))
                {
                    _packageRootDir = packageRoot;
                    return _packageRootDir;
                }

                // Third, check development build - look for libg folder at solution root
                var currentDir = assemblyDir;
                for (int i = 0; i < 5; i++)
                {
                    var libgFolder = Path.Combine(currentDir, "libg");
                    if (Directory.Exists(libgFolder))
                    {
                        _packageRootDir = libgFolder;
                        return _packageRootDir;
                    }
                    currentDir = Path.GetDirectoryName(currentDir);
                    if (string.IsNullOrEmpty(currentDir))
                        break;
                }
            }
            catch (Exception)
            {
                // If we can't determine the package root, return null
            }

            return null;
        }

        /// <summary>
        /// Gets the RootDir path (contains ProtoGeometry.dll and LibG.Interface.dll).
        /// </summary>
        public static string GetRootDir()
        {
            var packageRoot = GetPackageRootDirectory();
            if (packageRoot != null)
            {
                var rootDir = Path.Combine(packageRoot, "RootDir");
                if (Directory.Exists(rootDir))
                    return rootDir;
            }

            // Fallback to Downloads folder (for development/testing)
            return @"C:\Users\nenovd\Downloads\Dynamo\RootDir";
        }

        /// <summary>
        /// Gets the libg_231_0_0 directory path.
        /// </summary>
        public static string GetLibgDir()
        {
            if (_packageLibgDir != null)
                return _packageLibgDir;

            var packageRoot = GetPackageRootDirectory();
            if (packageRoot != null)
            {
                var libgDir = Path.Combine(packageRoot, "libg_231_0_0");
                if (Directory.Exists(libgDir))
                {
                    _packageLibgDir = libgDir;
                    return _packageLibgDir;
                }
            }

            // Fallback to Downloads folder (for development/testing)
            _packageLibgDir = @"C:\Users\nenovd\Downloads\Dynamo\Libg_231_0_0";
            return _packageLibgDir;
        }

        /// <summary>
        /// Gets the path to ProtoGeometry.dll.
        /// </summary>
        public static string GetProtoGeometryPath()
        {
            if (_protoGeometryPath != null)
                return _protoGeometryPath;

            var rootDir = GetRootDir();
            _protoGeometryPath = Path.Combine(rootDir, "ProtoGeometry.dll");
            return _protoGeometryPath;
        }

        /// <summary>
        /// Loads the LibG dependencies (managed DLLs and sets native DLL search path).
        /// Must be called before loading ProtoGeometry.dll.
        /// </summary>
        /// <param name="logger">Optional diagnostics logger</param>
        public static void LoadLibGDependencies(DiagnosticsLogger logger = null)
        {
            if (_dependenciesLoaded)
                return;

            try
            {
                var libgDir = GetLibgDir();
                var rootDir = GetRootDir();

                // Add the LibG directory to DLL search path for native DLLs
                try
                {
                    SetDllDirectory(libgDir);
                }
                catch (Exception ex)
                {
                    logger?.Warning($"Failed to set DLL directory: {ex.Message}");
                }

                // Load managed LibG DLLs in correct order
                var managedDlls = new[]
                {
                    "LibG.Managed.dll",
                    "LibG.ProtoInterface.dll",
                    "LibG.AsmPreloader.Managed.dll"
                };

                foreach (var dllName in managedDlls)
                {
                    var dllPath = Path.Combine(libgDir, dllName);
                    if (File.Exists(dllPath))
                    {
                        try
                        {
                            Assembly.LoadFrom(dllPath);
                        }
                        catch (Exception ex)
                        {
                            logger?.Warning($"Failed to load {dllName}: {ex.Message}");
                        }
                    }
                }

                // Load LibG.Interface.dll from RootDir
                var libgInterfacePath = Path.Combine(rootDir, "LibG.Interface.dll");
                if (File.Exists(libgInterfacePath))
                {
                    try
                    {
                        Assembly.LoadFrom(libgInterfacePath);
                    }
                    catch (Exception ex)
                    {
                        logger?.Warning($"Failed to load LibG.Interface.dll: {ex.Message}");
                    }
                }

                _dependenciesLoaded = true;
            }
            catch (Exception ex)
            {
                logger?.Error($"Error loading LibG dependencies: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the Geometry type from ProtoGeometry.dll.
        /// Loads dependencies and assembly if not already loaded.
        /// </summary>
        /// <param name="logger">Optional diagnostics logger</param>
        /// <returns>The Geometry type from ProtoGeometry, or the default Geometry type if loading fails</returns>
        public static Type GetGeometryType(DiagnosticsLogger logger = null)
        {
            if (_geometryType != null)
                return _geometryType;

            try
            {
                // Load LibG dependencies first
                LoadLibGDependencies(logger);

                var protoGeometryPath = GetProtoGeometryPath();
                if (!File.Exists(protoGeometryPath))
                {
                    logger?.Warning($"ProtoGeometry.dll not found at: {protoGeometryPath}, using default");
                    return typeof(Geometry);
                }

                _protoGeometryAssembly = Assembly.LoadFrom(protoGeometryPath);

                // Get Geometry type from the assembly
                _geometryType = _protoGeometryAssembly.GetType("Autodesk.DesignScript.Geometry.Geometry");
                if (_geometryType == null)
                {
                    logger?.Warning("Could not find Geometry type in ProtoGeometry assembly, using default");
                    return typeof(Geometry);
                }

                return _geometryType;
            }
            catch (Exception ex)
            {
                logger?.Warning($"Error loading ProtoGeometry.dll: {ex.Message}");
                if (ex.InnerException != null)
                {
                    logger?.Debug($"Inner exception: {ex.InnerException.Message}");
                }
                return typeof(Geometry);
            }
        }

        /// <summary>
        /// Gets the loaded ProtoGeometry assembly.
        /// </summary>
        /// <param name="logger">Optional diagnostics logger</param>
        /// <returns>The loaded assembly, or null if not loaded</returns>
        public static Assembly GetProtoGeometryAssembly(DiagnosticsLogger logger = null)
        {
            if (_protoGeometryAssembly != null)
                return _protoGeometryAssembly;

            // Calling GetGeometryType will load the assembly
            GetGeometryType(logger);
            return _protoGeometryAssembly;
        }

        /// <summary>
        /// Checks if LibG dependencies have been loaded.
        /// </summary>
        public static bool AreDependenciesLoaded => _dependenciesLoaded;

        /// <summary>
        /// Resets all cached state. Use for testing or when paths change.
        /// </summary>
        public static void Reset()
        {
            _packageRootDir = null;
            _packageLibgDir = null;
            _protoGeometryPath = null;
            _protoGeometryAssembly = null;
            _geometryType = null;
            _dependenciesLoaded = false;
        }
    }
}
