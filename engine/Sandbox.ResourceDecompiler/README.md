# Sandbox.ResourceDecompiler

A standalone .NET 10 command-line recovery tool for s&box / Source 2 resources.
It does not boot the engine or require an editor instance. Only recover assets you
own or have permission to modify.

## Usage

From the repository root:

```powershell
dotnet run --project engine\Sandbox.ResourceDecompiler\Sandbox.ResourceDecompiler.csproj -- "D:\my-project\Assets"
```

Or build and run the executable:

```powershell
dotnet build engine\Sandbox.ResourceDecompiler\Sandbox.ResourceDecompiler.csproj
.\engine\Sandbox.ResourceDecompiler\bin\Debug\net10.0\Sandbox.ResourceDecompiler.exe "D:\my-project\Assets"
```

The tool recursively scans files whose extension ends in `_c`, case-insensitively.
For `ABC.xxx_c`, if `ABC.xxx` already exists, the compiled file is skipped without
being parsed. Otherwise, recovered source is written beside the compiled resource.
Existing files, including extracted dependencies, are never overwritten. Directory
links/junctions and file links are not followed.

### Choosing default formats

Edit the single extension list in [DecompilerFormats.cs](DecompilerFormats.cs).
Each compiled extension has its own line. Comment out any unwanted format:

```csharp
".scene_c",
".prefab_c",
// ".vtex_c",
// ".vmat_c",
```

Rebuild after editing (or use `dotnet run`, which rebuilds automatically).
Matching is case-insensitive. Unlisted/disabled extensions are logged as
`[SKIP FORMAT]` before parsing and counted separately; they do not cause a failure
exit code. An empty list disables all recovery. There is no wildcard fallback:
to recover a custom JSON GameResource, add its compiled extension, e.g.
`".myresource_c"`, to this list. Adding an extension does not implement a decoder
for an unsupported native format.

The list controls which compiled files are selected for recovery, not dependency
decoding. An enabled material/model exporter can still read textures or other
dependencies and emit required source images/meshes even if their extensions are
disabled as standalone scan targets.

Prefer passing the whole `Assets` directory so material/model dependencies can be
found and written at the correct content-relative paths. If the input is inside an
`Assets` directory, that directory is used for dependency lookup, but output is
still confined to the input directory. Missing/out-of-scope dependencies are
reported rather than silently treated as a complete recovery.

Each resource is staged before its outputs are published, with the primary source
written last. A failed operation rolls back files newly created by that operation.
A conflicting existing dependency (including `.scene_d` / other blob companions)
fails recovery instead of pairing source with incorrect binary data. Identical
existing dependencies are retained. The original `_c` is unchanged.

## Resource formats

| Compiled input | Recovered output / limitations |
| --- | --- |
| `.scene_c`, `.prefab_c` | Source JSON and optional binary `DBLO` companion (`.scene_d`, `.prefab_d`). |
| `.sound_c`, `.tmat_c` | JSON sound-event / terrain-material definition, not audio / image data. Their external assets are separate resources. |
| `.sprite_c`, `.decal_c`, `.surface_c`, `.terrain_c`, `.clutter_c`, `.navarea_c`, `.sndscape_c`, `.rtex_c` | Managed `GameResource` JSON; custom game resource extensions are detected by JSON content too, once added to the enabled list. |
| `.vmat_c` | Editable `Layer0` material definition and unpacked 2D PNG texture maps. s&box `line` and common `glass`, `foliage`, `bark`, `water_simple` color/normal/RMA mappings are handled explicitly. Other inputs use VRF mappings and are reported as partial/inferred. Missing textures are reported. HDR, array, and cubemap material-map unpacking is not supported. |
| `.vtex_c` | `.vtex` definition plus PNG/EXR or embedded image. Includes generated child textures. HDR cubemaps are reconstructed from a lat-long EXR. Texture arrays and non-HDR cubemap source layouts currently report unsupported. |
| `.vmdl_c` | ModelDoc definition plus DMX mesh/rig/physics dependencies, using VRF's model extractor. For s&box packed `R10G10B10A2_UNORM` tangent frames, geometry is exported without those unsupported attributes and the result is reported as partial: regenerate normals/tangents in ModelDoc. |
| `.vsnd_c` | **Audio source**, with its actual `.wav`, `.mp3`, `.ogg`, or `.opus` extension. s&box compiles audio files to `.vsnd_c`; writing audio bytes under a `.vsnd` name would not restore a valid source. s&box v5 PCM8/16, MP3, Vorbis, and 48 kHz mono/stereo Opus are supported; Opus packets are remuxed into Ogg without re-encoding. Older versions use VRF. `LIPS` / `SUBT` metadata becomes an adjacent audio `.meta` file. Reported as partial because no same-stem `.vsnd` is created. |
| `.shader_c` | If present, the `HLSL` block is recovered as `.shader.hlsl.json` and one `.shader.VFX_PROGRAM_*.hlsl` per stage. These are masked/preprocessed stage sources, **not** a complete editable `.shader`: original `HEADER` / `MODES` / `FEATURES` and source structure may be lost. Reported as partial; shaders without embedded source report unsupported. |
| `.vpcf_c`, `.vanmgrph_c`, `.vpost_c`, `.vsnap_c` | VRF's specialized particle, animation-graph, post-processing, and snapshot exporters, including subfiles. Availability depends on the compiled format/version and dependencies. |
| Other native formats, e.g. standalone `.vmesh_c`, `.vphys_c` | Not enabled by default. If explicitly enabled, reported unsupported unless their `DATA` is managed JSON. A raw runtime KV3 dump is not mislabeled as editable authoring source. Mesh/physics embedded in a model are recovered through `.vmdl_c`. |

This is best-effort reconstruction, not a byte-for-byte inverse compiler. Comments,
original filenames/layout, discarded shader data, original uncompressed pixels,
source modeling history, and other information removed by compilation cannot be
recreated. Recovered JSON retains its saved version and identifiers. Native
definitions should be reviewed and recompiled in the matching s&box editor;
successful extraction alone does not guarantee identical appearance or bytecode.
The tool does not invent source files for unrecoverable formats.

Output is labeled `[RECOVERED]`, `[SKIP]`, `[SKIP FORMAT]`, `[KEEP]`, `[PARTIAL]`, or `[FAILED]`, with a
final summary. For audio/shader partial exports, rerunning preserves existing
outputs but retries the resource because the exact suffix-stripped source does
not exist.

Exit codes:

- `0`: all encountered resources recovered or skipped because source exists or their format is disabled.
- `1`: at least one partial, unsupported, corrupt, inaccessible, or failed resource.
  Other resources are still processed.
- `2`: invalid command-line arguments / nonexistent input directory.

## Implementation and tests

The pinned MIT-licensed [ValveResourceFormat](https://github.com/ValveResourceFormat/ValveResourceFormat)
NuGet package provides native Source 2 decoding/export. The compatibility reader
preserves s&box extension blocks and original block indices without modifying the
input files. Managed JSON, blobs, shader HLSL, and s&box v5 audio are handled locally.

Run the isolated, native-engine-free MSTest suite:

```powershell
dotnet test engine\Sandbox.ResourceDecompiler\Tests\Sandbox.ResourceDecompiler.Tests.csproj
```
