# 新会话开场 prompt

把下面整段粘贴到新会话。

---

我们在重新实现 Auto Magic（Ozon 批量上架桌面工具），新仓库是 D:/ys-project/AM。设计在 D:/ys-project/auto-magic-design，旧仓库 D:/ys-project/auto-magic 只作参考。

请按这个顺序读，不要整目录读入：

1. D:/ys-project/AM/AGENTS.md
2. D:/ys-project/AM/docs/dev-plan.md
3. D:/ys-project/auto-magic-design/INDEX.md
4. D:/ys-project/auto-magic-design/19-ui-design.md（界面以它为准）

其他设计文档（03、09、10、13、16、18 等）需要时再单独读。

当前进度以 docs/dev-plan.md 为准：M0、M1 已完成，M3 真实采集已跑通（2026-10-22）。下一步见 dev-plan。

要求：

- 开始 M1 之前，先问我界面风格。我不喜欢旧项目的界面，不要模仿它。
- 每次只问我一个问题，并给出你的建议。你可以对我的想法提出异议。
- 实现中发现与设计文档不符的地方，先告诉我，不要自己改设计文档。
- 做完每个模块先构建通过再汇报。没有验证过的东西要说明没验证。
- 不要提交 git，除非我明确要求。
- 不要读 ozon-category-tree.test.json 的全文，也不要读 bin/、obj/。
- 动作涉及我的真实 Ozon 卖家后台、真实 1688 采集或密钥时，先问我。
