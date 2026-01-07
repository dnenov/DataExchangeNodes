using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.DesignScript.Geometry;
using Autodesk.DesignScript.Runtime;

// Required for IntPtr and ACIS pointer operations
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Load geometry from a DataExchange using ProtoGeometry SMB file APIs
    /// </summary>
    public static class LoadGeometryFromExchange
    {
        // Singleton pattern - auth provider registered by SelectExchangeElements view
        private static Func<string> _getTokenFunc = null;
        
        // Paths to the new DLLs from colleague
        private static readonly string DownloadsRootDir = @"C:\Users\nenovd\Downloads\Dynamo\RootDir";
        private static readonly string DownloadsLibgDir = @"C:\Users\nenovd\Downloads\Dynamo\Libg_231_0_0";
        private static readonly string NewProtoGeometryPath = Path.Combine(DownloadsRootDir, "ProtoGeometry.dll");
        private static Assembly _newProtoGeometryAssembly = null;
        private static Type _geometryTypeFromNewAssembly = null;
        private static bool _dependenciesLoaded = false;
        
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);
        
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int AddDllDirectory(string lpPathName);
        
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
                // when the managed code references them, as long as they're in the DLL search path
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
        /// Registers the token provider function (called by SelectExchangeElements view)
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public static void RegisterAuthProvider(Func<string> getTokenFunc)
        {
            _getTokenFunc = getTokenFunc;
        }

        /// <summary>
        /// Standalone test node: Loads geometry from an SMB file on disk.
        /// Use this to test if SMB files work with the LibG implementation.
        /// </summary>
        /// <param name="smbFilePath">Full path to the SMB file on disk</param>
        /// <param name="unit">Unit type for geometry (default: "kUnitType_CentiMeter"). Options: kUnitType_CentiMeter, kUnitType_Meter, kUnitType_Feet, kUnitType_Inch</param>
        /// <returns>Dictionary with "geometries" (list of Dynamo geometry), "log" (diagnostic messages), and "success" (boolean)</returns>
        [MultiReturn(new[] { "geometries", "log", "success" })]
        public static Dictionary<string, object> ImportSMBFileFromPath(
            string smbFilePath,
            string unit = "kUnitType_CentiMeter")
        {
            var geometries = new List<Geometry>();
            var diagnostics = new List<string>();
            bool success = false;

            try
            {
                diagnostics.Add("=== Standalone SMB File Import Test ===");
                diagnostics.Add($"SMB File Path: {smbFilePath}");
                diagnostics.Add($"Unit: {unit}");

                if (string.IsNullOrEmpty(smbFilePath))
                {
                    throw new ArgumentNullException(nameof(smbFilePath), "SMB file path cannot be null or empty");
                }

                if (!File.Exists(smbFilePath))
                {
                    throw new FileNotFoundException($"SMB file not found: {smbFilePath}");
                }

                var fileInfo = new FileInfo(smbFilePath);
                diagnostics.Add($"File exists: ✓");
                diagnostics.Add($"File size: {fileInfo.Length} bytes");
                diagnostics.Add($"File extension: {fileInfo.Extension}");
                
                // Normalize the path
                var normalizedPath = Path.GetFullPath(smbFilePath);
                diagnostics.Add($"Original path: {smbFilePath}");
                diagnostics.Add($"Normalized path: {normalizedPath}");
                
                // Check if path has spaces or special characters
                if (normalizedPath.Contains(" "))
                {
                    diagnostics.Add($"⚠️ Path contains spaces - copying to temp location without spaces");
                    // Copy to temp directory without spaces
                    var tempDir = Path.Combine(Path.GetTempPath(), "DataExchangeNodes");
                    if (!Directory.Exists(tempDir))
                        Directory.CreateDirectory(tempDir);
                    
                    var tempFileName = Path.GetFileName(smbFilePath);
                    var tempPath = Path.Combine(tempDir, tempFileName);
                    File.Copy(normalizedPath, tempPath, overwrite: true);
                    smbFilePath = tempPath;
                    diagnostics.Add($"Copied to: {smbFilePath}");
                }
                else
                {
                    smbFilePath = normalizedPath;
                }
                
                // Convert unit string to mmPerUnit (millimeters per unit)
                double mmPerUnit = 10.0; // Default to cm
                if (unit.Contains("Meter") && !unit.Contains("Centi"))
                    mmPerUnit = 1000.0;
                else if (unit.Contains("CentiMeter") || unit.Contains("cm"))
                    mmPerUnit = 10.0;
                else if (unit.Contains("Feet") || unit.Contains("ft"))
                    mmPerUnit = 304.8;
                else if (unit.Contains("Inch") || unit.Contains("in"))
                    mmPerUnit = 25.4;
                
                diagnostics.Add($"mmPerUnit: {mmPerUnit} (for unit: {unit})");
                
                // Get Geometry type from the new ProtoGeometry.dll (not the NuGet one)
                var geometryType = GetGeometryTypeFromNewAssembly(diagnostics);
                
                // Try to find all ImportFromSMB methods
                var allImportMethods = geometryType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Where(m => m.Name == "ImportFromSMB")
                    .ToList();
                
                diagnostics.Add($"Found {allImportMethods.Count} ImportFromSMB method(s):");
                foreach (var method in allImportMethods)
                {
                    var paramInfo = method.GetParameters();
                    var paramStr = string.Join(", ", paramInfo.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    var visibility = method.IsPublic ? "public" : "internal";
                    diagnostics.Add($"  [{visibility}] {method.ReturnType.Name} ImportFromSMB({paramStr})");
                }
                
                // Try the public method first
                var importMethod = allImportMethods.FirstOrDefault(m => 
                    m.IsPublic && 
                    m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType == typeof(string) &&
                    m.GetParameters()[1].ParameterType == typeof(double));
                
                Geometry[] result = null;
                
                if (importMethod != null)
                {
                    diagnostics.Add($"Trying public method: ImportFromSMB(String, Double)...");
                    try
                    {
                        result = importMethod.Invoke(null, new object[] { smbFilePath, mmPerUnit }) as Geometry[];
                        if (result != null && result.Length > 0)
                        {
                            diagnostics.Add($"✓ Public method succeeded");
                        }
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add($"⚠️ Public method failed: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }
                
                // If public method failed or returned empty, try the internal method with ref parameter
                if (result == null || result.Length == 0)
                {
                    var internalMethod = allImportMethods.FirstOrDefault(m => 
                        !m.IsPublic && 
                        m.GetParameters().Length == 2 &&
                        m.GetParameters()[0].ParameterType.IsByRef &&
                        m.GetParameters()[1].ParameterType == typeof(double));
                    
                    if (internalMethod != null)
                    {
                        diagnostics.Add($"Trying internal method with ref parameter...");
                        try
                        {
                            // For ref parameters, we need to pass a boxed string
                            var fileNameParam = smbFilePath;
                            var paramArray = new object[] { fileNameParam, mmPerUnit };
                            var invokeResult = internalMethod.Invoke(null, paramArray);
                            
                            // The internal method returns IGeometryEntity[], need to convert
                            if (invokeResult != null)
                            {
                                var entityArray = invokeResult as System.Collections.IEnumerable;
                                if (entityArray != null)
                                {
                                    result = new List<Geometry>().ToArray();
                                    foreach (var entity in entityArray)
                                    {
                                        // Try to cast to Geometry
                                        if (entity is Geometry geo)
                                        {
                                            var list = result.ToList();
                                            list.Add(geo);
                                            result = list.ToArray();
                                        }
                                    }
                                    if (result.Length > 0)
                                    {
                                        diagnostics.Add($"✓ Internal method succeeded");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            diagnostics.Add($"⚠️ Internal method failed: {ex.InnerException?.Message ?? ex.Message}");
                        }
                    }
                }
                
                if (result == null)
                {
                    throw new InvalidOperationException($"All ImportFromSMB methods failed. Check diagnostics for details.");
                }
                
                if (result != null && result.Length > 0)
                {
                    geometries.AddRange(result);
                    diagnostics.Add($"✓ Successfully loaded {geometries.Count} geometry object(s) from SMB file");
                    success = true;
                }
                else if (result != null && result.Length == 0)
                {
                    diagnostics.Add($"⚠️ ImportFromSMB returned empty array - no geometries loaded");
                }
                else
                {
                    diagnostics.Add($"⚠️ ImportFromSMB returned null - no geometries loaded");
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add($"\n✗ ERROR: {ex.GetType().Name}: {ex.Message}");
                diagnostics.Add($"Stack: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    diagnostics.Add($"Inner Exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                    if (ex.InnerException.StackTrace != null)
                    {
                        diagnostics.Add($"Inner Stack: {ex.InnerException.StackTrace}");
                    }
                }
            }

            return new Dictionary<string, object>
            {
                { "geometries", geometries },
                { "log", string.Join("\n", diagnostics) },
                { "success", success }
            };
        }

        /// <summary>
        /// Loads geometry from a DataExchange as Dynamo geometry objects.
        /// Downloads SMB files from DataExchange and reads them using ProtoGeometry APIs.
        /// Authentication is handled automatically using Dynamo's login - no token required!
        /// </summary>
        /// <param name="exchange">Exchange object from SelectExchange node</param>
        /// <param name="unit">Unit type for geometry (default: "kUnitType_CentiMeter"). Options: kUnitType_CentiMeter, kUnitType_Meter, kUnitType_Feet, kUnitType_Inch</param>
        /// <returns>Dictionary with "geometries" (list of Dynamo geometry), "log" (diagnostic messages), and "success" (boolean)</returns>
        [MultiReturn(new[] { "geometries", "log", "success" })]
        public static Dictionary<string, object> Load(
            Exchange exchange,
            string unit = "kUnitType_CentiMeter")
        {
            var geometries = new List<Geometry>();
            var diagnostics = new List<string>();
            bool success = false;

            try
            {
                diagnostics.Add("=== DataExchange Geometry Loader (ProtoGeometry SMB) ===");
                diagnostics.Add($"Exchange: {exchange?.ExchangeTitle ?? "null"}");
                diagnostics.Add($"Unit: {unit}");

                // Validate inputs
                if (exchange == null)
                    throw new ArgumentNullException(nameof(exchange), "Exchange cannot be null");

                if (string.IsNullOrEmpty(exchange.ExchangeId))
                    throw new ArgumentException("ExchangeId is required", nameof(exchange));

                if (string.IsNullOrEmpty(exchange.CollectionId))
                    throw new ArgumentException("CollectionId is required", nameof(exchange));

                // Get token from registered auth provider
                if (_getTokenFunc == null)
                    throw new InvalidOperationException("Authentication not configured. Please use SelectExchange node first to log in.");

                var accessToken = _getTokenFunc();
                if (string.IsNullOrEmpty(accessToken))
                    throw new InvalidOperationException("Not logged in. Please log in to Dynamo first.");

                diagnostics.Add($"✓ Authenticated (token length: {accessToken.Length} chars)");

                // Download SMB file from DataExchange
                // Note: Using .Result blocks the thread, but Dynamo ZeroTouch nodes are synchronous
                // This is acceptable for now as Dynamo will handle the blocking appropriately
                var smbFilePath = DownloadSMBFile(exchange, accessToken, diagnostics).GetAwaiter().GetResult();
                diagnostics.Add($"✓ Downloaded SMB file: {smbFilePath}");

                // Load geometry using ProtoGeometry SMB APIs
                geometries = LoadGeometryFromSMB(smbFilePath, unit, diagnostics);
                diagnostics.Add($"✓ Loaded {geometries.Count} geometry object(s)");

                success = geometries.Count > 0;
                diagnostics.Add($"\n=== Load Completed: {geometries.Count} geometries ===");
            }
            catch (Exception ex)
            {
                diagnostics.Add($"\nERROR: {ex.Message}");
                diagnostics.Add($"Type: {ex.GetType().Name}");
                diagnostics.Add($"Stack: {ex.StackTrace}");

                if (ex.InnerException != null)
                {
                    diagnostics.Add($"Inner: {ex.InnerException.Message}");
                }
            }

            return new Dictionary<string, object>
            {
                { "geometries", geometries },
                { "log", string.Join("\n", diagnostics) },
                { "success", success }
            };
        }

        /// <summary>
        /// Downloads SMB file from DataExchange API
        /// First tries to use internal Client SDK methods via reflection, then falls back to HTTP API
        /// </summary>
        private static async Task<string> DownloadSMBFile(
            Exchange exchange,
            string accessToken,
            List<string> diagnostics)
        {
            try
            {
                // Create temp directory for SMB file
                var tempDir = Path.Combine(Path.GetTempPath(), "DataExchangeNodes");
                if (!Directory.Exists(tempDir))
                    Directory.CreateDirectory(tempDir);

                var smbFilePath = Path.Combine(tempDir, $"Exchange_{exchange.ExchangeId}_{exchange.CollectionId}.smb");

                // First, try to use the DataExchange Client SDK via reflection to access internal/private methods
                diagnostics.Add("  Attempting to use DataExchange Client SDK (reflection)...");
                var clientDownloadResult = await TryDownloadSMBViaClientReflection(exchange, smbFilePath, diagnostics);
                
                if (clientDownloadResult)
                {
                    if (File.Exists(smbFilePath))
                    {
                        diagnostics.Add($"  ✓ Downloaded via Client SDK: {new FileInfo(smbFilePath).Length} bytes");
                        return smbFilePath;
                    }
                    else
                    {
                        diagnostics.Add($"  ⚠️ Client SDK reported success but file not found: {smbFilePath}");
                    }
                }

                // Fallback to HTTP API if Client SDK method not found or failed
                // NOTE: HTTP API fallback is disabled as it doesn't work - need to fix GetBinaryAssetDownloadInfoAsync instead
                //diagnostics.Add("  Falling back to HTTP API download...");
                //await DownloadSMBViaHttpAPI(exchange, accessToken, smbFilePath, diagnostics);
                
                // Verify file was actually downloaded
                if (!File.Exists(smbFilePath))
                {
                    throw new IOException($"SMB file was not downloaded via Client SDK. GetBinaryAssetDownloadInfoAsync needs proper BinaryDownloadBatchRequest structure.");
                }
                
                return smbFilePath;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"✗ Failed to download SMB file: {ex.Message}");
                throw new IOException($"Failed to download SMB file from DataExchange: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Attempts to download SMB file using DataExchange Client SDK via reflection
        /// Inspects internal/private methods to find SMB download functionality
        /// </summary>
        private static async Task<bool> TryDownloadSMBViaClientReflection(
            Exchange exchange,
            string smbFilePath,
            List<string> diagnostics)
        {
            try
            {
                // Get Client instance
                var client = TryGetClientInstance(diagnostics);
                if (client == null)
                {
                    diagnostics.Add("  ⚠️ Client instance is null - ensure SelectExchangeElements node has been used first");
                    diagnostics.Add("  This is expected if the SelectExchangeElements node hasn't been opened yet");
                    return false;
                }

                diagnostics.Add($"  ✓ Found Client instance: {client.GetType().FullName}");

                // Inspect all methods (public, private, internal, protected, static, instance)
                var clientType = client.GetType();
                var allMethods = clientType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | 
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy);

                // Try each download method in sequence
                // Approach B (Parked): GetBinaryAssetDownloadInfoAsync requires binary asset IDs from ElementDataModel first
                //if (await TryDownloadViaBinaryAssetDownloadInfoAsync(client, exchange, smbFilePath, allMethods, diagnostics))
                //    return true;

                // Approach A (WRONG - returns STEP, not SMB): GetElementDataModel → GetElementGeometriesAsync 
                // This returns STEP files, not SMB files. Disabled.
                //if (await TryDownloadViaElementDataModelAsync(client, exchange, smbFilePath, allMethods, diagnostics))
                //    return true;

                //if (await TryDownloadViaGetAllAssetInfosWithTranslatedGeometryPath(client, exchange, smbFilePath, allMethods, diagnostics))
                //    return true;

                // Look for direct SMB download methods
                var candidateMethods = allMethods.Where(m =>
                {
                    var name = m.Name.ToLowerInvariant();
                    return name.Contains("smb") || 
                           (name.Contains("download") && !name.Contains("step")) || 
                           name.Contains("getfile") ||
                           (name.Contains("format") && name.Contains("smb")) ||
                           (name.Contains("convert") && name.Contains("smb"));
                }).ToList();

                var smbDownloadMethod = candidateMethods.FirstOrDefault(m =>
                {
                    var parameters = m.GetParameters();
                    var returnType = m.ReturnType;
                    
                    bool hasExchangeParams = parameters.Any(p => 
                        p.ParameterType.Name.Contains("Exchange") || 
                        p.Name.ToLowerInvariant().Contains("exchange") ||
                        p.Name.ToLowerInvariant().Contains("collection"));
                    
                    bool returnsPath = returnType == typeof(string) || 
                                      returnType == typeof(Task<string>) ||
                                      (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>));
                    
                    return hasExchangeParams && (m.Name.ToLowerInvariant().Contains("smb") || 
                                                m.Name.ToLowerInvariant().Contains("download"));
                });

                //if (await TryDownloadViaDirectSmbMethod(client, exchange, smbFilePath, smbDownloadMethod, diagnostics))
                //    return true;

                if (await TryDownloadViaParseGeometryAssetBinaryToIntermediateGeometry(client, exchange, smbFilePath, allMethods, diagnostics))
                    return true;

                return false;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"  ⚠️ Reflection inspection failed: {ex.Message}");
                diagnostics.Add($"  Stack: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Tries to get the Client instance from various sources
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
                    }
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add($"  ⚠️ Could not access ClientInstance property: {ex.Message}");
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
                                diagnostics.Add($"  Found Client type: {foundClientType.FullName}");
                                var staticFields = foundClientType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                                var instanceField = staticFields.FirstOrDefault(f => 
                                    f.FieldType == foundClientType || 
                                    f.FieldType.IsAssignableFrom(foundClientType));
                                
                                if (instanceField != null)
                                {
                                    client = instanceField.GetValue(null);
                                    if (client != null)
                                    {
                                        diagnostics.Add($"  Found Client via static field: {instanceField.Name}");
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
                    diagnostics.Add($"  ⚠️ Could not find Client via type search: {ex.Message}");
                }
            }

            return client;
        }

        // ====================================================================
        // SMB Download: Binary Data Retrieval and Conversion
        // ====================================================================

        // Removed unused methods: TryDownloadViaBinaryAssetDownloadInfoAsync, TryDownloadViaElementDataModelAsync,
        // TryDownloadViaGetAllAssetInfosWithTranslatedGeometryPath, TryDownloadViaDirectSmbMethod

        /// <summary>
        /// Gets binary data for a geometry asset from ElementDataModel
        /// Returns the binary data and related objects needed for conversion
        /// </summary>
        // Removed unused methods: TryDownloadViaBinaryAssetDownloadInfoAsync, TryDownloadViaElementDataModelAsync,
        // TryDownloadViaGetAllAssetInfosWithTranslatedGeometryPath, TryDownloadViaDirectSmbMethod

        /// <summary>
        /// Gets binary data for a geometry asset from ElementDataModel
        /// Returns the binary data and related objects needed for conversion
        /// </summary>
        private static async Task<(byte[] binaryData, object geometryAsset, object assetInfo, bool success)> GetGeometryAssetBinaryData(
            object client,
            Exchange exchange,
            MethodInfo[] allMethods,
            List<string> diagnostics)
        {
            diagnostics.Add($"  === Step 1: Getting binary data from geometry asset ===");
            try
            {
                var downloadBinaryMethod = allMethods.FirstOrDefault(m => m.Name == "DownloadAndCacheBinaryForBinaryAsset");
                if (downloadBinaryMethod == null)
                {
                    diagnostics.Add($"  ⚠️ DownloadAndCacheBinaryForBinaryAsset method not found");
                    return (null, null, null, false);
                }

                var identifierType = Type.GetType("Autodesk.DataExchange.Core.Models.DataExchangeIdentifier, Autodesk.DataExchange.Core");
                if (identifierType == null)
                {
                    diagnostics.Add($"  ⚠️ DataExchangeIdentifier type not found");
                    return (null, null, null, false);
                }

                var identifier = Activator.CreateInstance(identifierType);
                var exchangeIdProp = identifierType.GetProperty("ExchangeId");
                var collectionIdProp = identifierType.GetProperty("CollectionId");
                var hubIdProp = identifierType.GetProperty("HubId");
                
                if (exchangeIdProp != null) exchangeIdProp.SetValue(identifier, exchange.ExchangeId);
                if (collectionIdProp != null) collectionIdProp.SetValue(identifier, exchange.CollectionId);
                if (hubIdProp != null && !string.IsNullOrEmpty(exchange.HubId))
                {
                    hubIdProp.SetValue(identifier, exchange.HubId);
                }

                var getElementDataModelMethod = allMethods.FirstOrDefault(m => 
                    m.Name == "GetElementDataModelAsync" && m.IsPublic);
                
                if (getElementDataModelMethod == null)
                {
                    diagnostics.Add($"  ⚠️ GetElementDataModelAsync method not found");
                    return (null, null, null, false);
                }

                diagnostics.Add($"  Getting ElementDataModel to find GeometryAssets...");
                var elementDataModelResult = getElementDataModelMethod.Invoke(client, new object[] { identifier, CancellationToken.None });
                
                if (elementDataModelResult == null)
                {
                    diagnostics.Add($"  ⚠️ GetElementDataModelAsync returned null");
                    return (null, null, null, false);
                }

                var taskResult = ((dynamic)elementDataModelResult).GetAwaiter().GetResult();
                object elementDataModel = null;
                
                diagnostics.Add($"  Task result type: {taskResult?.GetType().Name ?? "null"}");
                
                var responseType = taskResult?.GetType();
                if (responseType != null)
                {
                    var isSuccessProp = responseType.GetProperty("IsSuccess") ?? 
                                       responseType.GetProperty("Success");
                    if (isSuccessProp != null)
                    {
                        var isSuccess = (bool)isSuccessProp.GetValue(taskResult);
                        diagnostics.Add($"  Response IsSuccess: {isSuccess}");
                    }
                    
                    var valueProp = responseType.GetProperty("Value");
                    if (valueProp != null)
                    {
                        try
                        {
                            elementDataModel = valueProp.GetValue(taskResult);
                            diagnostics.Add($"  Got ElementDataModel from Value: {elementDataModel?.GetType().Name ?? "null"}");
                        }
                        catch (Exception ex)
                        {
                            diagnostics.Add($"  ⚠️ Error getting Value: {ex.Message}");
                            var errorProp = responseType.GetProperty("Error");
                            if (errorProp != null)
                            {
                                var error = errorProp.GetValue(taskResult);
                                diagnostics.Add($"  Response Error: {error}");
                            }
                            elementDataModel = taskResult;
                        }
                    }
                    else
                    {
                        elementDataModel = taskResult;
                    }
                }
                
                if (elementDataModel == null)
                {
                    diagnostics.Add($"  ⚠️ ElementDataModel is null");
                    return (null, null, null, false);
                }

                var elementDataModelType = elementDataModel.GetType();
                diagnostics.Add($"  ElementDataModel type: {elementDataModelType.FullName}");
                
                // ExchangeData is an internal property, so we need to use NonPublic binding flags
                var exchangeDataProp = elementDataModelType.GetProperty("ExchangeData", 
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (exchangeDataProp == null)
                {
                    diagnostics.Add($"  ⚠️ ExchangeData property not found on ElementDataModel (tried with NonPublic binding)");
                    return (null, null, null, false);
                }

                var exchangeData = exchangeDataProp.GetValue(elementDataModel);
                diagnostics.Add($"  Got ExchangeData: {exchangeData?.GetType().Name ?? "null"}");
                if (exchangeData == null)
                {
                    diagnostics.Add($"  ⚠️ ExchangeData is null");
                    return (null, null, null, false);
                }

                var exchangeDataType = exchangeData.GetType();
                
                // Get GeometryAsset type first
                var geometryAssetType = Type.GetType("Autodesk.DataExchange.SchemaObjects.Assets.GeometryAsset, Autodesk.DataExchange");
                if (geometryAssetType == null)
                {
                    // Fallback: search through loaded assemblies
                    diagnostics.Add($"  GeometryAsset type not found with direct lookup, searching loaded assemblies...");
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            geometryAssetType = assembly.GetType("Autodesk.DataExchange.SchemaObjects.Assets.GeometryAsset");
                            if (geometryAssetType != null)
                            {
                                diagnostics.Add($"  Found GeometryAsset type in assembly: {assembly.FullName}");
                                break;
                            }
                        }
                        catch
                        {
                            // Skip assemblies that can't be queried
                        }
                    }
                }
                
                if (geometryAssetType == null)
                {
                    diagnostics.Add($"  ⚠️ GeometryAsset type not found in any loaded assembly");
                    return (null, null, null, false);
                }
                
                // Use ExchangeData.GetAssetsByType<GeometryAsset>() - this is the simplest and most direct way!
                var getAssetsByTypeMethod = exchangeDataType.GetMethod("GetAssetsByType", BindingFlags.Public | BindingFlags.Instance);
                if (getAssetsByTypeMethod == null)
                {
                    diagnostics.Add($"  ⚠️ GetAssetsByType method not found on ExchangeData");
                    return (null, null, null, false);
                }
                
                // Make the generic method with GeometryAsset type
                var genericGetAssetsByType = getAssetsByTypeMethod.MakeGenericMethod(geometryAssetType);
                var geometryAssetsResult = genericGetAssetsByType.Invoke(exchangeData, null);
                
                if (geometryAssetsResult == null)
                {
                    diagnostics.Add($"  ⚠️ GetAssetsByType<GeometryAsset>() returned null");
                    return (null, null, null, false);
                }
                
                var geometryAssets = geometryAssetsResult as System.Collections.IEnumerable;
                if (geometryAssets == null)
                {
                    diagnostics.Add($"  ⚠️ GeometryAssets result is not enumerable");
                    return (null, null, null, false);
                }
                
                // Count geometry assets for diagnostics
                var allGeometryAssets = new List<object>();
                foreach (var geomAsset in geometryAssets)
                {
                    if (geomAsset != null)
                    {
                        allGeometryAssets.Add(geomAsset);
                    }
                }
                
                diagnostics.Add($"  Found {allGeometryAssets.Count} GeometryAsset(s) via ExchangeData.GetAssetsByType<GeometryAsset>()");
                
                if (allGeometryAssets.Count == 0)
                {
                    diagnostics.Add($"  ⚠️ No geometry assets found in ExchangeData");
                    return (null, null, null, false);
                }

                // Try each geometry asset until we successfully download binary data
                foreach (var geometryAsset in geometryAssets)
                {
                    if (geometryAsset == null)
                        continue;

                    try
                    {
                        var binaryRefProp = geometryAsset.GetType().GetProperty("BinaryReference");
                        if (binaryRefProp == null)
                        {
                            diagnostics.Add($"  GeometryAsset has no BinaryReference property, skipping...");
                            continue;
                        }

                        var binaryRef = binaryRefProp.GetValue(geometryAsset);
                        if (binaryRef == null)
                        {
                            diagnostics.Add($"  GeometryAsset BinaryReference is null, skipping...");
                            continue;
                        }

                        diagnostics.Add($"  Found GeometryAsset with BinaryReference, downloading binary...");
                        
                        var downloadParams = downloadBinaryMethod.GetParameters();
                        object[] downloadInvokeParams;
                        
                        if (downloadParams.Length == 3)
                        {
                            downloadInvokeParams = new object[] { identifier, binaryRef, CancellationToken.None };
                        }
                        else if (downloadParams.Length == 4)
                        {
                            var clientTypeForWorkSpace = client.GetType();
                            var workSpaceProp = clientTypeForWorkSpace.GetProperty("WorkSpaceUserGeometryPath");
                            var workSpacePath = workSpaceProp?.GetValue(client)?.ToString() ?? Path.GetTempPath();
                            downloadInvokeParams = new object[] { identifier, binaryRef, workSpacePath, CancellationToken.None };
                        }
                        else
                        {
                            downloadInvokeParams = new object[] { identifier, binaryRef };
                        }
                        
                        diagnostics.Add($"  Invoking DownloadAndCacheBinaryForBinaryAsset with {downloadInvokeParams.Length} parameters...");
                        var downloadResult = downloadBinaryMethod.Invoke(client, downloadInvokeParams);
                        
                        if (downloadResult != null && downloadResult.GetType().IsGenericType)
                        {
                            var binaryFilePath = ((dynamic)downloadResult).GetAwaiter().GetResult();
                            
                            if (string.IsNullOrEmpty(binaryFilePath) || !File.Exists(binaryFilePath))
                            {
                                diagnostics.Add($"  ⚠️ Binary file not found or path is empty: {binaryFilePath}");
                                continue;
                            }

                            diagnostics.Add($"  ✓ Downloaded binary file: {binaryFilePath} ({new FileInfo(binaryFilePath).Length} bytes)");
                            
                            var startProp = binaryRef.GetType().GetProperty("Start");
                            var endProp = binaryRef.GetType().GetProperty("End");
                            
                            byte[] binaryData = null;
                            if (startProp != null && endProp != null)
                            {
                                var start = Convert.ToInt64(startProp.GetValue(binaryRef));
                                var end = Convert.ToInt64(endProp.GetValue(binaryRef));
                                var length = end - start + 1;
                                
                                diagnostics.Add($"  Reading binary range [{start}-{end}] from file...");
                                
                                using (var fileStream = new FileStream(binaryFilePath, FileMode.Open, FileAccess.Read))
                                {
                                    fileStream.Seek(start, SeekOrigin.Begin);
                                    binaryData = new byte[length];
                                    var bytesRead = fileStream.Read(binaryData, 0, (int)length);
                                    if (bytesRead < length)
                                    {
                                        Array.Resize(ref binaryData, bytesRead);
                                    }
                                }
                                diagnostics.Add($"  ✓ Read {binaryData.Length} bytes from range");
                            }
                            else
                            {
                                binaryData = File.ReadAllBytes(binaryFilePath);
                                diagnostics.Add($"  ✓ Read entire file: {binaryData.Length} bytes");
                            }

                            // Create AssetInfo using CreateAssetInfoForGeometryAsset method
                            object assetInfo = null;
                            var createAssetInfoMethod = allMethods.FirstOrDefault(m => m.Name == "CreateAssetInfoForGeometryAsset");
                            if (createAssetInfoMethod != null)
                            {
                                diagnostics.Add($"  Creating AssetInfo using CreateAssetInfoForGeometryAsset...");
                                try
                                {
                                    assetInfo = createAssetInfoMethod.Invoke(client, new object[] { geometryAsset });
                                    diagnostics.Add($"  ✓ Created AssetInfo: {assetInfo?.GetType().FullName ?? "null"}");
                                }
                                catch (Exception ex)
                                {
                                    diagnostics.Add($"  ⚠️ Error creating AssetInfo: {ex.Message}");
                                    diagnostics.Add($"  Will try with empty AssetInfo");
                                    // Fallback: create empty AssetInfo
                                    var assetInfoType = Type.GetType("Autodesk.GeometryUtilities.SDK.AssetInfo, Autodesk.GeometryUtilities");
                                    if (assetInfoType == null)
                                    {
                                        // Try alternative namespace
                                        assetInfoType = Type.GetType("Autodesk.DataExchange.DataModels.AssetInfo, Autodesk.DataExchange.DataModels");
                                    }
                                    if (assetInfoType != null)
                                    {
                                        assetInfo = Activator.CreateInstance(assetInfoType);
                                    }
                                }
                            }
                            else
                            {
                                diagnostics.Add($"  ⚠️ CreateAssetInfoForGeometryAsset method not found, creating empty AssetInfo");
                                var assetInfoType = Type.GetType("Autodesk.GeometryUtilities.SDK.AssetInfo, Autodesk.GeometryUtilities");
                                if (assetInfoType == null)
                                {
                                    assetInfoType = Type.GetType("Autodesk.DataExchange.DataModels.AssetInfo, Autodesk.DataExchange.DataModels");
                                }
                                if (assetInfoType != null)
                                {
                                    assetInfo = Activator.CreateInstance(assetInfoType);
                                }
                            }
                            
                            diagnostics.Add($"  ✓ Successfully got binary data: {binaryData.Length} bytes");
                            return (binaryData, geometryAsset, assetInfo, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add($"  ⚠️ Error processing GeometryAsset: {ex.Message}");
                        if (ex.InnerException != null)
                        {
                            diagnostics.Add($"  Inner exception: {ex.InnerException.Message}");
                        }
                    }
                }

                diagnostics.Add($"  ⚠️ No geometry assets with valid binary data found");
                return (null, null, null, false);
            }
            catch (Exception ex)
            {
                diagnostics.Add($"  ⚠️ Error getting binary data: {ex.Message}");
                diagnostics.Add($"  Stack: {ex.StackTrace}");
                return (null, null, null, false);
            }
        }

        // ====================================================================
        // SMB Download: Binary Data Retrieval and Conversion
        // ====================================================================

        /// <summary>
        /// Converts binary data to SMB format using ParseGeometryAssetBinaryToIntermediateGeometry
        /// </summary>
        private static bool ConvertBinaryToSMB(
            object client,
            byte[] binaryData,
            object geometryAsset,
            object assetInfo,
            string smbFilePath,
            MethodInfo[] allMethods,
            List<string> diagnostics)
        {
            diagnostics.Add($"  === Step 2: Converting binary data to SMB format ===");
            try
            {
                var parseMethod = allMethods.FirstOrDefault(m => m.Name == "ParseGeometryAssetBinaryToIntermediateGeometry");
                if (parseMethod == null)
                {
                    diagnostics.Add($"  ⚠️ ParseGeometryAssetBinaryToIntermediateGeometry method not found");
                    return false;
                }

                if (binaryData == null || binaryData.Length == 0)
                {
                    diagnostics.Add($"  ⚠️ Binary data is null or empty");
                    return false;
                }

                diagnostics.Add($"  Binary data size: {binaryData.Length} bytes");
                diagnostics.Add($"  GeometryAsset type: {geometryAsset?.GetType().FullName ?? "null"}");
                diagnostics.Add($"  AssetInfo type: {assetInfo?.GetType().FullName ?? "null"}");
                
                diagnostics.Add($"  Invoking ParseGeometryAssetBinaryToIntermediateGeometry...");
                var smbBytes = parseMethod.Invoke(client, new object[] { assetInfo, geometryAsset, binaryData }) as byte[];
                
                if (smbBytes == null)
                {
                    diagnostics.Add($"  ⚠️ ParseGeometryAssetBinaryToIntermediateGeometry returned null");
                    return false;
                }

                if (smbBytes.Length == 0)
                {
                    diagnostics.Add($"  ⚠️ ParseGeometryAssetBinaryToIntermediateGeometry returned empty byte array");
                    return false;
                }

                File.WriteAllBytes(smbFilePath, smbBytes);
                diagnostics.Add($"  ✓ Successfully converted and saved SMB file: {smbBytes.Length} bytes");
                return true;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"  ⚠️ Error converting binary to SMB: {ex.Message}");
                diagnostics.Add($"  Exception type: {ex.GetType().Name}");
                if (ex.InnerException != null)
                {
                    diagnostics.Add($"  Inner exception: {ex.InnerException.Message}");
                }
                diagnostics.Add($"  Stack: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Attempts to download SMB using ParseGeometryAssetBinaryToIntermediateGeometry
        /// Uses two-step process: 1) Get binary data, 2) Convert to SMB
        /// </summary>
        private static async Task<bool> TryDownloadViaParseGeometryAssetBinaryToIntermediateGeometry(
            object client,
            Exchange exchange,
            string smbFilePath,
            MethodInfo[] allMethods,
            List<string> diagnostics)
        {
            diagnostics.Add($"  === Attempting to use ParseGeometryAssetBinaryToIntermediateGeometry (2-step process) ===");
            
            // Step 1: Get binary data
            var (binaryData, geometryAsset, assetInfo, success) = await GetGeometryAssetBinaryData(
                client, exchange, allMethods, diagnostics);
            
            if (!success || binaryData == null)
            {
                diagnostics.Add($"  ⚠️ Failed to get binary data");
                return false;
            }

            // Step 2: Convert binary to SMB
            return ConvertBinaryToSMB(
                client, binaryData, geometryAsset, assetInfo, smbFilePath, allMethods, diagnostics);
        }

        // Removed unused methods: TryDownloadViaElementDataModelAsync, TryDownloadViaGetAllAssetInfosWithTranslatedGeometryPath, 
        // TryDownloadViaDirectSmbMethod, LogInternalFormatConversionMethods, DownloadSMBViaHttpAPI


        /// <summary>
        /// Inspects ProtoGeometry assembly to find SMB-related methods
        /// </summary>
        private static void InspectProtoGeometryForSMBMethods(List<string> diagnostics)
        {
            diagnostics.Add("\n=== INSPECTING PROTOGEOMETRY ASSEMBLY FOR SMB METHODS ===");
            
            try
            {
                // Get the Geometry type and its assembly
                var geometryType = typeof(Geometry);
                var assembly = geometryType.Assembly;
                
                diagnostics.Add($"\n1. PROTOGEOMETRY ASSEMBLY LOCATION:");
                diagnostics.Add($"   Full Name: {assembly.FullName}");
                diagnostics.Add($"   Location: {assembly.Location}");
                diagnostics.Add($"   CodeBase: {assembly.CodeBase}");
                
                // Get all types in the assembly
                var allTypes = assembly.GetTypes();
                diagnostics.Add($"\n2. ASSEMBLY CONTAINS {allTypes.Length} TYPES");
                
                // Find Geometry class
                diagnostics.Add($"\n3. GEOMETRY CLASS:");
                diagnostics.Add($"   Full Name: {geometryType.FullName}");
                diagnostics.Add($"   Namespace: {geometryType.Namespace}");
                
                // Get all methods on Geometry class
                var allMethods = geometryType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                diagnostics.Add($"   Total Methods: {allMethods.Length}");
                
                // Find SMB-related methods on Geometry class
                var smbMethods = allMethods.Where(m => 
                    m.Name.ToLowerInvariant().Contains("smb")).ToList();
                
                diagnostics.Add($"\n4. SMB-RELATED METHODS ON GEOMETRY CLASS: {smbMethods.Count}");
                if (smbMethods.Any())
                {
                    foreach (var method in smbMethods)
                    {
                        var paramInfo = method.GetParameters();
                        var paramStr = string.Join(", ", paramInfo.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        var returnType = method.ReturnType.Name;
                        var isStatic = method.IsStatic ? "static" : "instance";
                        var visibility = method.IsPublic ? "public" : (method.IsPrivate ? "private" : (method.IsFamily ? "protected" : "internal"));
                        
                        diagnostics.Add($"   [{visibility}] [{isStatic}] {returnType} {method.Name}({paramStr})");
                    }
                }
                else
                {
                    diagnostics.Add("   ⚠️ NO SMB METHODS FOUND ON GEOMETRY CLASS");
                }
                
                // Search ALL types in the assembly for SMB methods
                diagnostics.Add($"\n5. SEARCHING ALL TYPES IN ASSEMBLY FOR SMB METHODS:");
                var allSmbMethods = new List<(Type type, MethodInfo method)>();
                
                foreach (var type in allTypes)
                {
                    try
                    {
                        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        var typeSmbMethods = methods.Where(m => m.Name.ToLowerInvariant().Contains("smb"));
                        
                        foreach (var method in typeSmbMethods)
                        {
                            allSmbMethods.Add((type, method));
                        }
                    }
                    catch
                    {
                        // Skip types we can't inspect
                    }
                }
                
                diagnostics.Add($"   Found {allSmbMethods.Count} SMB method(s) across all types:");
                if (allSmbMethods.Any())
                {
                    foreach (var (type, method) in allSmbMethods)
                    {
                        var paramInfo = method.GetParameters();
                        var paramStr = string.Join(", ", paramInfo.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        var returnType = method.ReturnType.Name;
                        var isStatic = method.IsStatic ? "static" : "instance";
                        var visibility = method.IsPublic ? "public" : (method.IsPrivate ? "private" : (method.IsFamily ? "protected" : "internal"));
                        
                        diagnostics.Add($"   {type.FullName}.{method.Name}({paramStr})");
                        diagnostics.Add($"     - Return: {returnType}");
                        diagnostics.Add($"     - Visibility: {visibility}");
                        diagnostics.Add($"     - Static: {isStatic}");
                    }
                }
                else
                {
                    diagnostics.Add("   ⚠️ NO SMB METHODS FOUND IN ENTIRE ASSEMBLY");
                }
                
                diagnostics.Add($"\n=== INSPECTION COMPLETE ===");
            }
            catch (Exception ex)
            {
                diagnostics.Add($"\n✗ ERROR DURING INSPECTION: {ex.GetType().Name}: {ex.Message}");
                diagnostics.Add($"   Stack: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Loads geometry from SMB file using ProtoGeometry APIs
        /// Uses the new SMB import APIs provided by Craig Long's DLLs
        /// </summary>
        private static List<Geometry> LoadGeometryFromSMB(
            string smbFilePath,
            string unit,
            List<string> diagnostics)
        {
            var geometries = new List<Geometry>();

            try
            {
                diagnostics.Add("\nLoading via ProtoGeometry SMB APIs...");

                if (!File.Exists(smbFilePath))
                {
                    throw new FileNotFoundException($"SMB file not found: {smbFilePath}");
                }

                diagnostics.Add($"  SMB file path: {smbFilePath}");
                diagnostics.Add($"  File size: {new FileInfo(smbFilePath).Length} bytes");
                diagnostics.Add($"  Unit: {unit}");

                // Convert unit string to mmPerUnit (millimeters per unit)
                // kUnitType_CentiMeter = 10.0 mm/cm
                // kUnitType_Meter = 1000.0 mm/m
                // kUnitType_Feet = 304.8 mm/ft
                // kUnitType_Inch = 25.4 mm/in
                double mmPerUnit = 10.0; // Default to cm
                if (unit.Contains("Meter") && !unit.Contains("Centi"))
                    mmPerUnit = 1000.0;
                else if (unit.Contains("CentiMeter") || unit.Contains("cm"))
                    mmPerUnit = 10.0;
                else if (unit.Contains("Feet") || unit.Contains("ft"))
                    mmPerUnit = 304.8;
                else if (unit.Contains("Inch") || unit.Contains("in"))
                    mmPerUnit = 25.4;
                
                // Normalize the path
                var normalizedPath = Path.GetFullPath(smbFilePath);
                diagnostics.Add($"  Original path: {smbFilePath}");
                diagnostics.Add($"  Normalized path: {normalizedPath}");
                
                // If path has spaces, copy to temp location without spaces
                if (normalizedPath.Contains(" "))
                {
                    diagnostics.Add($"  ⚠️ Path contains spaces - copying to temp location");
                    var tempDir = Path.Combine(Path.GetTempPath(), "DataExchangeNodes");
                    if (!Directory.Exists(tempDir))
                        Directory.CreateDirectory(tempDir);
                    
                    var tempFileName = Path.GetFileName(smbFilePath);
                    var tempPath = Path.Combine(tempDir, tempFileName);
                    File.Copy(normalizedPath, tempPath, overwrite: true);
                    smbFilePath = tempPath;
                    diagnostics.Add($"  Copied to: {smbFilePath}");
                }
                else
                {
                    smbFilePath = normalizedPath;
                }
                
                // Get Geometry type from the new ProtoGeometry.dll (not the NuGet one)
                var geometryType = GetGeometryTypeFromNewAssembly(diagnostics);
                
                // Find all ImportFromSMB methods
                var allImportMethods = geometryType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Where(m => m.Name == "ImportFromSMB")
                    .ToList();
                
                diagnostics.Add($"  Found {allImportMethods.Count} ImportFromSMB method(s)");
                
                // Try the public method first
                var importMethod = allImportMethods.FirstOrDefault(m => 
                    m.IsPublic && 
                    m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType == typeof(string) &&
                    m.GetParameters()[1].ParameterType == typeof(double));
                
                Geometry[] result = null;
                
                if (importMethod != null)
                {
                    diagnostics.Add($"  Trying public method: ImportFromSMB(String, Double)...");
                    try
                    {
                        result = importMethod.Invoke(null, new object[] { smbFilePath, mmPerUnit }) as Geometry[];
                        if (result != null && result.Length > 0)
                        {
                            diagnostics.Add($"  ✓ Public method succeeded");
                        }
                        else if (result != null)
                        {
                            diagnostics.Add($"  ⚠️ Public method returned empty array");
                        }
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add($"  ⚠️ Public method failed: {ex.InnerException?.GetType().Name ?? ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}");
                        result = null; // Try internal method as fallback
                    }
                }
                
                // If public method failed, the error is in the native LibG code
                // The internal method would have the same issue since it calls the same native code
                if (result == null || result.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"ImportFromSMB failed in native LibG code. " +
                        $"This suggests the SMB file format may not be compatible with LibG, " +
                        $"or there's an issue with the native LibG implementation. " +
                        $"Check diagnostics for detailed error information.");
                }
                
                if (result != null && result.Length > 0)
                {
                    geometries.AddRange(result);
                    diagnostics.Add($"  ✓ Successfully loaded {geometries.Count} geometry object(s) from SMB file");
                }
                else if (result != null && result.Length == 0)
                {
                    diagnostics.Add($"  ⚠️ ImportFromSMB returned empty array - no geometries loaded");
                }
                else
                {
                    diagnostics.Add($"  ⚠️ ImportFromSMB returned null - no geometries loaded");
                }
                
                return geometries;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"✗ Error loading geometry from SMB: {ex.Message}");
                diagnostics.Add($"  Type: {ex.GetType().Name}");
                diagnostics.Add($"  Stack: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    diagnostics.Add($"  Inner: {ex.InnerException.Message}");
                }
                throw;
            }
        }
    }
}

