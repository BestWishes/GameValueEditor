# 本地发布流程

GitHub Actions 不是发布前提。正式版本先完成源码级构建与测试并经过复核，然后提交源码；最终发布包必须从干净的已提交工作树重新构建和验证，再推送指向同一提交的带注释标签并上传不可变资产。二进制 `ProductVersion` 中的源码提交号必须等于 `HEAD`。

## 1. 验证标准包

```powershell
./scripts/verify-release.ps1 -Version 0.4.4
```

记录脚本输出的标准 ZIP 路径与 SHA-256。正式发布版本是独立的十进制计数器：每次只加 `0.0.1`，`0.4.9` 的下一版是 `0.5.0`，`0.9.9` 的下一版是 `1.0.0`。发布脚本从最新正式标签计算唯一下一版本并拒绝跳号；预览标签和 `complete-offline` 完整离线包均不能上传 GitHub Release，完整离线包只供本地或 QQ 分发。Host API 与 Schema 是独立整数协议号，不参与此进位。

## 2. 提交并创建标签

确认工作树、差异和远端状态后提交，在已验证的提交上创建带注释标签：

```powershell
git tag -a v0.4.4 -m "GameValueEditor v0.4.4"
```

发布脚本从 `origin`（或 `-RemoteName` 指定的远端）解析 GitHub 仓库，避免手工填写错误的所有者或仓库名。可用 `-PushRefs` 同时推送当前分支和标签；脚本先使用配置的 Git 远端，HTTPS 失败时自动通过 GitHub SSH 443 重试。

## 3. 发布并验证

```powershell
./scripts/publish-github-release.ps1 `
  -Tag v0.4.4 `
  -ReleaseName "肝肾大圣 v0.4.4" `
  -AssetPath ./dist/GameValueEditor-v0.4.4-win-x64.zip `
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
  -Tag v0.4.4 `
  -ReleaseName "肝肾大圣 v0.4.4" `
  -AssetPath ./dist/GameValueEditor-v0.4.4-win-x64.zip `
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
