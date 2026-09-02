using System.Globalization;
using System.Text;
using AutoMagic.Application.Ozon;

namespace AutoMagic.Application.Ozon.Mapping;

/// <summary>
/// Narrows Ozon's complete dictionary to values that exactly match the text
/// candidates produced by the first semantic pass. It intentionally does not
/// use fuzzy matching: an uncertain match must remain visible to a reviewer.
/// </summary>
public static class SemanticDictionaryCandidateResolver
{
    public static IReadOnlyList<SemanticDictionaryCandidate> Resolve(
        IReadOnlyCollection<string> candidateTexts,
        IReadOnlyCollection<OzonDictionaryValue> dictionaryValues,
        int maximumCandidates = 20)
    {
        ArgumentNullException.ThrowIfNull(candidateTexts);
        ArgumentNullException.ThrowIfNull(dictionaryValues);
        if (maximumCandidates <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        }

        var wanted = candidateTexts
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Normalize)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        return dictionaryValues
            .Where(value => value.ValueId > 0 && wanted.Contains(Normalize(value.Value)))
            .GroupBy(value => value.ValueId)
            .Select(group => group.First())
            .OrderBy(value => value.ValueId)
            .Take(maximumCandidates)
            .Select(value => new SemanticDictionaryCandidate(value.ValueId, value.Value))
            .ToArray();
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Trim().ToLower(CultureInfo.InvariantCulture));
        var normalized = new StringBuilder(builder.Length);
        foreach (var character in builder.ToString().Normalize(NormalizationForm.FormKC))
        {
            if (char.IsWhiteSpace(character) || char.IsPunctuation(character) || char.IsSymbol(character))
            {
                continue;
            }

            normalized.Append(character);
        }

        return normalized.ToString();
    }
}
