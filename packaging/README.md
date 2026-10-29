# MSIX 打包与商店上架

这个目录把免安装版打包成 MSIX（Microsoft Store 使用的格式）。

## 目录内容

| 文件 | 用途 |
|---|---|
| `Package.appxmanifest` | 应用清单：身份、图标、启动任务、能力声明 |
| `Assets/` | 商店素材（44 / 150 / 310 方图、宽图、StoreLogo），由 `tools/BrandAssets` 生成 |
| `build-msix.ps1` | 一键打包脚本 |

生成素材（图标改动后重跑）：

```bash
cd ../tools/BrandAssets && dotnet run -- ../../SoundBridge/packaging/Assets
```

打包：

```powershell
cd packaging
.\build-msix.ps1 -Version 1.1.1
```

产物：`dist/SoundBridge-1.1.1-x64.msix`（约 64 MB，自包含 .NET 运行时）。

## 已在本机实测通过的

用自签证书走完了 `安装 → 启动 → 连手机` 全流程，结论如下：

| 项 | 结果 |
|---|---|
| MSIX 打包 | 通过（makeappx，63.9 MB） |
| 安装 / 启动 | 通过，窗口正常显示 |
| **`AudioPlaybackConnection` 在打包环境** | **可用**，日志 `OpenAsync 结果 = Success，当前状态 = Opened` |
| 蓝牙设备枚举 | 正常，识别到已配对的手机 |
| 启动任务登记 | 系统已注册 `SoundBridgeStartup`（`State=0`，即清单里的默认关闭） |
| 设置 / 日志位置 | 仍在 `%APPDATA%\SoundBridge\`，与免安装版共用，**没有**被重定向到 `Packages\...\LocalCache` |

最后一条是好消息：全信任的桌面打包应用不会重定向 AppData，所以商店版和免安装版共享同一份设置，用户迁移时不用重新配置。

## 上架还缺什么

1. **开发者账号**：`storedeveloper.microsoft.com` 注册，个人账号免注册费，需要身份证 + 自拍做身份验证。
2. **替换清单里的身份**：Partner Center 保留名称后会分配 `Package/Identity/Name` 和 `Publisher`，把它们填回 `Package.appxmanifest`。当前是占位值 `SoundBridge.Desktop` / `CN=SoundBridge`。
3. **商店列表素材**：至少 1 张截图（1366×768 或 1920×1080）、应用描述、分类、年龄分级问卷；如果声明了网络或个人信息相关能力，还要隐私政策 URL。
4. **认证备注（关键）**：写明「本应用需要支持 A2DP Sink 的蓝牙适配器；若列表为空，说明该适配器未开放音频接收能力」。审核员的机器未必支持，不写清楚容易被判「无法测试」。
5. 提交认证，通常 1–3 个工作日。

## 已知差异与待办

- **开机启动的静默性未验证**：MSIX 的 startupTask 不会传入 `--tray` 参数（免安装版靠这个参数隐藏窗口）。登录时是否弹窗需要重启实测；若会弹，需要在 `App.OnStartup` 里加一个「本次是启动任务拉起」的判断。
- **两版不能同时运行**：单实例互斥体是共用的，第二部分启动只会把第一个的窗口叫出来，然后自己退出。
- **政策风险**：「声音输出到」用的是未公开接口 `IPolicyConfig` 改系统默认播放设备，可能触碰 Store 政策 10.2.8（不得以未文档化或不受支持的方式修改系统设置）。稳妥做法是商店版关闭该功能，只保留「检测到默认设备不可用」的提示。
- **包体 64 MB**：自包含 .NET 运行时导致。如果要瘦身，可改成框架依赖并声明对 .NET Desktop Runtime 的依赖，但会要求用户先装运行时。

## 本地自测（不想上架，只想验证包装能不能跑）

需要一次性的自签证书，并在本机信任（仅用于自己测试，不能分发）：

```powershell
# 1. 建证书（Subject 必须和清单里的 Publisher 完全一致）
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=SoundBridge" `
    -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(1)

# 2. 导出公钥并导入到「受信任人」，否则装不上
Export-Certificate -Cert $cert -FilePath ".\test-sign.cer" | Out-Null
Import-Certificate -FilePath ".\test-sign.cer" -CertStoreLocation "Cert:\LocalMachine\TrustedPeople"

# 3. 签名并安装
.\build-msix.ps1 -Sign -Thumbprint $cert.Thumbprint
Add-AppxPackage -Path ..\dist\SoundBridge-1.1.1-x64.msix

# 4. 清理
Remove-AppxPackage (Get-AppxPackage -Name "SoundBridge.Desktop").PackageFullName
Get-ChildItem "Cert:\LocalMachine\TrustedPeople" | Where-Object { $_.Subject -eq "CN=SoundBridge" } | Remove-Item
Get-ChildItem "Cert:\CurrentUser\My" | Where-Object { $_.Subject -eq "CN=SoundBridge" } | Remove-Item
```

自签证书只能让自己这台机器信任，发给别人照样会被拦，所以对外分发只能走商店签名或买正规证书。
