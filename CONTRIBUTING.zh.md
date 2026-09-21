# 为 Cordis.NET 贡献

[English](CONTRIBUTING.md)

欢迎贡献代码、文档、翻译、兼容性反例和断言审阅结果。

## 修改代码之前

请阅读[开发文档](docs/development.zh.md)、[兼容性文档](docs/compatibility.zh.md)及相关组件映射。小而集中的修复不需要 RFC。公共 API 变更或有意偏离锁定行为的改动，应在实现前讨论。

## 验证改动

```console
dotnet restore Cordis.slnx --locked-mode
dotnet build Cordis.slnx -c Release --no-restore
dotnet test Cordis.slnx -c Release --no-build
python scripts/check-docs.py
```

运行时、打包、兼容性或发布改动应运行 `python scripts/verify.py`。纯文档改动只需运行文档检查并进行针对性审阅，不要求执行全部 Windows、Linux 或 AOT 门禁。

承诺、命令或代码示例变化时，请同步英文与中文配对文档。引入第三方代码时必须保留许可证与来源。不要提交凭据、个人路径、原始本地日志或未公开的漏洞细节。

拉取请求应说明行为变化、测试、双语文档影响和许可证影响。提交贡献即表示同意按仓库 MIT 许可证提供该贡献。
