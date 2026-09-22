# 开发与验证

[English](development.md)

## 快速循环

源码构建的包版本统一定义在 [Directory.Build.props](../Directory.Build.props)。项目继承该值，包验证脚本也读取它来构建隔离消费者。需要精确的本地包版本时，查询同一个值：

```console
dotnet msbuild src/Cordis.Core/Cordis.Core.csproj -getProperty:Version
```

普通提交不需要提升包版本。源码版本号不代表 NuGet 上已存在对应实现；已发布包请按[安装指南](getting-started.zh.md)使用，尚未发布的 API 请按[作者指南](authoring.zh.md)从源码运行。

```console
dotnet restore Cordis.slnx --locked-mode
dotnet build Cordis.slnx -c Release --no-restore
dotnet test Cordis.slnx -c Release --no-build
python scripts/check-docs.py
```

## 完整门禁

```console
python scripts/verify.py
```

完整门禁检查锁定的 SDK 与依赖、分析器、测试、上游源码执行、差分场景、Native AOT、API 形状、包和隔离消费者。发布候选应在 Windows x64 与真实 Linux x64 上运行。最近完成的证据见[验证记录](validation.zh.md)，证据含义见[兼容性文档](compatibility.zh.md)。

生成输出和原始日志可能包含本地路径或用户名。应将其保存在公开树之外；只提交脱敏摘要与稳定的机器可读证据。
