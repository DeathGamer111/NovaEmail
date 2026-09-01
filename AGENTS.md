# Repository guidance

- Keep the product and repository independent: do not add company-specific branding, copied mail, production credentials, private data, or proprietary migration code.
- Preserve the local-demo safety boundary. Staged messages remain in the local Outbox unless a later task explicitly authorizes live submission.
- Use synthetic addresses and fixtures in committed tests.
- Keep AI optional, user-configured, and consent-gated for every operation that processes message or draft content.
- Run the full solution build and tests before committing changes.
