# DL2MC

Minecraft 原版与加载器下载安装工具。

从 Mojang 官方 API 获取游戏本体，配合社区公共镜像源加速，把可启动的版本完整写入 `.minecraft` 目录。安装完成后由你常用的启动器负责登录与启动，本程序不涉及账号认证。

## 为什么有这个项目

Minecraft 原版下载与加载器安装是启动器的核心功能之一，但并非所有启动器都长期提供。本项目把这部分能力独立出来，做成一个只管下载与安装的独立工具：

- 不绑定任何启动器，产出标准 `.minecraft` 目录布局
- 不处理登录、不接触账号、不管理游戏启动
- 只支持将指定文件下载到指定目录

## 功能

### 原版游戏

- 支持正式版、快照、远古版本
- 官方源与 BMCLAPI 镜像双通道，测速低于阈值自动切换
- SHA1 校验、断点续传、失败重试
- 实时显示总进度、分项进度与下载速度

### 加载器

| 加载器 | 安装方式 |
|---|---|
| Fabric | 直接写版本 JSON 与库文件 |
| Fabric API | 从 Modrinth 下载到 `mods` |
| Forge | 调用官方 installer |
| NeoForge | 调用官方 installer，依赖预下载避开网络问题 |
| OptiFine | 自研安装，老版本自动改用 mod 方式 |

- 自动探测适配版本，也可手动指定任意历史版本
- 加载器之间冲突检测，互斥项置灰
- 老版本 Forge 自动降级为手工安装

### 版本管理

- 扫描并列出已有版本，显示加载器构成
- 重命名：同步改写 JSON 内的 id 与依赖它的 `inheritsFrom`
- 删除：存在子版本依赖时阻止，避免删出无法启动的版本
- 切换加载器：原有 `mods` / `config` / `saves` 自动迁移
- 安装或更新失败自动回滚

### 界面

- 亮色、暗色、跟随系统三套主题
- 列表宽度、日志高度可拖拽，尺寸写入配置
- 版本搜索实时过滤
- 日志面板支持全选复制
- 下载进度可最小化到状态栏

## 快速开始

1. 下载 `DL2MC.exe`，双击运行
2. 首次启动需完整阅读并同意用户协议
3. 设置中选择 `.minecraft` 目录
   - 官方启动器：`%APPDATA%\.minecraft`
   - 其他启动器：`启动器所在目录\.minecraft`
4. 切到「添加新版本」，选择游戏版本
5. 按需勾选加载器，点击安装
6. 安装完成后，回到你的启动器刷新版本列表

## 系统要求

- Windows 10 1607 或更高（含 LTSC / Enterprise）
- 64 位系统
- 安装 Forge / NeoForge 需要 Java，程序会自动探测

程序为自包含单文件，无需安装 .NET 运行时。

## 命令行

附带一个命令行子程序，便于单独调试各环节：

```bash
DownLoad2MC list
DownLoad2MC info 1.21.11
DownLoad2MC install 1.21.11 --dir "C:\Users\You\AppData\Roaming\.minecraft"
DownLoad2MC fabric 1.21.11 --dir "..."
DownLoad2MC forge 1.20.1 --dir "..."
DownLoad2MC neoforge 1.21.11 --dir "..."
DownLoad2MC optifine 1.21.11 --dir "..."
DownLoad2MC mod fabric-api --mc 1.21.11 --dir "..."
```

## 技术栈

| 层 | 选型 |
|---|---|
| 运行时 | .NET 10 |
| 界面 | Avalonia 11 + CommunityToolkit.Mvvm |
| 网络 | HttpClient，内置指数退避重试 |
| 测试 | xUnit |
| 打包 | 自包含单文件（win-x64） |

## 项目结构

```
src/
├─ McInstaller.Core/    下载引擎、加载器安装、版本管理（无 UI 依赖）
├─ McInstaller.App/     Avalonia 图形界面
└─ McInstaller.Cli/     命令行调试工具
```

## 构建

```bash
# 开发运行
dotnet run --project src/McInstaller.App

# 发布单文件
dotnet publish src/McInstaller.App/McInstaller.App.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o publish
```

## 声明

- 本工具**不储存、不托管、不二次分发**任何 Minecraft 游戏文件，所有下载直连官方源或社区镜像
- Minecraft 游戏本体与资源版权归 Mojang / Microsoft 所有，**下载游戏文件不等于获得使用授权**，使用游戏需持有正版购买凭证
- 请勿用于盗版传播、商业牟利、私服引流等违规场景
- 本项目与 Mojang、Microsoft 无任何隶属或授权关系

## 许可证

见 [LICENSE](LICENSE)。
