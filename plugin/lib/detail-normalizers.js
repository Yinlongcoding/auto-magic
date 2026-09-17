(() => {
  function normalizeColorOptions(rawValue) {
    return String(rawValue ?? '')
      .normalize('NFKC')
      .split(/[、,，;；|]/u)
      .map((sourceValue) => {
        const trimmed = sourceValue.trim();
        const match = trimmed.match(/^\s*\d*\s*(.*?)\s*(?:有现货|现货|缺货|无货|库存\s*\d+\s*(?:件|个)?)?\s*$/u);
        const normalizedValue = match?.[1]?.trim() ?? '';
        return {
          sourceValue: trimmed,
          normalizedValue: normalizedValue || null,
          status: normalizedValue ? 'normalized' : 'unresolved',
        };
      })
      .filter((option) => option.sourceValue);
  }

  function normalizeSimpleOptions(rawValue) {
    return String(rawValue ?? '')
      .normalize('NFKC')
      .split(/[、,，;；|]/u)
      .map((value) => value.trim())
      .filter(Boolean);
  }

  globalThis.__AUTO_MAGIC_DETAIL_NORMALIZERS__ = Object.freeze({
    normalizeColorOptions,
    normalizeSimpleOptions,
  });
})();
