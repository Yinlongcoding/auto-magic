using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace AutoMagic.Application.Ozon.Mapping;

public sealed record RuleFile(int SchemaVersion, long CategoryId, long? TypeId, IReadOnlyList<JsonFieldRule> Rules);
public sealed record CommonRuleFile(int SchemaVersion, string ModuleId, IReadOnlyList<JsonFieldRule> Rules);
public sealed record JsonFieldRule(string RuleId, int Revision, string Action, string Status,
    string Scope, IReadOnlyList<string> SourceLabels, long AttributeId, long DictionaryId,
    string TargetFingerprint, string Strategy, IReadOnlyList<JsonValueRule> Values);
public sealed record JsonValueRule(string MappingId, int Revision, string Status,
    string SourceValue, string DisplayZh, string TargetValue, long? ValueId);
public sealed record RuleBundle(string Token, RuleFile TypeFile, IReadOnlyList<JsonFieldRule> EffectiveRules);
public sealed record ReviewCount(int Checked, int Correct)
{
    public bool Trusted => Checked >= 100 && Correct * 100L >= Checked * 98L;
    public bool LowConfidence => Checked >= 100 && !Trusted;
    public string Display => $"{Correct}/{Checked}" + (Trusted ? " · 可信" : LowConfidence ? " · 待复核" : " · 积累中");
}
public sealed record RuleAudit(string Key, string Sample, bool Correct, string Before, string After, string Actor);
public sealed record ReviewCommit(long CategoryId, long TypeId, string ExpectedToken, RuleFile TypeFile,
    string DraftKey, string DraftJson, IReadOnlyList<RuleAudit> Audits);
public interface IRuleReviewStore
{
    string RootPath { get; }
    RuleBundle Load(long categoryId, long typeId);
    ReviewCount Counts(string key);
    string? LoadDraft(string key);
    void SaveDraft(string key, string json);
    void Commit(ReviewCommit commit);
}
public sealed record ReviewFact(string FactId, string ScopeKey, string Label, string Value)
{
    public string Display => $"{Label}：{Value}";
}
public sealed record ReviewDraftRow(string ScopeKey, long AttributeId, string SourceFactId, string InputText,
    bool Reviewed, bool SaveAsRule, string ComplexInstanceKey, string? TargetValue = null, long? ValueId = null);
public sealed record ReviewDraft(string Fingerprint, IReadOnlyList<ReviewDraftRow> Rows, ProductMappingRun? ConfirmedResult = null);

public sealed class RuleReviewRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public required string ScopeKey { get; init; }
    public required string ScopeDisplay { get; init; }
    public required ProductMappingAttribute Attribute { get; init; }
    public required IReadOnlyList<ReviewFact> Facts { get; init; }
    public string AttributeName => Attribute.Name;
    public bool IsRequired => Attribute.IsRequired;
    public bool IsDictionary => Attribute.DictionaryId > 0;
    private string _sourceFactId = "";
    public string SourceFactId { get => _sourceFactId; set { if (_sourceFactId == value) return; _sourceFactId = value ?? ""; ClearSelection(); Changed(); } }
    private string _inputText = "";
    public string InputText { get => _inputText; set { if (_inputText == value) return; _inputText = value ?? ""; ClearSelection(); Changed(); } }
    private bool _reviewed;
    public bool Reviewed { get => _reviewed; set { _reviewed = value; Changed(); } }
    public bool SaveAsRule { get; set; } = true;
    public string DictionarySearchText { get; set; } = "";
    public string ComplexInstanceKey { get; set; } = "";
    private string _status = "待填写";
    public string Status { get => _status; set { _status = value; Changed(); } }
    private IReadOnlyList<ProductDictionaryCandidate> _candidates = [];
    public IReadOnlyList<ProductDictionaryCandidate> Candidates { get => _candidates; set { _candidates = value; Changed(); } }
    private ProductDictionaryCandidate? _selected;
    public ProductDictionaryCandidate? SelectedCandidate { get => _selected; set { _selected = value; Changed(); Changed(nameof(ValueIdDisplay)); } }
    public string ValueIdDisplay => SelectedCandidate?.ValueId.ToString() ?? "";
    private string _statistics = "尚无审核统计";
    public string Statistics { get => _statistics; set { _statistics = value; Changed(); } }
    public string? OriginalRuleId { get; set; }
    public string? OriginalBindingKey { get; set; }
    public string? OriginalMappingKey { get; set; }
    public string OriginalInput { get; set; } = "";
    public string OriginalSourceFactId { get; set; } = "";
    public string OriginalTarget { get; set; } = "";
    public long? OriginalValueId { get; set; }
    public string CandidateQuery { get; set; } = "";
    public void ClearSelection() { SelectedCandidate = null; Candidates = []; CandidateQuery = ""; Status = "待确认解析"; }
}
public sealed record RuleReviewSession(ProductMappingInput Input, RuleBundle Bundle, string DraftKey,
    IReadOnlyList<RuleReviewRow> Rows);
public sealed record RuleConfirmation(ProductMappingRun Run, int SavedRules, int Unresolved, string Message);
