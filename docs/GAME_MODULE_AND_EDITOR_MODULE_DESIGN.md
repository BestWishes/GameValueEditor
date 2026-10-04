# 游戏专属模块与游戏内编辑模块设计

> 状态：Host API v2、独立模块中心、竖向导航、游戏库生命周期、贡献者机制及通用实体字段编辑契约已实施。最近更新：2026-10-02。
>
> 本文档定义“本游专属”功能下一阶段的统一模型，并作为后续框架改造、`fzzml` 新版本适配和贡献者扩展工作的设计依据。

## 1. 两层概念必须分开

### 1.1 游戏专属模块（Game Module）

游戏专属模块代表一个游戏，而不是某一种修改功能。例如 `fzzml` 对应一个游戏专属模块。它负责：

- 声明游戏身份、进程身份和可验证的构建指纹。
- 声明模块包版本、主程序接口版本和更新信息。
- 提供该游戏共享的运行时定位、线程调度、缓存刷新和存档能力。
- 注册这个游戏包含的所有“游戏内编辑模块”。
- 汇总本地是否安装、是否最新、当前游戏构建是否受支持等状态。

稳定 ID 示例：`game.fzzml`。显示名称可以修改，稳定 ID 发布后不得改变。

### 1.2 游戏内编辑模块（Game Editor Module）

游戏内编辑模块是某个游戏专属模块下面的一项具体编辑功能。例如：

- `game.fzzml.inventory`：背包物品。
- `game.fzzml.character-attributes`：人物属性。
- 后续还可增加技能、任务、门派资源等编辑模块。

它负责声明自己的显示名称、排序、标准界面类型、支持的构建、可读写能力，以及读取、修改、刷新和验证数据的流程。

显示名称不是身份；重命名“人物属性”不能改变模块 ID。子模块 ID 在所属游戏内必须唯一，冲突时拒绝装配，不能静默覆盖。

## 2. 包与目录层级

一个游戏使用一个可下载的游戏模块包；包内包含该游戏的多个编辑模块。某一项编辑功能变化时，发布新的游戏模块包版本。初期不把每个编辑模块拆成单独 DLL 或单独下载项，避免依赖和版本关系失控。

建议的模块中心仓库结构：

```text
games/
  fzzml/
    module.json
    contributors.generated.json
    src/
      Shared/
      Editors/
        Inventory/
        CharacterAttributes/
    tests/
    docs/
schemas/
templates/
catalog.json
```

主程序和游戏模块中心应使用不同仓库：主程序仓库负责宿主、界面和公共接口；模块中心仓库负责目录清单、各游戏源码、构建产物与贡献规范。Release 只承载可下载的 ZIP 等二进制附件，仓库目录负责保存清单、源码和文档。

## 3. “本游专属”界面规则

“本游专属”页分为上下两层：

1. 顶部是整个游戏专属模块的状态区，显示本地是否装配、模块包版本、是否最新、当前游戏构建是否受支持，并提供“检查新有”和“可更新”等操作。
2. 状态区下方是游戏内编辑区域。左侧使用固定宽度的竖向 Tab 或等价的竖向导航列出“背包物品”“人物属性”等编辑模块；右侧显示当前选中模块的内容。模块名称必须位于内容区左边，不得横向排列或以整行标题的形式堆在内容区上方。

顶部操作统一为“检查新有”“可下载/可更新”“卸载”，并在留白后提供低干扰的“贡献者”入口。贡献者属于整个游戏包，不属于某个编辑模块；弹框只显示 GitHub 名称/主页与首次、最新贡献日期。

`master-detail` 模块内部可以继续使用自己的左右分栏，例如“人物属性”右侧编辑内容中再以左侧人物列表、右侧属性表格呈现。这个内部分栏属于模块内容，不得与外层的游戏内编辑模块导航混为一层。

装备、材料、进度等同样属于 `master-detail`，但不应为了复用界面而伪装成人物属性。Host API v2 的 `IEntityEditorsGameAdapter` 用统一的实体、字段、范围和可写状态模型承载这些编辑器；宿主负责筛选、选择、修改和保存快捷入口，游戏模块只负责稳定语义身份与真实读写。

竖向条目按 `order` 后接稳定 ID 排序。主题、字体、间距、弹框、忙碌状态和错误呈现由主程序统一管理；游戏模块提供数据与操作，不应各自拼出风格不一致的任意界面。

建议优先支持三种标准界面模板：

- `collection`：平面集合/表格，适用于背包物品、技能列表等。
- `master-detail`：左侧实体列表、右侧字段详情，适用于人物列表与人物属性。
- `property-grid`：单个对象的键值属性，适用于玩家基础属性、全局资源等。

只有标准模板确实无法表达需求时，才讨论受约束的自定义界面扩展；该能力不进入第一阶段。

## 4. 概念接口

下面是接口职责草案，不代表现有代码已经实现，也不锁定最终 C# 方法签名。

```csharp
public interface IGameModule
{
    GameModuleMetadata Metadata { get; }
    IReadOnlyList<IGameEditorModule> Editors { get; }
}

public interface IGameEditorModule
{
    GameEditorMetadata Metadata { get; }
    bool Supports(GameBuildIdentity build);
    Task<EditorData> LoadAsync(EditorContext context, CancellationToken token);
    Task<EditorWriteResult> WriteAsync(
        EditorContext context,
        EditorWriteRequest request,
        CancellationToken token);
}
```

标准数据至少需要表达：

- 实体：稳定键、显示名称、摘要和可选图标。
- 字段：稳定键、显示名称、当前值、数据类型、是否可写、范围和可选值。
- 写入请求：编辑模块 ID、实体稳定键、字段稳定键和新值。
- 写入结果：成功状态、重新读取的真实值和可向用户展示的错误。

快捷入口保存的是编辑模块 ID、实体稳定键和字段稳定键组成的语义定位信息，不能保存会话地址作为跨重启身份。锁定功能只有在编辑模块声明安全支持时才可启用，并复用同一套完整写入流程。

## 5. 清单与兼容范围

清单应同时表达“整个游戏包认识哪些构建”和“每个编辑模块真正支持哪些构建”。概念示例：

```json
{
  "id": "game.fzzml",
  "version": "2.0.0",
  "hostApiVersion": 2,
  "game": {
    "displayName": "放置斩魔录",
    "processNames": ["fzzml"]
  },
  "supportedBuilds": ["fzzml-build-20260926"],
  "editors": [
    {
      "id": "game.fzzml.inventory",
      "displayName": "背包物品",
      "kind": "collection",
      "order": 100,
      "supportedBuilds": ["fzzml-build-20260926"]
    },
    {
      "id": "game.fzzml.character-attributes",
      "displayName": "人物属性",
      "kind": "master-detail",
      "order": 200,
      "supportedBuilds": ["fzzml-build-20260926"]
    }
  ]
}
```

顶层构建兼容不等于所有子模块自动兼容。允许同一个游戏包中“背包物品”支持当前构建，而“人物属性”显示“当前构建暂不支持”。未知构建必须安全失败，不能用通配符或相似版本猜测偏移。

一个游戏稳定 ID 同时只能启用一个游戏模块包版本。显示名称相同但 ID 不同不能视为同一模块；稳定 ID 相同的重复来源不能同时启用。

## 6. 标准读取和写入生命周期

每次操作应遵循：

1. 验证进程仍在运行，并重新确认当前构建身份。
2. 按稳定语义键重新定位运行时对象，不复用跨会话裸地址。
3. 读取并验证对象类型、实体身份和当前值。
4. 根据字段声明校验类型、范围、只读状态和上下文。
5. 在游戏要求的线程上执行真实修改；Unity/IL2CPP 对象通常需要主线程。
6. 调用游戏自身的缓存刷新、界面刷新和保存流程。
7. 重新读取真实值并返回验证结果；不能仅因内存写入调用成功就报告完成。

所有 DLL 模块都属于可执行代码。模块中心下载必须保留 HTTPS、SHA-256、路径安全检查和原子安装；后续可增加发布签名。贡献文档必须明确这一信任边界。

## 7. `fzzml` 第一项实验

现有背包功能迁移成 `game.fzzml.inventory`；新增 `game.fzzml.character-attributes` 作为第一项 `master-detail` 实验：

- 左侧列出游戏中的全部人物，使用稳定人物 ID 定位，名称仅用于显示。
- 选中人物后，右侧列出已确认安全的可修改属性。
- 每个属性使用稳定属性键，声明数据类型、范围、只读状态和写入能力。
- 不把“能读到”自动等同于“可以安全写入”；第一版采用经过验证的属性白名单。
- 修改后必须立即反映到游戏，并完成聚合/UI 刷新和重新读取。首版明确采用“仅本次游戏运行有效”的实验语义，不写入存档。
- 切换人物后必须重新定位目标对象，不能沿用上一人物的地址。

当前新构建已经按以下精确指纹适配：

- EXE SHA-256：`8B476C50395ACF8B4BD32E3DB60E29AC136436A5FADAB0E8D0049CA87CE3AACD`
- `GameAssembly.dll` SHA-256：`BF156D35DDB79839797517BC95BC07080DAACDCF78D08579B7D0D4C09386D752`
- `global-metadata.dat` SHA-256：`AEB09A9D3C8359F54C2DF29F3045C5AE1D9270DC269EC0A8E18C0D359CE4CD29`
- 组合构建指纹：`844E5ADE...C16BA`

### 7.1 当前构建实机侦察记录

下列结论来自上述精确指纹构建，不能直接套用到另一个 `GameAssembly.dll`：

- 人物权威来源已经确认是 `SaveManager._cachedSnapshot.playerDefault.units`，而 `ownedRows` 是同一批数据的二维字符串表示。
- 每个人物使用 `UnitSlotData.unitId` 作为稳定身份，`displayName` 仅用于界面显示；当前存档可以稳定枚举 10 个人物，并且两份表示中的人物 ID、姓名和字段值一致。
- `UnitSlotData` 已确认包含五维、成长率、生命、物理/法术攻击、物理/法术防御、速度、暴击、闪避、穿透、五行增伤与抗性等字段。第一版仍只开放通过写入、刷新和重启验证的白名单。
- 当前构建中 `SaveManager.get_Instance` RVA 为 `0x603E60`，`SaveManager._cachedSnapshot` 偏移为 `0x1A8`，`PlayerSaveData.units` 偏移为 `0x30`。
- 已否定“直接写 `UnitSlotData.strength` 后请求保存”的链路：即使保存文件时间发生变化，游戏的派生快照重建仍会把数值恢复为配置与成长公式算出的结果，不能把一次成功的内存写入误报为人物属性修改成功。
- 当前构建的 `UnitySynchronizationContext.ExecuteTasks` RVA 为 `0x17E2FF0`，入口字节已核对；主线程跳板必须按精确构建校验后才能安装，不能沿用旧构建 RVA。
- 旧构建 `SaveManager.get_Instance` RVA `0x602F90` 在当前构建中无效，曾导致侦察进程崩溃，因此所有未来布局都必须由三文件指纹精确匹配后再执行任何目标函数。

人物枚举、稳定身份、五维运行时写入、聚合刷新和背包保存均已在该精确构建实机验证。人物属性首版不寻找更深的持久化来源，也不在连接或重启时自动重应用。

### 7.2 2026-09-27 实机验证：玄道力道成长

用户当前选中人物为“玄道”。修改前的读取结果如下：

- 进程 PID：`29740`，进程名：`fzzml`。
- `UnitSlotData.level = 80`。
- 原始/派生前人物力道：`470`；游戏界面和 `PlayerAttributeAggregator.Compute` 的聚合力道：`506`，额外加成为 `36`。
- `UnitSlotData.growthStrength = 5.0`。
- `PlayerUnitConfig` 字段布局：`strength +0x48`，`growthStrength +0x58`；`UnitSlotData` 字段布局：`strength +0x50`，`growthStrength +0x60`。
- `CharacterGrowthFormulaCalculator.ApplyGrowthSnapshot` RVA：`0xAE0310`。
- `PlayerAttributeAggregator.Compute(string, SaveManager, ConfigManager)` RVA：`0x328300`。
- `PlayerAttributeEventHub.RaiseAttributesChanged` RVA：`0x32BD90`。
- `CharacterMenuInventoryManager.TryGetUnitConfigById` RVA：`0x51C230`，可用于按稳定人物 ID 取得对应的 `PlayerUnitConfig`。

反汇编已确认五维的核心公式。以力道为例，先计算：

```text
round(PlayerUnitConfig.strength
      + (UnitSlotData.level - 1) * PlayerUnitConfig.growthStrength)
```

随后再叠加境界修炼摘要中的力道，并由聚合器继续加入装备、天赋等加成。`PlayerDerivedSnapshotBuilder` 会从 `PlayerUnitConfig` 复制成长值并重新生成派生快照，因此只修改 `UnitSlotData.strength` 或 `UnitSlotData.growthStrength` 都会在刷新/保存时被覆盖。

对玄道当前数据，`470 = 5 + 79 * 5.0 + 70`：配置模板基础力道为 `5`，当前境界摘要提供 `70`，等级成长部分为 `395`。若保持模板与境界部分不变并让派生前力道达到 `1000`，候选成长值为：

```text
(1000 - 75) / 79 = 11.708860759...
```

实机实验严格执行了下列步骤：

1. 重新核对进程、三文件 SHA-256、人物 ID、等级、原始力道与成长值，全部与记录一致。
2. 先做一次 `5.0 -> 5.0` 的同值主线程试运行，验证配置定位、聚合器和事件通知；进程正常，聚合力道仍为 `506`。
3. 在 Unity 主线程通过 `TryGetUnitConfigById("玄道")` 取得本次会话配置对象 `0x13B50F61480`，并仅在旧成长值仍为 `5.0` 时写入 `11.708861f`。
4. 调用 `PlayerAttributeAggregator.Compute` 重算，再通过 `PlayerAttributeEventHub.RaiseAttributesChanged` 通知界面。
5. 工具回读得到派生前力道 `1000`、聚合力道 `1036`；用户在游戏人物界面同步确认显示值确实变为 `1036`，游戏进程继续正常响应。

首次实验后，用户观察到总力道已是 `1036`，但“力道成长”仍显示 `5.0`。原因是总属性聚合读取运行时 `PlayerUnitConfig.growthStrength`，而成长文本读取 `UnitSlotData.growthStrength`；单改配置成长会造成两个数据源语义不一致。因此该方案被进一步修正：在同一主线程事务中把配置成长从 `11.708861` 恢复为 `5.0`，同时把玄道的配置模板基础力道从 `5` 改为 `535`。最终公式为 `535 + 79 * 5.0 + 70 = 1000`，工具再次回读派生前力道 `1000`、聚合力道 `1036`，进程正常响应。此时界面继续显示成长 `5.0` 是正确结果。

这证明人物属性模块可以通过“稳定人物 ID -> 当前会话 `PlayerUnitConfig` -> 修改模板基础值 -> 游戏聚合器 -> 属性事件”实现即时生效，并保持成长显示语义一致。重启游戏后实测恢复原值，因此产品语义确定为 `SessionOnly`：关闭游戏后失效，不自动重应用，不允许锁定。快捷入口只保存人物 ID、属性键与目标值组成的语义键，不保存本次配置对象地址。

已经生成的只读/实验材料：

- `artifacts/FzzmlCharacterProbe/before-growth-test.json`：暂停前的只读人物及聚合属性快照。
- `artifacts/FzzmlCharacterProbe/growth-dry-run.json`：`5.0 -> 5.0` 同值链路验证结果。
- `artifacts/FzzmlCharacterProbe/growth-write-result.json`：`5.0 -> 11.708861` 的成功写入、派生前力道 `1000` 与聚合力道 `1036` 回读结果。
- `artifacts/FzzmlCharacterProbe/base-strength-write-result.json`：修正后的原子写入结果，配置基础力道 `5 -> 535`、配置成长 `11.708861 -> 5.0`，最终仍回读为派生前 `1000`、聚合 `1036`。
- `artifacts/growth-disasm.txt`：上述成长公式、配置定位与属性事件函数的反汇编记录。
- `artifacts/disasm_growth.py`：按当前精确构建 RVA 导出相关函数反汇编的辅助脚本。
- `artifacts/backups/fzzml-character-before-strength-20260927-214344/Saves` 与 `artifacts/backups/fzzml-character-before-progression-strength-20260927-214853/Saves`：此前两次失败写入前的存档备份。

## 8. 验收条件

框架验收：

- 一个游戏模块包能够注册两个以上游戏内编辑模块。
- “本游专属”使用竖向导航，切换模块不会串数据、串状态或破坏主题。
- 游戏包状态与每个编辑模块的构建支持状态可以分别显示。
- 清单、ID、排序、重复项、接口版本和不受支持构建均有自动验证。
- 现有 fzzml 背包能力迁移后行为不退化。

`fzzml` 人物属性验收：

- 当前目标构建能稳定列出全部人物。
- 人物身份和属性值与游戏界面或真实存档一致。
- 小范围测试写入立即生效，并在刷新、切换人物后保持正确；重启游戏后应恢复原值。
- 不支持或未验证的属性清晰标记为只读，不进行猜测性写入。
- 快捷入口只保存语义定位信息；如支持锁定，锁定必须走相同的安全写入和验证流程。

## 9. 推荐实施顺序

基础两层框架完成于 `v0.3.0-preview.8`；游戏库关联、模块卸载、自动贡献者与审核流程完成于 `v0.3.0-preview.12` / `game.fzzml` v2.0.1 / `game.worldapart` v1.0.1。后续新增游戏时仍按相同顺序执行：先声明稳定 ID 和构建范围，再实现共享运行时能力、注册编辑模块、验证真实读写，最后由发布脚本生成目录并由自动化登记贡献者。
