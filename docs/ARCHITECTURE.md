# 架构说明

## 产品边界

肝肾大圣是面向 Windows x64 离线单机游戏的本地扫描器和模块宿主。扫描结果属于临时会话；玩家确认的游戏、构建和字段属于便携式本地档案。项目不提供在线游戏支持、反反作弊、驱动或隐藏行为。

## 仓库职责

主程序仓库负责：

- 进程发现、逻辑进程组、构建指纹、内存扫描和游戏加速。
- 游戏库、快捷入口、锁定、主题、弹框、模块安装和应用更新。
- `collection`、`master-detail`、`property-grid` 三类标准页面模板。
- `GameValueEditor.ModuleSdk` Host API 与模块加载、隔离和安全校验。

独立的 `GameValueEditor-Modules` 仓库负责：

- 每个游戏的稳定模块 ID、进程身份和兼容构建。
- 页面清单、稳定编辑器 ID、显示名称、顺序、空状态和字段生命周期。
- 游戏运行时定位、线程切换、刷新、保存和真实回读。
- 游戏专属文档、实机测试、贡献者和发布资产。

普通新增游戏不得要求主程序增加游戏 ID、页面名称、固定 Tab、编辑器后缀判断或游戏专属测试分支。只有两个以上游戏都需要的新交互形态，才考虑增加新的宿主标准页面角色。

## 持久数据

```text
LibraryDocument
└─ GameProfile
   └─ GameVersionProfile
      └─ SavedField
```

- `GameProfile` 保存玩家看到的游戏条目、安装路径、置顶、锁定和可选模块 ID。
- `GameVersionProfile` 保存确定构建；普通游戏使用 EXE SHA-256，IL2CPP 游戏还组合 `GameAssembly.dll` 与 metadata。
- `SavedField` 保存玩家备注、分组、类型、定位器和模块语义键；显示名称从不作为内部身份。

原始地址不能跨进程或构建复用。可迁移的专属字段只能使用模块、编辑器、实体和字段组成的稳定语义键。

## 进程与扫描

`ProcessService` 把同一实例的父子进程归成逻辑游戏组，并根据窗口、运行时和工作集选择实际数据进程。数据进程重建时，宿主销毁进程级服务和临时地址，但保留可重新定位的语义字段。

首次扫描完整遍历可读内存并把候选写入 `data/scan-temp` 分区；界面只物化有限预览，再次扫描仍过滤完整候选集。清空、断开、进程重建和正常退出都会清理对应临时目录。

## Host API 4

公共契约位于 `src/GameValueEditor.ModuleSdk`。CLR `AssemblyVersion` 保持兼容绑定，实际能力门槛由 `module.json.hostApiVersion` 控制。

- `IGameAdapter`：模块身份、构建支持、编辑器描述和语义字段读写。
- `IInventoryGameAdapter`：集合/背包页面能力。
- `ICharacterAttributesGameAdapter`：人物属性页面能力。
- `IEntityEditorsGameAdapter`：装备、资源、进度等实体数字字段能力。
- `IGameEditorPageProvider`：API 4 必需，显式把每个编辑器绑定到标准页面角色。
- `IGameEditorFieldPolicyProvider`：可选，声明混合页面中单个字段的持久化和锁定策略。
- `IGameVersionMetadataProvider`：可选，只提供游戏自报版本信息，不能代替构建验证。

宿主加载模块时核对程序集与 `module.json` 的模块显示名，以及编辑器 ID、名称、类型、顺序和生命周期。API 4 还要求每个编辑器恰好注册一个页面，并验证页面角色与适配器能力一致。

## 模块安装与隔离

模块目录先按进程名发现可下载模块；下载安装后仍由模块的 `Supports` 对当前构建执行精确指纹或完整结构验证。未知布局必须安全拒绝。

ZIP 先校验 SHA-256、清单身份和路径，再解压到 `data/modules/packages/{模块ID}/{版本}`。运行时从会话影子副本加载，因此正式包可更新或卸载；残留占用会记录并在下次启动重试。

模块与游戏库保持一一关联。卸载模块保留普通游戏档案；从库移出会先卸载模块，再删除档案。模块贡献者来自包内快照，宿主只负责展示。

## 更新与发布

应用只接受正式语义版本和精确命名的标准 ZIP。独立更新器校验 SHA-256、拒绝越出应用目录的路径、跳过 `data`，并在文件替换失败时恢复原版本。

GitHub Actions 不是发布前提。正式发布由本地脚本执行完整构建、宿主烟雾测试、真实模块包兼容测试、标准包启动和更新器沙箱测试，再创建不可变 Release 并验证远端 SHA-256。

标准 GitHub Release 只包含主程序 ZIP。带模块的完整离线包是本地交付物，不上传到 GitHub Release。
