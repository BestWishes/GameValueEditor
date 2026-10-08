# 本地发布流程

GitHub Actions 不是发布前提。正式版本先完成源码级构建与测试并经过复核，然后提交源码；最终发布包必须从干净的已提交工作树重新构建和验证，再推送指向同一提交的带注释标签并上传不可变资产。二进制 `ProductVersion` 中的源码提交号必须等于 `HEAD`。

递增版本时须同步主程序、更新器和 `tests/GameValueEditor.SmokeTests/GameValueEditor.SmokeTests.csproj` 的项目版本，以及正式入口默认版本和当前文档示例。离线页面核验报告读取测试入口程序集版本，未同步核验器版本会安全拒绝组包；历史升级场景的夹具版本不随当前版本机械替换。

## 1. 验证标准包

```powershell
./scripts/verify-release.ps1 -Version 0.5.2
```

记录脚本输出的标准 ZIP 路径与 SHA-256。正式发布版本是独立的十进制计数器：每次只加 `0.0.1`，`0.4.9` 的下一版是 `0.5.0`，`0.9.9` 的下一版是 `1.0.0`。发布脚本从最新正式标签计算唯一下一版本并拒绝跳号；预览标签和 `complete-offline` 完整离线包均不能上传 GitHub Release，完整离线包只供本地或 QQ 分发。Host API 与 Schema 是独立整数协议号，不参与此进位。

`publish.ps1` 要求 PowerShell 7.4+，调用共享 `application-package.ps1` 在独立工作目录生成候选包，严格验证六个标准文件、二进制版本/提交、真实启动和候选更新器后才原子提升。已有同名 ZIP 不覆盖；构建或验证失败不删除任何旧包，成功也不顺手删除其他本地 ZIP。服务器三个版本的保留策略仍由后续保留脚本执行。`test-application-package.ps1` 覆盖这些失败边界并已接入正式验证入口。

尚未提交的本地代码用 `./scripts/build-local-review-package.ps1` 验证，输出到唯一的 `artifacts/local-review-*` 目录，并在目录旁明确标注“未发布、含未提交改动”。此命令不改版本、不生成正式 dist 资产、不提交或联网发布；不得把同版本 review 包上传覆盖正式资产。二进制提交号仅代表基线提交，不代表其工作树已经提交。

v0.5.0 汇总离线打包、下载反馈、模块资料/安装事务、单实例、游戏库结构、通用写入回读、日志及会话生命周期修复，见[历史集成记录](RELEASE_0_5_0_INTEGRATION_REVIEW.md)。该版本配置格式为 7，Host API 为 7，旧版资产不覆盖。

v0.5.1 经用户授权集成此前本地后台操作、字段顺序、共享目标协调、解锁和刷新边界修复，并调整游戏库小图标。Host API 正式升为 8，主程序、更新器及离线核验器版本均为 0.5.1，SDK 两仓源码为 4.0.3（CLR 2.0.0.0）。标准包能力和发布索引最大 API 均为 8；资料 Schema 7、目录 Schema 5 不变。三个新模块分别为 WorldApart 1.3.4、Fzzml 2.1.4、Last Epoch 0.5.5，最低宿主 0.5.1，先发布宿主再发布模块和目录。见[方案与实际复核](RELEASE_0_5_1_INTEGRATION_REVIEW.md)。

v0.5.2 集成用户确认的名称优先识别、启动器通用路由/倍速、页面会话保留/串行读取与本地 review 组合。API/Schema 不变；Last Epoch 0.5.6 使用当前元数据定位，远征 0.0.6 首次正式发布且最低宿主 0.5.2。不问凡尘本轮仅提交问题记录，万里仙途不发新版。发布顺序和实际核验见[本次集成记录](RELEASE_0_5_2_INTEGRATION_REVIEW.md)。

完整冒烟包含扫描读取边界回归，也可运行 `GameValueEditor.SmokeTests.exe --scan-read-only`。扫描变化页使用真实自有 native 区域先红后绿验证，异常枚举/全失败与正常零匹配分别处理；历史间歇失败与确定性缺口的证据边界见[扫描记录](SCAN_RELIABILITY_0_5_0.md)。不得通过跳过断言或无限重跑放行。

原本地锁定取消、模块会话隔离及再次筛选全失败修复见[历史方案与验收](BACKGROUND_OPERATION_BOUNDARIES_LOCAL.md)。针对性回归入口为 `GameValueEditor.SmokeTests.exe --background-boundaries-only`，也包含于完整冒烟；历史文档保留当时不提交的授权范围，本次正式集成单独记录。

保存字段顺序、模块页面有效期和刷新进程身份修复见[历史设计与验收](FIELD_ORDER_AND_PAGE_LIFETIME_LOCAL.md)。针对性入口为 `GameValueEditor.SmokeTests.exe --field-order-only`（也包含于完整冒烟）；可用 `--field-area=fields|pages|identity` 分组诊断。禁止把同版本本地构建覆盖既有正式资产。

实际字段协调与模块停用边界的原记录见[本地设计](SHARED_FIELD_COORDINATION_LOCAL.md)，针对性入口为 `--shared-field-only`。三个模块及脚手架同步接入 API 8，旧 API 6/7 页面包仍可加载，但未接入桥接的模块字段锁定安全暂停。先用 `validate-modules.ps1 -SkipCatalog` 验证新源码，不能把跳过目录检查当成发布完成；真实新资产核验后才更新 catalog。正式组包仍要求干净的已提交工作树。

解锁与整页快照的原记录见[本地边界方案](REFRESH_AND_UNLOCK_BOUNDARIES_LOCAL.md)。针对性入口 `--shared-field-only --shared-area=refresh`，可用 `--refresh-area=unlock|legacy|snapshots` 分组；完整冒烟包含这些回归。两仓 SDK 必须同步，新页面读取需要宿主快照能力，不能宣称现有 API 7 ZIP 已具备新能力。快照只覆盖宿主协调的模块写入，不保证游戏自然变化或未知原生地址写入的一致性。

## 2. 提交并创建标签

确认工作树、差异和远端状态后提交，在已验证的提交上创建带注释标签：

```powershell
git tag -a v0.5.2 -m "GameValueEditor v0.5.2"
```

发布脚本从 `origin`（或 `-RemoteName` 指定的远端）解析 GitHub 仓库，避免手工填写错误的所有者或仓库名。可用 `-PushRefs` 同时推送当前分支和标签；脚本先使用配置的 Git 远端，HTTPS 失败时自动通过 GitHub SSH 443 重试。

## 3. 发布并验证

```powershell
./scripts/publish-github-release.ps1 `
  -Tag v0.5.2 `
  -ReleaseName "肝肾大圣 v0.5.2" `
  -AssetPath ./dist/GameValueEditor-v0.5.2-win-x64.zip `
  -PushRefs
```

网络代理按以下优先级解析：

1. `-ProxyUrl` 显式参数；
2. `HTTPS_PROXY` / `HTTP_PROXY` / `ALL_PROXY` 环境变量；
3. Windows 当前用户的系统代理。

也可以显式禁用代理：

```powershell
./scripts/publish-github-release.ps1 ... -DisableProxy
```

资产上传显示实时进度。默认连接超时 15 秒，连续 60 秒低于 1 KiB/s 会中止，单次上传最多 30 分钟，最多尝试三次。失败重试只会删除同名且尚未完成的 GitHub 资产；已经上传的资产如果大小或 SHA-256 不一致，脚本会停止并要求提升版本，绝不覆盖。

发布完成后再次执行只读验证：

```powershell
./scripts/publish-github-release.ps1 `
  -Tag v0.5.2 `
  -ReleaseName "肝肾大圣 v0.5.2" `
  -AssetPath ./dist/GameValueEditor-v0.5.2-win-x64.zip `
  -VerifyOnly
```

输出必须包含 `Verified = True`，远端资产大小和 SHA-256 必须与本地文件完全一致。

## 4. 更新索引并执行保留策略

远端资产验证完成后，用 `scripts/update-release-index.ps1` 把新版本加入 `release-index.json`，最多保留 3 个快照；提交并推送索引后，复核公开 raw 文件与 Release 一致。最后运行 `scripts/prune-github-releases.ps1`，它只删除第 4 个及更旧的正式 Release/资产并保留标签。主程序的每次发布必须在模块目录所需 Schema 不超过该版本声明上限的前提下更新索引。

主程序和各模块的版本号独立；兼容关系不靠版本号相等，而由主程序索引中的 Host API 范围，以及模块发布快照中的 `hostApiVersion`、`minimumHostVersion`、`maximumHostVersion` 共同确定。应用中的“回退”始终由用户显式确认；保留脚本和检查版本流程都不得触发自动回退。

## 本地完整离线包（不要求提交或发布）

这是组合已有已验证产物的独立流程，不重新编译应用、不提升版本、不提交、推送、打标签、上传或清理 GitHub。要求 PowerShell 7.4+，首次组包需要主程序仓库 `dist` 中已有标准 ZIP，相邻模块仓库包含当前 Schema 5 目录、本地 JSON Schema 和目录引用的模块 ZIP。先用 `dotnet build GameValueEditor.sln -c Release` 准备与目标宿主语义版本一致的模块页面核验器；组合命令本身不会隐式构建或下载依赖。

```powershell
./scripts/build-complete-offline-bundle.ps1
# 版本默认读取宿主项目，也可显式指定；模块仓库可以在别的位置：
./scripts/build-complete-offline-bundle.ps1 -ApplicationVersion 0.5.2 `
  -ModuleRepository D:/MyOtherProjects/GameValueEditor-Modules
./scripts/build-complete-offline-bundle.ps1 -VerifyOnly
./scripts/test-offline-bundle.ps1
```

主程序输入固定为 `dist/GameValueEditor-v{version}-win-x64.zip`，必须与本地发布索引的大小/SHA-256、二进制版本/源码提交和能力声明一致。尚未进入索引的独立验证标准 ZIP 可显式提供 `-ExpectedHostSha256`；不能用它绕过已存在的索引记录。不得从正在使用的应用目录或 `artifacts/package` 直接组包，不能复制个人游戏库、存档、缓存或更新事务。

包含未提交源码或尚未发布模块的完整测试包也使用同一入口，但显式指定 `-LocalHostArchivePath <review ZIP>` 与 `-ExpectedHostSha256 <SHA256>`。宿主输入只接受 artifacts 下由 review 工具生成、带同哈希 LOCAL-REVIEW.txt 的六文件包，不接受标准发布 ZIP。本地模块用 `-LocalModuleArchivePaths <ZIP>` 与一一对应的 `-LocalModuleSha256 <SHA256>`，路径限模块 artifacts 下，逐包核验 schema/宿主边界/文件图并实际创建页面；不能与本次已选择的目录模块 ID 重复。测试已发布模块的本地修改时，用 `-ModuleIds` 只选择仍复用的目录模块，再显式加入修改后的本地模块，最终每个 ID 只安装一份；不修改 catalog 或正式 ZIP。

本地模式必须显式提供带 `-local-review-` 的新 `-OutputPath`，不允许 Force；未指定 ModuleIds 时选择全部目录模块，指定时精确选择并由维护者核对最终模块集合。包内说明与 ZIP 外核验记录写入 LocalOnly，源码提交号仅表示构建基线，不宣称工作树已提交；新旧状态为 LocalReview 而非“已是服务器最新”。二次核验仅需同一 OutputPath 加 VerifyOnly，优先使用冻结记录，无须仍保有本地输入。正式发布入口、索引和目录完全不改，不能把该同版本包上传替换正式包。

默认包含每个模块版本列表中与宿主 API 范围和主程序版本边界兼容的最新快照；没有兼容项、缺少本地 ZIP、清单/目录不一致或哈希错误时停止，不能静默漏装。未知目录协议也停止，不自动放宽兼容边界。工具不连接 GitHub；先准备所需模块原始 ZIP，不用应用下载缓存代替。

默认输出为 `dist/GameValueEditor-v{version}-complete-offline-win-x64.zip`。`-ModuleIds` 指定子集时默认名称为 `selected-offline`；`-OutputPath` 只能指向宿主 `dist` 内的非标准 ZIP。已有内容一致的包重新核验并启动后复用，包字节不变；不同内容默认报错，可选择新文件名或显式 `-Force`。新包在独立暂存目录中生成，逐项散列核验并隔离启动，通过后才原子替换；失败保留原包。`-VerifyOnly` 必须已有输出且不能与 `-Force` 同用，不改变包，但会短暂创建并清理隔离验证目录。

核验不再仅检查 EXE 存活：隔离模式实际加载包内 DLL，核对安装身份、编辑器注册，并创建和释放模块自有页面。它使用当前源码构建的同语义版本宿主实现，再独立启动包内 EXE；不宣称证明历史宿主二进制的全部行为或游戏内读写效果。核验器缺失、版本不同、模块加载/页面创建失败都会明确停止。

核验报告的 Host API 必须等于待验证包内 `release-compatibility.json` 冻结声明的 `MaximumModuleHostApi`，不写死当前数字，也不只看当前源码。报告仍必须满足目标版本、协议、成功标记和全部模块/页面的一致性。API 8 的真实组合发现并修复旧工具写死 7 的遗漏；共享断言新增 API 7/8/9 报告及不匹配/非法声明回归，离线组合累计 96 项，真实 DLL/页面失败集成两例也必须拒绝。这个本地源码工具修复不重打已发布标准或模块 ZIP，详见[最终集成记录](RELEASE_0_5_1_INTEGRATION_REVIEW.md)。

成功组包或复用后，在 ZIP 外保存 `artifacts/offline-bundle-receipts/<SHA256>.json` 本地核验记录，冻结宿主来源、模块版本和全部文件散列。`VerifyOnly` 优先使用记录，原始模块/宿主 ZIP 删除、目录升级或目录不可用不会因此把原包误判损坏。无记录时，按包内安装版本匹配目录保留快照和原 ZIP，不按最新版本；缺证据时明确要求恢复核验记录或原输入。记录需要保留或单独备份，是本机可信工作流证据而非数字签名；同时篡改包及本机记录不在其安全承诺内。`VerifyOnly` 不创建或修复记录。

返回结果含 `IntegrityVerified`、`ModulesVerified`、`StartupVerified`、`Reused`、SHA-256、大小、源码提交、模块版本/源哈希和 `ReceiptPath`。`IsLatest` / `LatestStatus` 独立描述当前本地目录下的组合新旧程度：`Current`、`NewerRecipeAvailable` 或目录不可用时的 `Unknown`（`IsLatest=null`）。目录与包不同不等于包损坏，新旧检查不联网。解压整个包后运行 EXE；预装模块仍受游戏构建检查约束，未来查新/更新可能需要联网。归档时间、安装时间与说明改变时新 ZIP 的哈希可能不同，不能把离线组合包当作标准应用更新资产。

旧仓库 `create-local-complete-package.ps1` 及发布技能的同名离线脚本都只调用仓库正式入口。离线回归测试已接入 `verify-release.ps1`，但单独组离线包无需运行该正式发布流程。详见[原离线打包设计](COMPLETE_OFFLINE_BUNDLE_DESIGN.md)和[本地可靠性改进及检查记录](LOCAL_RELIABILITY_DESIGN.md)。

## 下载状态与取消

### v0.5.0 游戏身份与退出修复

同名进程现在只用于发现候选，不合并游戏档案。唯一安装路径、已验证模块身份或已保存构建才能确认游戏；换目录后仍可关联已确认的档案，未知/多义候选使用顶部实际进程连接。移出时若已切到另一个游戏，原目标安全回正并断开，不改当前游戏；仍在原目标时保留其完整临时连接。模块已卸载但回正失败时保留档案、如实更新模块状态，让用户可以重试。

关闭时只有安全回正全部成功才取消任务和正式退出。回正失败或实际倍速任务仍在执行时，保留窗口、操作与连接监测；已回正的游戏保持正常速度，不自动再加速。正式清理已开始后的资源错误不会把窗口留在半销毁状态。这不是自动版本回退，主程序/模块手动回退策略不变。

定向回归可运行 `GameValueEditor.SmokeTests.exe --game-lifecycle-only`，窗口关闭协调随完整冒烟测试执行。详细规则与三遍检查记录见[设计文档](GAME_IDENTITY_AND_SESSION_LIFECYCLE_DESIGN.md)；历史本地实现记录与本次 v0.5.0 发布记录分开保留，旧版本资产不覆盖。

### 传输状态

主程序更新/手动回退和模块安装/手动回退共用单个下载会话。网络连接和等待数据期间每秒反馈，传输显示累计大小与平均速率；保留总计 20 分钟、连续无数据 60 秒的超时边界，不做自动回退或自动重试。底部固定槽位的“取消”只在传输阶段可点，开始校验/安装前同步关闭，安装事务不能被 UI 取消。用户取消清理临时下载，不改本地版本、不生成待安装记录、不弹错误或重启提示。取消按钮空闲 Hidden，右侧原有四个按钮的位置及 12 DIP 间距不随状态变化。

## 中断恢复

- 标签已存在但 Release 不存在：使用同一参数重新运行脚本。
- Release 已存在但资产缺失：重新运行，脚本复用原 Release。
- 同名资产处于未完成状态：脚本只清理该半成品后重试。
- 同名资产已经上传且哈希一致：直接视为完成。
- 同名资产已经上传但哈希不同：停止，提升版本；不得删除或替换不可变资产。

## 本机安装失败恢复

### v0.5.0 模块与多开保护

主程序在处理待更新、读取资料和清理临时文件前取得 `data/.application-instance.lock` 文件租约。同一目录的第二实例不处理任何资料或更新，只前置原窗口或提示退出；独立目录不受影响。正常退出或进程崩溃都会释放句柄，不要用删除锁文件的方式绕过独占保护。

模块记录 `data/modules/installed.json` 和 `pending-deletions.json` 的 `.backup` 为上一份有效资料，不能自动解释为回退指令。损坏、未来 Schema 或主文件缺失但备份存在会保留现场并阻止模块变更。先退出主程序，保留损坏主文件和备份的副本，再由维护者核对登记及对应包是否完整，明确恢复正确的主记录后重启；不得只删除损坏记录重新安装，也不能直接重放旧的待删除列表。

模块安装记录位于 `data/modules/.install-transaction-<GUID>/transaction.json`。未提交事务在启动时尝试恢复本次操作前的包与登记；仅有已知准备文件、尚未开始替换的中断可以安全清理。恢复失败时保留事务与原包备份，停止模块变更/不确定模块加载；解除占用或权限问题后重启重试。未知/损坏记录、链接、外部资料冲突须先由维护者检查，不能删除事务目录绕过检查。成功提交的事务只清理，不会改回旧版本。主程序更新器也拒绝尚未完成的模块事务、卸载或损坏记录。

以上是未完成安装的故障恢复，主程序和模块的版本回退仍是用户主动确认的操作。详见[设计及三遍检查记录](MODULE_STATE_AND_INSTANCE_RELIABILITY_DESIGN.md)。

### 已发布的主程序安装恢复

0.4.7 标准包新增 release-compatibility.json；其版本、API 范围与目录 Schema 上限须与发布索引一致，索引脚本会核对。旧宿主生成的 pending 没有这些字段时，只有通过既有 SHA-256 校验的新包清单可以补足；不能猜测旧包兼容信息。

0.4.7 宿主使用当前安装的更新器副本（data/updates/recovery-runner-*.exe），不使用待回退旧包中的旧更新器。启动时会在正常界面和游戏库加载前拦截未完成事务，包括缺标记但存在 Applying 记录的情况。该保护只适用于新版本，历史旧宿主的启动代码不能被追溯改变。

任何替换前先写 Applying 恢复记录与标记，并把 pending 移入事务目录；强制终止后保留备份、runner 与记录，不再次自动安装。全部替换成功后写 Committed，提交后残留仅清理、不能恢复旧文件。实际替换前重新核对当前模块，期间与模块安装/卸载共用变更锁。

安装失败时更新器先尝试恢复事务前的原文件；这不是自动降级版本。若恢复也因文件占用失败，不会重启混合版本，事务目录中的 `backup` 与 `recovery.json` 必须保留。`data/updates/recovery-required.json` 记录恢复记录及本次更新器的位置，错误提示也提供记录位置。该状态下不允许新更新/回退，不清理更新器及失败证据。

关闭主程序和占用文件的程序后，按记录中的绝对路径执行：

```powershell
& "<RunnerPath>" --recover "<JournalPath>" --app-dir "<应用目录>"
```

退出码 0 表示原文件已完整恢复、标记和事务目录已清除；退出码 1 时保留备份，查看事务目录中的 `recovery-error.log`，释放占用后再试。不要手动删除恢复标记绕过检查，也不要移动/编辑恢复记录或覆盖备份。用户 `data` 不在恢复范围内。本次设计与三轮检查记录见[稳定性修复设计](STABILITY_OPERATIONS_STORAGE_AND_RECOVERY_DESIGN.md)，之前一轮的范围见[历史设计](COMPATIBILITY_ASYNC_LIFECYCLE_AND_RECOVERY_FIX_DESIGN.md)。
