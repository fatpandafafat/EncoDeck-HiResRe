# EncoDeck 隐私政策 / Privacy Policy

最后更新 / Last updated: 2026-10-23

## 中文

EncoDeck 是一款非官方的开源 Windows 应用，用于控制兼容的蓝牙耳机。

- **不收集个人信息。** EncoDeck 没有账户系统，不包含广告、统计或遥测，不会把你的个人数据发送给开发者或任何第三方。
- **蓝牙。** EncoDeck 通过蓝牙与你已配对的耳机通信，用于读取电量、切换降噪等功能。这些数据只在你的电脑本地处理。
- **设备图片与 3D 模型。** 为显示设备外观，EncoDeck 会向 `encoapi.tianli0.top` 请求与耳机型号对应的图片资源。请求中只包含产品型号 ID 和颜色 ID，不含设备地址、序列号或任何个人信息。服务器可能像普通网站一样记录访问 IP 地址等标准日志，用于运维。
- **本地存储。** 设置、缓存和日志保存在你电脑的 `%LOCALAPPDATA%\EncoDeck` 文件夹中，不会上传。卸载应用后可手动删除该文件夹。
- **可选 LHDC 组件。** 仅在非商店版中点击下载时访问 EncoDeck 组件的 GitHub Release API 和下载服务。请求不包含耳机名称、蓝牙地址或序列号；GitHub 可能记录访问 IP 等标准网络信息。组件下载后保存在本机，打开应用不会自动下载。
- **联系方式。** 如有疑问，请在 https://github.com/TIANLI0/EncoDeck/issues 提交 issue。

## English

EncoDeck is an unofficial, open-source Windows app for controlling compatible Bluetooth earbuds and headphones.

- **No personal data is collected.** EncoDeck has no accounts, ads, analytics or telemetry, and sends no personal data to the developer or any third party.
- **Bluetooth.** EncoDeck talks to your paired earbuds over Bluetooth to read battery levels, switch noise control, and so on. This data is processed locally on your PC.
- **Device artwork and 3D models.** To show your device, EncoDeck requests model-specific images from `encoapi.tianli0.top`. Requests contain only the product model ID and color ID — no device address, serial number or personal information. The server may keep standard access logs (such as IP addresses) for operation.
- **Local storage.** Settings, caches and logs are stored in `%LOCALAPPDATA%\EncoDeck` on your PC and are never uploaded. You can delete this folder after uninstalling.
- **Optional LHDC component.** Only an explicit download action in the non-Store edition accesses the EncoDeck component GitHub Release API and download service. Requests contain no earbud names, Bluetooth addresses or serial numbers. GitHub may log standard network information such as IP addresses. Components are cached locally; starting the app does not download them automatically.
- **Contact.** Questions: https://github.com/TIANLI0/EncoDeck/issues


多品牌素材查询会发送公开目录中的型号名称、索引编号及可用的颜色码。型号匹配在本机完成，自定义蓝牙名称、蓝牙地址、序列号与账户信息不会随素材请求发送。

For multi-brand artwork, requests contain public catalog model names, indexed model IDs and available color codes. Matching happens locally; customized Bluetooth names, Bluetooth addresses, serial numbers and account data are not sent with artwork requests.
