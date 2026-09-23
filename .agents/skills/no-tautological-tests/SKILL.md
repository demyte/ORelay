---
name: no-tautological-tests
description: Use before adding or changing automated tests, when deciding what regression coverage a change needs, or when reviewing test quality. Do not invoke solely to run existing tests.
---

# No tautological tests

Apply this guidance to tests within the current task. It does not authorize a suite-wide audit or deletion of unrelated tests.

Before adding a test, identify what could realistically go wrong in production behaviour or an explicit contract, and which assertion would catch it. If there is no useful answer, skip the test. Inspect nearby tests first and extend existing coverage when it already exercises the relevant behaviour.

## Choose independent assertions

- Derive expected results from requirements, worked examples, or documented contracts. Do not copy the implementation's algorithm into the test or call the same production helper to compute both actual and expected values.
- Exercise real production code. Do not merely assert that a mock returns its configured value, that a fixture contains values assigned by the test, or that a test-only implementation behaves as written.
- Mock dependencies at boundaries and assert the resulting behaviour. Interaction assertions are useful when the interaction itself is a required contract, such as ensuring a rejected request never triggers a payment.
- Prefer observable outcomes. Avoid source-text searches, private-method checks, or snapshots that merely freeze today's code structure unless that structure is itself an explicit requirement. A static contract check or snapshot can be valuable when it catches a concrete contract violation.

For example, comparing `calculateTotal(order)` with another call to `calculateTotal(order)` proves nothing. Given a documented 10% discount, asserting that the real calculation turns a 100-unit subtotal into 90 units provides an independent expectation. Asserting only that a mocked calculator returns the configured 90 units does not test the calculation.

## Keep coverage proportionate

- Cover meaningful behaviour, failure paths, boundaries, or regressions. Do not add tests solely to increase coverage or test counts, duplicate coverage without a distinct failure mode, or test trivial getters and framework behaviour without a specific risk.
- For a bug fix, prefer a focused test that fails for the original defect and passes with the fix. Where practical, verify that failure without disturbing unrelated changes. Do not claim to have observed it unless you did.
- Run the relevant checks. Do not introduce mutation-testing infrastructure or a broader test campaign solely to apply this skill.
- When existing checks are sufficient, explain that briefly. When reviewing tests, identify the missing defect detection and suggest a concrete improvement. Do not treat every mock, snapshot, or simple assertion as low-value.
