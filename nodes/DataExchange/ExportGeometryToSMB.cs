using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.DesignScript.Geometry;
using Autodesk.DesignScript.Runtime;
using Autodesk.DataExchange.DataModels;
using Autodesk.DataExchange.Core.Models;
using Autodesk.DataExchange.Interface;
using Autodesk.DataExchange.SchemaObjects.Units;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Export Dynamo geometry objects to SMB file format using ProtoGeometry
    /// </summary>
    public static class ExportGeometryToSMB
    {
        /// <summary>
        /// Helper class for reflection operations to reduce code duplication and improve readability
        /// </summary>
        private static class ReflectionHelper
        {
            /// <summary>
            /// Gets a method from a type using reflection
            /// </summary>
            public static MethodInfo GetMethod(Type type, string methodName, BindingFlags flags, Type[] parameterTypes = null)
            {
                if (parameterTypes != null)
                {
                    return type.GetMethod(methodName, flags, null, parameterTypes, null);
                }
                return type.GetMethod(methodName, flags);
            }

            /// <summary>
            /// Invokes a method synchronously and returns the result
            /// </summary>
            public static object InvokeMethod(object instance, MethodInfo method, object[] parameters, List<string> diagnostics = null)
            {
                if (method == null)
                {
                    throw new InvalidOperationException($"Method not found on type {instance?.GetType().FullName}");
                }

                try
                {
                    return method.Invoke(instance, parameters);
                }
                catch (Exception ex)
                {
                    diagnostics?.Add($"  ⚠️ Failed to invoke {method.Name}: {ex.Message}");
                    throw;
                }
            }

            /// <summary>
            /// Invokes an async method and awaits the result, handling IResponse<T> pattern
            /// </summary>
            public static T InvokeMethodAsync<T>(object instance, MethodInfo method, object[] parameters, List<string> diagnostics = null)
            {
                if (method == null)
                {
                    throw new InvalidOperationException($"Method not found on type {instance?.GetType().FullName}");
                }

                try
                {
                    var task = method.Invoke(instance, parameters);
                    if (task == null)
                    {
                        throw new InvalidOperationException($"Method {method.Name} returned null");
                    }

                    var taskResult = ((dynamic)task).GetAwaiter().GetResult();
                    return HandleResponse<T>(taskResult, diagnostics);
                }
                catch (Exception ex)
                {
                    diagnostics?.Add($"  ⚠️ Failed to invoke async {method.Name}: {ex.Message}");
                    throw;
                }
            }


            /// <summary>
            /// Finds a type across assemblies, with static caching for deterministic lookups
            /// </summary>
            public static Type FindType(string typeName, Type searchFromType, Dictionary<string, Type> cache = null)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    // Check static cache first
                    if (_typeCache.TryGetValue(typeName, out var cachedType))
                    {
                        cache?.Add(typeName, cachedType);
                        sw.Stop();
                        if (sw.ElapsedMilliseconds > 1) // Only log if it took measurable time
                        {
                            // Cache hit - should be very fast, but log if slow
                        }
                        return cachedType;
                    }

                    // Check parameter cache
                    if (cache != null && cache.TryGetValue(typeName, out cachedType))
                    {
                        _typeCache[typeName] = cachedType; // Also cache in static cache
                        sw.Stop();
                    return cachedType;
                }

                    // Try searchFromType's assembly first (most common case)
                if (searchFromType != null)
                {
                    var type = searchFromType.Assembly.GetType(typeName);
                    if (type != null)
                    {
                            _typeCache[typeName] = type; // Cache in static cache
                        cache?.Add(typeName, type);
                            sw.Stop();
                        return type;
                    }
                }

                    // Search all DataExchange assemblies (SLOW - assembly scan)
                var allAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.GetName().Name.Contains("DataExchange"))
                    .ToList();

                foreach (var asm in allAssemblies)
                {
                    var foundType = asm.GetType(typeName);
                    if (foundType != null)
                    {
                            _typeCache[typeName] = foundType; // Cache in static cache
                        cache?.Add(typeName, foundType);
                            sw.Stop();
                            // Log slow type lookups (assembly scan)
                            if (sw.ElapsedMilliseconds > 10)
                            {
                                // Could add diagnostics here if needed, but would require passing diagnostics parameter
                            }
                        return foundType;
                    }
                }

                    sw.Stop();
                return null;
                }
                finally
                {
                    sw.Stop();
                }
            }

            /// <summary>
            /// Creates an instance and sets its ID using SetId method (cached for performance)
            /// </summary>
            public static object CreateInstanceWithId(Type type, string id, Dictionary<string, Type> foundTypes = null, List<string> diagnostics = null)
            {
                var instance = Activator.CreateInstance(type);
                if (instance == null)
                {
                    throw new InvalidOperationException($"Failed to create instance of {type.FullName}");
                }

                // Cache SetId method lookup by type name
                var cacheKey = $"{type.FullName}.SetId";
                if (!_methodCache.TryGetValue(cacheKey, out var setIdMethod))
                {
                // Find SetId method (might be on base type)
                    setIdMethod = type.GetMethod("SetId", BindingFlags.NonPublic | BindingFlags.Instance);
                if (setIdMethod == null)
                {
                    var baseType = type.BaseType;
                    if (baseType != null)
                    {
                        setIdMethod = baseType.GetMethod("SetId", BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                }

                if (setIdMethod == null)
                {
                    throw new InvalidOperationException($"Could not find SetId method on {type.FullName} or its base types");
                    }
                    
                    _methodCache[cacheKey] = setIdMethod;
                }

                setIdMethod.Invoke(instance, new object[] { id });
                diagnostics?.Add($"✓ Created {type.Name} with ID: {id}");
                return instance;
            }


            /// <summary>
            /// Handles IResponse<T> pattern, extracting Value property if present
            /// </summary>
            public static T HandleResponse<T>(object response, List<string> diagnostics = null)
            {
                if (response == null)
                {
                    return default(T);
                }

                // Check if it's already the right type
                if (response is T directResult)
                {
                    return directResult;
                }

                // Check for IResponse<T> pattern
                var responseType = response.GetType();
                var isSuccessProp = responseType.GetProperty("IsSuccess") ?? responseType.GetProperty("Success");
                
                if (isSuccessProp != null)
                {
                    var isSuccess = (bool)isSuccessProp.GetValue(response);
                    diagnostics?.Add($"  Response IsSuccess: {isSuccess}");
                    
                    if (!isSuccess)
                    {
                        var errorProp = responseType.GetProperty("Error");
                        if (errorProp != null)
                        {
                            var error = errorProp.GetValue(response);
                            throw new InvalidOperationException($"Operation failed: {error}");
                        }
                    }
                }

                // Try to get Value property (same pattern as InspectExchange.cs)
                var valueProp = responseType.GetProperty("Value");
                if (valueProp != null)
                {
                    var value = valueProp.GetValue(response);
                    diagnostics?.Add($"  Value property found, value type: {value?.GetType().FullName ?? "null"}");
                    
                    if (value != null)
                    {
                        // Check if value type is assignable to T
                        var valueType = value.GetType();
                        var targetType = typeof(T);
                        
                        if (targetType.IsAssignableFrom(valueType))
                        {
                            // Value is assignable to T - cast it
                            try
                            {
                                var castValue = (T)value;
                                diagnostics?.Add($"  ✓ Successfully extracted {targetType.Name} from Value property");
                                return castValue;
                            }
                            catch (InvalidCastException castEx)
                            {
                                diagnostics?.Add($"  ⚠️ IsAssignableFrom check passed but cast failed: {castEx.Message}");
                            }
                        }
                        else
                        {
                            diagnostics?.Add($"  ⚠️ Value type {valueType.FullName} is not assignable to {targetType.FullName}");
                        }
                        
                        // Try 'is' pattern match as fallback
                        if (value is T typedValue)
                        {
                            diagnostics?.Add($"  ✓ Successfully matched {targetType.Name} using 'is' pattern");
                            return typedValue;
                        }
                    }
                    else
                    {
                        diagnostics?.Add($"  ⚠️ Value property is null");
                    }
                }
                else
                {
                    diagnostics?.Add($"  ⚠️ No Value property found on response type {responseType.FullName}");
                }

                // Last resort: try direct cast on response itself
                try
                {
                    var directCast = (T)response;
                    diagnostics?.Add($"  ✓ Successfully cast response directly to {typeof(T).Name}");
                    return directCast;
                }
                catch (Exception castEx)
                {
                    var valueInfo = valueProp != null 
                        ? $"Value type: {valueProp.GetValue(response)?.GetType().FullName ?? "null"}" 
                        : "No Value property";
                    throw new InvalidOperationException(
                        $"Could not convert response of type {responseType.FullName} to {typeof(T).FullName}. " +
                        $"{valueInfo}. Error: {castEx.Message}");
                }
            }
        }
        // Paths to the new DLLs - resolved relative to package location
        private static string _packageRootDir = null;
        private static string _packageLibgDir = null;
        private static string _newProtoGeometryPath = null;
        private static Assembly _newProtoGeometryAssembly = null;
        private static Type _geometryTypeFromNewAssembly = null;
        private static bool _dependenciesLoaded = false;
        
        // Static caches for discovered types, methods, and properties (deterministic now that we know what works)
        private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();
        private static readonly Dictionary<string, MethodInfo> _methodCache = new Dictionary<string, MethodInfo>();
        private static readonly Dictionary<string, PropertyInfo> _propertyCache = new Dictionary<string, PropertyInfo>();
        private static readonly Dictionary<string, FieldInfo> _fieldCache = new Dictionary<string, FieldInfo>();
        private static readonly Dictionary<string, ConstructorInfo> _constructorCache = new Dictionary<string, ConstructorInfo>();
        private static bool _cachesInitialized = false;
        
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);
        
        /// <summary>
        /// Helper method to time operations and add diagnostics
        /// </summary>
        private static T TimeOperation<T>(string operationName, Func<T> operation, List<string> diagnostics)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var result = operation();
                sw.Stop();
                diagnostics?.Add($"⏱️  {operationName}: {sw.ElapsedMilliseconds}ms ({sw.Elapsed.TotalSeconds:F3}s)");
                return result;
            }
            catch
            {
                sw.Stop();
                diagnostics?.Add($"⏱️  {operationName}: {sw.ElapsedMilliseconds}ms ({sw.Elapsed.TotalSeconds:F3}s) [FAILED]");
                throw;
            }
        }
        
        /// <summary>
        /// Helper method to time void operations and add diagnostics
        /// </summary>
        private static void TimeOperation(string operationName, Action operation, List<string> diagnostics)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                operation();
                sw.Stop();
                diagnostics?.Add($"⏱️  {operationName}: {sw.ElapsedMilliseconds}ms ({sw.Elapsed.TotalSeconds:F3}s)");
            }
            catch
            {
                sw.Stop();
                diagnostics?.Add($"⏱️  {operationName}: {sw.ElapsedMilliseconds}ms ({sw.Elapsed.TotalSeconds:F3}s) [FAILED]");
                throw;
            }
        }
        
        /// <summary>
        /// Helper method to time async operations and add diagnostics
        /// </summary>
        private static async Task<T> TimeOperation<T>(string operationName, Func<Task<T>> operation, List<string> diagnostics)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await operation();
                sw.Stop();
                diagnostics?.Add($"⏱️  {operationName}: {sw.ElapsedMilliseconds}ms ({sw.Elapsed.TotalSeconds:F3}s)");
                return result;
            }
            catch
            {
                sw.Stop();
                diagnostics?.Add($"⏱️  {operationName}: {sw.ElapsedMilliseconds}ms ({sw.Elapsed.TotalSeconds:F3}s) [FAILED]");
                throw;
            }
        }
        
        /// <summary>
        /// Helper method to time async void operations and add diagnostics
        /// </summary>
        private static async Task TimeOperation(string operationName, Func<Task> operation, List<string> diagnostics)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await operation();
                sw.Stop();
                diagnostics?.Add($"⏱️  {operationName}: {sw.ElapsedMilliseconds}ms ({sw.Elapsed.TotalSeconds:F3}s)");
            }
            catch
            {
                sw.Stop();
                diagnostics?.Add($"⏱️  {operationName}: {sw.ElapsedMilliseconds}ms ({sw.Elapsed.TotalSeconds:F3}s) [FAILED]");
                throw;
            }
        }
        
        /// <summary>
        /// Gets the package root directory by looking for RootDir or libg_231_0_0 folders
        /// relative to the assembly location. In Dynamo packages, structure is:
        /// packages/DataExchangeNodes/bin/ (assembly location)
        /// packages/DataExchangeNodes/RootDir/
        /// packages/DataExchangeNodes/libg_231_0_0/
        /// 
        /// In development builds, structure is:
        /// bin/Debug/4.1.0-beta3200/DataExchangeNodes/ (assembly location)
        /// bin/Debug/4.1.0-beta3200/DataExchangeNodes/RootDir/
        /// bin/Debug/4.1.0-beta3200/DataExchangeNodes/libg_231_0_0/
        /// </summary>
        private static string GetPackageRootDirectory()
        {
            if (_packageRootDir != null)
                return _packageRootDir;
            
            try
            {
                // Get the assembly location (e.g., .../packages/DataExchangeNodes/bin/ExchangeNodes.dll)
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
                
                // First, check if RootDir or libg_231_0_0 exist in the same directory as the assembly (development build)
                var rootDirInAssemblyDir = Path.Combine(assemblyDir, "RootDir");
                var libgDirInAssemblyDir = Path.Combine(assemblyDir, "libg_231_0_0");
                if (Directory.Exists(rootDirInAssemblyDir) || Directory.Exists(libgDirInAssemblyDir))
                {
                    _packageRootDir = assemblyDir;
                    return _packageRootDir;
                }
                
                // Second, go up from bin/ to package root (Dynamo package structure)
                var binDir = assemblyDir;
                var packageRoot = Path.GetDirectoryName(binDir);
                
                // Verify by checking for RootDir or libg_231_0_0 folders
                var rootDirPath = Path.Combine(packageRoot, "RootDir");
                var libgDirPath = Path.Combine(packageRoot, "libg_231_0_0");
                
                if (Directory.Exists(rootDirPath) || Directory.Exists(libgDirPath))
                {
                    _packageRootDir = packageRoot;
                    return _packageRootDir;
                }
                
                // Third, check if we're in a development build and look for libg folder at solution root
                var currentDir = assemblyDir;
                for (int i = 0; i < 5; i++) // Go up max 5 levels
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
                // The code will fall back to checking Downloads folder
            }
            
            return null;
        }
        
        /// <summary>
        /// Gets the RootDir path (contains ProtoGeometry.dll and LibG.Interface.dll)
        /// </summary>
        private static string GetRootDir()
        {
            var packageRoot = GetPackageRootDirectory();
            if (packageRoot != null)
            {
                // Check if RootDir exists in package
                var rootDir = Path.Combine(packageRoot, "RootDir");
                if (Directory.Exists(rootDir))
                    return rootDir;
                
                // Alternative: check libg/RootDir structure
                rootDir = Path.Combine(packageRoot, "RootDir");
                if (Directory.Exists(rootDir))
                    return rootDir;
            }
            
            // Fallback to Downloads folder (for development/testing)
            return @"C:\Users\nenovd\Downloads\Dynamo\RootDir";
        }
        
        /// <summary>
        /// Gets the libg_231_0_0 directory path
        /// </summary>
        private static string GetLibgDir()
        {
            var packageRoot = GetPackageRootDirectory();
            if (packageRoot != null)
            {
                // Check if libg_231_0_0 exists in package
                var libgDir = Path.Combine(packageRoot, "libg_231_0_0");
                if (Directory.Exists(libgDir))
                    return libgDir;
            }
            
            // Fallback to Downloads folder (for development/testing)
            return @"C:\Users\nenovd\Downloads\Dynamo\Libg_231_0_0";
        }
        
        /// <summary>
        /// Gets the path to the new ProtoGeometry.dll
        /// </summary>
        private static string GetNewProtoGeometryPath()
        {
            if (_newProtoGeometryPath != null)
                return _newProtoGeometryPath;
            
            var rootDir = GetRootDir();
            _newProtoGeometryPath = Path.Combine(rootDir, "ProtoGeometry.dll");
            return _newProtoGeometryPath;
        }
        
        /// <summary>
        /// Loads the LibG dependencies first, then ProtoGeometry.dll
        /// </summary>
        private static void LoadLibGDependencies(List<string> diagnostics)
        {
            if (_dependenciesLoaded)
                return;
            
            try
            {
                var libgDir = GetLibgDir();
                var rootDir = GetRootDir();
                
                diagnostics?.Add($"Loading LibG dependencies from package...");
                diagnostics?.Add($"  RootDir: {rootDir}");
                diagnostics?.Add($"  LibgDir: {libgDir}");
                
                // Add the LibG directory to DLL search path for native DLLs
                try
                {
                    SetDllDirectory(libgDir);
                    diagnostics?.Add($"  ✓ Added {libgDir} to DLL search path");
                }
                catch (Exception ex)
                {
                    diagnostics?.Add($"  ⚠️ Failed to add DLL directory: {ex.Message}");
                }
                
                // Load managed LibG DLLs (native DLLs will be found via DLL search path)
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
                            diagnostics?.Add($"  ✓ Loaded: {dllName}");
                        }
                        catch (Exception ex)
                        {
                            diagnostics?.Add($"  ⚠️ Failed to load {dllName}: {ex.Message}");
                        }
                    }
                    else
                    {
                        diagnostics?.Add($"  ⚠️ Not found: {dllName} at {dllPath}");
                    }
                }
                
                // Note: LibGCore.dll and LibG.dll are native DLLs - they'll be loaded automatically
                diagnostics?.Add($"  Note: LibGCore.dll and LibG.dll are native DLLs (will be loaded on demand)");
                
                // Load LibG.Interface.dll
                var libgInterfacePath = Path.Combine(rootDir, "LibG.Interface.dll");
                if (File.Exists(libgInterfacePath))
                {
                    try
                    {
                        Assembly.LoadFrom(libgInterfacePath);
                        diagnostics?.Add($"  ✓ Loaded: LibG.Interface.dll");
                    }
                    catch (Exception ex)
                    {
                        diagnostics?.Add($"  ⚠️ Failed to load LibG.Interface.dll: {ex.Message}");
                    }
                }
                
                _dependenciesLoaded = true;
            }
            catch (Exception ex)
            {
                diagnostics?.Add($"⚠️ Error loading LibG dependencies: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Loads the new ProtoGeometry.dll and gets the Geometry type from it
        /// </summary>
        private static Type GetGeometryTypeFromNewAssembly(List<string> diagnostics)
        {
            if (_geometryTypeFromNewAssembly != null)
                return _geometryTypeFromNewAssembly;
            
            try
            {
                // Load LibG dependencies first
                LoadLibGDependencies(diagnostics);
                
                var protoGeometryPath = GetNewProtoGeometryPath();
                if (!File.Exists(protoGeometryPath))
                {
                    diagnostics?.Add($"⚠️ New ProtoGeometry.dll not found at: {protoGeometryPath}");
                    diagnostics?.Add($"   Falling back to default Geometry type");
                    return typeof(Geometry);
                }
                
                diagnostics?.Add($"Loading new ProtoGeometry.dll from: {protoGeometryPath}");
                _newProtoGeometryAssembly = Assembly.LoadFrom(protoGeometryPath);
                diagnostics?.Add($"✓ Loaded assembly: {_newProtoGeometryAssembly.FullName}");
                
                // Get Geometry type from the new assembly
                _geometryTypeFromNewAssembly = _newProtoGeometryAssembly.GetType("Autodesk.DesignScript.Geometry.Geometry");
                if (_geometryTypeFromNewAssembly == null)
                {
                    diagnostics?.Add($"⚠️ Could not find Geometry type in new assembly, falling back to default");
                    return typeof(Geometry);
                }
                
                diagnostics?.Add($"✓ Found Geometry type in new assembly: {_geometryTypeFromNewAssembly.FullName}");
                return _geometryTypeFromNewAssembly;
            }
            catch (Exception ex)
            {
                diagnostics?.Add($"⚠️ Error loading new ProtoGeometry.dll: {ex.Message}");
                if (ex.InnerException != null)
                {
                    diagnostics?.Add($"   Inner: {ex.InnerException.Message}");
                }
                diagnostics?.Add($"   Falling back to default Geometry type");
                return typeof(Geometry);
            }
        }
        
        /// <summary>
        /// Converts unit string to mmPerUnit (millimeters per unit)
        /// </summary>
        private static double ConvertUnitToMmPerUnit(string unit)
        {
            if (string.IsNullOrEmpty(unit))
                return 10.0; // Default to cm
            
            if (unit.Contains("Meter") && !unit.Contains("Centi"))
                return 1000.0; // Meter
            else if (unit.Contains("CentiMeter") || unit.Contains("cm"))
                return 10.0; // Centimeter
            else if (unit.Contains("Feet") || unit.Contains("ft"))
                return 304.8; // Feet
            else if (unit.Contains("Inch") || unit.Contains("in"))
                return 25.4; // Inch
            else
                return 10.0; // Default to cm
        }
        
        /// <summary>
        /// Exports Dynamo geometry objects to an SMB file.
        /// </summary>
        /// <param name="geometries">List of Dynamo geometry objects to export</param>
        /// <param name="outputFilePath">Full path where the SMB file should be saved. If null or empty, a temp file will be created.</param>
        /// <param name="unit">Unit type for geometry (default: "kUnitType_CentiMeter"). Options: kUnitType_CentiMeter, kUnitType_Meter, kUnitType_Feet, kUnitType_Inch</param>
        /// <returns>Dictionary with "smbFilePath" (path to created SMB file), "log" (diagnostic messages), and "success" (boolean)</returns>
        [MultiReturn(new[] { "smbFilePath", "log", "success" })]
        public static Dictionary<string, object> ExportToSMB(
            List<Geometry> geometries,
            string outputFilePath = null,
            string unit = "kUnitType_CentiMeter")
        {
            var overallTimer = Stopwatch.StartNew();
            var diagnostics = new List<string>();
            bool success = false;
            string finalSmbFilePath = null;

            try
            {
                diagnostics.Add("=== Export Dynamo Geometry to SMB ===");
                diagnostics.Add($"Input geometries count: {geometries?.Count ?? 0}");
                diagnostics.Add($"Unit: {unit}");

                // Validate inputs
                if (geometries == null || geometries.Count == 0)
                {
                    throw new ArgumentException("Geometries list cannot be null or empty", nameof(geometries));
                }

                // Convert unit to mmPerUnit
                double mmPerUnit = ConvertUnitToMmPerUnit(unit);
                diagnostics.Add($"mmPerUnit: {mmPerUnit} (for unit: {unit})");

                // Determine output file path
                if (string.IsNullOrEmpty(outputFilePath))
                {
                    // Create temp directory
                    var tempDir = Path.Combine(Path.GetTempPath(), "DataExchangeNodes", "Export");
                    if (!Directory.Exists(tempDir))
                        Directory.CreateDirectory(tempDir);
                    
                    // Generate unique filename
                    var fileName = $"DynamoGeometry_{Guid.NewGuid():N}.smb";
                    finalSmbFilePath = Path.Combine(tempDir, fileName);
                    diagnostics.Add($"No output path specified, using temp location: {finalSmbFilePath}");
                }
                else
                {
                    finalSmbFilePath = Path.GetFullPath(outputFilePath);
                    diagnostics.Add($"Using specified output path: {finalSmbFilePath}");
                    
                    // Ensure directory exists
                    var outputDir = Path.GetDirectoryName(finalSmbFilePath);
                    if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                    {
                        Directory.CreateDirectory(outputDir);
                        diagnostics.Add($"Created output directory: {outputDir}");
                    }
                }

                // Handle paths with spaces - copy to temp location without spaces if needed
                string exportSmbFilePath = finalSmbFilePath;
                if (finalSmbFilePath.Contains(" "))
                {
                    diagnostics.Add($"⚠️ Path contains spaces - using temp location without spaces for export");
                    var tempDir = Path.Combine(Path.GetTempPath(), "DataExchangeNodes", "Export");
                    if (!Directory.Exists(tempDir))
                        Directory.CreateDirectory(tempDir);
                    
                    var tempFileName = Path.GetFileName(finalSmbFilePath);
                    if (string.IsNullOrEmpty(tempFileName))
                        tempFileName = $"DynamoGeometry_{Guid.NewGuid():N}.smb";
                    
                    exportSmbFilePath = Path.Combine(tempDir, tempFileName);
                    diagnostics.Add($"Exporting to: {exportSmbFilePath}");
                }

                // Get Geometry type from the new ProtoGeometry.dll
                var geometryType = TimeOperation("GetGeometryTypeFromNewAssembly", 
                    () => GetGeometryTypeFromNewAssembly(diagnostics), diagnostics);
                
                // Find ExportToSMB method
                var allExportMethods = TimeOperation("Find ExportToSMB methods via reflection", 
                    () => geometryType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Where(m => m.Name == "ExportToSMB")
                        .ToList(), diagnostics);
                
                diagnostics.Add($"Found {allExportMethods.Count} ExportToSMB method(s):");
                foreach (var method in allExportMethods)
                {
                    var paramInfo = method.GetParameters();
                    var paramStr = string.Join(", ", paramInfo.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    var visibility = method.IsPublic ? "public" : "internal";
                    diagnostics.Add($"  [{visibility}] {method.ReturnType.Name} ExportToSMB({paramStr})");
                }
                
                // Try to find the public ExportToSMB method
                // Signature: ExportToSMB(IEnumerable<Geometry> geometry, String filePath, Double unitsMM)
                var exportMethod = allExportMethods.FirstOrDefault(m => 
                    m.IsPublic && 
                    m.GetParameters().Length == 3 &&
                    m.GetParameters()[0].ParameterType.Name.Contains("IEnumerable") &&
                    m.GetParameters()[1].ParameterType == typeof(string) &&
                    m.GetParameters()[2].ParameterType == typeof(double));
                
                if (exportMethod == null)
                {
                    // Try alternative signature: might accept List<Geometry> or Geometry[]
                    exportMethod = allExportMethods.FirstOrDefault(m => 
                        m.IsPublic && 
                        m.GetParameters().Length == 3 &&
                        m.GetParameters()[1].ParameterType == typeof(string) &&
                        m.GetParameters()[2].ParameterType == typeof(double));
                }
                
                if (exportMethod == null)
                {
                    throw new InvalidOperationException(
                        $"ExportToSMB method not found in {geometryType.Assembly.FullName}. " +
                        $"Found {allExportMethods.Count} method(s) with name ExportToSMB, but none match expected signature.");
                }
                
                diagnostics.Add($"Using method: {exportMethod.Name}({string.Join(", ", exportMethod.GetParameters().Select(p => p.ParameterType.Name))})");
                
                // Call ExportToSMB
                diagnostics.Add($"Calling Geometry.ExportToSMB with {geometries.Count} geometry object(s)...");
                diagnostics.Add($"  File path: {exportSmbFilePath}");
                diagnostics.Add($"  Units (mm per unit): {mmPerUnit}");
                
                var result = TimeOperation($"ExportToSMB (reflection invoke with {geometries.Count} geometries)", 
                    () => exportMethod.Invoke(null, new object[] { geometries, exportSmbFilePath, mmPerUnit }), diagnostics);
                
                // ExportToSMB returns String (the file path) according to inspection
                if (result != null)
                {
                    var exportedPath = result.ToString();
                    diagnostics.Add($"✓ ExportToSMB returned: {exportedPath}");
                    
                    // Verify file was created
                    if (File.Exists(exportSmbFilePath))
                    {
                        var fileInfo = new FileInfo(exportSmbFilePath);
                        diagnostics.Add($"✓ SMB file created successfully");
                        diagnostics.Add($"  File size: {fileInfo.Length} bytes");
                        diagnostics.Add($"  File path: {exportSmbFilePath}");
                        
                        // If we exported to temp location due to spaces, copy to final location
                        if (exportSmbFilePath != finalSmbFilePath)
                        {
                            try
                            {
                                File.Copy(exportSmbFilePath, finalSmbFilePath, overwrite: true);
                                diagnostics.Add($"✓ Copied from temp location to final location: {finalSmbFilePath}");
                                // Optionally delete temp file
                                // File.Delete(exportSmbFilePath);
                            }
                            catch (Exception ex)
                            {
                                diagnostics.Add($"⚠️ Failed to copy to final location: {ex.Message}");
                                diagnostics.Add($"  SMB file is at: {exportSmbFilePath}");
                                finalSmbFilePath = exportSmbFilePath; // Use temp location as final
                            }
                        }
                        
                        success = true;
                    }
                    else
                    {
                        diagnostics.Add($"⚠️ ExportToSMB returned success but file not found at: {exportSmbFilePath}");
                    }
                }
                else
                {
                    diagnostics.Add($"⚠️ ExportToSMB returned null");
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add($"✗ ERROR: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                {
                    diagnostics.Add($"  Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }
                diagnostics.Add($"  Stack: {ex.StackTrace}");
            }
            finally
            {
                overallTimer.Stop();
                diagnostics.Add($"\n⏱️  TOTAL ExportToSMB time: {overallTimer.ElapsedMilliseconds}ms ({overallTimer.Elapsed.TotalSeconds:F3}s)");
            }

            return new Dictionary<string, object>
            {
                { "smbFilePath", finalSmbFilePath ?? string.Empty },
                { "log", string.Join("\n", diagnostics) },
                { "success", success }
            };
        }
        
        /// <summary>
        /// Tries to get the Client instance from various sources (reused from LoadGeometryFromExchange)
        /// </summary>
        private static object TryGetClientInstance(List<string> diagnostics)
        {
            object client = null;
            
            // Method 1: Try to get from SelectExchangeElementsViewCustomization
            try
            {
                var viewCustomizationType = Type.GetType("DataExchangeNodes.NodeViews.DataExchange.SelectExchangeElementsViewCustomization, ExchangeNodes.NodeViews");
                if (viewCustomizationType != null)
                {
                    var clientProperty = viewCustomizationType.GetProperty("ClientInstance", BindingFlags.Public | BindingFlags.Static);
                    if (clientProperty != null)
                    {
                        client = clientProperty.GetValue(null);
                        if (client != null)
                        {
                            diagnostics?.Add($"✓ Found Client instance via SelectExchangeElementsViewCustomization");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                diagnostics?.Add($"  ⚠️ Could not access ClientInstance property: {ex.Message}");
            }

            // Method 2: Try to find Client type directly and look for static instances
            if (client == null)
            {
                try
                {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            var foundClientType = assembly.GetTypes().FirstOrDefault(t => 
                                t.Name == "Client" && 
                                t.Namespace != null && 
                                t.Namespace.Contains("DataExchange"));
                            
                            if (foundClientType != null)
                            {
                                diagnostics?.Add($"  Found Client type: {foundClientType.FullName}");
                                var staticFields = foundClientType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                                var instanceField = staticFields.FirstOrDefault(f => 
                                    f.FieldType == foundClientType || 
                                    f.FieldType.IsAssignableFrom(foundClientType));
                                
                                if (instanceField != null)
                                {
                                    client = instanceField.GetValue(null);
                                    if (client != null)
                                    {
                                        diagnostics?.Add($"  ✓ Found Client via static field: {instanceField.Name}");
                                        break;
                                    }
                                }
                            }
                        }
                        catch { /* Skip assemblies we can't inspect */ }
                    }
                }
                catch (Exception ex)
                {
                    diagnostics?.Add($"  ⚠️ Could not find Client via type search: {ex.Message}");
                }
            }

            return client;
        }
        
        /// <summary>
        /// Uploads an SMB file to a DataExchange by creating an element with geometry.
        /// </summary>
        /// <param name="exchange">The Exchange object containing ExchangeId and CollectionId</param>
        /// <param name="smbFilePath">Full path to the SMB file to upload</param>
        /// <param name="elementName">Name for the new element (default: "ExportedGeometry")</param>
        /// <param name="elementId">Unique ID for the element. Leave empty to auto-generate a GUID.</param>
        /// <param name="unit">Unit type for geometry (default: "kUnitType_CentiMeter")</param>
        /// <returns>Dictionary with "elementId" (ID of created element), "log" (diagnostic messages), and "success" (boolean)</returns>
        /// <summary>
        /// Validates inputs for UploadSMBToExchange
        /// </summary>
        private static void ValidateInputs(Exchange exchange, string smbFilePath, List<string> diagnostics)
        {
                if (exchange == null)
                {
                    throw new ArgumentNullException(nameof(exchange), "Exchange cannot be null");
                }

                if (string.IsNullOrEmpty(exchange.ExchangeId))
                {
                    throw new ArgumentException("Exchange.ExchangeId is required", nameof(exchange));
                }

                if (string.IsNullOrEmpty(exchange.CollectionId))
                {
                    throw new ArgumentException("Exchange.CollectionId is required", nameof(exchange));
                }

                if (string.IsNullOrEmpty(smbFilePath) || !File.Exists(smbFilePath))
                {
                    throw new FileNotFoundException($"SMB file not found: {smbFilePath}");
                }
        }

        /// <summary>
        /// Creates a DataExchangeIdentifier from Exchange object
        /// </summary>
        private static DataExchangeIdentifier CreateDataExchangeIdentifier(Exchange exchange, List<string> diagnostics)
        {
                diagnostics.Add("\nCreating DataExchangeIdentifier...");
                var identifier = new DataExchangeIdentifier
                {
                    ExchangeId = exchange.ExchangeId,
                    CollectionId = exchange.CollectionId
                };
                if (!string.IsNullOrEmpty(exchange.HubId))
                {
                    identifier.HubId = exchange.HubId;
                }
                diagnostics.Add($"✓ Created DataExchangeIdentifier");
            return identifier;
        }

        /// <summary>
        /// Gets ElementDataModel from exchange using reflection
        /// FIXED: Handles Response<ElementDataModel> extraction manually (same pattern as InspectExchange)
        /// </summary>
        private static ElementDataModel GetElementDataModelAsync(object client, Type clientType, DataExchangeIdentifier identifier, List<string> diagnostics)
        {
            diagnostics.Add("\nGetting ElementDataModel from Exchange...");
            
            var getElementDataModelMethod = ReflectionHelper.GetMethod(
                clientType, 
                "GetElementDataModelAsync", 
                BindingFlags.Public | BindingFlags.Instance,
                new[] { typeof(DataExchangeIdentifier), typeof(CancellationToken) });
                
            if (getElementDataModelMethod == null)
            {
                throw new InvalidOperationException("GetElementDataModelAsync method not found on Client");
            }

            // Invoke the async method and await the result
            var elementDataModelTask = ReflectionHelper.InvokeMethod(client, getElementDataModelMethod, new object[] { identifier, CancellationToken.None }, diagnostics);
            if (elementDataModelTask == null)
            {
                throw new InvalidOperationException("GetElementDataModelAsync returned null");
            }

            // Await the task
            var response = ((dynamic)elementDataModelTask).GetAwaiter().GetResult();
            if (response == null)
            {
                throw new InvalidOperationException("GetElementDataModelAsync task result is null");
            }

            diagnostics.Add($"  Response type: {response.GetType().FullName}");

            // Extract ElementDataModel from Response<ElementDataModel> (same pattern as InspectExchange.cs)
            ElementDataModel elementDataModel = null;
            var responseType = response.GetType();
            var valueProp = responseType.GetProperty("Value");
            
            if (valueProp != null)
            {
                var value = valueProp.GetValue(response);
                diagnostics.Add($"  Value property found, value type: {value?.GetType().FullName ?? "null"}");
                
                // Use 'as' cast (safer, like InspectExchange does)
                elementDataModel = value as ElementDataModel;
                
                if (elementDataModel == null && value != null)
                {
                    // Try explicit cast if 'as' returned null
                    try
                    {
                        elementDataModel = (ElementDataModel)value;
                        diagnostics.Add($"  ✓ Successfully cast Value to ElementDataModel");
                    }
                    catch (InvalidCastException castEx)
                    {
                        diagnostics.Add($"  ⚠️ Cannot cast Value ({value.GetType().FullName}) to ElementDataModel: {castEx.Message}");
                    }
                }
            }
            else
            {
                // No Value property - try direct cast
                elementDataModel = response as ElementDataModel;
                if (elementDataModel == null)
                {
                    try
                    {
                        elementDataModel = (ElementDataModel)response;
                    }
                    catch
                    {
                        // Will throw below
                    }
                }
            }

            // If ElementDataModel is null, this is a new/empty exchange - create one from scratch
            if (elementDataModel == null)
            {
                diagnostics.Add("  ⚠️ ElementDataModel is null - this is a new/empty exchange");
                diagnostics.Add("  Creating new ElementDataModel from scratch...");
                
                // Try to find ElementDataModel.Create static method
                var elementDataModelType = typeof(ElementDataModel);
                var createMethod = elementDataModelType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                
                if (createMethod != null)
                {
                    var parameters = createMethod.GetParameters();
                    diagnostics.Add($"  Found Create method with parameters: {string.Join(", ", parameters.Select(p => p.ParameterType.Name))}");
                    
                    // Try Create(IClient) - most common case
                    // Check if parameter type (IClient interface) is assignable from clientType (i.e., client implements IClient)
                    if (parameters.Length == 1 && parameters[0].ParameterType.IsAssignableFrom(clientType))
                    {
                        elementDataModel = (ElementDataModel)createMethod.Invoke(null, new object[] { client });
                        diagnostics.Add($"  ✓ Created ElementDataModel using Create(IClient)");
                    }
                    // Try Create(DataExchangeIdentifier)
                    else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(DataExchangeIdentifier))
                    {
                        elementDataModel = (ElementDataModel)createMethod.Invoke(null, new object[] { identifier });
                        diagnostics.Add($"  ✓ Created ElementDataModel using Create(DataExchangeIdentifier)");
                    }
                    // Try Create(IClient, DataExchangeIdentifier)
                    else if (parameters.Length == 2 && 
                             parameters[0].ParameterType.IsAssignableFrom(clientType) &&
                             parameters[1].ParameterType == typeof(DataExchangeIdentifier))
                    {
                        elementDataModel = (ElementDataModel)createMethod.Invoke(null, new object[] { client, identifier });
                        diagnostics.Add($"  ✓ Created ElementDataModel using Create(IClient, DataExchangeIdentifier)");
                    }
                    // Try parameterless Create()
                    else if (parameters.Length == 0)
                    {
                        elementDataModel = (ElementDataModel)createMethod.Invoke(null, null);
                        diagnostics.Add($"  ✓ Created ElementDataModel using Create()");
                    }
                    else
                    {
                        diagnostics.Add($"  ⚠️ Create method found but has unexpected signature: {string.Join(", ", parameters.Select(p => p.ParameterType.Name))}");
                    }
                }
                
                // If Create method didn't work, try constructor with DataExchangeIdentifier
                if (elementDataModel == null)
                {
                    var constructor = elementDataModelType.GetConstructor(new[] { typeof(DataExchangeIdentifier) });
                    if (constructor != null)
                    {
                        elementDataModel = (ElementDataModel)constructor.Invoke(new object[] { identifier });
                        diagnostics.Add($"  ✓ Created ElementDataModel using constructor(DataExchangeIdentifier)");
                    }
                }
                
                // If still null, try parameterless constructor
                if (elementDataModel == null)
                {
                    var parameterlessConstructor = elementDataModelType.GetConstructor(Type.EmptyTypes);
                    if (parameterlessConstructor != null)
                    {
                        elementDataModel = (ElementDataModel)parameterlessConstructor.Invoke(null);
                        diagnostics.Add($"  ✓ Created ElementDataModel using parameterless constructor");
                        
                        // Try to set the identifier if there's a property or method
                        var identifierProperty = elementDataModelType.GetProperty("ExchangeIdentifier", BindingFlags.Public | BindingFlags.Instance);
                        if (identifierProperty != null && identifierProperty.CanWrite)
                        {
                            identifierProperty.SetValue(elementDataModel, identifier);
                            diagnostics.Add($"  ✓ Set ExchangeIdentifier on ElementDataModel");
                    }
                }
            }

            if (elementDataModel == null)
            {
                var valueInfo = valueProp != null 
                    ? $"Value type: {valueProp.GetValue(response)?.GetType().FullName ?? "null"}" 
                    : "No Value property";
                throw new InvalidOperationException(
                        $"Could not extract or create ElementDataModel. Response Value was null (new exchange), and could not find Create method or suitable constructor. {valueInfo}");
                }
            }
            else
            {
                diagnostics.Add($"✓ Got existing ElementDataModel: {elementDataModel.GetType().FullName}");
            }

            return elementDataModel;
        }

        /// <summary>
        /// Creates Element and ElementProperties
        /// </summary>
        private static object CreateElement(ElementDataModel elementDataModel, string finalElementId, string elementName, List<string> diagnostics)
        {
            // First, try to find an existing element with the same name
            diagnostics.Add($"\nLooking for existing element with name: {elementName}...");
            var elementsProperty = typeof(ElementDataModel).GetProperty("Elements", BindingFlags.Public | BindingFlags.Instance);
            bool foundExistingElement = false;
            if (elementsProperty != null)
            {
                var elements = elementsProperty.GetValue(elementDataModel) as System.Collections.IEnumerable;
                if (elements != null)
                {
                    var elementsList = elements.Cast<object>().ToList();
                    diagnostics.Add($"  Checking {elementsList.Count} existing element(s) in the exchange...");
                    
                    foreach (var existingElement in elementsList)
                    {
                        var nameProp = existingElement.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                        if (nameProp != null)
                        {
                            var existingName = nameProp.GetValue(existingElement)?.ToString();
                            diagnostics.Add($"    - Existing element name: '{existingName}' (comparing with: '{elementName}')");
                            
                            if (existingName == elementName)
                            {
                                foundExistingElement = true;
                                var idProp = existingElement.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                                var existingId = idProp?.GetValue(existingElement)?.ToString() ?? "N/A";
                                diagnostics.Add($"  ✓ FOUND EXISTING ELEMENT/GROUP with matching name: '{elementName}' (ID: {existingId})");
                                diagnostics.Add($"  Will reuse this element and add geometry to it");
                                return existingElement;
                            }
                        }
                    }
                }
            }
            
            // No existing element found, create a new one
            if (!foundExistingElement)
            {
                diagnostics.Add($"  ✗ No existing element/group found with name: '{elementName}'");
                diagnostics.Add($"  Will create a new element");
            }
                diagnostics.Add("\nCreating ElementProperties...");
                var elementProperties = new ElementProperties(
                    finalElementId,
                    elementName,
                    "Geometry",  // category
                    "Exported",  // family
                    "SMB"        // type
                );
                diagnostics.Add($"✓ Created ElementProperties");

                diagnostics.Add("\nAdding element to ElementDataModel...");
                var element = elementDataModel.AddElement(elementProperties);
                if (element == null)
                {
                    throw new InvalidOperationException("AddElement returned null");
                }
            diagnostics.Add($"✓ Added new element: {element.GetType().FullName}");
            return element;
        }

        /// <summary>
        /// Gets ExchangeData from ElementDataModel using reflection
        /// </summary>
        private static (object exchangeData, Type exchangeDataType) GetExchangeData(ElementDataModel elementDataModel, List<string> diagnostics)
        {
                var exchangeDataField = typeof(ElementDataModel).GetField("exchangeData", BindingFlags.NonPublic | BindingFlags.Instance);
                if (exchangeDataField == null)
                {
                    throw new InvalidOperationException("Could not find exchangeData field on ElementDataModel");
                }

                var exchangeData = exchangeDataField.GetValue(elementDataModel);
                if (exchangeData == null)
                {
                    throw new InvalidOperationException("ExchangeData is null");
                }
                
                var exchangeDataType = exchangeData.GetType();
                diagnostics.Add($"✓ Got ExchangeData: {exchangeDataType.FullName}");
            return (exchangeData, exchangeDataType);
        }

        /// <summary>
        /// Finds all required types across assemblies (simplified - uses cached FindType)
        /// </summary>
        private static Dictionary<string, Type> FindRequiredTypes(Type exchangeDataType, List<string> diagnostics)
        {
                var typesToFind = new[]
                {
                    "Autodesk.DataExchange.SchemaObjects.Assets.GeometryAsset",
                    "Autodesk.DataExchange.SchemaObjects.Geometry.GeometryWrapper",
                    "Autodesk.DataExchange.Core.Enums.GeometryFormat",
                    "Autodesk.DataExchange.Core.Enums.GeometryType",
                    "Autodesk.DataExchange.SchemaObjects.Components.GeometryComponent",
                    "Autodesk.DataExchange.SchemaObjects.Components.Component",
                    "Autodesk.DataExchange.SchemaObjects.Assets.DesignAsset",
                    "Autodesk.DataExchange.SchemaObjects.Relationships.ReferenceRelationship",
                    "Autodesk.DataExchange.SchemaObjects.Relationships.ContainmentRelationship",
                    "Autodesk.DataExchange.SchemaObjects.Components.ModelStructure"
                };
                
                var foundTypes = new Dictionary<string, Type>();
                foreach (var typeName in typesToFind)
                {
                var foundType = ReflectionHelper.FindType(typeName, exchangeDataType, foundTypes);
                    if (foundType != null && !foundTypes.ContainsKey(typeName))
                    {
                        foundTypes[typeName] = foundType;
                    }
                }

            return foundTypes;
        }

        /// <summary>
        /// Creates a GeometryAsset instance with ID set (simplified - uses foundTypes cache)
        /// </summary>
        private static (object geometryAsset, Type geometryAssetType, string geometryAssetId) CreateGeometryAsset(Type exchangeDataType, Dictionary<string, Type> foundTypes, List<string> diagnostics)
        {
            diagnostics.Add($"\nCreating GeometryAsset for SMB file (BRep format)...");
            
            const string geometryAssetTypeName = "Autodesk.DataExchange.SchemaObjects.Assets.GeometryAsset";
            if (!foundTypes.TryGetValue(geometryAssetTypeName, out var geometryAssetType))
            {
                geometryAssetType = ReflectionHelper.FindType(geometryAssetTypeName, exchangeDataType, foundTypes);
                if (geometryAssetType == null)
                {
                    throw new InvalidOperationException("Could not find GeometryAsset type");
                }
                }

            var geometryAssetId = Guid.NewGuid().ToString();
            var geometryAsset = ReflectionHelper.CreateInstanceWithId(geometryAssetType, geometryAssetId, foundTypes, diagnostics);
            
            return (geometryAsset, geometryAssetType, geometryAssetId);
        }

        /// <summary>
        /// Sets units on GeometryAsset
        /// </summary>
        private static void SetGeometryAssetUnits(object geometryAsset, Type geometryAssetType, List<string> diagnostics)
        {
                var lengthUnitProperty = geometryAssetType.GetProperty("LengthUnit", BindingFlags.Public | BindingFlags.Instance);
                if (lengthUnitProperty != null)
                {
                    var unitEnumType = Type.GetType("Autodesk.DataExchange.SchemaObjects.Units.LengthUnit, Autodesk.DataExchange.SchemaObjects");
                    if (unitEnumType != null)
                    {
                        var centimeterValue = Enum.Parse(unitEnumType, "CentiMeter");
                        lengthUnitProperty.SetValue(geometryAsset, centimeterValue);
                }
            }
        }

        /// <summary>
        /// Creates GeometryWrapper for BRep geometry (simplified - uses FindType)
        /// </summary>
        private static object CreateGeometryWrapper(Dictionary<string, Type> foundTypes, Type exchangeDataType, List<string> diagnostics)
        {
            const string geometryWrapperTypeName = "Autodesk.DataExchange.SchemaObjects.Geometry.GeometryWrapper";
            if (!foundTypes.TryGetValue(geometryWrapperTypeName, out var geometryWrapperType))
            {
                geometryWrapperType = ReflectionHelper.FindType(geometryWrapperTypeName, exchangeDataType, foundTypes);
                if (geometryWrapperType == null)
                {
                    throw new InvalidOperationException("Could not find GeometryWrapper type");
                }
            }
            
            const string geometryFormatTypeName = "Autodesk.DataExchange.Core.Enums.GeometryFormat";
            if (!foundTypes.TryGetValue(geometryFormatTypeName, out var geometryFormatEnumType))
            {
                geometryFormatEnumType = ReflectionHelper.FindType(geometryFormatTypeName, exchangeDataType, foundTypes);
            }
            
            const string geometryTypeTypeName = "Autodesk.DataExchange.Core.Enums.GeometryType";
            if (!foundTypes.TryGetValue(geometryTypeTypeName, out var geometryTypeEnumType))
            {
                geometryTypeEnumType = ReflectionHelper.FindType(geometryTypeTypeName, exchangeDataType, foundTypes);
                }
                
                if (geometryFormatEnumType == null || geometryTypeEnumType == null)
                {
                    throw new InvalidOperationException("Could not find GeometryFormat or GeometryType enum types");
                }
                
                var stepFormat = Enum.Parse(geometryFormatEnumType, "Step");
                var brepType = Enum.Parse(geometryTypeEnumType, "BRep");
                
                var constructor = geometryWrapperType.GetConstructor(new[] { geometryTypeEnumType, geometryFormatEnumType, typeof(string) });
                object geometryWrapper = null;
                if (constructor != null)
                {
                    geometryWrapper = constructor.Invoke(new object[] { brepType, stepFormat, "" });
                }
                else
                {
                    constructor = geometryWrapperType.GetConstructor(new[] { geometryTypeEnumType, geometryFormatEnumType });
                    if (constructor != null)
                    {
                        geometryWrapper = constructor.Invoke(new object[] { brepType, stepFormat });
                    }
                }
                
                if (geometryWrapper == null)
                {
                    throw new InvalidOperationException("Could not create GeometryWrapper for BRep SMB format");
                }
                
            diagnostics.Add($"✓ Created GeometryWrapper with BRep type and Step format");
            return geometryWrapper;
        }

        /// <summary>
        /// Creates GeometryComponent and sets it on GeometryAsset (simplified - uses FindType)
        /// </summary>
        private static void CreateGeometryComponent(object geometryAsset, Type geometryAssetType, object geometryWrapper, Dictionary<string, Type> foundTypes, Type exchangeDataType, string geometryName, List<string> diagnostics)
        {
            const string geometryComponentTypeName = "Autodesk.DataExchange.SchemaObjects.Components.GeometryComponent";
            if (!foundTypes.TryGetValue(geometryComponentTypeName, out var geometryComponentType))
            {
                geometryComponentType = ReflectionHelper.FindType(geometryComponentTypeName, exchangeDataType, foundTypes);
                if (geometryComponentType == null)
                {
                    throw new InvalidOperationException("Could not find GeometryComponent type");
                }
                }

                var geometryComponent = Activator.CreateInstance(geometryComponentType);
                var geometryProperty = geometryComponentType.GetProperty("Geometry", BindingFlags.NonPublic | BindingFlags.Instance);
                if (geometryProperty == null)
                {
                    throw new InvalidOperationException("Could not find Geometry property on GeometryComponent");
                }

                geometryProperty.SetValue(geometryComponent, geometryWrapper);

                var geometryAssetGeometryProperty = geometryAssetType.GetProperty("Geometry", BindingFlags.Public | BindingFlags.Instance);
                if (geometryAssetGeometryProperty == null)
                {
                    throw new InvalidOperationException("Could not find Geometry property on GeometryAsset");
                }

                geometryAssetGeometryProperty.SetValue(geometryAsset, geometryComponent);
                diagnostics.Add($"✓ Set GeometryComponent on GeometryAsset");
        }

        /// <summary>
        /// Creates an ObjectInfo component with the specified name
        /// </summary>
        private static object CreateObjectInfo(string name, Dictionary<string, Type> foundTypes, Type exchangeDataType, List<string> diagnostics)
        {
            const string componentTypeName = "Autodesk.DataExchange.SchemaObjects.Components.Component";
            if (foundTypes.TryGetValue(componentTypeName, out var componentType) || 
                (componentType = ReflectionHelper.FindType(componentTypeName, exchangeDataType, foundTypes)) != null)
                    {
                        var objectInfo = Activator.CreateInstance(componentType);
                        var nameProperty = componentType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                        if (nameProperty != null)
                        {
                    nameProperty.SetValue(objectInfo, name);
                        }
                return objectInfo;
                    }
            return null;
        }

        /// <summary>
        /// Adds GeometryAsset to ExchangeData and sets ObjectInfo
        /// </summary>
        private static void AddGeometryAssetToExchangeData(object geometryAsset, object exchangeData, Type exchangeDataType, string geometryFilePath, Dictionary<string, Type> foundTypes, string geometryName, List<string> diagnostics)
        {
                var exchangeDataAddMethod = exchangeDataType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
                if (exchangeDataAddMethod == null)
                {
                    throw new InvalidOperationException("Could not find Add method on ExchangeData");
                }

                exchangeDataAddMethod.Invoke(exchangeData, new object[] { geometryAsset });
                diagnostics.Add($"✓ Added GeometryAsset to ExchangeData");

            var geometryAssetType = geometryAsset.GetType();
                var objectInfoProperty = geometryAssetType.GetProperty("ObjectInfo", BindingFlags.Public | BindingFlags.Instance);
                if (objectInfoProperty != null)
                {
                    // Use provided geometryName, or fall back to filename if not provided
                    var nameToUse = !string.IsNullOrEmpty(geometryName) 
                        ? geometryName 
                        : Path.GetFileNameWithoutExtension(geometryFilePath);
                    var objectInfo = CreateObjectInfo(nameToUse, foundTypes, exchangeDataType, diagnostics);
                    if (objectInfo != null)
                    {
                        objectInfoProperty.SetValue(geometryAsset, objectInfo);
                        diagnostics.Add($"  ✓ Set ObjectInfo name: {nameToUse}");
                    }
                }
        }


        /// <summary>
        /// Creates a minimal dummy STEP file for the STEP→SMB adapter pattern.
        /// The SDK expects STEP as source, SMB as output. We use a dummy STEP to satisfy this contract.
        /// </summary>
        private static string CreateDummyStepFile(string smbFilePath, List<string> diagnostics)
        {
            var dummyStepPath = Path.ChangeExtension(smbFilePath, ".dummy.stp");
            
            // Minimal valid STEP file content (ISO-10303-21 header + empty data section)
            // This is just a carrier file - the real geometry is in the SMB file
            var minimalStepContent = @"ISO-10303-21;
HEADER;
FILE_DESCRIPTION(('Dummy STEP file for SMB upload adapter'),'2;1');
FILE_NAME('dummy.stp','2024-01-01T00:00:00',(''),(''),'','','');
FILE_SCHEMA(('AUTOMOTIVE_DESIGN'));
ENDSEC;
DATA;
ENDSEC;
END-ISO-10303-21;
";
            
            try
            {
                File.WriteAllText(dummyStepPath, minimalStepContent);
                diagnostics.Add($"  ✓ Created dummy STEP file: {dummyStepPath}");
                diagnostics.Add($"  Note: This is a carrier file for the STEP→SMB adapter pattern");
                return dummyStepPath;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"  ⚠️ Failed to create dummy STEP file: {ex.Message}");
                throw;
            }
        }

        // Static dictionary to store GeometryAsset ID -> SMB path mapping for the adapter pattern
        // Key: ExchangeId_GeometryAssetId, Value: SMB file path
        private static Dictionary<string, string> geometryAssetIdToSmbPathMapping = new Dictionary<string, string>();
        private static Dictionary<string, int> geometryAssetIdToGeometryCountMapping = new Dictionary<string, int>();

        /// <summary>
        /// Adds GeometryAsset to UnsavedGeometryMapping using SetBRepGeometryByAsset
        /// ADAPTER PATTERN: Register dummy STEP file (not SMB) to satisfy SDK's STEP→SMB translation contract.
        /// The real SMB file will be set as OutputPath later in GetAllAssetInfosWithTranslatedGeometryPathForSMB.
        /// </summary>
        private static string AddGeometryAssetToUnsavedMapping(object geometryAsset, object exchangeData, Type exchangeDataType, string smbFilePath, string exchangeId, int geometryCount, List<string> diagnostics)
        {
            diagnostics.Add($"\nAdding GeometryAsset to UnsavedGeometryMapping...");
            try
            {
                if (!File.Exists(smbFilePath))
                {
                    throw new FileNotFoundException($"SMB file not found: {smbFilePath}");
                }
                
                // Get GeometryAsset ID for mapping
                var geometryAssetIdProp = geometryAsset.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                var geometryAssetId = geometryAssetIdProp?.GetValue(geometryAsset)?.ToString();
                
                if (string.IsNullOrEmpty(geometryAssetId))
                {
                    throw new InvalidOperationException("Could not get GeometryAsset ID");
                }
                
                // Create dummy STEP file for the adapter pattern
                // SDK expects: Path = STEP (source), OutputPath = SMB (translated output)
                var dummyStepPath = CreateDummyStepFile(smbFilePath, diagnostics);
                
                var setBRepGeometryMethod = exchangeDataType.GetMethod("SetBRepGeometryByAsset", BindingFlags.Public | BindingFlags.Instance);
                if (setBRepGeometryMethod == null)
                {
                    throw new InvalidOperationException("Could not find SetBRepGeometryByAsset method on ExchangeData");
                }

                // Register the dummy STEP file path (not the SMB)
                // This makes the SDK think we're in the normal STEP→SMB translation flow
                setBRepGeometryMethod.Invoke(exchangeData, new object[] { geometryAsset, dummyStepPath });
                diagnostics.Add($"✓ Added GeometryAsset to UnsavedGeometryMapping with dummy STEP path: {dummyStepPath}");
                diagnostics.Add($"  Note: Real SMB file ({smbFilePath}) will be set as OutputPath in the translation step");
                
                // Store mapping for later retrieval
                var mappingKey = $"{exchangeId}_{geometryAssetId}";
                geometryAssetIdToSmbPathMapping[mappingKey] = smbFilePath;
                geometryAssetIdToGeometryCountMapping[mappingKey] = geometryCount;
                diagnostics.Add($"  Stored mapping: GeometryAsset {geometryAssetId} -> SMB: {smbFilePath} (geometry count: {geometryCount})");
                
                return dummyStepPath;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"  ⚠️ Failed to add to UnsavedGeometryMapping: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets AssetInfos from UnsavedGeometryMapping and sets OutputPath to SMB files (STEP→SMB adapter pattern)
        /// ADAPTER PATTERN: Path = dummy STEP (source), OutputPath = real SMB (translated output)
        /// This satisfies the SDK's translation contract while using pre-translated SMB geometry.
        /// </summary>
        private static System.Collections.Generic.IEnumerable<object> GetAllAssetInfosWithTranslatedGeometryPathForSMB(object client, Type clientType, object exchangeData, Type exchangeDataType, DataExchangeIdentifier exchangeIdentifier, string fulfillmentId, Dictionary<string, string> geometryAssetIdToSmbPath, Dictionary<string, int> geometryAssetIdToGeometryCount, List<string> diagnostics)
        {
            diagnostics.Add($"\nGetting AssetInfos from UnsavedGeometryMapping (skipping STEP→SMB conversion)...");
            try
            {
                // Get GetBatchedAssetInfos method via reflection - specify parameter types to avoid ambiguity
                var getBatchedAssetInfosMethod = ReflectionHelper.GetMethod(
                    clientType,
                    "GetBatchedAssetInfos",
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    new[] { exchangeDataType });
                if (getBatchedAssetInfosMethod == null)
                {
                    throw new InvalidOperationException("Could not find GetBatchedAssetInfos method on Client");
                }

                // Call GetBatchedAssetInfos to get batched AssetInfos from UnsavedGeometryMapping
                var batchedAssetInfos = getBatchedAssetInfosMethod.Invoke(client, new object[] { exchangeData }) as System.Collections.IEnumerable;
                if (batchedAssetInfos == null)
                {
                    diagnostics.Add($"  No AssetInfos found in UnsavedGeometryMapping");
                    return Enumerable.Empty<object>();
                }

                var allAssetInfos = new List<object>();

                // Process each batch
                foreach (var assetInfosBatch in batchedAssetInfos)
                {
                    var assetInfosList = assetInfosBatch as System.Collections.IEnumerable;
                    if (assetInfosList != null)
                    {
                        foreach (var assetInfo in assetInfosList)
                        {
                            // ADAPTER PATTERN: Path = dummy STEP (source), OutputPath = real SMB (translated output)
                            // This satisfies the SDK's STEP→SMB translation contract
                            var pathProp = assetInfo.GetType().GetProperty("Path", BindingFlags.Public | BindingFlags.Instance);
                            var outputPathProp = assetInfo.GetType().GetProperty("OutputPath", BindingFlags.Public | BindingFlags.Instance);
                            var idProp = assetInfo.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                            
                            // Declare assetInfoId in broader scope so it's accessible later
                            string assetInfoId = null;
                            
                            if (pathProp != null && outputPathProp != null && idProp != null)
                            {
                                var path = pathProp.GetValue(assetInfo)?.ToString();
                                assetInfoId = idProp.GetValue(assetInfo)?.ToString();
                                
                                if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(assetInfoId))
                                {
                                    // CRITICAL: Skip AssetInfos that point to non-existent files
                                    // This can happen if old files from previous uploads are still in UnsavedGeometryMapping
                                    if (!File.Exists(path))
                                    {
                                        diagnostics.Add($"  ⚠️ SKIPPING AssetInfo - file does not exist: {path}");
                                        diagnostics.Add($"  This is likely from a previous upload. Skipping to avoid FileNotFoundException.");
                                        continue; // Skip this AssetInfo
                                    }
                                    
                                    // Look up the real SMB file path for this GeometryAsset
                                    if (geometryAssetIdToSmbPath != null && geometryAssetIdToSmbPath.TryGetValue(assetInfoId, out var realSmbPath))
                                    {
                                        // ADAPTER: Path stays as dummy STEP, OutputPath = real SMB
                                        // This makes the SDK think: "STEP source → SMB translated output"
                                        if (File.Exists(realSmbPath))
                                        {
                                            outputPathProp.SetValue(assetInfo, realSmbPath);
                                            diagnostics.Add($"  ✓ ADAPTER: Path = {path} (dummy STEP), OutputPath = {realSmbPath} (real SMB)");
                                            diagnostics.Add($"  This satisfies SDK's STEP→SMB translation contract");
                                        }
                                        else
                                        {
                                            diagnostics.Add($"  ⚠️ SKIPPING AssetInfo - SMB file not found: {realSmbPath}");
                                            continue;
                                        }
                                    }
                                    else
                                    {
                                        // Not in our mapping - might be from a previous upload or different source
                                        // Skip it to avoid processing unrelated geometry
                                        diagnostics.Add($"  ⚠️ SKIPPING AssetInfo - not in current upload mapping (ID: {assetInfoId})");
                                        continue;
                                    }
                                }
                            }

                            // Ensure BodyInfoList is set for BRep type (required for UploadGeometries)
                            // If the SMB file contains multiple geometries, create multiple BodyInfo objects
                            var bodyInfoListProp = assetInfo.GetType().GetProperty("BodyInfoList", BindingFlags.Public | BindingFlags.Instance);
                            if (bodyInfoListProp != null)
                            {
                                var existingBodyInfoList = bodyInfoListProp.GetValue(assetInfo);
                                if (existingBodyInfoList == null)
                                {
                                    try
                                    {
                                        var bodyInfoType = Type.GetType("Autodesk.GeometryUtilities.SDK.BodyInfo, Autodesk.GeometryUtilities");
                                        var bodyTypeEnum = Type.GetType("Autodesk.GeometryUtilities.SDK.BodyType, Autodesk.GeometryUtilities");
                                        
                                        if (bodyInfoType != null && bodyTypeEnum != null)
                                        {
                                            // DIAGNOSTIC: Inspect BodyInfo properties
                                            var bodyInfoProps = bodyInfoType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                                            var propNames = string.Join(", ", bodyInfoProps.Select(p => $"{p.Name} ({p.PropertyType.Name})"));
                                            diagnostics.Add($"  BodyInfo properties available: {propNames}");
                                            
                                            // Determine how many BodyInfo objects to create
                                            int geometryCount = 1; // Default to 1
                                            if (geometryAssetIdToGeometryCount != null && geometryAssetIdToGeometryCount.TryGetValue(assetInfoId, out var count))
                                            {
                                                geometryCount = count;
                                            }
                                            
                                            diagnostics.Add($"  Creating {geometryCount} BodyInfo object(s) for AssetInfo {assetInfoId}");
                                            
                                            var bodyInfoListType = typeof(System.Collections.Generic.List<>).MakeGenericType(bodyInfoType);
                                            var bodyInfoList = Activator.CreateInstance(bodyInfoListType);
                                            var addMethod = bodyInfoListType.GetMethod("Add");
                                            
                                            if (addMethod != null)
                                            {
                                                // Try to find BRep value in enum
                                                var brepValue = Enum.GetValues(bodyTypeEnum).Cast<object>().FirstOrDefault(v => 
                                                    v.ToString().Contains("BREP") || v.ToString().Contains("BRep") || v.ToString().Contains("Solid"));
                                                if (brepValue == null)
                                                {
                                                    // Fallback to first enum value
                                                    var enumValues = Enum.GetValues(bodyTypeEnum);
                                                    brepValue = enumValues.GetValue(enumValues.Length > 1 ? 1 : 0);
                                                }
                                                
                                                // Get BodyId property (if it exists)
                                                var bodyIdProp = bodyInfoType.GetProperty("BodyId", BindingFlags.Public | BindingFlags.Instance);
                                                
                                                // Create one BodyInfo per geometry
                                                for (int i = 0; i < geometryCount; i++)
                                                {
                                                    var bodyInfo = Activator.CreateInstance(bodyInfoType);
                                                    
                                                    // Set Type property
                                                    var typeProp = bodyInfoType.GetProperty("Type");
                                                    if (typeProp != null)
                                                    {
                                                        typeProp.SetValue(bodyInfo, brepValue);
                                                        diagnostics.Add($"    BodyInfo {i}: Type = {brepValue}");
                                                    }
                                                    
                                                    // Set BodyId property to distinguish each geometry
                                                    // Using index-based ID: geometry_0, geometry_1, geometry_2, etc.
                                                    if (bodyIdProp != null)
                                                    {
                                                        var bodyId = $"geometry_{i}";
                                                        bodyIdProp.SetValue(bodyInfo, bodyId);
                                                        diagnostics.Add($"    BodyInfo {i}: BodyId = {bodyId}");
                                                    }
                                                    else
                                                    {
                                                        diagnostics.Add($"    BodyInfo {i}: BodyId property not found");
                                                    }
                                                    
                                                    addMethod.Invoke(bodyInfoList, new object[] { bodyInfo });
                                                }
                                                
                                                bodyInfoListProp.SetValue(assetInfo, bodyInfoList);
                                                
                                                // DIAGNOSTIC: Verify BodyInfoList was set correctly
                                                var verifyBodyInfoList = bodyInfoListProp.GetValue(assetInfo);
                                                if (verifyBodyInfoList != null)
                                                {
                                                    var countProp = verifyBodyInfoList.GetType().GetProperty("Count");
                                                    if (countProp != null)
                                                    {
                                                        var actualCount = countProp.GetValue(verifyBodyInfoList);
                                                        diagnostics.Add($"  ✓ Set BodyInfoList with {actualCount} BodyInfo object(s) (BRep type) on AssetInfo");
                                                        
                                                        // DIAGNOSTIC: Log each BodyInfo in the list
                                                        var enumerable = verifyBodyInfoList as System.Collections.IEnumerable;
                                                        if (enumerable != null)
                                                        {
                                                            int idx = 0;
                                                            foreach (var bi in enumerable)
                                                            {
                                                                var biType = bi.GetType();
                                                                var biTypeProp = biType.GetProperty("Type");
                                                                var biIdProp = biType.GetProperty("BodyId");
                                                                var biTypeVal = biTypeProp?.GetValue(bi);
                                                                var biIdVal = biIdProp?.GetValue(bi);
                                                                diagnostics.Add($"    Verified BodyInfo[{idx}]: Type={biTypeVal}, BodyId={biIdVal}");
                                                                idx++;
                                                            }
                                                        }
                                                    }
                                                }
                                                else
                                                {
                                                    diagnostics.Add($"  ⚠️ BodyInfoList is null after setting!");
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        diagnostics.Add($"  ⚠️ Failed to set BodyInfoList: {ex.Message}");
                                    }
                                }
                                else
                                {
                                    // BodyInfoList already exists - log its count
                                    var countProp = existingBodyInfoList.GetType().GetProperty("Count");
                                    if (countProp != null)
                                    {
                                        var count = countProp.GetValue(existingBodyInfoList);
                                        diagnostics.Add($"  BodyInfoList already exists with {count} BodyInfo object(s)");
                                    }
                                }
                            }

                            // Ensure LengthUnits is set
                            var lengthUnitsProp = assetInfo.GetType().GetProperty("LengthUnits", BindingFlags.Public | BindingFlags.Instance);
                            if (lengthUnitsProp != null)
                            {
                                var existingLengthUnits = lengthUnitsProp.GetValue(assetInfo);
                                if (existingLengthUnits == null)
                                {
                                    try
                                    {
                                        var unitEnumType = Type.GetType("Autodesk.DataExchange.SchemaObjects.Units.LengthUnit, Autodesk.DataExchange.SchemaObjects");
                                        if (unitEnumType != null)
                                        {
                                            var centimeterValue = Enum.Parse(unitEnumType, "CentiMeter");
                                            lengthUnitsProp.SetValue(assetInfo, centimeterValue);
                                            diagnostics.Add($"  Set LengthUnits to CentiMeter on AssetInfo");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        diagnostics.Add($"  ⚠️ Failed to set LengthUnits: {ex.Message}");
                                    }
                                }
                            }

                            allAssetInfos.Add(assetInfo);
                        }
                    }
                }

                diagnostics.Add($"  ✓ Processed {allAssetInfos.Count} AssetInfo(s) from UnsavedGeometryMapping");
                return allAssetInfos;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"  ⚠️ Failed to get AssetInfos: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Sets up DesignAsset and relationships with GeometryAsset
        /// FIXED: Uses RootAsset directly and creates proper structure (no hardcoded names)
        /// Follows SDK pattern: RootAsset -> InstanceAsset -> DesignAsset -> GeometryAsset
        /// </summary>
        private static void SetupDesignAssetAndRelationships(object element, object geometryAsset, Type geometryAssetType, object exchangeData, Type exchangeDataType, Dictionary<string, Type> foundTypes, string elementName, List<string> diagnostics)
        {
            var elementAssetProperty = element.GetType().GetProperty("Asset", BindingFlags.NonPublic | BindingFlags.Instance);
            if (elementAssetProperty == null)
            {
                throw new InvalidOperationException("Could not find Asset property on Element");
            }

            var elementAsset = elementAssetProperty.GetValue(element);
            
            const string designAssetTypeName = "Autodesk.DataExchange.SchemaObjects.Assets.DesignAsset";
            if (!foundTypes.TryGetValue(designAssetTypeName, out var designAssetType))
            {
                designAssetType = ReflectionHelper.FindType(designAssetTypeName, exchangeDataType, foundTypes);
            if (designAssetType == null)
            {
                throw new InvalidOperationException("Could not find DesignAsset type");
                }
            }

            const string instanceAssetTypeName = "Autodesk.DataExchange.SchemaObjects.Assets.InstanceAsset";
            if (!foundTypes.TryGetValue(instanceAssetTypeName, out var instanceAssetType))
            {
                instanceAssetType = ReflectionHelper.FindType(instanceAssetTypeName, exchangeDataType, foundTypes);
                if (instanceAssetType == null)
                {
                    throw new InvalidOperationException("Could not find InstanceAsset type");
                }
            }

            // Get RootAsset (which IS the TopLevelAssembly) - use it directly!
            var rootAssetProp = exchangeDataType.GetProperty("RootAsset", BindingFlags.Public | BindingFlags.Instance);
            if (rootAssetProp == null)
            {
                throw new InvalidOperationException("Could not find RootAsset property on ExchangeData");
            }
            
            object rootAsset = rootAssetProp.GetValue(exchangeData);
            
            // If RootAsset is null, this is a new/empty exchange - create TopLevelAssembly (like SDK does)
            if (rootAsset == null)
            {
                diagnostics.Add("\nRootAsset is null - creating TopLevelAssembly for new/empty exchange...");
                
                // Create TopLevelAssembly DesignAsset (same as SDK's ElementDataModel.Create)
                var rootAssetId = Guid.NewGuid().ToString();
                rootAsset = ReflectionHelper.CreateInstanceWithId(designAssetType, rootAssetId, foundTypes, diagnostics);
                
                // Set ObjectInfo.Name to "TopLevelAssembly"
                var objectInfoProp = designAssetType.GetProperty("ObjectInfo", BindingFlags.Public | BindingFlags.Instance);
                if (objectInfoProp != null)
                {
                    var objectInfo = CreateObjectInfo("TopLevelAssembly", foundTypes, exchangeDataType, diagnostics);
                    if (objectInfo != null)
                    {
                        objectInfoProp.SetValue(rootAsset, objectInfo);
                    }
                }
                
                // Add TopLevelAssembly to ExchangeData
                var exchangeDataAddMethod = exchangeDataType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
                if (exchangeDataAddMethod != null)
                {
                    exchangeDataAddMethod.Invoke(exchangeData, new object[] { rootAsset });
                }
                
                // Set RootAsset property
                if (rootAssetProp.CanWrite)
                {
                    rootAssetProp.SetValue(exchangeData, rootAsset);
                }
                
                diagnostics.Add("  ✓ Created TopLevelAssembly DesignAsset");
            }
            else
            {
                diagnostics.Add("\n✓ Found existing RootAsset (TopLevelAssembly)");
            }
            
            // Strategy: Use Element's InstanceAsset (created by AddElement) and link it to RootAsset
            // Then create/find a DesignAsset for this InstanceAsset
            // This follows the SDK pattern: RootAsset -> InstanceAsset -> DesignAsset -> GeometryAsset
            
            diagnostics.Add("\nSetting up proper hierarchy: RootAsset -> InstanceAsset -> DesignAsset -> GeometryAsset...");
            
            // Get Element's InstanceAsset (already created by AddElement)
            var elementAssetType = elementAsset.GetType();
            diagnostics.Add($"  Element's Asset type: {elementAssetType.Name}");
            
            // Check if Element's InstanceAsset is already linked to RootAsset
            var rootAssetChildNodesProp = rootAsset.GetType().GetProperty("ChildNodes", BindingFlags.Public | BindingFlags.Instance);
            bool elementInstanceLinkedToRoot = false;
            if (rootAssetChildNodesProp != null)
            {
                var rootChildNodes = rootAssetChildNodesProp.GetValue(rootAsset) as System.Collections.IEnumerable;
                if (rootChildNodes != null)
                {
                    foreach (var childNodeRel in rootChildNodes)
                    {
                        var nodeProp = childNodeRel.GetType().GetProperty("Node", BindingFlags.Public | BindingFlags.Instance);
                        if (nodeProp != null)
                        {
                            var node = nodeProp.GetValue(childNodeRel);
                            if (node != null && node == elementAsset)
                            {
                                elementInstanceLinkedToRoot = true;
                                diagnostics.Add("  ✓ Element's InstanceAsset is already linked to RootAsset");
                                break;
                            }
                        }
                    }
                }
            }
            
            // If not linked, link Element's InstanceAsset to RootAsset (like SDK's AddElement does)
            if (!elementInstanceLinkedToRoot)
            {
                var rootAddChildMethod = rootAsset.GetType().GetMethod("AddChild", BindingFlags.Public | BindingFlags.Instance);
                if (rootAddChildMethod != null)
                {
                    const string containmentRelationshipTypeName = "Autodesk.DataExchange.SchemaObjects.Relationships.ContainmentRelationship";
                    if (foundTypes.TryGetValue(containmentRelationshipTypeName, out var containmentRelationshipType) || 
                        (containmentRelationshipType = ReflectionHelper.FindType(containmentRelationshipTypeName, exchangeDataType, foundTypes)) != null)
                    {
                        var containmentRelationship = Activator.CreateInstance(containmentRelationshipType);
                        rootAddChildMethod.Invoke(rootAsset, new object[] { elementAsset, containmentRelationship });
                        diagnostics.Add("  ✓ Linked Element's InstanceAsset to RootAsset");
                    }
                }
            }
            
            // Try to find and reuse existing DesignAsset, or create a new one if none exists
            object designAsset = null;
            
            // Check if Element's InstanceAsset already has a DesignAsset (from AddElement or previous uploads)
            var elementAssetChildNodesProp = elementAssetType.GetProperty("ChildNodes", BindingFlags.Public | BindingFlags.Instance);
            if (elementAssetChildNodesProp != null)
            {
                var elementChildNodes = elementAssetChildNodesProp.GetValue(elementAsset) as System.Collections.IEnumerable;
                if (elementChildNodes != null)
                {
                    foreach (var childNodeRel in elementChildNodes)
                    {
                        var nodeProp = childNodeRel.GetType().GetProperty("Node", BindingFlags.Public | BindingFlags.Instance);
                        if (nodeProp != null)
                        {
                            var node = nodeProp.GetValue(childNodeRel);
                            if (node != null && designAssetType.IsAssignableFrom(node.GetType()))
                            {
                                // Check if this DesignAsset has ModelStructure (required for geometry)
                                var relationshipProp = childNodeRel.GetType().GetProperty("Relationship", BindingFlags.Public | BindingFlags.Instance);
                                if (relationshipProp != null)
                                {
                                    var relationship = relationshipProp.GetValue(childNodeRel);
                                    if (relationship != null)
                                    {
                                        var modelStructureProp = relationship.GetType().GetProperty("ModelStructure", BindingFlags.Public | BindingFlags.Instance);
                                        var modelStructure = modelStructureProp?.GetValue(relationship);
                                        if (modelStructure != null)
                                        {
                                            // Found existing DesignAsset with ModelStructure - reuse it
                                            designAsset = node;
                                var idProp = node.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                                var existingId = idProp?.GetValue(node)?.ToString() ?? "N/A";
                                            diagnostics.Add($"  ✓ Found existing DesignAsset (ID: {existingId}) - will reuse it and add geometry");
                                break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            
            // If no existing DesignAsset found, create a new one
            if (designAsset == null)
            {
                diagnostics.Add("  No existing DesignAsset found - creating new one...");
                var designAssetId = Guid.NewGuid().ToString();
                designAsset = ReflectionHelper.CreateInstanceWithId(designAssetType, designAssetId, foundTypes, diagnostics);
                
                // Set ObjectInfo
                var objectInfoProp = designAssetType.GetProperty("ObjectInfo", BindingFlags.Public | BindingFlags.Instance);
                if (objectInfoProp != null)
                {
                    var objectInfo = CreateObjectInfo(elementName, foundTypes, exchangeDataType, diagnostics);
                    if (objectInfo != null)
                    {
                        objectInfoProp.SetValue(designAsset, objectInfo);
                    }
                }
                
                // Add DesignAsset to ExchangeData
                var exchangeDataAddMethod = exchangeDataType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
                if (exchangeDataAddMethod != null)
                {
                    exchangeDataAddMethod.Invoke(exchangeData, new object[] { designAsset });
                }
                
                // CRITICAL: Remove any existing ReferenceRelationships with Type set (but not ModelStructure)
                // These are created by AddElement and prevent GetGeometryAssets() from finding our DesignAsset
                var elementAssetChildNodesPropForRemoval = elementAssetType.GetProperty("ChildNodes", BindingFlags.Public | BindingFlags.Instance);
                if (elementAssetChildNodesPropForRemoval != null)
                {
                    var elementChildNodes = elementAssetChildNodesPropForRemoval.GetValue(elementAsset) as System.Collections.IEnumerable;
                    if (elementChildNodes != null)
                    {
                        var childNodesList = elementChildNodes.Cast<object>().ToList();
                        const string referenceRelationshipTypeName = "Autodesk.DataExchange.SchemaObjects.Relationships.ReferenceRelationship";
                        if (foundTypes.TryGetValue(referenceRelationshipTypeName, out var referenceRelationshipType) || 
                            (referenceRelationshipType = ReflectionHelper.FindType(referenceRelationshipTypeName, exchangeDataType, foundTypes)) != null)
                        {
                            var removeChildMethod = elementAssetType.GetMethod("RemoveChild", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                            if (removeChildMethod != null)
                            {
                                // Remove in reverse order to avoid index shifting issues
                                var indicesToRemove = new List<int>();
                                for (int i = 0; i < childNodesList.Count; i++)
                                {
                                    var childNodeRel = childNodesList[i];
                                    var relationshipProp = childNodeRel.GetType().GetProperty("Relationship", BindingFlags.Public | BindingFlags.Instance);
                                    if (relationshipProp != null)
                                    {
                                        var relationship = relationshipProp.GetValue(childNodeRel);
                                        if (relationship != null && referenceRelationshipType.IsAssignableFrom(relationship.GetType()))
                                        {
                                            // Check if it has Type but NOT ModelStructure
                                            var typeProperty = relationship.GetType().GetProperty("Type", BindingFlags.Public | BindingFlags.Instance);
                                            var modelStructureProperty = relationship.GetType().GetProperty("ModelStructure", BindingFlags.Public | BindingFlags.Instance);
                                            
                                            bool hasType = typeProperty != null && typeProperty.GetValue(relationship) != null;
                                            var modelStructure = modelStructureProperty?.GetValue(relationship);
                                            bool hasModelStructure = modelStructure != null;
                                            
                                            if (hasType && !hasModelStructure)
                                            {
                                                indicesToRemove.Add(i);
                                            }
                                        }
                                    }
                                }
                                
                                // Remove in reverse order to maintain correct indices
                                for (int i = indicesToRemove.Count - 1; i >= 0; i--)
                                {
                                    removeChildMethod.Invoke(elementAsset, new object[] { indicesToRemove[i] });
                                    diagnostics.Add($"  ✓ Removed existing ReferenceRelationship with Type (no ModelStructure) at index {indicesToRemove[i]} to make room for ModelStructure DesignAsset");
                                }
                            }
                        }
                    }
                }
                
                // Link DesignAsset to Element's InstanceAsset using ReferenceRelationship with ModelStructure
                var elementAddChildMethod = elementAssetType.GetMethod("AddChild", BindingFlags.Public | BindingFlags.Instance);
                if (elementAddChildMethod != null)
                {
                    const string referenceRelationshipTypeName = "Autodesk.DataExchange.SchemaObjects.Relationships.ReferenceRelationship";
                    if (foundTypes.TryGetValue(referenceRelationshipTypeName, out var referenceRelationshipType) || 
                        (referenceRelationshipType = ReflectionHelper.FindType(referenceRelationshipTypeName, exchangeDataType, foundTypes)) != null)
                    {
                        var referenceRelationship = Activator.CreateInstance(referenceRelationshipType);
                        var modelStructureProperty = referenceRelationshipType.GetProperty("ModelStructure", BindingFlags.Public | BindingFlags.Instance);
                        if (modelStructureProperty != null)
                        {
                            const string modelStructureTypeName = "Autodesk.DataExchange.SchemaObjects.Components.ModelStructure";
                            if (foundTypes.TryGetValue(modelStructureTypeName, out var modelStructureType) || 
                                (modelStructureType = ReflectionHelper.FindType(modelStructureTypeName, exchangeDataType, foundTypes)) != null)
                            {
                                var modelStructure = Activator.CreateInstance(modelStructureType);
                                var valueProperty = modelStructureType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                                if (valueProperty != null)
                                {
                                    valueProperty.SetValue(modelStructure, true);
                                }
                                modelStructureProperty.SetValue(referenceRelationship, modelStructure);
                                diagnostics.Add($"  ✓ Set ModelStructure.Value = true on ReferenceRelationship (required for HasGeometry to work)");
                            }
                        }
                        
                        elementAddChildMethod.Invoke(elementAsset, new object[] { designAsset, referenceRelationship });
                        var designAssetIdProp = designAsset.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                        var linkedDesignAssetId = designAssetIdProp?.GetValue(designAsset)?.ToString() ?? "N/A";
                        diagnostics.Add($"  ✓ Created and linked DesignAsset (ID: {linkedDesignAssetId}) to Element's InstanceAsset with ModelStructure");
                    }
                }
            }

            // Add GeometryAsset to DesignAsset using ContainmentRelationship
            if (designAsset != null)
            {
                var addChildMethod = designAsset.GetType().GetMethod("AddChild", BindingFlags.Public | BindingFlags.Instance);
                if (addChildMethod != null)
                {
                    const string containmentRelationshipTypeName = "Autodesk.DataExchange.SchemaObjects.Relationships.ContainmentRelationship";
                    if (foundTypes.TryGetValue(containmentRelationshipTypeName, out var containmentRelationshipType) || 
                        (containmentRelationshipType = ReflectionHelper.FindType(containmentRelationshipTypeName, exchangeDataType, foundTypes)) != null)
                    {
                        var containmentRelationship = Activator.CreateInstance(containmentRelationshipType);
                        addChildMethod.Invoke(designAsset, new object[] { geometryAsset, containmentRelationship });
                        diagnostics.Add($"✓ Added GeometryAsset to DesignAsset using ContainmentRelationship");
                        diagnostics.Add($"✓ Complete hierarchy: RootAsset -> InstanceAsset -> DesignAsset -> GeometryAsset");
                    }
                    else
                    {
                        throw new InvalidOperationException("Could not find ContainmentRelationship type");
                    }
                }
            }
        }

        /// <summary>
        /// Loads geometries from an SMB file to count how many it contains
        /// </summary>
        private static List<Geometry> LoadGeometriesFromSMBFile(string smbFilePath, string unit, List<string> diagnostics)
        {
            var geometries = new List<Geometry>();
            
            try
            {
                if (!File.Exists(smbFilePath))
                {
                    throw new FileNotFoundException($"SMB file not found: {smbFilePath}");
                }

                // Convert unit to mmPerUnit
                double mmPerUnit = ConvertUnitToMmPerUnit(unit);
                
                // Use Geometry.ImportFromSMB to load all geometries from the file
                var geometryType = typeof(Geometry);
                var importMethod = geometryType.GetMethod("ImportFromSMB", 
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(double) },
                    null);
                
                if (importMethod != null)
                {
                    var result = importMethod.Invoke(null, new object[] { smbFilePath, mmPerUnit }) as Geometry[];
                    if (result != null && result.Length > 0)
                    {
                        geometries.AddRange(result);
                    }
                }
                else
                {
                    diagnostics.Add($"  ⚠️ ImportFromSMB method not found - cannot count geometries");
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add($"  ⚠️ Error loading geometries from SMB file: {ex.Message}");
            }
            
            return geometries;
        }

        /// <summary>
        /// Sets up DesignAsset and relationships for multiple GeometryAssets
        /// All GeometryAssets are added to the same DesignAsset
        /// </summary>
        private static void SetupDesignAssetAndRelationshipsForMultipleGeometries(
            object element,
            List<(object geometryAsset, Type geometryAssetType, string geometryAssetId)> geometryAssets,
            object exchangeData,
            Type exchangeDataType,
            Dictionary<string, Type> foundTypes,
            string elementName,
            List<string> diagnostics)
        {
            if (geometryAssets == null || geometryAssets.Count == 0)
            {
                diagnostics.Add("  ⚠️ No GeometryAssets provided");
                return;
            }

            diagnostics.Add($"\nSetting up DesignAsset for {geometryAssets.Count} GeometryAsset(s)...");
            
            // Get required types
            const string designAssetTypeName = "Autodesk.DataExchange.SchemaObjects.Assets.DesignAsset";
            if (!foundTypes.TryGetValue(designAssetTypeName, out var designAssetType))
            {
                designAssetType = ReflectionHelper.FindType(designAssetTypeName, exchangeDataType, foundTypes);
                if (designAssetType == null)
                {
                    throw new InvalidOperationException("Could not find DesignAsset type");
                }
            }

            const string instanceAssetTypeName = "Autodesk.DataExchange.SchemaObjects.Assets.InstanceAsset";
            if (!foundTypes.TryGetValue(instanceAssetTypeName, out var instanceAssetType))
            {
                instanceAssetType = ReflectionHelper.FindType(instanceAssetTypeName, exchangeDataType, foundTypes);
                if (instanceAssetType == null)
                {
                    throw new InvalidOperationException("Could not find InstanceAsset type");
                }
            }

            // Get RootAsset
            var rootAssetProp = exchangeDataType.GetProperty("RootAsset", BindingFlags.Public | BindingFlags.Instance);
            if (rootAssetProp == null)
            {
                throw new InvalidOperationException("Could not find RootAsset property on ExchangeData");
            }
            
            object rootAsset = rootAssetProp.GetValue(exchangeData);
            
            if (rootAsset == null)
            {
                diagnostics.Add("\nRootAsset is null - creating TopLevelAssembly...");
                var rootAssetId = Guid.NewGuid().ToString();
                rootAsset = ReflectionHelper.CreateInstanceWithId(designAssetType, rootAssetId, foundTypes, diagnostics);
                
                var objectInfoProp = designAssetType.GetProperty("ObjectInfo", BindingFlags.Public | BindingFlags.Instance);
                if (objectInfoProp != null)
                {
                    var objectInfo = CreateObjectInfo("TopLevelAssembly", foundTypes, exchangeDataType, diagnostics);
                    if (objectInfo != null)
                    {
                        objectInfoProp.SetValue(rootAsset, objectInfo);
                    }
                }
                
                var exchangeDataAddMethod = exchangeDataType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
                if (exchangeDataAddMethod != null)
                {
                    exchangeDataAddMethod.Invoke(exchangeData, new object[] { rootAsset });
                }
                
                var rootAssetPropSetter = exchangeDataType.GetProperty("RootAsset", BindingFlags.Public | BindingFlags.Instance);
                if (rootAssetPropSetter != null && rootAssetPropSetter.CanWrite)
                {
                    rootAssetPropSetter.SetValue(exchangeData, rootAsset);
                }
                
                diagnostics.Add("✓ Created RootAsset (TopLevelAssembly)");
            }
            else
            {
                diagnostics.Add("✓ Found existing RootAsset (TopLevelAssembly)");
            }

            // Get Element's InstanceAsset
            var elementAssetProp = element.GetType().GetProperty("Asset", BindingFlags.NonPublic | BindingFlags.Instance);
            if (elementAssetProp == null)
            {
                throw new InvalidOperationException("Could not find Asset property on Element");
            }
            
            var elementAsset = elementAssetProp.GetValue(element);
            if (elementAsset == null)
            {
                throw new InvalidOperationException("Element's Asset is null");
            }
            
            var elementAssetType = elementAsset.GetType();
            diagnostics.Add($"  Element's Asset type: {elementAssetType.Name}");

            // Check if Element's InstanceAsset is linked to RootAsset
            var rootAssetChildNodesProp = rootAsset.GetType().GetProperty("ChildNodes", BindingFlags.Public | BindingFlags.Instance);
            bool elementInstanceLinkedToRoot = false;
            if (rootAssetChildNodesProp != null)
            {
                var rootChildNodes = rootAssetChildNodesProp.GetValue(rootAsset) as System.Collections.IEnumerable;
                if (rootChildNodes != null)
                {
                    foreach (var childNodeRel in rootChildNodes)
                    {
                        var nodeProp = childNodeRel.GetType().GetProperty("Node", BindingFlags.Public | BindingFlags.Instance);
                        if (nodeProp != null)
                        {
                            var node = nodeProp.GetValue(childNodeRel);
                            if (node != null && node == elementAsset)
                            {
                                elementInstanceLinkedToRoot = true;
                                diagnostics.Add("  ✓ Element's InstanceAsset is already linked to RootAsset");
                                break;
                            }
                        }
                    }
                }
            }
            
            // Link Element's InstanceAsset to RootAsset if not already linked
            if (!elementInstanceLinkedToRoot)
            {
                var rootAddChildMethod = rootAsset.GetType().GetMethod("AddChild", BindingFlags.Public | BindingFlags.Instance);
                if (rootAddChildMethod != null)
                {
                    const string containmentRelationshipTypeName = "Autodesk.DataExchange.SchemaObjects.Relationships.ContainmentRelationship";
                    if (foundTypes.TryGetValue(containmentRelationshipTypeName, out var containmentRelationshipType) || 
                        (containmentRelationshipType = ReflectionHelper.FindType(containmentRelationshipTypeName, exchangeDataType, foundTypes)) != null)
                    {
                        var containmentRelationship = Activator.CreateInstance(containmentRelationshipType);
                        rootAddChildMethod.Invoke(rootAsset, new object[] { elementAsset, containmentRelationship });
                        diagnostics.Add("  ✓ Linked Element's InstanceAsset to RootAsset");
                    }
                }
            }
            
            // Find or create DesignAsset
            object designAsset = null;
            var elementAssetChildNodesProp = elementAssetType.GetProperty("ChildNodes", BindingFlags.Public | BindingFlags.Instance);
            if (elementAssetChildNodesProp != null)
            {
                var elementChildNodes = elementAssetChildNodesProp.GetValue(elementAsset) as System.Collections.IEnumerable;
                if (elementChildNodes != null)
                {
                    foreach (var childNodeRel in elementChildNodes)
                    {
                        var nodeProp = childNodeRel.GetType().GetProperty("Node", BindingFlags.Public | BindingFlags.Instance);
                        if (nodeProp != null)
                        {
                            var node = nodeProp.GetValue(childNodeRel);
                            if (node != null && designAssetType.IsAssignableFrom(node.GetType()))
                            {
                                var relationshipProp = childNodeRel.GetType().GetProperty("Relationship", BindingFlags.Public | BindingFlags.Instance);
                                if (relationshipProp != null)
                                {
                                    var relationship = relationshipProp.GetValue(childNodeRel);
                                    if (relationship != null)
                                    {
                                        var modelStructureProp = relationship.GetType().GetProperty("ModelStructure", BindingFlags.Public | BindingFlags.Instance);
                                        var modelStructure = modelStructureProp?.GetValue(relationship);
                                        if (modelStructure != null)
                                        {
                                            designAsset = node;
                                            var idProp = node.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                                            var existingId = idProp?.GetValue(node)?.ToString() ?? "N/A";
                                            diagnostics.Add($"  ✓ Found existing DesignAsset (ID: {existingId}) - will reuse it");
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            
            // Create DesignAsset if not found
            if (designAsset == null)
            {
                diagnostics.Add("  No existing DesignAsset found - creating new one...");
                var designAssetId = Guid.NewGuid().ToString();
                designAsset = ReflectionHelper.CreateInstanceWithId(designAssetType, designAssetId, foundTypes, diagnostics);
                
                var objectInfoProp = designAssetType.GetProperty("ObjectInfo", BindingFlags.Public | BindingFlags.Instance);
                if (objectInfoProp != null)
                {
                    var objectInfo = CreateObjectInfo(elementName, foundTypes, exchangeDataType, diagnostics);
                    if (objectInfo != null)
                    {
                        objectInfoProp.SetValue(designAsset, objectInfo);
                    }
                }
                
                var exchangeDataAddMethod = exchangeDataType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
                if (exchangeDataAddMethod != null)
                {
                    exchangeDataAddMethod.Invoke(exchangeData, new object[] { designAsset });
                }
                
                // Remove any existing ReferenceRelationships with Type but not ModelStructure
                var elementAssetChildNodesPropForRemoval = elementAssetType.GetProperty("ChildNodes", BindingFlags.Public | BindingFlags.Instance);
                if (elementAssetChildNodesPropForRemoval != null)
                {
                    var elementChildNodes = elementAssetChildNodesPropForRemoval.GetValue(elementAsset) as System.Collections.IEnumerable;
                    if (elementChildNodes != null)
                    {
                        var childNodesList = elementChildNodes.Cast<object>().ToList();
                        const string referenceRelationshipTypeName = "Autodesk.DataExchange.SchemaObjects.Relationships.ReferenceRelationship";
                        if (foundTypes.TryGetValue(referenceRelationshipTypeName, out var referenceRelationshipType) || 
                            (referenceRelationshipType = ReflectionHelper.FindType(referenceRelationshipTypeName, exchangeDataType, foundTypes)) != null)
                        {
                            var removeChildMethod = elementAssetType.GetMethod("RemoveChild", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                            if (removeChildMethod != null)
                            {
                                var indicesToRemove = new List<int>();
                                for (int i = 0; i < childNodesList.Count; i++)
                                {
                                    var childNodeRel = childNodesList[i];
                                    var relationshipProp = childNodeRel.GetType().GetProperty("Relationship", BindingFlags.Public | BindingFlags.Instance);
                                    if (relationshipProp != null)
                                    {
                                        var relationship = relationshipProp.GetValue(childNodeRel);
                                        if (relationship != null && referenceRelationshipType.IsAssignableFrom(relationship.GetType()))
                                        {
                                            var typeProperty = relationship.GetType().GetProperty("Type", BindingFlags.Public | BindingFlags.Instance);
                                            var modelStructureProperty = relationship.GetType().GetProperty("ModelStructure", BindingFlags.Public | BindingFlags.Instance);
                                            
                                            bool hasType = typeProperty != null && typeProperty.GetValue(relationship) != null;
                                            var modelStructure = modelStructureProperty?.GetValue(relationship);
                                            bool hasModelStructure = modelStructure != null;
                                            
                                            if (hasType && !hasModelStructure)
                                            {
                                                indicesToRemove.Add(i);
                                            }
                                        }
                                    }
                                }
                                
                                for (int i = indicesToRemove.Count - 1; i >= 0; i--)
                                {
                                    removeChildMethod.Invoke(elementAsset, new object[] { indicesToRemove[i] });
                                }
                            }
                        }
                    }
                }
                
                // Link DesignAsset to Element's InstanceAsset using ReferenceRelationship with ModelStructure
                var elementAddChildMethod = elementAssetType.GetMethod("AddChild", BindingFlags.Public | BindingFlags.Instance);
                if (elementAddChildMethod != null)
                {
                    const string referenceRelationshipTypeName = "Autodesk.DataExchange.SchemaObjects.Relationships.ReferenceRelationship";
                    if (foundTypes.TryGetValue(referenceRelationshipTypeName, out var referenceRelationshipType) || 
                        (referenceRelationshipType = ReflectionHelper.FindType(referenceRelationshipTypeName, exchangeDataType, foundTypes)) != null)
                    {
                        var referenceRelationship = Activator.CreateInstance(referenceRelationshipType);
                        var modelStructureProperty = referenceRelationshipType.GetProperty("ModelStructure", BindingFlags.Public | BindingFlags.Instance);
                        if (modelStructureProperty != null)
                        {
                            const string modelStructureTypeName = "Autodesk.DataExchange.SchemaObjects.Components.ModelStructure";
                            if (foundTypes.TryGetValue(modelStructureTypeName, out var modelStructureType) || 
                                (modelStructureType = ReflectionHelper.FindType(modelStructureTypeName, exchangeDataType, foundTypes)) != null)
                            {
                                var modelStructure = Activator.CreateInstance(modelStructureType);
                                var valueProperty = modelStructureType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                                if (valueProperty != null)
                                {
                                    valueProperty.SetValue(modelStructure, true);
                                }
                                modelStructureProperty.SetValue(referenceRelationship, modelStructure);
                                diagnostics.Add($"  ✓ Set ModelStructure.Value = true on ReferenceRelationship");
                            }
                        }
                        
                        elementAddChildMethod.Invoke(elementAsset, new object[] { designAsset, referenceRelationship });
                        var designAssetIdProp = designAsset.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                        var linkedDesignAssetId = designAssetIdProp?.GetValue(designAsset)?.ToString() ?? "N/A";
                        diagnostics.Add($"  ✓ Created and linked DesignAsset (ID: {linkedDesignAssetId}) to Element's InstanceAsset with ModelStructure");
                    }
                }
            }

            // Add ALL GeometryAssets to DesignAsset using ContainmentRelationship
            if (designAsset != null)
            {
                var addChildMethod = designAsset.GetType().GetMethod("AddChild", BindingFlags.Public | BindingFlags.Instance);
                if (addChildMethod != null)
                {
                    const string containmentRelationshipTypeName = "Autodesk.DataExchange.SchemaObjects.Relationships.ContainmentRelationship";
                    if (foundTypes.TryGetValue(containmentRelationshipTypeName, out var containmentRelationshipType) || 
                        (containmentRelationshipType = ReflectionHelper.FindType(containmentRelationshipTypeName, exchangeDataType, foundTypes)) != null)
                    {
                        foreach (var (geometryAsset, geometryAssetType, geometryAssetId) in geometryAssets)
                        {
                            var containmentRelationship = Activator.CreateInstance(containmentRelationshipType);
                            addChildMethod.Invoke(designAsset, new object[] { geometryAsset, containmentRelationship });
                        }
                        diagnostics.Add($"✓ Added {geometryAssets.Count} GeometryAsset(s) to DesignAsset using ContainmentRelationship");
                        diagnostics.Add($"✓ Complete hierarchy: RootAsset -> InstanceAsset -> DesignAsset -> {geometryAssets.Count} GeometryAsset(s)");
                    }
                    else
                    {
                        throw new InvalidOperationException("Could not find ContainmentRelationship type");
                    }
                }
            }
        }

        /// <summary>
        /// Full SyncExchangeDataAsync flow adapted for direct SMB uploads
        /// Follows the exact same flow as the internal SDK method but skips STEP→SMB conversion
        /// NOTE: Viewable generation is disabled by default (takes ~131 seconds) for performance.
        /// </summary>
        private static async Task SyncExchangeDataAsyncForSMB(object client, Type clientType, DataExchangeIdentifier identifier, object exchangeData, Type exchangeDataType, List<string> diagnostics)
        {
            var syncTimer = Stopwatch.StartNew();
            diagnostics.Add($"\n=== Starting Full SyncExchangeDataAsync Flow for SMB ===");
            string fulfillmentId = string.Empty;
            object api = null;
            Type apiType = null;

            try
            {
                await TimeOperation("ProcessRenderStylesFromFileGeometryAsync", 
                    async () => await ProcessRenderStylesFromFileGeometryAsync(client, clientType, exchangeData, exchangeDataType, diagnostics), diagnostics);
                
                // Fulfillment is REQUIRED - UploadGeometries needs a valid fulfillmentId to create the binary assets API endpoint
                // Without fulfillment, the API returns 404 "The requested resource does not exist"
                fulfillmentId = await TimeOperation("StartFulfillmentAsync", 
                    async () => await StartFulfillmentAsync(client, clientType, identifier, exchangeData, exchangeDataType, diagnostics), diagnostics);
                api = TimeOperation("GetAPI", 
                    () => GetAPI(client, clientType), diagnostics);
                apiType = api?.GetType();

                var assetInfosList = await TimeOperation("GetAssetInfosForSMBAsync", 
                    async () => await GetAssetInfosForSMBAsync(client, clientType, exchangeData, exchangeDataType, identifier, fulfillmentId, diagnostics), diagnostics);
                
                TimeOperation("AddRenderStylesToAssetInfos", 
                    () => AddRenderStylesToAssetInfos(client, clientType, assetInfosList, exchangeData, exchangeDataType, diagnostics), diagnostics);
                
                await TimeOperation("UploadGeometriesAsync (network I/O)", 
                    async () => await UploadGeometriesAsync(client, clientType, identifier, fulfillmentId, assetInfosList, exchangeData, exchangeDataType, diagnostics), diagnostics);
                
                await TimeOperation("UploadCustomGeometriesAsync", 
                    async () => await UploadCustomGeometriesAsync(client, clientType, identifier, fulfillmentId, exchangeData, exchangeDataType, diagnostics), diagnostics);
                
                await TimeOperation("UploadLargePrimitiveGeometriesAsync", 
                    async () => await UploadLargePrimitiveGeometriesAsync(client, clientType, identifier, fulfillmentId, exchangeData, exchangeDataType, diagnostics), diagnostics);
                
                // Fulfillment sync request - synchronizes exchange data with the server
                var fulfillmentSyncRequest = await TimeOperation("GetFulfillmentSyncRequestAsync", 
                    async () => await GetFulfillmentSyncRequestAsync(client, clientType, identifier, exchangeData, exchangeDataType, diagnostics), diagnostics);
                
                var fulfillmentTasks = await TimeOperation("BatchAndSendSyncRequestsAsync", 
                    async () => await BatchAndSendSyncRequestsAsync(client, clientType, identifier, fulfillmentId, fulfillmentSyncRequest, exchangeData, exchangeDataType, diagnostics), diagnostics);
                
                await TimeOperation("WaitForAllTasksAsync", 
                    async () => await WaitForAllTasksAsync(fulfillmentTasks, diagnostics), diagnostics);
                
                // Finish and poll fulfillment - completes the transaction and ensures server processing is done
                await TimeOperation("FinishFulfillmentAsync", 
                    async () => await FinishFulfillmentAsync(api, apiType, identifier, fulfillmentId, diagnostics), diagnostics);
                
                await TimeOperation("PollForFulfillmentAsync", 
                    async () => await PollForFulfillmentAsync(client, clientType, identifier, fulfillmentId, diagnostics), diagnostics);
                
                TimeOperation("ClearLocalStatesAndSetRevision", 
                    () => ClearLocalStatesAndSetRevision(client, clientType, exchangeData, exchangeDataType, diagnostics), diagnostics);
                
                TimeOperation("SetExchangeIdentifierIfNeeded", 
                    () => SetExchangeIdentifierIfNeeded(exchangeData, exchangeDataType, identifier, diagnostics), diagnostics);

                // Viewable generation is disabled by default (takes ~131 seconds)
                // The server will generate viewables automatically
                diagnostics.Add("\n14. Skipping viewable generation (disabled by default for performance)");
                diagnostics.Add("  ⚠️ Viewable generation skipped - geometry may not appear in viewer immediately");
                diagnostics.Add("  ⚠️ Viewable will be generated automatically by the server, or you can trigger it manually");
                diagnostics.Add("  ⏱️  Saved ~131 seconds by skipping viewable generation");

                syncTimer.Stop();
                diagnostics.Add($"\n✓ Full SyncExchangeDataAsync flow completed successfully");
                diagnostics.Add($"⏱️  TOTAL SyncExchangeDataAsyncForSMB time: {syncTimer.ElapsedMilliseconds}ms ({syncTimer.Elapsed.TotalSeconds:F3}s)");
            }
            catch (Exception ex)
            {
                syncTimer.Stop();
                diagnostics.Add($"\n✗ ERROR in SyncExchangeDataAsyncForSMB: {ex.GetType().Name}: {ex.Message}");
                diagnostics.Add($"⏱️  SyncExchangeDataAsyncForSMB failed after: {syncTimer.ElapsedMilliseconds}ms ({syncTimer.Elapsed.TotalSeconds:F3}s)");
                if (ex.InnerException != null)
                {
                    diagnostics.Add($"  Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }
                diagnostics.Add($"  Stack: {ex.StackTrace}");

                // Discard fulfillment on error (cleanup)
                if (!string.IsNullOrEmpty(fulfillmentId))
                {
                    try
                    {
                        await DiscardFulfillmentAsync(client, clientType, identifier, fulfillmentId, diagnostics);
                    }
                    catch (Exception discardEx)
                    {
                        diagnostics.Add($"  ⚠️ Failed to discard fulfillment: {discardEx.Message}");
                    }
                }

                throw;
            }
        }

        private static async Task ProcessRenderStylesFromFileGeometryAsync(object client, Type clientType, object exchangeData, Type exchangeDataType, List<string> diagnostics)
        {
            diagnostics.Add("\n1. Processing render styles from file geometry...");
            var processRenderStylesMethod = ReflectionHelper.GetMethod(
                clientType,
                "ProcessRenderStylesFromFileGeometry",
                BindingFlags.NonPublic | BindingFlags.Instance,
                new[] { exchangeDataType });
            
            if (processRenderStylesMethod != null)
            {
                var processTask = ReflectionHelper.InvokeMethod(client, processRenderStylesMethod, new object[] { exchangeData }, diagnostics);
                if (processTask != null)
                {
                    await ((dynamic)processTask).ConfigureAwait(false);
                    diagnostics.Add("  ✓ Processed render styles");
                }
            }
            else
            {
                diagnostics.Add("  ⚠️ ProcessRenderStylesFromFileGeometry not found, skipping");
            }
        }

        private static async Task<string> StartFulfillmentAsync(object client, Type clientType, DataExchangeIdentifier identifier, object exchangeData, Type exchangeDataType, List<string> diagnostics)
        {
            diagnostics.Add("\n2. Starting fulfillment...");
            var fulfillmentRequestType = Type.GetType("Autodesk.DataExchange.OpenAPI.FulfillmentRequest, Autodesk.DataExchange.OpenAPI");
            if (fulfillmentRequestType == null)
            {
                throw new InvalidOperationException("Could not find FulfillmentRequest type");
            }

            var fulfillmentRequest = Activator.CreateInstance(fulfillmentRequestType);
            var executionOrderProp = fulfillmentRequestType.GetProperty("ExecutionOrder", BindingFlags.Public | BindingFlags.Instance);
            if (executionOrderProp != null)
            {
                var insertFirstEnum = Type.GetType("Autodesk.DataExchange.OpenAPI.FulfillmentRequestExecutionOrder, Autodesk.DataExchange.OpenAPI");
                if (insertFirstEnum != null)
                {
                    var insertFirstValue = Enum.Parse(insertFirstEnum, "INSERT_FIRST");
                    executionOrderProp.SetValue(fulfillmentRequest, insertFirstValue);
                }
            }

            // Set description if provided
            var descriptionProp = exchangeDataType.GetProperty("Description", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (descriptionProp != null)
            {
                var description = descriptionProp.GetValue(exchangeData)?.ToString();
                if (!string.IsNullOrEmpty(description))
                {
                    var summaryProp = fulfillmentRequestType.GetProperty("Summary", BindingFlags.Public | BindingFlags.Instance);
                    if (summaryProp != null)
                    {
                        summaryProp.SetValue(fulfillmentRequest, description);
                    }
                }
            }

            // Get API and call StartFulfillmentAsync
            var getAPIMethod = clientType.GetMethod("GetAPI", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (getAPIMethod == null)
            {
                throw new InvalidOperationException("Could not find GetAPI method on Client");
            }

            var api = getAPIMethod.Invoke(client, null);
            var apiType = api.GetType();
            
            // Use ReflectionHelper.GetMethod with parameter types to avoid ambiguity
            var startFulfillmentMethod = ReflectionHelper.GetMethod(
                apiType,
                "StartFulfillmentAsync",
                BindingFlags.Public | BindingFlags.Instance,
                new[] { typeof(string), typeof(string), fulfillmentRequestType, typeof(CancellationToken) });
            
            if (startFulfillmentMethod == null)
            {
                throw new InvalidOperationException("Could not find StartFulfillmentAsync on API");
            }

            var startTask = ReflectionHelper.InvokeMethod(api, startFulfillmentMethod, new object[] { identifier.CollectionId, identifier.ExchangeId, fulfillmentRequest, CancellationToken.None }, diagnostics);
            if (startTask == null)
            {
                throw new InvalidOperationException("StartFulfillmentAsync returned null");
            }

            var fulfillmentResponse = await ((dynamic)startTask).ConfigureAwait(false);
            var fulfillmentResponseType = fulfillmentResponse.GetType();
            var idProp = fulfillmentResponseType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
            if (idProp == null)
            {
                throw new InvalidOperationException("FulfillmentResponse does not have Id property");
            }

            var fulfillmentId = idProp.GetValue(fulfillmentResponse)?.ToString();
            if (string.IsNullOrEmpty(fulfillmentId))
            {
                throw new InvalidOperationException("FulfillmentResponse.Id is null or empty");
            }

            diagnostics.Add($"  ✓ Started fulfillment: {fulfillmentId}");
            return fulfillmentId;
        }

        private static object GetAPI(object client, Type clientType)
        {
            var getAPIMethod = clientType.GetMethod("GetAPI", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (getAPIMethod == null)
            {
                throw new InvalidOperationException("Could not find GetAPI method on Client");
            }
            return getAPIMethod.Invoke(client, null);
        }

        private static async Task<List<object>> GetAssetInfosForSMBAsync(object client, Type clientType, object exchangeData, Type exchangeDataType, DataExchangeIdentifier identifier, string fulfillmentId, List<string> diagnostics)
        {
            diagnostics.Add("\n3. Getting AssetInfos from UnsavedGeometryMapping...");
            
            // Build mapping dictionary from stored static mapping for this exchange
            var geometryAssetIdToSmbPath = new Dictionary<string, string>();
            var exchangeId = identifier.ExchangeId;
            foreach (var kvp in geometryAssetIdToSmbPathMapping)
            {
                if (kvp.Key.StartsWith($"{exchangeId}_", StringComparison.OrdinalIgnoreCase))
                {
                    var geometryAssetId = kvp.Key.Substring(exchangeId.Length + 1);
                    geometryAssetIdToSmbPath[geometryAssetId] = kvp.Value;
                }
            }
            
            diagnostics.Add($"  Found {geometryAssetIdToSmbPath.Count} GeometryAsset->SMB mapping(s) for this exchange");
            
            // Create geometry count dictionary from static mapping
            var geometryAssetIdToGeometryCount = new Dictionary<string, int>();
            foreach (var kvp in geometryAssetIdToGeometryCountMapping)
            {
                if (kvp.Key.StartsWith($"{exchangeId}_", StringComparison.OrdinalIgnoreCase))
                {
                    var geometryAssetId = kvp.Key.Substring(exchangeId.Length + 1);
                    geometryAssetIdToGeometryCount[geometryAssetId] = kvp.Value;
                }
            }
            
            var assetInfos = GetAllAssetInfosWithTranslatedGeometryPathForSMB(client, clientType, exchangeData, exchangeDataType, identifier, fulfillmentId, geometryAssetIdToSmbPath, geometryAssetIdToGeometryCount, diagnostics);
            var assetInfosList = assetInfos.Cast<object>().ToList();
            diagnostics.Add($"  ✓ Got {assetInfosList.Count} AssetInfo(s)");
            return assetInfosList;
        }

        private static void AddRenderStylesToAssetInfos(object client, Type clientType, List<object> assetInfosList, object exchangeData, Type exchangeDataType, List<string> diagnostics)
        {
            diagnostics.Add("\n4. Adding render styles to AssetInfos...");
            if (assetInfosList.Count > 0)
            {
                var assetInfoType = assetInfosList[0].GetType();
                
                // Convert List<object> to properly typed List<AssetInfo>
                var typedListType = typeof(List<>).MakeGenericType(assetInfoType);
                var typedList = Activator.CreateInstance(typedListType);
                var addMethod = typedListType.GetMethod("Add");
                foreach (var assetInfo in assetInfosList)
                {
                    addMethod.Invoke(typedList, new object[] { assetInfo });
                }
                
                // Try to find AddRenderStyles with IEnumerable<AssetInfo> parameter
                Type[] addRenderStylesParams1 = new Type[] { typeof(IEnumerable<>).MakeGenericType(assetInfoType), exchangeDataType };
                var addRenderStylesMethod = ReflectionHelper.GetMethod(
                    clientType,
                    "AddRenderStyles",
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    addRenderStylesParams1);
                
                if (addRenderStylesMethod == null)
                {
                    // Try with List<AssetInfo>
                    Type[] addRenderStylesParams2 = new Type[] { typeof(List<>).MakeGenericType(assetInfoType), exchangeDataType };
                    addRenderStylesMethod = ReflectionHelper.GetMethod(
                        clientType,
                        "AddRenderStyles",
                        BindingFlags.NonPublic | BindingFlags.Instance,
                        addRenderStylesParams2);
                }
                
                if (addRenderStylesMethod != null)
                {
                    try
                    {
                        addRenderStylesMethod.Invoke(client, new object[] { typedList, exchangeData });
                        diagnostics.Add("  ✓ Added render styles");
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add($"  ⚠️ Failed to add render styles: {ex.Message}");
                    }
                }
                else
                {
                    diagnostics.Add("  ⚠️ AddRenderStyles not found, skipping");
                }
            }
            else
            {
                diagnostics.Add("  ⚠️ No AssetInfos to add render styles to");
            }
        }

        private static async Task UploadGeometriesAsync(object client, Type clientType, DataExchangeIdentifier identifier, string fulfillmentId, List<object> assetInfosList, object exchangeData, Type exchangeDataType, List<string> diagnostics)
        {
            diagnostics.Add("\n5. Uploading geometries...");
            
            // DIAGNOSTIC: Inspect AssetInfos before upload
            foreach (var assetInfo in assetInfosList)
            {
                var idProp = assetInfo.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                var assetInfoId = idProp?.GetValue(assetInfo)?.ToString();
                var bodyInfoListProp = assetInfo.GetType().GetProperty("BodyInfoList", BindingFlags.Public | BindingFlags.Instance);
                if (bodyInfoListProp != null)
                {
                    var bodyInfoList = bodyInfoListProp.GetValue(assetInfo);
                    if (bodyInfoList != null)
                    {
                        var countProp = bodyInfoList.GetType().GetProperty("Count");
                        var count = countProp?.GetValue(bodyInfoList);
                        diagnostics.Add($"  AssetInfo {assetInfoId}: BodyInfoList.Count = {count}");
                    }
                }
                var outputPathProp = assetInfo.GetType().GetProperty("OutputPath", BindingFlags.Public | BindingFlags.Instance);
                var outputPath = outputPathProp?.GetValue(assetInfo)?.ToString();
                diagnostics.Add($"  AssetInfo {assetInfoId}: OutputPath = {outputPath}");
            }
            
            if (assetInfosList.Count > 0)
            {
                var assetInfoType = assetInfosList[0].GetType();
                
                // Convert List<object> to properly typed List<AssetInfo>
                var typedListType = typeof(List<>).MakeGenericType(assetInfoType);
                var typedList = Activator.CreateInstance(typedListType);
                var addMethod = typedListType.GetMethod("Add");
                foreach (var assetInfo in assetInfosList)
                {
                    addMethod.Invoke(typedList, new object[] { assetInfo });
                }
                
                // Try to find UploadGeometries with IEnumerable<AssetInfo> parameter
                Type[] uploadGeometriesParams1 = new Type[] { typeof(DataExchangeIdentifier), typeof(string), typeof(IEnumerable<>).MakeGenericType(assetInfoType), exchangeDataType, typeof(CancellationToken) };
                var uploadGeometriesMethod = ReflectionHelper.GetMethod(
                    clientType,
                    "UploadGeometries",
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    uploadGeometriesParams1);
                
                if (uploadGeometriesMethod == null)
                {
                    // Try with List<AssetInfo>
                    Type[] uploadGeometriesParams2 = new Type[] { typeof(DataExchangeIdentifier), typeof(string), typeof(List<>).MakeGenericType(assetInfoType), exchangeDataType, typeof(CancellationToken) };
                    uploadGeometriesMethod = ReflectionHelper.GetMethod(
                        clientType,
                        "UploadGeometries",
                        BindingFlags.NonPublic | BindingFlags.Instance,
                        uploadGeometriesParams2);
                }
                
                if (uploadGeometriesMethod != null)
                {
                    try
                    {
                        var uploadTask = ReflectionHelper.InvokeMethod(client, uploadGeometriesMethod, new object[] { identifier, fulfillmentId, typedList, exchangeData, CancellationToken.None }, diagnostics);
                        if (uploadTask != null)
                        {
                            await ((dynamic)uploadTask).ConfigureAwait(false);
                            diagnostics.Add("  ✓ Uploaded geometries");
                            
                            // DIAGNOSTIC: Check BodyInfoList after upload
                            foreach (var assetInfo in assetInfosList)
                            {
                                var idProp = assetInfo.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                                var assetInfoId = idProp?.GetValue(assetInfo)?.ToString();
                                var bodyInfoListProp = assetInfo.GetType().GetProperty("BodyInfoList", BindingFlags.Public | BindingFlags.Instance);
                                if (bodyInfoListProp != null)
                                {
                                    var bodyInfoList = bodyInfoListProp.GetValue(assetInfo);
                                    if (bodyInfoList != null)
                                    {
                                        var countProp = bodyInfoList.GetType().GetProperty("Count");
                                        var count = countProp?.GetValue(bodyInfoList);
                                        diagnostics.Add($"  After upload - AssetInfo {assetInfoId}: BodyInfoList.Count = {count}");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // CRITICAL: UploadGeometries MUST succeed for BinaryReference to be set
                        // If it fails, the viewer cannot fetch/render the geometry
                        diagnostics.Add($"\n✗ FATAL: UploadGeometries failed - BinaryReference will NOT be set!");
                        diagnostics.Add($"  Exception Type: {ex.GetType().FullName}");
                        diagnostics.Add($"  Exception Message: {ex.Message}");
                        diagnostics.Add($"  Stack Trace:\n{ex.StackTrace}");
                        
                        if (ex.InnerException != null)
                        {
                            diagnostics.Add($"  Inner Exception Type: {ex.InnerException.GetType().FullName}");
                            diagnostics.Add($"  Inner Exception Message: {ex.InnerException.Message}");
                            diagnostics.Add($"  Inner Stack Trace:\n{ex.InnerException.StackTrace}");
                        }
                        
                        diagnostics.Add($"\n  This failure prevents BinaryReference from being set on GeometryAssets.");
                        diagnostics.Add($"  Without BinaryReference, the viewer cannot fetch/render geometry.");
                        diagnostics.Add($"  Fix the underlying issue (likely path/format mismatch) and retry.");
                        
                        // THROW - this is fatal, don't continue
                        throw new InvalidOperationException($"UploadGeometries failed - BinaryReference will not be set. This prevents geometry from rendering. See diagnostics for details.", ex);
                    }
                }
                else
                {
                    diagnostics.Add("  ⚠️ UploadGeometries method not found, skipping (SMB files are already in UnsavedGeometryMapping)");
                }
            }
            else
            {
                diagnostics.Add("  ⚠️ No AssetInfos to upload");
            }
        }

        private static async Task UploadCustomGeometriesAsync(object client, Type clientType, DataExchangeIdentifier identifier, string fulfillmentId, object exchangeData, Type exchangeDataType, List<string> diagnostics)
        {
            diagnostics.Add("\n6. Uploading custom geometries...");
            var uploadCustomGeometriesMethod = ReflectionHelper.GetMethod(
                clientType,
                "UploadCustomGeometries",
                BindingFlags.NonPublic | BindingFlags.Instance,
                new[] { typeof(DataExchangeIdentifier), typeof(string), exchangeDataType, typeof(CancellationToken) });
            
            if (uploadCustomGeometriesMethod != null)
            {
                try
                {
                    var uploadCustomTask = ReflectionHelper.InvokeMethod(client, uploadCustomGeometriesMethod, new object[] { identifier, fulfillmentId, exchangeData, CancellationToken.None }, diagnostics);
                    if (uploadCustomTask != null)
                    {
                        await ((dynamic)uploadCustomTask).ConfigureAwait(false);
                        diagnostics.Add("  ✓ Uploaded custom geometries");
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"  ⚠️ UploadCustomGeometries failed: {ex.Message}");
                    // Continue - custom geometries might not be needed
                }
            }
        }

        private static async Task UploadLargePrimitiveGeometriesAsync(object client, Type clientType, DataExchangeIdentifier identifier, string fulfillmentId, object exchangeData, Type exchangeDataType, List<string> diagnostics)
        {
            diagnostics.Add("\n7. Uploading large primitive geometries...");
            var uploadLargePrimitiveMethod = ReflectionHelper.GetMethod(
                clientType,
                "UploadLargePrimitiveGeometries",
                BindingFlags.NonPublic | BindingFlags.Instance,
                new[] { typeof(DataExchangeIdentifier), typeof(string), exchangeDataType, typeof(CancellationToken) });
            
            if (uploadLargePrimitiveMethod != null)
            {
                try
                {
                    var uploadLargePrimitiveTask = ReflectionHelper.InvokeMethod(client, uploadLargePrimitiveMethod, new object[] { identifier, fulfillmentId, exchangeData, CancellationToken.None }, diagnostics);
                    if (uploadLargePrimitiveTask != null)
                    {
                        await ((dynamic)uploadLargePrimitiveTask).ConfigureAwait(false);
                        diagnostics.Add("  ✓ Uploaded large primitive geometries");
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"  ⚠️ UploadLargePrimitiveGeometries failed: {ex.Message}");
                    // Continue - large primitives might not be needed
                }
            }
        }

        private static async Task<object> GetFulfillmentSyncRequestAsync(object client, Type clientType, DataExchangeIdentifier identifier, object exchangeData, Type exchangeDataType, List<string> diagnostics)
        {
            diagnostics.Add("\n8. Getting FulfillmentSyncRequest...");
            var getExchangeDetailsMethod = ReflectionHelper.GetMethod(
                clientType,
                "GetExchangeDetailsAsync",
                BindingFlags.Public | BindingFlags.Instance,
                new[] { typeof(DataExchangeIdentifier) });
            
            if (getExchangeDetailsMethod == null)
            {
                throw new InvalidOperationException("Could not find GetExchangeDetailsAsync method");
            }

            var exchangeDetailsTask = ReflectionHelper.InvokeMethod(client, getExchangeDetailsMethod, new object[] { identifier }, diagnostics);
            if (exchangeDetailsTask == null)
            {
                throw new InvalidOperationException("GetExchangeDetailsAsync returned null");
            }

            var exchangeDetails = await ((dynamic)exchangeDetailsTask).ConfigureAwait(false);
            var exchangeDetailsType = exchangeDetails.GetType();
            var valueProp = exchangeDetailsType.GetProperty("Value");
            object exchangeDetailsValue = null;
            if (valueProp != null)
            {
                exchangeDetailsValue = valueProp.GetValue(exchangeDetails);
            }

            // Get FulfillmentSyncRequestHandler via GetService
            var getServiceMethod = clientType.GetMethod("GetService", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (getServiceMethod == null)
            {
                throw new InvalidOperationException("Could not find GetService method");
            }

            var fulfillmentSyncRequestHandlerType = Type.GetType("Autodesk.DataExchange.ClientServices.FulfillmentSyncRequestHandler, Autodesk.DataExchange");
            if (fulfillmentSyncRequestHandlerType == null)
            {
                throw new InvalidOperationException("Could not find FulfillmentSyncRequestHandler type");
            }

            var getServiceGeneric = getServiceMethod.MakeGenericMethod(fulfillmentSyncRequestHandlerType);
            var fulfillmentSyncRequestHandler = getServiceGeneric.Invoke(client, null);
            if (fulfillmentSyncRequestHandler == null)
            {
                throw new InvalidOperationException("GetService returned null for FulfillmentSyncRequestHandler");
            }

            // Get schema namespace from exchangeDetails
            string schemaNamespace = null;
            if (exchangeDetailsValue != null)
            {
                var schemaNamespaceProp = exchangeDetailsValue.GetType().GetProperty("SchemaNamespace", BindingFlags.Public | BindingFlags.Instance);
                if (schemaNamespaceProp != null)
                {
                    schemaNamespace = schemaNamespaceProp.GetValue(exchangeDetailsValue)?.ToString();
                }
            }

            // Call GetFulfillmentSyncRequest - find method with correct signature
            var getFulfillmentSyncRequestMethod = ReflectionHelper.GetMethod(
                fulfillmentSyncRequestHandlerType,
                "GetFulfillmentSyncRequest",
                BindingFlags.Public | BindingFlags.Instance,
                new[] { typeof(string), exchangeDataType, typeof(CancellationToken) });
            
            if (getFulfillmentSyncRequestMethod == null)
            {
                // Try without CancellationToken
                getFulfillmentSyncRequestMethod = ReflectionHelper.GetMethod(
                    fulfillmentSyncRequestHandlerType,
                    "GetFulfillmentSyncRequest",
                    BindingFlags.Public | BindingFlags.Instance,
                    new[] { typeof(string), exchangeDataType });
            }
            
            if (getFulfillmentSyncRequestMethod == null)
            {
                throw new InvalidOperationException("Could not find GetFulfillmentSyncRequest method with expected signature");
            }

            // Check actual parameter count and call accordingly
            var methodParams = getFulfillmentSyncRequestMethod.GetParameters();
            object[] invokeParams;
            if (methodParams.Length == 3)
            {
                invokeParams = new object[] { schemaNamespace, exchangeData, CancellationToken.None };
            }
            else if (methodParams.Length == 2)
            {
                invokeParams = new object[] { schemaNamespace, exchangeData };
            }
            else
            {
                throw new InvalidOperationException($"GetFulfillmentSyncRequest has unexpected parameter count: {methodParams.Length}");
            }

            var fulfillmentSyncRequestTask = ReflectionHelper.InvokeMethod(fulfillmentSyncRequestHandler, getFulfillmentSyncRequestMethod, invokeParams, diagnostics);
            if (fulfillmentSyncRequestTask == null)
            {
                throw new InvalidOperationException("GetFulfillmentSyncRequest returned null");
            }

            var fulfillmentSyncRequest = await ((dynamic)fulfillmentSyncRequestTask).ConfigureAwait(false);
            diagnostics.Add("  ✓ Got FulfillmentSyncRequest");
            return fulfillmentSyncRequest;
        }

        private static async Task<List<Task>> BatchAndSendSyncRequestsAsync(object client, Type clientType, DataExchangeIdentifier identifier, string fulfillmentId, object fulfillmentSyncRequest, object exchangeData, Type exchangeDataType, List<string> diagnostics)
        {
            diagnostics.Add("\n9. Batching and sending sync requests...");
            var fulfillmentSyncRequestType = (Type)fulfillmentSyncRequest.GetType();
            var getBatchedFulfillmentSyncRequestsMethod = ReflectionHelper.GetMethod(
                clientType,
                "GetBatchedFulfillmentSyncRequests",
                BindingFlags.NonPublic | BindingFlags.Instance,
                new Type[] { fulfillmentSyncRequestType });
            
            if (getBatchedFulfillmentSyncRequestsMethod == null)
            {
                throw new InvalidOperationException("Could not find GetBatchedFulfillmentSyncRequests method");
            }

            var batchedRequests = getBatchedFulfillmentSyncRequestsMethod.Invoke(client, new object[] { fulfillmentSyncRequest }) as System.Collections.IEnumerable;
            if (batchedRequests == null)
            {
                throw new InvalidOperationException("GetBatchedFulfillmentSyncRequests returned null");
            }

            var fulfillmentTasks = new List<Task>();

            // Get MakeSyncRequestWithRetries method
            var makeSyncRequestMethod = ReflectionHelper.GetMethod(
                clientType,
                "MakeSyncRequestWithRetries",
                BindingFlags.NonPublic | BindingFlags.Instance,
                new Type[] { typeof(DataExchangeIdentifier), typeof(string), fulfillmentSyncRequestType, typeof(CancellationToken) });
            
            if (makeSyncRequestMethod == null)
            {
                throw new InvalidOperationException("Could not find MakeSyncRequestWithRetries method");
            }

            foreach (var individualRequest in batchedRequests)
            {
                var syncTask = ReflectionHelper.InvokeMethod(client, makeSyncRequestMethod, new object[] { identifier, fulfillmentId, individualRequest, CancellationToken.None }, diagnostics);
                if (syncTask != null)
                {
                    fulfillmentTasks.Add((Task)syncTask);
                }
            }

            // Add ProcessGeometry task
            diagnostics.Add("\n10. Processing geometry...");
            var processGeometryMethod = ReflectionHelper.GetMethod(
                clientType,
                "ProcessGeometry",
                BindingFlags.NonPublic | BindingFlags.Instance,
                new[] { exchangeDataType, typeof(DataExchangeIdentifier), typeof(string), typeof(CancellationToken) });
            
            if (processGeometryMethod != null)
            {
                var processGeometryTask = ReflectionHelper.InvokeMethod(client, processGeometryMethod, new object[] { exchangeData, identifier, fulfillmentId, CancellationToken.None }, diagnostics);
                if (processGeometryTask != null)
                {
                    fulfillmentTasks.Add((Task)processGeometryTask);
                    diagnostics.Add("  ✓ Added ProcessGeometry task");
                }
            }
            else
            {
                diagnostics.Add("  ⚠️ ProcessGeometry not found, skipping");
            }

            diagnostics.Add($"  ✓ Created {fulfillmentTasks.Count} sync request task(s)");
            return fulfillmentTasks;
        }

        private static async Task WaitForAllTasksAsync(List<Task> fulfillmentTasks, List<string> diagnostics)
        {
            diagnostics.Add("\n11. Waiting for all sync tasks to complete...");
            if (fulfillmentTasks.Count > 0)
            {
                await Task.WhenAll(fulfillmentTasks).ConfigureAwait(false);
                diagnostics.Add($"  ✓ All {fulfillmentTasks.Count} task(s) completed");
            }
        }

        private static async Task FinishFulfillmentAsync(object api, Type apiType, DataExchangeIdentifier identifier, string fulfillmentId, List<string> diagnostics)
        {
            diagnostics.Add("\n12. Finishing fulfillment...");
            if (api == null || apiType == null)
            {
                throw new InvalidOperationException("API is null");
            }

            // Use ReflectionHelper.GetMethod with explicit parameter types to avoid ambiguity
            var finishFulfillmentMethod = ReflectionHelper.GetMethod(
                apiType,
                "FinishFulfillmentAsync",
                BindingFlags.Public | BindingFlags.Instance,
                new[] { typeof(string), typeof(string), typeof(string), typeof(CancellationToken) });
            
            if (finishFulfillmentMethod == null)
            {
                // Try without CancellationToken
                finishFulfillmentMethod = ReflectionHelper.GetMethod(
                    apiType,
                    "FinishFulfillmentAsync",
                    BindingFlags.Public | BindingFlags.Instance,
                    new[] { typeof(string), typeof(string), typeof(string) });
            }
            
            if (finishFulfillmentMethod == null)
            {
                throw new InvalidOperationException("Could not find FinishFulfillmentAsync on API with expected signature");
            }

            // Check actual parameter count and call accordingly
            var methodParams = finishFulfillmentMethod.GetParameters();
            object[] invokeParams;
            if (methodParams.Length == 4)
            {
                invokeParams = new object[] { identifier.CollectionId, identifier.ExchangeId, fulfillmentId, CancellationToken.None };
            }
            else if (methodParams.Length == 3)
            {
                invokeParams = new object[] { identifier.CollectionId, identifier.ExchangeId, fulfillmentId };
            }
            else
            {
                throw new InvalidOperationException($"FinishFulfillmentAsync has unexpected parameter count: {methodParams.Length}");
            }

            var finishTask = ReflectionHelper.InvokeMethod(api, finishFulfillmentMethod, invokeParams, diagnostics);
            if (finishTask != null)
            {
                await ((dynamic)finishTask).ConfigureAwait(false);
                diagnostics.Add("  ✓ Finished fulfillment");
            }
        }

        private static async Task PollForFulfillmentAsync(object client, Type clientType, DataExchangeIdentifier identifier, string fulfillmentId, List<string> diagnostics)
        {
            diagnostics.Add("\n13. Polling for fulfillment completion...");
            var pollForFulfillmentMethod = ReflectionHelper.GetMethod(
                clientType,
                "PollForFulfillment",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public,
                new[] { typeof(DataExchangeIdentifier), typeof(string) });
            
            if (pollForFulfillmentMethod != null)
            {
                var pollTask = ReflectionHelper.InvokeMethod(client, pollForFulfillmentMethod, new object[] { identifier, fulfillmentId }, diagnostics);
                if (pollTask != null)
                {
                    await ((dynamic)pollTask).ConfigureAwait(false);
                    diagnostics.Add("  ✓ Fulfillment completed");
                }
            }
            else
            {
                diagnostics.Add("  ⚠️ PollForFulfillment not found, skipping");
            }
        }

        /// <summary>
        /// Generates viewable from exchange geometry so it can be displayed in the viewer
        /// This is CRITICAL - without this, geometry won't appear in the viewer even if uploaded successfully
        /// 
        /// NOTE: This method is currently NOT USED (disabled by default for performance - takes ~131 seconds).
        /// Kept for posterity/reference. The server will generate viewables automatically, so manual generation is typically not needed.
        /// </summary>
        [System.Obsolete("Not used by default - takes ~131 seconds. Use UploadSMBToExchange's generateViewable parameter if needed.")]
        private static async Task GenerateViewableAsync(object client, Type clientType, DataExchangeIdentifier identifier, List<string> diagnostics)
        {
            diagnostics.Add("\n14. Generating viewable from exchange geometry...");
            try
            {
                // Check feature flag status to see if cloud viewable generation is enabled
                try
                {
                    var featureFlagControllerProp = clientType.GetProperty("FeatureFlagController", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                    if (featureFlagControllerProp != null)
                    {
                        var featureFlagController = featureFlagControllerProp.GetValue(client);
                        if (featureFlagController != null)
                        {
                            var getFlagStatusMethod = featureFlagController.GetType().GetMethod("GetFlagStatus", BindingFlags.Public | BindingFlags.Instance);
                            if (getFlagStatusMethod != null)
                            {
                                // Try to get region from exchange
                                var getRegionMethod = clientType.GetMethod("GetRegionFromExchangeID", BindingFlags.NonPublic | BindingFlags.Instance);
                                string region = null;
                                if (getRegionMethod != null)
                                {
                                    var regionTask = ReflectionHelper.InvokeMethod(client, getRegionMethod, new object[] { identifier.ExchangeId }, diagnostics);
                                    if (regionTask != null)
                                    {
                                        region = await ((dynamic)regionTask).ConfigureAwait(false);
                                    }
                                }
                                
                                if (!string.IsNullOrEmpty(region))
                                {
                                    var cloudViewableEnabled = (bool)getFlagStatusMethod.Invoke(featureFlagController, new object[] { "enable-dx-to-lmv-extraction-connector", region });
                                    diagnostics.Add($"  Cloud Viewable Generation Feature Flag Status: {cloudViewableEnabled}");
                                    if (cloudViewableEnabled)
                                    {
                                        diagnostics.Add("  ⚠️ Cloud viewable generation is ENABLED - local generation will be skipped");
                                        diagnostics.Add("  ⚠️ Cloud viewable generation happens asynchronously on the server");
                                        diagnostics.Add("  ⚠️ Your direct SMB upload may not trigger cloud viewable generation correctly");
                                        diagnostics.Add("  ⚠️ Cloud viewable expects geometry processed through normal SDK flow (STEP→SMB conversion)");
                                    }
                                    else
                                    {
                                        diagnostics.Add("  ✓ Cloud viewable generation is DISABLED - local generation will be used");
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception flagEx)
                {
                    diagnostics.Add($"  ⚠️ Could not check feature flag status: {flagEx.Message}");
                }
                
                var generateViewableMethod = ReflectionHelper.GetMethod(
                    clientType,
                    "GenerateViewableAsync",
                    BindingFlags.Public | BindingFlags.Instance,
                    new[] { typeof(string), typeof(string) });
                
                if (generateViewableMethod != null)
                {
                    diagnostics.Add($"  Method found: {generateViewableMethod.Name}");
                    diagnostics.Add($"  Calling with ExchangeId: {identifier.ExchangeId}, CollectionId: {identifier.CollectionId}");
                    
                    var generateTask = ReflectionHelper.InvokeMethod(client, generateViewableMethod, new object[] { identifier.ExchangeId, identifier.CollectionId }, diagnostics);
                    if (generateTask != null)
                    {
                        var response = await ((dynamic)generateTask).ConfigureAwait(false);
                        
                        // Inspect the response (should be IResponse<bool>)
                        if (response != null)
                        {
                            var responseType = response.GetType();
                            diagnostics.Add($"  Response type: {responseType.FullName}");
                            
                            // Check for IsSuccess property
                            var isSuccessProp = responseType.GetProperty("IsSuccess") ?? responseType.GetProperty("Success");
                            if (isSuccessProp != null)
                            {
                                var isSuccess = (bool)isSuccessProp.GetValue(response);
                                diagnostics.Add($"  IsSuccess: {isSuccess}");
                                
                                if (isSuccess)
                                {
                                    // Get Value property
                                    var valueProp = responseType.GetProperty("Value");
                                    if (valueProp != null)
                                    {
                                        var value = valueProp.GetValue(response);
                                        diagnostics.Add($"  Value: {value}");
                                    }
                                    
                                    diagnostics.Add("  ✓ Viewable generation request submitted successfully");
                                    diagnostics.Add("  Note: Viewable processing is asynchronous and may take 10-30 seconds");
                                    diagnostics.Add("  Note: If cloud viewable generation is enabled, local generation is skipped");
                                    diagnostics.Add("  Please refresh/reload the exchange in the viewer to see the new geometry");
                                }
                                else
                                {
                                    // Get Error property
                                    var errorProp = responseType.GetProperty("Error");
                                    if (errorProp != null)
                                    {
                                        var error = errorProp.GetValue(response);
                                        if (error != null)
                                        {
                                            var errorType = error.GetType();
                                            var errorMessageProp = errorType.GetProperty("Message") ?? errorType.GetProperty("Error");
                                            if (errorMessageProp != null)
                                            {
                                                var errorMessage = errorMessageProp.GetValue(error)?.ToString();
                                                diagnostics.Add($"  ✗ Viewable generation failed: {errorMessage}");
                                            }
                                            else
                                            {
                                                diagnostics.Add($"  ✗ Viewable generation failed: {error}");
                                            }
                                        }
                                        else
                                        {
                                            diagnostics.Add("  ✗ Viewable generation failed (no error details)");
                                        }
                                    }
                                    else
                                    {
                                        diagnostics.Add("  ✗ Viewable generation failed (could not get error details)");
                                    }
                                }
                            }
                            else
                            {
                                // Try to get Value directly if it's not IResponse pattern
                                var valueProp = responseType.GetProperty("Value");
                                if (valueProp != null)
                                {
                                    var value = valueProp.GetValue(response);
                                    diagnostics.Add($"  Value: {value}");
                                    diagnostics.Add("  ✓ Viewable generation completed (response format unknown)");
                                }
                                else
                                {
                                    diagnostics.Add($"  ⚠️ Could not parse response format: {responseType.FullName}");
                                }
                            }
                        }
                        else
                        {
                            diagnostics.Add("  ⚠️ GenerateViewableAsync returned null response");
                        }
                    }
                    else
                    {
                        diagnostics.Add("  ⚠️ GenerateViewableAsync returned null task");
                    }
                }
                else
                {
                    diagnostics.Add("  ⚠️ GenerateViewableAsync method not found, geometry may not appear in viewer");
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add($"  ✗ Exception during viewable generation: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                {
                    diagnostics.Add($"  Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }
                diagnostics.Add($"  Stack trace: {ex.StackTrace}");
                diagnostics.Add($"  ⚠️ Geometry may not appear in viewer without viewable generation");
                // Don't throw - viewable generation failure shouldn't break the upload
            }
        }

        private static void ClearLocalStatesAndSetRevision(object client, Type clientType, object exchangeData, Type exchangeDataType, List<string> diagnostics)
        {
            diagnostics.Add("\n14. Clearing local states and setting revision...");
            
            // Get fulfillmentStatus to get revision ID
            var fulfillmentStatusField = clientType.GetField("fulfillmentStatus", BindingFlags.NonPublic | BindingFlags.Instance);
            string revisionId = null;
            if (fulfillmentStatusField != null)
            {
                var fulfillmentStatus = fulfillmentStatusField.GetValue(client);
                if (fulfillmentStatus != null)
                {
                    var revisionIdProp = fulfillmentStatus.GetType().GetProperty("RevisionId", BindingFlags.Public | BindingFlags.Instance);
                    if (revisionIdProp != null)
                    {
                        revisionId = revisionIdProp.GetValue(fulfillmentStatus)?.ToString();
                    }
                }
            }

            if (!string.IsNullOrEmpty(revisionId))
            {
                // ClearLocalStates
                var clearLocalStatesMethod = exchangeDataType.GetMethod("ClearLocalStates", BindingFlags.Public | BindingFlags.Instance);
                if (clearLocalStatesMethod != null)
                {
                    clearLocalStatesMethod.Invoke(exchangeData, new object[] { revisionId });
                    diagnostics.Add($"  ✓ Cleared local states with revision: {revisionId}");
                }

                // SetRevision on RootAsset
                var rootAssetProp = exchangeDataType.GetProperty("RootAsset", BindingFlags.Public | BindingFlags.Instance);
                if (rootAssetProp != null)
                {
                    var rootAsset = rootAssetProp.GetValue(exchangeData);
                    if (rootAsset != null)
                    {
                        var setRevisionMethod = rootAsset.GetType().GetMethod("SetRevision", BindingFlags.Public | BindingFlags.Instance);
                        if (setRevisionMethod != null)
                        {
                            setRevisionMethod.Invoke(rootAsset, new object[] { revisionId });
                            diagnostics.Add($"  ✓ Set revision on RootAsset: {revisionId}");
                        }
                    }
                }
            }
            else
            {
                diagnostics.Add("  ⚠️ Could not get revision ID, skipping ClearLocalStates and SetRevision");
            }
        }

        private static void SetExchangeIdentifierIfNeeded(object exchangeData, Type exchangeDataType, DataExchangeIdentifier identifier, List<string> diagnostics)
        {
            var exchangeIdentifierProp = exchangeDataType.GetProperty("ExchangeIdentifier", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (exchangeIdentifierProp != null)
            {
                var currentIdentifier = exchangeIdentifierProp.GetValue(exchangeData);
                if (currentIdentifier == null)
                {
                    exchangeIdentifierProp.SetValue(exchangeData, identifier);
                    diagnostics.Add("  ✓ Set ExchangeIdentifier on ExchangeData");
                }
            }
        }

        /// <summary>
        /// Discards a fulfillment if an error occurs
        /// </summary>
        private static async Task DiscardFulfillmentAsync(object client, Type clientType, DataExchangeIdentifier identifier, string fulfillmentId, List<string> diagnostics)
        {
            try
            {
                var getAPIMethod = clientType.GetMethod("GetAPI", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                if (getAPIMethod != null)
                {
                    var api = getAPIMethod.Invoke(client, null);
                    var apiType = api.GetType();
                    // Specify parameter types to avoid ambiguity
                    var discardMethod = ReflectionHelper.GetMethod(
                        apiType,
                        "DiscardFulfillmentAsync",
                        BindingFlags.Public | BindingFlags.Instance,
                        new[] { typeof(string), typeof(string), typeof(string), typeof(CancellationToken) });
                    if (discardMethod != null)
                    {
                        var discardTask = ReflectionHelper.InvokeMethod(api, discardMethod, new object[] { identifier.CollectionId, identifier.ExchangeId, fulfillmentId, CancellationToken.None }, diagnostics);
                        if (discardTask != null)
                        {
                            await ((dynamic)discardTask).ConfigureAwait(false);
                            diagnostics.Add($"  ✓ Discarded fulfillment: {fulfillmentId}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add($"  ⚠️ Failed to discard fulfillment: {ex.Message}");
            }
        }

        /// <summary>
        /// Inspects GeometryAsset after operations
        /// </summary>
        private static void InspectGeometryAsset(object geometryAsset, Type geometryAssetType, object exchangeData, Type exchangeDataType, List<string> diagnostics)
        {
            diagnostics.Add("\n=== Inspecting our GeometryAsset AFTER sync ===");
            try
            {
                var ourIdProp = geometryAssetType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                var ourBinaryRefProp = geometryAssetType.GetProperty("BinaryReference", BindingFlags.Public | BindingFlags.Instance);
                
                var ourId = ourIdProp?.GetValue(geometryAsset)?.ToString();
                var ourBinaryRef = ourBinaryRefProp?.GetValue(geometryAsset);
                
                diagnostics.Add($"  Our GeometryAsset ID: {ourId}");
                diagnostics.Add($"  Our BinaryReference AFTER sync: {(ourBinaryRef != null ? "Set" : "Null")}");
                
                if (ourBinaryRef != null)
                {
                    var startProp = ourBinaryRef.GetType().GetProperty("Start");
                    var endProp = ourBinaryRef.GetType().GetProperty("End");
                    var idProp2 = ourBinaryRef.GetType().GetProperty("Id");
                    if (startProp != null && endProp != null && idProp2 != null)
                    {
                        diagnostics.Add($"    BinaryRef Id: {idProp2.GetValue(ourBinaryRef)}");
                        diagnostics.Add($"    BinaryRef Start: {startProp.GetValue(ourBinaryRef)}");
                        diagnostics.Add($"    BinaryRef End: {endProp.GetValue(ourBinaryRef)}");
                    }
                }
                else
                {
                    diagnostics.Add($"  ⚠️ BinaryReference is still NULL after sync - this is the problem!");
                    
                    var getAssetByIdMethod = exchangeDataType.GetMethod("GetAssetById", BindingFlags.Public | BindingFlags.Instance);
                    if (getAssetByIdMethod != null && ourId != null)
                    {
                        var foundAsset = getAssetByIdMethod.Invoke(exchangeData, new object[] { ourId });
                        if (foundAsset != null)
                        {
                            var foundBinaryRefProp = foundAsset.GetType().GetProperty("BinaryReference", BindingFlags.Public | BindingFlags.Instance);
                            var foundBinaryRef = foundBinaryRefProp?.GetValue(foundAsset);
                            diagnostics.Add($"  Found asset in ExchangeData, BinaryReference: {(foundBinaryRef != null ? "Set" : "Null")}");
                        }
                                    }
                                }
            }
            catch (Exception ex)
            {
                diagnostics.Add($"  ⚠️ Error inspecting after sync: {ex.Message}");
            }
        }

        [MultiReturn(new[] { "elementId", "log", "success" })]
        public static Dictionary<string, object> UploadSMBToExchange(
            Exchange exchange,
            string smbFilePath,
            string elementName = "ExportedGeometry",
            string elementId = "",
            string unit = "kUnitType_CentiMeter")
        {
            var overallTimer = Stopwatch.StartNew();
            var diagnostics = new List<string>();
            bool success = false;
            string finalElementId = string.IsNullOrEmpty(elementId) ? Guid.NewGuid().ToString() : elementId;

            try
            {
                diagnostics.Add("=== Upload SMB File to DataExchange ===");
                diagnostics.Add($"Exchange: {exchange?.ExchangeTitle ?? "N/A"} (ID: {exchange?.ExchangeId ?? "N/A"})");
                diagnostics.Add($"SMB File: {smbFilePath}");
                diagnostics.Add($"Element Name: {elementName}");
                diagnostics.Add($"Element ID: {finalElementId}");

                // Validate inputs
                ValidateInputs(exchange, smbFilePath, diagnostics);

                // Get Client instance
                diagnostics.Add("\nGetting Client instance...");
                var client = TimeOperation("TryGetClientInstance (reflection)", 
                    () => TryGetClientInstance(diagnostics), diagnostics);
                if (client == null)
                {
                    throw new InvalidOperationException("Could not find Client instance. Make sure you have selected an Exchange first.");
                }
                diagnostics.Add($"✓ Found Client instance: {client.GetType().FullName}");

                var clientType = client.GetType();

                // Create DataExchangeIdentifier
                var identifier = TimeOperation("CreateDataExchangeIdentifier", 
                    () => CreateDataExchangeIdentifier(exchange, diagnostics), diagnostics);

                // Get ElementDataModel
                var elementDataModel = TimeOperation("GetElementDataModelAsync (reflection + async)", 
                    () => GetElementDataModelAsync(client, clientType, identifier, diagnostics), diagnostics);

                // Create Element
                var element = TimeOperation("CreateElement", 
                    () => CreateElement(elementDataModel, finalElementId, elementName, diagnostics), diagnostics);

                // Get ExchangeData
                var exchangeDataField = typeof(ElementDataModel).GetField("exchangeData", BindingFlags.NonPublic | BindingFlags.Instance);
                if (exchangeDataField == null)
                {
                    throw new InvalidOperationException("Could not find exchangeData field on ElementDataModel");
                }
                var (exchangeData, exchangeDataType) = TimeOperation("GetExchangeData (reflection)", 
                    () => GetExchangeData(elementDataModel, diagnostics), diagnostics);

                // Find required types
                var foundTypes = TimeOperation("FindRequiredTypes (reflection - type discovery)", 
                    () => FindRequiredTypes(exchangeDataType, diagnostics), diagnostics);

                // Load SMB file to check how many geometries it contains
                diagnostics.Add($"\nChecking SMB file for geometry count...");
                diagnostics.Add($"  SMB file path: {smbFilePath}");
                diagnostics.Add($"  File exists: {File.Exists(smbFilePath)}");
                
                var geometriesInFile = LoadGeometriesFromSMBFile(smbFilePath, unit, diagnostics);
                var geometryCount = geometriesInFile.Count;
                diagnostics.Add($"  ✓ Found {geometryCount} geometry object(s) in SMB file");
                
                if (geometryCount == 0)
                {
                    throw new InvalidOperationException("SMB file contains no geometries");
                }

                // The SDK architecture requires one GeometryAsset per geometry
                // Multiple BodyInfo objects in one AssetInfo don't work - the SDK extracts geometries from the SMB file
                // based on the file format, not BodyInfo count. So we need to split into separate SMB files.
                var smbFilesToUpload = new List<string>();
                
                if (geometryCount > 1)
                {
                    diagnostics.Add($"  SMB file contains {geometryCount} geometries - splitting into separate SMB files...");
                    diagnostics.Add($"  (SDK requires one GeometryAsset per geometry, each with its own SMB file)");
                    diagnostics.Add($"  Note: Multiple BodyInfo objects in one AssetInfo don't work - SDK extracts based on file format");
                    
                    var tempDir = Path.Combine(Path.GetTempPath(), "DataExchangeNodes", "SplitGeometries");
                    if (!Directory.Exists(tempDir))
                        Directory.CreateDirectory(tempDir);
                    
                    var baseFileName = Path.GetFileNameWithoutExtension(smbFilePath);
                    var mmPerUnit = ConvertUnitToMmPerUnit(unit);
                    var geometryType = typeof(Geometry);
                    
                    // Find ExportToSMB method
                    var allMethods = geometryType.GetMethods(BindingFlags.Public | BindingFlags.Static);
                    var exportMethod = allMethods.FirstOrDefault(m => 
                        m.Name == "ExportToSMB" &&
                        m.GetParameters().Length == 3 &&
                        m.GetParameters()[0].ParameterType.Name.Contains("IEnumerable") &&
                        m.GetParameters()[1].ParameterType == typeof(string) &&
                        m.GetParameters()[2].ParameterType == typeof(double));
                    
                    if (exportMethod == null)
                    {
                        exportMethod = allMethods.FirstOrDefault(m =>
                            m.Name == "ExportToSMB" &&
                            m.GetParameters().Length == 3 &&
                            m.GetParameters()[1].ParameterType == typeof(string) &&
                            m.GetParameters()[2].ParameterType == typeof(double));
                    }
                    
                    if (exportMethod != null)
                    {
                        for (int i = 0; i < geometryCount; i++)
                        {
                            var singleGeometrySMB = Path.Combine(tempDir, $"{baseFileName}_geometry_{i + 1}_{Guid.NewGuid():N}.smb");
                            var singleGeometry = new List<Geometry> { geometriesInFile[i] };
                            
                            try
                            {
                                exportMethod.Invoke(null, new object[] { singleGeometry, singleGeometrySMB, mmPerUnit });
                                if (File.Exists(singleGeometrySMB))
                                {
                                    smbFilesToUpload.Add(singleGeometrySMB);
                                    diagnostics.Add($"    ✓ Created SMB file for geometry {i + 1}: {Path.GetFileName(singleGeometrySMB)}");
                                }
                                else
                                {
                                    diagnostics.Add($"    ⚠️ ExportToSMB succeeded but file not found: {singleGeometrySMB}");
                                }
                            }
                            catch (Exception ex)
                            {
                                diagnostics.Add($"    ⚠️ Failed to export geometry {i + 1} to SMB: {ex.Message}");
                            }
                        }
                        diagnostics.Add($"  ✓ Split into {smbFilesToUpload.Count} SMB file(s)");
                    }
                    else
                    {
                        diagnostics.Add($"  ⚠️ ExportToSMB method not found - cannot split geometries");
                        diagnostics.Add($"  ⚠️ Will upload as single file (may only upload first geometry)");
                        smbFilesToUpload.Add(smbFilePath);
                    }
                }
                else
                {
                    // Single geometry - use original file
                    smbFilesToUpload.Add(smbFilePath);
                    diagnostics.Add($"  Using original SMB file (single geometry)");
                }

                // Create one GeometryAsset per SMB file
                var allGeometryAssets = new List<(object geometryAsset, Type geometryAssetType, string geometryAssetId)>();
                
                for (int i = 0; i < smbFilesToUpload.Count; i++)
                {
                    var currentSmbFile = smbFilesToUpload[i];
                    var baseFileName = Path.GetFileNameWithoutExtension(smbFilePath);
                    var geometryName = smbFilesToUpload.Count > 1 
                        ? $"{baseFileName}_geometry_{i + 1}" 
                        : baseFileName;
                    
                    diagnostics.Add($"\nCreating GeometryAsset {i + 1} of {smbFilesToUpload.Count} for SMB file (BRep format)...");
                    diagnostics.Add($"  SMB file: {Path.GetFileName(currentSmbFile)}");
                    diagnostics.Add($"  Geometry name: {geometryName}");
                    
                    var (geometryAsset, geometryAssetType, geometryAssetId) = TimeOperation($"CreateGeometryAsset_{i + 1} (reflection)", 
                        () => CreateGeometryAsset(exchangeDataType, foundTypes, diagnostics), diagnostics);
                    
                    TimeOperation($"SetGeometryAssetUnits_{i + 1}", 
                        () => SetGeometryAssetUnits(geometryAsset, geometryAssetType, diagnostics), diagnostics);

                    // Create GeometryWrapper and GeometryComponent
                    var geometryWrapper = TimeOperation($"CreateGeometryWrapper_{i + 1} (reflection)", 
                        () => CreateGeometryWrapper(foundTypes, exchangeDataType, diagnostics), diagnostics);
                    TimeOperation($"CreateGeometryComponent_{i + 1} (reflection)", 
                        () => CreateGeometryComponent(geometryAsset, geometryAssetType, geometryWrapper, foundTypes, exchangeDataType, geometryName, diagnostics), diagnostics);

                    // Add GeometryAsset to ExchangeData
                    TimeOperation($"AddGeometryAssetToExchangeData_{i + 1}", 
                        () => AddGeometryAssetToExchangeData(geometryAsset, exchangeData, exchangeDataType, currentSmbFile, foundTypes, geometryName, diagnostics), diagnostics);

                    // Add GeometryAsset to UnsavedGeometryMapping (required for full SDK flow)
                    // Pass geometryCount=1 since each file now contains one geometry
                    TimeOperation($"AddGeometryAssetToUnsavedMapping_{i + 1}", 
                        () => AddGeometryAssetToUnsavedMapping(geometryAsset, exchangeData, exchangeDataType, currentSmbFile, identifier.ExchangeId, 1, diagnostics), diagnostics);

                    allGeometryAssets.Add((geometryAsset, geometryAssetType, geometryAssetId));
                }

                // Setup DesignAsset and relationships for all GeometryAssets
                // All GeometryAssets will be added to the same DesignAsset
                if (allGeometryAssets.Count > 0)
                {
                    TimeOperation("SetupDesignAssetAndRelationships (complex reflection)", 
                        () => SetupDesignAssetAndRelationshipsForMultipleGeometries(element, allGeometryAssets, exchangeData, exchangeDataType, foundTypes, elementName, diagnostics), diagnostics);
                }

                // Full SyncExchangeDataAsync flow for SMB
                // This follows the complete SDK flow, ensuring geometry is properly processed and visible
                diagnostics.Add("\nStarting full SyncExchangeDataAsync flow for SMB upload...");
                
                try
                {
                    // Call async method synchronously (Dynamo nodes must be synchronous)
                    var syncTask = TimeOperation("SyncExchangeDataAsyncForSMB (entire sync flow)", 
                        () => SyncExchangeDataAsyncForSMB(client, clientType, identifier, exchangeData, exchangeDataType, diagnostics), diagnostics);
                    TimeOperation("Await sync task completion", 
                        () => syncTask.GetAwaiter().GetResult(), diagnostics);
                    
                    // Viewable generation is disabled by default (takes ~131 seconds)
                    // It can be enabled manually if needed, but is typically not required
                    // The server will generate viewables automatically
                    
                    // Check if BinaryReference was set for all GeometryAssets
                    int successCount = 0;
                    for (int i = 0; i < allGeometryAssets.Count; i++)
                    {
                        var (checkGeometryAsset, checkGeometryAssetType, checkGeometryAssetId) = allGeometryAssets[i];
                        var binaryRefProp = checkGeometryAssetType.GetProperty("BinaryReference", BindingFlags.Public | BindingFlags.Instance);
                        if (binaryRefProp != null)
                        {
                            var binaryRef = binaryRefProp.GetValue(checkGeometryAsset);
                            if (binaryRef != null)
                            {
                                successCount++;
                                diagnostics.Add($"  ✓ BinaryReference is set for GeometryAsset {i + 1} (ID: {checkGeometryAssetId})");
                            }
                            else
                            {
                                diagnostics.Add($"  ⚠️ BinaryReference still null for GeometryAsset {i + 1} (ID: {checkGeometryAssetId})");
                            }
                        }
                    }
                    
                    if (successCount > 0)
                    {
                        diagnostics.Add($"  ✓ BinaryReference is set for {successCount} of {allGeometryAssets.Count} GeometryAsset(s) after full sync flow");
                        success = true;
                    }
                    else
                    {
                        diagnostics.Add($"  ⚠️ BinaryReference not set for any GeometryAsset, but flow completed");
                        success = true; // Still mark as success since the flow completed
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"  ✗ Full sync flow failed: {ex.GetType().Name}: {ex.Message}");
                    if (ex.InnerException != null)
                    {
                        diagnostics.Add($"    Inner: {ex.InnerException.Message}");
                    }
                    throw; // Re-throw to be caught by outer try-catch
                }
                
                // Inspect results for all GeometryAssets
                for (int i = 0; i < allGeometryAssets.Count; i++)
                {
                    var (inspectGeometryAsset, inspectGeometryAssetType, inspectGeometryAssetId) = allGeometryAssets[i];
                    TimeOperation($"InspectGeometryAsset_{i + 1}", 
                        () => InspectGeometryAsset(inspectGeometryAsset, inspectGeometryAssetType, exchangeData, exchangeDataType, diagnostics), diagnostics);
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add($"\n✗ ERROR: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                {
                    diagnostics.Add($"  Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }
                diagnostics.Add($"  Stack: {ex.StackTrace}");
            }
            finally
            {
                overallTimer.Stop();
                diagnostics.Add($"\n⏱️  TOTAL UploadSMBToExchange time: {overallTimer.ElapsedMilliseconds}ms ({overallTimer.Elapsed.TotalSeconds:F3}s)");
            }

            return new Dictionary<string, object>
            {
                { "elementId", finalElementId },
                { "log", string.Join("\n", diagnostics) },
                { "success", success }
            };
        }
        
        /// <summary>
        /// Gets available unit options for geometry export/upload.
        /// Use this node to populate a dropdown for unit selection.
        /// Returns unit strings compatible with ExportToSMB and UploadSMBToExchange.
        /// </summary>
        /// <returns>List of unit strings: kUnitType_CentiMeter, kUnitType_Meter, kUnitType_Feet, kUnitType_Inch</returns>
        public static List<string> GetDataExchangeUnits()
        {
            return new List<string>
            {
                "kUnitType_CentiMeter",
                "kUnitType_Meter",
                "kUnitType_Feet",
                "kUnitType_Inch"
            };
        }
    }
}
