namespace AutoMagic.Application.Ozon.Rules;

/// <summary>Orders attributes so that "when.attribute" dependencies are cleaned first; rejects cycles.</summary>
public static class DependencyResolver
{
    public static IReadOnlyList<int> Order(IReadOnlyDictionary<int, AttributeRule> rules)
    {
        var sorted = new List<int>();
        var visiting = new HashSet<int>();
        var visited = new HashSet<int>();
        foreach (var id in rules.Keys.Order()) Visit(id, [], rules, visiting, visited, sorted);
        return sorted;
    }

    private static void Visit(int id, List<int> path, IReadOnlyDictionary<int, AttributeRule> rules,
        HashSet<int> visiting, HashSet<int> visited, List<int> sorted)
    {
        if (visited.Contains(id)) return;
        if (!visiting.Add(id))
            throw new InvalidOperationException($"规则存在循环依赖：{string.Join(" → ", path.SkipWhile(p => p != id).Append(id))}");
        path.Add(id);
        if (rules.TryGetValue(id, out var rule))
        {
            foreach (var dependency in rule.When.Where(w => w.Attribute.HasValue).Select(w => w.Attribute!.Value).Distinct())
            {
                if (!rules.ContainsKey(dependency))
                    throw new InvalidOperationException($"属性 {id} 的条件引用了 {dependency}，但当前类型没有该属性的规则。");
                Visit(dependency, path, rules, visiting, visited, sorted);
            }
        }
        path.RemoveAt(path.Count - 1);
        visiting.Remove(id);
        visited.Add(id);
        sorted.Add(id);
    }
}
