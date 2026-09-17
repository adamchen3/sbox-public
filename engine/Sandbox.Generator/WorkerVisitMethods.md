# `Worker` 中 `Visit*` 方法处理说明

本文统一说明 `Sandbox.Generator.Worker` 中的 `Visit*` 方法。每一节都从真实源码调用链出发，展示输入、处理器、处理前后代码以及最终结果。

本文不展开 `base.Visit*` 的 Roslyn 默认递归实现；这里只说明 `Worker` 和 S&box 生成器实际增加的处理。

## 通用模型

`Worker` 继承 `CSharpSyntaxRewriter`，通过 `Run` 访问输入语法树：

```csharp
var node = TreeInput.GetRoot() as CSharpSyntaxNode;
OutputNode = Visit(node) as CSharpSyntaxNode;
```

多数入口使用两个节点变量：

- `_node` 或原始 `node`：输入节点，用于获取原始符号和源码位置。
- `node`：经过 `base.Visit*` 和生成器处理器逐步替换后的节点。

Roslyn 语法节点不可变，因此所谓“修改”通常是创建新节点并返回它。

## 1. `Worker.VisitMethodDeclaration`

### 方法入口

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

### 示例输入

假设文件为 `PlayerComponent.cs`，处于完整生成模式：

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

下文使用 `PlayerComponent.cs` 和第 `8` 行作为示例位置；真实行号由输入文件决定。

### 处理总览

```text
原始方法
  -> ComponentSubscriberInterfaces.VisitMethod
  -> Description.VisitMethod
  -> CodeGen.VisitMethod
  -> LinePreserve.AddLineNumber
  -> ClassFileLocation.VisitNode
最终方法节点
```

### `ComponentSubscriberInterfaces.VisitMethod`

调用：

```csharp
ComponentSubscriberInterfaces.VisitMethod( _node, symbol, this );
```

它要求方法所在类型继承 `Sandbox.Component`，方法不是 `virtual` 或 `abstract`，并且有方法体；然后按方法名映射接口：

```text
OnUpdate      -> Sandbox.Internal.IUpdateSubscriber
OnFixedUpdate -> Sandbox.Internal.IFixedUpdateSubscriber
OnPreRender   -> Sandbox.Internal.IPreRenderSubscriber
```

`OnUpdate` 满足条件，因此收集：

```csharp
master.AddBaseTypeToCurrentClass(
    "Sandbox.Internal.IUpdateSubscriber" );
```

方法节点本身不变：

```csharp
protected bool OnUpdate(float deltaTime)
{
    UpdateMovement(deltaTime);
    return true;
}
```

类节点之后会从：

```csharp
public partial class PlayerComponent : Component
```

变为：

```csharp
public partial class PlayerComponent
    : Component, Sandbox.Internal.IUpdateSubscriber
```

### `Description.VisitMethod`

调用：

```csharp
Description.VisitMethod( ref node, symbol, this );
```

它读取方法的 XML 文档注释，为方法、参数和返回值添加描述特性。

处理前：

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

处理后：

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

如果已有手写 `[Description]`，生成器会报告警告，不再重复添加。

### `CodeGen.VisitMethod`

调用：

```csharp
CodeGen.VisitMethod( ref node, symbol, this );
```

它只对带有 `CodeGeneratorAttribute` 且配置了 `WrapMethod` 的方法进行包装。本例的 `OnUpdate` 没有这类配置，因此：

处理前：

```csharp
protected bool OnUpdate(float deltaTime)
{
    UpdateMovement(deltaTime);
    return true;
}
```

处理后：

```csharp
protected bool OnUpdate(float deltaTime)
{
    UpdateMovement(deltaTime);
    return true;
}
```

真实 RPC 变体：

处理前：

```csharp
[Rpc.Host]
public void AskToLeave(PlayerController player)
{
    Leave(player);
}
```

处理后，概念上会经过 RPC 回调：

```csharp
[Rpc.Host]
public void AskToLeave(PlayerController player)
{
    Sandbox.Rpc.OnCallRpc(
        /* 包装原始 Leave(player) 的回调 */,
        player);
}
```

实际生成代码还会根据参数、返回值、`async`/`Task`、静态/实例方法和回调签名生成不同形式。

### `LinePreserve.AddLineNumber`

调用：

```csharp
node = LinePreserve.AddLineNumber(
    node, _node, TreeInput, this );
```

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

处理后：

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

它只维护编译器诊断和调试位置，不改变运行时逻辑。非完整生成模式或 `_gen_` 文件会跳过。

### `ClassFileLocation.VisitNode`

调用：

```csharp
node = ClassFileLocation.VisitNode(
    node, _node, symbol, this, TreeInput )
    as MethodDeclarationSyntax;
```

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

它使用输入文件的相对路径和原始节点行号生成 `SourceLocation`。路径无法转换成相对路径时会报告诊断。

### 最终结果

方法最终概念上为：

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

所属类同时会得到：

```csharp
[Description("Controls the player every frame.")]
[Sandbox.Internal.SourceLocation("PlayerComponent.cs", 4)]
public partial class PlayerComponent
    : Component, Sandbox.Internal.IUpdateSubscriber
{
    // OnUpdate 已经过方法级处理
}
```

## 2. `Worker.VisitExpressionStatement`

### 处理逻辑

```csharp
public override SyntaxNode VisitExpressionStatement(
    ExpressionStatementSyntax _node )
{
    var node = base.VisitExpressionStatement( _node )
        as ExpressionStatementSyntax;

    if ( IsGeneratedRazorFile() ) return node;

    node = LinePreserve.AddLineNumber(
        node, _node, TreeInput, this );

    return node;
}
```

它处理“表达式加分号”的语句，例如调用、赋值和自增。Razor 生成文件会跳过处理。

### 处理前后

处理前：

```csharp
void Update()
{
    player.Move();
}
```

处理后：

```csharp
void Update()
{
#line 4 "Player.cs"
    player.Move();
}
```

这里唯一的实际改写来自 `LinePreserve.AddLineNumber`；调用表达式和运行时行为不变。

## 3. `Worker.VisitAnonymousMethodExpression`

### 处理逻辑

```csharp
public override SyntaxNode VisitAnonymousMethodExpression(
    AnonymousMethodExpressionSyntax _node )
{
    var node = base.VisitAnonymousMethodExpression( _node )
        as AnonymousMethodExpressionSyntax;

    node = LinePreserve.AddLineNumber(
        node, _node, TreeInput, this );

    return node;
}
```

它处理 `delegate` 形式的匿名方法，不处理 lambda 专用节点。

### 处理前后

处理前：

```csharp
Action action = delegate
{
    Console.WriteLine("Hello");
};
```

处理后：

```csharp
Action action = delegate
{
#line 3 "Example.cs"
    Console.WriteLine("Hello");
};
```

它只添加源位置映射，不改变匿名方法逻辑。

## 4. `Worker.VisitInvocationExpression`

### 处理逻辑

```csharp
public override SyntaxNode VisitInvocationExpression(
    InvocationExpressionSyntax node )
{
    var location = node.GetLocation();
    var symbolInfo = Model.GetSymbolInfo( node.Expression );
    node = base.VisitInvocationExpression( node )
        as InvocationExpressionSyntax;

    var symlist = symbolInfo.CandidateSymbols;
    if ( symbolInfo.Symbol is not null )
        symlist = ImmutableArray.Create( symbolInfo.Symbol );

    CloudAssetProvider.VisitInvocation(
        ref node, location, symlist, this );
    StringTokenUpgrader.VisitInvocation(
        ref node, location, symlist, this );

    return node;
}
```

它先解析调用目标，再把唯一 `Symbol` 或候选符号交给两个专项处理器。

### `StringTokenUpgrader.VisitInvocation`

假设 `SetName` 的参数是 `StringToken`。

处理前：

```csharp
SetName("player_name");
```

处理后：

```csharp
SetName(global::Sandbox.StringToken.Literal(
    "player_name", 123456789));
```

真实哈希由 `MurmurHash2(true)` 计算；非字符串字面量不会转换。

### `CloudAssetProvider.VisitInvocation`

如果目标方法带有 `CloudAssetProviderAttribute`，调用节点本身不变，但会追加程序集级资源属性。

处理前：

```csharp
LoadPackageAsset("my_package");
```

处理后，调用节点仍是：

```csharp
LoadPackageAsset("my_package");
```

假设解析器返回 `content/my_package/wood.vmat`，额外生成：

```csharp
[assembly: Sandbox.Cloud.Asset(
    "my_package",
    "content/my_package/wood.vmat")]
```

参数数量错误、参数不是字符串字面量或资源无法解析时会报告诊断。

## 5. `Worker.VisitIdentifierName`

### 处理逻辑

```csharp
public override SyntaxNode VisitIdentifierName(
    IdentifierNameSyntax node )
{
    var rewritten = StringBuilderRedirect
        .VisitIdentifierName( node, this );
    if ( rewritten is not null ) return rewritten;

    rewritten = ArrayPoolSharedRedirect
        .VisitIdentifierName( node, this );
    if ( rewritten is not null ) return rewritten;

    return base.VisitIdentifierName( node );
}
```

它按顺序尝试两个替换器；第一个返回非空节点后就停止，不再调用第二个替换器或默认遍历。

### `StringBuilderRedirect.VisitIdentifierName`

仅在启用 corelib polyfill 且语义上确实是 `System.Text.StringBuilder` 时替换。

处理前：

```csharp
StringBuilder builder = new StringBuilder();
```

处理后：

```csharp
global::Sandbox.Internal.SafeStringBuilder builder =
    new global::Sandbox.Internal.SafeStringBuilder();
```

同名但属于其他命名空间的类型不会被替换。

### `ArrayPoolSharedRedirect.VisitIdentifierName`

处理通过 `using static` 等形式出现的、单独绑定到 `ArrayPool<T>.Shared` 的名称。

处理前：

```csharp
var buffer = Shared.Rent(16);
```

处理后概念上为：

```csharp
var buffer = global::Sandbox.Internal.PublicArrayPool<byte>
    .Shared.Rent(16);
```

该替换同样要求 corelib polyfill 已启用并且符号绑定正确。

## 6. `Worker.VisitQualifiedName`

### 处理逻辑

```csharp
public override SyntaxNode VisitQualifiedName(
    QualifiedNameSyntax node )
{
    var rewritten = StringBuilderRedirect
        .VisitQualifiedName( node, this );
    if ( rewritten is not null ) return rewritten;

    return base.VisitQualifiedName( node );
}
```

它在访问子节点前尝试替换整个限定名称，确保 SemanticModel 查询使用原始节点。

### 处理前后

处理前：

```csharp
System.Text.StringBuilder builder =
    new System.Text.StringBuilder();
```

处理后：

```csharp
global::Sandbox.Internal.SafeStringBuilder builder =
    new global::Sandbox.Internal.SafeStringBuilder();
```

只有当限定名解析为真正的 `System.Text.StringBuilder` 且 polyfill 启用时才会发生变化。

## 7. `Worker.VisitMemberAccessExpression`

### 处理逻辑

```csharp
public override SyntaxNode VisitMemberAccessExpression(
    MemberAccessExpressionSyntax node )
{
    var visited = base.VisitMemberAccessExpression( node )
        as ExpressionSyntax;
    if ( visited is null ) return visited;

    var rewritten = ArrayPoolSharedRedirect
        .VisitMemberAccess( node, visited, this );
    return rewritten ?? visited;
}
```

它先递归处理成员访问表达式，再尝试重定向 `ArrayPool<T>.Shared`。

### 处理前后

处理前：

```csharp
var buffer = ArrayPool<byte>.Shared.Rent(16);
```

处理后：

```csharp
var buffer = global::Sandbox.Internal.PublicArrayPool<byte>
    .Shared.Rent(16);
```

替换由符号决定，而不是单纯根据文本 `Shared` 决定。

## 8. `Worker.VisitBlock`

### 处理逻辑

```csharp
public override SyntaxNode VisitBlock( BlockSyntax node )
{
    node = base.VisitBlock( node ) as BlockSyntax;

    if ( IsGeneratedRazorFile() ) return node;

    bool changes = false;
    var statements = node.Statements;

    if ( false && IsFullGeneration )
    {
        // 查找调用语句并插入 EnsureSufficientExecutionStack
    }

    if ( changes )
        return node.WithStatements( statements );

    return node;
}
```

### 当前实际结果

处理前：

```csharp
{
    DoWork();
}
```

当前处理后仍是：

```csharp
{
    DoWork();
}
```

因为插入逻辑被 `false && IsFullGeneration` 永久禁用；Razor 生成文件也会直接返回。

### 如果将来启用分支

处理后概念上会变成：

```csharp
{
    global::System.Runtime.CompilerServices.RuntimeHelpers
        .EnsureSufficientExecutionStack();
    DoWork();
}
```

## 9. `Worker.VisitFieldDeclaration`

### 处理逻辑

```csharp
public override SyntaxNode VisitFieldDeclaration(
    FieldDeclarationSyntax _node )
{
    var symbol = Model.GetDeclaredSymbol( _node );
    var node = base.VisitFieldDeclaration( _node )
        as FieldDeclarationSyntax;

    node = ClassFileLocation.VisitNode(
        node, _node, symbol, this, TreeInput )
        as FieldDeclarationSyntax;

    return node;
}
```

它不改变字段类型或初始化表达式，只调用 `ClassFileLocation.VisitNode` 添加源码位置。

### 处理前后

处理前：

```csharp
private int count;
```

处理后：

```csharp
[Sandbox.Internal.SourceLocation("Player.cs", 5)]
private int count;
```

## 10. `Worker.VisitEnumMemberDeclaration`

### 处理逻辑

```csharp
public override SyntaxNode VisitEnumMemberDeclaration(
    EnumMemberDeclarationSyntax _node )
{
    var symbol = Model.GetDeclaredSymbol( _node );
    var node = base.VisitEnumMemberDeclaration( _node )
        as EnumMemberDeclarationSyntax;

    Description.VisitEnumMember( ref node, symbol, this );

    return node;
}
```

### 处理前后

处理前：

```csharp
/// <summary>The player is running.</summary>
Running,
```

处理后：

```csharp
[Description("The player is running.")]
Running,
```

没有 XML summary 时，`Description.VisitEnumMember` 不会添加描述特性。

## 11. `Worker.VisitPropertyDeclaration`

### 方法入口

```csharp
public override SyntaxNode VisitPropertyDeclaration(
    PropertyDeclarationSyntax _node )
{
    var symbol = Model.GetDeclaredSymbol( _node );
    var node = base.VisitPropertyDeclaration( _node )
        as PropertyDeclarationSyntax;

    DefaultValue.VisitProperty( ref node, symbol, this );
    Description.VisitProperty( ref node, symbol, this );
    CodeGen.VisitProperty( ref node, symbol, this );

    node = LinePreserve.AddLineNumber(
        node, _node, TreeInput, this );
    node = ClassFileLocation.VisitNode(
        node, _node, symbol, this, TreeInput )
        as PropertyDeclarationSyntax;

    return node;
}
```

### 处理前后

输入：

```csharp
/// <summary>Maximum players.</summary>
public int MaxPlayers { get; set; } = 8;
```

`DefaultValue.VisitProperty` 生成实际默认值的元数据：

```csharp
[DefaultValue(8)]
```

`Description.VisitProperty` 生成：

```csharp
[Description("Maximum players.")]
```

`LinePreserve` 和 `ClassFileLocation` 再添加位置数据。最终概念上：

```csharp
#line 4 "Settings.cs"
[DefaultValue(8)]
[Description("Maximum players.")]
[Sandbox.Internal.SourceLocation("Settings.cs", 4)]
public int MaxPlayers { get; set; } = 8;
```

`= 8` 仍然负责真正初始化；`DefaultValue(8)` 只是元数据。

如果属性带 `WrapPropertyGet` 或 `WrapPropertySet`，`CodeGen.VisitProperty` 还会生成 getter/setter 包装逻辑。

## 12. `Worker.VisitClassDeclaration`

### 方法入口

`VisitClassDeclaration` 负责建立当前类的收集上下文，再递归访问类成员：

```csharp
public override SyntaxNode VisitClassDeclaration(
    ClassDeclarationSyntax _node )
{
    var symbol = Model.GetDeclaredSymbol( _node ) as INamedTypeSymbol;

    var oldClassAdditions = ClassAdditions;
    var oldClassModifiers = ClassModifiers;
    var oldClassAttributes = ClassBaseTypes;

    ClassAdditions = new List<string>();
    ClassModifiers = new List<string>();
    ClassBaseTypes = new List<string>();

    var node = _node;

    try
    {
        node = base.VisitClassDeclaration( _node ) as ClassDeclarationSyntax;
        Description.VisitClass( ref node, symbol, this );
        node = ClassFileLocation.VisitNode(
            node, _node, symbol, this, TreeInput )
            as ClassDeclarationSyntax;

        // 根据 ClassAdditions 创建额外语法树
    }
    finally
    {
        // 把 ClassModifiers 和 ClassBaseTypes 添加回当前类
        ClassAdditions = oldClassAdditions;
        ClassModifiers = oldClassModifiers;
        ClassBaseTypes = oldClassAttributes;
    }

    node = LinePreserve.AddLineNumber(
        node, _node, TreeInput, this );
    return node;
}
```

### 处理前

```csharp
/// <summary>Controls the player every frame.</summary>
public partial class PlayerComponent : Component
{
    protected void OnUpdate()
    {
        UpdatePlayer();
    }
}
```

访问 `OnUpdate` 时，`ComponentSubscriberInterfaces.VisitMethod` 会收集：

```csharp
Sandbox.Internal.IUpdateSubscriber
```

### 处理后

`VisitClassDeclaration` 会把收集到的接口追加到类声明：

```csharp
[Description("Controls the player every frame.")]
[Sandbox.Internal.SourceLocation("PlayerComponent.cs", 4)]
#line 4 "PlayerComponent.cs"
public partial class PlayerComponent
    : Component, Sandbox.Internal.IUpdateSubscriber
{
    protected void OnUpdate()
    {
        UpdatePlayer();
    }
}
```

如果某个处理器调用 `AddToCurrentClass` 生成成员，类必须声明为 `partial`；否则会报告错误并拒绝添加生成成员。生成成员可能被放入新的附加语法树，之后与原类一起编译。

## 阶段对比表

| `Worker.Visit*` | 直接修改当前节点 | 调用的主要处理器 | 主要结果 |
| --- | --- | --- | --- |
| `VisitMethodDeclaration` | 是，有条件 | Component、Description、CodeGen、LinePreserve、ClassFileLocation | 方法元数据、可选包装和源码位置 |
| `VisitExpressionStatement` | 是 | LinePreserve | 表达式语句的 `#line` 映射 |
| `VisitAnonymousMethodExpression` | 是 | LinePreserve | 匿名方法的 `#line` 映射 |
| `VisitInvocationExpression` | 是，有条件 | CloudAssetProvider、StringTokenUpgrader | 参数替换或程序集资源属性 |
| `VisitIdentifierName` | 是，有条件 | StringBuilderRedirect、ArrayPoolSharedRedirect | 类型/成员名称重定向 |
| `VisitQualifiedName` | 是，有条件 | StringBuilderRedirect | 限定类型名称重定向 |
| `VisitMemberAccessExpression` | 是，有条件 | ArrayPoolSharedRedirect | `ArrayPool<T>.Shared` 重定向 |
| `VisitBlock` | 当前否 | 当前插入分支被禁用 | 当前通常原样返回 |
| `VisitFieldDeclaration` | 是 | ClassFileLocation | 字段源码位置 |
| `VisitEnumMemberDeclaration` | 是，有条件 | Description | 枚举成员描述 |
| `VisitPropertyDeclaration` | 是 | DefaultValue、Description、CodeGen、LinePreserve、ClassFileLocation | 属性元数据、包装和源码位置 |
| `VisitClassDeclaration` | 是 | Description、ClassFileLocation、LinePreserve | 类元数据、接口和附加成员汇总 |

## 共同限制

- 许多描述、默认值、RPC 和源码位置处理要求 `IsFullGeneration`。
- `StringBuilder` 和 `ArrayPool` 重定向要求 `CorelibPolyfillsEnabled`。
- 重定向逻辑依赖 `SemanticModel`，不会只按文本名称盲目替换。
- `VisitBlock` 当前的 `EnsureSufficientExecutionStack` 分支由 `false &&` 禁用。
- 生成附加成员时，目标类必须是 `partial`。
- `Description` 和 `DefaultValue` 会检查用户是否已经手动添加对应特性，并在不必要时报告诊断或跳过生成。
