using System.Text;
using System.Text.RegularExpressions;
using GameValueEditor.Models;
using GameValueEditor.ModuleSdk;

namespace GameValueEditor.Services.Adapters;

internal sealed record ModuleCompatibilityDiagnosticContext(
    string ApplicationVersion,
    GameVersionProfile? Version,
    ProcessItem? Process,
    VersionFingerprint? Fingerprint,
    string ModuleId,
    InstalledModuleRecord? InstalledModule,
    InstalledModuleManifest? InstalledManifest,
    GameModuleCheckResult? CheckResult,
    IGameAdapter? Adapter,
    IReadOnlyList<string> LoadErrors);

internal sealed record ModuleCompatibilityReportItem(
    string Category,
    string DisplayName,
    GameCompatibilityDiagnosticStatus Status,
    string Message)
{
    public string StatusDisplay => Status switch
    {
        GameCompatibilityDiagnosticStatus.Passed => "通过",
        GameCompatibilityDiagnosticStatus.Warning => "提醒",
        GameCompatibilityDiagnosticStatus.Failed => "失败",
        _ => "信息"
    };
}

internal sealed record ModuleCompatibilityReport(IReadOnlyList<ModuleCompatibilityReportItem> Items, string Text);

internal static partial class ModuleCompatibilityDiagnosticsService
{
    public static ModuleCompatibilityReport Create(ModuleCompatibilityDiagnosticContext context)
    {
        var items = new List<ModuleCompatibilityReportItem>();
        Add(items, "宿主", "主程序", GameCompatibilityDiagnosticStatus.Passed,
            $"肝肾大圣 v{context.ApplicationVersion}");
        Add(items, "宿主", "Host API", GameCompatibilityDiagnosticStatus.Passed,
            ModuleHostApi.CurrentVersion.ToString());

        AddBuildItems(items, context);
        AddModuleItems(items, context);

        var text = BuildText(items);
        return new ModuleCompatibilityReport(items, text);
    }

    private static void AddBuildItems(
        ICollection<ModuleCompatibilityReportItem> items,
        ModuleCompatibilityDiagnosticContext context)
    {
        var processName = context.Process?.ProcessName;
        if (!string.IsNullOrWhiteSpace(processName))
        {
            Add(items, "当前构建", "游戏进程", GameCompatibilityDiagnosticStatus.Passed, processName);
            Add(items, "当前构建", "运行时", GameCompatibilityDiagnosticStatus.Information,
                context.Process!.RuntimeKind.ToString());
        }
        else
        {
            Add(items, "当前构建", "游戏进程", GameCompatibilityDiagnosticStatus.Information,
                "未连接游戏；以下信息来自已保存的游戏版本。");
        }

        var version = context.Version;
        if (version is not null)
        {
            AddOptional(items, "当前构建", "游戏声明版本", version.GameDeclaredVersion);
            AddOptional(items, "当前构建", "游戏声明名称", version.GameDeclaredProductName);
            AddOptional(items, "当前构建", "游戏构建 GUID", version.GameDeclaredBuildGuid);
            AddOptional(items, "当前构建", "平台", version.PlatformVersionDisplay, "未识别");
            AddOptional(items, "当前构建", "引擎文件版本", version.FileVersion);
            AddOptional(items, "当前构建", "引擎产品版本", version.ProductVersion);
            AddOptional(items, "当前构建", "架构", version.Architecture, "Unknown");
        }

        var executableHash = context.Fingerprint?.Sha256 ?? version?.ExecutableSha256 ?? string.Empty;
        var buildHash = context.Fingerprint?.BuildSha256 ?? version?.BuildFingerprint ?? string.Empty;
        var assemblyHash = context.Fingerprint?.GameAssemblySha256 ?? version?.GameAssemblySha256 ?? string.Empty;
        var metadataHash = context.Fingerprint?.MetadataSha256 ?? version?.MetadataSha256 ?? string.Empty;
        AddHash(items, "EXE SHA-256", executableHash);
        AddHash(items, "组合构建指纹", buildHash);
        AddHash(items, "GameAssembly SHA-256", assemblyHash);
        AddHash(items, "metadata SHA-256", metadataHash);
    }

    private static void AddModuleItems(
        ICollection<ModuleCompatibilityReportItem> items,
        ModuleCompatibilityDiagnosticContext context)
    {
        if (string.IsNullOrWhiteSpace(context.ModuleId))
        {
            Add(items, "专属模块", "模块身份", GameCompatibilityDiagnosticStatus.Warning,
                "尚未识别当前游戏的专属模块。可先点击“检查新有”，或安装模块后重新诊断。");
        }
        else
        {
            Add(items, "专属模块", "模块 ID", GameCompatibilityDiagnosticStatus.Passed, context.ModuleId);
        }

        if (context.InstalledModule is null)
        {
            Add(items, "专属模块", "本地安装", GameCompatibilityDiagnosticStatus.Information, "未安装专属模块。");
        }
        else
        {
            Add(items, "专属模块", "本地安装", GameCompatibilityDiagnosticStatus.Passed,
                $"已安装 v{context.InstalledModule.Version}");
        }

        if (context.InstalledManifest is not null)
        {
            Add(items, "专属模块", "模块清单", GameCompatibilityDiagnosticStatus.Passed,
                $"{context.InstalledManifest.DisplayName} · Host API {context.InstalledManifest.HostApiVersion} · " +
                $"{context.InstalledManifest.Editors.Count} 个页面");
        }
        else if (context.InstalledModule is not null)
        {
            Add(items, "专属模块", "模块清单", GameCompatibilityDiagnosticStatus.Failed,
                "已登记安装记录，但无法读取对应 module.json。");
        }

        if (context.CheckResult is not null)
        {
            var status = context.CheckResult.Availability switch
            {
                GameModuleAvailability.Current => GameCompatibilityDiagnosticStatus.Passed,
                GameModuleAvailability.UpdateAvailable or GameModuleAvailability.Available =>
                    GameCompatibilityDiagnosticStatus.Warning,
                GameModuleAvailability.NotAvailable => GameCompatibilityDiagnosticStatus.Failed,
                _ => GameCompatibilityDiagnosticStatus.Information
            };
            Add(items, "专属模块", "服务器目录", status, context.CheckResult.StatusText);
            var catalogModule = context.CheckResult.RemoteModule ?? context.CheckResult.CatalogReferenceModule;
            if (catalogModule is not null)
            {
                var canValidateLocally = catalogModule.SupportsUnlistedBuildValidation &&
                                         context.CheckResult.Availability != GameModuleAvailability.NotAvailable;
                Add(items, "专属模块", "目录构建记录",
                    context.CheckResult.IsExactBuildMatch
                        ? GameCompatibilityDiagnosticStatus.Passed
                        : canValidateLocally
                            ? GameCompatibilityDiagnosticStatus.Warning
                            : GameCompatibilityDiagnosticStatus.Failed,
                    context.CheckResult.IsExactBuildMatch
                        ? "当前构建指纹已被服务器目录明确收录。"
                        : canValidateLocally
                            ? "服务器目录未明确收录当前构建；允许下载，但只有本地模块只读兼容检查通过后才会启用。"
                            : "服务器目录未明确收录当前构建，且模块未声明可对未知构建执行本地安全验证。");
                AddCatalogBuildComparisonItems(items, context, catalogModule, canValidateLocally);
            }
        }
        else
        {
            Add(items, "专属模块", "服务器目录", GameCompatibilityDiagnosticStatus.Information,
                "本次尚未联网检查模块目录。");
        }

        var relevantErrors = context.LoadErrors.Where(error =>
                string.IsNullOrWhiteSpace(context.ModuleId) ||
                error.Contains(context.ModuleId, StringComparison.Ordinal))
            .ToList();
        if (relevantErrors.Count == 0)
        {
            Add(items, "专属模块", "模块加载", GameCompatibilityDiagnosticStatus.Passed,
                "没有记录到模块加载错误。");
        }
        else
        {
            foreach (var error in relevantErrors)
                Add(items, "专属模块", "模块加载", GameCompatibilityDiagnosticStatus.Failed, error);
        }

        if (context.Adapter is null)
        {
            Add(items, "运行兼容", "适配器", context.InstalledModule is null
                    ? GameCompatibilityDiagnosticStatus.Information
                    : GameCompatibilityDiagnosticStatus.Failed,
                context.InstalledModule is null ? "本地没有可检查的适配器。" : "已安装模块未能加载适配器。");
            return;
        }

        Add(items, "运行兼容", "适配器", GameCompatibilityDiagnosticStatus.Passed,
            $"已加载 {context.Adapter.DisplayName}");
        if (context.Process is null || context.Fingerprint is null)
        {
            Add(items, "运行兼容", "当前构建支持", GameCompatibilityDiagnosticStatus.Information,
                "连接游戏后可执行本地只读兼容检查。");
            return;
        }

        bool supported;
        try
        {
            supported = context.Adapter.Supports(
                context.Process.ToModuleContext(), context.Fingerprint.ToModuleIdentity());
            Add(items, "运行兼容", "当前构建支持",
                supported ? GameCompatibilityDiagnosticStatus.Passed : GameCompatibilityDiagnosticStatus.Failed,
                supported ? "模块接受当前进程和构建指纹。" : "模块安全拒绝当前进程或构建。");
        }
        catch (Exception exception)
        {
            supported = false;
            Add(items, "运行兼容", "当前构建支持", GameCompatibilityDiagnosticStatus.Failed,
                $"兼容检查发生异常：{exception.Message}");
        }

        if (supported) AddEditorItems(items, context.Adapter, context.Process);
        AddProviderItems(items, context.Adapter, context.Process, context.Fingerprint);
    }

    private static void AddEditorItems(
        ICollection<ModuleCompatibilityReportItem> items,
        IGameAdapter adapter,
        ProcessItem process)
    {
        if (adapter is IGameEditorPageFactoryProvider)
        {
            foreach (var editor in adapter.Editors.OrderBy(editor => editor.Order))
                Add(items, "页面兼容", editor.DisplayName, GameCompatibilityDiagnosticStatus.Passed,
                    "模块自有 WPF 页面将由 Host API 6 页面工厂创建。");
            return;
        }

        var registrations = GameEditorPageResolver.Resolve(adapter)
            .ToDictionary(page => page.EditorId, StringComparer.Ordinal);
        foreach (var editor in adapter.Editors.OrderBy(editor => editor.Order))
        {
            try
            {
                if (!registrations.TryGetValue(editor.Id, out var page))
                {
                    Add(items, "页面兼容", editor.DisplayName, GameCompatibilityDiagnosticStatus.Failed,
                        "模块没有为该编辑器注册页面。");
                    continue;
                }

                var isSupported = page.Role switch
                {
                    GameEditorPageRole.Inventory => adapter is IInventoryGameAdapter,
                    GameEditorPageRole.CharacterAttributes => adapter is ICharacterAttributesGameAdapter characters &&
                                                               characters.SupportsCharacterAttributes(process),
                    GameEditorPageRole.Entity => adapter is IEntityEditorsGameAdapter entities &&
                                                 entities.SupportsEntityEditor(process, editor.Id),
                    _ => false
                };
                Add(items, "页面兼容", editor.DisplayName,
                    isSupported ? GameCompatibilityDiagnosticStatus.Passed : GameCompatibilityDiagnosticStatus.Warning,
                    isSupported ? $"{page.Role} 页面可用于当前构建。" : "当前构建暂不支持该页面。");
            }
            catch (Exception exception)
            {
                Add(items, "页面兼容", editor.DisplayName, GameCompatibilityDiagnosticStatus.Failed,
                    $"页面支持检查发生异常：{exception.Message}");
            }
        }
    }

    private static void AddProviderItems(
        ICollection<ModuleCompatibilityReportItem> items,
        IGameAdapter adapter,
        ProcessItem process,
        VersionFingerprint fingerprint)
    {
        if (adapter is not IGameCompatibilityDiagnosticsProvider provider)
        {
            Add(items, "模块扩展诊断", "详细检查", GameCompatibilityDiagnosticStatus.Information,
                "该模块未提供扩展诊断；宿主基础检查已经完成。");
            return;
        }

        try
        {
            var diagnostics = provider.GetCompatibilityDiagnostics(
                process.ToModuleContext(), fingerprint.ToModuleIdentity()) ?? [];
            if (diagnostics.Count == 0)
            {
                Add(items, "模块扩展诊断", "详细检查", GameCompatibilityDiagnosticStatus.Information,
                    "模块没有返回额外检查项。");
                return;
            }
            foreach (var diagnostic in diagnostics)
                Add(items, "模块扩展诊断", diagnostic.DisplayName, diagnostic.Status, diagnostic.Message);
        }
        catch (Exception exception)
        {
            Add(items, "模块扩展诊断", "详细检查", GameCompatibilityDiagnosticStatus.Failed,
                $"模块扩展诊断发生异常：{exception.Message}");
        }
    }

    private static void AddHash(
        ICollection<ModuleCompatibilityReportItem> items,
        string name,
        string value) => Add(items, "构建指纹", name,
        string.IsNullOrWhiteSpace(value)
            ? GameCompatibilityDiagnosticStatus.Information
            : GameCompatibilityDiagnosticStatus.Passed,
        string.IsNullOrWhiteSpace(value) ? "未提供" : value);

    private static void AddCatalogBuildComparisonItems(
        ICollection<ModuleCompatibilityReportItem> items,
        ModuleCompatibilityDiagnosticContext context,
        GameModuleCatalogEntry module,
        bool canValidateLocally)
    {
        var version = context.Version;
        AddCatalogBuildComparison(items, "EXE 对照",
            context.Fingerprint?.Sha256 ?? version?.ExecutableSha256 ?? string.Empty,
            module.CompatibleBuilds.Select(build => build.ExecutableSha256), canValidateLocally);
        AddCatalogBuildComparison(items, "GameAssembly 对照",
            context.Fingerprint?.GameAssemblySha256 ?? version?.GameAssemblySha256 ?? string.Empty,
            module.CompatibleBuilds.Select(build => build.GameAssemblySha256), canValidateLocally);
        AddCatalogBuildComparison(items, "metadata 对照",
            context.Fingerprint?.MetadataSha256 ?? version?.MetadataSha256 ?? string.Empty,
            module.CompatibleBuilds.Select(build => build.MetadataSha256), canValidateLocally);
    }

    private static void AddCatalogBuildComparison(
        ICollection<ModuleCompatibilityReportItem> items,
        string displayName,
        string current,
        IEnumerable<string> catalogValues,
        bool canValidateLocally)
    {
        var expected = catalogValues.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (string.IsNullOrWhiteSpace(current) || expected.Count == 0)
        {
            Add(items, "目录指纹对照", displayName, GameCompatibilityDiagnosticStatus.Information,
                "当前值或目录值未提供，无法逐项对照。");
            return;
        }

        var matched = expected.Any(value => string.Equals(value, current, StringComparison.OrdinalIgnoreCase));
        var status = matched
            ? GameCompatibilityDiagnosticStatus.Passed
            : canValidateLocally
                ? GameCompatibilityDiagnosticStatus.Warning
                : GameCompatibilityDiagnosticStatus.Failed;
        var expectedDisplay = string.Join("、", expected.Take(3).Select(ShortHash));
        if (expected.Count > 3) expectedDisplay += $" 等 {expected.Count} 个";
        Add(items, "目录指纹对照", displayName, status,
            matched
                ? $"当前 {ShortHash(current)} 已命中目录登记值（共 {expected.Count} 个）。"
                : $"当前 {ShortHash(current)} 未命中目录值：{expectedDisplay}。");
    }

    private static string ShortHash(string value) =>
        value.Length <= 12 ? value : $"{value[..12]}…";

    private static void AddOptional(
        ICollection<ModuleCompatibilityReportItem> items,
        string category,
        string name,
        string value,
        string missingValue = "未提供") => Add(items, category, name,
        string.IsNullOrWhiteSpace(value) || string.Equals(value, missingValue, StringComparison.Ordinal)
            ? GameCompatibilityDiagnosticStatus.Information
            : GameCompatibilityDiagnosticStatus.Passed,
        string.IsNullOrWhiteSpace(value) ? missingValue : value);

    private static void Add(
        ICollection<ModuleCompatibilityReportItem> items,
        string category,
        string displayName,
        GameCompatibilityDiagnosticStatus status,
        string message) => items.Add(new(
        Sanitize(category), Sanitize(displayName), status, Sanitize(message)));

    private static string BuildText(IReadOnlyList<ModuleCompatibilityReportItem> items)
    {
        var builder = new StringBuilder();
        builder.AppendLine("肝肾大圣 · 游戏专属模块兼容性诊断");
        builder.AppendLine("报告已经脱敏，不包含本机路径、用户名、PID、内存地址或存档内容。");
        string? category = null;
        foreach (var item in items)
        {
            if (!string.Equals(category, item.Category, StringComparison.Ordinal))
            {
                category = item.Category;
                builder.AppendLine();
                builder.AppendLine($"[{category}]");
            }
            builder.AppendLine($"[{item.StatusDisplay}] {item.DisplayName}：{item.Message}");
        }
        return builder.ToString().TrimEnd();
    }

    private static string Sanitize(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrEmpty(normalized)) return "未提供";
        if (SensitiveRuntimeValue().IsMatch(normalized) ||
            (!string.IsNullOrWhiteSpace(Environment.UserName) &&
             normalized.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase)))
            return "详细信息包含本机路径或运行期标识，已隐藏。";
        return normalized;
    }

    [GeneratedRegex(@"(?i)(?:[a-z]:[\\/]|\\\\|\bPID\s*[:=]?\s*\d+\b|\b0x[0-9a-f]+\b)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveRuntimeValue();
}
