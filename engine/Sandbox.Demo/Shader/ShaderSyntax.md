# s&box `.shader` 自定义语法与 Slang 的对应关系

## 1. 不是把所有自定义语法改写成 Slang

这里的“转换”分为三类：

1. **外层文件格式**：`HEADER`、`MODES`、`FEATURES` 等是引擎描述信息；`COMMON`、`VS`、`PS` 等是代码容器。示例导出器移除或拆开这些区块。
2. **引擎提供的宏**：`Attribute(...)`、`RenderState(...)` 等不是 Slang 内建函数，但可以通过 `system.fxc` 中的宏展开成编译器接受的源码。
3. **旧式 effect 注解**：`< ... >` 不是 s&box 独有的语法。Slang 提供 `-enable-effect-annotations` 兼容选项，因此不需要在示例中手动把它改写成其他语法。

本文描述当前仓库中的 [ShaderCompiler 示例](ShaderCompiler.cs)、[system.fxc](../../../game/core/shaders/system.fxc)，以及能从托管代码确认的引擎流程。宏展开示例调整了空白和换行，便于阅读；不是完整编译器输出。

**生成的 `.slang` 仍保留 `#include`、宏调用和 effect 注解，并不是已经完全展开的“纯 Slang 源码”。**

## 2. 外层区块如何处理

| `.shader` 写法 | 含义 | 示例导出后的内容 |
|---|---|---|
| `HEADER { ... }` | 描述、版本、开发用途等元数据 | 整块移除；不会变成 GPU 函数 |
| `MODES { ... }` | 引擎渲染模式及关联配置 | 整块移除；不会自动生成 Vulkan 管线 |
| `FEATURES { ... }` | 材质功能、可选值及规则 | 整块移除；示例不导出材质功能配置 |
| `COMMON { ... }` | 各阶段共享的源码 | 保留内部代码，去掉区块名称和外层花括号 |
| `VS { ... }` | 顶点程序 | 内部代码放进独立的 `.VS.slang` |
| `PS { ... }` | 像素程序 | 内部代码放进独立的 `.PS.slang` |
| `GS { ... }` | 几何程序 | 内部代码放进独立的 `.GS.slang` |
| `CS { ... }` | 计算程序 | 内部代码放进独立的 `.CS.slang` |
| 区块外的 `struct`、函数等 | 共享声明 | 示例保留到各阶段文件中 |

导出器目前只输出 VS、PS、GS、CS；不提供 HS、DS、RTX、PS_RENDER_STATE 的独立编译支持，也不是通用的完整 VFX 格式转换器。

例如：

```hlsl
COMMON
{
    #include "system.fxc"
}

PS
{
    float4 MainPs() : SV_Target0
    {
        return float4(1, 0, 0, 1);
    }
}
```

导出为：

```hlsl
#define PROGRAM VFX_PROGRAM_PS
#include "system.fxc"

#include "system.fxc"

float4 MainPs() : SV_Target0
{
    return float4(1, 0, 0, 1);
}
```

重复的 include 来自导出器头部和原来的 COMMON；`system.fxc` 的 include guard 防止重复定义。

`PROGRAM` 用来让共享头文件判断当前阶段，不负责选择编译入口。阶段和入口另外通过命令行指定：

```bat
slangc -enable-effect-annotations -target spirv -stage fragment -entry MainPs -I "..\..\..\game\core\shaders" sdl_triangle.PS.slang -o sdl_triangle.PS.spv
```

此命令假设当前目录为本文所在的 Shader 文件夹；实际使用可直接运行 [CompileTriangle.bat](CompileTriangle.bat)，它用自身路径定位文件。

## 3. 资源绑定注解：`Attribute`、`Default`、`Range`

输入：

```hlsl
Texture2D<float4> ColorTexture < Attribute("ColorTexture"); >;
float Exposure < Default(1.0); Range(0.0, 4.0); UiGroup("Color"); >;
```

按 `system.fxc` 宏定义展开后：

```hlsl
Texture2D<float4> ColorTexture < string attribute = "ColorTexture"; >;
float Exposure <
    float default1 = { 1.0 };
    float range1[2] = { 0.0, 4.0 };
    string uigroup = "Color";
>;
```

这里 `< ... >` 内保存的是注解，而不是纹理对象的泛型参数；`Texture2D<float4>` 中的 `<float4>` 才是资源元素类型。

几个常见宏的对应关系：

| 宏调用 | 宏展开后的内容 |
|---|---|
| `Attribute("Name")` | `string attribute = "Name"` |
| `Source(ViewportSize)` | `string source = "ViewportSize"` |
| `Semantic(PosXyz)` | `string semantic = "PosXyz"` |
| `Default(1)` | `float default1 = { 1 }` |
| `Range(0, 1)` | `float range1[2] = { 0, 1 }` |
| `UiGroup("Color")` | `string uigroup = "Color"` |
| `UiType(Slider)` | `string uitype = "Slider"` |
| `ActiveIf(S_ALPHA_TEST)` | `string activeif = "S_ALPHA_TEST"` |

**注解被编译器接受，不等于执行了引擎绑定逻辑。**

例如 `Attribute("ColorTexture")` 不会自动变成 `register(t0)` 或 `[[vk::binding(...)]]`；`Default(1.0)` 也不是给 GPU uniform 写入初始值。独立渲染程序仍需自己设置描述符、上传参数并处理资源布局。

## 4. 顶点字段：`Semantic` 与 HLSL semantic 不同

[三角形源码](../../../game/core/shaders/sdl_triangle.shader) 中：

```hlsl
float3 position : POSITION < Semantic(PosXyz); >;
```

宏展开后：

```hlsl
float3 position : POSITION < string semantic = "PosXyz"; >;
```

- `: POSITION` 是 HLSL 的语义标记，不是 s&box 自定义语法。
- `Semantic(PosXyz)` 是 s&box 的宏，展开为旧式注解。
- 兼容该注解不会自动配置调用者的顶点缓冲布局。

`SV_Position`、`SV_Target0`、`SV_DispatchThreadID` 和 `[numthreads(...)]` 也属于正常的着色器语言接口，不需要靠示例导出器翻译。

## 5. 渲染状态：`RenderState`

输入：

```hlsl
RenderState(DepthEnable, false);
```

宏展开后：

```hlsl
string g_renderState_DepthEnable < string arg1 = "false"; > = "";
;
```

最后的额外分号来自宏定义本身和调用处的分号。这是描述状态的数据，不是 GPU 指令。

**它不会让生成的 `.spv` 自己关闭深度测试。** 在引擎外使用该程序时，调用者需要在图形管线状态中设置深度测试、剔除、混合等选项。

同类的 `BoolAttribute(SupportsMappingDimensions, true)` 展开为：

```hlsl
string g_boolAttr_SupportsMappingDimensions < string value = "true"; > = "";
;
```

它描述引擎属性，不是一个可供 shader 运算使用的普通 `bool` 常量。

## 6. 采样器状态：`Filter`、`AddressU`

输入：

```hlsl
SamplerState LinearClamp < Filter(BILINEAR); AddressU(CLAMP); >;
```

宏展开后：

```hlsl
SamplerState LinearClamp <
    string SamplerState_Filter = "BILINEAR";
    string SamplerState_AddressU = "CLAMP";
>;
```

这些字符串保存期望的采样器状态。独立调用 Vulkan 时，仍需创建并绑定具有相应过滤和寻址模式的 sampler；SPIR-V 编译成功不代表引擎的采样器创建流程也被移植了。

## 7. 功能与组合：`Feature`、`StaticCombo`、`DynamicCombo`

`FEATURES` 中常见：

```hlsl
Feature(F_ALPHA_TEST, 0..1, "Alpha test");
```

这里的 `Feature`、`0..1` 范围及功能 UI 描述属于引擎格式。示例会移除整个 FEATURES 区块，不把它改写为 Slang `enum` 或常量。

阶段代码中的组合声明：

```hlsl
DynamicCombo(D_ALPHA, 0..1, Sys(ALL));
```

`system.fxc` 将 `DynamicCombo(...)` 定义为 `;`，所以预处理后这个调用只留下空语句。`StaticCombo`、`SpecializationCombo`、`DynamicComboFromFeature` 也采用类似的空宏定义。

**声明被移除，不代表相应的组合值已定义。** 示例不会自动枚举组合，也不会自动生成 Slang specialization constant。

例如：

```hlsl
#if D_ALPHA
    // 透明分支
#endif
```

要编译明确的变体，需要给 `slangc` 增加 `-DD_ALPHA=1`，即 `-D` 加上宏名 `D_ALPHA` 和值 `1`；不指定时，Slang 对未定义的预处理标识符会警告并按零处理。

仓库的 [YUV shader](../../../game/core/shaders/yuv_resolve_cs.shader) 有三个二值组合：`D_LIMITED_RANGE`、`D_BT709`、`D_ALPHA`。其 8 种显式宏组合已用 Slang 2026.20 编译验证。

## 8. include 与支持宏

`#include` 本身是编译器支持的预处理指令，不是 s&box 特殊语法。

`system.fxc` 提供前述宏，还提供 `PROGRAM` 阶段常量、纹理辅助函数，并继续包含公共采样器和描述符支持代码。

部分封装宏展开为正常的资源声明和采样表达式，例如 `CreateTexture2D(name)` 创建配套的 sampler 与 Texture2D 声明。这些展开仍需要相应 include 可用。

当前生成文件**不是单文件、自包含的发布物**：它仍依赖磁盘上的 include。`blendable.shader` 的独立编译测试因缺少 `math_general.fxc` 等文件失败，不能用空文件或虚构函数替代这些依赖并声称行为等价。

## 9. 与引擎内部实现的区别

| 环节 | 本示例 | 可从托管代码确认的引擎流程 |
|---|---|---|
| 阶段提取 | 手写区块解析，输出独立文件 | 调用原生 `ShaderTools.MaskShaderSource` |
| include | 保留给 `slangc` 处理 | 托管预处理展开非核心 include，核心 include 留给原生处理 |
| 组合 | 由使用者通过 `-D` 选择 | 枚举 Static/Dynamic Combo 并调用原生编译接口 |
| 编译调用 | BAT 启动 `slangc` | 调用 `vfx_vulkan.dll` 的接口 |
| 产物 | 各阶段 `.spv` | 打包为 `.shader_c` 资源 |

来源：[ProgramSource.cs](../../Sandbox.Engine/Systems/Render/ShaderCompile/ProgramSource.cs)、[ShaderPreprocessor.cs](../../Sandbox.Engine/Systems/Render/ShaderCompile/ShaderPreprocessor.cs)、[ShaderCompile.cs](../../Sandbox.Engine/Systems/Render/ShaderCompile/ShaderCompile.cs)。

这里不把示例的宏展开过程当作原生编译器实现的逐行复刻，也不根据托管入口推断原生 Slang 调用的全部参数。

## 10. 如何查看真实宏展开结果

在本文所在目录执行：

```bat
slangc -E -enable-effect-annotations -I "..\..\..\game\core\shaders" sdl_triangle.PS.slang
```

`-E` 只输出预处理结果，不生成 SPIR-V。输出包括展开后的 include、宏和条件编译结果，可以直接看到 `RenderState` 如何变为 `g_renderState_*` 字符串声明、`Semantic` 如何变为注解。

已实测：Slang 2026.20 可用兼容选项编译本目录的 [VS 文件](sdl_triangle.VS.slang) 与 [PS 文件](sdl_triangle.PS.slang)；不加该选项时，旧式注解会导致解析错误。

**结论：外壳被拆开，宏被预处理，旧式注解由 Slang 兼容模式接受；引擎的材质和管线配置不会因此自动变成 GPU 代码。**
