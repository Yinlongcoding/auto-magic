# AM repository instructions

- Design documents live in D:/ys-project/auto-magic-design. Read INDEX.md first, then only the documents you need. UI design is in 19-ui-design.md.
- The old repository D:/ys-project/auto-magic is a reference only. Do not copy its UI. Copy low-level code (protocol, services, engines) only when needed, and check it for dependencies on the old flow.
- Never read `ozon-category-tree.test.json` (any copy) in full. Query it only with `jq` or `grep` limited by `-m` or piped through `head`.
- Do not read build output under `bin/` or `obj/`.
