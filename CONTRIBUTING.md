# 参与开发

感谢你对“单机游戏数值编辑器”的兴趣。

## 开发原则

- 保持工具面向离线单机游戏。
- 不加入反反作弊、隐蔽注入、驱动绕过或在线游戏支持。
- 玩家字段名称始终由玩家主动填写，扫描器的技术信息与显示名称分离。
- 新的定位器必须在失效时安全失败，不能对未经验证的地址静默写入。
- 新增内存数据类型或定位器时，请补充自动化测试和文档。

扩展新游戏前请阅读 [新游戏搜索套路与专属适配器扩展指南](docs/GAME_EXTENSION_GUIDE.md)，先判断应该新增可复用的参数化套路，还是按构建指纹严格匹配的专属适配器。

## 提交前检查

```powershell
dotnet build GameValueEditor.sln -c Release
dotnet run --project tests/GameValueEditor.SmokeTests/GameValueEditor.SmokeTests.csproj -c Release
```

Pull Request 请说明：

- 改动解决的问题。
- 对现有游戏档案兼容性的影响。
- 已执行的测试。
- 是否新增写入行为或系统权限要求。
