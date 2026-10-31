# s&box `.shader` 原生编译流程与自定义语法

## 1. 结论

s&box 的 `.shader` 不是纯 HLSL 或 Slang 文件，而是 **VFX 外层格式 + HLSL 风格程序 + 引擎参数注解**。

根据托管代码及本机原生 DLL 的定向分析，所确认的编译主干是：

```text
.shader
  → 加载 VFX：解析模式、功能、组合与规则
  → 按阶段遮蔽源码
  → 托管 include / #line 处理
  → 根据组合 ID 构造宏定义
  → DXC 预处理，处理原生提供的 include
  → 引擎 CHlslParser / ANTLR 解析参数、注解和入口
  → 计算参数布局，改写源码与资源声明
  → Slang 编译为 SPIR-V，并读取反射
  → 结合 VFX 注解形成参数和状态描述
  → 打包 shader 程序、组合和描述数据
  → .shader_c
```

这里有两类产物：

- **GPU 程序**：入口、运算、输入输出、纹理与参数接口、SPIR-V。
- **引擎描述**：功能选项、参数来源、默认值、UI、渲染状态、sampler 配置。

不是所有自定义字段都要变成另一种 shader 语言的语句。Slang 负责程序编译，不负责实现 s&box 的全部 VFX 和材质编辑器语义。

## 2. 证据与适用范围

本文基于仓库源码、`system.fxc` 宏定义，以及用户提供的原生 DLL。使用 Visual Studio `dumpbin` 和 **Ghidra 12.1.4 headless** 做本地静态分析，没有执行原生编译器或上传二进制。

| 本次分析的文件 | SHA-256 |
|---|---|
| `game/bin/win64/engine2.dll` | `D740D644EA5492E8E9EAADF5D61151F8791C73919CF447F8E4C7AE29A91E0DAC` |
| `game/bin/win64/vfx_vulkan.dll` | `5CFF66A4663EAF863DECD4498DAB91126D27696794B06FCDA46F376BB7476123` |

以下标记含义不同：

- **宏展开**：根据头文件定义得到的实际展开形式。
- **原生观察**：反编译中确认的调用、判断、数据写入或生成模板。
- **代码示意**：用来说明职责，不是一次引擎编译的精确源码 dump。

分析没有 PDB，也没有完整自动分析全部函数；反编译的类型、局部名称、部分分支和间接调用可能不完整。RVA 只适用于上述二进制。本文不声称恢复了每个字段、全部运行时绑定或每个 shader 的最终源码。

## 3. 从托管入口追踪到原生编译器

### 3.1 `Initialize` 与 `igen_engine` 的对应关系

[Interop.Engine.cs](../../Sandbox.Engine/Interop.Engine.cs) 中的初始化调用原生 `igen_engine`，取得供托管包装使用的函数表。

本机 `engine2.dll` 的 `igen_engine` 位于 RVA `0x35F7A0`，检查 hash **9355**，与当前托管初始化一致。实际函数表映射如下：

| 托管入口 | 函数表索引 | 原生包装 RVA | 后续调用 |
|---|---:|---:|---|
| `CVfx.CreateFromShaderFile` | 1478 | `0x3434A0` | 直接调用 `0xBF1F30` |
| `CVfx.GetPropertiesJson` | 1487 | `0x3436B0` | 本次确认映射，未完整分析实现 |
| `IVfx.CompileShader` | 2987 | `0x3572B0` | 接口虚表偏移 `0x10` |
| `ShaderTools.MaskShaderSource` | 3320 | `0x35CE10` | 直接调用 `0xC4CF40` |

[生成的 C++ 包装](../../../src/engine2/interop.engine.cpp) 却检查 hash **61550**，遮蔽函数索引也是另一版本。因此不能直接用它的索引套到当前 DLL；需要结合托管表和实际汇编核对。hash 一致也不保证整个工作树与 native build 完全一致。

### 3.2 托管层负责什么

1. [Shader.LoadFromSource](../../Sandbox.Engine/Resources/Shader/Shader.cs) 调用原生 `CVfx.CreateFromShaderFile`。
2. [ProgramSource.Compile](../../Sandbox.Engine/Systems/Render/ShaderCompile/ProgramSource.cs) 调用原生阶段遮蔽。
3. [ShaderPreprocessor](../../Sandbox.Engine/Systems/Render/ShaderCompile/ShaderPreprocessor.cs) 添加 `#line` 和 `system.fxc` include，展开可解析的非核心 include；核心 include 留给原生处理。
4. [ShaderProgram](../../Sandbox.Engine/Resources/Shader/ShaderProgram.cs) 通过原生迭代器枚举组合。
5. [ShaderCompile](../../Sandbox.Engine/Systems/Render/ShaderCompile/ShaderCompile.cs) 将组合 ID、阶段和上下文交给 `IVfx.CompileShader`，目标为 `SM_6_0_VULKAN`。
6. [ShaderSource.Serialize](../../Sandbox.Engine/Systems/Render/ShaderCompile/ShaderSource.cs) 打包编译程序和 FEATURE 数据；非核心资源还可嵌入阶段处理后的源码。

[hshader.def](../../Definitions/resources/hshader.def) 可用于理解包装语义，但生成互操作文件不是应该手工修改的实现位置。

### 3.3 原生层负责什么

下表的 RVA 均属于 `vfx_vulkan.dll`：

| RVA | 已确认的职责 |
|---|---|
| `0x20E760` | `CompileShaderAndGetVariablesInternal` 主干 |
| `0x20B460` | 解码组合 ID，构造宏名称/值，添加 `PROGRAM`、`SHADER_MODEL`、`VULKAN`、`SLANG` 等 |
| `0x214F10` | 构造宏参数及 `CVfxDxcIncludeHandler`，用 `IDxcCompiler3::Compile` 预处理 |
| `0x20D4C0` | 预处理之后添加引擎源码前缀 |
| `0x21BE90` | 构造 ANTLR 输入、`hlslvariablesLexerNewSSD`、token stream、`hlslvariablesParserNewSSD` |
| `0x21B580` | 根据 token 改写事件跳过范围、插入声明、重定位 globals |
| `0x21C150` | 组装最终源码；含普通重定位、BDA、RTX 等分支 |
| `0x210250` | 通过编译器接口编译改写后的 HLSL；失败诊断包含 `Failed to convert HLSL to SPIRV` |
| `0x207DC0` | Slang session、入口、代码输出与参数反射 |
| `0x222F50` | 变量和状态描述处理 |

因此，DXC 和 Slang 并非只是同时出现在导入表里：调用链确认了 **DXC 预处理 → 引擎参数解析/改写 → Slang 编译** 的关系。缓存命中、没有程序入口的状态阶段和 RTX 路径不一定经过全部步骤。

`CHlslParser` 是引擎的参数解析器，不应等同于完整 HLSL 编译器；完整程序检查和 GPU 代码生成仍由后续编译器承担。

## 4. 外层区块如何处理

```hlsl
HEADER
{
    Description = "Example";
    Version = 1;
}
MODES
{
    Default();
}
FEATURES
{
    Feature(F_ALPHA_TEST, 0..1, "Alpha test");
}
COMMON
{
    float3 Tint(float3 value) { return value * 0.5; }
}
PS
{
    float4 MainPs() : SV_Target0
    {
        return float4(Tint(float3(1, 0, 0)), 1);
    }
}
```

### 4.1 `HEADER`、`MODES`、`FEATURES`

这些不是普通 shader 声明，也不会整体交给 Slang。

| 字段 | `engine2.dll` 原生观察 | 处理结果 |
|---|---|---|
| `Version` | `0xC3D480` 用阶段值 11 遮蔽，然后用 `V_sscanf` 读取整数 | VFX 字段，不是 GPU 常量 |
| `MODES` | `0xC3BFD0` 用阶段值 10 遮蔽，调用 `0xC3B9E0` | 模式描述；无模式时补充 `Default` |
| 模式参数 | `0xC3B9E0` 解析 shader fallback 或静态组合赋值，禁止混用 | 选择 shader 或组合；特定分支生成 `S_MODE_%s` |
| `FEATURES` | `0xC3A860` 用阶段值 0 遮蔽后解析 | 功能、范围、选项及规则 |
| `Feature` | 要求 `F_` 前缀、禁止重复、检查 `0..N` 和选项完整性 | 功能描述，不自动变成同名 uniform |
| `FeatureUpgrade` | 查已声明功能并校验默认值范围 | 升级描述 |
| `FeatureRule` | `0xC3C150` 解析 `ChildOf`、`Requires/RequiresN`、`Allow/AllowN`、`==N` 等 | 引擎规则，不是 shader `if` |

其他 HEADER 字段如 `Description`、`DevShader` 的逐字段解析和序列化位置没有全部恢复，不能断言它们与 Version 使用同一分支。

### 4.2 `COMMON` 与阶段区块

阶段遮蔽函数 RVA `0xC4CF40` 会原地将不需要的字符变为空格，并保留相关换行。匹配阶段及 COMMON 的正文被保留，区块头和外层括号被遮蔽；不匹配区块被遮蔽。

选择上例的 PS 后，忽略空白和行号的 **HLSL/Slang 职责示意** 是：

```hlsl
float3 Tint(float3 value) { return value * 0.5; }

float4 MainPs() : SV_Target0
{
    return float4(Tint(float3(1, 0, 0)), 1);
}
```

对应的 GLSL fragment 程序可以示意为：

```glsl
#version 450
layout(location = 0) out vec4 outColor;

vec3 Tint(vec3 value) { return value * 0.5; }

void main()
{
    outColor = vec4(Tint(vec3(1, 0, 0)), 1);
}
```

GLSL 示例不是引擎实际生成的文本。真实入口是 `MainPs`，而不是 `PS` 区块名称。

原生遮蔽还处理注释及 VS/PS/GS/CS/RTX、特定输入结构等，但本次未运行验证全部非法输入或字符串中的注释标记，不将它视为已经完整验证的通用词法分析器。

**VS/PS 不一定要分源码文件，SPIR-V 也允许多入口模块。** 引擎按阶段管理和编译，不代表文件格式强制分开。

## 5. 参数注解：先承载信息，再由引擎解释

[system.fxc](../../../game/core/shaders/system.fxc) 将许多自定义辅助宏展开为旧式 effect 注解：

| 写法 | 宏展开的关键内容 | 原生解释 |
|---|---|---|
| `Attribute("Name")` | `string attribute = "Name"` | 参数属性关联名 |
| `Source(ViewportSize)` | `string source = "ViewportSize"` | 参数来源 |
| `Default(1)` | `float default1 = { 1 }` | 描述默认值，不是 GPU 赋值 |
| `Range(0, 1)` | `float range1[2] = { 0, 1 }` | 描述范围，不自动插入 `clamp` |
| `UiGroup("Color")` | `string uigroup = "Color"` | 编辑器分组 |
| `UiType(Slider)` | `string uitype = "Slider"` | 编辑器类型 |
| `ActiveIf(S_ALPHA_TEST)` | `string activeif = "S_ALPHA_TEST"` | 引擎条件描述，不自动插入 GPU 分支 |
| `Semantic(PosXyz)` | `string semantic = "PosXyz"` | 顶点输入描述，见第 6 节 |

例如：

```hlsl
float Exposure < Default(1.0); Range(0.0, 4.0); UiGroup("Color"); >;
```

宏展开为：

```hlsl
float Exposure <
    float default1 = { 1.0 };
    float range1[2] = { 0.0, 4.0 };
    string uigroup = "Color";
>;
```

独立 Slang 可用 `-enable-effect-annotations` 接受这种注解语法，但“能解析注解”不等于“实现了引擎语义”。

### 原生消费者

`vfx_vulkan.dll` RVA `0x225C30` 会：

- 查找 feature 或内建 `source`，检查参数类型和分量等格式；未知来源或格式不匹配有错误路径。
- 检查 feature 来源参数的 int/bool/float 限制及 bool 范围等条件。
- 保存 attribute、UI 信息、不同分量数的 default/range。
- 用 `CVfxStackMachineBuilder` 编译 `activeif`，保存条件数据；存在 256 字节上限及错误诊断。

`0x224E00` 另处理纹理的 Source/Attribute/Expression 和纹理处理描述，校验其组合及格式。不能把普通数值参数规则直接套到纹理。

[Shader.Editor.cs](../../Sandbox.Engine/Resources/Shader/Shader.Editor.cs) 中的变量 schema 也有属性名、来源、UI、默认值及范围字段，说明这些信息有引擎侧用途。

解析器收集注解及源码改写信息，生成器按 token 范围跳过或替换源码。本次未逐项验证所有注解在全部路径中是否都以相同方式移除。

`Source(ViewportSize)` 也不代表生成 `ViewportSize()` 函数调用。它关联参数来源；GPU 从实际参数接口取值。运行时如何填入每种 source 的数值，本次未全部追踪。

## 6. 顶点 `Semantic` 与 HLSL semantic 不是同一件事

```hlsl
float3 position : POSITION < Semantic(PosXyz); >;
```

宏展开：

```hlsl
float3 position : POSITION < string semantic = "PosXyz"; >;
```

原生有两条不同路径：

- `0x22B090` 读取 VS 输入成员的 `"semantic"` 字符串注解，保存到输入描述；非法类型有诊断。
- `0x20D120` 的 `BuildVertexAttributeMap` 拆开 HLSL semantic 名称与数字后缀，匹配含 `POSITION` 的表，生成输入映射。

GPU 输入职责可示意为：

```hlsl
struct VertexInput
{
    float3 position : POSITION;
};
```

```glsl
// location 编号是示例，不是引擎映射结果
layout(location = 0) in vec3 position;
```

`PosXyz` 不直接等于 `location = 0`，也不能单凭它推导顶点缓冲偏移或格式。`SV_Position`、`SV_Target0`、`SV_DispatchThreadID`、`[numthreads(...)]` 属于普通 shader 语法。

## 7. RenderState、sampler 状态与 BoolAttribute

```hlsl
RenderState(DepthEnable, false);
SamplerState LinearClamp < Filter(BILINEAR); AddressU(CLAMP); >;
```

宏展开的关键内容：

```hlsl
string g_renderState_DepthEnable < string arg1 = "false"; > = "";
;
SamplerState LinearClamp <
    string SamplerState_Filter = "BILINEAR";
    string SamplerState_AddressU = "CLAMP";
>;
```

原生观察：

- `0x1FD370` 的参数解析检查 `g_renderState_` 前缀。
- `0x222F50`、`0x2223A0` 等路径处理状态声明和值。
- `0x2240E0` 匹配 `SamplerState_` 前缀及状态表，解析字符串配置，检查非法类型。

GPU 程序仍只需要 sampler 和采样操作：

```hlsl
// HLSL / Slang 职责示意
SamplerState LinearClamp;
float4 ReadColor(Texture2D<float4> image, float2 uv)
{
    return image.Sample(LinearClamp, uv);
}
```

关闭深度测试不是 shader 指令，过滤和寻址也不是在 shader 中创建 Vulkan sampler。这些由引擎状态描述和运行时管线/资源管理承担；本次未完整追踪 Vulkan 创建调用。

`BoolAttribute(Name, true)` 同样展开为带注解的 `g_boolAttr_Name` 字符串声明，不是普通的 GPU `bool Name = true`。

## 8. Feature、Combo 与 specialization

### 8.1 `FEATURES` 如何变成可编译的 HLSL/Slang

`FEATURES` 区块和 `Feature(...)` 是 VFX 自定义语法。常见的静态组合路径不是把 Feature 直接转换成 GPU 变量，而是：

```text
Feature 功能描述
  → StaticCombo 将功能关联到程序组合
  → 所选组合值作为编译宏传入预处理器
  → 预处理选出程序代码
  → Slang 编译选出的 HLSL 风格程序
```

例如，原始 `.shader` 片段：

```hlsl
FEATURES
{
    Feature(F_ALPHA_TEST, 0..1, "Alpha test");
}

PS
{
    StaticCombo(S_ALPHA_TEST, F_ALPHA_TEST, Sys(ALL));

    float4 MainPs() : SV_Target0
    {
#if S_ALPHA_TEST
        return float4(1, 0, 0, 1);
#else
        return float4(0, 1, 0, 1);
#endif
    }
}
```

引擎解析功能描述及组合关联，选择 `F_ALPHA_TEST = 1` 对应的组合。其传给预处理器的效果相当于：

```hlsl
#define S_ALPHA_TEST 1

float4 MainPs() : SV_Target0
{
#if S_ALPHA_TEST
    return float4(1, 0, 0, 1);
#else
    return float4(0, 1, 0, 1);
#endif
}
```

这里的 `#define` 是效果示意：原生实际构造宏名称和值，通过编译参数传入，不一定向源码插入这一行。外层区块已被处理，`StaticCombo` 声明宏展开为空语句；示例省略该空语句、行号及其他预处理内容。

预处理后，后续 HLSL/Slang 程序相当于：

```hlsl
float4 MainPs() : SV_Target0
{
    return float4(1, 0, 0, 1);
}
```

若选择 `F_ALPHA_TEST = 0` 对应的组合，则保留绿色分支。这里的值在编译时选择，不是每次执行 `MainPs` 时读取一个可变的运行时参数。

因此，声明 `Feature(F_ALPHA_TEST, ...)` **不会自动生成 `float F_ALPHA_TEST` 或同名 uniform**。本例中程序使用的是关联后的 `S_ALPHA_TEST` 宏；通过 `Source(...)` 关联参数或 specialization 则属于其他路径，不能用这一例概括。

### 8.2 动态组合与原生解析

```hlsl
StaticCombo(S_ALPHA_TEST, F_ALPHA_TEST, Sys(ALL));
DynamicCombo(D_ALPHA, 0..1, Sys(ALL));

#if D_ALPHA
    float alpha = AlphaTexture.Load(int3(pixel, 0)).r;
#else
    float alpha = 1.0;
#endif
```

组合声明宏在 `system.fxc` 中展开为 `;`，但原生加载器已经解析了组合描述，并不是完全忽略它们。

`engine2.dll` RVA `0xC38930` 解析 Combo/Rule、include 内声明，识别 StaticCombo、DynamicCombo、SpecializationCombo、DynamicComboFromFeature、ShaderConstant 等，并验证名称、范围、来源和重复项。`ShaderConstant()` 有仅支持静态组合的检查。

`vfx_vulkan.dll` RVA `0x20B460` 将组合 ID 解码为所选数值，构造宏名称/值。DXC 预处理函数再组织 `name=value` 参数。因此选择 `D_ALPHA = 1` 时，分支的逻辑结果为：

```hlsl
float alpha = AlphaTexture.Load(int3(pixel, 0)).r;
```

### 8.3 Specialization 的区别

specialization 不能全部简化为重复预处理。[SpirvDump.cs](../../Sandbox.Engine/Systems/Render/ShaderCompile/SpirvDump.cs) 支持编译结果中的 `(constant_id, value)` 数据，以及模块和组合的对应关系。

Slang 与 GLSL 的 specialization 职责示意：

```hlsl
// Slang：ID 为示例，不是某个 Combo 的恢复结果
[[vk::constant_id(0)]] const int Mode = 0;
```

```glsl
layout(constant_id = 0) const int Mode = 0;
```

这不证明 `SpecializationCombo(...)` 被原样替换成上述源码。本次未完整恢复每个声明到常量 ID 的生成规则。

## 9. 原生源码改写不只是删除注解

以下格式来自 `vfx_vulkan.dll` 中的生成模板；代入的名称、数值和布局是示例，不是完整源码 dump。

### 9.1 参数布局及资源声明

主干 `0x20E760` 计算参数组大小和偏移，存在 16 字节对齐处理。生成器 `0x21B580` 对非 `$Globals` 参数可插入 `: packoffset( c%d.%c )`：

```hlsl
cbuffer ExampleParameters
{
    float Exposure : packoffset(c0.x);
};
```

同一生成器还有 `Texture2D %s[ %d ] : register( t%d, space%d );` 模板：

```hlsl
Texture2D ExampleTextures[8] : register(t0, space1);
```

这不是每个纹理都必然变数组的证据。实际编号由布局处理决定，不能从 Attribute 或 UiGroup 推导。

`0x21AFA0` 的 bindless 常量分支还生成 `BindlessConstants_t`、必要的 padding 及常量数组：

```hlsl
// 类型名和 g_constants 来自模板；组名、长度为示例
cbuffer ExampleConstants
{
    BindlessConstants_t g_constants[8];
};
```

### 9.2 BDA：从 GPU 地址读取 globals

`0x21C150` 的 BDA 分支生成静态 globals、push constant 地址表和读取函数。下面以一个 float 参数代入：

```hlsl
static float Exposure;

struct GlobalsBDAPushConstant_t
{
    uint64_t nGlobalsBDAPerStage[6];
};
[[vk::push_constant]] GlobalsBDAPushConstant_t g_GlobalsBDAPushConstant;

void FetchBDAGlobals(uint64_t address)
{
    // 原生模板：%s = vk::RawBufferLoad<%s>( address + %i*4 );
    Exposure = vk::RawBufferLoad<float>(address + 0 * 4);
}
```

对应矩阵路径还能生成 `transpose(...)`。`static float Exposure` 不代表 Default 被转换为 GPU 初始值；这里从地址取得参数。

用 GLSL buffer reference 辅助解释相同职责：

```glsl
#version 460
#extension GL_EXT_buffer_reference : require
#extension GL_EXT_shader_explicit_arithmetic_types_int64 : require

layout(buffer_reference, std430, buffer_reference_align = 4)
readonly buffer ExampleGlobals
{
    float Exposure;
};
layout(push_constant, std430) uniform GlobalsAddresses
{
    uint64_t addresses[6];
};

float ReadExposure(uint stageIndex)
{
    ExampleGlobals globals = ExampleGlobals(addresses[stageIndex]);
    return globals.Exposure;
}
```

GLSL 不是 native 输出。偏移 0、阶段索引以及地址表布局是简化示意；实际调用时机、能力开关和地址管理未完整恢复。BDA 需要设备能力和有效 GPU 地址，不能随意替代普通 uniform block。

RTX 路径还可生成 `__RTX_MATERIAL_DATA__` 及含 `NonUniformResourceIndex(...)` 的宏，不宜直接套用普通 VS/PS 参数布局。

## 10. `.shader`、HLSL、Slang、GLSL 完整对照

本节是教学示例，**不是引擎必然输出**。为了便于比较，GPU 代码采用普通参数组，且所有绑定编号人为指定。

### 10.1 原始 VFX 片段

```hlsl
PS
{
    RenderState(DepthEnable, false);
    Texture2D<float4> ColorTexture < Attribute("ColorTexture"); >;
    SamplerState LinearClamp < Filter(BILINEAR); AddressU(CLAMP); >;
    float Exposure < Default(1.0); Range(0.0, 4.0); UiGroup("Color"); >;

    float4 MainPs(float2 uv : TEXCOORD0) : SV_Target0
    {
        return ColorTexture.Sample(LinearClamp, uv) * Exposure;
    }
}
```

这是阶段片段，不包含完整 HEADER、MODES 和 VS。注解展开后形成第 5、7 节的字符串/数值描述，阶段外壳被遮蔽。

### 10.2 信息的去向

| 原始字段 | 引擎描述 | GPU 职责 |
|---|---|---|
| `Attribute("ColorTexture")` | 属性关联名 | 纹理资源 |
| `Default(1.0)` | 默认值 | 从参数接口读取 Exposure |
| `Range(0.0, 4.0)` | 范围 | 不自动增加 clamp |
| `UiGroup("Color")` | 编辑器分组 | 无对应指令 |
| `Filter/AddressU` | sampler 配置 | sampler 资源与采样操作 |
| `DepthEnable` | 深度状态描述 | 无关闭深度测试的 shader 语句 |

此表不是实际序列化格式或 schema JSON。

### 10.3 HLSL 接口示意

```hlsl
Texture2D<float4> ColorTexture : register(t0, space0);
SamplerState LinearClamp : register(s1, space0);
cbuffer MaterialParameters : register(b2, space0)
{
    float Exposure;
};

float4 MainPs(float2 uv : TEXCOORD0) : SV_Target0
{
    return ColorTexture.Sample(LinearClamp, uv) * Exposure;
}
```

这里没有 `Exposure = 1.0` 或 `clamp(Exposure, 0.0, 4.0)`。实际引擎可能采用第 9 节的 BDA/bindless 或其他布局，而不是这个普通参数组。

### 10.4 Slang 接口示意

```hlsl
[[vk::binding(0, 0)]]
Texture2D<float4> ColorTexture;
[[vk::binding(1, 0)]]
SamplerState LinearClamp;
[[vk::binding(2, 0)]]
cbuffer MaterialParameters
{
    float Exposure;
};

[shader("fragment")]
float4 MainPs(float2 uv : TEXCOORD0) : SV_Target0
{
    return ColorTexture.Sample(LinearClamp, uv) * Exposure;
}
```

这里用 `vk::binding(binding, set)` 显式说明 Vulkan 绑定，用阶段属性说明入口。没有证据表明引擎一定添加这些字面属性；它也通过 Slang API 配置编译。

Slang 本身能编译 HLSL 风格源码，因此不需要把所有函数改写成另一种表面语法。

### 10.5 Vulkan GLSL 职责示意

```glsl
#version 450

layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform texture2D ColorTexture;
layout(set = 0, binding = 1) uniform sampler LinearClamp;
layout(set = 0, binding = 2, std140) uniform MaterialParameters
{
    float Exposure;
};

void main()
{
    outColor = texture(sampler2D(ColorTexture, LinearClamp), uv) * Exposure;
}
```

用分离 texture/sampler 对应 HLSL/Slang 两个资源，用输入输出声明说明 TEXCOORD/SV_Target 的职责。location 仍是示例，不是引擎映射结果。

三段 GPU 代码都说明“采样纹理并乘参数”，但不负责材质 UI 或状态创建，也不保证二进制与布局完全一致。**本次确认的主干没有先生成 GLSL 的步骤。**

## 11. 能确认什么，还有什么没有恢复

| 问题 | 当前结论 |
|---|---|
| 托管与 native 的对应关系 | 校验了 hash、函数表及包装 RVA，发现生成 C++ 文件版本差异 |
| 主编译流程 | 跟踪了加载、遮蔽、组合、DXC、ANTLR、改写、Slang 和打包 |
| 外层字段 | 恢复 Version、模式、功能、组合/规则关键解析；未穷举全部 HEADER |
| 参数和状态 | 跟踪了 source、attribute、UI、默认值/范围、activeif、输入 semantic、状态消费者 |
| 改写后的 shader 语法 | 恢复了生成模板，提供单独标记的三种语言示例 |
| 实际最终 Slang 源码 | 未在编译入口做动态捕获，不能用 Demo 导出替代 |
| 确切 binding、location、常量 ID | 不能仅从字段名称或示例推导 |
| 全部运行时 Vulkan 创建和绑定 | 未完整追踪 |

`Programs[].Source` 或嵌入资源的源码是托管阶段处理结果，不一定是原生最后送入 Slang 的文本。SPIR-V dump 能观察实际模块及组合映射，但不能完整恢复编辑器元数据。

本目录的 Demo 导出器和 BAT 是独立编译演示，不等同于 native pipeline。引擎的真正处理包括 **消费描述、组合选择、参数布局和资源接口改写**，不只是拆开区块并启动 `slangc`。
