using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoMagic.Application.Ozon.Mapping;
using Microsoft.Data.Sqlite;

namespace AutoMagic.Infrastructure.Ozon.Mapping;

/// <summary>Editable JSON rules with SQLite review history and recoverable file writes.</summary>
public sealed class JsonRuleReviewStore : IRuleReviewStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };
    public string RootPath { get; }
    private readonly string _connectionString;
    private readonly string _mutexName;
    public JsonRuleReviewStore(string root)
    {
        RootPath = Path.GetFullPath(root);
        Directory.CreateDirectory(RootPath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(RootPath, "reviews.db") }.ToString();
        _mutexName = "AutoMagic.Rules." + Hash(RootPath.ToUpperInvariant());
        Locked(db => { Execute(db, """
            CREATE TABLE IF NOT EXISTS drafts (key TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS audits (key TEXT NOT NULL, sample TEXT NOT NULL, correct INTEGER NOT NULL,
              before_json TEXT NOT NULL, after_json TEXT NOT NULL, actor TEXT NOT NULL, at TEXT NOT NULL,
              PRIMARY KEY(key,sample));
            CREATE TABLE IF NOT EXISTS history (id INTEGER PRIMARY KEY, category_id INTEGER, type_id INTEGER,
              before_json TEXT NOT NULL, after_json TEXT NOT NULL, at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS pending (id INTEGER PRIMARY KEY CHECK(id=1), category_id INTEGER NOT NULL,
              type_id INTEGER NOT NULL, expected_hash TEXT NOT NULL, json TEXT NOT NULL);
            """); return 0; }, recover: false);
    }
    public RuleBundle Load(long categoryId, long typeId) => Locked(db => LoadCore(categoryId, typeId));
    private RuleBundle LoadCore(long categoryId, long typeId)
    {
        CheckIds(categoryId, typeId);
        var commonTexts = Directory.Exists(CommonPath())
            ? Directory.GetFiles(CommonPath(), "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => (Path: path, Text: Read(path)))
                .ToArray()
            : [];
        var categoryText = Read(CategoryPath(categoryId));
        var typeText = Read(TypePath(categoryId, typeId));
        var category = Parse(categoryText, categoryId, null);
        var type = Parse(typeText, categoryId, typeId);
        var effective = new Dictionary<string, JsonFieldRule>(StringComparer.Ordinal);
        foreach (var common in commonTexts)
        {
            var module = ParseCommon(common.Text, Path.GetFileName(common.Path));
            foreach (var rule in module.Rules) Apply(rule, "公共属性");
        }
        foreach (var rule in category.Rules) Apply(rule, "品类");
        foreach (var rule in type.Rules) Apply(rule, "类型");
        if (effective.Values.GroupBy(r => (r.AttributeId, r.Scope)).Any(g => g.Count() > 1))
            throw new InvalidDataException("同一属性和作用域存在多个规则，请显式覆盖或禁用冲突规则。");
        var tokenSource = string.Join("\0", commonTexts.Select(item => item.Path + "\0" + item.Text)) +
                          "\0" + categoryText + "\0" + typeText;
        return new(Hash(tokenSource), type, effective.Values.ToArray());

        void Apply(JsonFieldRule rule, string layer)
        {
            if (rule.Action == "disable") { effective.Remove(rule.RuleId); return; }
            if (rule.Action == "add" && effective.ContainsKey(rule.RuleId))
                throw new InvalidDataException($"规则 {rule.RuleId} 已存在，{layer}文件请使用 override。 ");
            effective[rule.RuleId] = rule;
        }
    }
    public ReviewCount Counts(string key) => Locked(db =>
    {
        using var command = Command(db, "SELECT COUNT(*), COALESCE(SUM(correct),0) FROM audits WHERE key=$key", ("$key", key));
        using var reader = command.ExecuteReader(); reader.Read(); return new ReviewCount(reader.GetInt32(0), reader.GetInt32(1));
    });
    public string? LoadDraft(string key) => Locked(db =>
    {
        using var cmd = Command(db, "SELECT json FROM drafts WHERE key=$key", ("$key", key));
        return cmd.ExecuteScalar() as string;
    });
    public void SaveDraft(string key, string json) => Locked(db =>
    {
        Execute(db, "INSERT INTO drafts VALUES($key,$json) ON CONFLICT(key) DO UPDATE SET json=$json", ("$key", key), ("$json", json)); return 0;
    });
    public void Commit(ReviewCommit commit) => Locked(db =>
    {
        var current = LoadCore(commit.CategoryId, commit.TypeId);
        if (current.Token != commit.ExpectedToken) throw new InvalidOperationException("规则已变化，请保存草稿并重新运行匹配后确认。");
        var next = JsonSerializer.Serialize(commit.TypeFile, Json);
        Parse(next, commit.CategoryId, commit.TypeId);
        var previous = Read(TypePath(commit.CategoryId, commit.TypeId));
        using (var tx = db.BeginTransaction())
        {
            void Write(string sql, params (string Key, object Value)[] args)
            { using var command = Command(db, sql, args); command.Transaction = tx; command.ExecuteNonQuery(); }
            if (previous != next)
            {
                Write("INSERT INTO history(category_id,type_id,before_json,after_json,at) VALUES($c,$t,$b,$a,$at)",
                    ("$c", commit.CategoryId), ("$t", commit.TypeId), ("$b", previous), ("$a", next), ("$at", DateTimeOffset.UtcNow.ToString("O")));
                Write("INSERT INTO pending VALUES(1,$c,$t,$h,$j)", ("$c", commit.CategoryId), ("$t", commit.TypeId), ("$h", Hash(previous)), ("$j", next));
            }
            Write("INSERT INTO drafts VALUES($k,$j) ON CONFLICT(key) DO UPDATE SET json=$j", ("$k", commit.DraftKey), ("$j", commit.DraftJson));
            foreach (var audit in commit.Audits)
                Write("""
                    INSERT INTO audits VALUES($k,$s,$c,$b,$a,$actor,$at)
                    ON CONFLICT(key,sample) DO UPDATE SET correct=MIN(audits.correct,excluded.correct),
                    after_json=excluded.after_json,actor=excluded.actor,at=excluded.at
                    """, ("$k", audit.Key), ("$s", audit.Sample), ("$c", audit.Correct ? 1 : 0), ("$b", audit.Before),
                    ("$a", audit.After), ("$actor", audit.Actor), ("$at", DateTimeOffset.UtcNow.ToString("O")));
            tx.Commit();
        }
        try { Recover(db); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new IOException("商品和审核记录已保存，规则文件待写入；修复文件权限后重新加载会重试。", error); }
        return 0;
    });
    private void Recover(SqliteConnection db)
    {
        long category, type; string expected, json;
        using (var cmd = Command(db, "SELECT category_id,type_id,expected_hash,json FROM pending WHERE id=1"))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) return;
            category = reader.GetInt64(0); type = reader.GetInt64(1); expected = reader.GetString(2); json = reader.GetString(3);
        }
        var path = TypePath(category, type); var now = Read(path);
        if (now != json)
        {
            if (Hash(now) != expected) throw new IOException("待写入规则与手动修改的文件冲突，已保留两份内容，请处理 reviews.db 中的 pending 记录。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { var bytes = Encoding.UTF8.GetBytes(json); stream.Write(bytes); stream.Flush(true); }
                File.Move(temp, path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        Execute(db, "DELETE FROM pending WHERE id=1");
    }
    private T Locked<T>(Func<SqliteConnection, T> body, bool recover = true)
    {
        using var mutex = new Mutex(false, _mutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("规则库正被其他操作使用，请稍后重试。");
            using var db = new SqliteConnection(_connectionString); db.Open();
            if (recover) Recover(db);
            return body(db);
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }
    private static RuleFile Parse(string text, long category, long? type)
    {
        var file = text.Length == 0 ? new RuleFile(1, category, type, []) : JsonSerializer.Deserialize<RuleFile>(text, Json)
            ?? throw new InvalidDataException("规则文件不能为空对象。");
        if (file.SchemaVersion != 1 || file.CategoryId != category || file.TypeId != type || file.Rules is null)
            throw new InvalidDataException("规则版本或目录中的品类/类型标识不一致。");
        if (file.Rules.Any(r => r is null || string.IsNullOrWhiteSpace(r.RuleId)) || file.Rules.Select(r => r.RuleId).Distinct().Count() != file.Rules.Count)
            throw new InvalidDataException("规则 ID 缺失或重复。");
        foreach (var r in file.Rules)
        {
            if (r.Revision < 1 || r.Action is not ("add" or "override" or "disable") ||
                r.Status is not ("active" or "trusted" or "candidate" or "disabled" or "revalidation_required"))
                throw new InvalidDataException("规则版本、操作或状态无效。");
            if (r.Action == "disable") continue;
            if (r.AttributeId <= 0 || r.DictionaryId < 0 || r.Scope is not ("product" or "sku") ||
                r.Strategy is not ("direct" or "dictionary") || (r.DictionaryId > 0) != (r.Strategy == "dictionary") ||
                string.IsNullOrWhiteSpace(r.TargetFingerprint) || r.SourceLabels is null || r.SourceLabels.Count == 0 ||
                r.SourceLabels.Any(string.IsNullOrWhiteSpace) || r.Values is null)
                throw new InvalidDataException($"规则 {r.RuleId} 的来源、目标或处理方式无效。");
            if (r.Values.Select(v => v.MappingId).Distinct().Count() != r.Values.Count || r.Values.Select(v => v.SourceValue).Distinct().Count() != r.Values.Count)
                throw new InvalidDataException("值映射 ID 或源值重复。");
            foreach (var v in r.Values)
                if (string.IsNullOrWhiteSpace(v.MappingId) || v.Revision < 1 || string.IsNullOrWhiteSpace(v.SourceValue) ||
                    string.IsNullOrWhiteSpace(v.TargetValue) || v.Status is not ("active" or "trusted" or "candidate" or "disabled" or "revalidation_required") ||
                    (r.DictionaryId > 0 ? v.ValueId is not > 0 : v.ValueId is not null))
                    throw new InvalidDataException("值映射结构或字典 ID 无效。");
        }
        return file;
    }
    private static CommonRuleFile ParseCommon(string text, string fileName)
    {
        var file = JsonSerializer.Deserialize<CommonRuleFile>(text, Json)
            ?? throw new InvalidDataException($"公共规则文件 {fileName} 不能为空对象。");
        if (file.SchemaVersion != 1 || string.IsNullOrWhiteSpace(file.ModuleId) || file.Rules is null || file.Rules.Count == 0)
            throw new InvalidDataException($"公共规则文件 {fileName} 的版本、模块 ID 或规则为空。");
        var validated = Parse(JsonSerializer.Serialize(new RuleFile(1, 1, null, file.Rules), Json), 1, null);
        if (validated.Rules.Any(rule => rule.Action != "add" || rule.TargetFingerprint != "*"))
            throw new InvalidDataException($"公共规则文件 {fileName} 只能包含 action=add、targetFingerprint=* 的规则。");
        return file;
    }
    private string CommonPath() => Path.Combine(RootPath, "common");
    private string CategoryPath(long category) => Path.Combine(RootPath, "categories", category.ToString(), "rules.json");
    private string TypePath(long category, long type) { CheckIds(category, type); return Path.Combine(RootPath, "categories", category.ToString(), "types", type + ".json"); }
    private static void CheckIds(long category, long type) { if (category <= 0 || type <= 0) throw new ArgumentException("品类/类型 ID 无效。"); }
    private static string Read(string path) => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static SqliteCommand Command(SqliteConnection db, string sql, params (string Key, object Value)[] args)
    { var cmd = db.CreateCommand(); cmd.CommandText = sql; foreach (var a in args) cmd.Parameters.AddWithValue(a.Key, a.Value); return cmd; }
    private static void Execute(SqliteConnection db, string sql, params (string Key, object Value)[] args)
    { using var cmd = Command(db, sql, args); cmd.ExecuteNonQuery(); }
}
