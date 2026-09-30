using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutoMagic.Application.Ozon.Mapping;

public sealed class RuleReviewService(IRuleReviewStore store, IOzonDictionaryService dictionaries)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string RulesPath => store.RootPath;
    public static string Fingerprint(ProductMappingAttribute a) => Hash(JsonSerializer.Serialize(new
        { a.AttributeId, a.AttributeComplexId, a.Type, a.IsCollection, a.MaxValueCount, a.DictionaryId, a.IsRequired }));
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static bool Active(string status) => status is "active" or "trusted";
    private static bool TargetMatches(JsonFieldRule rule, ProductMappingAttribute attribute) =>
        rule.AttributeId == attribute.AttributeId && rule.DictionaryId == attribute.DictionaryId &&
        (rule.TargetFingerprint == "*" || rule.TargetFingerprint == Fingerprint(attribute));
    private static string Label(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c is not ':' and not '：')).ToUpperInvariant();
    private static string BindingKey(ProductMappingRequest q, JsonFieldRule r) =>
        $"{q.DescriptionCategoryId}/{q.TypeId}/{r.RuleId}/{r.Revision}/{r.TargetFingerprint}/" +
        Hash(JsonSerializer.Serialize(new { r.Scope, r.SourceLabels, r.Strategy, r.AttributeId, r.DictionaryId }));
    private static string ValueKey(ProductMappingRequest q, JsonFieldRule r, JsonValueRule v) =>
        BindingKey(q, r) + $"/{v.MappingId}/{v.Revision}/" +
        Hash(JsonSerializer.Serialize(new { v.SourceValue, v.DisplayZh, v.TargetValue, v.ValueId }));

    public async Task<RuleReviewSession> OpenAsync(FieldMatchingInput source, OzonTemporaryCredentials credentials, CancellationToken token)
    {
        var cleaned = CleanedProductBuilder.Create(source);
        var input = ProductMappingInputBuilder.Create(source);
        // Only confirmed scalar cleaned facts enter automatic matching; SKU facts stay scoped.
        var allowed = cleaned.ConfirmedFacts.Where(f => f.FactId is not null && f.RawValue is string)
            .Select(f => "product:" + f.FactId).ToHashSet(StringComparer.Ordinal);
        var request = input.Request with { Facts = input.Request.Facts.Where(f => f.ScopeKey != "product" ||
            allowed.Contains(f.FactId) || f.FactId == "product:title").ToArray() };
        input = input with { Request = request };
        if (input.SourceIssues.Any(i => i.Severity == "error"))
            throw new InvalidOperationException(string.Join("；", input.SourceIssues.Where(i => i.Severity == "error").Select(i => i.Message)));
        var bundle = store.Load(request.DescriptionCategoryId, request.TypeId);
        var rows = new List<RuleReviewRow>();
        var scopes = request.Skus.Count == 0 ? new[] { ("product", "商品公共属性") }
            : request.Skus.Select(s => (s.VariantKey, s.MerchantSku + " · " + string.Join(" / ", s.Options.Values))).ToArray();
        var dictionaryCache = new Dictionary<(long, string), IReadOnlyList<ProductDictionaryCandidate>>();
        foreach (var (scope, display) in scopes)
        foreach (var attribute in request.Attributes)
        {
            token.ThrowIfCancellationRequested();
            var facts = request.Facts.Where(f => f.ScopeKey == "product" || f.ScopeKey == scope)
                .Select(f => new ReviewFact(f.FactId, f.ScopeKey, f.Label, f.Value)).ToArray();
            var row = new RuleReviewRow { ScopeKey = scope, ScopeDisplay = display, Attribute = attribute, Facts = facts };
            rows.Add(row);
            var rules = bundle.EffectiveRules.Where(r => r.AttributeId == attribute.AttributeId && (scope != "product" || r.Scope == "product"))
                .OrderByDescending(r => r.Scope == "sku").ToArray();
            var rule = rules.FirstOrDefault(); // A SKU rule suppresses the product rule even when unresolved.
            var related = rule is null ? facts.Where(f => Label(f.Label) == Label(attribute.Name)).ToArray()
                : facts.Where(f => (rule.Scope == "product" ? f.ScopeKey == "product" : f.ScopeKey == scope && f.ScopeKey != "product") &&
                    rule.SourceLabels.Any(l => Label(l) == Label(f.Label))).ToArray();
            if (related.Length == 1) row.SourceFactId = related[0].FactId;
            if (rule is null) { row.Status = "无规则；请选择来源并填写"; continue; }
            if (related.Length != 1) { row.Status = related.Length == 0 ? "规则缺少来源事实" : "来源冲突，待人工确认"; continue; }
            var bindingKey = BindingKey(request, rule);
            var bindingCount = store.Counts(bindingKey);
            row.Statistics = "字段 " + bindingCount.Display;
            if (!Active(rule.Status) || bindingCount.LowConfidence || !TargetMatches(rule, attribute))
            { row.Status = "规则未启用、低可信或 Schema 已变化"; continue; }
            if (attribute.AttributeComplexId > 0 || attribute.IsCollection)
            { row.Status = "复杂/多值属性需人工按项填写"; continue; }
            var fact = related[0];
            var mapping = rule.Values.SingleOrDefault(v => v.SourceValue == fact.Value);
            if (mapping is not null)
            {
                var count = store.Counts(ValueKey(request, rule, mapping));
                row.Statistics += "；值 " + count.Display;
                if (!Active(mapping.Status) || count.LowConfidence) { row.Status = "值映射低可信或未启用"; continue; }
            }
            var target = mapping?.TargetValue ?? fact.Value;
            if (attribute.DictionaryId > 0)
            {
                var key = (attribute.AttributeId, target);
                try
                {
                    if (!dictionaryCache.TryGetValue(key, out var found))
                        dictionaryCache[key] = found = await SearchAsync(request, attribute, target, credentials, token);
                    var exact = found.Where(v => v.Text == target && (mapping is null || v.ValueId == mapping.ValueId)).ToArray();
                    if (exact.Length != 1) { row.Status = "字典未确认，目标值留空"; continue; }
                    row.InputText = mapping?.DisplayZh ?? fact.Value;
                    row.Candidates = found; row.CandidateQuery = row.InputText; row.SelectedCandidate = exact[0];
                }
                catch (OperationCanceledException) { throw; }
                catch { row.Status = "字典查询失败，目标值留空"; continue; }
            }
            else
            {
                if (!ValidScalar(attribute.Type, target)) { row.Status = "值类型不符，目标值留空"; continue; }
                row.InputText = target;
            }
            row.OriginalRuleId = rule.RuleId; row.OriginalBindingKey = bindingKey;
            row.OriginalMappingKey = mapping is null ? null : ValueKey(request, rule, mapping);
            row.OriginalInput = row.InputText; row.OriginalSourceFactId = row.SourceFactId;
            row.OriginalTarget = target; row.OriginalValueId = row.SelectedCandidate?.ValueId;
            row.Status = "自动匹配；勾选已核对后计入审核";
        }
        var draftKey = $"{request.DescriptionCategoryId}/{request.TypeId}/{request.SourceOfferId}";
        var session = new RuleReviewSession(input, bundle, draftKey, rows);
        var draftJson = store.LoadDraft(draftKey);
        if (draftJson is not null)
        {
            var draft = JsonSerializer.Deserialize<ReviewDraft>(draftJson, Json);
            if (draft?.Fingerprint == DraftFingerprint(request))
                foreach (var saved in draft.Rows)
                {
                    var row = rows.SingleOrDefault(r => r.ScopeKey == saved.ScopeKey && r.Attribute.AttributeId == saved.AttributeId);
                    if (row is null) continue;
                    row.SourceFactId = row.Facts.Any(f => f.FactId == saved.SourceFactId) ? saved.SourceFactId : "";
                    row.InputText = saved.InputText; row.SaveAsRule = saved.SaveAsRule;
                    row.ComplexInstanceKey = saved.ComplexInstanceKey;
                    if (row.IsDictionary && saved.ValueId is > 0 && !string.IsNullOrWhiteSpace(saved.TargetValue))
                    {
                        var restored = new ProductDictionaryCandidate(saved.ValueId.Value, saved.TargetValue);
                        row.Candidates = [restored]; row.CandidateQuery = row.InputText; row.SelectedCandidate = restored;
                    }
                    row.Reviewed = false; // Reopening a draft is not a fresh audit.
                    row.Status = "已恢复草稿；字典值需确认校验";
                }
        }
        return session;
    }
    public RuleReviewSession RefreshBundle(RuleReviewSession session) => session with { Bundle = store.Load(session.Input.Request.DescriptionCategoryId, session.Input.Request.TypeId) };
    public void SaveDraft(RuleReviewSession session) => store.SaveDraft(session.DraftKey, SerializeDraft(session));
    private static string DraftFingerprint(ProductMappingRequest q) => Hash(q.InputFingerprint + string.Join(";", q.Attributes.Select(Fingerprint)));
    private static string SerializeDraft(RuleReviewSession s, ProductMappingRun? result = null) => JsonSerializer.Serialize(new ReviewDraft(DraftFingerprint(s.Input.Request),
        s.Rows.Select(r => new ReviewDraftRow(r.ScopeKey, r.Attribute.AttributeId, r.SourceFactId, r.InputText,
            r.Reviewed, r.SaveAsRule, r.ComplexInstanceKey, r.SelectedCandidate?.Text, r.SelectedCandidate?.ValueId)).ToArray(), result), Json);

    public async Task SearchRowAsync(RuleReviewSession session, RuleReviewRow row, OzonTemporaryCredentials credentials, CancellationToken token)
    {
        if (!row.IsDictionary || string.IsNullOrWhiteSpace(row.InputText)) return;
        var input = row.InputText;
        row.SelectedCandidate = null;
        row.Candidates = await SearchAsync(session.Input.Request, row.Attribute, string.IsNullOrWhiteSpace(row.DictionarySearchText) ? input : row.DictionarySearchText.Trim(), credentials, token);
        row.CandidateQuery = input;
        row.Status = row.Candidates.Count == 0 ? "未找到候选；可输入字典原文搜索，不猜测 ID" : "请选择官方候选后再次确认";
    }
    private async Task<IReadOnlyList<ProductDictionaryCandidate>> SearchAsync(ProductMappingRequest q, ProductMappingAttribute a,
        string text, OzonTemporaryCredentials credentials, CancellationToken token) =>
        (await dictionaries.SearchAttributeValuesAsync(credentials, q.DescriptionCategoryId, q.TypeId, a.AttributeId, text, token))
        .Where(v => v.ValueId > 0 && !string.IsNullOrWhiteSpace(v.Value)).Select(v => new ProductDictionaryCandidate(v.ValueId, v.Value)).Distinct().ToArray();

    public async Task<RuleConfirmation> ConfirmAsync(RuleReviewSession session, OzonTemporaryCredentials credentials, CancellationToken token)
    {
        var q = session.Input.Request;
        var queryCache = new Dictionary<(long, string), IReadOnlyList<ProductDictionaryCandidate>>();
        var failedQueries = new HashSet<(long, string)>();
        async Task<IReadOnlyList<ProductDictionaryCandidate>> Query(ProductMappingAttribute attribute, string text)
        {
            var key = (attribute.AttributeId, text);
            if (failedQueries.Contains(key)) throw new InvalidOperationException("字典查询失败。");
            if (!queryCache.TryGetValue(key, out var found))
            {
                try { queryCache[key] = found = await SearchAsync(q, attribute, text, credentials, token); }
                catch { failedQueries.Add(key); throw; }
            }
            return found;
        }
        var current = store.Load(q.DescriptionCategoryId, q.TypeId);
        if (current.Token != session.Bundle.Token) throw new InvalidOperationException("规则已变化，请保存草稿并重新运行匹配。");
        var typeRules = current.TypeFile.Rules.ToDictionary(r => r.RuleId, StringComparer.Ordinal);
        var audits = new List<RuleAudit>();
        var facts = q.Facts.ToList();
        var candidates = q.Attributes.ToDictionary(a => a.AttributeId, a => new List<ProductDictionaryCandidate>());
        var outputs = new Dictionary<string, List<ProductAttributeSuggestion>>();
        var writes = new Dictionary<string, string>();
        var writtenIds = new HashSet<string>();
        var unresolved = 0;
        foreach (var row in session.Rows)
        {
            token.ThrowIfCancellationRequested();
            if (!outputs.TryGetValue(row.ScopeKey, out var mappings)) outputs[row.ScopeKey] = mappings = [];
            var a = row.Attribute;
            ProductAttributeSuggestion Blank(string reason) => new(a.AttributeId, null, ProductMappingStatuses.ManualRequired, [], [], reason);
            if (string.IsNullOrWhiteSpace(row.InputText))
            {
                mappings.Add(Blank("尚未填写或已清空")); unresolved++;
                if (row.Reviewed && row.SaveAsRule && row.OriginalRuleId is not null)
                {
                    var original = current.EffectiveRules.Single(r => r.RuleId == row.OriginalRuleId);
                    var source = row.Facts.SingleOrDefault(f => f.FactId == row.OriginalSourceFactId);
                    var value = original.Values.SingleOrDefault(v => v.SourceValue == source?.Value);
                    var clearIdentity = original.RuleId + "/" + source?.Value;
                    if (writes.TryGetValue(clearIdentity, out var clearExisting) && clearExisting != "__cleared__")
                        throw new InvalidOperationException("同一来源值不能同时清空和保存映射，请核对其他 SKU。");
                    writes[clearIdentity] = "__cleared__";
                    var suspended = value is null
                        ? original.Status == "revalidation_required" ? original : original with { Revision = original.Revision + 1, Status = "revalidation_required" }
                        : original with { Values = original.Values.Select(v => v == value && v.Status != "revalidation_required"
                            ? v with { Revision = v.Revision + 1, Status = "revalidation_required" } : v).ToArray() };
                    typeRules[original.RuleId] = suspended with { Action = "override" };
                    AddAudit(row, false, "清空并暂停");
                }
                continue;
            }
            if (!row.Reviewed && (row.InputText != row.OriginalInput || row.SourceFactId != row.OriginalSourceFactId || row.OriginalRuleId is null || (row.IsDictionary && row.SelectedCandidate is not null && row.SelectedCandidate.ValueId != row.OriginalValueId)))
            { mappings.Add(Blank("填写尚未勾选已核对")); unresolved++; continue; }
            string target = row.InputText.Trim(); long? valueId = null;
            if (a.DictionaryId > 0)
            {
                try
                {
                    var selected = row.SelectedCandidate;
                    if (selected is not null && (row.CandidateQuery != row.InputText || !row.Candidates.Contains(selected))) selected = null;
                    if (selected is null)
                    {
                        // Reuse the approved source-value mapping, never a guessed translation.
                        var fact = row.Facts.SingleOrDefault(f => f.FactId == row.SourceFactId);
                        var known = current.EffectiveRules.Where(r => TargetMatches(r, a) &&
                            Active(r.Status) && !store.Counts(BindingKey(q,r)).LowConfidence &&
                            (r.Scope == "product" ? fact?.ScopeKey == "product" : fact?.ScopeKey == row.ScopeKey && row.ScopeKey != "product") &&
                            r.SourceLabels.Any(l => Label(l) == Label(fact?.Label ?? "")))
                            .SelectMany(r => r.Values.Where(v => Active(v.Status) && !store.Counts(ValueKey(q,r,v)).LowConfidence &&
                                v.SourceValue == fact?.Value && v.DisplayZh == row.InputText)).ToArray();
                        var search = known.Length == 1 ? known[0].TargetValue : target;
                        row.Candidates = await Query(a, search);
                        row.CandidateQuery = row.InputText;
                        var exact = row.Candidates.Where(v => v.Text == search && (known.Length != 1 || v.ValueId == known[0].ValueId)).ToArray();
                        if (exact.Length == 1) selected = exact[0];
                    }
                    if (selected is null) { row.Status = "请明确选择官方候选；valueId 留空"; mappings.Add(Blank(row.Status)); unresolved++; continue; }
                    // Revalidate selected IDs against the live dictionary, including restored/manual selections.
                    var live = await Query(a, selected.Text);
                    if (!live.Contains(selected)) { row.SelectedCandidate = null; row.Status = "字典已变化，请重新选择"; mappings.Add(Blank(row.Status)); unresolved++; continue; }
                    row.SelectedCandidate = selected; target = selected.Text; valueId = selected.ValueId;
                    candidates[a.AttributeId].Add(selected);
                }
                catch (OperationCanceledException) { throw; }
                catch { row.SelectedCandidate = null; row.Status = "字典查询失败，未保存该字段规则"; mappings.Add(Blank(row.Status)); unresolved++; continue; }
            }
            if (a.DictionaryId <= 0 && !ValidScalar(a.Type, target))
            { row.Status = "值不符合目标数据类型"; mappings.Add(Blank(row.Status)); unresolved++; continue; }
            if (a.AttributeComplexId > 0 && string.IsNullOrWhiteSpace(row.ComplexInstanceKey))
            { row.Status = "复杂属性需要实例键"; mappings.Add(Blank(row.Status)); unresolved++; continue; }
            var sourceFact = row.Facts.SingleOrDefault(f => f.FactId == row.SourceFactId);
            var evidence = sourceFact?.FactId;
            if (evidence is null)
            {
                evidence = "manual:" + Hash(row.ScopeKey + "/" + a.AttributeId);
                facts.Add(new(evidence, row.ScopeKey, a.Name, target, "manual-reviewed", "manual-form"));
            }
            mappings.Add(new(a.AttributeId, a.AttributeComplexId > 0 ? row.ComplexInstanceKey.Trim() : null,
                ProductMappingStatuses.Suggested, [new(target, valueId)], [evidence],
                row.Reviewed ? "人工确认；" + (sourceFact is null ? "仅当前商品人工事实" : "保留来源事实") : "有效规则匹配"));
            row.Status = "已解析，待保存";
            if (!row.Reviewed) continue;
            var outcome = JsonSerializer.Serialize(new { target, valueId });
            AddAudit(row, target == row.OriginalTarget && valueId == row.OriginalValueId && row.SourceFactId == row.OriginalSourceFactId, outcome);
            if (!row.SaveAsRule || sourceFact is null || a.AttributeComplexId > 0 || a.IsCollection) continue;
            var scope = sourceFact.ScopeKey == "product" ? "product" : "sku";
            var old = current.EffectiveRules.SingleOrDefault(r => r.AttributeId == a.AttributeId && r.Scope == scope);
            var id = old?.RuleId ?? $"attribute-{a.AttributeId}-{scope}";
            var labels = new[] { sourceFact.Label };
            var bindingChanged = old is null || old.TargetFingerprint != Fingerprint(a) || !old.SourceLabels.Contains(sourceFact.Label) || !Active(old.Status) || store.Counts(BindingKey(q,old)).LowConfidence;
            var basis = old is not null && !bindingChanged ? old : new JsonFieldRule(id, (old?.Revision ?? 0) + 1,
                "override", "active", scope, labels, a.AttributeId, a.DictionaryId, Fingerprint(a), a.DictionaryId > 0 ? "dictionary" : "direct", []);
            var prior = basis.Values.SingleOrDefault(v => v.SourceValue == sourceFact.Value);
            var unchanged = prior is not null && prior.TargetValue == target && prior.ValueId == valueId && prior.DisplayZh == row.InputText && Active(prior.Status) && !store.Counts(ValueKey(q,basis,prior)).LowConfidence;
            var mapping = unchanged ? prior! : new JsonValueRule(prior?.MappingId ?? "value-" + Hash(sourceFact.Value)[..16],
                (prior?.Revision ?? 0) + 1, "active", sourceFact.Value, row.InputText, target, valueId);
            var identity = id + "/" + sourceFact.Value;
            var signature = JsonSerializer.Serialize(new { sourceFact.Label, target, valueId });
            if (writes.TryGetValue(identity, out var other) && other != signature)
                throw new InvalidOperationException("同一来源值在多个 SKU 中被填写为不同答案，请取消规则保存或先明确来源范围。");
            writes[identity] = signature;
            if (writtenIds.Contains(id) && typeRules.TryGetValue(id, out var already) && already.TargetFingerprint == basis.TargetFingerprint)
            {
                if (!already.SourceLabels.Contains(sourceFact.Label))
                    throw new InvalidOperationException("同一字段存在不同来源绑定，请分开确认并检查适用范围。");
                basis = already;
            }
            var updated = basis with { Action = "override", Status = "active", Values = basis.Values.Where(v => v.SourceValue != sourceFact.Value).Append(mapping).ToArray() };
            typeRules[id] = updated;
            writtenIds.Add(id);
            if (row.OriginalRuleId is not null)
            {
                var replaced = current.EffectiveRules.Single(r => r.RuleId == row.OriginalRuleId);
                if (replaced.Scope != scope)
                    typeRules[replaced.RuleId] = replaced with { Action = "disable", Revision = replaced.Revision + 1 };
            }
            var sample = Sample(sourceFact);
            // A reviewed correction is the first observation for the new version, not inherited history.
            audits.Add(new(BindingKey(q, updated), sample, true, "", outcome, Environment.UserName));
            audits.Add(new(ValueKey(q, updated, mapping), sample, true, "", outcome, Environment.UserName));
        }
        // One shared source-value rule cannot represent conflicting answers in the same save.
        foreach (var group in session.Rows.Where(r => r.Status == "已解析，待保存" && r.SourceFactId.Length > 0)
                     .GroupBy(r => (r.Attribute.AttributeId, Fact: r.Facts.Single(f => f.FactId == r.SourceFactId))))
        {
            if (group.Any(r => r.Reviewed && r.SaveAsRule) &&
                group.Select(r => r.IsDictionary ? r.SelectedCandidate!.ValueId.ToString() : r.InputText.Trim()).Distinct().Count() > 1)
                throw new InvalidOperationException("同一来源事实在多个 SKU 中填写不一致，请统一公共值或选择独立 SKU 来源。");
        }
        var request = q with { Facts = facts, Attributes = q.Attributes.Select(a => a with
            { DictionaryCandidates = candidates[a.AttributeId].Distinct().ToArray() }).ToArray() };
        var response = new ProductMappingResponse(q.RequestId, outputs.GetValueOrDefault("product") ?? [],
            q.Skus.Select(s => new ProductVariantSuggestion(s.VariantKey, outputs.GetValueOrDefault(s.VariantKey) ?? [])).ToArray(), []);
        var validation = ProductMappingValidator.Validate(request, response, session.Input.SourceIssues);
        if (!validation.ContractValid)
            throw new InvalidOperationException("表单未通过校验：" + string.Join("；", validation.Issues.Where(i => i.Severity == "error").Select(i => i.Message).Distinct()));
        token.ThrowIfCancellationRequested();
        var file = current.TypeFile with { Rules = typeRules.Values.OrderBy(r => r.RuleId, StringComparer.Ordinal).ToArray() };
        store.Commit(new(q.DescriptionCategoryId, q.TypeId, current.Token, file, session.DraftKey, SerializeDraft(session, new(request, response, validation)), audits));
        var savedBundle = store.Load(q.DescriptionCategoryId, q.TypeId);
        foreach (var row in session.Rows.Where(r => r.Status == "已解析，待保存"))
        {
            row.Status = "已保存";
            row.Reviewed = false;
            var fact = row.Facts.SingleOrDefault(f => f.FactId == row.SourceFactId);
            var rule = savedBundle.EffectiveRules.SingleOrDefault(r => r.AttributeId == row.Attribute.AttributeId &&
                (r.Scope == "product" ? fact?.ScopeKey == "product" : fact?.ScopeKey == row.ScopeKey && row.ScopeKey != "product"));
            var value = rule?.Values.SingleOrDefault(v => v.SourceValue == fact?.Value);
            row.OriginalInput = row.InputText; row.OriginalSourceFactId = row.SourceFactId;
            row.OriginalTarget = row.IsDictionary ? row.SelectedCandidate!.Text : row.InputText.Trim();
            row.OriginalValueId = row.SelectedCandidate?.ValueId;
            row.OriginalRuleId = rule?.RuleId;
            row.OriginalBindingKey = rule is null ? null : BindingKey(q, rule);
            row.OriginalMappingKey = rule is null || value is null ? null : ValueKey(q, rule, value);
            row.Statistics = row.OriginalMappingKey is null ? "仅当前商品" : "值 " + store.Counts(row.OriginalMappingKey).Display;
        }
        return new(new(request, response, validation), writes.Count, unresolved,
            $"已保存；本次处理 {writes.Count} 条值映射，{unresolved} 项未解决。未解决字段已保存填写进度，可选择候选后再次确认。仍不直接上架。");

        string Sample(ReviewFact fact) => q.SourceOfferId + "/" + (fact.ScopeKey == "product" ? "product" : fact.ScopeKey) + "/" + Hash(fact.Label + "\0" + fact.Value);
        void AddAudit(RuleReviewRow row, bool correct, string after)
        {
            var fact = row.Facts.SingleOrDefault(f => f.FactId == row.OriginalSourceFactId);
            if (fact is null) return;
            if (row.OriginalBindingKey is not null)
                audits.Add(new(row.OriginalBindingKey, Sample(fact), row.SourceFactId == row.OriginalSourceFactId, row.OriginalTarget, after, Environment.UserName));
            if (row.OriginalMappingKey is not null)
                audits.Add(new(row.OriginalMappingKey, Sample(fact), correct, row.OriginalTarget, after, Environment.UserName));
        }
    }
    private static bool ValidScalar(string type, string value) => type.Trim().ToLowerInvariant() switch
    {
        "string" or "text" => !string.IsNullOrWhiteSpace(value),
        "integer" or "int" or "int32" or "int64" => long.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _),
        "decimal" or "number" or "float" or "double" => decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _),
        "boolean" or "bool" => value is "true" or "false",
        _ => false,
    };
}
