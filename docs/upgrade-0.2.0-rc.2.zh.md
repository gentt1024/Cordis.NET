# DSH 0.2.0-rc.2 升级

[English](upgrade-0.2.0-rc.2.md)

复审修正将引用快照与 Fiber 的完整有效对象分离，保留父级 raw 表达式的不透明性，并为 self-contained ASP.NET 宿主捕获 SDK 框架引用。CLR 框架识别明确区分适配器核心清单、可执行宿主的框架引用清单和 framework-dependent 宿主 deps 三种来源。既有打包 gate 通过隔离 NuGet 消费端运行五种实际 CLR 部署；托管 CI 执行仍是独立证据。

多文件作者示例应使用 `PatchLayers`，不要修改旧 `Patches` 兼容视图：

```csharp
var changed = bundle with
{
    PatchLayers = [
        new ConfigurationLayer("first.yml", [new EntryOptions { Id = "row", Config = "first" }]),
        new ConfigurationLayer("second.yml", [new EntryOptions { Id = "row", Config = "second" }])
    ]
};
```

单文件 `Patches` 返回原可变列表，多文件返回展开后的副本。赋值 `Patches` 会将整个 bundle 替换成主路径的一层；`PatchLayers` 保留文件来源和声明顺序。

Cordis.NET `0.2.0-alpha.1` 针对固定 DSH 提交 `639ed015397290b3745d163aafe02ffee4aa3f84`，包含下面的有界公开审查修复。此前升级检查不能替代当前候选的回归检查；最终源码结果记录在本次交付证据中。历史测试映射仍是历史证据。

| 行为 | 原生责任位置 | 相关测试 | 平台适配或限制 |
|---|---|---|---|
| 捕获描述及稳定引用 | `Configuration.cs`、`Plugin.cs`、`Fiber.cs` | `ConfigurationTests`、静态作者检查 | 一个绑定 validator，显式类型投影、等值及保存；不反射生成第二个 validator |
| volatile raw / effective / noSave、混合更新及代码换代 | 既有 Entry/Fiber 与 HMR 链 | `VolatileEntryTests`、V01/V02、HMR 测试 | 旧代引用冻结，通用 Loader 保留独立政策，保留激活等待与失败恢复 |
| 有序多 patch、来源、整包跳过及诊断 | `Profiles.cs`、Profile composition/session/maintenance | `ProfileUpgradeTests`、`ProfileSessionTests`、P01 | 多 patch 不自动增加所有 bundle watcher |
| Profile 准入及解析代 | DSH policy/admission、`DeploymentResolution.cs` | 政策、授权、链接解析及 HMR 测试 | 损坏授权不可覆写，不引入 Node loader 或 DSH 产品运行时 |
| R1：表达式 raw 等值 | `JsExpression`、既有 Entry diff | `PublicConfigurationReviewTests`、V03 | YAML 节点暴露 JSON `__jsExpr`；比较不求值，未知 CLR 值仍按身份比较 |
| R2：有限 lazy 解析 | `ConfigDescriptor`、`Fiber.ResolveConfig` | 有限叶/树、optional、raw 映射转 POCO、类型形状与 union 测试；V04 | 验证成功后沿 validator 输入遍历。不透明类型输入使用 `WithDescriptionData`，lazy union 声明选中的分支。raw diff 不运行回调，selector 不能序列化为数据 |
| R3：Bundle record 更新 | `Bundle` | 旧 `with`、来源、空/多文件视图 | 有序 layers 是唯一权威，赋值旧 `Patches` 将 bundle 替换成主路径上的一层 |
| R4：零引用默认等值 | 既有 Entry 提交链 | 等值显式默认及普通值变化；V05 | 等值零引用分支保留 raw 并跳过重新验证；force 仍处理 patch-context |
| R5：宿主框架加载 | `ClrModuleResolver` | 实际目录/单文件/ASP.NET 部署及私有依赖反例 | SDK 核心框架名称加宿主声明的 shared-framework manifest，排除应用/私有依赖 |
| 基础类型引用生命周期 | `Configuration.References.cs`、`Fiber.cs` | 可回收投影委托、退役 `CollectibleSettings` 的 scalar 引用及类型引用持有反例 | scalar 引用保留路径/cell 和字段快照，不额外持有完整有效 POCO 或投影 binding。主动保留完整有效对象，或持有以插件类型为泛型参数的引用，仍可能阻止其程序集回收 |
| R6–R8：政策与发布元数据 | DSH 政策、props、参考锁、包检查 | 固定四 bundle 政策、定向 audit、portable PDB/checksum 及隔离 NuGet 调试消费者 | 本库版本 `0.2.0-alpha.1`，嵌入源码支持离线调试，不宣称远端发布 |

既有 trace 集仍为 35 项，升级集为六项，包括本次 V03–V05。实际固定源码执行、.NET 测试、源码导航、独立探针与成对 trace 继续分别记录。配置 API 责任与迁移见[配置描述](configuration-description.zh.md)。

最终复跑使用既有验证及作者流程、静态 Native AOT、真实 CLR 部署 fixture 与独立包消费者。整个 DSH 外围测试及已停用的全仓 S0 gate 不是前置条件。Native AOT 覆盖静态 Core/Composition；运行时 CLR 换代及 JavaScript 求值保留既有平台边界。

托管 CI 与发布需要另行授权，历史断言审查债务单独保留。参考工具 `js-yaml` 经定向修复固定为 4.3.2，没有改变运行时 NuGet 依赖。离线符号验证不宣称可通过远端 SourceLink URL 抓取未推送的提交。实际命令与限制见[验证记录](validation.zh.md)和最终交付证据。
