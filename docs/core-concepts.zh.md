# 核心概念

[English](core-concepts.md)

## 上下文与执行

`Context` 是插件环境与服务视图。`RunAsync` 建立串行执行域，同时允许同步重入。处置根上下文会结束宿主生命周期。

## 插件与 Fiber

`Plugin<T>` 将配置验证与同步、异步或基于 effect 的 apply 函数组合起来。应用插件会创建 `Fiber`，由它拥有 effect，并跟踪就绪、失败和处置状态。依赖声明控制激活与服务可见性。

## 服务、事件与 Effect

服务归提供者所有，并通过调用方上下文形成视图。事件保留监听器上下文，并区分 `Undefined.Value`、`null`、`false` 和零。Effect 立即执行 setup，并随所属 fiber 清理。

## 组合

`Loader`、group、include、patch 层和 profile 将配置转为实时 entry 树。模块解析器提供插件。Native AOT 使用 `StaticModuleResolver`，运行时 DLL 加载使用 `ClrModuleResolver`；也可以按应用的信任与部署模型组合解析器。
