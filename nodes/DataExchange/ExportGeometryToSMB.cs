using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Autodesk.DesignScript.Geometry;
using Autodesk.DesignScript.Runtime;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Export Dynamo geometry objects to SMB file format using ProtoGeometry
    /// </summary>
    public static class ExportGeometryToSMB
    {
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
                { "log", diagnostics },
                { "success", success }
            };
        }
    }
}
