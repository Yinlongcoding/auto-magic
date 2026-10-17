# Auto Magic repository instructions

## Development direction and simplicity

- Build an explicit JSON mapping engine: each type plan lists its complete attribute IDs and module references. Human-defined modules under common or dictionary own source bindings, conversions, defaults, and value mappings. Resolve only explicit references, including dictionary-to-common chains; do not restore implicit common/category/type inheritance or fallback searches. Reuse verified dictionaries with attribute, dictionary, and applicability checks.
- Unknown, ambiguous, or low-confidence values remain blank for human review. Do not restore AI/Qwen inference as a fallback.
- AI is limited to free text and images: Russian title, description, color name (10097), and image processing. AI output never feeds dictionary matching or attribute inference. Auto mode skips only confirmation steps; blocking items still stop the product for human input.
- Follow the confirmed pipeline in docs/pipeline-design.md. Changes to decisions recorded there need the user's confirmation and an update to that document.
- Before adding or changing a feature, inspect its entry points and references. Remove obsolete implementations, unused dependencies, misleading UI, and superseded documentation within the affected scope.
- Keep one active implementation for each workflow. Do not retain dead compatibility wrappers, provider settings, prompts, or parallel engines for hypothetical future use. Git history preserves retired code.
- Preserve collected data, credentials, rule files, review history, and actively used validation and normalization logic during cleanup. Never equate a legacy name with unused behavior.
- Validate cleanup with a solution build and relevant existing checks; report what was removed and any remaining limitations. Prefer focused behavior checks over tests that duplicate configuration values.
- Human review should group shared product attributes and repeated source values; do not introduce SKU-by-field editing as the primary workflow.

## Rule change reporting

Whenever a change adds, modifies, promotes, demotes, disables, or removes a mapping rule, explicitly report that rule change to the user in the same task's final response.

This requirement applies to:

- common semantic modules;
- category profiles;
- type-specific overrides;
- Ozon attribute bindings;
- SKU validity and normalization rules;
- variant grouping policies;
- deterministic conversion rules;
- rule precedence, fallback, and applicability behavior.

The report must identify the affected rule or module, its scope, the behavior before and after the change, and the validation performed. Do not hide rule changes inside a general code-change summary.
