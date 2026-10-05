# 本地发布流程

GitHub Actions 不是发布前提。正式版本必须先在本机完成构建、烟雾测试、标准包启动和更新器沙箱验证，再提交源码、推送带注释标签并上传不可变资产。

## 1. 验证标准包

```powershell
./scripts/verify-release.ps1 -Version 0.4.3
```

记录脚本输出的标准 ZIP 路径与 SHA-256。正式发布版本是独立的十进制计数器：每次只加 `0.0.1`，`0.4.9` 的下一版是 `0.5.0`，`0.9.9` 的下一版是 `1.0.0`。发布脚本从最新正式标签计算唯一下一版本并拒绝跳号；预览标签和 `complete-offline` 完整离线包均不能上传 GitHub Release，完整离线包只供本地或 QQ 分发。Host API 与 Schema 是独立整数协议号，不参与此进位。

## 2. 提交并创建标签

确认工作树、差异和远端状态后提交，在已验证的提交上创建带注释标签：

```powershell
git tag -a v0.4.3 -m "GameValueEditor v0.4.3"
```

发布脚本从 `origin`（或 `-RemoteName` 指定的远端）解析 GitHub 仓库，避免手工填写错误的所有者或仓库名。可用 `-PushRefs` 同时推送当前分支和标签；脚本先使用配置的 Git 远端，HTTPS 失败时自动通过 GitHub SSH 443 重试。

## 3. 发布并验证

```powershell
./scripts/publish-github-release.ps1 `
  -Tag v0.4.3 `
  -ReleaseName "肝肾大圣 v0.4.3" `
  -AssetPath ./dist/GameValueEditor-v0.4.3-win-x64.zip `
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
  -Tag v0.4.3 `
  -ReleaseName "肝肾大圣 v0.4.3" `
  -AssetPath ./dist/GameValueEditor-v0.4.3-win-x64.zip `
  -VerifyOnly
```

输出必须包含 `Verified = True`，远端资产大小和 SHA-256 必须与本地文件完全一致。

## 中断恢复

- 标签已存在但 Release 不存在：使用同一参数重新运行脚本。
- Release 已存在但资产缺失：重新运行，脚本复用原 Release。
- 同名资产处于未完成状态：脚本只清理该半成品后重试。
- 同名资产已经上传且哈希一致：直接视为完成。
- 同名资产已经上传但哈希不同：停止，提升版本；不得删除或替换不可变资产。
