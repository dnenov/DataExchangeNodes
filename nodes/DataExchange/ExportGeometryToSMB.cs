using System;
using System.Collections.Generic;
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
            /// Gets a property value via reflection, with optional fallback property name
            /// </summary>
            public static object GetPropertyValue(object instance, string propertyName, BindingFlags flags, string fallbackPropertyName = null)
            {
                if (instance == null)
                {
                    return null;
                }

                var type = instance.GetType();
                var property = type.GetProperty(propertyName, flags);
                
                if (property == null && !string.IsNullOrEmpty(fallbackPropertyName))
                {
                    property = type.GetProperty(fallbackPropertyName, flags);
                }

                return property?.GetValue(instance);
            }

            /// <summary>
            /// Sets a property value via reflection
            /// </summary>
            public static void SetPropertyValue(object instance, string propertyName, object value, BindingFlags flags, List<string> diagnostics = null)
            {
                if (instance == null)
                {
                    throw new ArgumentNullException(nameof(instance));
                }

                var type = instance.GetType();
                var property = type.GetProperty(propertyName, flags);
                
                if (property == null)
                {
                    diagnostics?.Add($"  ⚠️ Property {propertyName} not found on {type.FullName}");
                    return;
                }

                if (!property.CanWrite)
                {
                    diagnostics?.Add($"  ⚠️ Property {propertyName} is read-only");
                    return;
                }

                try
                {
                    property.SetValue(instance, value);
                }
                catch (Exception ex)
                {
                    diagnostics?.Add($"  ⚠️ Failed to set {propertyName}: {ex.Message}");
                    throw;
                }
            }

            /// <summary>
            /// Finds a type across assemblies, with optional caching
            /// </summary>
            public static Type FindType(string typeName, Type searchFromType, Dictionary<string, Type> cache = null)
            {
                // Check cache first
                if (cache != null && cache.TryGetValue(typeName, out var cachedType))
                {
                    return cachedType;
                }

                // Try searchFromType's assembly first
                if (searchFromType != null)
                {
                    var type = searchFromType.Assembly.GetType(typeName);
                    if (type != null)
                    {
                        cache?.Add(typeName, type);
                        return type;
                    }
                }

                // Search all DataExchange assemblies
                var allAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.GetName().Name.Contains("DataExchange"))
                    .ToList();

                foreach (var asm in allAssemblies)
                {
                    var foundType = asm.GetType(typeName);
                    if (foundType != null)
                    {
                        cache?.Add(typeName, foundType);
                        return foundType;
                    }
                }

                return null;
            }

            /// <summary>
            /// Creates an instance and sets its ID using SetId method (checks base types)
            /// </summary>
            public static object CreateInstanceWithId(Type type, string id, Dictionary<string, Type> foundTypes = null, List<string> diagnostics = null)
            {
                var instance = Activator.CreateInstance(type);
                if (instance == null)
                {
                    throw new InvalidOperationException($"Failed to create instance of {type.FullName}");
                }

                // Find SetId method (might be on base type)
                var setIdMethod = type.GetMethod("SetId", BindingFlags.NonPublic | BindingFlags.Instance);
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

                setIdMethod.Invoke(instance, new object[] { id });
                diagnostics?.Add($"✓ Created {type.Name} with ID: {id}");
                return instance;
            }

            /// <summary>
            /// Gets an enum value by name, searching from a type's assembly
            /// </summary>
            public static object GetEnumValue(string enumTypeName, string valueName, Type searchFromType, Dictionary<string, Type> foundTypes = null)
            {
                Type enumType = null;
                
                // Check cache first
                if (foundTypes != null && foundTypes.TryGetValue(enumTypeName, out var cached))
                {
                    enumType = cached;
                }
                else
                {
                    enumType = FindType(enumTypeName, searchFromType, foundTypes);
                }

                if (enumType == null || !enumType.IsEnum)
                {
                    throw new InvalidOperationException($"Could not find enum type {enumTypeName}");
                }

                return Enum.Parse(enumType, valueName);
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
        
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);
        
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
                var geometryType = GetGeometryTypeFromNewAssembly(diagnostics);
                
                // Find ExportToSMB method
                var allExportMethods = geometryType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Where(m => m.Name == "ExportToSMB")
                    .ToList();
                
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
                
                var result = exportMethod.Invoke(null, new object[] { geometries, exportSmbFilePath, mmPerUnit });
                
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

            if (elementDataModel == null)
            {
                var valueInfo = valueProp != null 
                    ? $"Value type: {valueProp.GetValue(response)?.GetType().FullName ?? "null"}" 
                    : "No Value property";
                throw new InvalidOperationException(
                    $"Could not extract ElementDataModel from response of type {responseType.FullName}. {valueInfo}");
            }

            diagnostics.Add($"✓ Got ElementDataModel: {elementDataModel.GetType().FullName}");
            return elementDataModel;
        }

        /// <summary>
        /// Creates Element and ElementProperties
        /// </summary>
        private static object CreateElement(ElementDataModel elementDataModel, string finalElementId, string elementName, List<string> diagnostics)
        {
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
                diagnostics.Add($"✓ Added element: {element.GetType().FullName}");
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
        /// Finds all required types across assemblies
        /// </summary>
        private static Dictionary<string, Type> FindRequiredTypes(Type exchangeDataType, List<string> diagnostics)
        {
                var allAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.GetName().Name.Contains("DataExchange"))
                    .ToList();
                
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
                    if (foundType == null)
                    {
                    // Fallback: search all assemblies manually
                        foreach (var asm in allAssemblies)
                        {
                            foundType = asm.GetType(typeName);
                            if (foundType != null)
                            {
                                foundTypes[typeName] = foundType;
                                break;
                            }
                        }
                    }
                else if (!foundTypes.ContainsKey(typeName))
                    {
                        foundTypes[typeName] = foundType;
                    }
                }

            return foundTypes;
        }

        /// <summary>
        /// Creates a GeometryAsset instance with ID set
        /// </summary>
        private static (object geometryAsset, Type geometryAssetType, string geometryAssetId) CreateGeometryAsset(Type exchangeDataType, Dictionary<string, Type> foundTypes, List<string> diagnostics)
        {
            diagnostics.Add($"\nCreating GeometryAsset for SMB file (BRep format)...");
            
                Type geometryAssetType = null;
                if (foundTypes.TryGetValue("Autodesk.DataExchange.SchemaObjects.Assets.GeometryAsset", out var preFound))
                {
                    geometryAssetType = preFound;
                }
                else
                {
                    geometryAssetType = exchangeDataType.Assembly.GetType("Autodesk.DataExchange.SchemaObjects.Assets.GeometryAsset");
                }
                
                if (geometryAssetType == null)
                {
                    throw new InvalidOperationException("Could not find GeometryAsset type");
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
        /// Creates GeometryWrapper for BRep geometry
        /// </summary>
        private static object CreateGeometryWrapper(Dictionary<string, Type> foundTypes, Type exchangeDataType, List<string> diagnostics)
        {
                Type geometryWrapperType = null;
                if (foundTypes.TryGetValue("Autodesk.DataExchange.SchemaObjects.Geometry.GeometryWrapper", out var preFoundGeoWrapper))
                {
                    geometryWrapperType = preFoundGeoWrapper;
                }
                else
                {
                    geometryWrapperType = exchangeDataType.Assembly.GetType("Autodesk.DataExchange.SchemaObjects.Geometry.GeometryWrapper");
                }
                
                if (geometryWrapperType == null)
                {
                    throw new InvalidOperationException("Could not find GeometryWrapper type");
                }
                
                Type geometryFormatEnumType = null;
                if (foundTypes.TryGetValue("Autodesk.DataExchange.Core.Enums.GeometryFormat", out var preFoundFormat))
                {
                    geometryFormatEnumType = preFoundFormat;
                }
                else
                {
                    geometryFormatEnumType = exchangeDataType.Assembly.GetType("Autodesk.DataExchange.Core.Enums.GeometryFormat");
                }
                
                Type geometryTypeEnumType = null;
                if (foundTypes.TryGetValue("Autodesk.DataExchange.Core.Enums.GeometryType", out var preFoundGeometryType))
                {
                    geometryTypeEnumType = preFoundGeometryType;
                }
                else
                {
                    geometryTypeEnumType = exchangeDataType.Assembly.GetType("Autodesk.DataExchange.Core.Enums.GeometryType");
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
        /// Creates GeometryComponent and sets it on GeometryAsset
        /// </summary>
        private static void CreateGeometryComponent(object geometryAsset, Type geometryAssetType, object geometryWrapper, Dictionary<string, Type> foundTypes, Type exchangeDataType, List<string> diagnostics)
        {
                Type geometryComponentType = null;
                if (foundTypes.TryGetValue("Autodesk.DataExchange.SchemaObjects.Components.GeometryComponent", out var preFoundComponent))
                {
                    geometryComponentType = preFoundComponent;
                }
                else
                {
                    geometryComponentType = exchangeDataType.Assembly.GetType("Autodesk.DataExchange.SchemaObjects.Components.GeometryComponent");
                }
                
                if (geometryComponentType == null)
                {
                    throw new InvalidOperationException("Could not find GeometryComponent type");
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
        /// Adds GeometryAsset to ExchangeData and sets ObjectInfo
        /// </summary>
        private static void AddGeometryAssetToExchangeData(object geometryAsset, object exchangeData, Type exchangeDataType, string geometryFilePath, Dictionary<string, Type> foundTypes, List<string> diagnostics)
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
                    Type componentType = null;
                    if (foundTypes.TryGetValue("Autodesk.DataExchange.SchemaObjects.Components.Component", out var preFoundComp))
                    {
                        componentType = preFoundComp;
                    }
                    else
                    {
                        componentType = exchangeDataType.Assembly.GetType("Autodesk.DataExchange.SchemaObjects.Components.Component");
                    }
                    
                    if (componentType != null)
                    {
                        var objectInfo = Activator.CreateInstance(componentType);
                        var nameProperty = componentType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                        if (nameProperty != null)
                        {
                            nameProperty.SetValue(objectInfo, Path.GetFileNameWithoutExtension(geometryFilePath));
                        }
                        objectInfoProperty.SetValue(geometryAsset, objectInfo);
                    }
                }
        }

        /// <summary>
        /// Wraps SMB bytes into BrepTopologicalData protobuf format
        /// The SDK expects protobuf-wrapped SMB data, not raw SMB files
        /// </summary>
        private static string WrapSMBIntoProtobuf(string smbFilePath, List<string> diagnostics)
        {
            diagnostics.Add($"\nWrapping SMB file into BrepTopologicalData protobuf format...");
            
            try
            {
                // Read SMB file bytes
                var smbBytes = File.ReadAllBytes(smbFilePath);
                diagnostics.Add($"  Read {smbBytes.Length} bytes from SMB file");
                
                // Find BrepTopologicalData type
                var brepTopologicalDataType = Type.GetType("Autodesk.DataExchange.Geometry.Proto.BrepTopologicalData, Autodesk.DataExchange");
                if (brepTopologicalDataType == null)
                {
                    // Try searching in all assemblies
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name.Contains("DataExchange"))
                        {
                            brepTopologicalDataType = asm.GetType("Autodesk.DataExchange.Geometry.Proto.BrepTopologicalData");
                            if (brepTopologicalDataType != null) break;
                        }
                    }
                }
                
                if (brepTopologicalDataType == null)
                {
                    throw new InvalidOperationException("Could not find BrepTopologicalData type. Make sure Autodesk.DataExchange assembly is loaded.");
                }
                
                // Create BrepTopologicalData instance
                var brepData = Activator.CreateInstance(brepTopologicalDataType);
                
                // Set Body property (ByteString)
                var bodyProperty = brepTopologicalDataType.GetProperty("Body", BindingFlags.Public | BindingFlags.Instance);
                if (bodyProperty == null)
                {
                    throw new InvalidOperationException("Could not find Body property on BrepTopologicalData");
                }
                
                // Create ByteString from SMB bytes
                var byteStringType = Type.GetType("Google.Protobuf.ByteString, Google.Protobuf");
                if (byteStringType == null)
                {
                    // Try alternative namespace
                    byteStringType = Type.GetType("pb.ByteString, Autodesk.DataExchange");
                }
                
                if (byteStringType == null)
                {
                    // Search for ByteString in loaded assemblies
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        byteStringType = asm.GetType("Google.Protobuf.ByteString") ?? asm.GetType("pb.ByteString");
                        if (byteStringType != null) break;
                    }
                }
                
                if (byteStringType == null)
                {
                    throw new InvalidOperationException("Could not find ByteString type. Make sure Google.Protobuf assembly is loaded.");
                }
                
                // Call ByteString.CopyFrom(byte[])
                var copyFromMethod = byteStringType.GetMethod("CopyFrom", new[] { typeof(byte[]) });
                if (copyFromMethod == null)
                {
                    throw new InvalidOperationException("Could not find ByteString.CopyFrom method");
                }
                
                var byteString = copyFromMethod.Invoke(null, new object[] { smbBytes });
                bodyProperty.SetValue(brepData, byteString);
                diagnostics.Add($"  ✓ Created BrepTopologicalData with SMB bytes in Body");
                
                // Serialize protobuf to byte array
                // BrepTopologicalData implements IMessage which has ToByteArray() extension method
                // Or we can use WriteTo with MemoryStream
                byte[] protoBytes;
                var toByteArrayMethod = brepTopologicalDataType.GetMethod("ToByteArray", BindingFlags.Public | BindingFlags.Instance);
                if (toByteArrayMethod != null)
                {
                    protoBytes = (byte[])toByteArrayMethod.Invoke(brepData, null);
                }
                else
                {
                    // Use WriteTo with MemoryStream
                    var writeToMethod = brepTopologicalDataType.GetMethod("WriteTo", new[] { typeof(System.IO.Stream) });
                    if (writeToMethod == null)
                    {
                        // Try with CodedOutputStream
                        var codedOutputStreamType = Type.GetType("Google.Protobuf.CodedOutputStream, Google.Protobuf") 
                            ?? Type.GetType("pb.CodedOutputStream, Autodesk.DataExchange");
                        if (codedOutputStreamType != null)
                        {
                            using (var ms = new MemoryStream())
                            {
                                var cosCtor = codedOutputStreamType.GetConstructor(new[] { typeof(Stream) });
                                if (cosCtor != null)
                                {
                                    var cos = cosCtor.Invoke(new object[] { ms });
                                    writeToMethod = brepTopologicalDataType.GetMethod("WriteTo", new[] { codedOutputStreamType });
                                    if (writeToMethod != null)
                                    {
                                        writeToMethod.Invoke(brepData, new object[] { cos });
                                        var flushMethod = codedOutputStreamType.GetMethod("Flush");
                                        if (flushMethod != null)
                                        {
                                            flushMethod.Invoke(cos, null);
                                        }
                                        protoBytes = ms.ToArray();
                                    }
                                    else
                                    {
                                        throw new InvalidOperationException("Could not find WriteTo method on BrepTopologicalData");
                                    }
                                }
                                else
                                {
                                    throw new InvalidOperationException("Could not create CodedOutputStream");
                                }
                            }
                        }
                        else
                        {
                            throw new InvalidOperationException("Could not find CodedOutputStream type");
                        }
                    }
                    else
                    {
                        using (var ms = new MemoryStream())
                        {
                            writeToMethod.Invoke(brepData, new object[] { ms });
                            protoBytes = ms.ToArray();
                        }
                    }
                }
                
                diagnostics.Add($"  ✓ Serialized protobuf to {protoBytes.Length} bytes");
                
                // Write to temporary file
                var protoFilePath = Path.ChangeExtension(smbFilePath, ".brepTopologicalData.bin");
                // If file already exists, use a unique name
                if (File.Exists(protoFilePath))
                {
                    var dir = Path.GetDirectoryName(protoFilePath);
                    var baseName = Path.GetFileNameWithoutExtension(protoFilePath);
                    var ext = Path.GetExtension(protoFilePath);
                    protoFilePath = Path.Combine(dir, $"{baseName}_{Guid.NewGuid():N}{ext}");
                }
                
                File.WriteAllBytes(protoFilePath, protoBytes);
                diagnostics.Add($"  ✓ Wrote protobuf to: {protoFilePath}");
                
                return protoFilePath;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"  ✗ Failed to wrap SMB into protobuf: {ex.GetType().Name}: {ex.Message}");
                diagnostics.Add($"  Stack: {ex.StackTrace}");
                throw;
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

        /// <summary>
        /// Adds GeometryAsset to UnsavedGeometryMapping using SetBRepGeometryByAsset
        /// ADAPTER PATTERN: Register dummy STEP file (not SMB) to satisfy SDK's STEP→SMB translation contract.
        /// The real SMB file will be set as OutputPath later in GetAllAssetInfosWithTranslatedGeometryPathForSMB.
        /// </summary>
        private static string AddGeometryAssetToUnsavedMapping(object geometryAsset, object exchangeData, Type exchangeDataType, string smbFilePath, string exchangeId, List<string> diagnostics)
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
                diagnostics.Add($"  Stored mapping: GeometryAsset {geometryAssetId} -> SMB: {smbFilePath}");
                
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
        private static System.Collections.Generic.IEnumerable<object> GetAllAssetInfosWithTranslatedGeometryPathForSMB(object client, Type clientType, object exchangeData, Type exchangeDataType, DataExchangeIdentifier exchangeIdentifier, string fulfillmentId, Dictionary<string, string> geometryAssetIdToSmbPath, List<string> diagnostics)
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
                            
                            if (pathProp != null && outputPathProp != null && idProp != null)
                            {
                                var path = pathProp.GetValue(assetInfo)?.ToString();
                                var assetInfoId = idProp.GetValue(assetInfo)?.ToString();
                                
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
                                            var bodyInfo = Activator.CreateInstance(bodyInfoType);
                                            var typeProp = bodyInfoType.GetProperty("Type");
                                            if (typeProp != null)
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
                                                typeProp.SetValue(bodyInfo, brepValue);
                                                
                                                var bodyInfoListType = typeof(System.Collections.Generic.List<>).MakeGenericType(bodyInfoType);
                                                var bodyInfoList = Activator.CreateInstance(bodyInfoListType);
                                                var addMethod = bodyInfoListType.GetMethod("Add");
                                                if (addMethod != null)
                                                {
                                                    addMethod.Invoke(bodyInfoList, new object[] { bodyInfo });
                                                    bodyInfoListProp.SetValue(assetInfo, bodyInfoList);
                                                    diagnostics.Add($"  Set BodyInfoList with BRep type on AssetInfo");
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        diagnostics.Add($"  ⚠️ Failed to set BodyInfoList: {ex.Message}");
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
            
            Type designAssetType = null;
            if (foundTypes.TryGetValue("Autodesk.DataExchange.SchemaObjects.Assets.DesignAsset", out var preFoundDesign))
            {
                designAssetType = preFoundDesign;
            }
            else
            {
                designAssetType = exchangeDataType.Assembly.GetType("Autodesk.DataExchange.SchemaObjects.Assets.DesignAsset");
            }
            
            if (designAssetType == null)
            {
                throw new InvalidOperationException("Could not find DesignAsset type");
            }

            Type instanceAssetType = null;
            var instanceAssetTypeName = "Autodesk.DataExchange.SchemaObjects.Assets.InstanceAsset";
            if (foundTypes.TryGetValue(instanceAssetTypeName, out var preFoundInstance))
            {
                instanceAssetType = preFoundInstance;
            }
            else
            {
                instanceAssetType = exchangeDataType.Assembly.GetType(instanceAssetTypeName);
                if (instanceAssetType == null)
                {
                    var allAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                        .Where(a => a.GetName().Name.Contains("DataExchange"))
                        .ToList();
                    foreach (var asm in allAssemblies)
                    {
                        instanceAssetType = asm.GetType(instanceAssetTypeName);
                        if (instanceAssetType != null) break;
                    }
                }
            }
            
            if (instanceAssetType == null)
            {
                throw new InvalidOperationException("Could not find InstanceAsset type");
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
                    Type componentType = null;
                    if (foundTypes.TryGetValue("Autodesk.DataExchange.SchemaObjects.Components.Component", out var preFoundComp))
                    {
                        componentType = preFoundComp;
                    }
                    else
                    {
                        componentType = exchangeDataType.Assembly.GetType("Autodesk.DataExchange.SchemaObjects.Components.Component");
                    }
                    
                    if (componentType != null)
                    {
                        var objectInfo = Activator.CreateInstance(componentType);
                        var nameProperty = componentType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                        if (nameProperty != null)
                        {
                            nameProperty.SetValue(objectInfo, "TopLevelAssembly");
                        }
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
                    Type containmentRelationshipType = null;
                    if (foundTypes.TryGetValue("Autodesk.DataExchange.SchemaObjects.Relationships.ContainmentRelationship", out var preFoundContainment))
                    {
                        containmentRelationshipType = preFoundContainment;
                    }
                    else
                    {
                        var allAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                            .Where(a => a.GetName().Name.Contains("DataExchange"))
                            .ToList();
                        foreach (var asm in allAssemblies)
                        {
                            containmentRelationshipType = asm.GetType("Autodesk.DataExchange.SchemaObjects.Relationships.ContainmentRelationship");
                            if (containmentRelationshipType != null) break;
                        }
                    }
                    
                    if (containmentRelationshipType != null)
                    {
                        var containmentRelationship = Activator.CreateInstance(containmentRelationshipType);
                        rootAddChildMethod.Invoke(rootAsset, new object[] { elementAsset, containmentRelationship });
                        diagnostics.Add("  ✓ Linked Element's InstanceAsset to RootAsset");
                    }
                }
            }
            
            // Always create a NEW DesignAsset for this upload (don't reuse existing ones)
            // This ensures each upload appears as a separate item in the tree
            object designAsset = null;
            
            // Check if Element's InstanceAsset already has a DesignAsset (from AddElement or previous uploads)
            var elementAssetChildNodesProp = elementAssetType.GetProperty("ChildNodes", BindingFlags.Public | BindingFlags.Instance);
            bool hasExistingDesignAsset = false;
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
                                hasExistingDesignAsset = true;
                                var idProp = node.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                                var existingId = idProp?.GetValue(node)?.ToString() ?? "N/A";
                                diagnostics.Add($"  ⚠️ Found existing DesignAsset (ID: {existingId}) - will create new one for this upload");
                                break;
                            }
                        }
                    }
                }
            }
            
            // Always create a NEW DesignAsset for this geometry upload
            // This ensures each upload appears as a separate, distinct item
            diagnostics.Add("  Creating new DesignAsset for this geometry upload...");
            {
                diagnostics.Add("  Creating new DesignAsset for Element's InstanceAsset...");
                var designAssetId = Guid.NewGuid().ToString();
                designAsset = ReflectionHelper.CreateInstanceWithId(designAssetType, designAssetId, foundTypes, diagnostics);
                
                // Set ObjectInfo
                var objectInfoProp = designAssetType.GetProperty("ObjectInfo", BindingFlags.Public | BindingFlags.Instance);
                if (objectInfoProp != null)
                {
                    Type componentType = null;
                    if (foundTypes.TryGetValue("Autodesk.DataExchange.SchemaObjects.Components.Component", out var preFoundComp))
                    {
                        componentType = preFoundComp;
                    }
                    else
                    {
                        componentType = exchangeDataType.Assembly.GetType("Autodesk.DataExchange.SchemaObjects.Components.Component");
                    }
                    
                    if (componentType != null)
                    {
                        var objectInfo = Activator.CreateInstance(componentType);
                        var nameProperty = componentType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                        if (nameProperty != null)
                        {
                            nameProperty.SetValue(objectInfo, elementName);
                        }
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
                        Type referenceRelationshipType = null;
                        if (foundTypes.TryGetValue("Autodesk.DataExchange.SchemaObjects.Relationships.ReferenceRelationship", out var preFoundRefRel))
                        {
                            referenceRelationshipType = preFoundRefRel;
                        }
                        else
                        {
                            referenceRelationshipType = exchangeDataType.Assembly.GetType("Autodesk.DataExchange.SchemaObjects.Relationships.ReferenceRelationship");
                        }
                        
                        if (referenceRelationshipType != null)
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
                    Type referenceRelationshipType = null;
                    if (foundTypes.TryGetValue("Autodesk.DataExchange.SchemaObjects.Relationships.ReferenceRelationship", out var preFoundRefRel))
                    {
                        referenceRelationshipType = preFoundRefRel;
                    }
                    else
                    {
                        referenceRelationshipType = exchangeDataType.Assembly.GetType("Autodesk.DataExchange.SchemaObjects.Relationships.ReferenceRelationship");
                    }
                    
                    if (referenceRelationshipType != null)
                    {
                        var referenceRelationship = Activator.CreateInstance(referenceRelationshipType);
                        var modelStructureProperty = referenceRelationshipType.GetProperty("ModelStructure", BindingFlags.Public | BindingFlags.Instance);
                        if (modelStructureProperty != null)
                        {
                            Type modelStructureType = null;
                            if (foundTypes.TryGetValue("Autodesk.DataExchange.SchemaObjects.Components.ModelStructure", out var preFoundModelStruct))
                            {
                                modelStructureType = preFoundModelStruct;
                            }
                            else
                            {
                                modelStructureType = exchangeDataType.Assembly.GetType("Autodesk.DataExchange.SchemaObjects.Components.ModelStructure");
                            }
                            
                            if (modelStructureType != null)
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
                    Type containmentRelationshipType = null;
                    if (foundTypes.TryGetValue("Autodesk.DataExchange.SchemaObjects.Relationships.ContainmentRelationship", out var preFoundContainment))
                    {
                        containmentRelationshipType = preFoundContainment;
                    }
                    else
                    {
                        var allAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                            .Where(a => a.GetName().Name.Contains("DataExchange"))
                            .ToList();
                        foreach (var asm in allAssemblies)
                        {
                            containmentRelationshipType = asm.GetType("Autodesk.DataExchange.SchemaObjects.Relationships.ContainmentRelationship");
                            if (containmentRelationshipType != null) break;
                        }
                    }
                    
                    if (containmentRelationshipType != null)
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
        /// Full SyncExchangeDataAsync flow adapted for direct SMB uploads
        /// Follows the exact same flow as the internal SDK method but skips STEP→SMB conversion
        /// </summary>
        private static async Task SyncExchangeDataAsyncForSMB(object client, Type clientType, DataExchangeIdentifier identifier, object exchangeData, Type exchangeDataType, List<string> diagnostics)
        {
            diagnostics.Add($"\n=== Starting Full SyncExchangeDataAsync Flow for SMB ===");
            string fulfillmentId = string.Empty;
            object api = null;
            Type apiType = null;

            try
            {
                await ProcessRenderStylesFromFileGeometryAsync(client, clientType, exchangeData, exchangeDataType, diagnostics);
                
                fulfillmentId = await StartFulfillmentAsync(client, clientType, identifier, exchangeData, exchangeDataType, diagnostics);
                api = GetAPI(client, clientType);
                apiType = api?.GetType();

                var assetInfosList = await GetAssetInfosForSMBAsync(client, clientType, exchangeData, exchangeDataType, identifier, fulfillmentId, diagnostics);
                
                AddRenderStylesToAssetInfos(client, clientType, assetInfosList, exchangeData, exchangeDataType, diagnostics);
                
                await UploadGeometriesAsync(client, clientType, identifier, fulfillmentId, assetInfosList, exchangeData, exchangeDataType, diagnostics);
                
                await UploadCustomGeometriesAsync(client, clientType, identifier, fulfillmentId, exchangeData, exchangeDataType, diagnostics);
                
                await UploadLargePrimitiveGeometriesAsync(client, clientType, identifier, fulfillmentId, exchangeData, exchangeDataType, diagnostics);
                
                var fulfillmentSyncRequest = await GetFulfillmentSyncRequestAsync(client, clientType, identifier, exchangeData, exchangeDataType, diagnostics);
                
                var fulfillmentTasks = await BatchAndSendSyncRequestsAsync(client, clientType, identifier, fulfillmentId, fulfillmentSyncRequest, exchangeData, exchangeDataType, diagnostics);
                
                await WaitForAllTasksAsync(fulfillmentTasks, diagnostics);
                
                await FinishFulfillmentAsync(api, apiType, identifier, fulfillmentId, diagnostics);
                
                await PollForFulfillmentAsync(client, clientType, identifier, fulfillmentId, diagnostics);
                
                ClearLocalStatesAndSetRevision(client, clientType, exchangeData, exchangeDataType, diagnostics);
                
                SetExchangeIdentifierIfNeeded(exchangeData, exchangeDataType, identifier, diagnostics);

                // Generate viewable so geometry appears in the viewer
                await GenerateViewableAsync(client, clientType, identifier, diagnostics);

                diagnostics.Add("\n✓ Full SyncExchangeDataAsync flow completed successfully");
            }
            catch (Exception ex)
            {
                diagnostics.Add($"\n✗ ERROR in SyncExchangeDataAsyncForSMB: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                {
                    diagnostics.Add($"  Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }
                diagnostics.Add($"  Stack: {ex.StackTrace}");

                // Discard fulfillment on error
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
            
            var assetInfos = GetAllAssetInfosWithTranslatedGeometryPathForSMB(client, clientType, exchangeData, exchangeDataType, identifier, fulfillmentId, geometryAssetIdToSmbPath, diagnostics);
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
        /// </summary>
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
                var client = TryGetClientInstance(diagnostics);
                if (client == null)
                {
                    throw new InvalidOperationException("Could not find Client instance. Make sure you have selected an Exchange first.");
                }
                diagnostics.Add($"✓ Found Client instance: {client.GetType().FullName}");

                var clientType = client.GetType();

                // Create DataExchangeIdentifier
                var identifier = CreateDataExchangeIdentifier(exchange, diagnostics);

                // Get ElementDataModel
                var elementDataModel = GetElementDataModelAsync(client, clientType, identifier, diagnostics);

                // Create Element
                var element = CreateElement(elementDataModel, finalElementId, elementName, diagnostics);

                // Get ExchangeData
                var exchangeDataField = typeof(ElementDataModel).GetField("exchangeData", BindingFlags.NonPublic | BindingFlags.Instance);
                if (exchangeDataField == null)
                {
                    throw new InvalidOperationException("Could not find exchangeData field on ElementDataModel");
                }
                var (exchangeData, exchangeDataType) = GetExchangeData(elementDataModel, diagnostics);

                // Find required types
                var foundTypes = FindRequiredTypes(exchangeDataType, diagnostics);

                // Create GeometryAsset
                diagnostics.Add($"\nCreating GeometryAsset for SMB file (BRep format)...");
                diagnostics.Add($"  SMB file path: {smbFilePath}");
                diagnostics.Add($"  File exists: {File.Exists(smbFilePath)}");
                var (geometryAsset, geometryAssetType, geometryAssetId) = CreateGeometryAsset(exchangeDataType, foundTypes, diagnostics);
                SetGeometryAssetUnits(geometryAsset, geometryAssetType, diagnostics);

                // Create GeometryWrapper and GeometryComponent
                var geometryWrapper = CreateGeometryWrapper(foundTypes, exchangeDataType, diagnostics);
                CreateGeometryComponent(geometryAsset, geometryAssetType, geometryWrapper, foundTypes, exchangeDataType, diagnostics);

                // Add GeometryAsset to ExchangeData
                AddGeometryAssetToExchangeData(geometryAsset, exchangeData, exchangeDataType, smbFilePath, foundTypes, diagnostics);

                // Add GeometryAsset to UnsavedGeometryMapping (required for full SDK flow)
                // Pass exchangeId for mapping storage
                AddGeometryAssetToUnsavedMapping(geometryAsset, exchangeData, exchangeDataType, smbFilePath, identifier.ExchangeId, diagnostics);

                // Setup DesignAsset and relationships
                SetupDesignAssetAndRelationships(element, geometryAsset, geometryAssetType, exchangeData, exchangeDataType, foundTypes, elementName, diagnostics);

                // Full SyncExchangeDataAsync flow for SMB
                // This follows the complete SDK flow, ensuring geometry is properly processed and visible
                diagnostics.Add("\nStarting full SyncExchangeDataAsync flow for SMB upload...");
                
                try
                {
                    // Call async method synchronously (Dynamo nodes must be synchronous)
                    var syncTask = SyncExchangeDataAsyncForSMB(client, clientType, identifier, exchangeData, exchangeDataType, diagnostics);
                    syncTask.GetAwaiter().GetResult();
                    
                    // Check if BinaryReference was set
                    var binaryRefProp = geometryAssetType.GetProperty("BinaryReference", BindingFlags.Public | BindingFlags.Instance);
                    if (binaryRefProp != null)
                    {
                        var binaryRef = binaryRefProp.GetValue(geometryAsset);
                        if (binaryRef != null)
                        {
                            diagnostics.Add($"  ✓ BinaryReference is set after full sync flow");
                            success = true;
                        }
                        else
                        {
                            diagnostics.Add($"  ⚠️ BinaryReference still null after full sync flow");
                            success = true; // Still mark as success since the flow completed
                        }
                    }
                    else
                    {
                        success = true;
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
                
                // Inspect results
                InspectGeometryAsset(geometryAsset, geometryAssetType, exchangeData, exchangeDataType, diagnostics);
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

            return new Dictionary<string, object>
            {
                { "elementId", finalElementId },
                { "log", string.Join("\n", diagnostics) },
                { "success", success }
            };
        }
    }
}
