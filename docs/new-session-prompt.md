# 新会话开场 prompt

在 VS Code 里打开 D:/ys-project/AM 再开新会话（会话开头显示的 git 状态才是 AM 的），然后把下面整段粘贴进去。

---

我们在重新实现 Auto Magic（Ozon 批量上架桌面工具），新仓库是 D:/ys-project/AM。设计在 D:/ys-project/auto-magic-design，旧仓库 D:/ys-project/auto-magic 只作参考。

请按这个顺序读，不要整目录读入：

1. D:/ys-project/AM/AGENTS.md
2. D:/ys-project/AM/docs/dev-plan.md
3. D:/ys-project/auto-magic-design/INDEX.md
4. D:/ys-project/auto-magic-design/19-ui-design.md（界面以它为准）

其他设计文档（03、09、10、13、16、18 等）需要时再单独读。

当前进度以 docs/dev-plan.md 为准：界面、Ozon 设置与类目缓存、真实 1688 采集、定价都已完成并经我确认（2026-10-23）。下一步从 dev-plan 的"下一步候选"里选，先问我做哪一个。

要求：

- 我不喜欢旧项目的界面，不要模仿它。
- 每次只问我一个问题，并给出你的建议。你可以对我的想法提出异议。
- 实现中发现与设计文档不符的地方，先告诉我，不要自己改设计文档。
- 做完每个模块先构建通过再汇报。没有验证过的东西要说明没验证。
- 不要提交 git，除非我明确要求。
- 不要读 ozon-category-tree.test.json 的全文，也不要读 bin/、obj/。
- 动作涉及我的真实 Ozon 卖家后台、真实 1688 采集或密钥时，先问我。
