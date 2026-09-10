# Nova Email

Nova Email is a private, local-first desktop email client demo for Windows 11. It combines standards-based IMAP and SMTP account support with offline drafts, a safe local Outbox, MIME handling, attachments, search, contacts, a local calendar, and optional consent-gated AI assistance.

The demo never submits staged messages. Choosing **Send** persists a complete MIME message—including locally selected attachments—in the local Outbox, so compose and reply workflows can be exercised without contacting an SMTP server.

After saving an account in **Settings**, **Refresh** discovers selectable IMAP folders and imports mail through read-only IMAP operations. Folder identities, UIDVALIDITY checkpoints, message flags, MIME bodies, and attachments are retained in the local store so repeated synchronization reconciles mail without duplicating it. The Inbox is projected into the main mail view, and other retained server folders appear lazily in the sidebar. HTML messages are sanitized before display, remote resources and in-message navigation remain blocked, and verified attachments can be saved as uniquely named local copies.

## Build and run

Prerequisites:

- Windows 11 (build 22621 or later)
- .NET SDK 10.0.401

From `Modern`:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' restore NovaEmail.slnx
& 'C:\Program Files\dotnet\dotnet.exe' build NovaEmail.slnx --no-restore
& 'C:\Program Files\dotnet\dotnet.exe' run --project src\NovaEmail.Client\NovaEmail.Client.csproj --no-build
```

Mail passwords and AI API keys are stored in Windows Credential Manager. Application data is stored under `%LOCALAPPDATA%\NovaEmail` and is excluded from this repository.

## Safety boundary

- No production credentials, private mail, or copied mailbox data belong in the repository.
- AI content processing requires a per-request consent checkbox and an explicit action.
- IMAP synchronization is user-triggered and receive-only; SMTP submission is disabled in the demo.
- Remote content and MIME inputs are treated as untrusted.
- Deployment and production-server testing require separate explicit approval.
