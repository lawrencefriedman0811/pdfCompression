# Matt Pocock Skills

A collection of agent skills (slash commands and behaviors) loaded. Skills are organized into buckets and consumed by per-repo configuration.

## Language

**Issue tracker**:
The tool that hosts a repo's issues — GitHub Issues, Linear, a local `.scratch/` markdown convention, or similar. Skills like `to-issues`, `to-prd`, `triage`, and `qa` read from and write to it.
_Avoid_: backlog manager, backlog backend, issue host

**Issue**:
A single tracked unit of work inside an **Issue tracker** — a bug, task, PRD, or slice produced by `to-issues`.
_Avoid_: ticket (use only when quoting external systems that call them tickets)

**Triage role**:
A canonical state-machine label applied to an **Issue** during triage (e.g. `needs-triage`, `ready-for-afk`). Each role maps to a real label string in the **Issue tracker** via `docs/agents/triage-labels.md`.

## Relationships

- An **Issue tracker** holds many **Issues**
- An **Issue** carries one **Triage role** at a time

## Flagged ambiguities

- "backlog" was previously used to mean both the *tool* hosting issues and the *body of work* inside it — resolved: the tool is the **Issue tracker**; "backlog" is no longer used as a domain term.
- "backlog backend" / "backlog manager" — resolved: collapsed into **Issue tracker**.

## K-2 Relationship Agent language

**Processing run**:
A single button-triggered pass that starts with workbook submission and ends when results are returned to the callback.
_Avoid_: job, execution, workflow invocation

**Proposal row**:
A single output row written to `(K.03) Relationship Proposals` for one target Part II/Part X input row.
_Avoid_: match recommendation, assignment record, resolution row

**Manual override**:
A case where a user-provided Schedule K line override is present and the row is surfaced for confirmation rather than auto-rewritten.
_Avoid_: locked row, user-forced mapping, pinned mapping

**Cell-level reconciliation**:
The tie-out check that compares summed proposals to a specific K.01 active-row/activity-column amount within tolerance.
_Avoid_: line reconciliation, variance check, balance validation

**Escalated row**:
An amount-bearing Part II or Part X row that could not be resolved deterministically and is forwarded for LLM reasoning.
_Avoid_: unmatched row, LLM candidate, fallback row

**Amount-bearing row**:
A Part II/Part X input row whose total amount field is nonzero and therefore in scope for matching.
_Avoid_: data row, populated row

## K-2 Relationship Agent relationships

- A **Processing run** emits many **Proposal rows**
- A **Processing run** considers only **Amount-bearing rows** for matching scope
- A **Proposal row** may be marked as **Manual override**
- An **Escalated row** is one input path that can produce a **Proposal row**
- A **Processing run** is successful only when **Cell-level reconciliation** passes for all required cells
