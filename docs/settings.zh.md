# Settings 指南

[English](settings.md)

Settings 通过现有配置权威提供 live 配置表单。使用数值 revision；只有不要求比较先前表单版本时才省略 `expectedRevision`。

## 宿主与消费者用法

将普通 `SettingsForms` 服务挂在运行中会话既有的 `ConfigurationOperations` 之上。可选 `SettingsPolicy` 限制宿主可见范围；不提供政策时，开放已声明且受支持的 live 字段。独立于可选 provider 发布标准 controller，并注册其生成贡献：

```csharp
var registry = new TypertRegistry(session.Context);
var contribution = SettingsControllerTypert.Contribution("Application.Settings");
TypertHostConnection connection = null!;
await session.Context.RunAsync(ctx =>
{
    new SettingsForms(ctx, session.ConfigurationOperations);
    new SettingsController(ctx);
    registry.Register(ctx, contribution);
    connection = new(ctx, new TypertGateway(ctx, registry));
    return Task.CompletedTask;
});
app.UseWebSockets();
app.MapCordisRemote("/remote", connection, authorize);
var clientArtifact = TypertArtifacts.GenerateClient(contribution);
```

宿主提供 `authorize`，并一起交付生成模块与声明。普通服务可直接在进程内使用，无须 Remote。Remote 每次调用重新解析名为 `settings` 的当前 `ISettingsProvider`；缺失时以 `gateway/internal` 拒绝。仅依赖模型的 .NET 客户端构建见[作者指南](authoring.zh.md#原生作者模型与-net-消费者2026-10-10)。

```csharp
using var http = new HttpClient();
await using var connection = new TypertConnection(http, new Uri("http://localhost:5000/remote"));
await using var remote = new SettingsClient(connection);
var describe = await remote.DescribeAsync();
if (!describe.Ok) throw describe.Error;
var form = describe.Value.Namespaces.Single(row => row.Ns == "worker");
using var document = JsonDocument.Parse("{\"limit\":30}");
var updated = await remote.UpdateAsync(form.Ns, document.RootElement, form.Revision);
```

```ts
import { createRemote, TypertConnection } from './settings.mjs'

const connection = new TypertConnection(new URL('/remote', location.href))
const remote = createRemote(connection)
const read = await remote['settings/describe']({})
if (!read.ok) throw new Error(read.error.message)
const form = read.value.namespaces.find(row => row.ns === 'worker')!
const write = await remote['settings/mutate']({
  ns: form.ns,
  ops: [{ op: 'set', path: ['limit'], value: 30 }],
  expectedRevision: form.revision,
})
await remote.dispose()
await connection.dispose()
```

正常编辑提交先前读到的 revision。三种 Remote 写操作均返回新的脱敏 namespace。原生 provider 写入返回 Task；controller 随后 describe 同一个已捕获的 provider。空 `ns` 在查找 provider 前以 `gateway/bad-request` 拒绝，不受可选 provider 是否存在影响。`update` 递归合并对象，数组整体替换。`replace` 将开放的 live 字段重置到继承值，再合并所提交 section，保留普通配置。由脱敏读取拼成的 replace 可能重置省略的 secret。持有不完整视图时使用 `mutate`：按序执行的对象 unset 恢复继承，数组 unset 删除该元素。set 必须提供 `value`，允许显式 JSON null；unset 省略 `value`。

表单 Schema 保留脱敏后的公开默认值，移除 secret 默认值和 required 标记，省略宿主 hidden 字段。非 live 对象路径使用默认值为空对象的新包装；进入 live 边界后保留作者元数据。

## Revision 与提交语义

.NET 使用 `double` 承载数值，对应 JavaScript number。revision 从零开始，由一个 `SettingsForms` owner 持有；它观察 Entry 的 Fiber 身份、完整 Config 声明及原始 Entry 配置。仅改变页面呈现的 `Configure` 会发送 `settings/document-updated`，revision 不变。观察到消失时增加一次，再次出现会继续推进观察版本。新 owner 从新的观察开始。revision 不持久化，不从内容 hash 转换，也不承诺跨进程永久单调。

提供过期 expected revision 时，写入在持久化前以 `settings/conflict` 拒绝，详情为数值 `{ns, expected, actual}`。省略它只取消先前表单版本比较，仍执行宿主授权、开放路径政策、完整候选校验、profile 队列及捕获源的一致性检查。生成客户端在可选 C# 参数取默认值时省略 wire 字段；显式 JSON null 是另一种输入，会被拒绝。不要将数值 revision 转成字符串、静默丢弃过期参数，或用 null 代替省略。

既有完整配置／文件 hash 机制继续承担独立的源编辑职责。`ReadConfigurationAsync`、`MutateConfigurationAsync`、`ReadConfigurationSchemasAsync` 与 profile／包操作仍遵循各自的 hash 合同。它们与 Settings 复用既有 profile 权威和写入流程。完整配置 hash 不能充当 Settings 表单 revision。
