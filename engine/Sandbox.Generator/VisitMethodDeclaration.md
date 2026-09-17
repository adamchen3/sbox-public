# `Worker.VisitMethodDeclaration` 处理说明

本文只说明 `Worker.VisitMethodDeclaration`，不展开 `base.VisitMethodDeclaration(_node)` 的 Roslyn 默认递归过程。

## 方法入口

源码位于 `Worker.cs`：

```csharp
public override SyntaxNode VisitMethodDeclaration( MethodDeclarationSyntax _node )
{
    var symbol = Model.GetDeclaredSymbol( _node );

    ComponentSubscriberInterfaces.VisitMethod( _node, symbol, this );

    var node = base.VisitMethodDeclaration( _node ) as MethodDeclarationSyntax;

    Description.VisitMethod( ref node, symbol, this );
    CodeGen.VisitMethod( ref node, symbol, this );

    node = LinePreserve.AddLineNumber( node, _node, TreeInput, this );
    node = ClassFileLocation.VisitNode( node, _node, symbol, this, TreeInput ) as MethodDeclarationSyntax;

    return node;
}
```

这个方法接收一个原始的 `MethodDeclarationSyntax`，最后返回一个可能被多个处理器修改过的新方法节点。

需要注意两个对象：

- `_node`：原始方法节点，始终用于取得原始位置和语义上下文。
- `node`：正在被逐步改写的方法节点。

Roslyn 的语法节点是不可变的，所以每个处理器不会直接修改原对象，而是返回一个新的节点。`ref node` 使处理器可以把替换后的节点交回给调用方。

## 示例输入

假设源码文件是 `PlayerComponent.cs`，内容如下：

```csharp
namespace Game;

/// <summary>Controls the player every frame.</summary>
public partial class PlayerComponent : Component
{
    /// <summary>Updates the player.</summary>
    /// <param name="deltaTime">The elapsed frame time.</param>
    /// <returns>Whether the player was updated.</returns>
    protected bool OnUpdate(float deltaTime)
    {
        UpdateMovement(deltaTime);
        return true;
    }

    private void UpdateMovement(float deltaTime)
    {
    }
}
```

本文假设：

- 当前是完整生成模式，即 `master.IsFullGeneration == true`。
- `PlayerComponent` 继承 `Sandbox.Component`。
- `OnUpdate` 不是 `virtual` 或 `abstract`，并且有方法体。
- `PlayerComponent` 所在输入文件的相对路径是 `PlayerComponent.cs`。

最终代码中的行号和 `SourceLocation` 行号以真实输入文件位置为准。下面使用 `8` 作为示例行号。

## 处理总览

针对 `OnUpdate` 这个方法，处理顺序是：

```text
原始方法
  -> ComponentSubscriberInterfaces.VisitMethod
  -> Description.VisitMethod
  -> CodeGen.VisitMethod
  -> LinePreserve.AddLineNumber
  -> ClassFileLocation.VisitNode
最终方法节点
```

其中：

- `ComponentSubscriberInterfaces.VisitMethod` 不修改 `node`，而是向当前类收集一个接口。
- `Description.VisitMethod` 修改方法节点，添加描述特性。
- `CodeGen.VisitMethod` 只有在方法拥有相应的 `CodeGenerator` 配置时才修改方法体；本示例没有该配置，因此保持不变。
- `LinePreserve.AddLineNumber` 给方法添加 `#line` 前导指令。
- `ClassFileLocation.VisitNode` 给方法添加 `Sandbox.Internal.SourceLocation` 特性。

## 1. `ComponentSubscriberInterfaces.VisitMethod`

调用：

```csharp
ComponentSubscriberInterfaces.VisitMethod( _node, symbol, this );
```

这个处理器检查方法符号和所在类型：

```csharp
if ( !symbol.ContainingType.DerivesFrom( "global::Sandbox.Component" ) )
    return;

if ( symbol.IsVirtual )
    return;

if ( !Map.ContainsKey( symbol.Name ) )
    return;
```

`Map` 中的映射是：

```csharp
OnUpdate      -> Sandbox.Internal.IUpdateSubscriber
OnFixedUpdate -> Sandbox.Internal.IFixedUpdateSubscriber
OnPreRender   -> Sandbox.Internal.IPreRenderSubscriber
```

它还要求方法有方法体，并且不是抽象方法。`OnUpdate` 满足所有条件，因此执行：

```csharp
master.AddBaseTypeToCurrentClass( "Sandbox.Internal.IUpdateSubscriber" );
```

### 方法节点变化

这个调用**不修改 `node` 方法节点**。此时方法仍然是：

```csharp
protected bool OnUpdate(float deltaTime)
{
    UpdateMovement(deltaTime);
    return true;
}
```

### 所属类的变化

它向 `Worker.ClassBaseTypes` 收集接口。等 `VisitClassDeclaration` 完成当前类处理时，这个接口会被追加到类的 base list。

因此所属类最终会从：

```csharp
public partial class PlayerComponent : Component
```

变成概念上的：

```csharp
public partial class PlayerComponent
    : Component, Sandbox.Internal.IUpdateSubscriber
```

这个变化发生在类节点上，而不是发生在 `OnUpdate` 方法节点上。

## 2. `Description.VisitMethod`

调用：

```csharp
Description.VisitMethod( ref node, symbol, this );
```

这个处理器读取方法的 XML 文档注释，并从中提取：

- 方法 summary
- 参数说明
- 返回值说明

输入方法的注释是：

```csharp
/// <summary>Updates the player.</summary>
/// <param name="deltaTime">The elapsed frame time.</param>
/// <returns>Whether the player was updated.</returns>
protected bool OnUpdate(float deltaTime)
{
    UpdateMovement(deltaTime);
    return true;
}
```

处理后，方法节点会添加三个描述特性：

```csharp
[Description("Updates the player.")]
[return: Description("Whether the player was updated.")]
protected bool OnUpdate(
    [Description("The elapsed frame time.")] float deltaTime)
{
    UpdateMovement(deltaTime);
    return true;
}
```

返回值的特性使用 `return:` 目标，因此它描述的是返回值，而不是方法本身：

```csharp
[return: Description("Whether the player was updated.")]
```

如果源码已经手动写了 `[Description]`，`Description.VisitMethod` 会报告警告并跳过该描述的自动添加。

## 3. `CodeGen.VisitMethod`

调用：

```csharp
CodeGen.VisitMethod( ref node, symbol, this );
```

它查找方法特性背后的 `CodeGeneratorAttribute` 配置，重点处理带有 `WrapMethod` 标志的配置。它可以用于 RPC 等方法包装功能。

### 在主示例中的结果

主示例的 `OnUpdate` 没有 `[Rpc.Host]` 或其他 `WrapMethod` 配置，因此 `CodeGen.VisitMethod` 不会改写它：

```csharp
[Description("Updates the player.")]
[return: Description("Whether the player was updated.")]
protected bool OnUpdate(
    [Description("The elapsed frame time.")] float deltaTime)
{
    UpdateMovement(deltaTime);
    return true;
}
```

这点很重要：`CodeGen.VisitMethod` 并不会无条件包装每个方法。

### 真实 RPC 变体

S&box 中 `[Rpc.Host]` 的特性定义包含 `WrapMethod` 配置。例如：

```csharp
[Rpc.Host]
public void AskToLeave(PlayerController player)
{
    Leave(player);
}
```

`CodeGen.VisitMethod` 发现这个方法关联了 RPC 的 `WrapMethod` 配置后，会保留原始方法体，并把方法入口改成回调调用。概念上的处理前后是：

处理前：

```csharp
[Rpc.Host]
public void AskToLeave(PlayerController player)
{
    Leave(player);
}
```

处理后：

```csharp
[Rpc.Host]
public void AskToLeave(PlayerController player)
{
    Sandbox.Rpc.OnCallRpc(
        /* 包装了 Leave(player) 的原始方法回调 */,
        player);
}
```

真实生成逻辑还会根据方法的参数数量、返回值、`async`/`Task` 类型、静态/实例属性和回调签名生成不同的语法。它还会验证回调是否存在；签名不匹配时会报告诊断。

## 4. `LinePreserve.AddLineNumber`

调用：

```csharp
node = LinePreserve.AddLineNumber( node, _node, TreeInput, this );
```

这个处理器不会改变方法的执行逻辑，也不会添加 C# 特性。它根据原始节点 `_node` 的位置，给当前方法节点添加 `#line` 指令。

处理前：

```csharp
[Description("Updates the player.")]
[return: Description("Whether the player was updated.")]
protected bool OnUpdate(
    [Description("The elapsed frame time.")] float deltaTime)
{
    UpdateMovement(deltaTime);
    return true;
}
```

处理后概念上是：

```csharp
#line 8 "PlayerComponent.cs"
[Description("Updates the player.")]
[return: Description("Whether the player was updated.")]
protected bool OnUpdate(
    [Description("The elapsed frame time.")] float deltaTime)
{
    UpdateMovement(deltaTime);
    return true;
}
```

它的目的不是改变运行时行为，而是让编译器诊断、调试信息和生成代码位置尽量指向原始源码文件和原始行号。

该处理器在以下情况直接返回原节点：

- 不是完整生成模式。
- 输入文件已经是 `_gen_` 开头的生成文件。

## 5. `ClassFileLocation.VisitNode`

调用：

```csharp
node = ClassFileLocation.VisitNode(
    node,
    _node,
    symbol,
    this,
    TreeInput
) as MethodDeclarationSyntax;
```

虽然方法名是 `VisitNode`，但它会处理类、方法、属性和字段等成员声明。它根据输入文件路径和原始节点行号，添加 `Sandbox.Internal.SourceLocation` 特性。

处理前：

```csharp
#line 8 "PlayerComponent.cs"
[Description("Updates the player.")]
[return: Description("Whether the player was updated.")]
protected bool OnUpdate(
    [Description("The elapsed frame time.")] float deltaTime)
{
    UpdateMovement(deltaTime);
    return true;
}
```

处理后：

```csharp
#line 8 "PlayerComponent.cs"
[Description("Updates the player.")]
[return: Description("Whether the player was updated.")]
[Sandbox.Internal.SourceLocation("PlayerComponent.cs", 8)]
protected bool OnUpdate(
    [Description("The elapsed frame time.")] float deltaTime)
{
    UpdateMovement(deltaTime);
    return true;
}
```

`SourceLocation` 的实际参数来自：

- `TreeInput.FilePath` 或 `AddonFileMap` 映射后的相对路径。
- `originalNode.GetLocation().GetMappedLineSpan()` 得到的原始行号。

如果路径仍然是绝对路径，处理器会报告无法找到相对类位置的诊断。

## 最终结果

把上述几个阶段合并起来，方法节点最终概念上是：

```csharp
#line 8 "PlayerComponent.cs"
[Description("Updates the player.")]
[return: Description("Whether the player was updated.")]
[Sandbox.Internal.SourceLocation("PlayerComponent.cs", 8)]
protected bool OnUpdate(
    [Description("The elapsed frame time.")] float deltaTime)
{
    UpdateMovement(deltaTime);
    return true;
}
```

同时，`ComponentSubscriberInterfaces.VisitMethod` 在当前类的收集器中留下了接口信息，类节点最终概念上是：

```csharp
[Description("Controls the player every frame.")]
[Sandbox.Internal.SourceLocation("PlayerComponent.cs", 4)]
public partial class PlayerComponent
    : Component, Sandbox.Internal.IUpdateSubscriber
{
    // 上面的 OnUpdate 方法经过了方法级处理
}
```

## 关键区别表

| 调用 | 直接修改方法节点 | 修改所属类或生成额外内容 | 主要结果 |
| --- | --- | --- | --- |
| `ComponentSubscriberInterfaces.VisitMethod` | 否 | 是 | 收集生命周期接口 |
| `Description.VisitMethod` | 是 | 否 | 添加方法、参数、返回值描述 |
| `CodeGen.VisitMethod` | 有条件 | 可能添加缓存字段 | 包装 RPC 等方法 |
| `LinePreserve.AddLineNumber` | 是 | 否 | 添加 `#line` 映射 |
| `ClassFileLocation.VisitNode` | 是 | 否 | 添加 `SourceLocation` 特性 |

因此，`VisitMethodDeclaration` 本身是一个**方法级源码处理流水线**：它先识别方法所属的运行时角色，再逐步补充元数据、可选地改写执行入口，最后恢复源代码位置和文件位置的信息。
