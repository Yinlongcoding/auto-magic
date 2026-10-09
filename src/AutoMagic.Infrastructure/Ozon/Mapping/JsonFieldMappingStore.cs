using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AutoMagic.Application.Ozon.Mapping;
using Microsoft.Data.Sqlite;

namespace AutoMagic.Infrastructure.Ozon.Mapping;

/// <summary>Loads only explicit references. The existing review database is preserved without rule writes.</summary>
public sealed class JsonFieldMappingStore : IFieldMappingStore
{
    public static readonly JsonSerializerOptions Json = new(ProductMappingJson.IndentedOptions) {
        RespectRequiredConstructorParameters = true
    };
    private static readonly Regex ModulePath = new(@"^(common|dictionary)(?:/[A-Za-z0-9_-]+(?:/[A-Za-z0-9_-]+)*\.json|(?:\.[A-Za-z0-9_-]+)+)$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Statuses = ["active", "trusted", "candidate", "disabled", "revalidation_required"];
    public string RootPath { get; }
    private readonly string _connectionString;
    private readonly string _mutexName;
    public JsonFieldMappingStore(string root)
    {
        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); Directory.CreateDirectory(RootPath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(RootPath,"reviews.db") }.ToString();
        _mutexName = "AutoMagic.Rules." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RootPath.ToUpperInvariant())));
        WithDatabase(db => {
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS drafts (key TEXT PRIMARY KEY, json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS audits (key TEXT NOT NULL, sample TEXT NOT NULL, correct INTEGER NOT NULL,
                  before_json TEXT NOT NULL, after_json TEXT NOT NULL, actor TEXT NOT NULL, at TEXT NOT NULL, PRIMARY KEY(key,sample));
                CREATE TABLE IF NOT EXISTS history (id INTEGER PRIMARY KEY, category_id INTEGER, type_id INTEGER,
                  before_json TEXT NOT NULL, after_json TEXT NOT NULL, at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS pending (id INTEGER PRIMARY KEY CHECK(id=1), category_id INTEGER NOT NULL,
                  type_id INTEGER NOT NULL, expected_hash TEXT NOT NULL, json TEXT NOT NULL);
                """;
            command.ExecuteNonQuery(); return 0;
        });
    }

    public FieldMappingBundle Load(long categoryId, long typeId)
    {
        if (categoryId <= 0 || typeId <= 0) throw new ArgumentException("品类/类型 ID 无效。");
        WithDatabase(db => {
            using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM pending";
            if (Convert.ToInt64(command.ExecuteScalar()) > 0)
                throw new InvalidDataException("审核数据库存在旧版待写入规则；已原样保留。请先处理 pending 记录，避免覆盖新版类型引用清单。");
            return 0;
        });
        var path = Path.Combine(RootPath,"categories",categoryId.ToString(),"types",typeId + ".json");
        var plan = File.Exists(path) ? Read<TypeFieldPlan>(path) : new(2,categoryId,typeId,[]);
        if (plan.SchemaVersion != 2 || plan.DescriptionCategoryId != categoryId || plan.TypeId != typeId || plan.Attributes is null ||
            plan.Attributes.Any(a => a is null || a.Id <= 0 || !ValidPath(a.Path)) ||
            plan.Attributes.Select(a => a.Id).Distinct().Count() != plan.Attributes.Count)
            throw new InvalidDataException("类型方案须为版本 2；属性 ID 必须唯一，路径只能引用 common/ 或 dictionary/ 下的 JSON 模块。");
        var fields = new List<LoadedFieldModule>();
        foreach (var reference in plan.Attributes)
        {
            var normalized = Normalize(reference.Path);
            var modulePath = Path.GetFullPath(Path.Combine(RootPath,normalized.Replace('/',Path.DirectorySeparatorChar)));
            var prefix = Path.EndsInDirectorySeparator(RootPath) ? RootPath : RootPath + Path.DirectorySeparatorChar;
            if (!modulePath.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("模块路径超出规则目录。");
            if (!File.Exists(modulePath))
            { fields.Add(new(reference,null,"引用模块不存在，留空")); continue; }
            var module = Read<FieldRuleModule>(modulePath);
            Validate(module,reference);
            var issue = module.DescriptionCategoryId is not null && module.DescriptionCategoryId != categoryId ||
                module.TypeId is not null && module.TypeId != typeId ? "模块不适用于当前品类/类型，留空" :
                module.Status is not ("active" or "trusted") ? "模块未启用，留空" : null;
            var (values,valueIssue,trace) = ResolveValues(module,reference,categoryId,typeId);
            fields.Add(new(reference,module with { Values=values },issue,valueIssue,trace));
        }
        return new(plan,fields);
    }

    public ReviewCount Counts(string key) => WithDatabase(db => {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(correct),0) FROM audits WHERE key=$key";
        command.Parameters.AddWithValue("$key",key);
        using var reader = command.ExecuteReader(); reader.Read(); return new ReviewCount(reader.GetInt32(0),reader.GetInt32(1));
    });

    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path,Encoding.UTF8),Json)
        ?? throw new InvalidDataException($"配置文件为空：{path}");
    private static void Validate(FieldRuleModule module, FieldModuleReference reference)
    {
        if (module.SchemaVersion != 1 || module.ModuleId != Normalize(reference.Path)[..^5] || module.Revision < 1 ||
            !Statuses.Contains(module.Status) || module.DictionaryId < 0 || module.Binding is null ||
            module.Binding.Id != reference.Id || string.IsNullOrWhiteSpace(module.Binding.Name) || module.Values is null ||
            string.IsNullOrWhiteSpace(module.TargetFingerprint) ||
            module.DescriptionCategoryId is <= 0 || module.TypeId is <= 0 ||
            module.TypeId is not null && module.DescriptionCategoryId is null)
            throw new InvalidDataException($"模块版本、身份、属性或适用范围无效：{reference.Path}");
        FieldBindingPreview.ValidateBindings([module.Binding]);
        ValidateValues(module.Values,module.DictionaryId,module.ValuePath,reference.Path);
    }
    private static void ValidateValues(IReadOnlyList<DictionaryValueMapping> values,long dictionaryId,string? valuePath,string path)
    {
        if (values is null || values.Any(v => v is null || string.IsNullOrWhiteSpace(v.MappingId) || v.Revision < 1 ||
            !Statuses.Contains(v.Status) || string.IsNullOrWhiteSpace(v.DisplayZh) || v.DisplayZh != v.DisplayZh.Trim() ||
            string.IsNullOrWhiteSpace(v.TargetValue) || v.ValueId <= 0) ||
            values.Select(v => v.MappingId).Distinct().Count() != values.Count ||
            dictionaryId == 0 && (values.Count > 0 || valuePath is not null) ||
            valuePath is not null && (values.Count > 0 || !ValidPath(valuePath)))
            throw new InvalidDataException($"值映射无效；普通字段不能配置 valueId，引用不能与本地值并存：{path}");
    }
    private static string Normalize(string path) => path.EndsWith(".json",StringComparison.Ordinal)
        ? path : path.Replace('.','/') + ".json";
    private static bool ValidPath(string? path) => path is not null && ModulePath.IsMatch(path) &&
        (path.Contains('/') || !path.EndsWith(".json",StringComparison.Ordinal));

    private (IReadOnlyList<DictionaryValueMapping> Values,string? Issue,IReadOnlyList<ValueModuleTrace> Trace)
        ResolveValues(FieldRuleModule root,FieldModuleReference reference,long categoryId,long typeId)
    {
        var trace = new List<ValueModuleTrace>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Normalize(reference.Path) };
        trace.Add(new(reference.Path,root.Revision,Fingerprint(new { root.Values,root.ValuePath,root.DictionaryId })));
        var next=root.ValuePath; var values=root.Values;
        while (next is not null)
        {
            var normalized=Normalize(next);
            if (!visited.Add(normalized)) return ([],"字典引用存在循环，留空",trace);
            if (visited.Count > 8) return ([],"字典引用超过 8 层，留空",trace);
            var path=Path.Combine(RootPath,normalized.Replace('/',Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return ([],"字典引用断链，留空："+next,trace);
            var module=Read<ValueDictionaryModule>(path);
            if (module.SchemaVersion!=1 || module.ModuleId!=normalized[..^5] || module.Revision<1 ||
                !Statuses.Contains(module.Status) || module.AttributeId<=0 || module.DictionaryId<=0 ||
                module.DescriptionCategoryId is <=0 || module.TypeId is <=0 ||
                module.TypeId is not null && module.DescriptionCategoryId is null)
                throw new InvalidDataException("字典模块身份、版本或适用范围无效："+next);
            ValidateValues(module.Values,module.DictionaryId,module.ValuePath,next);
            trace.Add(new(next,module.Revision,Fingerprint(module)));
            if (module.AttributeId!=reference.Id || module.DictionaryId!=root.DictionaryId)
                return ([],"引用链属性或字典 ID 不一致，留空",trace);
            if (module.DescriptionCategoryId is not null && module.DescriptionCategoryId!=categoryId ||
                module.TypeId is not null && module.TypeId!=typeId)
                return ([],"引用字典不适用于当前品类/类型，留空",trace);
            if (module.Status is not ("active" or "trusted")) return ([],"引用字典未启用，留空",trace);
            values=module.Values; next=module.ValuePath;
        }
        return (values,null,trace);
    }
    private static string Fingerprint<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,Json))));
    private T WithDatabase<T>(Func<SqliteConnection,T> body)
    {
        using var mutex = new Mutex(false,_mutexName); var acquired = false;
        try {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("规则库正被其他操作使用，请稍后重试。");
            using var db = new SqliteConnection(_connectionString); db.Open(); return body(db);
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }
}
