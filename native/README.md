# Native DLLs for DataExchange UI SDK

This folder contains native C/C++ DLLs required by the `Autodesk.DataExchange.UI` NuGet package (v6.2.12-beta).

## Why These DLLs Are Needed

The DataExchange UI SDK has a packaging bug where it references native DLLs but doesn't include them in the NuGet package. These DLLs must be manually copied from a working installation.

## Current Structure (DynamoATF Approach)

We adopted the **DynamoATF pattern**: all 117+ native DLLs are kept in a `FDXToCollab/` subfolder, not in the bin root. This keeps the bin folder clean and isolates the massive ACIS/ATF dependency chain.

### FDXToCollab/ (117 DLLs, ~170MB)

Copied from: `C:\Users\nenovd\Documents\GitHub\DynamoATF\dynamo-package\bin\FDXToCollab\`

#### ACIS Geometry Kernel (~60MB, 37 DLLs)
- `ASMKERN230A.dll` - Core ACIS kernel (13MB)
- `ASMBASE230A.dll`, `ASMHL230A.dll`, `ASMBLND230A.dll` - Base operations
- `ASMBOOL230A.dll`, `ASMINTR230A.dll` - Boolean and intersection ops
- Plus 30+ more ASM*.dll files for modeling, healing, offsetting, etc.

#### ATF Translation Framework (~50MB, 40 DLLs)
- `atf_api.dll` - Core ATF API
- `atf_fdx_consumer.dll`, `atf_fdx_producer.dll` - DataExchange I/O
- `atf_ifc_producer.dll` - IFC export
- `atf_step_consumer.dll`, `atf_step_producer.dll` - STEP I/O
- `atf_sat_consumer.dll`, `atf_sat_producer.dll` - SAT I/O
- `atf_obj_consumer.dll`, `atf_obj_producer.dll` - OBJ I/O
- `atf_svf_consumer.dll` - SVF import
- `atf_tritonmesh_consumer.dll`, `atf_tritonmesh_producer.dll` - Mesh I/O
- Plus geometry utilities, parameter management, extension data, etc.

#### Autodesk Translators (8 DLLs)
- `Autodesk.DesignTranslator.DX.dll` - DataExchange translator
- `Autodesk.DesignTranslator.IFC.dll` - IFC translator
- `Autodesk.DesignTranslator.STEP.dll` - STEP translator
- `Autodesk.DesignTranslator.SAT.dll` - SAT translator
- `Autodesk.DesignTranslator.OBJ.dll` - OBJ translator
- `Autodesk.DesignTranslator.SVF.dll` - SVF translator
- `Autodesk.DesignTranslator.TTMESH.dll` - TritonMesh translator
- `Autodesk.DesignTranslator.NET.dll` - .NET wrapper

#### Autodesk Geometry Utilities (8 DLLs)
- `Autodesk.GeometryUtilities.dll` - High-level geometry utilities
- `Autodesk.GeometryUtilities.BRepAPI.dll` - B-Rep API
- `Autodesk.GeometryUtilities.BRepAPI.IO.dll` - B-Rep I/O
- `Autodesk.GeometryUtilities.MeshAPI.dll` - Mesh API
- `Autodesk.GeometryUtilities.MeshAPI.IO.dll` - Mesh I/O
- `Autodesk.GeometryPrimitives.IO.dll` - Geometry I/O primitives
- `Autodesk.GeometryPrimitives.Data.dll` - Geometry data structures
- `Autodesk.DataExchange.OpenAPITools.dll` - OpenAPI tools

#### Open Design Alliance (Teigha) (~15MB, 20 DLLs)
- `TD_Ge_24.12_16.dll` - Geometry engine (4.76MB)
- `TD_Gi_24.12_16.dll` - Graphics interface (2.45MB)
- `TD_Root_24.12_16.dll`, `TD_DbRoot_24.12_16.dll` - Core/database
- `TD_Br_24.12_16.dll` - B-Rep support
- `TD_BrepBuilder_24.12_16.dll`, `TD_BrepRenderer_24.12_16.dll` - B-Rep tools
- `IfcCore_24.12_16.dll`, `IfcGeom_24.12_16.dll` - IFC support
- `FacetModeler_24.12_16.dll` - Facet modeling
- Plus IFC schema files (*.txexp), transaction files (*.tx)

#### STEP/IGES Libraries (~15MB)
- `stp_aim_x64_vc15.dll` - STEP AIM implementation (12.92MB)
- `rose_x64_vc15.dll`, `rosemath_x64_vc15.dll` - ROSE STEP toolkit
- `stix_x64_vc15.dll` - STEP utilities
- `iges_x64_vc15.dll` - IGES support (2.16MB)

#### Other Dependencies
- `tsplines12.dll` - T-Splines library (12.36MB)
- `mmsdk.dll` - Material/modeling SDK (15.76MB)
- `tritonmesh.dll` - TritonMesh library
- `xerces-ad-c_3.dll` - XML parser (3.75MB)
- `libzip.dll` - ZIP compression
- `tbb12.dll`, `tbbmalloc.dll` - Intel Threading Building Blocks
- `Ijwhost.dll` - C++/CLI interop
- `Google.Protobuf.dll`, `protobuf-net.dll` - Protocol Buffers
- `Newtonsoft.Json.dll`, `RestSharp.dll`, `Polly.dll` - .NET utilities
- `Parameters.dll`, `Units.dll` - Forge libraries
- `DesignDescription.dll`, `DesignMetadata.dll` - Design data
- `LMVCore.dll`, `LMVPackage.dll` - LMV (viewer) support

## Deployment

The entire `FDXToCollab/` folder is included in `ExchangeNodes.NodeViews.csproj` with:

```xml
<ItemGroup>
  <None Include="..\native\FDXToCollab\**\*.*">
    <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    <Link>FDXToCollab\%(RecursiveDir)%(Filename)%(Extension)</Link>
  </None>
</ItemGroup>
```

This copies the entire folder structure to the output, and the Python packaging script preserves it in the deployed package.

## Package Size Impact

**Total: ~170MB** for the FDXToCollab folder. This is unavoidable for DataExchange UI SDK support as it requires the full ACIS kernel and translation framework.

## Why Not Minimal DLLs?

We initially tried including only 8 "minimal" DLLs, but the DataExchange UI SDK is not lazily loading them - it attempts to load the entire ACIS/ATF chain during initialization. The error `Could not load file or assembly 'Autodesk.GeometryUtilities.BRepAPI.IO.dll'` was actually a missing transitive dependency (ASMKERN230A.dll).

## Comparison to DynamoATF

Our approach exactly mirrors DynamoATF:
- All translation DLLs in `FDXToCollab/` subfolder
- Clean bin root with only managed DLLs
- Full ACIS + ATF + Teigha + STEP/IGES support

## Future

If Autodesk:
1. Fixes the NuGet packaging bug, or
2. Makes the DataExchange UI SDK lazy-load geometry translation DLLs

...then this folder could be reduced or eliminated. For now, it's required for the UI browser to initialize.
