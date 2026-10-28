# 第一个插件

[English](getting-started.md) | 中文

本教程创建一个控制台应用，注册插件、调用它提供的服务，再停止插件。使用已发布的 `0.2.1-alpha.1` 包。你需要 .NET 10 SDK 和 nuget.org 访问能力，无需克隆仓库或安装 Node。

## 创建应用

在准备创建项目的目录中运行：

```console
dotnet new console -n MyApp -f net10.0
dotnet add MyApp package Cordis.NET.Core --version 0.2.1-alpha.1 --source https://api.nuget.org/v3/index.json
```

`Cordis.NET.Core` 提供插件运行时，这个程序不需要其他包。从 Cordis.NET 源码开发时，使用仓库 `global.json` 固定的 SDK。

## 注册插件并调用服务

将 `MyApp/Program.cs` 替换为：

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

程序使用三个对象：

- `Context` 提供服务视图和执行域。根 Context 拥有这个应用的插件生命周期。
- `Plugin<object?>` 定义激活时要做的事。这里以 `answer` 为服务名注册了一个函数。
- `Fiber` 表示已注册的插件实例，拥有 `Apply` 创建的服务注册。

`RunAsync` 进入 Context 执行域；插件回调本身就在其中运行。`WaitAsync` 等待插件当前的生命周期工作，激活出错时会报告异常。这个插件没有依赖，因此读取服务前它的 apply 已完成。如果插件缺少依赖，`WaitAsync` 可能在 Fiber 仍为 pending 时结束，不会等待未来的提供者。

## 运行并停止

```console
dotnet run --project MyApp
```

程序输出 `41` 后退出。`fiber.DisposeAsync()` 停止插件并撤销服务注册，外层 `await using` 随后关闭根 Context。撤销注册不会释放被注册的值；如果插件创建了需要释放的资源，应通过 effect 注册清理。见[服务与资源归属](core-concepts.zh.md#服务与资源归属)。

`answer()` 是普通 C# 调用。应用内部的服务使用不需要网络服务器或 Remote 合同。

## 继续构建应用

| 下一项任务 | 指南 |
|---|---|
| 理解依赖、服务可见性和插件清理 | [核心概念](core-concepts.zh.md)。 |
| 按配置加载静态模块并更新它们 | 添加相同版本的 `Cordis.NET.Composition`，参考 [Composition 示例](../examples/Composition/Program.cs)。 |
| 向 .NET 或 Web 客户端提供选定服务 | 先[声明与生成 Remote 合同](authoring.zh.md#声明与生成原生-remote-合同)，再[登记、调用与撤销](authoring.zh.md#登记调用与撤销合同)。 |
| 生成不依赖提供者程序集的 .NET 客户端 | [模型与客户端项目配置](authoring.zh.md#生成独立-net-客户端)，以及[后端支持范围](development.zh.md#生成后端支持范围)。 |
| 安装独立部署的 CLR 插件 | [托管插件](../examples/ManagedPlugin/README.md)、[托管应用](../examples/ManagedApplication/README.md)和 [CLR 制品流程](clr-artifacts.zh.md)。 |
| 从命令行校验或预览配置 | 安装匹配版本的 `Cordis.NET.Tool`，见 [CLI 指南（英文）](../tools/Cordis.Cli/README.md)。 |

Cordis.NET 包与工具应使用匹配版本；切换版本时，重新构建生成式提供者和客户端。浏览器归档位于[对应发行页](https://github.com/gentt1024/Cordis.NET/releases/tag/v0.2.1-alpha.1)。使用 main 文档中的后续能力前，先核对[当前源码状态](development.zh.md#当前支持与发行状态)。
