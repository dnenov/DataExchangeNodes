# DataExchangeNodes for Dynamo

Dynamo nodes for integrating with **Autodesk DataExchange** (ACC/BIM 360). Browse, select, and load geometry from DataExchange directly into Dynamo.

![Dynamo Version](https://img.shields.io/badge/Dynamo-4.1-blue)
![.NET](https://img.shields.io/badge/.NET-10.0--windows-purple)
![License](https://img.shields.io/badge/License-MIT-green)

---

## Upload Workflows 

https://github.com/user-attachments/assets/2b96a2a3-b2e8-47e7-93c0-11be174178f5


https://github.com/user-attachments/assets/bee1007a-012b-44ef-a11e-71979c258fc3


https://github.com/user-attachments/assets/54d0388b-502d-4399-9078-089d35bc24f0


https://github.com/user-attachments/assets/b3cb9e4a-45a9-45d4-87c6-79b7e36d6013


---

## ⚠️ Required: Local Dynamo Build Configuration

**This package requires custom LibG files** that provide SMB geometry support not included in Dynamo's default `ProtoGeometry.dll`.

If you're building Dynamo from source, open `dynamo.local.props` and set your Dynamo build path:

```xml
<DynamoBuildFolder>C:\path\to\Dynamo\bin\AnyCPU\Debug</DynamoBuildFolder>
```

On build, the following files are automatically copied to your Dynamo folder:
- `libg/RootDir/*` → `[DynamoBuildFolder]/` (ProtoGeometry.dll, LibG.Interface.dll)
- `libg/libg_231_0_0/*` → `[DynamoBuildFolder]/libg_231_0_0/`

---

## 📦 Nodes Included

### 1. **Select Exchange**
Browse and select a DataExchange from Autodesk Construction Cloud (ACC).

- Opens the official DataExchange SDK browser UI
- Uses Dynamo's built-in authentication (no separate login required)
- Outputs an `Exchange` object with full metadata

**Category:** `ExchangeNodes.DataExchangeNodes.Selection`

**Output:**
| Port | Type | Description |
|------|------|-------------|
| `exchange` | `Exchange` | Selected exchange with metadata (title, project, IDs, URNs, timestamps, etc.) |

### 2. **LoadGeometryFromExchange.Load**
Loads geometry from a DataExchange as native Dynamo geometry objects.

**Category:** `DataExchangeNodes.DataExchange`

**Inputs:**
| Port | Type | Default | Description |
|------|------|---------|-------------|
| `exchange` | `Exchange` | *required* | Exchange object from SelectExchange node |
| `unit` | `string` | `"kUnitType_CentiMeter"` | Unit type for geometry conversion |

**Unit Options:**
- `kUnitType_CentiMeter` (default)
- `kUnitType_Meter`
- `kUnitType_Feet`
- `kUnitType_Inch`

**Outputs (MultiReturn):**
| Port | Type | Description |
|------|------|-------------|
| `geometries` | `List<Geometry>` | Dynamo geometry objects (Solids, Surfaces, Curves, Points) |
| `log` | `string` | Diagnostic messages for debugging |
| `success` | `bool` | Whether the operation succeeded |

### 3. **ExportGeometryToSMB.ExportToSMB** (In Development)
Exports Dynamo geometry objects to SMB file format using ProtoGeometry.

**Category:** `DataExchangeNodes.DataExchange`

**Inputs:**
| Port | Type | Default | Description |
|------|------|---------|-------------|
| `geometries` | `List<Geometry>` | *required* | Dynamo geometry objects to export |
| `outputFilePath` | `string` | *optional* | Full path where SMB file should be saved (auto-generated if empty) |
| `unit` | `string` | `"kUnitType_CentiMeter"` | Unit type for geometry |

**Outputs (MultiReturn):**
| Port | Type | Description |
|------|------|-------------|
| `smbFilePath` | `string` | Path to created SMB file |
| `log` | `string` | Diagnostic messages |
| `success` | `bool` | Whether export succeeded |

### 4. **ConvertSmbToStep.ConvertSmbToStep** (Planned)
Converts SMB files to STEP format using Autodesk.DesignTranslator.NET.

**Category:** `DataExchangeNodes.DataExchange`

**Inputs:**
| Port | Type | Default | Description |
|------|------|---------|-------------|
| `smbFilePath` | `string` | *required* | Path to input SMB file |
| `outputStepPath` | `string` | *optional* | Path for output STEP file (auto-generated if empty) |
| `logFilePath` | `string` | *optional* | Path for translation log file |

**Outputs (MultiReturn):**
| Port | Type | Description |
|------|------|-------------|
| `stepFilePath` | `string` | Path to created STEP file |
| `log` | `string` | Diagnostic messages |
| `success` | `bool` | Whether conversion succeeded |

### 5. **UploadStepToExchange.UploadStepToExchange** (Planned)
Uploads STEP geometry files to a DataExchange using public async methods.

**Category:** `DataExchangeNodes.DataExchange`

**Inputs:**
| Port | Type | Default | Description |
|------|------|---------|-------------|
| `exchange` | `Exchange` | *required* | Exchange object from SelectExchange node |
| `stepFilePath` | `string` | *required* | Path to STEP file to upload |
| `elementName` | `string` | `"ExportedGeometry"` | Name for the new element |
| `elementId` | `string` | *optional* | Unique ID for element (auto-generated if empty) |
| `unit` | `string` | `"kUnitType_CentiMeter"` | Unit type for geometry |

**Outputs (MultiReturn):**
| Port | Type | Description |
|------|------|-------------|
| `elementId` | `string` | ID of created element |
| `log` | `string` | Diagnostic messages |
| `success` | `bool` | Whether upload succeeded |

---

## 🛠️ Prerequisites

### Required Software

1. **Dynamo** 4.1+ (Standalone or Revit)
   - Must be logged in with an Autodesk account
   - ACC/BIM 360 project access required

2. **Visual Studio 2022** or later
   - .NET 10.0 SDK
   - Windows Desktop development workload

3. **Python 3.x** (for package deployment script)

4. **DynamoATF Package** (for native geometry dependencies)
   - Must be installed in Dynamo packages folder
   - Location: `%APPDATA%\Dynamo\Dynamo Core\3.6\packages\DynamoATF`

### Required Dependencies

The project requires native DLLs from **DynamoATF** for geometry translation:

```
native/FDXToCollab/
├── ACIS Kernel (37 DLLs, ~60MB)
├── ATF Translation Framework (40 DLLs, ~50MB)
├── Autodesk Translators (8 DLLs)
├── Autodesk Geometry Utilities (8 DLLs)
├── Open Design Alliance/Teigha (20 DLLs, ~15MB)
├── STEP/IGES Libraries (~15MB)
└── Other Dependencies (TBB, Protobuf, etc.)
```

> **Note:** See `native/README.md` for complete DLL documentation.

---

## 🏗️ Building the Project

### Option 1: Visual Studio

1. Open `DataExchangeNodes.sln` in Visual Studio 2022
2. Restore NuGet packages (automatic on build)
3. Build the solution:
   - **Debug:** `Ctrl+Shift+B` or Build → Build Solution
   - **Release:** Build → Build Solution (with Release configuration)

### Option 2: Command Line

```powershell
cd path\to\DataExchangeNodes

# Restore dependencies
dotnet restore

# Build Debug
dotnet build --configuration Debug

# Build Release
dotnet build --configuration Release
```

### Build Output

After building, binaries are placed in:
```
bin/
├── Debug/
│   └── 4.1.0-beta3200/
│       └── DataExchangeNodes/
│           ├── ExchangeNodes.dll
│           ├── ExchangeNodes.NodeModels.dll
│           ├── ExchangeNodes.NodeViews.dll
│           ├── FDXToCollab/           # Native DLLs
│           └── ... (other dependencies)
└── Release/
    └── ... (same structure)
```

---

## 🚀 Installing / Deploying to Dynamo

### Automatic Deployment (Post-Build)

The build process **automatically deploys** the package to Dynamo:

1. **PostBuildStep.bat** runs `prepareDynamoPackage.py`
2. Creates `dynamo-package/` folder with proper structure
3. Copies to both **Dynamo Core** and **Dynamo Revit** package folders:

```
%APPDATA%\Dynamo\Dynamo Core\4.1\packages\DataExchangeNodes\
%APPDATA%\Dynamo\Dynamo Revit\4.1\packages\DataExchangeNodes\
```

### Manual Deployment

If automatic deployment fails, manually copy the `dynamo-package/` folder:

```powershell
# Copy to Dynamo Core
Copy-Item -Recurse .\dynamo-package\* "$env:APPDATA\Dynamo\Dynamo Core\4.1\packages\DataExchangeNodes\"

# Copy to Dynamo Revit
Copy-Item -Recurse .\dynamo-package\* "$env:APPDATA\Dynamo\Dynamo Revit\4.1\packages\DataExchangeNodes\"
```

### Package Structure

```
DataExchangeNodes/
├── bin/
│   ├── ExchangeNodes.dll              # Zero-touch nodes
│   ├── ExchangeNodes.NodeModels.dll   # NodeModel definitions
│   ├── ExchangeNodes.NodeViews.dll    # WPF view customizations
│   ├── FDXToCollab/                   # Native geometry DLLs
│   ├── WebUI/                         # DataExchange UI assets
│   └── ... (SDK dependencies)
├── dyf/                               # Custom node definitions (empty)
├── extra/                             # Additional resources (empty)
└── pkg.json                           # Package manifest
```
    
---

## 📦 NuGet Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `DynamoVisualProgramming.Core` | 4.1.0-beta3200 | Core Dynamo types |
| `DynamoVisualProgramming.DynamoServices` | 4.1.0-beta3200 | Dynamo services |
| `DynamoVisualProgramming.ZeroTouchLibrary` | 4.1.0-beta3200 | Zero-touch support |
| `DynamoVisualProgramming.WpfUILibrary` | 4.1.0-beta3200 | WPF node views |
| `Autodesk.DataExchange.UI` | 6.2.12-beta | DataExchange SDK browser UI |
| `Newtonsoft.Json` | 13.0.3 | JSON serialization |

### Native DLL References

The following DLLs are referenced from `native/FDXToCollab/` (already deployed):

| DLL | Purpose |
|-----|---------|
| `Autodesk.DesignTranslator.NET.dll` | .NET wrapper for geometry translation |
| `Autodesk.DesignTranslator.STEP.dll` | STEP format support for translation |
| `Autodesk.DesignTranslator.SAT.dll` | SAT format support |
| `Autodesk.DesignTranslator.OBJ.dll` | OBJ format support |
| `Autodesk.DesignTranslator.SVF.dll` | SVF format support |
| `Autodesk.GeometryUtilities.dll` | High-level geometry utilities |
| `Autodesk.GeometryPrimitives.Data.dll` | Geometry data structures |

> **Note:** All native DLLs are in `native/FDXToCollab/` and are automatically copied to output during build.

---

## 🔐 Authentication

This package uses **Dynamo's built-in authentication**. No additional login or API keys are required.

### How It Works

1. `DynamoAuthProvider` bridges Dynamo's `AuthenticationManager` to DataExchange SDK
2. Supports both **Dynamo Core** (IDSDKManager) and **Dynamo Revit** (RevitOAuth2Provider)
3. Token is automatically retrieved from the active session
4. Works with existing ACC/BIM 360 project permissions

### Troubleshooting Auth Issues

- **"Please log in"**: Click the user icon in Dynamo and sign in
- **"Not logged in"**: Refresh Dynamo or restart the application
- **"Token expired"**: Re-authenticate in Dynamo

---

## 📄 License

MIT License - See [LICENSE](LICENSE) for details.

---

## 🔧 SDK Internals & Reflection Usage

This package uses reflection to access internal DataExchange SDK methods for direct SMB upload/download (bypassing slow STEP conversion).

**See [REFLECTION_API_REQUEST.md](REFLECTION_API_REQUEST.md)** for:
- Detailed flow diagrams for upload and download operations
- List of internal SDK methods we access via reflection
- Proposed public API that would simplify integration

This document is intended for the DataExchange SDK team to understand what functionality we need exposed.

---

## 🙏 Acknowledgments

- **DynamoATF** - Native geometry translation infrastructure
- **Autodesk DataExchange SDK** - Browser UI and API client
- **Autodesk DesignTranslator** - SMB/STEP/OBJ format translation
- **Dynamo Team** - Authentication and geometry kernel integration

