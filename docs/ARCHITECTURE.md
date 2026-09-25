# 架构说明

## 目标

本项目将“搜索地址”和“管理已确认字段”分离：扫描结果是临时会话数据；游戏、版本和玩家命名字段是持久档案。

## 数据层级

```text
LibraryDocument
└─ GameProfile
   └─ GameVersionProfile
      └─ SavedField
```

- `GameProfile`：玩家看到的游戏条目、安装路径、置顶和锁定状态。
- `GameVersionProfile`：一个确定的 EXE 构建，以 SHA-256 为主标识。
- `SavedField`：玩家填写的名称，以及程序保存的类型和定位器。

显示名称不是定位依据。两个字段允许拥有相同名称，内部使用 GUID 区分。

置顶和锁定只保护整个游戏条目不被删除，不限制扫描、数值写入、字段管理、版本管理或重命名。

## 主要服务

- `ProcessService`：列出当前用户可访问的进程。
- `VersionFingerprintService`：读取版本、架构、大小并计算 SHA-256。
- `ProcessMemoryAccessor`：封装 `OpenProcess`、`VirtualQueryEx`、`ReadProcessMemory` 和 `WriteProcessMemory`。
- `MemoryScanService`：执行首次精确扫描与候选过滤。
- `ProfileStore`：以 JSON 原子写入本地游戏库，并维护上一版备份。

## 地址定位器

当前支持：

- `ModuleOffset`：地址位于已加载模块中，保存模块名和相对偏移。
- `SessionAddress`：地址位于动态内存，只在相同进程启动会话中使用。

后续计划增加多级指针、AOB 特征和运行时对象适配器。定位器失效时必须显示“需要重新定位”，不能尝试写入旧的动态地址。

## 权限边界

应用按普通用户权限启动，不自动请求管理员权限。连接权限不足时向用户解释错误，由用户自行决定是否以管理员身份重新启动。
