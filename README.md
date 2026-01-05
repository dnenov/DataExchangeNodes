# DataExchangeNodes for Dynamo

Dynamo nodes for integrating with **Autodesk DataExchange** (ACC/BIM 360). Browse, select, and load geometry from DataExchange directly into Dynamo.

![Dynamo Version](https://img.shields.io/badge/Dynamo-4.1-blue)
![.NET](https://img.shields.io/badge/.NET-10.0--windows-purple)
![License](https://img.shields.io/badge/License-MIT-green)

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
cd $(APPDATA)Documents\GitHub\DataExchangeNodes

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

## 📋 Usage in Dynamo

### Basic Workflow

1. **Launch Dynamo** (Standalone or in Revit)
2. **Log in** to your Autodesk account (if not already)
3. Search for `Select Exchange` in the node library
4. Place the node on the canvas
5. Click the **Select** button to open the DataExchange browser
6. Browse and select an exchange from ACC
7. Connect the `exchange` output to `LoadGeometryFromExchange.Load`
8. Run the graph to load geometry

### Example Graph

```
┌────────────────────────┐
│   Select Exchange      │
│  ┌─────────────────┐   │
│  │    [Select]     │   │───▶ exchange
│  └─────────────────┘   │
│   Selected: My Model   │
└────────────────────────┘
            │
            ▼
┌────────────────────────┐
│ LoadGeometryFromExchange│
│         .Load          │
│  exchange ●────────────│───▶ geometries
│  unit ●─"kUnitType_Feet"│───▶ log
└────────────────────────┘───▶ success
            │
            ▼
    [Dynamo Geometry]
```

### Supported Geometry Types

| Type | Support | Notes |
|------|---------|-------|
| **Brep** (Solids/Surfaces) | ✅ Full | Converted via ACIS native pointers |
| **Curves** | ✅ Full | Lines, arcs, splines |
| **Points** | ✅ Full | XYZ coordinates |
| **IndexMesh** | ⚠️ Partial | Not yet implemented |
| **Groups/Layers** | ℹ️ Container | Children extracted automatically |

---

## 🔧 Project Structure

```
DataExchangeNodes/
├── DataExchangeNodes.sln              # Solution file
├── nodes/                             # Zero-touch nodes
│   ├── ExchangeNodes.csproj
│   └── DataExchange/
│       ├── Exchange.cs                # Exchange data model
│       ├── DataExchangeUtils.cs       # AST helper functions
│       └── LoadGeometryFromExchange.cs # Geometry loader
├── node_models/                       # NodeModel definitions
│   ├── ExchangeNodes.NodeModels.csproj
│   └── DataExchange/
│       └── SelectExchangeElements.cs  # Select Exchange node
├── node_views/                        # WPF view customizations
│   ├── ExchangeNodes.NodeViews.csproj
│   └── DataExchange/
│       ├── SelectExchangeElementsViewCustomization.cs
│       ├── DynamoAuthProvider.cs      # Auth bridge to Dynamo
│       ├── ReadExchangeModel.cs       # DataExchange SDK model
│       └── MainThreadInvoker.cs       # WPF thread dispatcher
├── native/                            # Native DLLs
│   ├── FDXToCollab/                   # 117 DLLs from DynamoATF
│   └── README.md                      # DLL documentation
├── extras/                            # Build scripts
│   ├── package-template/
│   │   └── pkg.json                   # Package manifest template
│   └── prepareDynamoPackage.py        # Deployment script
├── dynamo-package/                    # Built package (generated)
└── PostBuildStep.bat                  # Post-build automation
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

## 🐛 Troubleshooting

### Common Issues

| Issue | Solution |
|-------|----------|
| Nodes not appearing in library | Restart Dynamo after build; check `pkg.json` is deployed |
| "Could not load assembly" errors | Ensure all native DLLs in `FDXToCollab/` are present |
| DataExchange UI doesn't open | Check Dynamo authentication; verify network connectivity |
| Geometry load returns empty | Check `log` output for errors; verify exchange has geometry |
| Package not deploying | Run `prepareDynamoPackage.py` manually with correct args |

### Checking Logs

Dynamo console shows detailed logs prefixed with `SelectExchangeElements:` and `ReadExchangeModel:`.

DataExchange SDK logs are written to:
```
%APPDATA%\Dynamo\DataExchange\logs\
```

---

## 📄 License

MIT License - See [LICENSE](LICENSE) for details.

---

## 🙏 Acknowledgments

- **DynamoATF** - Native geometry translation infrastructure
- **Autodesk DataExchange SDK** - Browser UI and API client
- **Dynamo Team** - Authentication and geometry kernel integration

