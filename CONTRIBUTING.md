# 参与贡献（CONTRIBUTING）

感谢你愿意帮助 EncoDeck！

## 兼容性报告（最有价值）

1. 按 [docs/TESTING.md](docs/TESTING.md) 运行 `EncoDeck.Probe devices / services / query --report report.json`。
2. 新建 issue，附上：型号、产品 ID、固件版本、Windows 版本、蓝牙适配器、Probe 输出和 `report.json`。
3. **请勿**在 issue 中粘贴未脱敏的完整蓝牙地址；Probe 默认已脱敏，`--show-sensitive` 的输出不要公开。

## 协议贡献

- 每条新事实都要写明证据等级（实测 / 协议实现 / 社区 / 推测），并更新 [PROTOCOL_FINDINGS.md](docs/PROTOCOL_FINDINGS.md)。
- 不要提交任何第三方专有软件、固件、源码、密钥、图片、字体或配置文件。
- 不要添加会扫描未知命令空间或批量写入的工具。写命令必须：能力门控 → 确认 → 回读 → 恢复/回滚。

## 代码规范

- C# / .NET 10，开启 nullable；每个文件以 `// SPDX-License-Identifier: GPL-3.0-only` 开头。
- 协议解析器保持严格；新增解析器要有抓包测试（去掉设备列表等个人数据）。
- 界面只能通过 `IDeviceCommandService` 改变耳机状态，只从 `IDeviceStateStore` 读取状态。
- 新型号放在 `OppoModels.cs`，未经真机验证的功能标为 `Experimental`。
- 提交前运行 `dotnet test tests/EncoDeck.Tests`，并确认 `dotnet build src/EncoDeck.App -p:Platform=x64` 无警告。

## 资源（图片与动画）

公开贡献只接受你有权以 GPL-3.0 分发的原创作品。常规设备图片与动画在运行时下载到本地缓存，不收集用户缓存到安装包。MOONDROP 官网产品图及处理记录已迁入 EncoAPI，客户端仅在运行时下载；图片不适用代码 GPL，来源与权利说明见 `THIRD_PARTY_NOTICES.md`。

## 许可

提交即表示你同意以 GPL-3.0-only 发布你的贡献，并确认你有权这样做。
