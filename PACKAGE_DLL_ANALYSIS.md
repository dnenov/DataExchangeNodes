# DataExchangeNodes Package DLL Analysis

## Current Package Structure

The package contains many DLLs, but not all are needed. Here's what's actually required:

## ✅ REQUIRED DLLs (Keep These)

### 1. Core Package DLLs
- `ExchangeNodes.dll` - Main package assembly
- `ExchangeNodes.NodeModels.dll` - Node model definitions
- `ExchangeNodes.NodeViews.dll` - UI customizations
- `ExchangeNodes.pdb`, `ExchangeNodes.xml` - Debug symbols and docs
- `ExchangeNodes.NodeModels.pdb`, `ExchangeNodes.NodeModels.xml`
- `ExchangeNodes.NodeViews.pdb`, `ExchangeNodes.NodeViews.xml`

### 2. Custom LibG DLLs (New - Required)
- `RootDir/ProtoGeometry.dll` - Custom ProtoGeometry with SMB support
- `RootDir/LibG.Interface.dll` - LibG interface
- `libg_231_0_0/` folder - All LibG DLLs (6 files)

### 3. DataExchange Core DLLs (Required for Runtime)
These are used by the code via reflection and direct references:

**Core Functionality:**
- `Autodesk.DataExchange.dll` - Main DataExchange SDK
- `Autodesk.DataExchange.Core.dll` - Core models and interfaces
- `Autodesk.DataExchange.BaseModels.dll` - Base data models
- `Autodesk.DataExchange.Interface.dll` - Core interfaces

**UI Components (NodeViews project):**
- `Autodesk.DataExchange.UI.dll` - UI components
- `Autodesk.DataExchange.UI.Bridge.dll` - UI bridge
- `Autodesk.DataExchange.UI.ViewModels.dll` - ViewModels
- `Autodesk.DataExchange.UI.Core.dll` - UI core (used in code)

**Extensions (Used by SelectExchangeElements):**
- `Autodesk.DataExchange.Extensions.HostingProvider.dll` - Used in SelectExchangeElementsViewCustomization
- `Autodesk.DataExchange.Extensions.Logging.File.dll` - Used in SelectExchangeElementsViewCustomization
- `Autodesk.DataExchange.Extensions.Storage.File.dll` - Used in SelectExchangeElementsViewCustomization

**Geometry/Data Models:**
- `Autodesk.DataExchange.DataModels.dll` - Data models (used in ExportGeometryToSMB)
- `Autodesk.DataExchange.Schemas.dll` - Schema definitions
- `Autodesk.GeometryPrimitives.Data.dll` - Geometry primitives
- `Autodesk.GeometryPrimitives.IO.dll` - Geometry I/O
- `Autodesk.GeometryUtilities.dll` - Geometry utilities
- `Autodesk.GeometryUtilities.MeshAPI.dll` - Mesh API
- `Autodesk.GeometryUtilities.MeshAPI.IO.dll` - Mesh I/O

**API/Network:**
- `Autodesk.DataExchange.OpenAPI.dll` - OpenAPI client
- `Autodesk.DataExchange.OpenAPITools.dll` - OpenAPI tools
- `RestSharp.dll` - HTTP client library

**Other:**
- `Google.Protobuf.dll` - Protocol buffers (dependency)
- `protobuf-net.dll`, `protobuf-net.Core.dll` - Protocol buffer serialization
- `Newtonsoft.Json.dll` - JSON serialization (but marked ExcludeAssets="runtime" - Dynamo provides it)
- `JsonSubTypes.dll` - JSON subtypes
- `OneOf.dll` - OneOf type
- `System.ComponentModel.Composition.dll` - MEF composition

### 4. FDXToCollab Folder (Required)
This entire folder is needed for geometry conversion operations. It contains:
- ACIS geometry kernel DLLs (ASM*230A.dll files)
- ATF (Autodesk Translation Framework) DLLs
- DesignTranslator components
- Native geometry libraries

### 5. WebUI Folder (Required)
Contains CefSharp web browser components needed for the UI:
- CefSharp DLLs
- Chromium Embedded Framework (CEF) binaries
- `ConnectorWebUI.exe` - Web UI host

### 6. Supporting DLLs
- `Greg.dll` - Greg library (NuGet package dependency)
- `Microsoft.Xaml.Behaviors.dll` - XAML behaviors
- `Ijwhost.dll` - .NET hosting support

## ⚠️ POTENTIALLY REMOVABLE DLLs

### 1. Duplicate/Unused DataExchange DLLs
These might be transitive dependencies that aren't actually used:

**Possibly Unused:**
- `Autodesk.DataExchange.Authentication.dll` - Might be provided by Dynamo/Client
- `Autodesk.DataExchange.CommonDependencies.dll` - Common deps (might be redundant)
- `Autodesk.DataExchange.ContractProvider.dll` - Contract provider (check if used)
- `Autodesk.DataExchange.Exceptions.dll` - Exception types (might be in Core)
- `Autodesk.DataExchange.Extensions.Loader.dll` - Extension loader (check if used)
- `Autodesk.DataExchange.Metrics.dll` - Metrics (probably optional)
- `Autodesk.DataExchange.Resiliency.dll` - Resiliency (probably optional)
- `Autodesk.DataExchange.Resources.dll` - Resources (might be embedded)
- `Autodesk.DataExchange.SourceProvider.dll` - Source provider (check if used)

**To Test:** Remove these one by one and test if the package still works.

### 2. Executables
- `adskdxui.exe` - DataExchange UI executable (might not be needed if UI is embedded)

### 3. win-x64 Folder
This appears to be a duplicate structure. Check if it's needed or if it's a build artifact.

### 4. manifests Folder
- `manifests/pkg.json` - This should be at package root, not in bin/

## 🔧 RECOMMENDATIONS

### Immediate Actions:

1. **Fix the NodeViews.csproj:**
   The `CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` setting is copying ALL transitive dependencies. Consider:
   - Setting it to `false` and explicitly listing only needed DLLs
   - Or using `PrivateAssets` to exclude unnecessary dependencies

2. **Test Minimal Package:**
   Create a test package with only the REQUIRED DLLs listed above and verify functionality.

3. **Check for Dynamo-Provided DLLs:**
   Many DLLs might already be provided by Dynamo or the DataExchange Client. Check if removing them causes issues.

4. **Remove Duplicates:**
   - Remove `win-x64` folder if it's a duplicate
   - Move `manifests/pkg.json` to package root if needed

### Suggested .csproj Changes:

```xml
<!-- In ExchangeNodes.NodeViews.csproj -->
<PropertyGroup>
  <!-- Change this to false to avoid copying all transitive dependencies -->
  <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
</PropertyGroup>

<!-- Then explicitly include only what's needed -->
<ItemGroup>
  <None Include="$(OutputPath)Autodesk.DataExchange.UI.dll" Condition="'$(CopyOutputAssemblies)' == 'true'">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
  <!-- Add other explicitly needed DLLs -->
</ItemGroup>
```

## 📊 Size Impact

Current package likely contains:
- ~50-100 MB of potentially unnecessary DLLs
- FDXToCollab folder: ~50-100 MB (required)
- WebUI folder: ~50-100 MB (required)
- DataExchange DLLs: ~20-30 MB (some may be removable)

**Potential savings:** 20-50 MB by removing unused DLLs

## 🧪 Testing Strategy

1. Start with a minimal set (core + explicitly used DLLs)
2. Test each node functionality:
   - SelectExchangeElements (needs UI DLLs)
   - LoadGeometryFromExchange (needs Core + Geometry DLLs)
   - ExportGeometryToSMB (needs Core + Geometry + FDXToCollab)
3. Add DLLs back if runtime errors occur
4. Document which DLLs are actually needed
