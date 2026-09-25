# 单机游戏数值编辑器

一个面向 Windows 离线单机游戏的本地数值扫描与管理工具。它不仅保存临时地址，还把玩家确认过的字段整理到按游戏、按版本隔离的本地游戏库中。

> 当前版本：`v0.1.1`（早期预览版）

## 主要功能

- 连接本机正在运行的 Windows 进程。
- 精确扫描 `4 Bytes`、`8 Bytes`、`Float` 和 `Double`。
- 按“精确值、已改变、未改变、增加、减少”继续过滤。
- 读取并修改选中的内存数值。
- 玩家在保存字段时手动填写显示名称；程序不会猜测字段含义。
- 左侧游戏库支持搜索、置顶、锁定、重命名和删除。
- 置顶或锁定的游戏条目不能删除；锁定不影响扫描、修改数值、保存字段或其他正常功能。
- 根据产品版本、文件版本、架构和 EXE SHA-256 保存独立版本档案。
- 当前进程版本不匹配时，不会自动套用旧版本字段。
- 静态地址优先保存为“模块 + 偏移”；动态地址保存为本次进程会话地址，并在重启后要求重新定位。
- 所有档案只保存在本机，写入时自动保留上一份配置备份。

## 下载与运行

在 GitHub Releases 下载 `GameValueEditor-v0.1.1-win-x64.zip`，解压后运行：

```text
GameValueEditor.exe
```

发布包为 Windows x64 自包含版本，不要求用户另行安装 .NET。某些高权限游戏需要以管理员身份运行本工具才能访问。

## 基本使用

1. 启动离线单机游戏。
2. 在顶部选择游戏进程并点击“连接”。
3. 输入游戏当前显示的数值并选择类型，点击“首次扫描”。
4. 回到游戏改变数值。
5. 在工具中选择“精确值、增加了、减少了”等条件，点击“再次扫描”。
6. 确认正确地址后，先点击“保存到游戏库”。
7. 选中扫描结果，点击“保存选中字段”。
8. 玩家手动输入名称，例如“洞府残核”；名称不由程序自动推导。

## 游戏版本规则

同一个游戏可以保存多个版本档案。应用使用 EXE 的 SHA-256 作为主要构建标识，即使两个构建显示相同版本号，也不会被误认为同一版本。

```text
游戏
├─ 版本 A（EXE SHA-256 A）
│  ├─ 玩家命名字段 1
│  └─ 玩家命名字段 2
└─ 版本 B（EXE SHA-256 B）
   └─ 玩家命名字段 1
```

## 数据位置

本地游戏库默认保存在：

```text
%LOCALAPPDATA%\GameValueEditor\library.json
```

备份文件：

```text
%LOCALAPPDATA%\GameValueEditor\library.backup.json
```

## 从源码构建

要求：Windows 10/11 和 .NET 8 SDK 或更高版本。

```powershell
dotnet build GameValueEditor.sln -c Release
dotnet run --project tests/GameValueEditor.SmokeTests/GameValueEditor.SmokeTests.csproj -c Release
```

生成自包含发布包：

```powershell
./scripts/publish.ps1
```

输出位于 `dist/`。

验证最终 ZIP 中的程序能够正常启动：

```powershell
./scripts/test-package-startup.ps1 -ArchivePath ./dist/GameValueEditor-v0.1.1-win-x64.zip
```

## 当前限制

- 当前版本仅支持 Windows x64 主程序。
- 不提供驱动、反反作弊、隐藏进程或绕过保护功能。
- 动态堆地址在游戏重启后通常需要重新扫描；指针链和引擎适配器仍在后续路线中。
- 不支持服务器权威的在线游戏数值。
- 修改异常数值可能损坏游戏进度，建议先备份存档。

## 使用范围

本项目仅面向离线单机游戏、个人研究、调试和可访问性用途。请遵守游戏许可协议和当地法律，不要用于多人在线游戏、破坏公平性或规避安全机制。

## 参与开发

参见 [CONTRIBUTING.md](CONTRIBUTING.md) 和 [架构说明](docs/ARCHITECTURE.md)。

## License

[MIT](LICENSE)
