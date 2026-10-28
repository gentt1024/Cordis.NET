# 读取与编辑实时 Settings

[English](settings.md) | 中文

Settings 通过运行中 Profile 的现有写入者提供选定实时配置。读取当前表单，编辑其提供的路径，提交读取到的数值 revision。完整原始配置使用独立权限与哈希合同。

## 准备 Host 与表单

在 .NET 10 ASP.NET Core Host 中使用匹配的 Composition 与 AspNetCore 包。作者通过 Config Schema 声明实时字段，运行中的 Entry 须提供要编辑的表单，见[配置描述](configuration-description.zh.md)。

下方是 Host 与消费者接线片段，不是一个合并程序。消费者假定已有 `worker` 表单及其实时 `limit` 字段；[ManagedApplication](../examples/ManagedApplication/README.zh.md)提供完整示例，使用的 namespace 是 `example.managedplugin`。

## 挂载提供者与控制器

<a id="宿主与消费者用法"></a>

使用运行会话的 `ConfigurationOperations`，`app`、`session` 和授权回调由 Host 提供。在现有 Context 执行域内创建 Connection：

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
    ctx.Provide("connection", connection);
    return Task.CompletedTask;
});
app.UseWebSockets();
app.MapCordisRemote("/remote", connection, authorize);
var clientArtifact = TypertArtifacts.GenerateClient(contribution);
```

可选 SettingsPolicy 限制可见性，未提供策略时展示支持的已声明实时字段。控制器可独立于提供者注册，但当前没有 `settings` 提供者时，调用以 `gateway/internal` 失败。每次调用解析当前提供者；普通进程内 Settings 不需要 Remote。

将 `clientArtifact.Module` 与声明以匹配的模块文件名一同交付，下方 TS 假定文件名为 `settings.mjs`。Host 管理认证、授权与退出；先等待 Connection 释放并撤销其注册，再释放所属 Context。客户端借用这些资源。

## 从 C# 读取与写入

按[客户端项目配置](authoring.zh.md#生成独立-net-客户端)，使用 Composition 保存模型，以 service `settingsController`、客户端名 `SettingsClient`、namespace `Example.Settings` 生成独立客户端。本例 Host 地址为 `http://localhost:5000`，应换成实际映射地址，并按需通过 HttpClient 提供授权：

```csharp
using Cordis.Composition;
using Example.Settings;
using System.Text.Json;

const string ns = "worker";
using var http = new HttpClient();
await using var connection = new TypertConnection(http, new Uri("http://localhost:5000/remote"));
await using var remote = new SettingsClient(connection);
var describe = await remote.DescribeAsync();
if (!describe.Ok) throw describe.Error;
var form = describe.Value.Namespaces?.SingleOrDefault(row => row.Ns == ns)
    ?? throw new InvalidOperationException("The Host does not offer the worker form.");
using var document = JsonDocument.Parse("""{"limit":30}""");
var updated = await remote.UpdateAsync(ns, document.RootElement, form.Revision);
if (!updated.Ok) throw updated.Error;
Console.WriteLine(updated.Value.Revision);
```

读取两次调用的成功值前均检查 `Ok`。写入返回新的脱敏 namespace，`await using` 顺序保证先释放客户端，再释放它借用的 Connection 和 HttpClient。没有表单表示 Host 未提供该 namespace，不应用无关 Entry 或 revision 替代。

## 从 TypeScript 读取与修改

导入生成模块并连接实际 Remote 地址；这里假定 Host 与页面同源：

```ts
import { createRemote, TypertConnection } from './settings.mjs'

const connection = new TypertConnection(new URL('/remote', location.href))
const remote = createRemote(connection)
const ns = 'worker'
try {
  const read = await remote['settings/describe']({})
  if (!read.ok) throw new Error(read.error.message)
  const form = read.value.namespaces?.find(row => row.ns === ns)
  if (!form) throw new Error('The Host does not offer the worker form')
  const write = await remote['settings/mutate']({
    ns,
    ops: [{ op: 'set', path: ['limit'], value: 30 }],
    expectedRevision: form.revision,
  })
  if (!write.ok) throw new Error(write.error.message)
  console.log(write.value.revision)
} finally {
  await remote.dispose()
  await connection.dispose()
}
```

生成输出允许省略字段，先检查可选的 namespaces 集合，再向应用选定的 namespace 写入。分别处理读取与写入失败。遇到 `settings/conflict` 时重新读取，由应用决定如何处理用户草稿，不自动重试同一编辑。原生提供者写入返回 Task，Remote 控制器完成后描述同一捕获的提供者。

## 选择编辑操作

- `update` 递归合并对象，替换数组。
- `replace` 按继承重置提供的实时字段，再合并提交部分，保留普通配置。从脱敏读取构造 replace 可能重置被省略的秘密。
- `mutate` 应用有序路径。对象 unset 恢复继承，数组 unset 移除元素。set 包含 `value`，可为显式 JSON null；unset 省略 `value`。

不完整视图使用 mutate。空 namespace 在查找提供者前以 `gateway/bad-request` 拒绝。读取保留公开默认值，省去隐藏属性，移除秘密默认值与 required 标记；秘密另行提供存在状态，不返回值。非实时对象路径是新的包装对象，实时边界上的 metadata 保留。

## Revision 与提交语义

.NET 数值载体为 double，对应 JavaScript number。每个 SettingsForms 所有者的 revision 从零开始，观察 Entry Fiber 身份、完整 Config 声明和原始配置。仅展示 Configure 变化以相同 revision 发出 `settings/document-updated`；观察到消失时增加一次，重新出现时再次推进，新所有者开始新的观察。revision 不是持久内容哈希或永久跨进程计数器。

陈旧 expected revision 在持久化前以 `settings/conflict` 和数值 `{ns, expected, actual}` 拒绝。省略只跳过旧表单比较，授权、提供路径、完整候选校验、Profile 队列和捕获源码一致性仍生效。省略可选参数才会在 wire 省略；显式 JSON null 不同且被拒绝，不要将 revision 字符串化。

完整配置 API 保留自身文件／源码哈希合同，共用同一 Profile 写入者；完整配置哈希不是 Settings 表单 revision。[文档打开](authoring.zh.md#打开-settings-文档)是另行授权的 Host 操作，打开 Host 文档，不是浏览器下载。
