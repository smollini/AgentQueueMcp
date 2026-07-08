# TODO / roadmap

Quality backlog — nothing here blocks current operation.

- [x] **.gitattributes** — stop the LF/CRLF warning noise on every commit (`* text=auto`).
- [ ] **Dead-letter queue** — move a task with `dequeueCount > N` (e.g. 5) to `deadletter-<agent>`
  instead of letting it reappear forever; one poisoned task must not loop a worker.
  Surface dead-lettered counts in `agents_health`.
- [ ] **CI on Azurite** — GitHub Actions workflow: spin up the Azurite emulator, run
  `test/e2e.mjs` against it on every push. No secrets needed; adds a badge and guards
  future changes.
- [ ] **`dotnet tool` packaging** — publish as a global tool so installing an agent becomes
  `dotnet tool install` instead of clone + build.
- [ ] **Blob offload for payloads > 64 KiB** — store oversized `brief`/`output` in a blob
  container and pass a reference in the envelope; transparent on both ends.
- [ ] **Versioning** — set a real version in the `.csproj` (assembly + MCP server info) and
  tag releases (current state ≈ v0.2.0: addressed envelopes, health, heartbeats, watchers).
