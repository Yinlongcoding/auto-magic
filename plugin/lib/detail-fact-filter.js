(() => {
  if (globalThis.__AUTO_MAGIC_DETAIL_FACT_FILTER__) return;

  const DIMENSION_LABEL = /^(?:颜色|颜色分类|色彩|尺码|规格|型号)$/u;
  const MATRIX_MEASURE = /^(?:重量|净重|毛重|尺寸|长度|宽度|高度|体积)(?:\s*\([^)]*\)|\s*[（][^）]*[）])?$/iu;
  const NUMERIC_MEASURE = /^\s*\d+(?:\.\d+)?\s*(?:g|kg|克|千克|cm|mm|厘米|毫米)?\s*$/iu;

  function filterMatrixFacts(candidates) {
    const valuesByDimension = new Map();
    for (const candidate of candidates ?? []) {
      const label = clean(candidate?.label);
      const values = splitValues(candidate?.value);
      if (!DIMENSION_LABEL.test(label) || values.length < 2 || values.some((value) => MATRIX_MEASURE.test(value))) continue;
      const known = valuesByDimension.get(label) ?? new Set();
      for (const value of values) known.add(value);
      valuesByDimension.set(label, known);
    }

    const matrixOptions = new Set();
    const headers = new Set();
    for (const candidate of candidates ?? []) {
      const label = clean(candidate?.label);
      const value = clean(candidate?.value);
      if (!DIMENSION_LABEL.test(label) || !MATRIX_MEASURE.test(value)) continue;
      headers.add(candidate);
      for (const option of valuesByDimension.get(label) ?? []) matrixOptions.add(option);
    }

    if (headers.size === 0) return Array.from(candidates ?? []);
    return Array.from(candidates ?? []).filter((candidate) => {
      if (headers.has(candidate)) return false;
      const label = clean(candidate?.label);
      const value = clean(candidate?.value);
      return !(matrixOptions.has(label) && NUMERIC_MEASURE.test(value));
    });
  }

  function splitValues(value) {
    return clean(value).split(/[,，、;/|]+/u).map(clean).filter(Boolean);
  }

  function clean(value) {
    return String(value ?? '').replace(/\s+/gu, ' ').trim();
  }

  globalThis.__AUTO_MAGIC_DETAIL_FACT_FILTER__ = Object.freeze({ filterMatrixFacts });
})();
