# Sandbox.ResourceDecompiler

A standalone .NET 10 command-line recovery tool for s&box / Source 2 resources.
It does not boot the engine or require an editor instance. Only recover assets you
own or have permission to modify.

VRF and its exporters have been removed. Independent implementations recover
managed resources, textures, audio, materials, animation graphs and static model
geometry. Recovery is deliberately best effort: some native formats yield runtime
reference data only, and some authoring information has been discarded by the
compiler. A runtime dump is never renamed to pretend it is editable source.
See the support matrix and measured round-trip results below before replacing
original compiled assets.

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

Prefer passing the whole `Assets` directory so recovered texture definitions use
the correct content-relative paths. If the input is inside an `Assets` directory,
that directory is used as the content root; output remains confined to the input
directory. The configured list includes runtime-only native formats so their
limitations are visible rather than silently hidden.

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
| `.vtex_c` | Independent VTEX internal-version-1 parser. Supports DXT1/BC1, DXT5/BC3, BC4, BC5, BC6H, BC7, I8, IA88, RGBA8888, BGRA8888, 16-bit R/RG/RGBA integers, 16-bit R/RG/RGBA floats, 32-bit R/RG/RGB/RGBA floats, and 2D embedded PNG/JPEG. Handles raw, LZ4 and Zstandard mip data. Outputs a DMX `.vtex` plus every stored mip as TGA, uncompressed floating-point OpenEXR, or the embedded image. Square cubemaps are reconstructed as correctly oriented horizontal-cross EXRs. Restores GUID. Sprite sheets recover the static atlas and retain their original metadata as `.sheet.bin`; animation sequences are not reauthored. Volumes, arrays, YCoCg and embedded-image cube layouts remain unsupported. |
| `.vsnd_c` | Independent s&box version-4/5 header parser for PCM8/16, MP3 and Vorbis, plus version-5 48 kHz mono/stereo Opus. Outputs actual `.wav`, `.mp3`, `.ogg` or `.opus`, not a fake `.vsnd`. Opus is remuxed without re-encoding. Recovers GUID, loop boundaries, `LIPS` / `SUBT` metadata, and disables lossy compression for recovered PCM via `.meta`. Earlier/different sound layouts and ADPCM are unsupported. |
| `.shader_c` | If present, the `HLSL` block is recovered as `.shader.hlsl.json` and one `.shader.VFX_PROGRAM_*.hlsl` per stage. These are masked/preprocessed stage sources, **not** a complete editable `.shader`: original `HEADER` / `MODES` / `FEATURES` and source structure may be lost. Reported as partial; shaders without embedded source report unsupported. |
| `.vmat_c` | Independent binary KV3 version-2 parser (raw/LZ4/Zstandard) and Valve KeyValues material writer. Recovers shader, integer/float/vector parameters, direct compiled texture bindings, attributes and GUID. Keeps full parsed DATA in `.vmat.runtime.json`. Retain the referenced `.vtex_c` dependencies; texture bindings deliberately use the compiler-supported output parameter names rather than guessing authoring inputs or unpacking packed channels incorrectly. Dynamic expressions and original editor structure are not restored; reported as partial. Other KV3 versions and external blob streams are explicitly unsupported. |
| `.vanmgrph_c` | Independent binary KV3 version-2 reader and `animgraph2` authoring writer. Recovers graph classes, nodes, connections, parameters, layout, notes and GUID. Native KV3 quoting is used instead of JSON Unicode escape sequences. Verified by full parsed DATA equality after recompilation of both a fixture and the copied citizen graph. Other KV3 versions/external blobs remain unsupported. |
| `.vmdl_c` | Independent embedded MDAT/MBUF parser plus meshoptimizer codec. Writes editable ModelDoc 29 `.vmdl`, `.recovered.obj`, `.recovered.lodN.obj`, `.recovered.mtl`, metadata and runtime JSON references. Recovers default-visible positions, triangle topology/winding, UV0, material bindings, all stored LOD geometry and switch distances (up to eight LODs); alternate bodygroups/LODs are not superimposed. Supports raw/meshoptimizer buffers, float32 positions, float32/float16 UV0 and 16-/32-bit indices. Normals/tangents regenerate. Skinning, animations, physics, morphs, extra vertex channels, original hierarchy and selectable bodygroups are not reconstructed as authoring structures. All embedded mesh metadata remains in `.meshN.runtime.json`, and every original resource block is retained under `.recovered.blocks`. Models without recoverable embedded default geometry yield runtime references only, not a placeholder `.vmdl`. |
| `.vpcf_c`, `.vpost_c`, `.vsnap_c` | Parsed binary KV3 version-2 DATA is salvaged as `.runtime.json`, reported as partial. Every original resource block is also preserved under `.recovered.blocks`. For an unsupported DATA encoding, raw blocks are retained with an explicit decoder limitation instead of inventing parsed data. Malformed supported KV3 still reports failure. No editable native source is claimed. The locally available official compiler does not register these extensions, so a valid authoring round trip cannot be verified against it. |
| Other native formats, e.g. standalone `.vmesh_c`, `.vphys_c` | Not enabled by default. If explicitly enabled, reported unsupported unless their `DATA` is managed JSON. |

This is best-effort reconstruction, not a byte-for-byte inverse compiler. Comments,
original filenames/layout, discarded shader data, original uncompressed pixels,
source modeling history, and other information removed by compilation cannot be
recreated. Recovered JSON retains its saved version and identifiers. Native
definitions should be reviewed and recompiled in the matching s&box editor;
successful extraction alone does not guarantee identical appearance or bytecode.
The tool does not invent source files for unrecoverable formats.

Textures are reported as partial even when their source compiles: their original
gamma/filter/import settings are not fully represented by the header. Recovered
images use linear input/output to preserve stored numeric pixel values, not to
claim the original authoring color space. A `Box` mip generator is selected when
the resource has multiple mips; the original mip images are also retained, but
the compiler regenerates its chain from mip 0. Pixel re-encoding, sprite metadata
and engine-version changes can prevent byte-identical results. For integer and
floating-point high-precision textures, EXR retains the decoded samples without
8-bit quantization. BC6H remains lossy on recompression. Cubemap face order and
rotations are based on coordinate-coded native compiler experiments, not guessed
from an assumed coordinate system. Sprite-sheet output is explicitly a static
atlas, not a restored animation.

Output is labeled `[RECOVERED]`, `[SKIP]`, `[SKIP FORMAT]`, `[KEEP]`, `[PARTIAL]`, or `[FAILED]`, with a
final summary. For audio, shader and runtime-only exports, rerunning preserves existing
outputs but retries the resource because the exact suffix-stripped source does
not exist.

For models and runtime-only legacy formats, `.recovered.blocks` preserves all
original block payloads, including animation, physics, vertex/index and metadata
blocks, plus any streaming tail. Names use `FOURCC.index.bin` (unusual nonalphanumeric
FOURCCs are hex-encoded to keep paths safe). These are reference payloads, not an
editable source or a complete byte-identical container backup. Repeated blocks
retain their per-name order. A conflicting reference file aborts publication;
matching files are kept. Keep the original `_c` as well, particularly before the
editor overwrites it during recompilation.

Exit codes:

- `0`: all encountered resources recovered or skipped because source exists or their format is disabled.
- `1`: at least one partial, unsupported, corrupt, inaccessible, or failed resource.
  Other resources are still processed.
- `2`: invalid command-line arguments / nonexistent input directory.

## Implementation and tests

There is no ValveResourceFormat dependency or fallback. Source 2 containers,
VTEX structures, mip addressing, authoring definitions and audio remuxing are
implemented in this project. External libraries only implement codecs:
BCnEncoder.Net (BC pixel decoding), K4os.Compression.LZ4, ZstdSharp.Port and
Alimer.Bindings.MeshOptimizer (native vertex/index codec). The meshoptimizer binding
supplies its native codec library; no VRF executable, library or exporter is used.
The uncompressed OpenEXR writer is implemented here and tested through the official
texture compiler.

The texture implementation is based on the engine's
[VTexWriter](../Sandbox.Engine/Resources/Textures/Writer/VTexWriter.cs),
[ResourceWriter](../Sandbox.Engine/Resources/Textures/Writer/ResourceWriter.cs),
and shipped `.vtex` definitions. Native extra-data layouts are checked against
local compiled samples and official compiler experiments; unsupported variants
are rejected, not guessed. Audio loop/packet behavior is corroborated by
[SoundCompressionTests](../Tests/Sandbox.Test.Integration/Resources/SoundCompressionTests.cs).

Native analysis now uses the supplied Ghidra 12.2 release with the installed JDK
27, entirely locally. REA 6.7.0 rejects that Ghidra version, so analysis uses
Ghidra's own headless analyzer instead; neither tool is a runtime dependency.
The bounded compiler analysis is incomplete, but recovered material compiler
branches and official compiler experiments establish the direct `.vtex_c`
binding and `Attributes` / `SystemAttributes` behavior. The native implementations
are not present in the public C# repository. The local compiler reports
`Failed to find compiler` for `.vpcf`, `.vpost` and `.vsnap`, sometimes with exit code
zero and no output. Tests therefore delete the previous target and require a new
compiled file, rather than trusting exit code zero. Missing authoring support
remains an implementation/compiler-compatibility limitation, not proof of
theoretical irrecoverability.

Run the isolated, native-engine-free MSTest suite:

```powershell
dotnet test engine\Sandbox.ResourceDecompiler\Tests\Sandbox.ResourceDecompiler.Tests.csproj
```

Run opt-in official compiler round trips on Windows:

```powershell
$env:SBOX_RESOURCE_COMPILER_ROOT = "D:\workspace\sbox-public\game"
dotnet test engine\Sandbox.ResourceDecompiler\Tests\Sandbox.ResourceDecompiler.Tests.csproj --filter "TestCategory=OfficialCompiler" --logger "console;verbosity=detailed"
```

These tests force real compilation in isolated temporary directories, remove
original sources, recover them, force recompilation and compare relevant data.
They also edit recovered LDR/HDR/cubemap images, material parameters, model vertex
positions and graph node names and confirm the compiled data changes.
They never recompile user assets. Without the environment variable,
official-compiler tests are inconclusive/skipped rather than reported passed.

The verified suite contains 85 passing cases, including 31 actual official-compiler
cases. Observed with the local compiler: twenty texture output formats compile
successfully after recovery, including multi-mip BC7 and BC6H cube fixtures;
recovered RGBA8888 samples
survive another compile unchanged. PCM and Opus fixtures preserve the complete
audio DATA block, streaming payload, rate, channels, loop bounds and GUID.
Whole-file hashes can still differ; even the baseline repeated compile
showed changing GUID blocks in these isolated fixtures despite stable DATA and
payload. The tests report baseline and round-trip hashes rather than claiming
whole-file equality. These observations are not validation of all historical
resource versions or all editor import options.

All ten high-precision uncompressed formats preserve their decoded sample EXRs
exactly after recompilation, including negative HDR and alpha values. BC6H is
measured separately: the gradient fixture has maximum absolute sample error
0.21191406 on recompression; its regression bounds are 0.25 maximum and 0.1 RMS.
This is not a universal error guarantee for other images.

Two material fixtures (constant color and imported image) also recover and
recompile with identical shader, integer/float/vector/texture parameter arrays
and all five attribute arrays. Numeric authoring attributes can populate several
runtime arrays; reconstruction deduplicates their names, preserves system texture
dimensions separately, and avoids adding dimensions that the compiler regenerates
for an inline `TextureColor`. This is a reconstructed material, not a raw KV3 dump
renamed to `.vmat`.

All 100 copied `.vmat_c` assets in the supplied ss2 directory were independently
parsed and recovered. With isolated compiled texture dependencies and the current
writer, 89 recompiled successfully. Eleven failed while loading their shader
dependencies with the available compiler; copying compiled custom shaders did not
make those dependencies loadable. Successful compilation alone does not establish equivalent
runtime data: compiler/shader-version differences can alter defaults, and the
recovered `.runtime.json` retains the original values for inspection.

Three copied assets from the supplied ss2 project were also independently
recovered and successfully recompiled: a 512x512 BC7 texture with eight mips and
Zstandard compression, a 4x4 BC1 texture, and an embedded 1024x1024 PNG texture.
The first two retained their pixel formats and mip counts; the PNG became an
RGBA8888 texture. These experiments used isolated copies, not the original
project assets.

The final copied-texture sweep recovered definitions and all stored mip images
for all 665 ss2 textures. Ninety-five include sprite-sheet metadata and are
explicitly recovered as static atlases, retaining `.sheet.bin`; their animation
sequencing is not restored. The three actual BC6H sky cubemaps and two half-float
HDR atlas textures also recompiled successfully with the official compiler.
This does not claim that all 665 textures were recompiled or are byte-identical.

Fresh copies of all 196 ss2 models were checked. Of these, 187 recovered editable
static model sources and all 187 produced new official compiler outputs. One build
still reported a missing material dependency: compiler output alone does not
guarantee complete appearance. The other nine models contain no recoverable
embedded default render geometry and yield runtime references only. All 196
retain their complete original block payloads separately, including data that
has not been restored to an authoring structure.
Asymmetric geometry verifies axis conversion, UVs and winding; an actual two-LOD
fixture verifies both LODs' triangle positions/UVs/winding and switch distance
after recompilation. The actual copied citizen model retains all five LOD
triangle counts (12860, 10476, 2538, 1376, 1188) and switch distances
`[0, 5, 20, 40, 70]` after recompilation. This is not a
restoration of animated character rigs or collision authoring.

The actual copied citizen animation graph recompiled with complete parsed DATA
equality. GUID equality is also enforced by the graph fixture. Whole-file native
resource equality is not claimed.
