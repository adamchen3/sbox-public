# 把 s&box `.shader` 手写成等效 HLSL / Slang

下面用同一个 shader 展示每一步输入和结果。转换目标是独立可编译、绘制职责相同的程序，不是复刻引擎的二进制布局。绑定编号由本例指定；引擎内部还可能采用 BDA/bindless 布局。

## 1. 原始 `.shader`

绘制一个三角形，采样纹理，乘曝光参数；可选择反转 RGB。

```hlsl
HEADER
{
    Description = "Texture example";
    Version = 1;
}

MODES
{
    Default();
}

FEATURES
{
    Feature(F_INVERT, 0..1, "Invert RGB");
}

COMMON
{
    struct PixelInput
    {
        float4 position : SV_Position;
        float2 uv : TEXCOORD0;
    };
}

VS
{
    PixelInput MainVs(uint vertexId : SV_VertexID)
    {
        PixelInput output;
        output.uv = float2((vertexId << 1) & 2, vertexId & 2);
        output.position = float4(output.uv * 2.0 - 1.0, 0.0, 1.0);
        return output;
    }
}

PS
{
    StaticCombo(S_INVERT, F_INVERT, Sys(ALL));
    RenderState(DepthEnable, false);

    Texture2D<float4> ColorTexture < Attribute("ColorTexture"); >;
    SamplerState LinearClamp <
        Filter(BILINEAR);
        AddressU(CLAMP);
        AddressV(CLAMP);
    >;
    float Exposure < Default(1.0); Range(0.0, 4.0); UiGroup("Color"); >;

    float4 MainPs(PixelInput input) : SV_Target0
    {
        float4 color = ColorTexture.Sample(LinearClamp, input.uv);
#if S_INVERT
        color.rgb = 1.0 - color.rgb;
#endif
        return float4(color.rgb * Exposure, color.a);
    }
}
```

## 2. 处理外层区块

引擎加载时读取 HEADER、MODES、FEATURES；阶段编译时遮蔽不需要的区块。

手写转换时：

| 原始内容 | 怎么处理 |
|---|---|
| `HEADER` | 不写入 GPU 程序 |
| `MODES` | 本例只有 Default，直接选择下面的 VS/PS |
| `FEATURES` | 保存可选值，下一步选择一个组合 |
| `COMMON` | 去掉外壳，保留结构声明 |
| `VS` / `PS` | 去掉外壳，保留入口和正文 |

本例可以将 VS、PS 放在同一个文件，通过入口名称分别编译。引擎实际按阶段遮蔽；合并文件是手写实现的选择。

```hlsl
struct PixelInput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

// 后面接 MainVs、PS 参数和 MainPs，已经没有外层 VFX 区块。
```

## 3. 将 Feature 选值转成组合宏

原始关联：

```hlsl
Feature(F_INVERT, 0..1, "Invert RGB");
StaticCombo(S_INVERT, F_INVERT, Sys(ALL));
```

选择 `F_INVERT = 1`，效果相当于给预处理器：

```hlsl
#define S_INVERT 1
```

引擎实际通过宏名称/值编译参数传入；手写时可以用 `#define`。删除 `StaticCombo(...)` 声明。

预处理之前：

```hlsl
#if S_INVERT
    color.rgb = 1.0 - color.rgb;
#endif
```

预处理之后：

```hlsl
color.rgb = 1.0 - color.rgb;
```

选择 0 则删除这一行。`F_INVERT` 不自动变成 uniform；本例是编译时选程序，不是每次绘制修改 GPU 参数。

### DynamicCombo：编译多个变体，运行时选择

如果反转由运行时渲染条件决定，将本例的 StaticCombo 改为：

```hlsl
DynamicCombo(D_INVERT, 0..1, Sys(ALL));
```

程序中的条件也改为 `D_INVERT`：

```hlsl
float4 MainPs(PixelInput input) : SV_Target0
{
    float4 color = ColorTexture.Sample(LinearClamp, input.uv);
#if D_INVERT
    color.rgb = 1.0 - color.rgb;
#endif
    return float4(color.rgb * Exposure, color.a);
}
```

手写 HLSL/Slang 时删除 DynamicCombo 声明，保留 `#if`，分别以 `D_INVERT=0`、`D_INVERT=1` 编译。其他声明和 MainVs 使用第 6 节代码，不再保留 S_INVERT 的固定反转结果。

预处理后得到两个 PS：

```hlsl
// D_INVERT = 0
float4 MainPs(PixelInput input) : SV_Target0
{
    float4 color = ColorTexture.Sample(LinearClamp, input.uv);
    return float4(color.rgb * Exposure, color.a);
}
```

```hlsl
// D_INVERT = 1
float4 MainPs(PixelInput input) : SV_Target0
{
    float4 color = ColorTexture.Sample(LinearClamp, input.uv);
    color.rgb = 1.0 - color.rgb;
    return float4(color.rgb * Exposure, color.a);
}
```

把带 `#if D_INVERT` 的完整源码保存为 `ExampleDynamic.hlsl`，编译：

```bat
slangc ExampleDynamic.hlsl -lang hlsl -entry MainPs -stage fragment -target spirv -profile spirv_1_5 -DD_INVERT=0 -fvk-t-shift 0 0 -fvk-s-shift 0 0 -fvk-b-shift 0 0 -o Example.PS.Invert0.spv
slangc ExampleDynamic.hlsl -lang hlsl -entry MainPs -stage fragment -target spirv -profile spirv_1_5 -DD_INVERT=1 -fvk-t-shift 0 0 -fvk-s-shift 0 0 -fvk-b-shift 0 0 -o Example.PS.Invert1.spv
```

`-DD_INVERT=1` 是 `-D` 加宏名 `D_INVERT=1`。Slang 文件可改用 `.slang` 和 `-lang slang`。

本例 VS 不依赖该组合，只需编译一次。宿主为两个 PS 准备对应管线，绘制时选择：

```text
不反转 → 使用 PS.Invert0 对应管线
反转   → 使用 PS.Invert1 对应管线
```

**DynamicCombo 的“动态”指运行时选择变体，不是把宏变成可修改的 uniform。** 不要求多个源码文件；本例输出两个 SPIR-V 只是便于手写管理。s&box 可把程序变体和组合映射打包在同一个 `.shader_c` 中，等同的编译结果也可能共享。

## 4. 展开注解，分离宿主配置

引擎的 `system.fxc` 宏展开后，关键声明变成：

```hlsl
string g_renderState_DepthEnable < string arg1 = "false"; > = "";
;
Texture2D<float4> ColorTexture < string attribute = "ColorTexture"; >;
SamplerState LinearClamp <
    string SamplerState_Filter = "BILINEAR";
    string SamplerState_AddressU = "CLAMP";
    string SamplerState_AddressV = "CLAMP";
>;
float Exposure <
    float default1 = { 1.0 };
    float range1[2] = { 0.0, 4.0 };
    string uigroup = "Color";
>;
```

`<...>` 是旧式 HLSL Effects 注解语法，Slang 也支持；这些字段的用途由引擎解析规则定义。

手写独立程序时，不需要保留注解；将它们的职责移到宿主：

| 原始字段 | 手写实现 |
|---|---|
| `Attribute("ColorTexture")` | 宿主把选定纹理绑定给 ColorTexture |
| `Default(1.0)` | 宿主初始化 Exposure 为 1.0 |
| `Range(0.0, 4.0)` | 若提供参数 UI，设置其范围；不向 shader 自动插入 clamp |
| `UiGroup("Color")` | 若提供编辑器，在宿主实现分组 |
| `Filter(BILINEAR)` | 宿主创建对应过滤 sampler |
| `AddressU/V(CLAMP)` | 宿主设置 sampler 寻址 |
| `DepthEnable = false` | 宿主关闭深度测试 |

GPU 声明只剩：

```hlsl
Texture2D<float4> ColorTexture;
SamplerState LinearClamp;
float Exposure;
```

引擎实际用自己的参数解析器收集描述并改写源码；这里直接给出手写等效做法。

## 5. 指定资源绑定和参数布局

不要把 Exposure 留成没有明确宿主接口的全局变量。选择一个常量缓冲，给资源分配绑定：

```hlsl
Texture2D<float4> ColorTexture : register(t0, space0);
SamplerState LinearClamp : register(s1, space0);
cbuffer MaterialParameters : register(b2, space0)
{
    float Exposure : packoffset(c0.x);
};
```

本例的宿主按该布局上传参数；常量缓冲大小和分配满足目标 API 的对齐要求。t0/s1/b2 是人为分配，不是从 Attribute 名称推导的。

## 6. 最终可编译的 HLSL / Slang

选择反转分支后，将以下代码保存为 `Example.hlsl`。它也可以直接保存为 `Example.slang`，无需重写函数。

```hlsl
struct PixelInput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

Texture2D<float4> ColorTexture : register(t0, space0);
SamplerState LinearClamp : register(s1, space0);
cbuffer MaterialParameters : register(b2, space0)
{
    float Exposure : packoffset(c0.x);
};

PixelInput MainVs(uint vertexId : SV_VertexID)
{
    PixelInput output;
    output.uv = float2((vertexId << 1) & 2, vertexId & 2);
    output.position = float4(output.uv * 2.0 - 1.0, 0.0, 1.0);
    return output;
}

float4 MainPs(PixelInput input) : SV_Target0
{
    float4 color = ColorTexture.Sample(LinearClamp, input.uv);
    color.rgb = 1.0 - color.rgb;
    return float4(color.rgb * Exposure, color.a);
}
```

用 Slang 分别编译两个入口：

```bat
slangc Example.hlsl -lang hlsl -entry MainVs -stage vertex -target spirv -profile spirv_1_5 -fvk-t-shift 0 0 -fvk-s-shift 0 0 -fvk-b-shift 0 0 -o Example.VS.spv
slangc Example.hlsl -lang hlsl -entry MainPs -stage fragment -target spirv -profile spirv_1_5 -fvk-t-shift 0 0 -fvk-s-shift 0 0 -fvk-b-shift 0 0 -o Example.PS.spv
```

若文件名为 `Example.slang`，将输入文件及 `-lang hlsl` 改为 `Example.slang -lang slang`。本例已去掉 effect 注解，所以不需要 `-enable-effect-annotations`。

三个 shift 参数明确将 space0 的 t/s/b 编号映射为 Vulkan set0 的 binding 0/1/2；不应仅凭 D3D register 声明假定 Vulkan 绑定。

编译成功只得到 GPU 程序。要绘制，还需要宿主创建管线、绑定纹理/sampler/参数、设置视口，并以三个顶点发起绘制。跨 API 的坐标约定也需要匹配宿主。

## 7. 其他字段怎么手写

### `Source(...)`：宿主提供参数来源

原始：

```hlsl
float2 Viewport < Source(ViewportSize); >;
```

手写：

```hlsl
cbuffer ViewParameters
{
    float2 Viewport;
};
```

宿主更新 Viewport。不是生成 `ViewportSize()` 函数，也不是把来源字符串传给 GPU。

### `Semantic(...)`：保留 HLSL 输入语义，宿主设置顶点布局

原始：

```hlsl
float3 position : POSITION < Semantic(PosXyz); >;
```

手写结构成员：

```hlsl
float3 position : POSITION;
```

宿主配置对应顶点格式、偏移和缓冲。`PosXyz` 注解不直接等于某个 Vulkan location。

### `ActiveIf(...)`：宿主条件，不是自动生成 shader 分支

把参数可用条件放在宿主/UI 规则里；如果确实要改变 GPU 运算，显式写 `#if` 或运行时 `if`，不要只保留 ActiveIf 注解。

### Specialization：不要混同于普通组合宏

若有意使用 specialization，可在 Slang 中声明：

```hlsl
[[vk::constant_id(0)]] const int Mode = 0;
```

宿主在创建管线时提供 specialization 值。0 是手写分配的 ID，不是已恢复的引擎 Combo ID。

## 8. MODES：模式名映射到 shader / 静态组合

一次绘制选择一种模式；同一物体可以在一帧中分别以 Depth、Forward 或自定义模式绘制多次。MODE 不是 shader 宏，也不会自动创建渲染 pass。

### 8.1 声明规则与手动转换

```hlsl
MODES
{
    Forward();
    MyPass(S_MY_PASS);
    MyOtherPass(S_OTHER = 2);
    Depth("depth_only.shader");
}
```

| 声明 | 手动转换 |
|---|---|
| `Forward()` | 当前 shader 的命名模式；没有显式组合覆盖 |
| `MyPass(S_MY_PASS)` | 当前 shader，以 `S_MY_PASS = 1` 编译对应变体 |
| `MyOtherPass(S_OTHER = 2)` | 当前 shader，以 `S_OTHER = 2` 编译对应变体 |
| `Depth("depth_only.shader")` | 转换并编译另一个 shader，深度绘制时使用它；不是 include |

规则：

- 名称可自定义，但宿主必须按这个名称选择模式，声明本身不会触发绘制。
- 模式参数中的 `S_*` 是静态组合赋值，组合仍需在 shader 或 include 中声明。
- 不写 `= N` 时，显式组合参数的值为 **1**。
- 同一模式项可以指定多个静态组合值；不能混用 shader 文件替代与组合赋值。
- 空的 MODES 会补充 Default。`Default()` 不额外覆盖静态组合，材质 Feature 关联的值仍然有效。
- 完全省略 MODES 区块未验证，手写原始 VFX 时保留空块或 Default。

**反例：同一模式项不能同时替换 shader 和指定组合。**

```hlsl
// 不允许：同一条声明混用两种选择方式。
Depth("depth_only.shader", S_MODE_DEPTH = 1);
```

应二选一：

```hlsl
Depth(S_MODE_DEPTH = 1);    // 使用当前 shader 的组合变体。
// 或：
Depth("depth_only.shader"); // 改用另一个 shader。
```

限制针对同一模式项，不针对整个 MODES 块。不同模式可以分别使用两种形式，例如：

```hlsl
MODES
{
    Forward();
    MyPass(S_MY_PASS = 1);
    Depth("depth_only.shader");
}
```

**省略赋值与省略参数不是同一件事：**

```hlsl
MODES
{
    Default();
    MyPass(S_MY_PASS);       // 相当于 S_MY_PASS = 1
    MyPass2(S_MY_PASS = 0);  // 显式覆盖为 0
    MyPass3();              // 不覆盖 S_MY_PASS
}
```

以上三个自定义名称可以映射同一个组合的不同选择。`MyPass(S_MY_PASS)` 默认取 1，是模式参数解析规则，不是“变量存在就是真”；即使组合范围是 `0..3`，省略 `= N` 仍取 1。指定值必须是该组合允许的值。

`MyPass3()` **不意味着 `S_MY_PASS = 0`**。它没有覆盖这个组合：

- 若组合关联 Feature，继续使用材质 Feature 对应值。
- 对本文未关联 Feature、范围为 `0..1` 的示例，普通基准值是 0；不是由空括号赋值为 0。
- 如需保证某模式选择 0，明确写 `MyPass2(S_MY_PASS = 0)`。

每次绘制独立选择模式；先绘制 MyPass 再绘制 MyPass3，不表示把前一次模式的值 1 留给后一次。

**Feature 与 MODE 的优先级：MODE 指定的组合值覆盖 Feature 关联值。**

```hlsl
FEATURES
{
    Feature(F_MY_PASS, 0..1, "My pass");
}
MODES
{
    Default();
    MyPass(S_MY_PASS = 1);
}
PS
{
    StaticCombo(S_MY_PASS, F_MY_PASS, Sys(ALL));
    // 使用 #if S_MY_PASS 选择代码分支。
}
```

| 材质 F_MY_PASS | 绘制模式 | 最终 S_MY_PASS |
|---|---|---|
| 0 | Default | 0 |
| 1 | Default | 1 |
| 0 | MyPass | 1 |
| 1 | MyPass | 1 |

手动转换时先从材质 Feature 计算组合值，再应用所选 MODE 的覆盖值，最后将结果作为编译宏选择变体。MODE 只覆盖它指定的组合，不修改材质保存的 Feature，也不影响其他组合。

`F_MY_PASS` 是功能描述名，`S_MY_PASS` 是本例的程序组合宏。声明 Feature 不等于自动生成同名 `#define F_MY_PASS`；不要把 `#if S_MY_PASS` 换成 `#if F_MY_PASS` 来绕过 MODE。只有另外明确提供了独立 F_MY_PASS 宏时，检查它的分支才由该独立宏控制；未定义的宏在 `#if` 中通常按 0 处理。

**替代 shader 自己有多个 MODE 时：保留请求名称，查找同名组合覆盖，不递归替换文件。**

例如，原 shader A 的声明是：

```hlsl
MODES
{
    Forward();
    Depth("B.shader");
}
```

B 的相关声明是：

```hlsl
MODES
{
    Default(S_B = 2);
    Depth(S_B = 1);
    MyPass(S_B = 3);
}
PS
{
    StaticCombo(S_B, 0..3, Sys(ALL));
    // 使用 #if S_B == ... 选择代码分支。
}
```

A 的材质以 Depth 绘制时，处理顺序是：

```text
请求 A.Depth
→ A 的 Depth 声明指定 B.shader，加载 B
→ 在 B 的 MODES 中查找同名 Depth 的组合赋值
→ B 的 S_B 取 1
→ 使用 B 的对应程序变体完成这次 Depth 绘制
```

不是选择 B 的第一项，也不是自动改成 B.Default，更不会执行 B 的所有模式。

| B 的声明情况 | A.Depth 的结果 |
|---|---|
| 有 `Depth(S_B = 1)` | 使用 B，应用同名 Depth 的 `S_B = 1` |
| 没有 Depth，只有上述 Default / MyPass | 仍使用 B；没有同名覆盖，本例 `S_B = 0`，不会取 Default 的 2 |
| 把 Depth 改成 `Depth("C.shader")` | 仍使用 B，不继续加载 C；该项没有组合覆盖，本例 `S_B = 0` |

表中的 0 针对本例未关联 Feature 的 `0..3` 组合，不是“找不到模式就把所有组合清零”。替代 shader 自己的 Feature/组合计算仍需正常处理。B 的其他模式也不会自动添加为 A 材质的新模式。

这里需要区分两件事：**加载哪个 shader**，由 A 的 MODE 决定；**B 的静态组合值**，计算时仍按原请求名称 Depth 查找。第二步只读取组合赋值，不再次执行 B 的文件替代声明。因此不要依赖 `A → B → C` 的递归 MODE 跳转，需要使用 C 时直接在 A 中写 `Depth("C.shader")`。

手写等效 HLSL/Slang 时，本例应编译 B 的代码并传入 `-DS_B=1`，宿主将这个程序映射到 A 的 Depth 绘制用途。

依据是本机 `materialsystem2.dll` 的定向静态分析：`CMaterial2::LoadShadersAndSetupModes`（RVA `0x1A6A0`）替换一次 shader；组合设置函数（RVA `0x1D5C0`）在已加载 shader 中按原模式名查找赋值。以上不是运行时抓帧结果。

### 8.2 内建名称及无参默认值

以下来自本机 `engine2.dll` 的 `0xC3B9E0` 解析分支，不是所有版本的固定规则：

| 声明 | 无参处理 |
|---|---|
| `Default()` | 没有自动静态组合赋值 |
| `Forward()` / `VrForward()` | 所检查分支没有自动赋值；使用当前 shader |
| `Depth()`，且不是 MODES 第一项 | 自动记录 `S_MODE_DEPTH = 1` |
| `Depth()`，且是第一项 | 不进入上述自动赋值分支，不能直接翻译为 `S_MODE_DEPTH = 1` |
| `ToolsShadingComplexity()` | 所检查分支没有自动赋值；项目样例通常显式指定替代 shader |
| 自定义 `MyPass()` | 所检查分支没有自动赋值；如需变体，显式写 `MyPass(S_MY_PASS)` |

例如：

```hlsl
MODES
{
    Forward();
    Depth();
}
```

在所分析版本里，Depth 的组合覆盖相当于：

```hlsl
Depth(S_MODE_DEPTH = 1);
```

手动转换时为深度变体传入 `S_MODE_DEPTH=1`。该组合和深度代码仍需在 shader/include 中存在；此默认值不是自动生成深度算法、关闭颜色写入或创建深度目标。

为避免依赖声明顺序，建议明确写 `Depth(S_MODE_DEPTH)` 或 `Depth("depth_only.shader")`。这里列出的名称是已见项目实例，未恢复其他名称的特殊规则，不能猜测每个名称都有 `S_MODE_名称` 默认宏。

### 8.3 完整自定义 MODE shader

保存为 `game/core/shaders/custom_mode.shader`：

```hlsl
HEADER
{
    Description = "Custom mode triangle";
    Version = 1;
}
MODES
{
    Default();
    MyPass(S_MY_PASS);
}
FEATURES
{
}
COMMON
{
    #include "system.fxc"
}
struct VS_INPUT
{
    float3 position : POSITION < Semantic(PosXyz); >;
};
struct PS_INPUT
{
    float4 position : SV_Position;
};
VS
{
    PS_INPUT MainVs(VS_INPUT input)
    {
        PS_INPUT output;
        output.position = float4(input.position, 1.0);
        return output;
    }
}
PS
{
    StaticCombo(S_MY_PASS, 0..1, Sys(ALL));
    RenderState(DepthEnable, false);
    RenderState(DepthWriteEnable, false);
    RenderState(CullMode, NONE);
    RenderState(BlendEnable, false);
    RenderState(ColorWriteEnable0, RGBA);

    float4 MainPs(PS_INPUT input) : SV_Target0
    {
#if S_MY_PASS
        return float4(1, 0, 0, 1);
#else
        return float4(0, 1, 0, 1);
#endif
    }
}
```

该例 Default 使用普通组合值 0，MyPass 指定值 1。若改为 `StaticCombo(S_MY_PASS, F_MY_PASS, Sys(ALL))` 并声明 Feature，则 Default 跟随材质 Feature；MyPass 仍覆盖为 1。Default 不是“强制所有组合为 0”。

### 8.4 引擎内部 C# 按模式名绘制

下面的类需要编译进 **Sandbox.Engine**，普通游戏组件不能访问这些 internal 接口。调用前需加载/编译上述 shader，且引擎图形系统已初始化。

```csharp
using System;

namespace Sandbox;

internal static class CustomModeExample
{
    internal static void Draw(Texture target, bool useMyPass)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsValid || target.Width <= 0 || target.Height <= 0)
            throw new ArgumentException("Invalid target.", nameof(target));

        var material = Material.FromShader("shaders/custom_mode.shader");
        if (material is null || material.native.IsNull)
            throw new InvalidOperationException("Custom mode material is unavailable.");

        var modeName = new StringToken(useMyPass ? "MyPass" : "Default");
        if (material.native.GetMode(modeName).IsNull)
            throw new InvalidOperationException("Requested mode is missing.");

        using var recording = Graphics.Scope.Create();
        using var view = new Graphics.ManagedView
        {
            Color = target.native,
            ColorFormat = target.ImageFormat,
            Viewport = new Rect(0, 0, target.Width, target.Height),
            ShaderMode = modeName,
            SrgbWrite = false
        };

        view.SetCamera(
            Vector3.Zero, Rotation.Identity, 90.0f, 0.1f, 100.0f,
            (float)target.Width / target.Height, false, 0.0f, 0.0f);

        using (new Graphics.Scope(recording.Context, view))
        {
            var vertices = new Vertex[]
            {
                new(new Vector3(-0.8f, -0.8f, 0.0f)),
                new(new Vector3( 0.8f, -0.8f, 0.0f)),
                new(new Vector3( 0.0f,  0.8f, 0.0f))
            };
            Graphics.Draw(vertices.AsSpan(), vertices.Length, material);
        }
    }
}
```

在主线程、没有其他渲染 scope/场景录制运行时调用：

```csharp
using var target = Texture.CreateRenderTarget(
    "custom-mode-example", ImageFormat.RGBA8888, new Vector2(512, 512));

CustomModeExample.Draw(target, useMyPass: false); // 绿色
CustomModeExample.Draw(target, useMyPass: true);  // 红色
```

结果绘制到 target，不自动显示到屏幕。上例没有清屏，只覆盖三角形区域；要显示或继续采样，调用方应保留 target 到使用结束。C# 接口已按仓库核对，但尚未运行此完整绘制示例。

选择链路：

```text
view.ShaderMode = "MyPass"
  → Graphics.Draw
  → 当前 view 按模式名查找材质模式
  → MyPass 指定 S_MY_PASS = 1
  → 绑定对应程序和渲染状态
  → 绘制三角形
```

### 8.5 手写成独立 HLSL / Slang

删除 HEADER、MODES、FEATURES 和阶段外壳；保留结构/入口，移出注解与 RenderState，并将 StaticCombo 替换为编译宏。得到：

```hlsl
struct VS_INPUT { float3 position : POSITION; };
struct PS_INPUT { float4 position : SV_Position; };

PS_INPUT MainVs(VS_INPUT input)
{
    PS_INPUT output;
    output.position = float4(input.position, 1.0);
    return output;
}

float4 MainPs(PS_INPUT input) : SV_Target0
{
#if S_MY_PASS
    return float4(1, 0, 0, 1);
#else
    return float4(0, 1, 0, 1);
#endif
}
```

保存为 `CustomMode.hlsl`，分别编译：

```bat
slangc CustomMode.hlsl -lang hlsl -entry MainVs -stage vertex -target spirv -profile spirv_1_5 -o CustomMode.VS.spv
slangc CustomMode.hlsl -lang hlsl -entry MainPs -stage fragment -target spirv -profile spirv_1_5 -DS_MY_PASS=0 -o CustomMode.Default.PS.spv
slangc CustomMode.hlsl -lang hlsl -entry MainPs -stage fragment -target spirv -profile spirv_1_5 -DS_MY_PASS=1 -o CustomMode.MyPass.PS.spv
```

宿主为两个 PS 建立管线；默认绘制选 Default 管线，自定义绘制选 MyPass 管线。宿主负责匹配顶点 POSITION 布局、目标、深度/剔除/混合状态。MODES 映射存在宿主代码里，不需要翻译成 HLSL 函数。

## 9. 与实际引擎处理的对应关系

分析确认的主干：**阶段遮蔽 → DXC 预处理 → ANTLR 参数解析 → 布局/源码改写 → Slang → SPIR-V**。

以上手写方法用普通 cbuffer 实现等效参数职责。引擎还有真实的 packoffset、资源数组、BDA/bindless 改写路径，不能将本例当作精确 native 输出。

依据：[system.fxc](../../../game/core/shaders/system.fxc)、[ProgramSource.cs](../../Sandbox.Engine/Systems/Render/ShaderCompile/ProgramSource.cs)、[ShaderCompile.cs](../../Sandbox.Engine/Systems/Render/ShaderCompile/ShaderCompile.cs)，以及本机 DLL 的定向静态分析。未捕获某次引擎编译的完整最终源码。
