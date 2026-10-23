# 验证

[English](validation.md)

## 候选 Remote 身份与资源，2026-10-23

S5 遵循[范围明确的候选例外](compatibility.zh.md#候选-remote-身份与资源分片2026-10-23)。候选源码仍为 `687f282e8625c6b8d11ff58211a90524be2e4317`；正式锁和历史清单仍为 `639ed015397290b3745d163aafe02ffee4aa3f84`。本轮环境为 macOS arm64、.NET SDK 10.0.111/runtime 10.0.11、Node 24.13.0。

未修改的候选原始测试共 65 项通过，无失败或跳过：`packages/typert/protocol/tests/remote-access.spec.ts` 8 项、`packages/typert/registry/tests/typert.spec.ts` 27 项、`packages/api/gateway/tests/gateway-access.host.spec.ts` 30 项。外部精简工具环境使用候选选定的 Vitest 4.1.11、TypeScript 6.0.3、Vite 8.2.2；这不是完整候选 pnpm workspace 构建或 TypeScript 类型检查。这些套件包含 S5 尚未实现的 S6/S7 参考行为，因此原始套件通过只代表参考端证据，不代表原生授权完全等价。

| S5 源码合同 | 原生检查 |
|---|---|
| `principalOf` 的 guest 身份、owner/调用外省略和 kind 大小写 | `TypertIdentityTests.Principal_is_detached_and_only_non_owner_calls_are_attributed`，以及非法 JSON 身份边界用例 |
| 资源 kind/field 所有权、保留 global、resolver 覆盖 | `TypertResourceRegistryTests` 全部 17 项，涵盖观察者异常隔离、订阅撤销、独立有序快照和原注册清理 |
| 已关闭 Peer 早于准备拒绝，流寿命与已运行 unary 分离 | `TypertIdentityTests` 的直接、显式、挂载调用、两个事件入口、owner 撤销、同级隔离、未使用 uplink 释放和缓冲事件取消 |
| wire 字段不能选择 Host 身份 | `RemoteCarrierTests.Carrier_identity_is_host_selected_and_non_owner_never_reaches_lookup`，使用真实 HTTP 和 WebSocket mux |
| 原生载体与可信直接调用适配 | 已关闭 Peer 的 HTTP payload 优先级、异步 Context 解析前捕获身份、已关闭挂载 Connection 不回退，以及现有 Peer/Remote 事件套件 |

原生 Composition 聚焦集通过 88 项，Remote 载体/事件聚焦集通过 15 项，均无失败或跳过。首轮实现曾把处于 `Loading` 状态的新建回退 scope 错判为关闭；修正后使用候选的取消信号规则，并重跑完整聚焦集。审查还补充了事件订阅、就绪帧和每条缓冲帧发送前的检查，防止关闭后继续转发。没有绕过失败断言。

必需的整库验证使用正式基线 checkout，再从同一 Release 构建打包，供独立 S5 消费者验证：

```sh
python3 scripts/verify.py --dsh <fixed-0.2.0-checkout> \
  --upstream-test packages/boot/app-boot/tests/profile.spec.ts \
  --upstream-test packages/boot/hmr/tests/modules.spec.ts
dotnet pack Cordis.slnx -c Release --no-build --no-restore -o <package-batch>
python3 scripts/verify-remote-identity.py --packages <package-batch> --output <empty-output>
```

独立门禁仅引用 `Cordis.NET.Composition` 及其包依赖，使用全新缓存、显式本地包源映射和仓库外生成的控制台程序。它检查身份快照、owner/guest 归因、provider 资源事实、owner 调用、非 owner 拒绝和 Peer 所有的注册撤销，并禁用 serializer 反射。这些检查不代表 Windows/Linux 或 Native AOT 已执行，也不代表剩余 S6/S7 授权已完成或完全兼容 DSH Node SDK。

执行结果：最终完整门禁通过全部 33 个请求步骤，`sourceUnchanged=true`。1,093 项原生测试通过，零失败，macOS 上仍只有既有的 Windows Job-object 宿主退出排除项。正式 profile/module 原始测试通过 54 项。生成客户端、API 快照、CLI、差分 trace 和映射测试实例均通过。本轮未请求或执行原始 Core/Loader 测试源码套件。此前一次执行因客户端输出目录非空的前置条件停止；归档其证据后，从空输出目录重跑了完整门禁，没有通过跳过测试解决该前置条件。

同一 Release 批次的 9 个包通过内容检查，独立 S5 消费者的全部 15 项检查通过，既有 saved-model Remote 包消费者的全部 37 个步骤均符合预期结果。7 个打包库 DLL 与最终测试的 Release 输出逐字节一致。12 个变更 C# 文件的四个标准格式化阶段结果一致。最终门禁后仅补充本双语结果段落，再次检查文档和空白，并通过源码哈希确认没有其他变更。机器路径、原始日志和私有审计配置留在仓库外。

## 候选 Profile 与包热重装，2026-10-22

S4 遵循[限定候选例外](compatibility.zh.md#候选-profile-与包热重装分片2026-10-22)，候选为 `687f282e8625c6b8d11ff58211a90524be2e4317`，正式锁仍为 `639ed015397290b3745d163aafe02ffee4aa3f84`。执行环境为 macOS arm64、.NET SDK 10.0.111/runtime 10.0.11、Node 24.13.0。

候选原始源码证据独立于正式清单：HMR 十个文件通过 236 项，profile-resolution 通过 233 项，patch、package-reload、profile-resolution-service 通过 52 项，manager.spec 通过 108 项。共 629 项通过，源码与断言未改，无失败、无跳过。外部测试配置使用固定 checkout 和候选 pnpm 11.28.5。

原生证据覆盖 successor 删除与缓存发布、稳定目录 metadata 变化、保留目录与宿主拒绝、发布前停止、组顺序恢复、清理失败、历史 warning 与 import 修复后重试。真实 NuGet 测试覆盖新版本及完全一致的同版本重装、停用安装、激活失败后移除重试、直接和外部 Fiber 在持久化前拒绝，以及私有依赖程序集变化后保留 delegate 仍调用旧依赖。Receipt 测试比较完整准备文件树，拒绝 scaffold、依赖和资源变化。工具链解除映射后出现持久化冲突，可以重试且保留无关 manifest 编辑；小写请求保持原 NuGet 拼写。

要求的完整门禁使用正式 checkout：

```sh
python3 scripts/verify.py --dsh <fixed-0.2.0-checkout> \
  --upstream-test packages/boot/app-boot/tests/profile.spec.ts \
  --upstream-test packages/boot/hmr/tests/modules.spec.ts
```

同一 Release 包批次还使用独立 CLR deployment 消费者及既有仓库外 macOS multi-entry 适配脚本验证。适配提供分别编译的 Mach-O native library 和 macOS RID，保留原 C# 消费者断言。这是 macOS 适配证据，不是原 Windows/Linux 脚本的执行结果。本地分片不宣称 Windows/Linux runtime 或 Native AOT 验收。机器路径、原始日志与私有审计配置留在仓库外。

执行结果：最终完整门禁通过全部 33 个请求步骤，`sourceUnchanged=true`。1,047 项原生测试通过，零失败，macOS 上仍只有既有的 Windows Job-object 宿主退出排除项。正式 profile/module 原始测试通过 54 项。生成的浏览器／.NET 客户端、API 快照、CLI、差分 trace 和全部映射测试实例均通过。实际管理声明包含可空 `restartReason`，HTTP 在无需重启时明确返回 null。本轮未请求或执行原始 Core/Loader 测试源码套件。

同批包通过全部 12 个 deployment 步骤和 25 个 macOS 适配 multi-entry 步骤；7 个打包库 DLL 与本轮测试的 Release 输出逐字节一致。17 个变更 C# 文件的四个标准格式化阶段结果一致。初轮整库发现一项过时的不完整目录诊断断言，更新后要求 receipt 失败，保留所有旧代码、已保存版本和字节不变检查。下一轮原生测试全部通过，但映射仍引用旧的 local 删除测试名；现已引用实际执行的后继测试，并明确撤回与被替代历史断言完整对应的声明。两项均未通过跳过测试解决。最终门禁后仅补充本双语结果与映射的平台证据措辞，再次检查文档、重新生成的映射证据和空白；运行时、测试代码及公开 API 字节未变。

## 候选模块身份，2026-10-22

S3 使用 `687f282e8625c6b8d11ff58211a90524be2e4317` 的[限定候选例外](compatibility.zh.md#候选模块身份切片2026-10-22)，正式行为锁和历史原始测试清单不变。

- 候选原始 `packages/boot/hmr/tests/package-manifest-dispatch.spec.ts` 的 32 项测试全部通过，未修改上游源码。候选证据与正式基线分开记录。
- 旧实现已在真实 CLR 分裂夹具上复现 “Aliases of one CLR export must retain the same replacement export.” 拒绝。新增 `ClrIdentityTests` 覆盖三个已加载导入共享旧回调、别名分裂到不同导出、连续替换、禁用 Entry、direct Fiber 歧义、导入失败、激活失败、全部身份恢复和重试。
- 原生身份测试还覆盖不同插件包装对象共享回调、Entry 活跃时 deployment 改映射、禁用后启用重新解析，以及 HMR 优先使用已加载文件名。既有 CLR 测试继续覆盖 collectible 卸载、持有引用、稳定文件、raw 配置、direct Fiber 和清理／恢复失败。
- 最终 `python3 scripts/verify.py --dsh <固定-0.2.0-checkout> --upstream-test packages/boot/hmr/tests/modules.spec.ts` 门禁覆盖 Release 构建、原生测试、API 快照、生成客户端、CLI／客户端契约、conformance trace、固定基线原始测试和空白检查。既有 Windows Job Object 宿主退出测试在 macOS 按平台排除；本片没有增加 skip 或排除项。
- 库内示例与独立 CLR 消费方源码已迁移到新的 resolver 返回值与回调列表。本次本机 macOS 执行不声称覆盖 Windows/Linux NuGet 多入口原生库消费、AOT 或原始 Core/Loader 测试源码门禁。

执行结果：最终门禁 33 个请求步骤全部通过，原生测试 1,007 项通过，另有一项既有平台排除。固定正式基线的 HMR modules 原始测试 20 项通过，与候选 dispatch 的 32 项分开记录。门禁结束后 runtime、测试、示例和 API 快照字节未再改变；本段结果说明在门禁后补写。

同批 Release NuGet 包在 `osx-arm64` 通过 `verify-clr-deployment.py` 的全部 12 步，包括 framework-dependent 目录、self-contained 单文件和 ASP.NET 宿主。三个已迁移独立消费方也均以零警告、零错误编译通过。仓库外 macOS 适配版 `verify-clr-multi-entry.py` 的 25 步全部通过，保留了全部消费方断言：共享／嵌套程序集、native 依赖冲突、候选恢复、生成 Remote 客户端的真实 HTTP 调用、契约撤销、NuGet 安装和保留 worker 时的资源／发布检查。适配仅用两个独立编译的 Mach-O native probe 库及 macOS RID 替代脚本中的 Windows/Linux 系统库；C# 夹具、客户端程序和库源码均未为本次运行修改。这是 macOS 适配证据，不是原样 Windows/Linux 脚本的执行结果。

机器路径、原始日志和调查环境留在本机。公开 API 迁移已同步到双语作者指南和经审阅的快照。

## 候选管理持久化，2026-10-22

S2 依据[兼容性说明中的候选例外](compatibility.zh.md#候选管理持久化分片2026-10-22)，固定源码为 `687f282e8625c6b8d11ff58211a90524be2e4317`。正式行为锁和历史源码测试清单保持不变。执行环境为 macOS arm64、.NET SDK 10.0.111/runtime 10.0.11、Node 24.13.0。

实现前，回归用例证明恢复 bundle 默认值仍留下冗余 `disabled: false`，以及选中但不是依赖的本地 bundle 无法移除。另一个反例证明 bundle 声明消失后会丢失管理保护。修复后均通过。聚焦 `PluginManagerOriginalTests` 共 **76 项通过**，其中 `PluginManagerPersistenceTests.cs` 包含 **32 个新增实例**。覆盖默认值变化、缺失 patch、注释与 flow 语法、命名断言、额外设置、entry/key/value anchor、alias、profile 插入、继承表达式、home/启动覆盖、base-owned group、准入拒绝、外部编辑冲突及删除覆盖后的激活失败。移除检查验证不卸载、再次选中、缺失 bundle、残留贡献和管理保护。

完整命令返回 **`requested-checks-passed`**，**33 个步骤全部通过**，且 **`sourceUnchanged=true`**：

```sh
python3 scripts/verify.py --dsh <locked-source-checkout> \
  --upstream-test packages/boot/app-boot/tests/user-patches.spec.ts \
  --upstream-test packages/boot/app-boot/tests/profile.spec.ts
```

- 原生测试：**999 项通过，零失败**；Composition 591、Core 147、Extensions 118、Platform 143。唯一既有 Windows Job-object 宿主退出用例仍在 macOS 上按平台排除。没有新增排除或跳过。
- 正式基线原始测试：user-patches 与 profile 共 **55 项通过**。JIT 重复性、固定 DSH 差分轨迹、客户端构建/类型/生命周期/替换/共享模块/watch/package、CLI、API 快照、可选 Typert 消费和空白检查全部通过。
- 候选原始测试：patch **15**、package-reload **27**、完整 manager **108**；合计 **150 项通过，零失败、零跳过**。原始源码和测试未修改。外部 Vitest 4.1.11 harness 需要候选固定的 pnpm 11.28.5：首次因缺少 pnpm 失败的四项，在安装并重跑完整 manager suite 后均通过。这是聚焦源码执行证据，不代表完成上游整个 workspace 的构建。

首轮整库验证通过原生测试后，因已有客户端替换证据而拒绝覆盖。旧输出已保留，随后从空证据目录重跑完整命令。最终中英文更新通过 `scripts/check-docs.py` 和 `git diff --check`。不提交机器原始日志或依赖安装。本轮不提供 Linux/Windows、AOT、完整包消费者或原始 Core/Loader 源码 suite 证据，也不晋升行为锁。

## engines.dsh 候选准入验证，2026-10-22

第一片的合同见[兼容性说明](compatibility.zh.md#dsh-021-候选准入分片2026-10-22)。候选固定为 `687f282e8625c6b8d11ff58211a90524be2e4317`；`upstream.lock.json` 和历史源测试清单仍固定在 `639ed015397290b3745d163aafe02ffee4aa3f84`。执行环境为 macOS arm64、.NET SDK 10.0.111/runtime 10.0.11、Node 24.13.0 及锁定的 npm 开发依赖。临时目录采用文件系统规范路径，避免 macOS 符号链接别名干扰。机器原始日志不提交。

完整必需入口已返回 **`requested-checks-passed`**：

```sh
python3 scripts/verify.py --dsh <locked-source-checkout> \
  --upstream-test packages/boot/app-boot/tests/plugin-compatibility.spec.ts \
  --upstream-test packages/boot/app-boot/tests/compatibility-preflight.spec.ts \
  --upstream-test packages/boot/app-boot/tests/profile-compatibility.spec.ts
```

- 原生测试：**967 通过、零失败**，仅原有 Windows Job 对象宿主退出测试在 macOS 未执行（Core 147、Composition 559、Extensions 118、Platform 143 通过）。test-map 门禁严格核对该单项排除，并要求跨平台进程用例实际通过。原来仅 Windows 执行的 SDK 退出用例现在也在 macOS/Linux 执行；没有把失败用例改为跳过。
- 65 个 `DshProfilePolicyTests` 全部通过，包含 engine 优先级、fallback、错误声明、精确身份、bundle／入口／reconciliation 准入及豁免锁并发／取消。实现前首批 13 个新兼容性用例中 12 个失败。13 个真实 NuGet 定向用例也通过，涵盖禁用安装在 SDK 执行前准入、HTTP/CLI 豁免、元数据保留、重启／撤销及兼容 engines 覆盖 malformed peers。
- 候选 `plugin-compatibility.spec.ts` 的 87 个原始用例另行通过。上面三个正式旧锁源文件的 117 个原始用例通过。这是参考侧执行，不代表候选全部行为已经兼容。
- restore／Release build、test-map、重复 JIT trace、示例、正式基线的两组差分、浏览器模块构建及生成客户端类型消费、生命周期／替换／共享模块、作者 watch／package、CLI 校验／预览／调用／错误退出、API 快照、可选 Typert 消费及 whitespace 均在必需入口运行中通过。该运行期间源码清单保持不变。最后更新的双语证据随后通过 `scripts/check-docs.py`。

首次整库运行暴露的两个 macOS 失败现已修复：

1. `PackageProcessTests.Cancelled_preparation_stops_child_with_independent_output_pipes_before_returning` 缺少 macOS fixture：setup 提前返回，没有创建 feed。未修改基线 `18b611c4cdc6972480e396702106484701118e81` 同样复现。fixture 现在会在 macOS 构建真实包和使用独立输出管道的写入进程。
2. `PackageManagementTests.Cancelling_running_sdk_preparation_drains_before_wait_completes_and_does_not_select` 暴露进程所有权不完整：旧 macOS 路径终止遍历到的树，但只等待主进程；.NET 明确说明[等待主进程退出不代表后代已退出](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill?view=net-10.0)。随后构造的稳定反例显示 `Exited_sdk_cannot_report_prepared_output_while_its_child_still_writes` 在写入进程仍存活时返回准备成功。该用例在进程管理修复前失败、修复后通过。原生组创建、组存活检查、有界终止等待及直属子进程回收，现已阻止这两类过早完成。

进程定向运行通过 9 项；原先的取消回归又在与整库并发的负载下连续通过 5 次。最终整库还通过新增的活动所有者／恢复记录用例，以及正常／非零退出和输出检查。无法确认终止时仍保留恢复记录，不返回已完成的取消。macOS 宿主崩溃和主动逃离进程组的边界继续明确记录在[兼容性说明](compatibility.zh.md)。

本轮不提供 Linux/Windows、AOT、完整包消费者或原始 Core/Loader 源测试套件证据，也不晋升候选行为锁。

## 选择检查

文档运行 `python scripts/check-docs.py`；代码先运行职责对应的测试。运行时、包、兼容性和发行改动运行 `python scripts/verify.py`。具体命令见[维护指南](development.zh.md)。保留 CI 必需检查。

## 已发行版本证据

[alpha.5.2 发行页](https://github.com/gentt1024/Cordis.NET/releases/tag/v0.2.0-alpha.5.2)提供正式 manifest、evidence 和 SHA256SUMS。tag 对应 `1ed99e39c44c8f7ecbd064b365b8ebb31530f3c4`，[正式运行](https://github.com/gentt1024/Cordis.NET/actions/runs/39145687915)七项作业全部通过。八个产品包及匹配符号来自 Linux 验证批次，Example 排除。公开 NuGet 载荷与封存一致，允许新增 NuGet 签名；发行附件已公共下载核对摘要。

## 已接受风险与证据边界

Jint 冷构造和原生初始化可能延迟 HMR 刷新、队列后续工作及退出清理。历史超时仍是失败，采样没有解释全部延迟或建立上界。十秒回归保持不变，新失败仍使门禁失败。缺少进展、错误发布或清理，或真实冷启动延迟不可接受时，应据具体证据跟进。

Windows／Ubuntu 有完整包与 AOT 验证；macOS 仅有同批作者及浏览器消费，不代表完整运行时、应用或 AOT 验收。Rider 验收不覆盖其他 IDE。Windows 文档打开只证明系统交接，不证明编辑器内容或其他桌面平台。后端限制和具名 UI 接入待验证项见[维护指南](development.zh.md)。作者目录保护不承诺恶意文件系统沙箱。

## 历史证据

[原验证记录](https://github.com/gentt1024/Cordis.NET/blob/1ed99e39c44c8f7ecbd064b365b8ebb31530f3c4/docs/validation.zh.md)保留各候选原始身份、失败与当时待办。[verification](../verification)保留受跟踪回执及机器读取的输入。Git 不保存未跟踪包、TRX、截图或已过期 CI artifact；hash 不证明原始字节仍可获取。

PR 合并检出与分支 head 分开记录。本地补完不能把完整门禁失败改成成功，不同候选不能拼成一次验收。
