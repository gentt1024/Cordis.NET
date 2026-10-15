# 入门

[English](getting-started.md)

## 选择批次与 SDK

使用 .NET 10 和所选批次 global.json 指定的 SDK；当前源码使用 10.0.111。从发行或[验证回执](validation.zh.md#当前候选索引)选择精确 Cordis.NET 版本与 feed。alpha5 发布前，使用该回执提供的隔离候选 feed。即使本地 CI 包文字版本相同，也不要与已发布 alpha.4 混用。

将下方 `<selected-version>` 和 `<feed>` 替换为该批次的精确版本及 NuGet 源。公开发行可以使用 https://api.nuget.org/v3/index.json；未发布批次需使用随附的本地 feed。这些是占位符，不要求发布或重命名包。

## 运行普通插件

创建控制台应用。Core 不需要 clone 仓库、Node、HTTP 服务或源生成配置。

```console
dotnet new console -n MyApp -f net10.0
dotnet add MyApp package Cordis.NET.Core --version <selected-version> --source <feed>
```

将 MyApp/Program.cs 替换为：

```csharp
using Cordis;

await using var context = new Context();
await context.RunAsync(async owner =>
{
    var plugin = new Plugin<object?>
    {
        Apply = (ctx, _) =>
        {
            ctx.Provide("answer", (Func<int>)(() => 41));
        }
    };
    var fiber = owner.Plugin(plugin);
    await fiber.WaitAsync();
    var answer = owner.Get<Func<int>>("answer")
        ?? throw new InvalidOperationException("The answer service is unavailable.");
    Console.WriteLine(answer());
    await fiber.DisposeAsync();
});
```

```console
dotnet run --project MyApp
```

程序输出 41 后退出。根 Context 拥有插件 Fiber，Fiber 拥有服务注册。WaitAsync 在读取前等待激活，DisposeAsync 在根关闭前撤销插件。调用位于 RunAsync 的 Context 执行域内。激活失败从 WaitAsync 抛出，不是 RemoteResult。应用拥有的外部资源仍由应用释放。

需要 Profile、配置和静态模块时，从同批次添加 Cordis.NET.Composition。现有 [Composition 示例](../examples/Composition/Program.cs)通过 StaticModuleResolver 和 Loader 更新 live 配置。普通插件／服务调用仍在进程内。依赖、服务视图和 effect 见[核心概念](core-concepts.zh.md)。

## 将现有 Host 接到生成消费者

Composition 提供作者声明和生成绑定，AspNetCore 提供 HTTP unary 与 WebSocket mux 适配。复用现有 Cordis Context 和 Registry，Host 拥有授权与 Connection，不为 RPC 再建插件运行时。

先按[作者指南](authoring.zh.md)的声明、生成和注册／调用／撤销章节接线，其中包含作者声明、JSON context、Host 绑定与 Web 调用。需要不依赖 Provider 实现的 .NET 调用方时，按该指南的模型／客户端项目配置执行普通 build，生成代码与 STJ 参与同次编译。

独立后端能表达合同时，使用保存声明事实和显式引用模型；导出需要作者运行时 metadata 时，使用实际 contribution 的 TypertArtifacts.GenerateClient。两者共享 emitter 与 Connection，离线生成不执行任意作者代码。[后端支持形状](development.zh.md#alpha5-独立后端处置)定义边界。

Unary 调用先检查 RemoteResult.Ok，再读取 Value／Error；准备失败和流错误具有独立合同。先释放客户端，再释放其借用的 Connection，最后关闭宿主资源；客户端不会关闭借用的 HttpClient。应用组合可参考现有 [Probes 应用](../examples/Probes/Program.cs)，增加监听时见 [Remote 事件](remote-events.zh.md)。运行 metadata 可用不等于任意 DTO 都支持静态 AOT。

## 交付独立 CLR 插件

沿用现有[托管插件](../examples/ManagedPlugin/README.md)和[托管应用](../examples/ManagedApplication/README.md)，其中演示包元数据、选定 feed、构建授权和安装命令。

标准路线由 DotnetPluginToolchain 与 ProfileSession.ConfigurationOperations 执行 Prepare → Publish → Validate → LoadDirectory，直接加载验证过的稳定目录。已安装包升级保存新的精确版本并要求重启；逻辑移除保留仍可能被代码或其他进程使用的文件。完整回执、所有权和显式离线删除见 [CLR 制品](clr-artifacts.zh.md)。动态 CLR 加载不支持 Native AOT。

## 验证配置并继续

所选 Cordis.NET.Tool 批次提供 cordis validate 和 cordis preview。使用 --version 与 --add-source 从匹配 feed 安装精确工具版本后执行：

```console
cordis validate ./profile/cordis.patch.yml
cordis preview ./profile/cordis.patch.yml
```

Settings、管理、可选适配器和生命周期细节见[作者指南](authoring.zh.md)。[当前支持](development.zh.md#当前支持与发行状态)与[历史证据](validation.zh.md)分别记录。
