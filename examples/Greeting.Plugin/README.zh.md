# Cordis.Example.Greeting

[English](https://github.com/gentt1024/Cordis.NET/blob/main/examples/Greeting.Plugin/README.md) | 中文

这个源码示例提供字符串类型的 `greeting` 服务与嵌入式配置 patch，展示应用如何注册静态模块并读取已部署资源。

## 运行示例

使用仓库 `global.json` 固定的 SDK，在仓库根目录运行：

```console
dotnet run --project examples/Composition/Composition.csproj
```

应用引用 Greeting.Plugin、注册 `GreetingModule.Plugin`、读取嵌入 patch，并更新配置。`ReadPatch()` 打开已部署程序集中的资源，运行时不查找源码目录。

## 参考其中的写法

在自己的应用中使用该模块与资源模式，并选择自己的服务名与配置规则。这个教学包参与仓库验证，但不进入公开发行，不属于八个已发布的 `Cordis.NET.*` 产品包。

Cordis.NET 独立维护，不是 DeepSeek、Cordiverse 或 Microsoft 的官方项目。
