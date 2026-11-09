# Go and WinUI maintenance policy

Follow the [Windows agent specification](../specs/windows-agent.md) and its linked decisions for behavior and compatibility. Use the toolchains pinned by [go.mod](../../go.mod) and [global.json](../../global.json). Keep Go changes formatted with `gofmt` and follow the surrounding C# conventions.

## Go core

- Keep peer identity, approval policy, private key handling, signing and public index persistence in the core. Preserve the kernel-backed identity checks, PID and creation-time matching, and private control channel binding to the supervised process and pending request.
- Check size, shape and concurrency limits before allocating or accepting external work. Pass context and cancellation through calls and waits. Where a native call can outlive its waiter, discard late results and check current ownership and generation before publication.
- Preserve the independent lifetimes of provider permission, loaded material and approvals. Dispose signer-owned resources, wipe mutable key buffers the application owns and expose safe error categories. Persist only the state allowed by the specification, including compatibility with existing configuration.

## WinUI tray

- Keep process supervision and user interaction in the tray. Keep WinUI changes on the owning dispatcher and tie timers, callbacks, subscriptions and pending operations to their process or window lifetime. Dispose them on close, failure and shutdown. Retire callbacks before a replacement takes ownership, and preserve the original failure if cleanup also fails.
- Keep tray preferences separate from core settings and approvals. Preserve existing values during changes to saved settings. Update both English and Simplified Chinese strings when changing visible behavior, and exercise the affected theme, localization or lifecycle paths.
- Preserve safe diagnostics across the Go and tray boundary. Keep raw provider errors, private material, credentials and personal paths out of maintained configuration, fixtures and evidence.
