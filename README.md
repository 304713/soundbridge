# 声桥 SoundBridge

把手机的声音，交给电脑来放。

Windows 从 10 2004 起支持蓝牙 A2DP Sink（让电脑当蓝牙音箱），但系统只提供了底层接口，**没有任何界面去管理它**。声桥就是那个界面：选中手机、点连接，手机上播的音乐、视频、语音就从电脑的耳机或音箱出来。

![界面截图](docs/screenshot.png)

## 它能做什么

- 列出**已配对且支持音频接收**的手机，一键连接 / 断开
- 关闭窗口缩到托盘，托盘菜单里也能连
- 开机自启（托盘启动）、启动即连上次那台手机
- 断线自动重连，指数退避
- **选择声音输出到哪台设备**，并在连接时把系统默认设备切过去
- 深色界面、单实例、日志可查

## 它不能做什么

这几条是蓝牙协议和 Windows 本身的限制，不是没做：

- **不能控制手机的播放**。上一首 / 下一首 / 暂停走的是 AVRCP 协议，Windows 没有公开的控制器接口，只能自己实现上千行蓝牙协议。手机连上后也不会注册成系统的媒体会话，所以模拟媒体键同样无效。
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

## 使用

1. 在 Windows「蓝牙和其他设备」里把手机和电脑配对
2. 打开声桥，选中手机，点「连接」
3. 手机上播放音频

首次运行会被 SmartScreen 拦一下（未购买代码签名证书），点「更多信息」→「仍要运行」。

## 从源码编译

```bash
dotnet build -c Release
dotnet run -c Release
```

打包成免安装单文件：

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

打包成 MSIX（Microsoft Store 用的格式）见 [`packaging/README.md`](packaging/README.md)。

## 一个踩过的坑

Windows 有个反直觉的行为：**默认输出设备消失后，它不会回落到别的设备，而是钉住那个已失效的设备**。于是会出现「手机连上了、界面显示已连接，但一点声音都没有，连 3.5mm 耳机也没声」。

所以本项目加了「声音输出到」这一项：连接时主动把系统默认设备切到你选的那台，不再受系统遗留设置影响。这也是本项目与同类工具最主要的区别。

## 与同类项目

| 项目 | 差异 |
|---|---|
| [AudioPlaybackConnector](https://github.com/ysc3839/AudioPlaybackConnector) | C++/Win32，纯托盘菜单，2020 年后基本停更。本项目的调用顺序（先 `StartAsync` 再 `OpenAsync`）参考了它 |
| [EasyBluetoothAudio](https://github.com/GorangN/EasyBluetoothAudio) | .NET + WPF，功能比本项目更全，活跃维护中 |
| [BluetoothAudioReceiver](https://github.com/Noxy229/BluetoothAudioReceiver) | C#，AMOLED 纯黑界面 |

上面几个都只管建连、把声音交给系统默认设备，没有输出设备选择。

## 隐私

不联网、不收集任何数据、无遥测。设置和日志全部在本地：

```
%APPDATA%\SoundBridge\settings.json
%APPDATA%\SoundBridge\soundbridge.log
```

## 许可证

[MIT](LICENSE)
