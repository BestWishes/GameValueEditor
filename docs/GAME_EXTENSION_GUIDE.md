# 新游戏搜索套路与专属模块扩展指南

这份文档说明如何扩展“肝肾大圣-单机游戏数值编辑器”。先判断需求属于通用搜索套路，还是必须调用游戏运行时对象与函数的游戏专属模块。完整两层模型见 [游戏专属模块与游戏内编辑模块设计](GAME_MODULE_AND_EDITOR_MODULE_DESIGN.md)。

## 先做分类

适合通用搜索套路：

- 数值仍以普通内存数据存在，只是经过可参数化的编码或换算。
- 规则能跨游戏复用，例如“界面值乘任意倍数后存储”。
- 写入候选地址后游戏会直接读取，不要求调用业务对象、缓存或保存函数。

适合游戏专属模块：

- 搜到的数字只是副本、缓存或 UI 文本。
- 必须按物品名、人物 ID 等稳定业务键重新定位运行时对象。
- 必须在游戏主线程调用缓存、事件或保存函数。
- 数据结构、函数偏移和类布局只对经过验证的构建有效。

尚未证明可跨游戏复用时，先做严格匹配构建的专属模块，不能把某个游戏的 RVA 伪装成通用套路。

## 通用搜索套路

相关代码在 `Models/MemoryValueType.cs`、`Models/ProcessItem.cs` 和 `ViewModels/MainViewModel.cs`。新增套路时：

1. 使用稳定且不可随意改名的套路 ID。
2. 把可变化部分保存为参数；2 倍、3 倍和 100 倍属于同一个“按比例存储”套路。
3. 首次扫描、再次扫描、临时写入和已保存字段必须使用相同的编码/解码规则。
4. 候选保留原始类型、套路 ID、参数、首次/上次/当前字节，显示值只是派生信息。
5. 为溢出、非法参数、编码、解码和扫描回归添加测试。

## 两层专属模块模型

主程序仓库 [GameValueEditor](https://github.com/BestWishes/GameValueEditor) 负责宿主、统一界面、Host API 和安全安装；独立模块中心 [GameValueEditor-Modules](https://github.com/BestWishes/GameValueEditor-Modules) 负责各游戏源码、清单、文档和发行包。

- 游戏专属模块代表一个游戏，例如 `game.fzzml`。
- 游戏内编辑模块代表该游戏的一种功能，例如 `game.fzzml.inventory` 和 `game.fzzml.character-attributes`。
- 一个游戏使用一个 ZIP/DLL，可以注册多个编辑模块。
- “本游专属”顶部状态属于整个游戏包，下方使用左侧竖向导航切换各编辑模块，所选模块内容显示在右侧；模块名称不得显示成内容区上方的横向标题行。
- 宿主统一渲染 `collection`、`master-detail` 和 `property-grid` 等标准界面；游戏模块返回数据和操作能力。

Host API v2 契约位于 `src/GameValueEditor.ModuleSdk`。模块中心保存同版本 SDK 源码快照；发布模块 ZIP 时只放游戏模块 DLL 和 `module.json`，不要把另一份 SDK DLL 或主程序 DLL 放入 ZIP。

## 创建游戏模块

建议目录：

```text
games/example/
  GameValueEditor.Modules.Example.csproj
  module.json
  src/
    ExampleGameAdapter.cs
    Shared/
    Editors/
      Inventory/
      CharacterAttributes/
  docs/
```

游戏适配器实现 `IGameAdapter`，并根据编辑能力实现 `IInventoryGameAdapter`、`ICharacterAttributesGameAdapter` 等小接口。`Editors` 返回稳定编辑器 ID、显示名称、类型、排序和是否仅会话有效。

`module.json` 示例：

```json
{
  "id": "game.example",
  "version": "1.0.0",
  "displayName": "示例游戏专属模块",
  "assemblyFile": "GameValueEditor.Modules.Example.dll",
  "hostApiVersion": 2,
  "editors": [
    {
      "id": "game.example.inventory",
      "displayName": "背包物品",
      "kind": "collection",
      "order": 100
    }
  ]
}
```

稳定 ID 发布后不得因中文名称变化而更换。同一游戏只启用一个游戏包版本；同一包内编辑器 ID 不得重复。

## 构建身份与兼容范围

`Supports` 必须严格验证目标构建：至少验证主 EXE SHA-256；Unity IL2CPP 游戏还应验证 `GameAssembly.dll` 和 `global-metadata.dat`。不得只依赖窗口标题、进程名、Unity 版本、文件时间或相似的显示版本。

每个编辑模块可以比整个游戏包支持更窄的构建范围。整个包支持当前构建时，某个未适配的编辑器应显示“当前构建暂不支持”，其他编辑器仍可使用。未知构建必须安全失败，不能猜偏移。

## 稳定字段键与快捷入口

快捷入口保存语义键，不保存本次会话的指针。Host API v2 提供 `ModuleFieldKey.Create(editorId, entityId, fieldId)`，把编辑器、实体和字段组合成稳定键。每次读取或写入都重新定位真实对象。

玩家填写的“备注名称”只用于显示，不能作为对象身份。发布新构建布局后，语义键相同的专属字段可以迁移；普通扫描地址不能跨构建复制。

如果编辑器声明 `SessionOnly`：

- UI 必须明确说明关闭游戏后失效。
- 不得把修改描述成已写入存档。
- 不启用锁定，也不在重启或重新连接时自动重应用。

## 安全读取与写入生命周期

每次操作按以下顺序执行：

1. 验证进程仍存在并确认精确构建。
2. 用稳定语义键重新定位运行时对象。
3. 读取并验证对象类型、身份和当前值。
4. 校验输入类型、范围和写入前置条件。
5. 在游戏要求的线程执行最小修改。
6. 调用所需的缓存、事件、UI 或保存流程。
7. 重新读取真实值；只有回读一致才报告成功。

Unity/IL2CPP 业务函数通常需要主线程。临时挂接必须校验原始指令，保存并恢复指令与页面保护，设置完成状态和硬超时；无法确认远程代码是否仍在执行时不要释放其内存。修改 `System.String` 字段时创建新托管字符串并使用写屏障替换引用，不要原地修改共享字符串。

## 清单、下载和安装

模块中心根目录 `catalog.json` 是“检查新有”的服务器清单。每项包括：

- 游戏模块 ID、语义化版本、Host API 版本和可选旧 ID。
- 进程名与一个或多个精确兼容构建。
- 编辑模块元数据。
- HTTPS Release 下载地址与 ZIP SHA-256。

安装流程固定为：临时下载、SHA-256 校验、安全解压、核对 `module.json`、原子移动到 `data/modules/packages/{游戏模块ID}/{版本}`，最后更新 `installed.json`。模块是可执行代码，必须审查来源。损坏模块不得阻止主程序启动。

不要先提交指向不存在资产的清单，也不要在相同版本下替换 ZIP 内容。内容变化必须提升模块版本并生成新哈希。

## fzzml 参考实现

模块中心的 `game.fzzml` v2.0.0 同时注册：

- `game.fzzml.inventory`：从 `SaveManager._cachedSnapshot.inventoryRows` 按物品名聚合，在 Unity 主线程替换数量字符串，刷新缓存并调用游戏自身保存函数。应用不会主动把超过 9999 的值拆栈。
- `game.fzzml.character-attributes`：从 `playerDefault.units` 枚举人物，用 `UnitSlotData.unitId` 定位，修改本次进程的 `PlayerUnitConfig` 基础五维，调用聚合器和属性事件。它仅本次游戏运行有效。

RVA、字段偏移和预期函数序言只属于清单列出的精确构建。支持新版本时新增经过审查的布局，不能静默覆盖旧布局。

## 测试与发布

主程序至少执行：

```powershell
dotnet build GameValueEditor.sln -c Release
dotnet run --project tests/GameValueEditor.SmokeTests/GameValueEditor.SmokeTests.csproj -c Release
```

模块中心至少执行：

```powershell
dotnet build GameValueEditor.Modules.slnx -c Release
./scripts/publish-fzzml.ps1 -Version 2.0.0
```

每个模块发布前还要实机验证：支持构建能加载，错误构建被拒绝；读取与游戏一致；最小写入即时生效；页面刷新和对象重定位不串数据；持久化编辑器重启后仍正确；`SessionOnly` 编辑器重启后恢复；异常输入、对象未加载、结构变化和超时均安全失败；模块不会出现在其他游戏中。

发布顺序：构建与测试、创建不可变 Release 资产、确认下载地址、把真实 SHA-256 写入 `catalog.json`、提交清单、再从已发布版本执行一次在线检查与安装验证。
