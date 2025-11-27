# SAT/SAB DataExchange Implementation Plan

## Overview

This document details the discovered SAT/SAB support in the fdx-connector and provides the exact implementation approach for the ExportExchangeToSAB and ImportSABToExchange nodes.

## Background

### Key Discovery

DataExchange internally uses `.smb` (proprietary BRep format) and `.ttmesh` (mesh format), **NOT** `.sab`. However, SAT/SAB support exists in the **internal translation layer** that bridges external formats to DataExchange's internal formats.

### Native DLLs Found

Located in `fdx-connector/atf-dependencies/libs/`:

- `atf_sat_consumer.dll` - Native C++ SAT writer
- `atf_sat_producer.dll` - Native C++ SAT reader
- `Autodesk.DesignTranslator.SAT.dll` - C++/CLI wrapper for SAT operations
- `Autodesk.DesignTranslator.DX.dll` - C++/CLI wrapper for DataExchange operations
- `Autodesk.DesignTranslator.NET.dll` - Main translation orchestrator
- All `ASM*.dll` files - ACIS geometry kernel (v231)
- All `atf_*.dll` files - ATF translation framework

## Architecture

// 1. Create .fdx config pointing to DataExchange
var fileLoader = new Dynamo.Proxy.FileLoader(fdxFile, unit);

// 2. Download geometry DIRECTLY from DataExchange (bypasses translation)
var bodies = fileLoader.GetBodies(...);

// 3. Convert to Dynamo Geometry (which uses ACIS/SAB internally)
var geometry = Autodesk.DesignScript.Geometry.FromNativePointer(bodies);

### Translation Paths

**Download Path (DataExchange → SAT):**

```
DataExchange Cloud (.smb internal format)
    ↓
DXProducerOptions (reads exchange via REST API)
    ↓
DesignTranslator.Translator.Run()
    ↓
SATConsumerOptions (writes .sat files)
    ↓
.sat files on disk
```

**Upload Path (SAT → DataExchange):**

```
.sat files on disk (from Dynamo native export)
    ↓
SATProducerOptions (reads .sat files)
    ↓
DesignTranslator.Translator.Run()
    ↓
DXConsumerOptions (writes to exchange via REST API)
    ↓
DataExchange Cloud (.smb internal format)
```

## Exact Internal Methods and Classes

### Assembly Locations

All classes are in C++/CLI DLLs that must be loaded from the deployed package's `FDXToCollab` directory:

**Path structure in deployed Dynamo package:**

```
DataExchangeNodes/
  bin/
    FDXToCollab/
      net48/ (or net8.0/ depending on runtime)
        Autodesk.DesignTranslator.NET.dll
        Autodesk.GeometryUtilities.dll
        (other .NET assemblies)
      libs/
        Autodesk.DesignTranslator.SAT.dll
        Autodesk.DesignTranslator.DX.dll
        atf_sat_consumer.dll
        atf_sat_producer.dll
        ASM*.dll (ACIS kernel)
        atf_*.dll (translation framework)
```

### Core Translator Class

**`DesignTranslator.Translator`** (in `Autodesk.DesignTranslator.NET.dll`)

**Properties:**

- `ProducerOptions` (type: `DesignTranslator.ProducerOptions`) - INPUT source (what to read from)
- `ConsumerOptions` (type: `DesignTranslator.ConsumerOptions`) - OUTPUT target (where to write to)
- `LogFile` (type: `string`) - Optional log file path for debugging

**Method:**

- `void Run()` - **THE KEY METHOD** that executes the translation

**Reference Implementation:**
See `fdx-connector/src/FDXSDK/DesignTranslator/DesignTranslator.cs` lines 54-61:

```csharp
var translator = new Autodesk.DesignTranslator.Translator
{
    ProducerOptions = producerOptions,  // What to read
    ConsumerOptions = consumerOptions,  // Where to write
    LogFile = this.TranslatorLog,
};
translator.Run();  // Execute translation
```

### SAT Input/Output Classes

**`DesignTranslator.SATProducerOptions`** (in `Autodesk.DesignTranslator.SAT.dll`)

Reads SAT files from disk.

- **Inherits from:** `ProducerOptions`
- **Property:** `Sources` - List of `Source` objects containing SAT file paths
- **Methods:**
  - `CreateProducer(Context)` - Creates the ATF producer
  - `ValidateOptionsApplied(...)` - Validates options before use

**`DesignTranslator.SATConsumerOptions`** (in `Autodesk.DesignTranslator.SAT.dll`)

Writes SAT files to disk.

- **Inherits from:** `ConsumerOptions`
- **Property:** `OutputFolder` (string) - Directory for output SAT files
- **Methods:**
  - `AllowMultiBodyConsumption()` - Returns bool for multi-body support
  - `CreateConsumer(Context)` - Creates the ATF consumer

### DataExchange Input/Output Classes

**`DesignTranslator.DXProducerOptions`** (in `Autodesk.DesignTranslator.DX.dll`)

Reads geometry from DataExchange cloud.

- **Inherits from:** `ProducerOptions`
- **Properties:**
  - `Url` (string) - Format: `https://developer.api.autodesk.com/exchange/v1/exchanges/{exchangeId}`
  - `AccessToken` (string) - OAuth2 bearer token
  - `CollectionID` (string) - DataExchange collection ID
  - `ExchangeID` (string) - DataExchange exchange ID
  - `WantExplicitlyMergeLatestDelta` (bool) - Whether to merge latest delta

**`DesignTranslator.DXConsumerOptions`** (in `Autodesk.DesignTranslator.DX.dll`)

Writes geometry to DataExchange cloud.

- **Inherits from:** `ConsumerOptions`
- **Properties:**
  - `Url` (string) - DataExchange API URL
  - `AccessToken` (string) - OAuth2 bearer token
  - `CollectionID` (string) - DataExchange collection ID
  - `ExchangeID` (string) - DataExchange exchange ID
  - `jsonDumpPath` (string) - Optional JSON dump path for debugging

### Supporting Classes

**`DesignTranslator.Source`**

Represents a source file or URN for translation.

- **Properties:**
  - `Name` (string) - File path or URN
  - `Units` - Unit override
  - `OverrideSourceUnits` (bool) - Whether to override source file units (SAT-specific)
  - `Style` (string) - Style data (serialized)
  - `SubEntStylesMap` (string) - Sub-entity styles map (serialized)
  - `PolygonIndexStylesMap` (string) - Polygon index styles map (serialized)

**`DesignTranslator.Context`**

Provides authentication and settings context for translation.

- **Properties:**
  - `CurrentSource` (string) - Current source being processed
  - `ConsumerOptions` - Gets consumer options
  - `ProducerOptions` - Gets producer options
- **Methods:**
  - `Log(severity, message)` - Logging method

## Implementation Code Templates

### ExportExchangeToSAB (Download: DataExchange → SAT)

```csharp
public static List<byte[]> ToByteArrays(Exchange exchange, bool exportAsBinary)
{
    var byteArrays = new List<byte[]>();
    var diagnostics = new List<string>();

    try
    {
        // 1. Setup assembly resolver and load DLLs
        AppDomain.CurrentDomain.AssemblyResolve += LoadFromFDXToCollab;

        var packageDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var fdxPath = Path.Combine(packageDir, "FDXToCollab");
        var netPath = Path.Combine(fdxPath, "net48");  // or net8.0
        var libsPath = Path.Combine(fdxPath, "libs");

        var netDll = Assembly.LoadFrom(Path.Combine(netPath, "Autodesk.DesignTranslator.NET.dll"));
        var dxDll = Assembly.LoadFrom(Path.Combine(libsPath, "Autodesk.DesignTranslator.DX.dll"));
        var satDll = Assembly.LoadFrom(Path.Combine(libsPath, "Autodesk.DesignTranslator.SAT.dll"));

        // 2. Get auth token from registered client
        var client = GetRegisteredClient();
        var authToken = GetAuthToken(client);

        // 3. Create temp directory for output
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        // 4. Create DXProducerOptions (input - reads from DataExchange)
        var dxProducerType = dxDll.GetType("DesignTranslator.DXProducerOptions");
        var dxProducer = Activator.CreateInstance(dxProducerType);

        var exchangeUrl = $"https://developer.api.autodesk.com/exchange/v1/exchanges/{exchange.ExchangeId}";
        SetProperty(dxProducer, "Url", exchangeUrl);
        SetProperty(dxProducer, "AccessToken", authToken);
        SetProperty(dxProducer, "CollectionID", exchange.CollectionId);
        SetProperty(dxProducer, "ExchangeID", exchange.ExchangeId);
        SetProperty(dxProducer, "WantExplicitlyMergeLatestDelta", true);

        // 5. Create SATConsumerOptions (output - writes SAT files)
        var satConsumerType = satDll.GetType("DesignTranslator.SATConsumerOptions");
        var satConsumer = Activator.CreateInstance(satConsumerType);
        SetProperty(satConsumer, "OutputFolder", tempDir);

        // 6. Create Translator and execute
        var translatorType = netDll.GetType("DesignTranslator.Translator");
        var translator = Activator.CreateInstance(translatorType);
        SetProperty(translator, "ProducerOptions", dxProducer);
        SetProperty(translator, "ConsumerOptions", satConsumer);
        // SetProperty(translator, "LogFile", logPath);  // Optional for debugging

        var runMethod = translatorType.GetMethod("Run");
        runMethod.Invoke(translator, null);

        // 7. Read output SAT files and convert to byte arrays
        var satFiles = Directory.GetFiles(tempDir, "*.sat");
        foreach (var satFile in satFiles)
        {
            byteArrays.Add(File.ReadAllBytes(satFile));
        }

        // Cleanup
        Directory.Delete(tempDir, true);
    }
    catch (Exception ex)
    {
        diagnostics.Add($"ERROR: {ex.Message}");
        throw;
    }

    return byteArrays;
}
```

### ImportSABToExchange (Upload: SAT → DataExchange)

```csharp
public static void Upload(Exchange exchange, List<byte[]> sabByteArrays, List<string> elementIds)
{
    var diagnostics = new List<string>();

    try
    {
        // 1. Write SAB byte arrays to temporary .sat files
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        var satFiles = new List<string>();
        for (int i = 0; i < sabByteArrays.Count; i++)
        {
            var satPath = Path.Combine(tempDir, $"geometry_{i}.sat");
            File.WriteAllBytes(satPath, sabByteArrays[i]);
            satFiles.Add(satPath);
        }

        // 2. Load assemblies from FDXToCollab directory
        AppDomain.CurrentDomain.AssemblyResolve += LoadFromFDXToCollab;

        var packageDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var fdxPath = Path.Combine(packageDir, "FDXToCollab");
        var netPath = Path.Combine(fdxPath, "net48");
        var libsPath = Path.Combine(fdxPath, "libs");

        var netDll = Assembly.LoadFrom(Path.Combine(netPath, "Autodesk.DesignTranslator.NET.dll"));
        var dxDll = Assembly.LoadFrom(Path.Combine(libsPath, "Autodesk.DesignTranslator.DX.dll"));
        var satDll = Assembly.LoadFrom(Path.Combine(libsPath, "Autodesk.DesignTranslator.SAT.dll"));

        // 3. Get auth token
        var client = GetRegisteredClient();
        var authToken = GetAuthToken(client);

        // 4. Create SATProducerOptions (input - reads SAT files)
        var satProducerType = satDll.GetType("DesignTranslator.SATProducerOptions");
        var satProducer = Activator.CreateInstance(satProducerType);

        // Create Source objects for each SAT file
        var sourceType = netDll.GetType("DesignTranslator.Source");
        var sourceListType = typeof(List<>).MakeGenericType(sourceType);
        var sources = Activator.CreateInstance(sourceListType);

        foreach (var satFile in satFiles)
        {
            var source = Activator.CreateInstance(sourceType);
            SetProperty(source, "Name", satFile);
            sourceListType.GetMethod("Add").Invoke(sources, new[] { source });
        }

        SetProperty(satProducer, "Sources", sources);

        // 5. Create DXConsumerOptions (output - writes to DataExchange)
        var dxConsumerType = dxDll.GetType("DesignTranslator.DXConsumerOptions");
        var dxConsumer = Activator.CreateInstance(dxConsumerType);

        var exchangeUrl = $"https://developer.api.autodesk.com/exchange/v1/exchanges/{exchange.ExchangeId}";
        SetProperty(dxConsumer, "Url", exchangeUrl);
        SetProperty(dxConsumer, "AccessToken", authToken);
        SetProperty(dxConsumer, "CollectionID", exchange.CollectionId);
        SetProperty(dxConsumer, "ExchangeID", exchange.ExchangeId);

        // 6. Create Translator and execute (uploads to DataExchange)
        var translatorType = netDll.GetType("DesignTranslator.Translator");
        var translator = Activator.CreateInstance(translatorType);
        SetProperty(translator, "ProducerOptions", satProducer);
        SetProperty(translator, "ConsumerOptions", dxConsumer);

        var runMethod = translatorType.GetMethod("Run");
        runMethod.Invoke(translator, null);

        // Cleanup
        Directory.Delete(tempDir, true);
    }
    catch (Exception ex)
    {
        diagnostics.Add($"ERROR: {ex.Message}");
        throw;
    }
}
```

## Critical Implementation Details

### SAB vs SAT

- **SAB** (Standard ACIS Binary) - Binary format used by Dynamo internally
- **SAT** (Standard ACIS Text) - Text format used by DesignTranslator
- **They are the SAME format**, just different encodings (binary vs text)
- Files are interchangeable - Dynamo can read SAT files, translator can read files written as SAB

### Assembly Loading

All assemblies **MUST** be loaded from the deployed package's `FDXToCollab` directory structure. The native C++ dependencies will only load if they're in the same directory as the C++/CLI wrappers.

**Required AssemblyResolve handler:**

```csharp
private static Assembly LoadFromFDXToCollab(object sender, ResolveEventArgs args)
{
    var dllName = new AssemblyName(args.Name).Name + ".dll";
    var packageDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    var fdxPath = Path.Combine(packageDir, "FDXToCollab");

    // Try net48 first
    var netPath = Path.Combine(fdxPath, "net48", dllName);
    if (File.Exists(netPath)) return Assembly.LoadFrom(netPath);

    // Then libs
    var libsPath = Path.Combine(fdxPath, "libs", dllName);
    if (File.Exists(libsPath)) return Assembly.LoadFrom(libsPath);

    return null;
}
```

### No C# Wrappers for SAT

Unlike OBJ and SVF formats (which have C# wrapper classes like `ObjOutputOptions`), there are **NO C# wrapper classes** for SAT in the FDX SDK. You MUST use reflection to access the C++/CLI classes directly.

### Dynamo Native SAT Export

Dynamo has native capability to export SAT files. If using this:

- Export Dynamo geometry directly to `.sat` files
- Pass file paths to `SATProducerOptions.Sources`
- No need to handle byte arrays for upload!

## Next Steps

1. ✅ Document exact implementation approach (this file)
2. Refactor `ExportExchangeToSAB.cs` to use DesignTranslator
3. Refactor `ImportSABToExchange.cs` to use DesignTranslator
4. Remove all obsolete code (ElementDataModel, GeometriesToInternal, InternalToGeometries)
5. Test with real DataExchange instance
6. Handle errors and edge cases

## References

### Implementation References

- **Core implementation:** `fdx-connector/src/FDXSDK/DesignTranslator/DesignTranslator.cs` (lines 54-61)
- **DX wrapper example:** `fdx-connector/src/FDXSDK/DesignTranslator/DXInputOptions.cs`
- **Test examples:** `fdx-connector/test/FDXSDKTests/DesignTranslator.cs`

### Documentation References

- **SAT XML docs:** `fdx-connector/atf-dependencies/libs/Autodesk.DesignTranslator.SAT.xml`
- **DX XML docs:** `fdx-connector/atf-dependencies/libs/Autodesk.DesignTranslator.DX.xml`
- **NET XML docs:** `fdx-connector/atf-dependencies/libs/Autodesk.DesignTranslator.NET.xml` (if exists)
- **ATF headers:** `fdx-connector/FDXToCollab/References/atf_api/` and `atf_asm_interface/`

## Critical Discovery: .NET Core Incompatibility

### The Problem

The DesignTranslator approach **cannot be used in .NET Core**:

- `Autodesk.DesignTranslator.DX.dll` and `Autodesk.DesignTranslator.SAT.dll` are **C++/CLI mixed-mode assemblies**
- C++/CLI assemblies compiled for .NET Framework **cannot be loaded in .NET Core**
- Error: "Bad IL format. The format of the file '...Autodesk.DesignTranslator.DX.dll' is invalid."
- These assemblies do NOT exist in the `net8.0` builds, only in `libs/` (Framework only)

### The Solution: DynamoATF Approach

**Use Dynamo.Proxy + Fulfillment API** (works in .NET Core):

#### Download (DataExchange → SAT)

```csharp
// 1. Create .fdx config file
File.WriteAllLines("Exchange.fdx", new[] {
    "Token=" + authToken,
    "ExchangeFileUrl=" + exchangeUrl,
    "FDXConsumerLog=0",
    "unit=" + unit
});

// 2. FileLoader downloads geometry from DataExchange
var fileLoader = new Dynamo.Proxy.FileLoader(fdxFile, unit);
var bodies = fileLoader.GetBodies(...);

// 3. Convert to Dynamo Geometry (which is SAB internally)
var geometry = Autodesk.DesignScript.Geometry.Geometry.FromNativePointer(bodies);
```

#### Upload (SAT → DataExchange)

```csharp
// 1. Start fulfillment
var fulfillmentId = await client.StartFulfillmentAsync(exchangeIdentifier);

// 2. Convert Dynamo Geometry to native pointers
var nativePointers = Geometry.ToNativePointer(geometries);

// 3. Write to .fdx file using FdxWriter
var fdxWriter = new Dynamo.Proxy.FdxWriter();
fdxWriter.Write(fdxFilePath, exchangeCollections, meshes);

// 4. Finish fulfillment (auto-uploads the .fdx to DataExchange)
await client.FinishFulfillmentAsync(exchangeIdentifier, fulfillmentId);
await client.PollForFulfillment(exchangeIdentifier, fulfillmentId);
```

### Why This Works

- `Dynamo.Proxy.FileLoader` and `Dynamo.Proxy.FdxWriter` are **native assemblies loaded by Dynamo Core**
- These are provided via `Translation.Proxy.dll` reference (ExcludeAssets="runtime")
- They work in .NET Core because Dynamo Core handles loading the native C++ components
- The Fulfillment API is part of the standard DataExchange client (works in .NET Core)

### Implementation Status

**Current Implementation (Nov 26, 2025):**

- ✅ Added `Translation.Proxy.dll` reference to ExchangeNodes.csproj
- ✅ ExportExchangeToSAB.cs skeleton using FileLoader
- ✅ ImportSABToExchange.cs skeleton using Fulfillment API
- ⚠️ Need to complete SAT byte extraction from FileLoader
- ⚠️ Need to handle SAB bytes → Dynamo Geometry → Native Pointers → FdxWriter

### Next Steps

1. Complete ExportExchangeToSAB to extract SAT bytes from FileLoader
2. Refactor ImportSABToExchange to accept Dynamo Geometry objects (not byte arrays)
3. Use Geometry.ToNativePointer() and Dynamo.Proxy.FdxWriter pattern
4. Test with real DataExchange instance

## Conclusion

The DesignTranslator approach would have been ideal, but it's **incompatible with .NET Core**. Instead, we use the **DynamoATF approach** with `Dynamo.Proxy` + Fulfillment API, which provides a working path for SAT/SAB ↔ DataExchange translation in .NET Core environments.

---

## FINAL DECISION (Nov 27, 2025)

### Why SAT-Based Workflow Is Not Feasible

After comprehensive analysis of the entire `fdx-connector` codebase and DataExchange SDK architecture, we have concluded that **a SAT-file-based workflow is not feasible** for Dynamo (.NET Core) environments.

#### Technical Barriers Identified

1. **C++/CLI Incompatibility (.NET Core)**

   - `Autodesk.DesignTranslator.SAT.dll` is a C++/CLI mixed-mode assembly
   - C++/CLI mixed-mode assemblies **cannot be loaded in .NET Core / .NET 5+**
   - Error: "BadImageFormatException: An attempt was made to load a program with an incorrect format"
   - This is a fundamental .NET Core limitation, not a configuration issue

2. **No Pure .NET SAT Support**

   - **GUSDK** (Geometry Utilities SDK) supports: STEP, OBJ, IFC
   - **GUSDK does NOT support SAT** (confirmed via test file analysis)
   - **DesignTranslator** (C++/CLI) supports: STEP, OBJ, IFC, SAT, SVF
   - SAT is **only** available via DesignTranslator (which requires .NET Framework 4.8)

3. **DataExchange Internal Format**
   - DataExchange stores geometry internally as `.smb` (proprietary BRep) and `.ttmesh` (proprietary mesh)
   - These are **NOT** SAB/SAT files (different formats entirely)
   - Translation from `.smb` → `.sab` requires external tooling (DesignTranslator or ATF)

#### What We Tried

1. ✅ **Explored Reflection Approach**

   - Attempted to use reflection to access `DesignTranslator.SAT` classes
   - **Result:** Failed with BadImageFormatException (C++/CLI incompatible with .NET Core)

2. ✅ **Analyzed GUSDK**

   - Reviewed all GUSDK test files and component tests
   - **Result:** No `SATToSMBTests.cs` or `SMBToSATTests.cs` - SAT not supported

3. ✅ **Considered External Process Approach**
   - Build .NET Framework 4.8 CLI tool with DesignTranslator
   - Spawn from Dynamo, write SAT files to disk, read back
   - **Result:** Too complex (40+ DLLs, process management, temp files, error handling)

### The Right Solution: Dynamo.Proxy.FileLoader

Instead of pursuing SAT files, we use **`Dynamo.Proxy.FileLoader`** which:

✅ **Works in .NET Core** - Dynamo Core handles native assembly loading  
✅ **No intermediate files** - Downloads `.smb` from DataExchange and converts to ACIS in memory  
✅ **Direct ACIS geometry** - Returns Dynamo-native geometry objects (same as SAB, just not written to disk)  
✅ **Already proven** - DynamoATF uses this exact approach successfully

#### Implementation Pattern (from DynamoATF)

```csharp
// Create .fdx manifest
string[] fileLines = new string[4];
fileLines[0] = "Token=" + accessToken;
fileLines[1] = "ExchangeFileUrl=" + exchangeUrl;
fileLines[2] = "FDXConsumerLog=0";
fileLines[3] = "unit=" + unit;
File.WriteAllLines(fdxPath, fileLines);

// Load geometry via FileLoader
var fileLoader = new Dynamo.Proxy.FileLoader(fdxPath, unit);
fileLoader.Load();

// Extract geometry objects
var importedObjects = fileLoader.GetImportedObjects();
// Returns: Brep, Curve, Point, IndexMesh, etc. (Dynamo-native ACIS geometry)
```

### Why This Is Better Than SAT Files

| Aspect             | SAT Files                         | Dynamo.Proxy.FileLoader |
| ------------------ | --------------------------------- | ----------------------- |
| .NET Core Support  | ❌ No (requires .NET Framework)   | ✅ Yes                  |
| Intermediate Files | ❌ Yes (temp SAT files on disk)   | ✅ No (in-memory)       |
| Process Management | ❌ Complex (spawn external .exe)  | ✅ Simple (direct API)  |
| DLL Dependencies   | ❌ 40+ C++/CLI DLLs to ship       | ✅ 1 DLL reference      |
| Geometry Format    | SAB/SAT (ACIS)                    | ACIS (same kernel)      |
| Performance        | ❌ Slow (disk I/O, process spawn) | ✅ Fast (direct memory) |

### Final Recommendation

**For DataExchangeNodes:**

1. ❌ **Discard** `ExportExchangeToSAB.cs` and `ImportSABToExchange.cs` (DesignTranslator approach)
2. ✅ **Implement** new nodes using `Dynamo.Proxy.FileLoader` pattern from DynamoATF
3. ✅ **Reference** `Translation.Proxy.dll` (from DynamoATF staticPackages)
4. ✅ **Focus** on in-memory ACIS geometry, not SAT files

### Key Learnings

- **C++/CLI mixed-mode assemblies are incompatible with .NET Core** - this is non-negotiable
- **GUSDK only supports STEP/OBJ/IFC** - SAT is exclusively DesignTranslator territory
- **DataExchange uses `.smb` internally** - not SAB/SAT
- **Dynamo.Proxy.FileLoader solves the problem** - no need for SAT files at all

---

**Decision Date:** November 27, 2025  
**Status:** Research Complete - Proceed with Dynamo.Proxy.FileLoader implementation  
**Next Branch:** `feature/fdx-geometry-loader` (clean implementation without SAT files)
