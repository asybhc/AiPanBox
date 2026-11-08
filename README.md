# AiPanBox

把百度网盘当成本地硬盘用的 Windows 播放工具。影片传到网盘前先加密，在网盘里是打不开的文件；在本机播放时自动解密，可以像本地视频一样拖动进度条。

作者：一杯好茶　　版本：1.0

## 功能

- 加密上传：文件先经 rclone crypt 加密再上传，网盘上看到的都是密文
- 本机播放：内置播放器，支持拖动进度条、暂停、继续、断点续播
- 多库并存：可以同时接入多个网盘目录，每个库一套独立密钥
- 上传队列：支持文件、文件夹、拖放，可暂停和继续，自动跳过已传完的文件
- 完整性校验：每个上传任务都有校验结果
- 账号管理：查看已登录账号，编辑凭证、移出挂载、删除账号
- 密钥备份：导出加密备份包（AES-256-GCM + 自定义密码），换电脑可恢复

## 运行环境

- Windows 10 / 11 64 位
- 首次运行需要联网，由 `setup-local.ps1` 下载 rclone 与 OpenList
- 百度网盘账号（SVIP 体验更好）

## 快速开始

1. 从 Releases 下载单文件版 `AiPanBox-1.0.exe`，双击后选择解压位置，得到一个 `AiPanBox-1.0` 文件夹；也可以下载 `AiPanBox-1.0-win64.zip` 直接解压
2. 进入该文件夹，运行 `setup-local.ps1` 完成初始化
3. 打开 `app\BaiduVault.App.exe`

百度网盘授权、添加库、上传、播放等详细步骤见 [使用说明.txt](使用说明.txt)。

## 从源码构建

需要 .NET 8 SDK。

```powershell
dotnet build -c Release BaiduVault.App\BaiduVault.App.csproj
```

打包自包含发行版：

```powershell
dotnet publish BaiduVault.App\BaiduVault.App.csproj -c Release -r win-x64 --self-contained true -o dist
```

## 目录结构

```
BaiduVault.App/      WPF 主程序（.NET 8）
BaiduVault-local/    部署与维护脚本、文档
  setup-local.ps1    首次部署：下载组件、建目录、生成密钥
  stop.ps1           停止后台服务
  upload.ps1         命令行上传
  verify.ps1         环境自检
  使用说明.md         程序内「使用说明」的内容
  多网盘接入指引.md    其他网盘的接入方法
```

## 数据与密钥

运行时数据都在 `%LOCALAPPDATA%\BaiduVault` 和 `%LOCALAPPDATA%\BaiduVault.App`，不写注册表、不装系统服务。

密钥只保存在本机。换电脑或重装系统前，请先在软件里导出密钥备份包；密钥丢失后，网盘上的加密文件无法恢复。

## 致谢

播放基于 libVLC；加密与挂载使用 rclone；网盘接口与 WebDAV 由 OpenList 提供。
