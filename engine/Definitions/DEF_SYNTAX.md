# `.def` 文件语法总结

## 1. 文件用途

本项目中的 `.def` 是一套用于生成 C++ 与 C# 互操作代码的领域专用语言（DSL）。它描述：

- 原生 DLL 和生成文件位置；
- 原生 C++ 类型如何映射为托管类型；
- C# 如何调用 C++；
- C++ 如何回调 C#；
- 参数和返回值如何封送；
- 原生对象的创建、释放和句柄生命周期。

它不是 C#、C++ 或标准 IDL。本文根据当前仓库中的定义文件、InteropGen 源码及其配套文档整理。

## 2. 注释与基本结构

单行注释使用 C++ 风格：

```text
// comment
```

声明通常以分号结尾，类体和内联函数体使用花括号：

```text
native class Example
{
    int GetValue();
}
```

内联函数体内直接书写 C++，因此其中遵循 C++ 语法。

## 3. 模块级指令

### 3.1 模块标识和原生库

```text
ident "engine"
nativedll engine2.dll
```

- `ident`：当前绑定模块的标识，主要用于生成两端配对的初始化入口名称。
- `nativedll`：绑定代码所属或加载的原生 DLL。

对于：

```text
ident "engine"
```

生成的 C# 初始化代码会使用 `"igen_" + ident` 作为 `NativeLibrary.GetExport` 的第二个参数，概念上为：

```csharp
IntPtr nativeInitPtr = NativeLibrary.GetExport( nativeDll, "igen_engine" );
```

C++ 侧同时生成同名导出入口：

```cpp
DLL_EXPORT void igen_engine(
    int hash,
    void** managedFunctions,
    void** nativeFunctions,
    int* structSizes );
```

如果改为 `ident "tools"`，两端名称都会变为 `igen_tools`。名称不匹配时，C# 无法从原生 DLL 找到初始化入口，互操作层也就无法交换两边的函数指针表。

`ident` 不控制公开 C# 类型名、业务类型命名空间、C# 输出文件名或原生 DLL 名；这些分别由类型声明、`namespace`、`cs` 和 `nativedll` 控制。

### 3.2 生成输出

```text
cpp "../../src/engine2/interop.engine.cpp"
hpp "../../src/engine2/interop.engine.h"
cs  "../Sandbox.Engine/Interop.Engine.cs"
```

分别指定生成的 C++ 实现、C++ 头文件和 C# 文件。路径相对于当前 `.def` 文件。

### 3.3 命名空间和异常

```text
namespace "Managed.SandboxEngine"
exceptions "Sandbox.Interop.BindingException"
```

- `namespace`：生成的 `Exports` 和 `NativeInterop` 基础设施所在的 C# 命名空间，同时也是对应 C++ 管理代码的命名空间。
- `exceptions`：生成的托管导出桩捕获异常后调用的 C# 静态方法，不是异常类型。该方法接收类名、函数名和异常对象。

概念化的 C# 输出为：

```csharp
namespace Managed.SandboxEngine
{
    internal static unsafe class Exports
    {
        [UnmanagedCallersOnly]
        internal static void Bootstrap_EnvironmentExit( int nCode )
        {
            try
            {
                Sandbox.Engine.Bootstrap.EnvironmentExit( nCode );
            }
            catch ( Exception exception )
            {
                Sandbox.Interop.BindingException(
                    "Sandbox.Engine.Bootstrap",
                    "EnvironmentExit",
                    exception );
            }
        }
    }

    internal static unsafe partial class NativeInterop
    {
        // 加载原生 DLL、查找 igen_engine 并交换函数指针表。
    }
}
```

`namespace` 不会改写业务类型 `Sandbox.Engine.Bootstrap` 的命名空间；它只包裹生成器自己的 `Exports`、`NativeInterop` 等基础设施。若有返回值的 `managed` 方法抛出异常，导出桩调用 `exceptions` 指定的方法后还会返回 `default`，避免异常越过原生 ABI 边界。

### 3.4 预编译头

```text
pch "cbase.h"
```

指定生成的 C++ 代码使用的预编译头。

### 3.5 `include` 的分派规则

InteropGen 会根据 `include` 参数的形式决定处理方式：

| 写法 | 处理方式 | 影响的生成端 |
| --- | --- | --- |
| `include "file.h"` | 在生成的原生头文件中输出 C++ `#include "file.h"` | 直接影响 C++ |
| `#include "file.h"` | 与上一种写法等价；解析器允许保留 C++ 风格拼写 | 直接影响 C++ |
| `include "file.def"` | 就地解析该 `.def`，效果近似把它的内容粘贴到当前位置 | 根据其中声明影响两端 |
| `include "folder"` | 按字母顺序解析目录直属的 `*.def` | 根据其中声明影响两端 |
| `include "folder/*"` | 递归解析目录及子目录中的 `*.def` | 根据其中声明影响两端 |

例如：

```text
include "dbg.h"
include "color.h"

include "engine/*"
include "tier3"
include "common/*"
include "resources"
```

其中 `dbg.h` 和 `color.h` 是提供原生声明的 C++ 头文件，C# 编译器不会读取它们。其余四项加载其他 `.def`：

- `engine/*` 和 `common/*` 递归加载子目录；
- `tier3` 和 `resources` 只加载目录直属的 `.def`；
- 加载到的 `native`、`managed` 等声明会继续参与 C++ 和 C# 两边的桥接代码生成。

因此，C# 编译器本身不处理 `include`。InteropGen 先汇总主文件和被包含的定义，再生成完整的 `.cs`、`.h` 和 `.cpp`。所有被包含的 `.def` 文本也参与定义哈希，修改它们会使两端绑定需要同步重新生成。

### 3.6 模块继承

```text
ident "tools"
inherit "engine.def"

include "common/*"
include "resources"
```

`inherit` 不是 C# 类继承，也不会把另一个生成文件复制到当前生成文件。它会单独解析指定的 `.def`，并把其中的声明登记为“已由其他绑定集提供”。

当前文件自己的 `include` 仍会照常解析。例如 `engine.def` 和 `tools.def` 都包含 `common/*`，因此 Tools 解析时仍能解析 `IPhysicsBody` 等参数和返回类型。输出阶段再将当前声明与继承集比较：

- 已由 Engine 提供的 `native` 类不会在 `Interop.Tools.cs` 和 Tools 原生导出表中重复生成；
- 已由 Engine 提供的结构体不会重复生成或加入当前结构体尺寸检查；
- Tools 新增的类型和函数照常生成；
- 普通 `inherit` 不会自动跳过重叠的 `managed` 回调，因为当前原生 DLL 可能需要自己的一组托管函数指针。

因此，`inherit` 本身主要提供去重依据；当前绑定仍通过自己的 `include` 获得参与类型解析的声明。

以 `tools.def` 为例，有 `inherit "engine.def"` 时：

```text
1. 解析 engine.def，并登记 Engine 已提供的声明
2. 解析 tools.def 的 common/*、resources 和 tools/*
3. 使用所有当前声明解析 Tools 方法的参数和返回类型
4. 跳过与 Engine 重叠的 native 类和结构体
5. 只为 Tools 需要的接口生成 Interop.Tools.cs 和 igen_tools 函数表
```

如果删除 `inherit "engine.def"`，其他内容不变：

```text
1. 只解析 tools.def 及其 include
2. common/* 中的类型仍能参与类型解析
3. 生成器不知道这些 native 类型已经由 Engine 提供
4. 在 Sandbox.Tools 中再次生成同名包装和原生导出桩
5. 重复类型分别属于 Sandbox.Engine 和 Sandbox.Tools 两个程序集
```

两份完全限定名相同的 C# 类型仍具有不同的程序集身份，可能造成类型歧义、无法直接互换、重复函数表槽位和重复结构体检查。

#### 生成代码对照示例

假设 `common/*` 中存在 Engine 和 Tools 都能解析到的定义：

```text
native class IPhysicsBody as NativeEngine.IPhysicsBody
{
    void SetMass( float value );
    float GetMass();
}
```

Tools 另外声明一个返回该类型的接口：

```text
native static class ToolPhysics as NativeTools.ToolPhysics
{
    IPhysicsBody GetSelectedBody();
}
```

以下代码仅展示生成结构，省略真实名称修饰、封送代码、可见性和错误检查。

有 `inherit "engine.def"` 时，`Interop.Engine.cs` 已经提供包装：

```csharp
// Sandbox.Engine.dll / Interop.Engine.cs
namespace NativeEngine;

internal readonly unsafe partial struct IPhysicsBody
{
    private readonly IntPtr self;

    internal void SetMass( float value )
        => __N.IPhysicsBody_SetMass( self, value );

    internal float GetMass()
        => __N.IPhysicsBody_GetMass( self );
}
```

`Interop.Tools.cs` 只生成 Tools 新接口，并直接使用 Engine 程序集已有的类型：

```csharp
// Sandbox.Tools.dll / Interop.Tools.cs
namespace NativeTools;

internal static unsafe partial class ToolPhysics
{
    internal static NativeEngine.IPhysicsBody GetSelectedBody()
    {
        IntPtr pointer = __N.ToolPhysics_GetSelectedBody();
        return pointer;
    }
}
```

对应的 Tools 原生导出桩概念上只有新增接口：

```cpp
void* Exports::ToolPhysics_GetSelectedBody()
{
    return ToolPhysics::GetSelectedBody();
}
```

`igen_tools` 返回的原生函数表概念上为：

```text
nativeFunctions[0] = Debug_Error
nativeFunctions[1] = ToolPhysics_GetSelectedBody
```

它不包含 `IPhysicsBody.SetMass/GetMass`，因为这两个包装已经属于 `igen_engine`。

如果删除 `inherit "engine.def"`，生成器不再把 `IPhysicsBody` 视为外部已提供类型。于是 `Interop.Tools.cs` 还会生成第二份包装：

```csharp
// Sandbox.Tools.dll / Interop.Tools.cs
namespace NativeEngine;

// 名称与 Sandbox.Engine.dll 中的类型相同，程序集身份却不同。
internal readonly unsafe partial struct IPhysicsBody
{
    private readonly IntPtr self;

    internal void SetMass( float value )
        => __N.IPhysicsBody_SetMass( self, value );

    internal float GetMass()
        => __N.IPhysicsBody_GetMass( self );
}
```

Tools 的 C++ 输出也会为这些重叠方法生成导出桩：

```cpp
void Exports::IPhysicsBody_SetMass( IPhysicsBody* self, float value )
{
    self->SetMass( value );
}

float Exports::IPhysicsBody_GetMass( IPhysicsBody* self )
{
    return self->GetMass();
}
```

此时 `igen_tools` 的函数表概念上变成：

```text
nativeFunctions[0] = Debug_Error
nativeFunctions[1] = IPhysicsBody_GetMass
nativeFunctions[2] = IPhysicsBody_SetMass
nativeFunctions[3] = ToolPhysics_GetSelectedBody
```

这并不是继承 Engine 的包装，而是在 `toolframework2.dll` 与 `Sandbox.Tools.dll` 之间又建立了一套独立包装。即使两份类型都叫 `NativeEngine.IPhysicsBody`，运行时仍分别是：

```text
NativeEngine.IPhysicsBody, Sandbox.Engine
NativeEngine.IPhysicsBody, Sandbox.Tools
```

因此 `inherit` 的核心生成效果是：让当前绑定能使用重叠定义完成类型解析，同时阻止已由基础绑定提供的原生包装进入当前输出。

| 行为 | 有 `inherit` | 没有 `inherit` |
| --- | --- | --- |
| 记录 Engine 已提供的声明 | 是 | 否 |
| 当前 `include` 的解析 | 正常进行 | 正常进行 |
| 重叠 `native` 包装 | 跳过生成 | 重复生成 |
| 重叠结构体 | 跳过生成和尺寸检查 | 重复生成和检查 |
| 当前模块新增接口 | 正常生成 | 正常生成 |
| 当前初始化入口 | 独立的 `igen_tools` | 仍是 `igen_tools`，但表中包含重复接口 |

`inherit` 不会自动添加 C# 程序集引用，也不会自动调用被继承绑定集的 `NativeInterop.Initialize()`；这些依赖和初始化顺序由项目及启动流程负责。

#### `skipall`

```text
inherit "tools.def"
skipall "tools.def"
```

`skipall` 是更强的排除规则。凡是出现在指定定义中的类（包括 `native` 和 `managed`）、结构体和 C++ 头文件包含，当前绑定集都跳过输出。它常用于 Hammer 等较深的继承链，防止通过多条继承路径再次生成同一批内容。

简化区别：普通 `inherit` 主要复用已有 `native` 包装和结构体；`skipall` 则将指定定义中的整批内容排除出当前输出。

## 4. 类型声明

### 4.1 原生类

```text
native class IPhysicsBody as NativeEngine.IPhysicsBody
{
    void SetMass( float value );
    float GetMass();
}
```

`native class` 描述原生 C++ 类型，主要生成 C# 调用 C++ 的包装。

- `IPhysicsBody`：原生类型名。
- `NativeEngine.IPhysicsBody`：托管侧映射名称。

如果不需要重命名，可以省略映射：

```text
native class IAssetType
{
}
```

### 4.2 继承

```text
native class CSceneObject as NativeEngine.CSceneObject
native class CSceneModel as NativeEngine.CSceneModel : NativeEngine.CSceneObject
```

冒号后是同一绑定集中的基类。派生包装会获得基类的非静态方法，并生成基类与派生类之间的转换运算符。转换通过 C++ `dynamic_cast` 完成，因为多继承场景中的基类指针地址可能与派生类指针不同。

概念化的 C# 输出：

```csharp
internal readonly partial struct CSceneModel
{
    internal static implicit operator CSceneObject( CSceneModel value )
        => __N.To_CSceneObject_From_CSceneModel( value );

    internal static explicit operator CSceneModel( CSceneObject value )
        => __N.From_CSceneObject_To_CSceneModel( value );
}
```

对应 C++ 转换桩：

```cpp
CSceneObject* To_CSceneObject_From_CSceneModel( CSceneModel* value )
{
    return dynamic_cast<CSceneObject*>( value );
}
```

### 4.3 C++ 模板类型

```text
native class CUtlVector<CUtlString> as NativeEngine.CUtlVectorString
```

可以绑定 C++ 模板实例，并为托管侧提供非泛型名称。

### 4.4 原生结构体

```text
native struct MeshTraceInput as Sandbox.MeshTraceInput;
```

`as` 或 `is` 可以为 C++ 结构体指定 C# 侧的类型名。例如，`tier3/mathlib.def` 中的定义：

```text
native struct Vector is Vector3;
```

表示 C++ 使用 `Vector`，而 C# 侧使用已有的 `Vector3` 类型。两者是名称映射关系，不是 C# 继承关系，也不会产生运行时类型转换：

```text
void SetOrigin( Vector origin );
Vector GetOrigin();
```

概念上对应 C#：

```csharp
void SetOrigin( Vector3 origin );
Vector3 GetOrigin();
```

C++ 的 `Vector` 与 C# 的 `Vector3` 必须具有兼容的内存布局、大小和字段排列。类似的映射还包括：

```text
native struct Vector4D is Vector4;
native struct QAngle is Angles;
native struct VMatrix is Matrix;
```

两边必须存在布局一致的结构体。初始化时，C# 会提交 `sizeof(Sandbox.MeshTraceInput)`，C++ 将其与 `sizeof(MeshTraceInput)` 比较；不一致会触发致命错误。

大小不小于 8 字节的结构体作为参数时通过指针跨边界：

```csharp
var structSizes = new[]
{
    sizeof( Sandbox.MeshTraceInput )
};
```

```cpp
if ( structSizes[0] != sizeof( MeshTraceInput ) )
    Plat_FatalError( "struct size mismatch" );
```

### 4.5 原生枚举

```text
native enum KeyValues3Type_t;
native enum Qt::CheckState as Editor.CheckState;
native enum TextureUsage_t is NativeEngine.TextureUsage;
```

- 无映射名：使用原生名称生成或引用枚举。
- `as`/`is`：同义，均用于指定另一侧的别名。

枚举以 64 位整数跨越 ABI 边界，并在两侧转换回相应枚举类型。

### 4.6 不透明指针句柄

```text
native pointer SwapChainHandle_t as NativeEngine.SwapChainHandle_t;
```

`native pointer` 用于 C++ 中表现为值类型、实际承载指针的句柄（例如 `DECLARE_POINTER_HANDLE`）。C# 侧生成一个 `IntPtr` 大小的不透明结构体，不公开其指向内容：

```csharp
internal readonly struct SwapChainHandle_t
{
    private readonly IntPtr value;
}
```

### 4.7 托管类

```text
managed static class Sandbox.SceneSystem
{
    void OnBeforeRender( CSceneObject obj, ManagedRenderSetup_t setup );
}
```

`managed class` 声明由托管端实现、供原生端调用的方法，调用方向通常为 C++ 到 C#。

支持：

```text
managed class Namespace.Type
managed static class Namespace.Type
```

`static` 表示托管静态类或静态调用入口。

### 4.8 委托

```text
delegate SboxAnimationEventCallback;
```

声明一种回调/函数指针类型，随后可作为参数使用：

```text
void RunAnimationEvents( SboxAnimationEventCallback callback );
```

委托以函数指针跨边界。C# 侧传递 `IntPtr`，原生侧将其转换为声明的回调类型，概念上类似：

```cpp
auto callback = FunctionPointerToDelegate<SboxAnimationEventCallback>( pointer );
self->RunAnimationEvents( callback );
```

## 5. 原生访问器和全局函数

### 5.1 全局对象访问器

```text
native accessor g_pFullFileSystem as NativeEngine.FullFileSystem
{
    string GetSymLink( string path, string pathId );
}
```

把 C++ 全局对象或指针包装成托管可访问对象。

生成的 C# 是不带 `self` 的静态包装，C++ 导出桩通过全局指针调用，并在调用前检查指针：

```csharp
internal static string GetSymLink( string path, string pathId )
    => __N.GetSymLink( path, pathId );
```

```cpp
const char* Exports::GetSymLink( const char* path, const char* pathId )
{
    Assert( g_pFullFileSystem != nullptr );
    return g_pFullFileSystem->GetSymLink( path, pathId );
}
```

也存在：

```text
native static accessor g_pAssetSystem as IAssetSystem
```

在当前生成器中，accessor 本身就会生成无实例的 C# 静态包装，并通过原生全局指针调用；额外写出的 `static` 不会把调用改成 `Type::Method()`，底层仍是 `g_pAssetSystem->Method()`。

### 5.2 全局/静态原生类

```text
native static class global as NativeEngine.EngineGlobal
{
    void Plat_MessageBox( string title, string message );
}
```

用于包装不依赖对象实例的 C/C++ 全局函数或静态函数。

三种无实例包装在 C++ 调用点的核心区别为：

```cpp
g_pThing->Method(); // native accessor
Type::Method();     // native static class Type
::Method();         // native static class global
```

## 6. 成员声明

### 6.1 实例方法

```text
void SetMass( float value );
float GetMass();
```

生成对应的原生调用包装。

实例方法的 C# 包装持有原生 `self` 指针，调用时将其作为第一个 ABI 参数传给 C++ 导出桩。方法末尾还允许使用 `const` 匹配原生 const 方法：

```text
int GetClassCount() const;
```

名为 `GetType` 的函数会在 C# 中生成为 `GetType_Native`，避免与 `object.GetType()` 冲突。

### 6.2 静态方法

```text
static CQueryResult Create();
```

`static` 表示调用不需要当前对象实例。

### 6.3 重载

```text
void SetValue( int value );
void SetValue( float value );
void SetValue( Vector3 value );
```

通过相同方法名和不同参数列表声明重载。

### 6.4 字段

```text
float m_flDeltaTime;
BBox m_worldBounds;
```

类体内可以直接声明原生字段，使生成器建立字段访问绑定。

例如：

```text
native class AnimationState
{
    float m_flDeltaTime;
}
```

会生成 C++ getter/setter 桩和 C# 属性，概念上为：

```cpp
float Exports::_Get__AnimationState_m_flDeltaTime( AnimationState* self )
{
    return self->m_flDeltaTime;
}

void Exports::_Set__AnimationState_m_flDeltaTime( AnimationState* self, float value )
{
    self->m_flDeltaTime = value;
}
```

```csharp
internal float m_flDeltaTime
{
    get => __N.Get__m_flDeltaTime( self );
    set => __N.Set__m_flDeltaTime( self, value );
}
```

## 7. 内联 C++ 适配

```text
inline bool HasPhysicsBones()
{
    return self->m_PhysicsBoneTransform.Count() > 0;
}
```

`inline` 在 `.def` 中提供一段实际 C++ 实现，用于：

- 组合多个原生调用；
- 访问字段；
- 处理生成器无法自动表达的类型转换；
- 为托管端提供更简单的接口。

实例上下文中的 `self` 表示当前原生对象：

```text
inline string GetMaterialName()
{
    return self->GetMaterial().m_pUserData->m_name.Get();
}
```

静态内联方法可以写成：

```text
inline static void BeginEvent( ... )
{
    // C++ implementation
}
```

这里的 `inline` 首要含义是“定义文件提供实现”，不应直接等同于 C++ 编译器一定执行函数内联优化。

例如，`tier3/CFrustum.def` 中的两个方法：

```text
inline Matrix GetView()
{
    return VMatrix( self->GetView() );
}

Matrix GetProj();
```

`GetView` 使用 `inline`，是因为它需要通过 `VMatrix( ... )` 对原生返回值进行额外的类型转换。生成的 C++ 桩概念上类似：

```cpp
Matrix Exports::CFrustum_GetView( CFrustum* self )
{
    return VMatrix( self->GetView() );
}
```

`GetProj` 没有 `inline` 函数体，表示原生类中已经存在可直接调用的 `GetProj()`，因此生成器只需生成转发桩：

```cpp
Matrix Exports::CFrustum_GetProj( CFrustum* self )
{
    return self->GetProj();
}
```

两种写法在 C# 侧都会生成普通包装方法；区别主要体现在 C++ 桩是否使用 `.def` 中提供的自定义实现。

生成器会把函数体写入生成的 `.cpp`，而 C# 侧仍得到普通包装：

```cpp
bool Exports::HasPhysicsBones( CSceneAnimatableObject* self )
{
    return self->m_PhysicsBoneTransform.Count() > 0;
}
```

```csharp
internal bool HasPhysicsBones()
    => __N.HasPhysicsBones( self );
```

也就是说，InteropGen 不会把内联 C++ 函数体翻译成 C#；它只为这段新生成的 C++ 实现建立跨边界调用包装。

## 8. 参数和返回值

### 8.1 常用类型

DSL 直接使用类似 C#/C++ 的类型名：

```text
bool
byte
ushort
int
uint
long
ulong
float
double
string
IntPtr
void*
```

还可使用项目定义类型，例如 `Vector3`、`Rotation`、`Transform`、`BBox`。

几个容易忽略的 ABI 转换：

| `.def` 类型 | C# 表示 | C++ 表示 | ABI 表示 |
| --- | --- | --- | --- |
| `bool` | `bool` | `bool` | `int`，使用 0/1 |
| `string` | `string` | `const char*` | UTF-8 指针 |
| `void*` / `IntPtr` | `IntPtr` | `void*` | 原生指针 |
| `StringToken` | `Sandbox.StringToken` | `uint32` | 32 位哈希值 |

### 8.2 引用参数

```text
void Method( ref int value );
void Method( out int value );
```

- `ref`：调用前后都可读写。
- `out`：由被调用方写入返回。

例如：

```text
void GetEngineSwapChainSize( out int width, out int height );
void SetValue( ref int value );
```

概念化的边界代码：

```csharp
internal static void GetEngineSwapChainSize( out int width, out int height )
{
    width = default;
    height = default;
    __N.GetEngineSwapChainSize( &width, &height );
}
```

```cpp
void Exports::GetEngineSwapChainSize( int* width, int* height )
{
    g_pEngineServiceMgr->GetEngineSwapChainSize( width, height );
}
```

### 8.3 C++ 引用

```text
void SetValue( cref QPixmap value );
bool TryGet( cref out Transform result );
```

`cref` 表示原生方法需要 C++ 引用。C# 侧仍通过指针跨 ABI；C++ 导出桩在调用原生方法时解引用：

```text
void SetPixmap( cref QPixmap image );
```

```cpp
void Exports::SetPixmap( QPixmap* image )
{
    self->SetPixmap( *image );
}
```

用于 C++ 调用 C# 的反方向包装时，生成代码会对本地值取地址。`cref out T` 则组合引用语义与输出参数。

### 8.4 显式原生转换

```text
void AddVertices( CastTo[Vector*] void* vertices, int count );
void SetFlags( CastTo[Qt::Alignment] int flags );
void SetWindow( CastTo[OsSpecificWindowHandle_t] IntPtr handle );
```

`CastTo[T]` 表示：

- 对托管侧维持后面的声明类型；
- 调用原生 API 时转换成方括号中的 C++ 类型。

常用于数组指针、枚举、平台句柄和 C++ typedef。

例如：

```text
void AddVertices( CastTo[Vector*] void* vertices, int count );
```

会保留 C# 的 `IntPtr` 表示，但在原生调用点生成显式转换：

```cpp
self->AddVertices( (Vector*)vertices, count );
```

### 8.5 数组与字面量参数

```text
void Upload( float[] values, int count );
```

`type[]` 将首元素的裸指针传过边界，不携带长度；调用方必须通过单独的 `count` 参数约定长度。

参数位置还可写原生字面量表达式：

```text
void Run( int value, [DEFAULT_FLAGS] );
```

`[DEFAULT_FLAGS]` 不会出现在生成的 C# 方法签名中，但 C++ 调用原生实现时会原样传入：

```csharp
internal void Run( int value );
```

```cpp
self->Run( value, DEFAULT_FLAGS );
```

### 8.6 `asref` 返回值

```text
asref Transform GetTransformStorage();
```

`asref` 用于返回值，使 C# 包装返回 `ref T`，直接引用原生内存，内部使用类似 `Unsafe.AsRef<T>` 的转换。该引用的有效期完全受原生对象和内存稳定性约束。

### 8.7 裸指针

```text
void* GetDataPointer();
void Copy( void* destination, void* source, long count );
```

`void*` 表示原生裸指针。托管侧通常对应 `IntPtr` 或不安全指针，其内存有效期由调用方和原生 API 共同约定。

### 8.8 字符串

```text
string GetName();
stable string GetName();
```

- `string`：使用 UTF-8 封送。普通原生字符串返回值会先复制到线程本地 `CUtlString`，避免返回指向临时对象的悬空指针。
- `stable string`：承诺返回的 `const char*` 在调用结束后仍然有效，因此跳过这次防御性线程本地复制。

概念化的普通字符串返回：

```cpp
return SafeReturnString( self->GetName() );
```

稳定字符串返回：

```cpp
return self->GetName();
```

只有内部驻留字符串或生命周期明确稳定的成员字符串才能标记 `stable`，不能用于指向临时对象的指针。

## 9. 生命周期和类型属性

属性使用方括号，既可放在类型前，也可放在方法前或方法声明后。

### 9.1 创建和释放

```text
static CQueryResult Create(); [new]
void DeleteThis(); [delete]
```

- `[new]`：该调用创建新的原生实例，并建立相应托管包装。
- `[delete]`：该调用销毁或释放原生实例。

方法名称不固定，`Dispose`、`Destroy`、`DeleteThis` 都可标记为 `[delete]`。

它们会改变 C++ 桩的调用方式，而不只是添加元数据：

```cpp
NativeType* Exports::Create( int value )
{
    return new NativeType( value );
}

void Exports::Destroy( NativeType* self )
{
    delete self;
}
```

C# 包装在执行 `[delete]` 方法后会把自身保存的原生指针清空，降低重复释放风险。

例如，`tier3/CFrustum.def` 中的定义：

```text
static CFrustum Create(); [new]
void Delete(); [delete]
```

概念上会生成一个可由静态工厂创建、由 `Delete()` 释放的 C# 包装：

```csharp
public unsafe partial class CFrustum
{
    private IntPtr _native;

    public static CFrustum Create()
    {
        IntPtr native = NativeInterop.CFrustum_Create();
        return new CFrustum( native );
    }

    public void Delete()
    {
        if ( _native == IntPtr.Zero )
            return;

        NativeInterop.CFrustum_Delete( _native );
        _native = IntPtr.Zero;
    }
}
```

对应的 C++ 桩概念上类似：

```cpp
CFrustum* CFrustum_Create()
{
    return new CFrustum();
}

void CFrustum_Delete( CFrustum* self )
{
    delete self;
}
```

实际生成代码还会包含函数表、调用约定、指针封送和完整的空指针检查；上面的代码只展示 `[new]` 和 `[delete]` 对生命周期的影响。

### 9.2 托管句柄

```text
[Handle:Sandbox.PhysicsBody]
native class IPhysicsBody as NativeEngine.IPhysicsBody
```

使用指定托管类型的句柄系统表示原生对象。原生侧保存整数句柄，托管侧通过句柄查找实际对象，可降低原生指针失效后仍被使用的风险。

普通 `native class` 通常以 `IntPtr` 跨边界，而 `[Handle:T]` 类以句柄 ID 跨边界。托管方向会通过类似代码恢复对象：

```csharp
Sandbox.PhysicsBody body = Sandbox.HandleIndex.Get<Sandbox.PhysicsBody>( handle );
```

原生方向则通过 `GetManagedHandle` 获取或保存相应句柄。

### 9.3 Source 2 资源句柄

```text
[ResourceHandle:HMaterial]
native class IMaterial2 as NativeEngine.IMaterial
```

表示该类型通过 Source 2 资源句柄（例如 `HMaterial`、`HModel`）管理，而非普通对象指针。

生成器会按强资源句柄解引用，并自动为包装增加一组管理函数：

```text
DestroyStrongHandle
IsStrongHandleValid
IsStrongHandleLoaded
IsError
CopyStrongHandle
GetBindingPtr
```

原生资源尚未加载或句柄无效时，生成桩会返回默认值，而不是直接解引用无效资源。

### 9.4 禁止 GC 的调用路径

```text
[nogc]
native class CAudioMixBuffer
```

或：

```text
[nogc]
Transform GetWorldSpaceRenderBoneTransform( int boneIndex );
```

`[nogc]` 会在 C# 函数指针调用上生成 `[SuppressGCTransition]`：

```csharp
delegate* unmanaged[SuppressGCTransition]<IntPtr, int, Transform> GetBone;
```

这会跳过常规 GC transition，因此原生调用必须短小、不可阻塞，并且不能回调托管代码。标记在类上时默认应用于该类全部函数。

如果类整体使用 `[nogc]`，但某个函数确实会回调 C#，可用 `[callback]` 取消该函数继承到的优化：

```text
[nogc]
native class Example
{
    void FastCall();

    [callback]
    void CallManaged();
}
```

### 9.5 小型值类型

```text
[small]
native struct HSteamNetConnection as Sandbox.Network.HSteamNetConnection;
```

`[small]` 表示小于指针大小（当前规则为 8 字节）的结构体，跨边界时按值传递。未标记的结构体按指针传递，并有静态断言确保其尺寸至少为 8 字节。

```cpp
static_assert( sizeof( HSteamNetConnection ) < 8 );
```

### 9.6 平台限制

```text
[WindowsOnly]
native class Example
```

在 Windows 上生成正常原生桩；在非 Windows 平台仍生成同一接口形状，但桩函数返回默认值，从而保持托管 API 和函数表布局稳定。

```cpp
Result Exports::WindowsFunction()
{
#ifdef _WIN32
    return NativeWindowsFunction();
#else
    return {};
#endif
}
```

### 9.7 Qt 共享数据指针

```text
[SharedDataPointer]
native class QStringList
```

用于 Qt 隐式共享类型，按共享数据指针的复制和生命周期语义处理。

C# 侧会生成公开的引用类型包装，而不是普通只读结构体，并从终结器将 `Dispose` 排入主线程：

```csharp
public unsafe partial class QStringList
{
    ~QStringList()
    {
        if ( !IsNull ) Sandbox.MainThread.QueueDispose( this );
    }
}
```

## 10. 着色器定义文件

`shaders.def` 使用另一组专用指令：

```text
shaderdir "../game/core/shaders/"
output "../src/common/vfx/interop.shaders.h"
whitelist "common/pixel.hlsl"
document "vr_lighting.fxc"
```

- `shaderdir`：着色器源目录。
- `output`：生成头文件。
- `whitelist`：允许处理或暴露的着色器包含文件。
- `document`：指定需要分析或生成文档信息的主着色器文件。

其精确处理流程需由着色器定义生成器源码验证。

## 11. 调用方向速查

| 声明 | 主要作用 | 常见调用方向 |
| --- | --- | --- |
| `native class` | 包装原生 C++ 对象 | C# → C++ |
| `native static class` | 包装全局/静态 C++ 函数 | C# → C++ |
| `native accessor` | 包装原生全局实例 | C# → C++ |
| `managed class` | 声明托管实现入口 | C++ → C# |
| `delegate` | 声明回调类型 | 双向回调桥接 |
| `inline` | 提供 C++ 适配实现 | 取决于所属声明 |

## 12. 双向桥接生成示例

以下代码用于说明生成结构，省略了真实生成代码中的名称修饰、异常处理、类型封送和调用约定，不应当作生成文件的逐字结果。

### 12.1 `managed`：C++ 调用 C#

定义文件：

```text
managed static class Sandbox.Engine.Bootstrap
{
    static void EnvironmentExit( int nCode );
}
```

这段定义表示：C# 中已经存在 `Sandbox.Engine.Bootstrap` 静态类型，并由它实现 `EnvironmentExit(int)`。生成器不会生成这个方法的业务实现；业务代码需要手写：

```csharp
namespace Sandbox.Engine;

internal static class Bootstrap
{
    internal static void EnvironmentExit( int nCode )
    {
        // Handwritten managed implementation
    }
}
```

生成器在 C# 输出中生成一个可由原生代码调用的导出桩，概念上类似：

```csharp
internal static unsafe class Exports
{
    [UnmanagedCallersOnly]
    internal static void Bootstrap_EnvironmentExit( int nCode )
    {
        try
        {
            Sandbox.Engine.Bootstrap.EnvironmentExit( nCode );
        }
        catch ( Exception exception )
        {
            Sandbox.Interop.BindingException(
                "Sandbox.Engine.Bootstrap",
                "EnvironmentExit",
                exception );
        }
    }
}
```

生成器同时在 C++ 输出中生成函数指针槽位和调用包装，概念上类似：

```cpp
using EnvironmentExitFn = void (*)( int );
static EnvironmentExitFn managedEnvironmentExit;

void Sandbox::Engine::Bootstrap::EnvironmentExit( int nCode )
{
    managedEnvironmentExit( nCode );
}
```

初始化互操作层时，C# 导出桩的函数指针会被交给 C++。最终调用链为：

```text
C++ 调用 Bootstrap::EnvironmentExit
    -> 生成的 C++ 调用包装
    -> C# 函数指针
    -> 生成的 C# [UnmanagedCallersOnly] 导出桩
    -> 手写的 Sandbox.Engine.Bootstrap.EnvironmentExit
```

因此，`managed` 表示“由 C# 实现，向 C++ 暴露”，但 C++ 和 C# 两边仍然都需要生成桥接代码。

### 12.2 `native`：C# 调用 C++

定义文件：

```text
native static class LZ4Glue as NativeEngine.LZ4Glue
{
    int CompressBound( int inputSize );
}
```

这段定义表示：原生侧已经存在 `LZ4Glue::CompressBound(int)`，业务实现位于手写的 C++ 代码或原生库中。生成器不会实现压缩算法。

生成器在 C++ 输出中生成一个导出桩，概念上类似：

```cpp
int Exports::LZ4Glue_CompressBound( int inputSize )
{
    return LZ4Glue::CompressBound( inputSize );
}
```

生成器在 C# 输出中生成对应包装，概念上类似：

```csharp
namespace NativeEngine;

internal static class LZ4Glue
{
    internal static int CompressBound( int inputSize )
    {
        return NativeInterop.LZ4Glue_CompressBound( inputSize );
    }
}
```

其中 `NativeInterop` 最终通过初始化阶段取得的原生函数指针调用 C++ 导出桩。最终调用链为：

```text
C# 调用 NativeEngine.LZ4Glue.CompressBound
    -> 生成的 C# 包装
    -> C++ 函数指针
    -> 生成的 C++ 导出桩
    -> 手写的 LZ4Glue::CompressBound
```

对于非静态 `native class`，流程相同，但 C# 包装还会保存原生对象指针，并把它作为 `self` 参数传给 C++ 导出桩。

因此，`native` 表示“由 C++ 实现，向 C# 暴露”，同样会在两边生成桥接代码。

### 12.3 两边如何配对

初始化入口会交换托管函数表和原生函数表，并校验由 `.def` 内容计算出的定义哈希。若 C# 与 C++ 使用了不同版本的生成结果，初始化会因哈希不一致而失败，而不是继续使用错位的函数指针。

可以把职责概括为：

| 声明 | 手写业务实现 | 生成的 C# | 生成的 C++ |
| --- | --- | --- | --- |
| `managed class` | C# | 导出桩 | 函数指针和调用包装 |
| `native class` | C++ | 托管包装和函数指针调用 | 导出桩 |

## 13. 完整模板

```text
ident "example"
nativedll example.dll
inherit "engine.def"

exceptions "Sandbox.Interop.BindingException"
namespace "Managed.Example"

cpp "../../src/example/interop.example.cpp"
hpp "../../src/example/interop.example.h"
cs  "../Sandbox.Example/Interop.Example.cs"

pch "examplepch.h"

include "example/header.h"
include "common/*"
include "example/*"

native enum NativeMode_t is Sandbox.ExampleMode;

delegate ExampleCallback;

[Handle:Sandbox.ExampleObject]
native class CExampleObject as NativeExample.CExampleObject
{
    static CExampleObject Create(); [new]
    void Destroy(); [delete]

    void SetValue( int value );
    bool TryGetValue( out int value );
    stable string GetName();

    inline bool IsReady()
    {
        return self->GetState() == STATE_READY;
    }
}

native accessor g_pExampleSystem as NativeExample.ExampleSystem
{
    CExampleObject Find( string name );
}

managed static class Sandbox.ExampleCallbacks
{
    void OnChanged( CExampleObject obj );
}
```

## 14. 阅读 `.def` 的建议顺序

1. 查看顶层模块文件中的 `nativedll`、输出路径和 `include`。
2. 确认声明是 `native` 还是 `managed`，判断调用方向。
3. 查看 `as`/`is` 后的托管类型映射。
4. 检查 `[Handle]`、`[ResourceHandle]`、`[new]`、`[delete]`，确定生命周期。
5. 检查 `ref`、`out`、`cref`、`CastTo`、`void*`，确定封送规则。
6. 对 `inline` 函数按 C++ 代码阅读，因为它包含真正的适配逻辑。

## 15. 验证边界

本文关于 InteropGen 的模块指令、类型解析、参数封送、函数表、生命周期属性和代码生成行为，均已结合 `Tools/InteropGen` 源码及其配套文档核对。

文中的生成代码为了突出结构而省略了真实名称修饰、平台调用约定、完整空指针检查和部分封送辅助函数；判断精确 ABI 时，应以实际生成的 `.cpp`、`.h`、`.cs` 为准。

`shaders.def` 由独立的着色器处理流程消费。本文只根据配置文件用法说明 `shaderdir`、`output`、`whitelist` 和 `document`；其中 `document` 的完整解析及输出行为尚未结合对应生成器源码验证。
