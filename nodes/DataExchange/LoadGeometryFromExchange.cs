using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Autodesk.DesignScript.Geometry;
using Autodesk.DesignScript.Runtime;

// Required for IntPtr and ACIS pointer operations
using System.Runtime.InteropServices;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Load geometry from a DataExchange using ProtoGeometry SMB file APIs
    /// </summary>
    public static class LoadGeometryFromExchange
    {
        // Singleton pattern - auth provider registered by SelectExchangeElements view
        private static Func<string> _getTokenFunc = null;

        /// <summary>
        /// Registers the token provider function (called by SelectExchangeElements view)
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public static void RegisterAuthProvider(Func<string> getTokenFunc)
        {
            _getTokenFunc = getTokenFunc;
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

                // Build DataExchange API URL for downloading SMB file
                // Typical endpoint: /exchanges/{exchangeId}/collections/{collectionId}/files or /download
                var baseUrl = "https://developer.api.autodesk.com/exchange/v1";
                var downloadUrl = $"{baseUrl}/exchanges/{exchange.ExchangeId}/collections/{exchange.CollectionId}/files";

                diagnostics.Add($"  Downloading from: {downloadUrl}");

                // Download file using HttpClient
                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Authorization = 
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                    httpClient.DefaultRequestHeaders.Add("Accept", "application/octet-stream");

                    var response = await httpClient.GetAsync(downloadUrl);
                    response.EnsureSuccessStatusCode();

                    // If the response is a redirect or JSON with a download URL, handle it
                    var contentType = response.Content.Headers.ContentType?.MediaType;
                    if (contentType == "application/json")
                    {
                        // Response might contain a download URL - parse it
                        var jsonContent = await response.Content.ReadAsStringAsync();
                        diagnostics.Add($"  Received JSON response, parsing...");
                        // TODO: Parse JSON to get actual download URL if needed
                        // For now, try alternative endpoint
                        downloadUrl = $"{baseUrl}/exchanges/{exchange.ExchangeId}/collections/{exchange.CollectionId}/download";
                        response = await httpClient.GetAsync(downloadUrl);
                        response.EnsureSuccessStatusCode();
                    }

                    // Save SMB file
                    using (var fileStream = new FileStream(smbFilePath, FileMode.Create))
                    {
                        await response.Content.CopyToAsync(fileStream);
                    }

                    diagnostics.Add($"  ✓ Downloaded {new FileInfo(smbFilePath).Length} bytes");
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
        /// Loads geometry from SMB file using ProtoGeometry APIs
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

                // Use ProtoGeometry to open SMB file
                // Note: The exact API signature may vary - adjust based on actual ProtoGeometry API
                // Common patterns:
                // - ProtoGeometry.Geometry.OpenSMB(string path)
                // - ProtoGeometry.FileLoader.OpenSMB(string path, string unit)
                // - Or similar static/instance methods

                // Try reflection-based approach to find the SMB opening method
                var protoGeometryAssembly = System.Reflection.Assembly.LoadFrom(
                    Path.Combine(Path.GetDirectoryName(typeof(Geometry).Assembly.Location), "ProtoGeometry.dll"));
                
                if (protoGeometryAssembly == null)
                {
                    // Fallback: try loading from common locations
                    var possiblePaths = new[]
                    {
                        @"C:\Users\nenovd\Downloads\Dynamo\RootDir\ProtoGeometry.dll",
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
                            @"Dynamo\Dynamo Core\4.1\ProtoGeometry.dll")
                    };

                    foreach (var path in possiblePaths)
                    {
                        if (File.Exists(path))
                        {
                            protoGeometryAssembly = System.Reflection.Assembly.LoadFrom(path);
                            break;
                        }
                    }
                }

                if (protoGeometryAssembly == null)
                {
                    throw new InvalidOperationException("ProtoGeometry.dll not found. Please ensure it's in the correct location.");
                }

                // Look for SMB-related types/methods
                var types = protoGeometryAssembly.GetTypes();
                var smbType = types.FirstOrDefault(t => 
                    t.Name.Contains("SMB") || 
                    t.Name.Contains("FileLoader") || 
                    t.Name.Contains("GeometryLoader"));

                if (smbType != null)
                {
                    diagnostics.Add($"  Found type: {smbType.FullName}");

                    // Try to find Open/Load methods
                    var openMethod = smbType.GetMethods(System.Reflection.BindingFlags.Public | 
                        System.Reflection.BindingFlags.Static | 
                        System.Reflection.BindingFlags.Instance)
                        .FirstOrDefault(m => 
                            (m.Name.Contains("Open") || m.Name.Contains("Load")) && 
                            m.GetParameters().Length >= 1);

                    if (openMethod != null)
                    {
                        diagnostics.Add($"  Found method: {openMethod.Name}");

                        object loader = null;
                        if (openMethod.IsStatic)
                        {
                            // Static method - call directly
                            var result = openMethod.Invoke(null, new object[] { smbFilePath });
                            if (result is IEnumerable<Geometry> geoEnumerable)
                            {
                                geometries.AddRange(geoEnumerable);
                            }
                            else if (result is Geometry geo)
                            {
                                geometries.Add(geo);
                            }
                        }
                        else
                        {
                            // Instance method - create instance first
                            loader = Activator.CreateInstance(smbType);
                            var result = openMethod.Invoke(loader, new object[] { smbFilePath });
                            if (result is IEnumerable<Geometry> geoEnumerable)
                            {
                                geometries.AddRange(geoEnumerable);
                            }
                            else if (result is Geometry geo)
                            {
                                geometries.Add(geo);
                            }
                        }

                        // Try to get geometry from loaded objects
                        if (loader != null)
                        {
                            var getGeometryMethod = loader.GetType().GetMethods()
                                .FirstOrDefault(m => m.Name.Contains("GetGeometry") || m.Name.Contains("GetGeometries"));
                            
                            if (getGeometryMethod != null)
                            {
                                var geoResult = getGeometryMethod.Invoke(loader, null);
                                if (geoResult is IEnumerable<Geometry> geoEnumerable)
                                {
                                    geometries.AddRange(geoEnumerable);
                                }
                            }
                        }
                    }
                    else
                    {
                        // Fallback: Try using Geometry.FromNativePointer if SMB file provides pointers
                        diagnostics.Add("  ⚠️ Direct SMB API not found, trying alternative approach...");
                        // This would require knowing how ProtoGeometry exposes SMB data
                    }
                }
                else
                {
                    // Alternative: Use Geometry.FromNativePointer with file path
                    // Some implementations allow direct file path to native pointer conversion
                    diagnostics.Add("  ⚠️ SMB loader type not found, trying Geometry.FromNativePointer...");
                    
                    // Try: Geometry.FromNativePointer(IntPtr) - but we need to get the pointer from SMB file
                    // This might require a different approach - loading SMB and getting ACIS body pointers
                }

                if (geometries.Count == 0)
                {
                    diagnostics.Add("  ⚠️ No geometries loaded - ProtoGeometry API may need adjustment");
                    diagnostics.Add("  Please check ProtoGeometry.dll for SMB file opening methods");
                }
                else
                {
                    diagnostics.Add($"  ✓ Successfully loaded {geometries.Count} geometry object(s)");
                }

                return geometries;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"✗ Error loading geometry from SMB: {ex.Message}");
                diagnostics.Add($"  Type: {ex.GetType().Name}");
                diagnostics.Add($"  Stack: {ex.StackTrace}");
                throw;
            }
            finally
            {
                // Cleanup SMB file
                try
                {
                    if (File.Exists(smbFilePath))
                        File.Delete(smbFilePath);
                }
                catch { /* Ignore cleanup errors */ }
            }
        }
    }
}
