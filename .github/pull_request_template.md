## 改动说明 / Summary

<!-- 这个 PR 做了什么、为什么。关联 issue 请写 Fixes #123 / What and why. Link issues with "Fixes #123". -->

## 改动类型 / Type

- [ ] 问题修复 / Bug fix
- [ ] 新功能 / Feature
- [ ] 新型号或协议适配 / New model or protocol support
- [ ] 文档 / Docs
- [ ] 重构或其他 / Refactor or other

## 验证 / Verification

- [ ] `dotnet test tests/EncoDeck.Tests` 通过 / passes
- [ ] `dotnet build src/EncoDeck.App -p:Platform=x64` 无警告 / builds without warnings
- [ ] 真机验证 / Tested on real hardware：<!-- 型号与固件 / model and firmware，或写“未验证” / or "not tested" -->

<!-- 界面改动请附截图 / Attach screenshots for UI changes. -->

## 检查清单 / Checklist

- [ ] 新文件以 `// SPDX-License-Identifier: GPL-3.0-only` 开头 / New files start with the SPDX header
- [ ] 日志、测试数据和截图中没有完整蓝牙地址等个人数据 / No full Bluetooth addresses or other personal data in logs, fixtures or screenshots
- [ ] 协议事实已写明证据等级并更新 `docs/PROTOCOL_FINDINGS.md`（如适用） / Protocol facts carry an evidence level and are recorded in `docs/PROTOCOL_FINDINGS.md` (if applicable)
- [ ] 写命令遵循 能力门控 → 确认 → 回读 → 回滚；未经真机验证的功能标为 `Experimental` / Writes follow capability gate → confirm → read back → roll back; unverified features are marked `Experimental`
- [ ] 未提交第三方专有软件、固件、密钥、图片或字体 / No third-party proprietary software, firmware, keys, images or fonts
- [ ] 用户可见的改动已更新 `CHANGELOG.md` 和相关文档 / User-visible changes are reflected in `CHANGELOG.md` and docs

提交即表示你同意以 GPL-3.0-only 发布本贡献 / By submitting, you agree to license this contribution under GPL-3.0-only.
