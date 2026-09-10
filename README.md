# Nova Email

Nova Email is an independent, local-first desktop email client demo for Windows 11. It provides a modern three-pane mail interface, standards-based IMAP synchronization, safe MIME rendering, local drafts and Outbox staging, contacts, calendar tools, customizable themes, and optional consent-gated AI assistance.

The project is intentionally a controlled demo rather than a production mail client. Clicking **Send** stores a complete MIME message in the local Outbox; it does **not** submit the message to an SMTP server.

## Project status

- Target platform: Windows 11, build 22621 or later
- Application framework: WinUI 3 and Windows App SDK
- Runtime: .NET 10, pinned to SDK 10.0.401
- Architecture: x64
- Test status: 309 automated tests passing at the time of this update
- Distribution: source build; no installer or signed release package yet

Windows 10 compatibility has not been validated and is not currently a project target.

## What the demo can do

### Mailbox experience

- Display Inbox, Drafts, Outbox, Sent, Archive, Trash, and Junk views.
- Discover and expose synchronized server folders.
- Search the active folder or local Contacts and Calendar views.
- Select one or multiple messages and apply local Archive, Trash, Junk, read, and unread state.
- Retain local mailbox presentation state and user-created filter labels across restarts.
- Switch between wide three-pane and compact mailbox layouts.
- Open a searchable contacts side panel while composing.

### Reading mail

- Parse RFC MIME messages with bounded input handling.
- Prefer readable plain text when available.
- Sanitize HTML before displaying it in WebView2.
- Block remote resources and in-message navigation by default.
- List verified attachments and save a copy using a collision-safe local filename.
- Surface synchronized read and flagged state in local views.

### Composing mail

- Create new messages with To, Cc, Bcc, Subject, and From fields.
- Reply, Reply All, or Forward from the reading pane.
- Draft inline replies and expand them into the full composer.
- Apply basic rich-text formatting, fonts, sizing, colors, highlighting, alignment, lists, and indentation.
- Include plain text, optional HTML, and an RTF alternative in the staged MIME message.
- Add or remove attachments.
- Set message priority and request a read receipt.
- Save versioned local drafts.
- Stage complete messages in the protected local Outbox.

### Contacts, calendar, and appearance

- Create, edit, search, and retire local contacts.
- Create, edit, search, and retire local calendar events.
- Use system, light, dark, and several color or seasonal themes.
- Persist the selected theme locally.

### Optional AI assistance

- Summarize a selected message.
- Suggest one or more replies.
- Draft an inline reply.
- Revise a composed draft for review.
- Connect to an OpenWebUI-compatible chat-completions endpoint.

AI is disabled until the user configures it. Every operation that includes message or draft content requires a fresh, explicit consent checkbox and action. Consent is scoped to that operation and is cleared afterward.

## Safety boundaries

Nova Email is designed so the demo can be exercised without accidentally sending mail or leaking repository data.

- **No live submission:** the client UI never dispatches staged Outbox messages to SMTP.
- **Receive-only synchronization:** user-triggered IMAP synchronization reads and retains mail; restored mailbox actions affect local presentation state and do not mutate the server.
- **TLS required:** mail endpoints support implicit TLS or required STARTTLS. Plaintext fallback is not offered.
- **Protected credentials:** mail passwords and AI API keys are stored in Windows Credential Manager, not in JSON settings or the repository.
- **Bounded local storage:** MIME, attachment, theme, and presentation-state inputs have explicit limits.
- **Safe HTML:** HTML is sanitized, remote resources are blocked, and navigation from message content is rejected.
- **Controlled AI endpoints:** AI requires a credential-free HTTPS origin. Loopback endpoints may use a custom port; public endpoints must use the default HTTPS port and pass network-boundary validation.
- **Synthetic committed data:** the checked-in mail corpus and automated tests use reserved synthetic addresses and fixtures.
- **No production data in Git:** database files, secrets, certificates, environment files, browser profiles, and generated state are ignored.

Do not commit real credentials, private mail, production database files, or exported user data.

## Prerequisites

- Windows 11, build 22621 or newer
- x64 processor and operating system
- [.NET SDK 10.0.401](https://dotnet.microsoft.com/download)
- WebView2 Runtime, normally included with current Windows 11 installations
- An IMAP account with TLS and a password or provider-issued app password, if testing synchronization
- An OpenWebUI-compatible HTTPS endpoint and API key, if testing AI features

The repository pins the SDK in `Modern/global.json` and verifies the selected SDK during restore and build.

## Build and run

Open PowerShell in the repository root, then run:

```powershell
Set-Location .\Modern
dotnet restore .\NovaEmail.slnx
dotnet build .\NovaEmail.slnx -c Debug --no-restore
dotnet run --project .\src\NovaEmail.Client\NovaEmail.Client.csproj --no-build
```

The built executable is placed under:

```text
Modern\src\NovaEmail.Client\bin\Debug\net10.0-windows10.0.22621.0\win-x64\NovaEmail.Client.exe
```

The `windows10.0.22621.0` target-framework suffix identifies the Windows SDK contract version. The application is currently validated only on Windows 11.

## Run the tests

From the `Modern` directory:

```powershell
dotnet test .\NovaEmail.slnx -c Debug --no-restore
```

The suite covers:

- MIME parsing, outbound composition, attachments, TLS policy, and safe local Outbox staging
- HTML sanitization and theme behavior
- SQLite storage, immutable history, protected paths, contacts, calendars, and mailbox presentation state
- IMAP folder discovery, synchronization checkpoints, mutation primitives, and scripted protocol failures
- AI endpoint restrictions, consent enforcement, bounded prompts, output validation, and OpenWebUI transport behavior
- Synthetic golden-corpus parsing and rendering

No production mail server or AI service is required for the automated suite.

## Try the local demo

1. Start Nova Email. The Inbox opens with synthetic messages.
2. Select a message to exercise Reply, Reply All, Forward, attachment display, and the reading pane.
3. Choose **Compose**, enter a reserved test address such as `recipient@example.test`, and create a draft.
4. Choose **Save Draft** to retain it locally, or **Send** to stage it in Outbox.
5. Open **Outbox** and confirm the staged message is present. No SMTP connection is attempted.
6. Try Archive, Trash, Junk, read/unread actions, labels, contacts, calendar events, and themes; their local state survives a restart.

## Configure IMAP synchronization

Open **Settings** and enter:

- Display name and sender address
- IMAP host and port
- SMTP host and port for retained account metadata
- Login name
- Password or app password
- Implicit TLS or required STARTTLS for each endpoint

Choose **Save account** to store the profile and place its secret in Windows Credential Manager. Choose **Test IMAP** to validate the receive connection. The **Refresh** command performs explicit receive-only synchronization, imports Inbox mail, and discovers other server folders.

Provider OAuth flows are not implemented. Accounts that reject basic authentication require a provider-issued app password or future OAuth support.

Although SMTP configuration and hardened transport components exist in the solution, the desktop demo deliberately does not invoke live SMTP submission.

## Configure AI assistance

Nova Email currently uses an OpenWebUI-compatible chat-completions endpoint.

1. Open **Settings**.
2. Enter an absolute HTTPS origin, such as `https://localhost:3000/`.
3. Enter the model identifier. The default is `qwen2.5:7b`.
4. Enter the API key and choose **Save AI settings**.
5. Select or compose a message.
6. Check the consent box for that specific operation.
7. Choose Summarize, Suggest Replies, Draft Response With AI, or Polish With AI.
8. Review all generated content before saving or staging it.

Public AI hosts must use HTTPS on port 443. Direct non-loopback IP addresses and public DNS names that resolve into private, loopback, link-local, documentation, multicast, or otherwise prohibited ranges are rejected.

## Local data and secrets

Application data is stored beneath:

```text
%LOCALAPPDATA%\NovaEmail
```

Important locations include:

| Data | Location or mechanism |
| --- | --- |
| Mail database and content-addressed MIME/attachments | `%LOCALAPPDATA%\NovaEmail\Data` |
| Non-secret client settings | `%LOCALAPPDATA%\NovaEmail\Settings\client.json` |
| Theme preference | `%LOCALAPPDATA%\NovaEmail\theme.json` |
| Mailbox view state and labels | `%LOCALAPPDATA%\NovaEmail\mailbox-state.json` |
| Mail passwords | Windows Credential Manager |
| AI API key | Windows Credential Manager, in a separate Nova Email secret namespace |

The repository `.gitignore` excludes databases, private keys, certificates, environment files, browser profiles, test state, build output, and local application state.

## Repository layout

```text
NovaEmail/
├─ Modern/
│  ├─ src/
│  │  ├─ NovaEmail.Client/        WinUI 3 desktop application
│  │  ├─ NovaEmail.Domain/        Shared domain records
│  │  ├─ NovaEmail.Mail/          MIME, attachments, and secure mail transport
│  │  ├─ NovaEmail.Rendering/     Safe HTML rendering and themes
│  │  ├─ NovaEmail.Storage/       SQLite and content-addressed local storage
│  │  ├─ NovaEmail.Sync/          IMAP and calendar synchronization components
│  │  ├─ NovaEmail.Intelligence/  Consent-aware email intelligence workflows
│  │  ├─ NovaEmail.Assistant/     Guarded OpenWebUI transport
│  │  └─ NovaEmail.Safety/        Credentials, endpoint, path, and certificate policy
│  ├─ tests/                      Six automated xUnit test projects
│  ├─ tools/                      AI demo and performance probes
│  └─ NovaEmail.slnx             Solution entry point
└─ TestLab/
   └─ fixtures/synthetic/         Synthetic RFC mail corpus
```

## Architecture notes

| Project | Responsibility |
| --- | --- |
| `NovaEmail.Client` | Desktop shell, mailbox navigation, reading, composing, settings, contacts, calendar, themes, and user-triggered workflows |
| `NovaEmail.Domain` | Shared data contracts used across layers |
| `NovaEmail.Mail` | Secure endpoint models, MailKit transport abstractions, MIME composition/parsing, recipient resolution, and attachment export |
| `NovaEmail.Rendering` | HTML sanitization, readable fallback generation, and application theme catalog/persistence |
| `NovaEmail.Storage` | Protected SQLite database, immutable revisions, content-addressed blobs, Outbox operations, backups, and local presentation state |
| `NovaEmail.Sync` | IMAP folder catalog and snapshots, synchronization checkpoints, mutation/Outbox dispatch primitives, and calendar import components |
| `NovaEmail.Intelligence` | Consent records, bounded AI requests, structured response validation, draft revision, and calendar evidence scanning |
| `NovaEmail.Assistant` | Network-constrained OpenWebUI adapter |
| `NovaEmail.Safety` | Windows credential vaults, endpoint and certificate policy, safe filesystem roots, and development audit controls |

Dependencies flow from the UI into focused libraries rather than placing protocol, storage, and safety logic directly in the window code. Package versions are centrally pinned and lock files are committed for repeatable restore behavior.

## Current limitations

- Live SMTP submission is disabled in the desktop demo.
- IMAP synchronization is receive-only from the UI.
- OAuth account enrollment is not implemented.
- Windows 10 compatibility is not claimed or tested.
- Only the Windows x64 target is configured.
- Calendar provider components exist, but the current desktop workflow focuses on local calendar editing.
- There is no installer, auto-update channel, crash reporting service, or production deployment pipeline.
- This is not yet a production-ready replacement for a fully supported mail client.

## Development rules

- Keep the repository independent and free of organization-specific branding or migration code.
- Use only synthetic fixtures and reserved example addresses in committed tests.
- Preserve local-only Outbox staging unless live submission is introduced as a separately reviewed feature.
- Keep AI optional, user-configured, bounded, and consent-gated for every content-processing request.
- Treat messages, MIME, HTML, attachments, remote folders, AI responses, and provider data as untrusted input.
- Run the full solution build and test suite before committing changes.

## Near-term roadmap

Likely next milestones are:

1. Package the Windows 11 demo for repeatable installation.
2. Add provider-specific OAuth enrollment while retaining generic IMAP support.
3. Expand UI-level automation for compose, reply, labels, themes, and responsive layouts.
4. Decide on an explicit, reviewable path from local Outbox staging to optional live SMTP submission.
5. Validate additional providers and larger synthetic interoperability corpora.

Live sending, deployment, telemetry, and any use of real mailbox data should remain separate, explicit decisions.
