# 验证

[English](validation.md)

## 候选 S10 同批门禁，晋升前验证

S10 验证汇合后的 S1–S9 实现，不修改 `upstream.lock.json` 或历史 `docs/upstream-tests.json`。正式基线仍为 `639ed015397290b3745d163aafe02ffee4aa3f84`；候选证据独立固定到派生提交 `fba488001a6c2d37787d800beeab54a770c826a1`，其上游基础为 `687f282e8625c6b8d11ff58211a90524be2e4317`。本验证片不引入新的应用公开 API。

使用 `python scripts/candidate_source.py --output ../dsh-candidate` 准备新的候选 checkout。命令获取固定上游父提交，并按 [candidate-adaptation.json](../reference/candidate-adaptation.json) 重建经过审查的提交。可选 `--source` 指向已有本地上游仓库，仍按同一固定提交 fetch，不编辑该仓库；输出目录已存在时拒绝执行。CI 的运行时 traces、发现及所有分片共用这一准备入口。派生提交只改变一个实现文件，原始测试和平台跳过规则与上游父提交逐字节一致。候选收据保留适配记录，拒绝混用来源身份。

在一个冻结的分支提交上运行现有 `verify` 工作流，并启用 dispatch 输入 `candidate`。Windows x64 和 Ubuntu x64 作业保留正式源码门禁，执行 JIT、Native AOT、包检查、独立作者消费、生成客户端和 CLR 部署。候选发现、分片执行及并集验证是额外必需检查。各分片必须使用相同源码身份、发现的用例及重复次数、workflow run 和 attempt；因分片筛选未运行的用例不计为执行。

某个候选套件失败后，其他独立套件仍继续运行。失败套件保留报告和日志，分片仍判失败。超时会保留已捕获的输出并停止分片，因为尚未确认后代进程已清理。源码身份变化也会立即停止执行。各平台分别校验源码并集；最终必需检查仍要求所有 runtime、源码和可移植消费者门禁通过。

候选范围包含 app-boot、plugin-manager 和 HMR 的全部测试文件，仅排除两份明确入账的 DSH 官方产品测试，另执行声明的 Remote、generator、浏览器和 Core/Loader suites。上游禁用的文档 catalog 仍记为 source-skipped。原始源码包含三个仅在 Windows 排除的 POSIX 后代进程夹具及一个文件系统能力排除；门禁只在适用平台接受这些精确身份，绝不计作通过。未进入执行范围的历史记录保留为范围未审查，除非已有明确的逐项排除决定。

Linux 作业生产唯一的包／runtime／作者工具批次。Windows、Ubuntu 和 macOS 消费者在运行前后检查生产者验证收据、源码摘要及制品摘要。可移植调用者和真实浏览器消费这些确切字节。Windows 和 Ubuntu 另执行静态 AOT 调用者与动态 CLR 部署，两者是不同合同。包消费者使用独立 NuGet 缓存、来源映射及相对所选批次的归档摘要检查。主消费者、Remote 元数据和原生浏览器消费者共用准备和核验函数，隔离父目录构建文件、清空 fallback 缓存，并检查实际还原的 assets 后才声明仅消费包。macOS 作者消费不代表完整 macOS runtime 或 AOT 支持。

文件名仅有大小写差异的源码回归要求 `CaseParts.cs` 和 `caseparts.cs` 是两个独立文件。macOS portable 作业提供区分大小写的 APFS 临时卷，并在收集证据后卸载。卸载前，作业使用消费者的 `TMPDIR` 正常关闭 SDK 构建服务器。[清理诊断](https://github.com/gentt1024/Cordis.NET/actions/runs/39934368446)确认 Roslyn 在该卷上保留了共享内存文件、锁和诊断套接字。消费者在写第二个文件之前拒绝路径别名，不跳过原有提取与条件编译断言。本地 macOS 执行完整 .NET 作者消费时也需要区分大小写的 `TMPDIR`。

基线晋升要求所有必需矩阵结果通过、候选源码／测试清单和原生映射完成审查、有序差分 traces 一致、API 迁移说明与中英文文档一致，并且包／源码归档保留准确来源和 notices。缺少平台、结果未知和意外跳过都不算通过。仅源码检查不能满足原生行为断言。旧收据保留原有身份。只有这些条件成立，才应以另一个经过审查的改动同步更新锁、reference 断言、源码片段、候选映射及兼容性声明，再对晋升后的提交执行完整验证。

## 候选浏览器安全与作者增量，2026-10-23

S9 的正式行为锁仍为 `639ed015397290b3745d163aafe02ffee4aa3f84`；历史清单保留原有 `ddefc45fbc7f8e46dd73185e68295696d1297887` 身份和处置记录。限定范围的源码来源记录候选 `687f282e8625c6b8d11ff58211a90524be2e4317`，并单独记录通知重入的本地修正。未修改的候选原始用例共通过 111 项，零失败、零跳过：浏览器 loader 72、entries 35、CSS／图片作者构建 4。这些结果来自外部源码执行环境，不代表完整候选工作区构建或类型检查通过。

必跑的 `client-release` 门禁通过公开包装器覆盖：两个版本字段、缺省身份、先准入后解析图、dismiss、retry、已接受的待处理更新、销毁、可观察对象生命周期及同步订阅者重入。管理重连测试使用真实 Connection／events 代码和套接字帧夹具，工厂脚本通过真实回环 HTTP 加载。`ManagementTransportTests` 另通过原生 Kestrel 图端点验证版本传递及缺省字段省略。这些是分开的检查，不宣称已完成原生管理 WebSocket 与浏览器整套部署验证。

真实 Chromium 检查通过公开 boot／sync 和 HTTP 提供的图、脚本，验证外壳样式保留、嵌套归属、工厂抛错清理及版本迁移。它检查 DOM 节点身份和计算后的 CSS，并证明 dismiss 后也不下载异版本工厂。独立安装的作者归档验证 SVG／PNG／WebP 精确 data URL、真实图片解码、仅图片变化触发 watch 发布，以及旧代目录不可变。输入／输出碰撞检查继续生效。外部严格 TypeScript 消费验证 Slots inline／keyed-member 声明和公开 release 类型。

```sh
python3 scripts/verify.py --dsh <fixed-0.2.0-checkout> \
  --upstream-test packages/boot/app-boot/tests/profile.spec.ts \
  --upstream-test packages/boot/hmr/tests/modules.spec.ts
node scripts/verify-client-safety.mjs artifacts/client-modules <empty-browser-output>
CORDIS_CLIENT_TOOLS_TGZ=<archive-from-the-full-gate> node scripts/verify-client-author-package.mjs \
  artifacts/client-modules <empty-author-browser-output> --headless
dotnet pack Cordis.slnx -c Release --no-build --no-restore -o <package-batch>
```

同批 portable authoring 运行器也强制执行浏览器安全检查。Chromium 不在运行器已知安装路径时设置 `CORDIS_TEST_BROWSER`。浏览器安全运行器也支持 `--serve` 交互执行。本地 macOS 证据不代表 Windows／Linux 或 Native AOT 发布准入。继续保留单 factory 发布和应用提供 renderer 的边界，不进行 S10 晋升。原始日志和机器专属证据保留在仓库之外。

## 候选 Remote 元数据，2026-10-23

S8 保持正式行为锁为 `639ed015397290b3745d163aafe02ffee4aa3f84`，对候选 `687f282e8625c6b8d11ff58211a90524be2e4317` 使用限定范围例外。未修改的候选原始测试实际通过 56 项，无失败或跳过：RemoteInfo 8 项、懒加载器 1 项、生成器元数据 29 项、完整 loader 套件 18 项。仓外源码模式工具使用 Vitest 4.1.11、TypeScript 6.0.3 和 Vite 8.2.2；这不代表完整上游工作区构建或类型检查通过。

原生测试覆盖索引、parser 与投影的独立懒加载、normal/strict 视图、快照身份与描述符复制、缺省与 null、命名调用及可选工具投影失败。保存模型的独立消费者在没有作者程序集时使用生成元数据，在递归 DTO 投影被拒绝后继续解析，并通过真实 HTTP 和双向流执行命名调用。生成 TypeScript 检查覆盖元数据身份、输入策略、可选投影及返回句柄保留。既有声明、输出 codec 与作者错误拒绝检查继续保留。

运行完整固定基线门禁，并验证同一批新构建的包：

```sh
python3 scripts/verify.py --dsh <fixed-0.2.0-checkout> \
  --upstream-test packages/boot/app-boot/tests/profile.spec.ts \
  --upstream-test packages/boot/hmr/tests/modules.spec.ts
dotnet pack Cordis.slnx -c Release --no-build --no-restore -o <package-batch>
python3 scripts/verify-typert-dotnet.py --packages <package-batch> --output <empty-saved-output> --saved-remote-only
python3 scripts/verify-typert-dotnet.py --packages <package-batch> --output <empty-web-output> --web-contracts-only
python3 scripts/verify-typert.py --packages <package-batch> --output <empty-author-output>
python3 scripts/verify-remote-metadata.py --packages <package-batch> --output <empty-metadata-output>
```

本地候选检查不构成 Windows/Linux 或 Native AOT 发布批准。这些仍属于正式基线晋升前的同批平台矩阵。原始日志和本机路径保留在仓库之外。


## 候选事件与访问客户端，2026-10-23

S7 使用固定候选 `687f282e8625c6b8d11ff58211a90524be2e4317` 的范围例外；正式锁仍为 `639ed015397290b3745d163aafe02ffee4aa3f84`。未修改的候选原始测试通过 67 项，失败／跳过均为零：Gateway access 30、RemoteAccess Host 21、ClientAccess 10、OperatorConnect 6。外部源码模式 harness 使用 Vitest 4.1.11、TypeScript 6.0.3、Vite 8.2.2。首次运行通过 61 项，但因缺少 `compression` 未能收集 OperatorConnect；在两个 checkout 之外补齐候选锁定依赖后，完整重跑通过 67 项。不跨运行累加数量。这不是完整上游 workspace 构建，也不是原生测试结果。

| S7 合同 | 原生证据 |
|---|---|
| 整条通知资源授权、reader 失败、owner 绕过、passive 拒绝 | `RemoteEventsGatewayTests` |
| 同 Peer 回答归属、非 owner 观察、跨代际保留待处理 waterfall | `RemoteEventsGatewayTests` |
| Peer／事件源关闭丢弃缓冲、sibling 隔离 | `TypertIdentityTests`、`RemoteEventsGatewayTests` |
| 快照顺序、失效合并、完整 principal 定向失效、策略／服务撤回 | `TypertAccessSnapshotTests` |
| 原生客户端载体代际、终止性失败、仅包引用消费者 | `scripts/verify-access-client.py` |
| 安装后 TypeScript 声明／运行时、稳定 observable、非法帧、重连、旧代际隔离与全新 remount | `scripts/verify-client-access.mjs`，已纳入 `verify.py` 必跑步骤 |

对正式 checkout 运行整库门禁，再用生成的包批次运行独立 NuGet 消费者：

```sh
python3 scripts/verify.py --dsh <fixed-0.2.0-checkout> \
  --upstream-test packages/boot/app-boot/tests/profile.spec.ts \
  --upstream-test packages/boot/hmr/tests/modules.spec.ts
dotnet pack Cordis.slnx -c Release --no-build --no-restore -o <package-batch>
python3 scripts/verify-access-client.py --packages <package-batch> --output <empty-access-output>
python3 scripts/verify-remote-access.py --packages <package-batch> --output <empty-policy-output>
```

浏览器检查在 checkout 外使用构建后的包及真实生成的 namespace／Connection API。.NET 消费者使用全新映射包缓存与真实 loopback 传输。remount 检查是原生验收证据，候选 ClientAccess 原始套件自身没有 remount 用例。这些本地检查不代表 Windows／Linux、Native AOT、完整 Node SDK 互通或 S8 投影兼容。原始日志、本机路径和 harness 配置保留在仓库外。

`ExecutionContextTests.Concurrent_entries_keep_waking_the_context_and_remain_serial` 通过一个或四个生产者反复调用公开的 `Context.RunAsync`，验证回调串行执行，并限制任务完成与清理的等待时间。该测试在内存顺序修复前复现了原生丢失唤醒；CLR 测试停滞时的转储也独立证实，清理回调已入队但没有待执行的调度。


## 候选资源决策与调用准备，2026-10-23

S6 使用固定候选 `687f282e8625c6b8d11ff58211a90524be2e4317` 的[范围明确例外](compatibility.zh.md#候选资源决策与调用准备2026-10-23)，正式行为锁仍为 `639ed015397290b3745d163aafe02ffee4aa3f84`；历史清单保留原有 `ddefc45fbc7f8e46dd73185e68295696d1297887` 身份和处置记录。本地环境为 macOS arm64、.NET SDK 10.0.111/runtime 10.0.11、Node 24.13.0。

未修改的候选原始测试通过 106 项，无失败或跳过：protocol remote-access 8 项、gateway-access 30 项、gateway host 47 项、RemoteAccess host 21 项。外部精简测试环境使用候选选定的 Vitest 4.1.11、TypeScript 6.0.3 和 Vite 8.2.2，不代表完整候选 workspace 构建或类型检查。部分原始用例覆盖 S7 事件／快照，其通过只提供参考端证据，不代表原生已实现这些后续合同。

| S6 合同 | 原生检查 |
|---|---|
| Binding 备选、资源交集、condition/explanation 优先级、策略失败与 owner | `TypertRemoteAccessTests`，以及非法原生输出和诊断抑制／重置用例 |
| 授权前没有 Context/lookup 副作用，普通 JSON 错误优先级 | `TypertGatewayAccessTests` 与现有仅 lookup 的 `TypertGatewayPreparationTests` |
| Reader/provider/default 优先级、首对象回退、重复 ID、非法和缺省资源 | `TypertGatewayAccessTests`、独立 NuGet 消费者 |
| 业务复用解码视图，默认值、只写和不透明／多态对象、可选／null 与取消 | `TypertGatewayAccessTests`，计数 codec 明确禁止序列化 |
| 动态列表过滤与继续关闭 S7 事件 | 真实 Gateway unary/stream 测试和独立 NuGet 消费者 |
| wire 无法选择身份，授权和撤销早于 provider 调用 | `RemoteCarrierTests.Carrier_identity_and_access_are_host_selected_before_lookup`，真实 HTTP 和 WebSocket mux |

必需的完整门禁使用正式 checkout，并在选择空输出目录前归档旧证据。独立 S6 消费者是单独命令，不是 `verify.py` 隐含步骤：

```sh
python3 scripts/verify.py --dsh <fixed-0.2.0-checkout> \
  --upstream-test packages/boot/app-boot/tests/profile.spec.ts \
  --upstream-test packages/boot/hmr/tests/modules.spec.ts
dotnet pack Cordis.slnx -c Release --no-build --no-restore -o <package-batch>
python3 scripts/verify-remote-access.py --packages <package-batch> --output <empty-output>
```

消费者使用仓库外生成的控制台、全新缓存和显式本地包源映射，仅直接依赖 `Cordis.NET.Composition`，并禁用 serializer 反射。本地证据不代表 Windows/Linux、Native AOT、完整 DSH Node SDK 互操作或剩余 S7 闭包已验收。机器路径、原始日志和私有审计配置留在仓库外。

执行结果：完整门禁全部 33 个请求步骤通过，`sourceUnchanged=true`。原生测试通过 1,153 项，零失败，macOS 上只有既有的 Windows Job-object 宿主退出排除项。正式 profile/module 原始测试通过 54 项。API、生成客户端、CLI、差分 trace 和映射测试实例均通过；本轮未请求原始 Core/Loader 源码套件。Composition 聚焦集通过 117 项，真实载体／事件集通过 16 项，均无失败或跳过。

9 个包通过内容检查，7 个打包库 DLL 与最终测试的 Release 输出逐字节一致。独立 S6 消费者的 37 项检查全部通过，既有 saved-model Remote 消费者的 37 个步骤全部符合预期。11 个变更 C# 文件通过四个标准格式化阶段。审查修正了不透明枚举对象、多态对象和可选顶层 ID 的隐式提取，回归测试证明拒绝路径和显式 reader 恢复路径均正确。

S5 消费者首轮发现一项旧错误优先级断言：未声明的 `principal` 参数现在先被精确字段验证拒绝。后继检查明确断言该拒绝，再单独检查合法 guest 参数的权限拒绝，两条路径均保留业务调用次数为零的断言。合入主工作区的脚本随后通过全部 16 项检查。完整门禁后仅修改这一独立验证过的消费者脚本与本双语结果段落，并重查文档、空白和源码哈希；运行时、测试和公开 API 字节不变。没有通过跳过或删除失败测试完成验证。

## 候选 Remote 身份与资源，2026-10-23

S5 遵循[范围明确的候选例外](compatibility.zh.md#候选-remote-身份与资源分片2026-10-23)。候选源码仍为 `687f282e8625c6b8d11ff58211a90524be2e4317`；正式行为锁仍为 `639ed015397290b3745d163aafe02ffee4aa3f84`；历史清单保留原有 `ddefc45fbc7f8e46dd73185e68295696d1297887` 身份和处置记录。本轮环境为 macOS arm64、.NET SDK 10.0.111/runtime 10.0.11、Node 24.13.0。

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

第一片的合同见[兼容性说明](compatibility.zh.md#dsh-021-候选准入分片2026-10-22)。候选固定为 `687f282e8625c6b8d11ff58211a90524be2e4317`；`upstream.lock.json` 仍固定在 `639ed015397290b3745d163aafe02ffee4aa3f84`，历史源测试清单保留原有 `ddefc45fbc7f8e46dd73185e68295696d1297887` 身份和处置记录。执行环境为 macOS arm64、.NET SDK 10.0.111/runtime 10.0.11、Node 24.13.0 及锁定的 npm 开发依赖。临时目录采用文件系统规范路径，避免 macOS 符号链接别名干扰。机器原始日志不提交。

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
