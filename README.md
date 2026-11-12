<!-- markdownlint-disable MD033 MD041 -->
<p align="center">
  <img alt="TKFMaaR" src="assets/logo.png" width="256" height="256" />
</p>

<div align="center">

# TKFMaaR

基于 [MaaFramework](https://github.com/MaaXYZ/MaaFramework) 的「天下布魔」日常自动化助手

RShock 维护版本，当前版本 **1.2**，在原 TKFMaa 项目基础上独立维护。

</div>

## 安装与使用

1. 从 [RShock/TKFMaaR Releases](https://github.com/RShock/TKFMaaR/releases/latest) 下载对应系统的完整压缩包并解压，普通 Windows 电脑选择 `TKFMaaR-win-x86_64-v1.2.zip`
2. 运行 `DependencySetup_依赖库安装_win.bat`
3. 运行 `MFAAvalonia.exe`

发布包包含 Python 3.12、MaaFramework 5.14.3 与 MFAAvalonia 2.16.2。启动时默认自动检查 `RShock/TKFMaaR` 的新版本，可在设置中关闭；发现更新后由用户确认下载和安装，不使用 Fork 前原项目的发布源。

1.0 的部分版本曾错误关闭并锁定自动检查，无法通过远程发布自动解除；这些用户须手动下载并安装一次 1.1 或更新版本。安装后会恢复检查开关，之后保留用户自己的开关选择。

1.2加入Windows首次打开的[旧版配置迁移向导](docs/zh_cn/tasks/config_migration.md)：打开旧助手后自动检测，唯一来源无需选择，点击导入即可继承 `config`、`appsettings.json` 和自定义关卡截图；多个来源时才需选择。可取消且只自动提示一次，导入前备份新版资料，旧目录保留。手动升级也须保留这三部分，不能只复制 `config`。

## 注意事项

闪退的话设置里更换一下截图方式~
有问题可以提issue，最近没啥时间，有时间会尝试优化的❤️

## 主要功能

- [x] 启动游戏
- [x] 商城购买
- [x] 领取体力
- [x] 炼金订单
- [x] 每日派遣
- [x] 消耗体力（随缘更新~）
- [x] 调教
- [x] 全境征才（为这顿醋包的饺子:)）
- [x] 奖励领取
- [ ] 协会奖励领取没做呢:(

## 免责声明

- 本项目仅用于节省本人日常重复操作时间，由于布魔加载时长不稳定所以**不保证稳定性**，使用中可能出现未预期的行为。
- 本项目**不修改游戏内存、不劫持网络通信、不绕过游戏安全机制**，仅模拟屏幕点击操作。
- 使用本软件产生的任何后果（包括但不限于账号异常、封禁、数据丢失）由使用者自行承担。开发者不对此负责。
- 如果您认为本项目的公开发布不妥，请通过 GitHub Issues 联系，我会立即处理。

## 致谢

- **[MaaFramework](https://github.com/MaaXYZ/MaaFramework)** — 自动化测试框架
- **[TKFMtool](https://github.com/BGFFw/TKFMtool.git)** — 全境征才参考
