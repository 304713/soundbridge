# 免责声明 / Disclaimer

最后更新：2026 年 10 月 29 日

---

## 摘要（请先看这段）

声桥是一个**免费、开源、纯本地**的小工具。它不做任何保证——包括不保证能在你的电脑上正常工作。

最需要你确认的一点：**它能否使用，取决于你的蓝牙适配器是否支持「音频接收」（A2DP Sink）。** 硬件不支持时，软件装上也用不了，这不是缺陷，是 Windows 在该硬件上根本不提供相应接口。详见第 2 条。

继续下载、安装或使用本软件，即视为你已阅读、理解并同意以下全部条款。**如果你不同意，请不要使用。**

---

## 1. 软件按「现状」提供

本软件按「现状」（AS IS）和「现有可用」（AS AVAILABLE）提供，不附带任何形式的明示或默示担保，包括但不限于：

- 适销性担保
- 特定用途适用性担保
- 不侵权担保
- 无中断、无错误、无延迟运行的担保
- 与任何特定硬件、驱动、操作系统版本或手机型号兼容的担保

作者不承诺修复任何缺陷、发布任何更新，或为任何特定问题提供支持。

## 2. 硬件与系统兼容性不作保证

**这是本软件最主要的失效原因，请务必先确认：**

| 条件 | 说明 |
|---|---|
| 蓝牙适配器支持 A2DP Sink | 许多笔记本内置的 Intel / Realtek 适配器只支持「发送」音频，不支持「接收」。此类硬件上本软件无法工作，且无法通过软件层面绕过 |
| Windows 10 2004（内部版本 19041）或更高 | 低于该版本，Windows 本身不具备蓝牙音频接收能力 |
| 手机已与电脑完成系统级配对 | 本软件不负责配对，只负责建立音频接收连接 |

**作者不对以下情况承担任何责任：** 因硬件不兼容导致的无法使用、因驱动程序缺陷导致的连接失败或音质异常、因蓝牙适配器省电策略导致的断连、因第三方蓝牙软件占用接收端导致的连接被拒。

判断方法很简单：连上之后，如果设备列表里出现了你的手机，说明硬件支持；如果列表始终为空，请先怀疑适配器，而不是反复重装软件。

## 3. 使用风险自负

你理解并同意，使用本软件的全部风险由你自行承担。这包括但不限于：

- 蓝牙连接中断、音频卡顿、音画不同步
- 系统默认播放设备被修改（本软件会按你的选择切换默认输出设备）
- 与其他蓝牙音频软件产生冲突
- 因系统睡眠、休眠、驱动更新、适配器重新枚举导致的连接失效

**建议：** 在重要场合（会议、直播、录音、演出）使用前，先做一次完整试运行。不要把本软件用于对音频连续性有严格要求的场景。

## 4. 听力与设备安全

本软件只是把手机的声音转发到电脑上播放，**不改变、也不限制音量**。

- 连接瞬间，电脑可能以较高音量输出，请提前把音量调低
- 长时间大音量使用蓝牙耳机或音箱可能造成听力损伤
- 请自行确认所连接的播放设备（尤其是大功率音箱、功放）音量处于安全范围

因音量设置不当造成的听力损伤或设备损坏，作者不承担责任。

## 5. 内容与版权

本软件传输的是**你自己设备上正在播放的音频**。这些内容的权利归其各自的权利人所有。

- 本软件不提供、不存储、不分发任何音频内容
- 你不得利用本软件规避版权保护措施，或用于侵犯他人著作权的用途
- 因你使用本软件传输的内容引发的任何法律纠纷，由你自行承担

## 6. 隐私与数据处理

本软件：

- 不联网，不上传任何数据
- 不录音，不保存任何音频内容
- 不含遥测、广告或账号体系
- 仅在本机保存一个设置文件和一个日志文件（`%APPDATA%\SoundBridge\`）

日志中可能包含设备名称与设备标识符，用于排查问题。分享日志前请自行检查是否包含你不希望公开的信息。

## 7. 责任限制

在适用法律允许的最大范围内，作者不对任何直接、间接、偶然、特殊、惩罚性或后果性损害承担责任，包括但不限于：

- 利润损失、数据丢失、业务中断
- 设备损坏、听力损伤
- 因无法使用本软件而产生的任何替代成本

即使作者已被告知此类损害的可能性，本条款依然适用。

部分司法管辖区不允许排除默示担保或限制附带损害的责任，因此上述限制可能对你不完全适用。在此类情形下，作者的责任以适用法律允许的最低限度为限。

## 8. 第三方商标

- 「Bluetooth」及蓝牙标志是 Bluetooth SIG, Inc. 的注册商标
- 「Windows」「Microsoft」是 Microsoft Corporation 的注册商标
- 本软件与上述公司**没有任何隶属、赞助或背书关系**

本软件调用的 `AudioPlaybackConnection` 是 Microsoft 随 Windows 提供的公开 API。

## 9. 开源许可与条款变更

本软件以 [MIT 许可证](LICENSE) 发布。MIT 许可证中的免责条款与本声明第 1、3、7 条相互独立、并行有效；两者冲突时，以更有利于作者免责的条款为准。

本免责声明可能随版本更新而调整，更新后自发布之日起生效。继续使用即视为接受变更后的条款。

## 10. 适用法律

本声明适用中华人民共和国法律（不含香港特别行政区、澳门特别行政区和台湾地区法律）。

因本软件产生的争议，双方应友好协商解决；协商不成的，提交作者住所地有管辖权的人民法院处理。

---

## English Summary

SoundBridge is free, open-source, local-only software provided **"AS IS"**, without warranty of any kind.

**It only works if your Bluetooth adapter supports A2DP Sink (audio receiving).** Many built-in Intel/Realtek adapters do not, and no software can work around that hardware limitation.

By downloading, installing, or using this software, you accept full responsibility for all risks, including connection failures, audio dropouts, changes to your system's default playback device, and any hearing or equipment damage caused by volume settings.

The author disclaims all liability for any direct, indirect, incidental, or consequential damages to the maximum extent permitted by applicable law.

This software is not affiliated with, sponsored by, or endorsed by Bluetooth SIG, Inc. or Microsoft Corporation.

Licensed under the [MIT License](LICENSE).
