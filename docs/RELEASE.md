# 本地发布流程

GitHub Actions 不是发布前提。正式版本先完成源码级构建与测试并经过复核，然后提交源码；最终发布包必须从干净的已提交工作树重新构建和验证，再推送指向同一提交的带注释标签并上传不可变资产。二进制 `ProductVersion` 中的源码提交号必须等于 `HEAD`。

## 1. 验证标准包

```powershell
./scripts/verify-release.ps1 -Version 0.4.8
```

记录脚本输出的标准 ZIP 路径与 SHA-256。正式发布版本是独立的十进制计数器：每次只加 `0.0.1`，`0.4.9` 的下一版是 `0.5.0`，`0.9.9` 的下一版是 `1.0.0`。发布脚本从最新正式标签计算唯一下一版本并拒绝跳号；预览标签和 `complete-offline` 完整离线包均不能上传 GitHub Release，完整离线包只供本地或 QQ 分发。Host API 与 Schema 是独立整数协议号，不参与此进位。

## 2. 提交并创建标签

确认工作树、差异和远端状态后提交，在已验证的提交上创建带注释标签：

```powershell
git tag -a v0.4.8 -m "GameValueEditor v0.4.8"
```

发布脚本从 `origin`（或 `-RemoteName` 指定的远端）解析 GitHub 仓库，避免手工填写错误的所有者或仓库名。可用 `-PushRefs` 同时推送当前分支和标签；脚本先使用配置的 Git 远端，HTTPS 失败时自动通过 GitHub SSH 443 重试。

## 3. 发布并验证

```powershell
./scripts/publish-github-release.ps1 `
  -Tag v0.4.8 `
  -ReleaseName "肝肾大圣 v0.4.8" `
  -AssetPath ./dist/GameValueEditor-v0.4.8-win-x64.zip `
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
  -Tag v0.4.8 `
  -ReleaseName "肝肾大圣 v0.4.8" `
  -AssetPath ./dist/GameValueEditor-v0.4.8-win-x64.zip `
  -VerifyOnly
```

输出必须包含 `Verified = True`，远端资产大小和 SHA-256 必须与本地文件完全一致。

## 4. 更新索引并执行保留策略

远端资产验证完成后，用 `scripts/update-release-index.ps1` 把新版本加入 `release-index.json`，最多保留 3 个快照；提交并推送索引后，复核公开 raw 文件与 Release 一致。最后运行 `scripts/prune-github-releases.ps1`，它只删除第 4 个及更旧的正式 Release/资产并保留标签。主程序的每次发布必须在模块目录所需 Schema 不超过该版本声明上限的前提下更新索引。

主程序和各模块的版本号独立；兼容关系不靠版本号相等，而由主程序索引中的 Host API 范围，以及模块发布快照中的 `hostApiVersion`、`minimumHostVersion`、`maximumHostVersion` 共同确定。应用中的“回退”始终由用户显式确认；保留脚本和检查版本流程都不得触发自动回退。

## 中断恢复

- 标签已存在但 Release 不存在：使用同一参数重新运行脚本。
- Release 已存在但资产缺失：重新运行，脚本复用原 Release。
- 同名资产处于未完成状态：脚本只清理该半成品后重试。
- 同名资产已经上传且哈希一致：直接视为完成。
- 同名资产已经上传但哈希不同：停止，提升版本；不得删除或替换不可变资产。

## 本机安装失败恢复

0.4.7 标准包新增 release-compatibility.json；其版本、API 范围与目录 Schema 上限须与发布索引一致，索引脚本会核对。旧宿主生成的 pending 没有这些字段时，只有通过既有 SHA-256 校验的新包清单可以补足；不能猜测旧包兼容信息。

0.4.7 宿主使用当前安装的更新器副本（data/updates/recovery-runner-*.exe），不使用待回退旧包中的旧更新器。启动时会在正常界面和游戏库加载前拦截未完成事务，包括缺标记但存在 Applying 记录的情况。该保护只适用于新版本，历史旧宿主的启动代码不能被追溯改变。

任何替换前先写 Applying 恢复记录与标记，并把 pending 移入事务目录；强制终止后保留备份、runner 与记录，不再次自动安装。全部替换成功后写 Committed，提交后残留仅清理、不能恢复旧文件。实际替换前重新核对当前模块，期间与模块安装/卸载共用变更锁。

安装失败时更新器先尝试恢复事务前的原文件；这不是自动降级版本。若恢复也因文件占用失败，不会重启混合版本，事务目录中的 `backup` 与 `recovery.json` 必须保留。`data/updates/recovery-required.json` 记录恢复记录及本次更新器的位置，错误提示也提供记录位置。该状态下不允许新更新/回退，不清理更新器及失败证据。

关闭主程序和占用文件的程序后，按记录中的绝对路径执行：

```powershell
& "<RunnerPath>" --recover "<JournalPath>" --app-dir "<应用目录>"
```

退出码 0 表示原文件已完整恢复、标记和事务目录已清除；退出码 1 时保留备份，查看事务目录中的 `recovery-error.log`，释放占用后再试。不要手动删除恢复标记绕过检查，也不要移动/编辑恢复记录或覆盖备份。用户 `data` 不在恢复范围内。本次设计与三轮检查记录见[稳定性修复设计](STABILITY_OPERATIONS_STORAGE_AND_RECOVERY_DESIGN.md)，之前一轮的范围见[历史设计](COMPATIBILITY_ASYNC_LIFECYCLE_AND_RECOVERY_FIX_DESIGN.md)。
