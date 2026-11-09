# Verification policy

Use behavior tests at clock, process, transport and storage boundaries. Verify source and artifact identity, build the intended Windows Release outputs, and run the complete offline checks before pushing. For scoped failures, trace affected dependencies and rerun relevant checks on the final state. Never treat a failed command as passing because a focused rerun succeeded. Report missing or skipped checks separately.

Once the validation driver and hooks are in place, the commit gate validates an isolated snapshot of the exact staged tree. Commit verification must fail on an incomplete run or a changed staged tree. Keep local evidence distinct from PR or main CI evidence. The first rules-only bootstrap precedes the product's validation driver. No pre-push hook repeats the local checks.

Real provider validation requires explicit authorization for the exact disposable test item and procedure. Ordinary tests do not require a signed-in vault, a real SSH connection, changes to SSH settings or administrative privileges. Never publish credential-bearing output or process dumps containing secrets.
