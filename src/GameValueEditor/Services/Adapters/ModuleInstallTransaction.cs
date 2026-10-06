using System.IO;
using System.Text.Json;

namespace GameValueEditor.Services.Adapters;

// Prepared transactions may restore only their own pre-install state. Committed
// transactions never choose a prior version; their backups are cleanup-only.
internal sealed class ModuleInstallTransaction(ModuleStateStore store)
{
    internal const string Prefix = ".install-transaction-";

    internal string CreateDirectory()
    {
        EnsureNoTransactions();
        var directory = store.Resolve(Prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal void Commit(string directory, string id, string version, InstalledModuleDocument before,
        InstalledModuleDocument after, Action<string>? checkpoint = null, IReadOnlyList<string>? legacyIds = null)
    {
        ValidateDirectory(directory);
        var staged = Path.Combine(directory, "staged");
        var target = store.Resolve(Path.Combine("packages", id, version));
        var journal = new Journal
        {
            ModulesRoot = store.Root, Id = id, Version = version, Before = before, After = after,
            LegacyIds = legacyIds?.ToList() ?? [],
            HadTarget = Directory.Exists(target), TargetFiles = store.HashPackage(staged),
            PreviousFiles = Directory.Exists(target) ? store.HashPackage(target) : new()
        };
        Validate(directory, journal);
        if (!SameRecords(store.ReadInstalled(), before)) throw new InvalidDataException("模块登记已改变，已拒绝开始安装事务。");
        Save(directory, journal);
        try
        {
            checkpoint?.Invoke("prepared");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (journal.HadTarget) Directory.Move(target, Path.Combine(directory, "backup-package"));
            checkpoint?.Invoke("backed-up");
            Directory.Move(staged, target);
            checkpoint?.Invoke("package-placed");
            store.SaveInstalled(after);
            checkpoint?.Invoke("registered");
            journal.State = "Committed";
            Save(directory, journal);
        }
        catch (Exception installError)
        {
            try { Recover(directory, Read(directory)); }
            catch (Exception recoveryError)
            {
                throw new InvalidOperationException("模块安装未完成，恢复也未完成；事务与备份已保留。请解除文件占用或权限问题后重启，不要删除恢复记录。",
                    new AggregateException(installError, recoveryError));
            }
            throw;
        }
        // A cleanup failure cannot turn a durably committed installation into a rollback.
        checkpoint?.Invoke("committed");
        TryCleanup(directory);
    }

    internal void RecoverAll()
    {
        if (!Directory.Exists(store.Root)) return;
        var directories = Directories().ToArray();
        var preparations = directories.Where(directory => !File.Exists(Path.Combine(directory, "transaction.json"))).ToArray();
        foreach (var directory in preparations)
        {
            ValidateDirectory(directory);
            // Without a journal, only an empty directory or a still-present staged
            // package can be a pre-mutation preparation. Never discard a backup.
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(entry);
                var validTemporary = name.Length == "transaction.json.".Length + 32 + ".tmp".Length &&
                    name.StartsWith("transaction.json.", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal) &&
                    Guid.TryParseExact(name["transaction.json.".Length..^4], "N", out _);
                if (!(name == "staged" && Directory.Exists(entry)) && !(validTemporary && File.Exists(entry)))
                    throw new InvalidDataException("模块事务记录缺失且含变更/未知证据，已保留现场。");
            }
            store.HashPackage(directory);
        }
        var records = directories.Except(preparations).Select(directory => (Directory: directory, Journal: Read(directory))).ToArray();
        foreach (var record in records) Validate(record.Directory, record.Journal);
        foreach (var directory in preparations) TryCleanup(directory);
        foreach (var record in records) Recover(record.Directory, record.Journal);
    }

    internal void EnsureNoTransactions()
    {
        if (Directory.Exists(store.Root) && Directories().Any())
            throw new InvalidOperationException("模块安装事务尚未收尾，请重启恢复；已禁止新的模块变更。");
    }

    internal void DiscardUnprepared(string directory)
    {
        ValidateDirectory(directory);
        if (!File.Exists(Path.Combine(directory, "transaction.json"))) TryCleanup(directory);
    }

    private IEnumerable<string> Directories() => Directory.EnumerateDirectories(store.Root, Prefix + "*", SearchOption.TopDirectoryOnly);

    private Journal Read(string directory)
    {
        ValidateDirectory(directory);
        var path = Path.Combine(directory, "transaction.json");
        store.EnsureNoLinks(path);
        // Before a journal exists, no installed file or registration has been touched.
        // Keep unknown/interrupted preparation directories as evidence rather than guessing.
        if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024)
            throw new InvalidDataException("模块事务记录缺失或过大；已保留现场，请手动检查后重启。");
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        var fields = json.RootElement.EnumerateObject().Select(item => item.Name).ToArray();
        var required = new[] { "SchemaVersion", "ModulesRoot", "State", "Id", "Version", "Before", "After", "HadTarget", "TargetFiles", "PreviousFiles", "LegacyIds" };
        if (fields.Distinct(StringComparer.Ordinal).Count() != fields.Length || required.Except(fields, StringComparer.Ordinal).Any())
            throw new InvalidDataException("模块事务记录字段不完整。");
        return json.Deserialize<Journal>() ?? throw new InvalidDataException("模块事务记录无效。");
    }

    private void ValidateDirectory(string directory)
    {
        var name = Path.GetFileName(directory);
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[Prefix.Length..], "N", out _) ||
            !string.Equals(Path.GetFullPath(directory), store.Resolve(name), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模块事务目录身份无效。");
        store.EnsureNoLinks(directory);
    }

    private void Validate(string directory, Journal journal)
    {
        ValidateDirectory(directory);
        if (journal.SchemaVersion != 1 || journal.State is not ("Prepared" or "Committed" or "RolledBack") ||
            !string.Equals(journal.ModulesRoot, store.Root, StringComparison.OrdinalIgnoreCase) ||
            !ModuleStateStore.IsSegment(journal.Id) || !ModuleStateStore.IsVersion(journal.Version) ||
            journal.Before is null || journal.After is null || journal.TargetFiles is null || journal.PreviousFiles is null || journal.LegacyIds is null ||
            journal.LegacyIds.Any(id => !ModuleStateStore.IsSegment(id)))
            throw new InvalidDataException("模块事务身份、格式或状态无效，已保留恢复证据。");
        ModuleStateStore.ValidateInstalled(journal.Before);
        ModuleStateStore.ValidateInstalled(journal.After);
        if (journal.After.Modules.Count(item => item.Id == journal.Id && item.Version == journal.Version) != 1 ||
            !journal.HadTarget && journal.PreviousFiles.Count != 0 || journal.TargetFiles.Count == 0)
            throw new InvalidDataException("模块事务的目标记录无效。");
        var unrelatedBefore = journal.Before.Modules.Where(item => item.Id != journal.Id && !journal.LegacyIds.Contains(item.Id, StringComparer.Ordinal));
        var unrelatedAfter = journal.After.Modules.Where(item => item.Id != journal.Id);
        if (!unrelatedBefore.OrderBy(item => item.Id, StringComparer.Ordinal).SequenceEqual(unrelatedAfter.OrderBy(item => item.Id, StringComparer.Ordinal)))
            throw new InvalidDataException("模块事务试图改变无关模块的登记。");
        foreach (var files in new[] { journal.TargetFiles, journal.PreviousFiles })
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (string.IsNullOrWhiteSpace(file.Key) || file.Key.Split(['/', '\\']).Any(segment => segment is "" or "." or "..") ||
                    file.Key.Contains(':') || !seen.Add(file.Key) || file.Value is null || file.Value.Length != 64 || !file.Value.All(Uri.IsHexDigit))
                    throw new InvalidDataException("模块事务含不安全的文件记录。");
                foreach (var child in new[] { "staged", "backup-package" })
                {
                    var root = Path.Combine(directory, child) + Path.DirectorySeparatorChar;
                    var resolved = Path.GetFullPath(Path.Combine(root, file.Key));
                    if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("模块事务文件越界。");
                    store.EnsureNoLinks(resolved);
                }
            }
        }
        store.Resolve(Path.Combine("packages", journal.Id, journal.Version));
    }

    private void Recover(string directory, Journal journal)
    {
        Validate(directory, journal);
        if (journal.State != "Prepared") { TryCleanup(directory); return; }
        var current = store.ReadInstalled();
        if (!SameRecords(current, journal.Before) && !SameRecords(current, journal.After))
            throw new InvalidDataException("模块登记与安装事务冲突，已拒绝覆盖外部修改。");
        var target = store.Resolve(Path.Combine("packages", journal.Id, journal.Version));
        var backup = Path.Combine(directory, "backup-package");
        var staged = Path.Combine(directory, "staged");
        if (journal.HadTarget)
        {
            if (Directory.Exists(backup))
            {
                if (!SameFiles(store.HashPackage(backup), journal.PreviousFiles))
                    throw new InvalidDataException("模块事务备份不完整，已保留恢复证据。");
                CheckOwnedTarget(target, journal);
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.CreateDirectory(target);
                foreach (var file in journal.PreviousFiles.Keys)
                {
                    var destination = Path.Combine(target, file);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(Path.Combine(backup, file), destination, true);
                }
                if (!SameFiles(store.HashPackage(target), journal.PreviousFiles))
                    throw new InvalidDataException("模块包恢复校验失败，备份仍保留。");
            }
            else if (!Directory.Exists(staged) || !Directory.Exists(target) || !SameFiles(store.HashPackage(target), journal.PreviousFiles))
                throw new InvalidDataException("模块事务原目标或备份缺失，已拒绝猜测恢复。");
        }
        else if (Directory.Exists(target))
        {
            CheckOwnedTarget(target, journal);
            Directory.Delete(target, true);
        }
        if (!SameRecords(current, journal.Before)) store.SaveInstalled(journal.Before);
        journal.State = "RolledBack";
        Save(directory, journal);
        TryCleanup(directory);
    }

    private void CheckOwnedTarget(string target, Journal journal)
    {
        if (!Directory.Exists(target)) return;
        foreach (var file in store.HashPackage(target))
            if (journal.TargetFiles.GetValueOrDefault(file.Key) != file.Value && journal.PreviousFiles.GetValueOrDefault(file.Key) != file.Value)
                throw new InvalidDataException("模块目标存在事务之外的文件变更，已拒绝删除。");
    }

    private static bool SameRecords(InstalledModuleDocument left, InstalledModuleDocument right) =>
        left.SchemaVersion == right.SchemaVersion && left.Modules.OrderBy(item => item.Id, StringComparer.Ordinal)
            .SequenceEqual(right.Modules.OrderBy(item => item.Id, StringComparer.Ordinal));
    private static bool SameFiles(Dictionary<string, string> left, Dictionary<string, string> right) =>
        left.Count == right.Count && left.All(item => right.GetValueOrDefault(item.Key) == item.Value);
    private static void Save(string directory, Journal journal) =>
        ModuleStateStore.WriteAtomic(Path.Combine(directory, "transaction.json"), JsonSerializer.SerializeToUtf8Bytes(journal));

    private void TryCleanup(string directory)
    {
        ValidateDirectory(directory);
        // Traverse owned package contents before recursive deletion to reject links.
        try
        {
            store.HashPackage(directory);
            // Leave the terminal-state journal until all backups are gone, so a
            // locked file or a second interruption cannot erase cleanup evidence.
            foreach (var child in Directory.EnumerateDirectories(directory)) Directory.Delete(child, true);
            foreach (var file in Directory.EnumerateFiles(directory))
                if (Path.GetFileName(file) != "transaction.json") File.Delete(file);
            var journalPath = Path.Combine(directory, "transaction.json");
            if (File.Exists(journalPath)) File.Delete(journalPath);
            Directory.Delete(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    internal sealed class Journal
    {
        public int SchemaVersion { get; set; } = 1;
        public string ModulesRoot { get; set; } = "";
        public string State { get; set; } = "Prepared";
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public InstalledModuleDocument Before { get; set; } = new();
        public InstalledModuleDocument After { get; set; } = new();
        public bool HadTarget { get; set; }
        public Dictionary<string, string> TargetFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> PreviousFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> LegacyIds { get; set; } = [];
    }
}
