# Cordis.NET.Clr

[English](https://github.com/gentt1024/Cordis.NET/blob/main/src/Cordis.Clr/README.md) | 中文

从完整制品目录加载独立部署的 CLR 插件、替换代码并请求协作卸载；同时提供 Profile 安装使用的 SDK／NuGet 工具链。

## 安装

使用 .NET 10 SDK，并保持 Cordis.NET 包版本一致。

```console
dotnet add package Cordis.NET.Clr --version 0.2.1-alpha.1
```

## 按任务使用

稳定制品交付遵循[Prepare → Publish → Validate → LoadDirectory](https://github.com/gentt1024/Cordis.NET/blob/main/docs/clr-artifacts.zh.md)。完整本地源安装见[独立插件](https://github.com/gentt1024/Cordis.NET/blob/main/examples/ManagedPlugin/README.md)及[应用示例](https://github.com/gentt1024/Cordis.NET/blob/main/examples/ManagedApplication/README.md)。准备插件源码需要选定的 .NET SDK 与显式源，启动已准备制品不重新构建。

注册 `ClrModuleDefinition`，通过 `sharedContracts` 传入 Host 的精确附加合同程序集。托管安装使用 `session.ConfigurationOperations`，Host 将构建执行与安装分别授权。代码替换见[CLR 作者指南](https://github.com/gentt1024/Cordis.NET/blob/main/docs/authoring.zh.md#交付-clr-插件)。

## 生命周期与支持范围

动态 CLR 加载需要普通 .NET 运行时，以 Host 进程权限执行，不提供沙箱或 Native AOT 路线。默认直接加载稳定目录；只有需要原地重建的外部开发输出才显式选择 shadow copy。

释放解析器前先停止所属 Context。卸载请求、GC 回收与文件删除是独立观察。逻辑移除保留文件，物理删除是显式离线操作。原生库沿用 CLR／操作系统行为，不保证各 ALC 状态隔离。

NuGet 消费者通过 `buildTransitive` 捕获可执行 Host 的框架引用。仓库外通过源码 `ProjectReference` 消费时，须在可执行项目中导入 `src/Cordis.Clr/buildTransitive/Cordis.NET.Clr.targets`。Host 声明每个计划共享的框架，私有应用依赖不会自动共享。

Cordis.NET 独立维护，不是 DeepSeek、Cordiverse 或 Microsoft 的官方项目。
