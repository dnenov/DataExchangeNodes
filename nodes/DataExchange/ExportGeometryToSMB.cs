using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
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

                // Try to get Value property
                var valueProp = responseType.GetProperty("Value");
                if (valueProp != null)
                {
                    var value = valueProp.GetValue(response);
                    if (value is T typedValue)
                    {
                        return typedValue;
                    }
                }

                // Last resort: try direct cast
                try
                {
                    return (T)response;
                }
                catch
                {
                    throw new InvalidOperationException($"Could not convert response of type {responseType.FullName} to {typeof(T).FullName}");
                }
            }
        }
        // Paths to the new DLLs from colleague
        private static readonly string DownloadsRootDir = @"C:\Users\nenovd\Downloads\Dynamo\RootDir";
        private static readonly string DownloadsLibgDir = @"C:\Users\nenovd\Downloads\Dynamo\Libg_231_0_0";
        private static readonly string NewProtoGeometryPath = Path.Combine(DownloadsRootDir, "ProtoGeometry.dll");
        private static Assembly _newProtoGeometryAssembly = null;
        private static Type _geometryTypeFromNewAssembly = null;
        private static bool _dependenciesLoaded = false;
        
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);
        
        /// <summary>
        /// Loads the LibG dependencies first, then ProtoGeometry.dll
        /// </summary>
        private static void LoadLibGDependencies(List<string> diagnostics)
        {
            if (_dependenciesLoaded)
                return;
            
            try
            {
                diagnostics?.Add("Loading LibG dependencies from Downloads folder...");
                
                // Add the LibG directory to DLL search path for native DLLs
                try
                {
                    SetDllDirectory(DownloadsLibgDir);
                    diagnostics?.Add($"  ✓ Added {DownloadsLibgDir} to DLL search path");
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
                    var dllPath = Path.Combine(DownloadsLibgDir, dllName);
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
                        diagnostics?.Add($"  ⚠️ Not found: {dllName}");
                    }
                }
                
                // Note: LibGCore.dll and LibG.dll are native DLLs - they'll be loaded automatically
                diagnostics?.Add($"  Note: LibGCore.dll and LibG.dll are native DLLs (will be loaded on demand)");
                
                // Load LibG.Interface.dll
                var libgInterfacePath = Path.Combine(DownloadsRootDir, "LibG.Interface.dll");
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
                
                if (!File.Exists(NewProtoGeometryPath))
                {
                    diagnostics?.Add($"⚠️ New ProtoGeometry.dll not found at: {NewProtoGeometryPath}");
                    diagnostics?.Add($"   Falling back to default Geometry type");
                    return typeof(Geometry);
                }
                
                diagnostics?.Add($"Loading new ProtoGeometry.dll from: {NewProtoGeometryPath}");
                _newProtoGeometryAssembly = Assembly.LoadFrom(NewProtoGeometryPath);
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

            var elementDataModel = ReflectionHelper.InvokeMethodAsync<ElementDataModel>(
                client, 
                getElementDataModelMethod, 
                new object[] { identifier, CancellationToken.None },
                diagnostics);

                if (elementDataModel == null)
                {
                    throw new InvalidOperationException("ElementDataModel is null");
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
        /// Sets up DesignAsset and relationships with GeometryAsset
        /// </summary>
        private static void SetupDesignAssetAndRelationships(object element, object geometryAsset, Type geometryAssetType, object exchangeData, Type exchangeDataType, Dictionary<string, Type> foundTypes, string elementName, List<string> diagnostics)
        {
            var elementAssetProperty = element.GetType().GetProperty("Asset", BindingFlags.NonPublic | BindingFlags.Instance);
            if (elementAssetProperty == null)
            {
                throw new InvalidOperationException("Could not find Asset property on Element");
            }

            var elementAsset = elementAssetProperty.GetValue(element);
            var childNodesProperty = elementAsset.GetType().GetProperty("ChildNodes", BindingFlags.Public | BindingFlags.Instance);
            if (childNodesProperty == null)
            {
                return; // No child nodes support
            }

            var childNodes = childNodesProperty.GetValue(elementAsset) as System.Collections.IEnumerable;
            if (childNodes == null)
            {
                return;
            }

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
                return; // Can't create DesignAsset
            }

            object designAsset = null;
            foreach (var nodeRel in childNodes)
            {
                var nodeProperty = nodeRel.GetType().GetProperty("Node", BindingFlags.Public | BindingFlags.Instance);
                if (nodeProperty != null)
                {
                    var node = nodeProperty.GetValue(nodeRel);
                    if (node != null && designAssetType.IsAssignableFrom(node.GetType()))
                    {
                        designAsset = node;
                        break;
                    }
                }
            }

            if (designAsset == null)
            {
                designAsset = ReflectionHelper.CreateInstanceWithId(designAssetType, Guid.NewGuid().ToString(), foundTypes, diagnostics);

                // Set ObjectInfo on DesignAsset
                var designAssetObjectInfoProperty = designAssetType.GetProperty("ObjectInfo", BindingFlags.Public | BindingFlags.Instance);
                if (designAssetObjectInfoProperty != null)
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
                        designAssetObjectInfoProperty.SetValue(designAsset, objectInfo);
                    }
                }

                // Set units from element asset
                var designAssetLengthUnitProperty = designAssetType.GetProperty("LengthUnit", BindingFlags.Public | BindingFlags.Instance);
                var designAssetDisplayLengthUnitProperty = designAssetType.GetProperty("DisplayLengthUnit", BindingFlags.Public | BindingFlags.Instance);
                var elementAssetLengthUnitProperty = elementAsset.GetType().GetProperty("LengthUnit", BindingFlags.Public | BindingFlags.Instance);
                var elementAssetDisplayLengthUnitProperty = elementAsset.GetType().GetProperty("DisplayLengthUnit", BindingFlags.Public | BindingFlags.Instance);
                
                if (designAssetLengthUnitProperty != null && elementAssetLengthUnitProperty != null)
                {
                    var elementLengthUnit = elementAssetLengthUnitProperty.GetValue(elementAsset);
                    designAssetLengthUnitProperty.SetValue(designAsset, elementLengthUnit);
                }
                
                if (designAssetDisplayLengthUnitProperty != null && elementAssetDisplayLengthUnitProperty != null)
                {
                    var elementDisplayLengthUnit = elementAssetDisplayLengthUnitProperty.GetValue(elementAsset);
                    designAssetDisplayLengthUnitProperty.SetValue(designAsset, elementDisplayLengthUnit);
                }

                // Add DesignAsset to ExchangeData
                var exchangeDataAddMethodForDesign = exchangeDataType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
                if (exchangeDataAddMethodForDesign != null)
                {
                    exchangeDataAddMethodForDesign.Invoke(exchangeData, new object[] { designAsset });
                }

                // Add DesignAsset to element's asset with ReferenceRelationship
                var addChildMethod = elementAsset.GetType().GetMethod("AddChild", BindingFlags.Public | BindingFlags.Instance);
                if (addChildMethod != null)
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
                            }
                        }
                        addChildMethod.Invoke(elementAsset, new object[] { designAsset, referenceRelationship });
                        diagnostics.Add($"✓ Created and linked DesignAsset to element");
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
                    }
                    else
                    {
                        throw new InvalidOperationException("Could not find ContainmentRelationship type");
                    }
                }
            }
        }

        /// <summary>
        /// Creates AssetInfo for geometry upload
        /// </summary>
        private static (object assetInfo, Type assetInfoType) CreateAssetInfo(string geometryAssetId, string smbFilePath, List<string> diagnostics)
                            {
                                var assetInfoType = Type.GetType("Autodesk.GeometryUtilities.SDK.AssetInfo, Autodesk.GeometryUtilities");
                                if (assetInfoType == null)
                                {
                throw new InvalidOperationException("Could not find AssetInfo type");
                                }

                                    var assetInfo = Activator.CreateInstance(assetInfoType);
                                    var idProp = assetInfoType.GetProperty("Id");
                                    var outputPathProp = assetInfoType.GetProperty("OutputPath");
            var pathProp = assetInfoType.GetProperty("Path");
                                    var lengthUnitsProp = assetInfoType.GetProperty("LengthUnits");
                                    var bodyInfoListProp = assetInfoType.GetProperty("BodyInfoList");
                                    
                                    if (idProp != null)
                                    {
                                        idProp.SetValue(assetInfo, geometryAssetId);
                                    }
            
                                    if (outputPathProp != null)
                                    {
                                        outputPathProp.SetValue(assetInfo, smbFilePath);
                                        diagnostics.Add($"  Set AssetInfo.OutputPath: {smbFilePath}");
                                    }
                                    else if (pathProp != null)
                                    {
                                        pathProp.SetValue(assetInfo, smbFilePath);
                                        diagnostics.Add($"  Set AssetInfo.Path (OutputPath not found): {smbFilePath}");
                                    }
            
                                    if (lengthUnitsProp != null)
                                    {
                                        var unitEnumType = Type.GetType("Autodesk.DataExchange.SchemaObjects.Units.LengthUnit, Autodesk.DataExchange.SchemaObjects");
                                        if (unitEnumType != null)
                                        {
                                            var centimeterValue = Enum.Parse(unitEnumType, "CentiMeter");
                                            lengthUnitsProp.SetValue(assetInfo, centimeterValue);
                                        }
                                    }
                                    
            // Set BodyInfoList for BRep type
                                    if (bodyInfoListProp != null)
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
                                                    var brepValue = Enum.GetValues(bodyTypeEnum).Cast<object>().FirstOrDefault(v => v.ToString().Contains("BREP") || v.ToString().Contains("BRep"));
                                                    if (brepValue == null)
                                                    {
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
                                                        diagnostics.Add($"  Set AssetInfo.BodyInfoList with BRep type");
                                                    }
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            diagnostics.Add($"  ⚠️ Failed to set BodyInfoList: {ex.Message}");
                                        }
                                    }
                                    
                                    diagnostics.Add($"  Created AssetInfo with SMB path: {smbFilePath}");
            return (assetInfo, assetInfoType);
        }

        /// <summary>
        /// Starts fulfillment and returns fulfillmentId
        /// </summary>
        private static string StartFulfillmentAsync(object client, Type clientType, DataExchangeIdentifier identifier, List<string> diagnostics)
        {
            var startFulfillmentMethod = ReflectionHelper.GetMethod(
                clientType, 
                "StartFulfillmentAsync", 
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            
                                    if (startFulfillmentMethod == null)
                                    {
                                        diagnostics.Add($"  ⚠️ Could not find StartFulfillmentAsync method");
                return null;
                                    }
                                    
                                        try
                                        {
                                            var executionOrderType = Type.GetType("Autodesk.DataExchange.Core.Enums.ExecutionOrder, Autodesk.DataExchange.Core");
                                            var defaultExecutionOrder = executionOrderType != null ? Enum.GetValues(executionOrderType).GetValue(0) : null;
                                            
                                            object[] parameters;
                                            if (defaultExecutionOrder != null)
                                            {
                                                parameters = new object[] { identifier, defaultExecutionOrder };
                                            }
                                            else
                                            {
                                                var methodParams = startFulfillmentMethod.GetParameters();
                                                if (methodParams.Length == 1)
                                                {
                                                    parameters = new object[] { identifier };
                                                }
                                                else
                                                {
                                                    parameters = new object[] { identifier, CancellationToken.None };
                                                }
                                            }
                                            
                var fulfillmentTask = ReflectionHelper.InvokeMethod(client, startFulfillmentMethod, parameters, diagnostics);
                if (fulfillmentTask == null)
                                            {
                    diagnostics.Add($"  ⚠️ StartFulfillmentAsync returned null");
                    return null;
                }

                                                var fulfillmentResult = ((dynamic)fulfillmentTask).GetAwaiter().GetResult();
                                                
                                                if (fulfillmentResult is string fulfillmentIdString)
                                                {
                    diagnostics.Add($"  Started fulfillment: {fulfillmentIdString}");
                    return fulfillmentIdString;
                                                }
                
                if (fulfillmentResult != null)
                                                {
                                                    var resultType = fulfillmentResult.GetType();
                                                    var fulfillmentIdProp = resultType.GetProperty("Id");
                                                    if (fulfillmentIdProp != null)
                                                    {
                        var fulfillmentId = fulfillmentIdProp.GetValue(fulfillmentResult) as string;
                        if (fulfillmentId != null)
                        {
                                                        diagnostics.Add($"  Got fulfillmentId from Id property: {fulfillmentId}");
                            return fulfillmentId;
                                                    }
                    }
                    
                                                        var valueProp = resultType.GetProperty("Value");
                                                        if (valueProp != null)
                                                        {
                                                            var value = valueProp.GetValue(fulfillmentResult);
                                                            if (value is string valueString)
                                                            {
                            diagnostics.Add($"  Got fulfillmentId from Value property (string): {valueString}");
                            return valueString;
                                                            }
                                                            else if (value != null)
                                                            {
                                                                var valueIdProp = value.GetType().GetProperty("Id");
                                                                if (valueIdProp != null)
                                                                {
                                var fulfillmentId = valueIdProp.GetValue(value) as string;
                                if (fulfillmentId != null)
                                {
                                                                    diagnostics.Add($"  Got fulfillmentId from Value.Id property: {fulfillmentId}");
                                    return fulfillmentId;
                                }
                                                                }
                                                            }
                                                        }
                                                        
                                                            try
                                                            {
                        var fulfillmentId = fulfillmentResult.ToString();
                                                                diagnostics.Add($"  Using fulfillmentId from ToString(): {fulfillmentId}");
                        return fulfillmentId;
                                                            }
                                                            catch { }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            diagnostics.Add($"  ⚠️ StartFulfillmentAsync failed: {ex.Message}");
                                            diagnostics.Add($"  Stack: {ex.StackTrace}");
                                        }
            
            return null;
        }

        /// <summary>
        /// Uploads geometries using UploadGeometries method
        /// </summary>
        private static void UploadGeometriesAsync(object client, Type clientType, DataExchangeIdentifier identifier, string fulfillmentId, object assetInfo, Type assetInfoType, object exchangeData, Type exchangeDataType, object geometryAsset, Type geometryAssetType, string geometryAssetId, List<string> diagnostics)
        {
            var uploadGeometriesMethod = ReflectionHelper.GetMethod(
                clientType, 
                "UploadGeometries", 
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            
                                        if (uploadGeometriesMethod == null)
                                        {
                                            diagnostics.Add($"  ⚠️ Could not find UploadGeometries method");
                return;
                                        }

                                            try
                                            {
                                                var assetInfoListType = typeof(System.Collections.Generic.List<>).MakeGenericType(assetInfoType);
                                                var assetInfoList = Activator.CreateInstance(assetInfoListType);
                                                var addMethod = assetInfoListType.GetMethod("Add");
                                                if (addMethod != null)
                                                {
                                                    addMethod.Invoke(assetInfoList, new object[] { assetInfo });
                                                    
                                                    var uploadTask = uploadGeometriesMethod.Invoke(client, new object[] { identifier, fulfillmentId, assetInfoList, exchangeData, CancellationToken.None });
                                                    if (uploadTask != null)
                                                    {
                                                        try
                                                        {
                                                            ((dynamic)uploadTask).GetAwaiter().GetResult();
                                                            diagnostics.Add($"  ✓ Uploaded SMB geometry directly");
                                                            
                            // Check if BinaryReference was set
                                                            var localBinaryRefProp = geometryAssetType.GetProperty("BinaryReference", BindingFlags.Public | BindingFlags.Instance);
                                                            if (localBinaryRefProp != null)
                                                            {
                                                                var localBinaryRef = localBinaryRefProp.GetValue(geometryAsset);
                                                                if (localBinaryRef != null)
                                                                {
                                                                    diagnostics.Add($"  ✓ BinaryReference WAS set on our local GeometryAsset after UploadGeometries");
                                                                }
                                                                else
                                                                {
                                    diagnostics.Add($"  ⚠️ BinaryReference NOT set on our local GeometryAsset");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            diagnostics.Add($"  ⚠️ UploadGeometries threw exception: {ex.Message}");
                            throw;
                        }
                                                                }
                                                            }
                                                        }
                                                        catch (Exception ex)
                                                        {
                diagnostics.Add($"  ⚠️ UploadGeometries failed: {ex.Message}");
                diagnostics.Add($"  Stack: {ex.StackTrace}");
                                                            throw;
            }
        }

        /// <summary>
        /// Finishes fulfillment
        /// </summary>
        private static void FinishFulfillmentAsync(object client, Type clientType, DataExchangeIdentifier identifier, string fulfillmentId, List<string> diagnostics)
        {
            // Try API method first
            var getAPIMethod = ReflectionHelper.GetMethod(
                clientType, 
                "GetAPI", 
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            
                                                        if (getAPIMethod != null)
                                                        {
                                                            try
                                                            {
                    var api = ReflectionHelper.InvokeMethod(client, getAPIMethod, null, diagnostics);
                                                                if (api != null)
                                                                {
                                                                    var apiType = api.GetType();
                                                                    diagnostics.Add($"  Got API type: {apiType.FullName}");
                        
                        var apiFinishMethod = ReflectionHelper.GetMethod(
                            apiType, 
                            "FinishFulfillmentAsync", 
                                                                        BindingFlags.Public | BindingFlags.Instance,
                            new[] { typeof(string), typeof(string), typeof(string) });
                        
                        if (apiFinishMethod != null)
                                                                    {
                                                                        var finishParams = apiFinishMethod.GetParameters();
                                                                        diagnostics.Add($"  FinishFulfillmentAsync has {finishParams.Length} parameter(s)");
                                                                        if (finishParams.Length == 3)
                                                                        {
                                                                            try
                                                                            {
                                    var finishTask = ReflectionHelper.InvokeMethod(api, apiFinishMethod, new object[] { identifier.CollectionId, identifier.ExchangeId, fulfillmentId }, diagnostics);
                                    if (finishTask != null)
                                                                                {
                                                                                    ((dynamic)finishTask).GetAwaiter().GetResult();
                                                                                    diagnostics.Add($"  ✓ Finished fulfillment via API");
                                        return;
                                                                                }
                                                                            }
                                                                            catch (Exception ex)
                                                                            {
                                                                                diagnostics.Add($"  ⚠️ FinishFulfillmentAsync invocation failed: {ex.Message}");
                                                                                }
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                            catch (Exception ex)
                                                            {
                                                                diagnostics.Add($"  ⚠️ GetAPI invocation failed: {ex.Message}");
                }
            }
            
            // Fallback: try Client's internal method
            var finishFulfillmentMethod = ReflectionHelper.GetMethod(
                clientType, 
                "FinishFulfillmentAsync", 
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            
            if (finishFulfillmentMethod != null)
                                                            {
                                                                try
                                                                {
                                                                    var finishParams = finishFulfillmentMethod.GetParameters();
                                                                    diagnostics.Add($"  Client FinishFulfillmentAsync has {finishParams.Length} parameter(s)");
                                                                    if (finishParams.Length == 2)
                                                                    {
                        var finishTask = ReflectionHelper.InvokeMethod(client, finishFulfillmentMethod, new object[] { identifier, fulfillmentId }, diagnostics);
                        if (finishTask != null)
                                                                        {
                                                                            ((dynamic)finishTask).GetAwaiter().GetResult();
                                                                            diagnostics.Add($"  ✓ Finished fulfillment via Client");
                                                                        }
                                                                    }
                                                                }
                                                                catch (Exception ex)
                                                                {
                                                                    diagnostics.Add($"  ⚠️ Client FinishFulfillmentAsync failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Refreshes ElementDataModel to get updated BinaryReference
        /// </summary>
        private static void RefreshElementDataModelAsync(object client, Type clientType, DataExchangeIdentifier identifier, string geometryAssetId, FieldInfo exchangeDataField, object geometryAsset, Type geometryAssetType, List<string> diagnostics)
        {
                                                        diagnostics.Add($"  Refreshing ElementDataModel to get BinaryReference...");
                                                        try
                                                        {
                var refreshMethod = ReflectionHelper.GetMethod(
                    clientType, 
                    "GetElementDataModelAsync", 
                                                                BindingFlags.Public | BindingFlags.Instance,
                    new[] { typeof(DataExchangeIdentifier), typeof(CancellationToken) });
                                                            
                                                            if (refreshMethod == null)
                                                            {
                                                                diagnostics.Add($"  ⚠️ Could not find GetElementDataModelAsync with 2 parameters, cannot refresh");
                    return;
                                                            }

                var refreshTask = ReflectionHelper.InvokeMethod(client, refreshMethod, new object[] { identifier, CancellationToken.None }, diagnostics);
                                                                if (refreshTask == null)
                                                                {
                                                                    diagnostics.Add($"  ⚠️ GetElementDataModelAsync returned null");
                    return;
                                                                }

                                                                    try
                                                                    {
                                                                        var refreshResult = ((dynamic)refreshTask).GetAwaiter().GetResult();
                                                                        if (refreshResult == null)
                                                                        {
                                                                            diagnostics.Add($"  ⚠️ Refresh task result is null");
                        return;
                                                                        }

                                                                            var refreshValueProp = refreshResult.GetType().GetProperty("Value");
                                                                            if (refreshValueProp == null)
                                                                            {
                                                                                diagnostics.Add($"  ⚠️ Refresh result has no Value property. Type: {refreshResult.GetType().FullName}");
                        return;
                                                                            }

                                                                                var refreshedModel = refreshValueProp.GetValue(refreshResult) as ElementDataModel;
                                                                                if (refreshedModel == null)
                                                                                {
                                                                                    diagnostics.Add($"  ⚠️ Refreshed model is null");
                        return;
                                                                                }

                                                                                    var refreshedExchangeData = exchangeDataField.GetValue(refreshedModel);
                                                                                    if (refreshedExchangeData == null)
                                                                                    {
                                                                                        diagnostics.Add($"  ⚠️ Refreshed ExchangeData is null");
                        return;
                    }

                    var refreshedExchangeDataType = refreshedExchangeData.GetType();
                    var getAssetByIdMethod = refreshedExchangeDataType.GetMethod("GetAssetById", BindingFlags.Public | BindingFlags.Instance);
                                                                                        if (getAssetByIdMethod == null)
                                                                                        {
                                                                                            diagnostics.Add($"  ⚠️ Could not find GetAssetById on refreshed ExchangeData");
                        return;
                                                                                        }

                                                                                            var refreshedAsset = getAssetByIdMethod.Invoke(refreshedExchangeData, new object[] { geometryAssetId });
                                                                                            if (refreshedAsset == null)
                                                                                            {
                                                                                                diagnostics.Add($"  ⚠️ Could not find our GeometryAsset (ID: {geometryAssetId}) in refreshed ExchangeData");
                        return;
                                                                                            }

                                                                                                var refreshedBinaryRefProp = refreshedAsset.GetType().GetProperty("BinaryReference", BindingFlags.Public | BindingFlags.Instance);
                                                                                                if (refreshedBinaryRefProp == null)
                                                                                                {
                                                                                                    diagnostics.Add($"  ⚠️ Refreshed asset has no BinaryReference property");
                        return;
                                                                                                }

                                                                                                    var refreshedBinaryRef = refreshedBinaryRefProp.GetValue(refreshedAsset);
                                                                                                    if (refreshedBinaryRef == null)
                                                                                                    {
                                                                                                        diagnostics.Add($"  ⚠️ Refreshed asset's BinaryReference is still null");
                        return;
                                                                                                    }

                                                                                                        // Copy BinaryReference to our local GeometryAsset
                                                                                                        var localBinaryRefProp = geometryAssetType.GetProperty("BinaryReference", BindingFlags.Public | BindingFlags.Instance);
                                                                                                        if (localBinaryRefProp == null)
                                                                                                        {
                                                                                                            diagnostics.Add($"  ⚠️ Local GeometryAsset has no BinaryReference property");
                        return;
                                                                                                        }

                    if (!localBinaryRefProp.CanWrite)
                                                                                                        {
                                                                                                            diagnostics.Add($"  ⚠️ Local BinaryReference property is read-only");
                        return;
                                                                                                        }

                                                                                                            localBinaryRefProp.SetValue(geometryAsset, refreshedBinaryRef);
                                                                                                            diagnostics.Add($"  ✓ Copied BinaryReference from refreshed asset");
                                                                                                        }
                catch (Exception ex)
                {
                    diagnostics.Add($"  ⚠️ Refresh failed: {ex.Message}");
                    diagnostics.Add($"  Stack: {ex.StackTrace}");
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add($"  ⚠️ Error during refresh: {ex.Message}");
                diagnostics.Add($"  Stack: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Polls for fulfillment completion
        /// </summary>
        private static void PollForFulfillmentAsync(object client, Type clientType, DataExchangeIdentifier identifier, string fulfillmentId, List<string> diagnostics)
        {
            if (string.IsNullOrEmpty(fulfillmentId))
            {
                return;
            }

            diagnostics.Add($"\nPolling for fulfillment completion...");
            try
            {
                var pollForFulfillmentMethod = ReflectionHelper.GetMethod(
                    clientType, 
                    "PollForFulfillment", 
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                
                if (pollForFulfillmentMethod == null)
                {
                    diagnostics.Add($"  ⚠️ Could not find PollForFulfillment method");
                    return;
                }

                var pollParams = pollForFulfillmentMethod.GetParameters();
                diagnostics.Add($"  PollForFulfillment has {pollParams.Length} parameter(s)");
                if (pollParams.Length == 2)
                {
                    var pollTask = ReflectionHelper.InvokeMethod(client, pollForFulfillmentMethod, new object[] { identifier, fulfillmentId }, diagnostics);
                    if (pollTask != null)
                    {
                        ((dynamic)pollTask).GetAwaiter().GetResult();
                        diagnostics.Add($"  ✓ Fulfillment completed");
                    }
                }
                else
                {
                    diagnostics.Add($"  ⚠️ Unexpected parameter count: {pollParams.Length}, expected 2");
                }
                                                                    }
                                                                    catch (Exception ex)
                                                                    {
                diagnostics.Add($"  ⚠️ PollForFulfillment failed: {ex.Message}");
                                                                        diagnostics.Add($"  Stack: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Syncs schema to make assets visible and persistent
        /// </summary>
        private static bool SyncSchemaAsync(object client, Type clientType, DataExchangeIdentifier identifier, ElementDataModel elementDataModel, object geometryAsset, Type geometryAssetType, List<string> diagnostics)
        {
            diagnostics.Add($"\nSyncing schema to make GeometryAsset visible and persistent...");
            try
            {
                // Ensure GeometryAsset has CreatedLocal status
                var statusProp = geometryAssetType.GetProperty("Status", BindingFlags.Public | BindingFlags.Instance);
                if (statusProp != null)
                {
                    var statusType = Type.GetType("Autodesk.DataExchange.Core.Enums.Status, Autodesk.DataExchange.Core");
                    if (statusType != null)
                    {
                        var createdLocalValue = Enum.Parse(statusType, "CreatedLocal");
                        var currentStatus = statusProp.GetValue(geometryAsset);
                        if (currentStatus == null || !currentStatus.Equals(createdLocalValue))
                        {
                            var setStatusMethod = geometryAssetType.GetMethod("SetStatus", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (setStatusMethod == null)
                            {
                                var baseType = geometryAssetType.BaseType;
                                if (baseType != null)
                                {
                                    setStatusMethod = baseType.GetMethod("SetStatus", BindingFlags.NonPublic | BindingFlags.Instance);
                                }
                            }
                            if (setStatusMethod != null)
                            {
                                setStatusMethod.Invoke(geometryAsset, new object[] { createdLocalValue });
                                diagnostics.Add($"  ✓ Set GeometryAsset status to CreatedLocal");
                            }
                        }
                    }
                }
                
                // Call SyncExchangeDataAsync
                var syncMethod = ReflectionHelper.GetMethod(
                    clientType, 
                    "SyncExchangeDataAsync", 
                    BindingFlags.Public | BindingFlags.Instance,
                    new[] { typeof(DataExchangeIdentifier), typeof(ElementDataModel), typeof(CancellationToken) });
                
                if (syncMethod == null)
                {
                    diagnostics.Add($"  ⚠️ Could not find SyncExchangeDataAsync method");
                    return false;
                }

                diagnostics.Add($"  Calling SyncExchangeDataAsync to sync schema...");
                var syncTask = ReflectionHelper.InvokeMethod(client, syncMethod, new object[] { identifier, elementDataModel, CancellationToken.None }, diagnostics);
                if (syncTask == null)
                {
                    diagnostics.Add($"  ⚠️ SyncExchangeDataAsync returned null");
                    return false;
                }

                try
                {
                    var syncResult = ((dynamic)syncTask).GetAwaiter().GetResult();
                    if (syncResult == null)
                    {
                        diagnostics.Add($"  ⚠️ Sync task result is null");
                        return false;
                    }

                    var syncResponseType = syncResult.GetType();
                    var isSuccessProp = syncResponseType.GetProperty("IsSuccess") ?? syncResponseType.GetProperty("Success");
                    if (isSuccessProp != null)
                    {
                        var isSuccess = (bool)isSuccessProp.GetValue(syncResult);
                        if (isSuccess)
                        {
                            diagnostics.Add($"  ✓ Schema sync completed successfully");
                            return true;
                                                        }
                                                        else
                                                        {
                            diagnostics.Add($"  ✗ Schema sync failed");
                            var syncErrorProp = syncResponseType.GetProperty("Error");
                            if (syncErrorProp != null)
                            {
                                var syncError = syncErrorProp.GetValue(syncResult);
                                diagnostics.Add($"  Error: {syncError}");
                            }
                            return false;
                                                        }
                                                    }
                                                    else
                                                    {
                        diagnostics.Add($"  ⚠️ Could not find IsSuccess property on sync response");
                        diagnostics.Add($"  Response type: {syncResponseType.FullName}");
                        return false;
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                    diagnostics.Add($"  ⚠️ SyncExchangeDataAsync failed: {ex.Message}");
                                                diagnostics.Add($"  Stack: {ex.StackTrace}");
                    if (ex.InnerException != null)
                                    {
                        diagnostics.Add($"  Inner: {ex.InnerException.Message}");
                                    }
                    return false;
                                }
                            }
                            catch (Exception ex)
                            {
                diagnostics.Add($"  ⚠️ Error during schema sync: {ex.Message}");
                                diagnostics.Add($"  Stack: {ex.StackTrace}");
                return false;
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

                // Setup DesignAsset and relationships
                SetupDesignAssetAndRelationships(element, geometryAsset, geometryAssetType, exchangeData, exchangeDataType, foundTypes, elementName, diagnostics);

                // Upload flow
                diagnostics.Add("\nUploading SMB geometry directly...");
                string fulfillmentId = null;
                
                // Create AssetInfo
                var (assetInfo, assetInfoType) = CreateAssetInfo(geometryAssetId, smbFilePath, diagnostics);
                
                if (assetInfo != null)
                {
                    // Start fulfillment
                    fulfillmentId = StartFulfillmentAsync(client, clientType, identifier, diagnostics);
                    
                    if (fulfillmentId != null)
                    {
                        // Upload geometries
                        UploadGeometriesAsync(client, clientType, identifier, fulfillmentId, assetInfo, assetInfoType, exchangeData, exchangeDataType, geometryAsset, geometryAssetType, geometryAssetId, diagnostics);
                        
                        // Finish fulfillment
                        FinishFulfillmentAsync(client, clientType, identifier, fulfillmentId, diagnostics);
                        
                        // Refresh to get BinaryReference
                        RefreshElementDataModelAsync(client, clientType, identifier, geometryAssetId, exchangeDataField, geometryAsset, geometryAssetType, diagnostics);
                        
                        // Check if BinaryReference was set
                        var binaryRefProp = geometryAssetType.GetProperty("BinaryReference", BindingFlags.Public | BindingFlags.Instance);
                        if (binaryRefProp != null)
                        {
                            var binaryRef = binaryRefProp.GetValue(geometryAsset);
                            if (binaryRef != null)
                            {
                                diagnostics.Add($"  ✓ BinaryReference is now set");
                                success = true;
                            }
                            else
                            {
                                diagnostics.Add($"  ⚠️ BinaryReference still null - geometry uploaded but not linked");
                                success = true;
                            }
                        }
                        else
                        {
                            success = true;
                        }
                    }
                    else
                    {
                        diagnostics.Add($"  ⚠️ Cannot upload - no fulfillmentId");
                    }
                }
                
                // Poll for fulfillment completion
                PollForFulfillmentAsync(client, clientType, identifier, fulfillmentId, diagnostics);
                
                // Sync schema
                if (SyncSchemaAsync(client, clientType, identifier, elementDataModel, geometryAsset, geometryAssetType, diagnostics))
                {
                    success = true;
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
