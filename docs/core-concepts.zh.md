# 核心概念

[English](core-concepts.md) | 中文

Cordis 将插件激活与服务可用性、资源清理联系起来。插件定义要做的工作；Fiber 表示它的一次注册实例；实例的 Context 决定它能看到哪些服务，以及它创建的注册归哪个 Fiber 管理。

## Context 与执行

创建根 `Context`，承载应用的 Cordis 生命周期。应用代码注册插件、读取服务或操作 Cordis 状态时，通过 `RunAsync` 进入执行域；插件回调本身就在其中运行。

执行域串行处理回调，并允许同步重入。`await` 会让其他排队工作执行，因此一个异步操作并不是事务。不要同步阻塞生命周期 Task，也不要使用 `async void` 回调。离开执行域的代码，例如使用 `ConfigureAwait(false)` 的延续，在操作 Cordis 前必须重新进入执行域。

子 Context 共享运行时，但携带自己的服务视图和所属 Fiber。关闭根 Context 会等待其所属生命周期工作清理，并永久结束这次 .NET 执行生命周期。释放或重启根 Fiber 是另一项操作，不能替代应用退出时对根 Context 的释放。

## 插件与 Fiber

`Plugin<T>` 是可复用的定义，包含配置校验、依赖声明和一个 apply 函数，`T` 是校验后的配置类型。调用 `context.Plugin(plugin)` 会注册一个实例并返回其 `Fiber`；同一定义可以有多个实例。

```text
Root Context
└── registered Fiber ← Plugin<T> definition
    ├── plugin Context: service view and registration owner
    ├── effects: service registrations, listeners and registered cleanup
    └── child Fibers
```

Fiber 跟踪激活、失败、更新和清理。注册插件会启动生命周期工作，不会同步完成异步 apply。通过 `fiber.WaitAsync()` 等待当前工作并观察错误。如果缺少必需服务，等待结束后 Fiber 仍可能处于 pending；`WaitAsync` 不保证未来会出现依赖。

`Inject` 声明的依赖控制激活。必需服务消失时，依赖它的插件会清理当前激活所创建的工作；依赖重新可用后，仍已注册的插件可以再次激活。子插件的生命周期也跟随父插件。不再需要该注册时，调用 `DisposeAsync` 移除它。

## 服务与资源归属

`Provide` 发布命名服务。`Get<T>` 通过调用方的 Context 显式查找服务，`T` 检查返回值的 CLR 类型。服务身份由名称与隔离 realm 决定，换一个 `T` 不会创建另一个同名服务。带注入约束的访问和类型化服务视图另有规则，见[架构（英文）](architecture.md)。

服务注册是提供者 Fiber 拥有的 effect，Fiber 停止时撤销注册。**撤销注册不会释放被注册的对象。** 插件创建资源后，必须通过 effect 将所需清理接入自身生命周期。借用服务或向另一组件传递对象，不会自动转移资源所有权。

effect 在注册时执行 setup，并为所属 Fiber 保留清理工作。因此生命周期取决于注册位置：应该随插件停止的工作，使用该插件的 Context 注册，避免误挂到根 Context。

## 事件

事件监听保留注册时的 Context，用它确定监听器的服务视图和 effect 归属。`Undefined.Value` 表示缺席，与显式 `null` 保持区别。`Bail` 和 `SerialAsync` 中，`Undefined.Value`、`null`、`false` 让分发继续；零、空字符串或 `true` 会停止分发并成为结果。具体规则取决于事件操作，不按普通 C# 布尔判断解释。

进程内事件属于 Core。向远端调用方提供选定事件时，还会涉及声明、连接和订阅生命周期，见 [Remote 事件](remote-events.zh.md)。应用事件不会自动成为网络事件。

## 应用组合

Core 可以直接注册插件，[第一个插件](getting-started.zh.md)就是这种方式。需要由配置决定模块选择和更新时，再添加 Composition。

`Loader` 将 Profile、分组、include 和 patch 层组织成实时的 Entry 树。模块解析器为 Entry 提供插件实现，Loader 使用该 Entry 的配置应用插件。配置编辑与 Settings 复用同一套协调操作，不维护另一棵应用状态树。

插件在构建时已确定时，使用 `StaticModuleResolver`，包括 Native AOT 应用。普通 .NET 运行时上动态加载 DLL 时，使用 `ClrModuleResolver`。[Composition 示例](../examples/Composition/Program.cs)展示静态模块和实时更新；独立插件交付见 [CLR 制品流程](clr-artifacts.zh.md)。

Remote 是选定服务的可选边界，复用应用的 Context 和 Registry，由 Host 管理授权与连接生命周期。需要生成 .NET 或 Web 消费者时，继续阅读[作者指南](authoring.zh.md)。
