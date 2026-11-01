# 声桥 SoundBridge

把手机的声音，交给电脑来放。

Windows 从 10 2004 起支持蓝牙 A2DP Sink（让电脑当蓝牙音箱），但系统只提供了底层接口，**没有任何界面去管理它**。声桥就是那个界面：选中手机、点连接，手机上播的音乐、视频、语音就从电脑的耳机或音箱出来。

![界面截图](docs/screenshot.png)

## 它能做什么

- 列出**已配对且支持音频接收**的手机，一键连接 / 断开
- **首次启动先讲清「什么情况下用不了」**，之后随时可从界面左下角重新打开
- 关闭窗口缩到托盘，托盘菜单里也能连
- 开机自启（托盘启动）、启动即连上次那台手机
- 断线自动重连，指数退避
- **睡眠 / 休眠唤醒后自动重建连接**（不是靠断线事件，见下文）
- **选择声音输出到哪台设备**，并在连接时把系统默认设备切过去（切不过去时界面会给一个「去手动选」的入口）
- **连接时把音量压到 80% 以内**。手机一开播就直接出声，音量是上次留下的，很容易轰一下
- **定时关闭**：选个时长，到点自动断开（多数播放器会跟着停下），可勾选「到点响一声」
- 深色界面、单实例、日志可查
- **轻量**：空闲 CPU 0%、私有内存约 65 MB、启动 0.7 秒（实测数据见下）

## 它不能做什么

这几条是蓝牙协议和 Windows 本身的限制，不是没做：

- **不能控制手机的播放**。上一首 / 下一首 / 暂停走的是 AVRCP 协议，Windows 没有公开的控制器接口，只能自己实现上千行蓝牙协议。手机连上后也不会注册成系统的媒体会话，所以模拟媒体键同样无效。真想让手机那边停下来，只能用「定时关闭」这种曲线办法：断开连接，多数 App 会因此暂停。
- **不是给电脑接蓝牙耳机用的**。那个是 Windows 自带功能，和本项目无关。两者可以串联：手机 → 电脑（声桥）→ 蓝牙耳机，把耳机设为默认输出即可。
- **延迟约 0.2 秒**。听歌无感，看视频会音画不同步。
- **同一时间只能连一台手机**。

## 系统要求

| 项 | 要求 |
|---|---|
| 系统 | Windows 10 2004（内部版本 19041）以上 / Windows 11，64 位 |
| 运行时 | 免安装版不需要；从源码编译需要 .NET 8 SDK |
| **蓝牙适配器** | **必须支持 A2DP Sink（接收音频）** |

最后一条是关键。部分笔记本内置的 Intel / Realtek 适配器不开放接收能力，表现为「已经配对了手机，但列表里什么都没有」。这不是程序的问题，换一个支持 A2DP Sink 的 USB 适配器即可。

## 下载

到 [Releases](https://github.com/304713/soundbridge/releases/latest) 下载 `SoundBridge-1.5.1-win-x64.zip`，**解压后双击 `SoundBridge.exe`**，免安装、不需要 .NET 运行库。

> 注意：1.5.0 起改成「文件夹 + zip」发布，所以要先把压缩包解开再运行，不能像以前那样从压缩包里直接双击。换来的是私有内存从 153 MB 降到 65 MB、启动快 400 毫秒。

## 使用

1. 在 Windows「蓝牙和其他设备」里把手机和电脑配对
2. 打开声桥，选中手机，点「连接」
3. 手机上播放音频

第一次打开会先弹一页说明，重点只有一条：**确认你的蓝牙适配器支持 A2DP Sink**。列表里能看到手机就是支持，一直是空的就基本是不支持。这一页之后可以从主界面左下角「使用说明」重看。

首次运行会被 SmartScreen 拦一下（未购买代码签名证书），点「更多信息」→「仍要运行」。

## 从源码编译

```bash
dotnet build -c Release
dotnet run -c Release
```

打包成免安装版（文件夹 + zip）：

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=false -p:PublishReadyToRun=false -o dist

# 自包含发布会把整个 Windows 桌面运行时拷一份，其中 WinForms 那套（约 22 MB）
# 在 1.5.1 之后已经没有任何引用了，运行时不加载，删掉不影响启动
cd dist && rm -f System.Windows.Forms.dll System.Windows.Forms.Primitives.dll \
  System.Windows.Forms.Design.dll System.Windows.Forms.Design.Editors.dll \
  Microsoft.VisualBasic.Forms.dll WindowsFormsIntegration.dll \
  System.Design.dll System.Drawing.Design.dll
```

**不建议再打成「单文件 + 压缩」**。压缩单文件在运行时要把程序集解压进内存才能加载，实测同样的空窗口：压缩单文件私有内存 110 MB，普通文件夹 57 MB；声桥本体则是 153 MB → 65 MB，启动还快了 400 毫秒。多出来的一个「先解压」步骤，比自己多花 90 MB 内存划算。

`PublishReadyToRun` 也测过：启动再快 60–70 毫秒，包体 +8.6 MB。当前 JIT 只有 4500 个方法、428 KB IL，本来就不是瓶颈，所以没开。老机器反馈启动慢时可以打开。

### 1.5.1 的轻量化：托盘去掉 WinForms

托盘图标以前借的是 WinForms 的 `NotifyIcon`。1.5.1 改成直接调 `Shell_NotifyIcon` + 原生弹出菜单。

中间试过 WPF 的 `ContextMenu`，能用，但**每多一个带 `HwndSource` 的窗口就要多吃 17～37 MB 私有内存**（每个窗口自带一整套渲染表面），比省下来的还多，所以放弃了——最后用的是一个屏幕外的 1×1 原生窗口收消息，开销以 KB 计。

窗口打开、空闲、未连接同一台手机时测：

| 指标 | 1.5.0 | 1.5.1 |
|---|---|---|
| 私有内存 | 72.1 MB | 64.9 MB |
| 工作集 | 154 MB | 135 MB |
| 加载模块 | 163 | 155 |
| 压缩包 | 67.7 MB | 60.4 MB |

打包成 MSIX（Microsoft Store 用的格式）见 [`packaging/README.md`](packaging/README.md)。

## 两个踩过的坑

**一、默认输出设备消失后不会回落。**

Windows 有个反直觉的行为：默认输出设备消失后，它不会回落到别的设备，而是钉住那个已失效的设备。于是会出现「手机连上了、界面显示已连接，但一点声音都没有，连 3.5mm 耳机也没声」。

所以本项目加了「声音输出到」这一项：连接时主动把系统默认设备切到你选的那台，不再受系统遗留设置影响。这也是本项目与同类工具最主要的区别。

**二、睡眠唤醒后，连接对象会变成僵尸。**

笔记本合盖睡眠再打开时，蓝牙链路被系统拆掉了。但那个 `AudioPlaybackConnection` 对象**既不抛异常，也不一定触发 `StateChanged`** —— 它只是静静地失效。表现是界面还写着「已连接」，声音却没了，而且断线重连逻辑压根不会启动。

正确做法不是等事件，而是监听 `PowerModeChanged`：挂起时记下当前是否已连，唤醒后等蓝牙适配器重新初始化，然后**把这个对象整个丢掉重建**。

```
Suspend → 记录状态
Resume  → 等 2.5s（适配器重初始化）
        → Dispose 旧对象（它的句柄已经作废）
        → 轮询等待设备就绪（最多 90s）
        → 走正常的重连退避流程
```

同一次唤醒里还有个连带问题：USB 声卡会重新枚举，音频端点的 GUID 整个换掉，只按 ID 保存的输出设备就失配了。所以这里按 ID 找不到时会再按名字兜一层，找到后把新 ID 记回去。

## 与同类项目

| 项目 | 差异 |
|---|---|
| [AudioPlaybackConnector](https://github.com/ysc3839/AudioPlaybackConnector) | C++/Win32，纯托盘菜单，2020 年后基本停更。本项目的调用顺序（先 `StartAsync` 再 `OpenAsync`）参考了它 |
| [EasyBluetoothAudio](https://github.com/GorangN/EasyBluetoothAudio) | .NET + WPF，功能比本项目更全，活跃维护中 |
| [BluetoothAudioReceiver](https://github.com/Noxy229/BluetoothAudioReceiver) | C#，AMOLED 纯黑界面 |
| [Bleam](https://apps.microsoft.com/detail/9n4g4sh8x95p) | 微软商店版，功能与声桥高度重合（原生 A2DP、自动重连、托盘、不录音），2026 年 8 月上架 |

前面几个都只管建连、把声音交给系统默认设备，没有输出设备选择，也都没处理睡眠唤醒。

## 隐私

不联网、不收集任何数据、无遥测。设置和日志全部在本地：

```
%APPDATA%\SoundBridge\settings.json
%APPDATA%\SoundBridge\soundbridge.log
```

日志里会记设备名称和蓝牙地址，但**地址只保留前 4 位**（形如 `0123********`）。你把日志贴到 issue 里求助时，不会顺带暴露手机的完整 MAC。

## 许可证

[MIT](LICENSE)

## 免责声明

**免费、开源、个人业余项目，不是商业产品**：不收费、无内购、无订阅、无广告，也没有付费支持。作者是个人分享者，不是销售者或服务提供者。

本软件按「现状」提供，不附带任何担保。**它能不能用，取决于你的蓝牙适配器是否支持 A2DP Sink** —— 硬件不支持时，软件装上也用不了，这点没法靠代码绕过。

使用前请至少读一遍 [DISCLAIMER.md](DISCLAIMER.md) 的第 2 条（硬件兼容性）和第 4 条（音量与听力）。继续使用即视为接受全部条款。
