# Instruction position and task execution: paper evidence

Status: frozen

Checked: 2026-11-10. Question: can changing instruction position change the task an LLM performs? The literature supports position-sensitive instruction following, including controlled inference experiments. It does not establish the internal cause of the local DeepSeek/MAF summary failure or a universally best placement.

## Direct inference evidence

**Chen et al., “The SIFo Benchmark,” Findings of EMNLP 2024.** The relevant experiment is Section 3 and Figure 2, before the main sequential benchmark. Researchers construct 20 constrained-writing tasks, each with six parallel requirements, and test every permutation: `20 × 6! = 14,400` prompts. The task requirements stay the same; their order changes. Existing Llama2-7B-Chat, Mistral-7B-Instruct-v0.2, and Llama3-8B-Instruct models run at temperature zero. Constraint compliance varies with position, and the preferred positions vary by model and constraint type. This is direct inference-time evidence that instruction order affects execution, rather than evidence about retrieving a fact buried in a document. It manipulates order within an instruction list, not chat roles or a summary system message around historical turns. The preliminary study has only 20 underlying tasks; its 14,400 permutations are not 14,400 independent task families. [Paper, pp. 1693–1694, Section 3/Figure 2](https://aclanthology.org/2024.findings-emnlp.92.pdf#page=3).

**Li et al., “Evaluating the Instruction-Following Robustness of Large Language Models to Prompt Injection,” EMNLP 2024.** Section 4.3/Figure 3 moves injected benign questions to the start, middle, or end of context; end placement is hardest to defend. More directly, Section 4.4/Figure 5 changes the legitimate question from before context to after it at inference. Context–question–answer ordering is generally easier to defend than question–context–answer, with GPT-3.5-Turbo an exception. The setup explicitly instructs models to ignore instructions inside XML-delimited context. This shows that placement can affect which request gets answered even when the intended boundary is stated. Limits: adversarial question answering, eight older models, and no DeepSeek V4.1 or MAF summarizer. [Paper, Sections 4.3–4.4, especially p. 564](https://aclanthology.org/2024.emnlp-main.33.pdf#page=8).

## Supporting training evidence

**Liu et al., “Instruction Position Matters in Sequence Generation with Large Language Models,” Findings of ACL 2024.** The method changes instruction fine-tuning examples from instruction–input–response to input–instruction–response and evaluates translation and summarization. On CNN/DailyMail, Table 5 reports BLOOMZ-3B ROUGE-1 increasing from 35.41 to 38.90; LLaMA-7B increases from 37.55 to 38.11. These are summary-quality metrics, not percentages of successful task switching. The full paper confirms that training format changes: it is not an inference-only reorder of one fixed deployed model, nor an experiment changing a `system` message's position. It supports the relevance of instruction placement but cannot supply our route's effect size. [Paper, Sections 3–5 and Table 5, p. 11658](https://aclanthology.org/2024.findings-acl.693.pdf#page=7).

## Boundaries for the local diagnosis

Position means where the same instruction appears. Role means whether it is encoded as `system`, `user`, or another role. Data framing means whether old dialogue is presented as live conversational turns or quoted material to summarize. These are separate interventions.

Our [ordering controls](../ops/2026-11-10-native-summary-order-controls.md) preserve the instruction's wording and system role while moving it last; both full-history trials then summarize. The two-message, approximately 177-input-token failure also prevents attributing the defect solely to long-context degradation. The papers support the observed phenomenon; they do not prove attention decay, a particular gateway transformation, or reliable production repair.

[“Lost in the Middle,” TACL 2024](https://aclanthology.org/2024.tacl-1.9/) is indirect background: its principal manipulation moves answer-bearing information in question-answering and key-value retrieval contexts. That is distinct from moving an instruction and observing which task is executed.
