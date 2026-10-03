# 兼容性与证据

[English](compatibility.md)

Cordis.NET 以[上游文档](upstream.zh.md)所述、DeepSeek Harness 锁定的 vendored Cordis 行为为目标。兼容性按可观察契约评估，不能只看包名或测试数量。

## 状态词汇

- **已实现：** .NET 行为已经存在。
- **已执行：** 已运行具名 .NET 测试、上游源码测试或配对场景。
- **断言已审阅：** 已逐项匹配上游 setup、helper 与断言，或记录明确适配。
- **不支持：** 行为位于产品或平台边界之外。

不可变的历史清单针对 DSH `ddefc45fbc7f8e46dd73185e68295696d1297887`，包含 631 个上游候选实例，不代表新基线的断言闭合状态。本次升级的范围、行为映射与平台适配见[升级矩阵](upgrade-0.2.0-rc.2.zh.md)。目前 323 项记录为实现完成，其中 36 项完成了断言审阅，287 项尚未完成全部断言审阅；其余 308 项明确为未完成，通常涉及 Cordis.NET 范围之外的 JavaScript 或 DeepSeek Harness 产品行为。这些数字不代表兼容率。

## 部署边界

| 路径 | 支持的行为 | 边界 |
|---|---|---|
| 静态 Core 与 Composition | JIT 与 Native AOT；静态模块注册、生命周期、配置、profile 与部分扩展 | 新插件代码需要重新发布应用 |
| CLR 适配器 | 托管程序集的运行时加载、替换、回滚与协作式卸载 | 仅普通运行时；保留的 CLR 引用可能阻止回收 |
| JavaScript 适配器 | 通过 Jint 实际求值 `!!js`，并提供受控上下文桥接 | 只处理受信任配置；不承诺 Node 模块环境、沙箱或 Native AOT |

Cordis.NET 不执行任意 TypeScript 插件。JavaScript 原型、抛出的 primitive、循环对象图、Node 解析、agent/LLM 功能、遥测、用户界面和包安装流程都不是通用兼容性承诺。

部署解析代在既有包映射和本地映射之外接受显式外部链接根。每一代捕获链接的真实目标；撤下根只停止其调用者的路由，不卸载保留的模块。同名链接改指另一目标需要重启，即使中间曾撤下该根。链接的 peer 声明从当前祖先 manifest 读取；私有依赖查找仍由宿主提供，CLR 导出仍使用显式映射。这不安装 Node loader，也不模拟 Node exports、包自引用、CommonJS/ESM 差异或 worker 传播。

HMR 等待候选生命周期完成；缺失服务时 fiber 可以停在 Pending。激活失败恢复原插件，后续清理、恢复或诊断观察者失败不会覆盖原始错误。保留的配置引用停留在该代最后提交的值，替换与恢复的 fiber 获得全新引用。若引用的泛型值类型属于可回收 bundle，宿主保留该引用也会保留 bundle，直到释放引用。CLR bundle 的私有托管依赖从影子副本或显式共享的宿主契约解析；无关的默认上下文程序集不能补齐缺失私有依赖。框架程序集仍使用运行时，原生库查找保留 CLR/操作系统的加载规则。

协作式卸载观察者错误通过 `ClrUnloadObservation.UnloadError` 的文本快照暴露，不保留异常实例或其可回收类型。`UnloadRequested` 记录一次尝试，`IsCollected` 观察托管加载上下文包装对象的回收。`Unloading` handler 抛错可能中止底层释放；即使包装对象已回收，影子 DLL 仍可能被锁定。宿主必须修复或移除失败的 handler，在仍持有实际上下文时显式再次请求 `Unload()`；适配器不会自动重试。影子目录删除是独立结果，保留引用或中断的释放仍可能阻止删除。

## 证据

最近完成的运行汇总见[验证记录](validation.zh.md)。`docs/upstream-tests.json` 是不可变候选清单；`docs/test-map.json` 保存当前处置；`docs/scenario-map.json` 记录差分场景。上游源码执行、.NET 测试、配对 trace 与人工断言审阅仍是彼此独立的证据类别。
