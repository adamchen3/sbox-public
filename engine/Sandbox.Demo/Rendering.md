# s&box 三角形渲染流程：从 WebGL 到引擎封装

本文结合 [SdlTest3 示例](Sdl/SdlTest3.cs)，解释：已有三角形顶点、Shader 和 SwapChain 时，s&box 如何把三角形画到窗口。

本文描述当前仓库中的实现。原生图形后端的内部管线创建、缓存和同步细节，没有全部包含在托管源码中，不应把下面的对应关系理解为底层 API 对象的一一映射。

## 1. 核心流程没有变化

无论 WebGL、Stride 还是 s&box，基本流程都是：

```text
上传顶点
    → 说明顶点布局
    → 选择 Shader 和渲染状态
    → 绑定输出目标
    → 设置参数
    → Draw
    → 提交
    → 显示
```

区别不在 GPU 的工作原理，而在状态如何组织：

- WebGL 把许多操作集中在 `gl` 接口上，由调用者逐个设置。
- Stride 用 Buffer、Effect、PipelineState、CommandList 等对象组织它们。
- s&box 用 GpuBuffer、VertexLayout、Material、RenderAttributes、Graphics、IRenderContext 等对象组织它们。

## 2. 与 WebGL 的对应关系

| WebGL 操作或概念 | s&box 对应操作或对象 |
|---|---|
| `canvas` | SDL 窗口，示例中由 SdlWindow 管理 |
| 默认绘图缓冲区和浏览器展示机制 | SwapChain、颜色目标和显式 Present |
| `gl.createBuffer()` | `GpuBuffer<T>` |
| `gl.bufferData(...)` | Buffer 的 `SetData(...)` |
| `vertexAttribPointer(...)` | VertexLayout，描述字段格式、偏移和语义 |
| 编译、链接得到 WebGLProgram | 引擎 Shader 资源与 Material 的 shader mode；不是完全一对一 |
| `gl.useProgram(...)` 和部分状态设置 | `RenderTools.SetRenderState(...)` |
| uniforms、纹理和 Buffer 参数 | RenderAttributes |
| `gl.viewport(...)` | 原生渲染上下文的 `SetViewport(...)` |
| `gl.clear(...)` | 原生渲染上下文的 `Clear(...)` |
| `gl.drawArrays(TRIANGLES, 0, 3)` | 原生渲染上下文的 `Draw(Triangles, 0, 3)` |
| 浏览器隐式处理命令和展示 | s&box 显式 Submit、Present |

WebGL 的一个 `gl` 接口，在 s&box 中被拆分成了设备、资源、命令录制、材质、参数和窗口呈现等职责。

## 3. 我要画什么：顶点、Buffer 和布局

### 3.1 Vertex：一个顶点包含的数据

CPU 顶点数组只是普通内存。绘制前需要把数据上传到 GPU Buffer。

[内置 Vertex](../Sandbox.Engine/Resources/Vertex.cs) 包含：

- Position：位置；
- Color：颜色；
- Normal：法线；
- TexCoord：纹理坐标；
- Tangent：切线。

三角形不一定需要这些全部字段。Shader 只读取位置时，可以定义只含位置的顶点结构，并标记相应的布局语义。

### 3.2 GpuBuffer：GPU 上的缓冲资源

[GpuBuffer<T>](../Sandbox.Engine/Systems/Render/GpuBuffer.cs) 持有 GPU Buffer：

```csharp
var buffer = new GpuBuffer<Vertex>(
    3,
    GpuBuffer.UsageFlags.Vertex );

buffer.SetData( vertices );
```

- 构造函数创建指定用途和容量的缓冲资源。
- `SetData` 把 CPU 数据交给引擎上传路径。
- `Dispose` 释放缓冲资源。

当前实现的 `SetData` 经由 `RenderTools.SetGPUBufferData`，并使用当前 `Graphics.Context`。不能假定所有上传操作都与当前渲染上下文无关。

### 3.3 VertexLayout：怎样解释 Buffer 中的字节

Buffer 本身只是字节，绘制时还需要知道：

```text
每个顶点多少字节？
位置从哪里开始？
位置是三个 float 吗？
颜色是四个 byte，还是四个 float？
哪个字段对应 Shader 的 POSITION / COLOR？
```

[VertexLayout](../Sandbox.Engine/Systems/Render/VertexLayout.cs) 从字段类型及布局标记构建原生布局，并按顶点类型缓存。例如：

```csharp
[VertexLayout.Position]
public Vector3 Position;

[VertexLayout.Color]
public Color32 Color;
```

它集中完成了 WebGL 中 `vertexAttribPointer` 所描述的那部分工作。

## 4. 我要怎样画：Shader、Material 和参数

### 4.1 Shader 与 Material

WebGL 示例可以直接编译、链接 GLSL 字符串。s&box 通常通过引擎 Shader 资源和材质使用 GPU 程序：

```csharp
var material = Material.FromShader( "shaders/my_triangle.shader" );
```

这是调用形式示意；对应的 Shader 资源需要实际存在。

[Material.FromShader](../Sandbox.Engine/Resources/Material/Material.Static.cs) 创建或复用基于该 Shader 的材质：

```text
Shader
    定义 GPU 程序、输入、可用模式、渲染状态等

Material
    使用某个 Shader 的材质资源，承载相应材质配置

Material mode
    选择当前绘制使用的模式，例如 Forward
```

Material 不只是纹理集合，也不等于裸的 WebGLProgram 或一个 VkPipeline。

s&box Shader 文件还可以声明剔除、深度、混合状态，以及不同模式、功能和变体。例如 [ui_basic.shader](../../game/core/shaders/ui_basic.shader) 声明了默认/Forward 模式、顶点和像素阶段，以及关闭背面剔除、关闭深度写入等状态。

因此，Stride 中显式放入 PipelineStateDescription 的信息，在 s&box 中有一部分放在了 Shader/Material 体系里。

材质应在允许创建资源的初始化阶段准备好；当前 `Material.Create` 不允许在活跃的 Graphics 渲染作用域内调用。

### 4.2 RenderAttributes：本次绘制的参数与资源

[RenderAttributes](../Sandbox.Engine/Systems/Render/RenderAttributes.cs) 按名字传递参数：

```csharp
attributes.Set( "TransformMat", Matrix.Identity );
attributes.Set( "Texture", texture );
attributes.SetCombo( "D_WORLDPANEL", 0 );
```

它涉及：

- 类似 WebGL uniforms 的参数；
- 纹理、Buffer 等资源；
- Shader 变体选择。

Combo 是 Shader 变体选择，不应简单等同于普通 uniform。

例如 Shader 中的声明：

```hlsl
float4x4 g_matTransform < Attribute( "TransformMat" ); >;
```

与 C# 侧同名参数关联。

### 4.3 RenderTools：进入原生渲染状态绑定

[RenderTools.SetRenderState](../Definitions/engine/Render/RenderTools.def) 接收：

```csharp
RenderTools.SetRenderState(
    context,
    attributes,
    materialMode,
    vertexLayout,
    stats );
```

也就是把材质模式、顶点布局、参数和资源、当前上下文一起交给原生渲染器。

WebGL 中逐条执行的 program、输入布局、资源状态设置，在这里收束成较高层的入口。托管代码可以确认这个边界，但无法仅凭它完整展开原生 Vulkan pipeline 如何创建和缓存。

## 5. 画到哪里、什么时候执行

### 5.1 g_pRenderDevice：设备级入口

[g_pRenderDevice](../Definitions/common/Render/renderdevice.def) 是原生渲染设备的托管访问入口，提供：

- GPU Buffer 等资源创建；
- SwapChain 创建、更新和销毁；
- SwapChain 颜色、深度目标获取；
- 渲染上下文创建和释放；
- Present；
- GPU 同步相关操作。

它不是某个物体或相机，而是设备能力和资源管理的入口。

### 5.2 IRenderContext：录制渲染工作

[IRenderContext](../Definitions/common/Render/IRenderContext.def) 接收：

```text
绑定目标
设置 viewport
清屏
绑定顶点 Buffer
Draw / DrawIndexed
Submit
```

它在用途上更接近 Stride 的图形命令列表，或现代图形 API 的命令录制接口，而不是浏览器的 WebGL 上下文生命周期。

Draw 描述绘制工作，不代表调用返回时 GPU 已经执行完毕；Submit 也不应理解为等待 GPU 全部完成。

### 5.3 SwapChain 与渲染目标

[SdlWindow](../Sandbox.Engine/Core/Windowing/SdlWindow.cs) 管理 SDL 窗口与对应 SwapChain 的生命周期。

SwapChain 与窗口图像呈现关联；实际绘制时需要绑定渲染目标。例如：

```csharp
var color = g_pRenderDevice.GetSwapChainTexture(
    swapChain,
    SwapChainBuffer.BufferColor );
```

区分三个概念：

- 颜色目标存储绘制结果。
- 深度目标辅助深度测试。
- SwapChain 组织窗口图像的呈现。

深度目标不是所有三角形绘制都必须有。如果不使用深度测试，只画一个三角形，可以不需要深度目标。需要深度测试时，要正确绑定、清理并匹配深度状态，不能直接照搬其他引擎的深度约定。

## 6. Graphics、Scope 和 ManagedView 为什么存在

### 6.1 Graphics：托管绘制门面

[Graphics](../Sandbox.Engine/Systems/Render/Graphics.cs) 提供方便的静态调用：

```csharp
Graphics.Draw( buffer, material );
```

这里没有显式传入 context，是因为 Graphics 从当前线程的渲染状态中取得它。因此调用前必须建立有效的当前渲染环境。

### 6.2 Graphics.Scope：安装和恢复当前渲染环境

当前示例的 `new Graphics.Scope(context, view)` 会：

1. 保存之前的 Graphics 状态；
2. 设置当前原生 context；
3. 设置当前 view 和 attributes；
4. 绑定 view 的目标和 viewport；
5. 退出时恢复之前的状态。

它是管理当前绘制环境的作用域，不是 GPU 上的资源。

注意不同入口的区别：

- `new Graphics.Scope(context, view)` 不会自动提交调用者创建的 context。
- `Graphics.Scope.Create()` 创建 standalone 作用域，并有自动提交和释放 context 的逻辑。

不能笼统地认为退出任意 Scope 就完成了 Submit。

### 6.3 Graphics.ManagedView：当前视图的描述和适配

[ManagedView](../Sandbox.Engine/Systems/Render/Graphics.ManagedView.cs) 描述：

- 颜色与深度目标；
- viewport 和深度范围；
- 颜色格式、MSAA；
- 相机、视锥相关信息；
- 材质模式选择；
- 某些引擎绘制 API 依赖的上下文。

它的 `Bind` 绑定目标并设置 viewport。

GPU 不认识 ManagedView，也不要求画三角形时必须存在它。示例使用它，是为了接入现成的 Graphics API：

```text
底层直接绘制：
    IRenderContext + Buffer + 布局 + 材质模式 + 参数 + 输出目标

借用托管便利层：
    再加 Graphics.Scope / ManagedView
```

## 7. Graphics.Draw 展开后是什么

[Graphics.Draw 的顶点 Buffer 重载](../Sandbox.Engine/Systems/Render/Graphics.Draw.Buffer.cs) 的核心可简化为：

```csharp
// 调用链示意，省略验证和统计细节。
var layout = VertexLayout.Get<T>();

RenderTools.SetRenderState(
    context,
    attributes,
    materialMode,
    layout,
    stats );

context.BindVertexBuffer( 0, vertexBuffer.native, 0 );
context.Draw( RenderPrimitiveType.RENDER_PRIM_TRIANGLES, 0, 3 );
```

因此：

```text
Graphics.Draw
    ├─ 确认处于有效渲染 Scope 中
    ├─ 检查 Buffer / Material
    ├─ 根据顶点类型取得 VertexLayout
    ├─ 根据当前 View 选择 Material mode
    ├─ RenderTools.SetRenderState
    ├─ context.BindVertexBuffer
    └─ context.Draw
```

最后两步与 WebGL 的绑定顶点数据、调用 drawArrays 很接近。主要封装在前面的状态准备阶段。

## 8. SDL 示例的一帧

[SdlTest3](Sdl/SdlTest3.cs) 当前的一帧可以概括为：

```text
处理事件、执行引擎帧更新
    │
更新 SwapChain 尺寸
    │
取得颜色和深度目标
    │
创建 IRenderContext
    │
设置引擎全局 bindless 资源表
    │
创建 ManagedView
    └─ 输出目标、viewport、格式
    │
进入 Graphics.Scope
    ├─ 安装当前 Graphics 状态
    └─ 绑定目标和 viewport
    │
Clear
    │
必要时上传顶点
    │
设置 RenderAttributes
    │
Graphics.Draw
    ├─ 布局和材质模式
    ├─ SetRenderState
    ├─ BindVertexBuffer
    └─ Draw
    │
退出 Scope，恢复 Graphics 状态
    │
BeginSubmittingDisplayLists(swapChain)
    │
context.Submit()
    │
Present(swapChain)
    │
释放 context 和本次取得的目标句柄
```

全局 bindless 资源表属于引擎资源绑定体系，让 Shader 能访问引擎管理的相关资源；它不是三角形顶点布局的一部分。

要区分：

- Draw：描述一次绘制。
- Submit：提交录制的工作。
- Present：请求呈现窗口图像。

带 SwapChain 的 `BeginSubmittingDisplayLists` 版本还用于标记该帧的 GPU 计时开始；它本身不是 Draw 或 Present。

资源释放与图形设备销毁顺序也很重要：Buffer、渲染上下文、目标句柄、SwapChain 等应在原生设备退出前正确结束使用和释放。

## 9. 为什么当前顶点坐标使用像素，而不是 -0.5 到 0.5

这是选用的 Shader 决定的，不是 s&box 强制要求。

一个简单 WebGL 顶点着色器可以直接输出裁剪空间位置：

```glsl
gl_Position = vec4(aVertexPosition, 1.0);
```

而当前 s&box 示例使用 [Material.UI.Basic](../Sandbox.Engine/Resources/Material/Material.UI.cs)。它的 [顶点着色器](../../game/core/shaders/ui/vertex.hlsl) 接收像素位置，再结合变换矩阵、viewport 转换到裁剪空间：

```text
当前示例：
    像素位置 → UI Shader 转换 → 裁剪空间

简单 WebGL：
    输入已是裁剪空间位置 → 直接输出
```

如果自定义最简单的 s&box Shader，让 VS 直接输出输入位置，就同样可以使用 -0.5 到 0.5 的顶点，并且不需要 UI 特有的 TransformMat、LayerMat 等参数。

当前 GPU Buffer 方案绕过了 Painter，但仍借用了 UI Shader；它还不是与最小 WebGL 示例完全等价的自定义 Shader 版本。UI Shader 还包含纹理、颜色空间及其他 UI 处理，不能把它当成只做位置透传和纯色输出的 Shader。

## 10. 不要混淆两层 CommandList

### 托管 Sandbox.Rendering.CommandList

[托管 CommandList](../Sandbox.Engine/Systems/Render/CommandList/CommandList.Resources.cs) 保存可回放的托管操作。例如先记录：

```csharp
commands.Draw( buffer, material );
```

回放时，再调用 Graphics 绘制 API。它的 Execute 不等于 GPU 已经执行完毕，也不等于 Present。

### 原生 IRenderContext

原生上下文接收更底层的渲染工作，最终 Submit。

两条示例路径：

```text
Painter
    → 托管 CommandList
    → Graphics / 原生绘制辅助 API
    → IRenderContext
    → Submit
    → Present
```

```text
Graphics.Draw
    → RenderTools + IRenderContext
    → Submit
    → Present
```

画三角形不强制要求经过托管 CommandList，更不强制要求经过 Painter。示例保留两条路径供比较。

## 11. 正常游戏场景为什么有更多对象

“画出已知的三个顶点”和“决定整个场景应该怎样画”是不同层次的问题。

正常场景还涉及：

```text
相机和可见性
    → 收集物体
    → 准备 Mesh、Material、光照等数据
    → 安排阴影、深度、主场景、透明等阶段
    → 后处理、UI 等
    → 输出到窗口
```

仓库中的实验性托管场景渲染器 [RenderSystem](../Sandbox.SceneRenderer/Pipeline/RenderSystem.cs) 展示了：

```text
Collect → Prepare → Setup → Record → Submit
```

当前仓库同时保留原生场景渲染路径。[ManagedSceneRendering](../Sandbox.Engine/Systems/Render/ManagedScene/ManagedSceneRendering.cs) 是受开关控制的替代入口，不能把托管 RenderSystem 当成所有场景唯一的渲染路径。

对于只画三角形的实验，Scene、GameObject、CameraComponent、MeshRenderer 并不是必需对象。它们主要解决场景组织和渲染调度，而不是单次底层 Draw 本身。

## 12. 一句话记住职责

> Buffer 存顶点；Layout 解释顶点；Shader 决定计算；Material 选择和配置 Shader；Attributes 提供参数；Context 记录操作；Render Target 接收像素；Submit 提交工作；SwapChain/Present 负责窗口呈现。

Graphics、Scope 和 ManagedView 则让这些操作方便地接入 s&box 已有的托管绘制体系。

## 13. 不初始化 Graphics：SdlTest4 原生路径

[SdlTest4](Sdl/SdlTest4.cs) 验证了另一条路径：在当前环境中，执行原生 `SourceEnginePreInit` 后，可以直接使用设备和原生材质系统绘制，不需要 `Graphics.Initialize`、Graphics.Scope、ManagedView、Painter，也不需要 `SourceEnginePanelAppInit`。

这不意味着只创建 SwapChain 和 context 就能自动绘图。仍需准备顶点、布局及 Shader 状态，只是不通过托管 Graphics 封装：

```text
SourceEnginePreInit
    → RenderDevice.CreateSwapChain
    → 原生 VertexLayout.Create / Add / Build
    → RenderDevice.CreateGPUBuffer
    → MaterialSystem2.CreateRawMaterial
    → RenderTools.SetGPUBufferData（显式传入上传 context）
    → 上传 context.Submit，等待上传完成并释放该 context

每帧：
    RenderDevice.CreateRenderContext
    → context.BindRenderTargets(swapChain, true, false)
    → context.SetViewport / Clear
    → RenderTools.SetRenderState(context, attributes, materialMode, layout, ...)
    → context.BindVertexBuffer
    → context.Draw(Triangles, 0, 3)
    → RenderDevice.BeginSubmittingDisplayLists
    → context.Submit
    → RenderDevice.ReleaseRenderContext
    → RenderDevice.Present
```

这里仍使用原生 Material 和 RenderTools，因为现有托管接口没有完整暴露独立的图形管线创建 API。它们不要求调用托管的 Material 包装类或 Graphics 门面。

示例使用独立的 [sdl_triangle.shader](../../game/core/shaders/sdl_triangle.shader)：

- 每个顶点是两个 float3：裁剪空间位置和 RGB 颜色，共 24 字节。
- VS 直接输出 `float4(position, 1)`，不需要相机和 UI 变换。
- PS 输出插值颜色，不读取纹理或全局 bindless 资源表。
- Shader 关闭深度测试、深度写入、混合和背面剔除，只绑定颜色目标。
- 颜色写入当前 SwapChain 时会进行 sRGB 编码；Shader 中的线性 RGB 不等于回读的 8 位 RGB。

从仓库根目录使用 PowerShell 编译和运行：

```powershell
dotnet build engine\Sandbox.Demo\Sandbox.Demo.csproj --no-restore
Set-Location game
& .\bin\managed\ShaderCompiler.exe (Join-Path (Get-Location) 'core\shaders\sdl_triangle.shader')
& ..\engine\Sandbox.Demo\bin\Debug\net10.0\Sandbox.Demo.exe
```

上述运行命令以入口选择 SdlTest4 为前提。ShaderCompiler 需要已构建；编译产生的 `.shader_c` 属于忽略的生成文件，新工作区需要先编译该 Shader。

可以增加 `--verify-triangle` 参数执行一次 GPU 回读验证后退出：

```powershell
& ..\engine\Sandbox.Demo\bin\Debug\net10.0\Sandbox.Demo.exe --verify-triangle
```

验证会比较三角形中心和背景的颜色，不只是检查进程是否存活。当前 800 × 600 窗口实测中心 RGB 为 `(187, 137, 138)`，背景为 `(80, 80, 89)`，符合 sRGB 编码后的预期；连续 Present、窗口缩放和关闭退出也已验证通过。

退出时先完成 GPU 工作，再释放材质句柄、Buffer、布局和 SwapChain，最后销毁窗口并关闭原生引擎，避免图形资源跨越设备生命周期。
