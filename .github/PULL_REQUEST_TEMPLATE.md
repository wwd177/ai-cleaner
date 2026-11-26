## Summary
Briefly describe the motivation and changes in this PR.

## Safety & Verification Checklist
- [ ] Built cleanly with `dotnet publish .\src\AICleaner.csproj -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true`
- [ ] Passed all built-in self-tests (`AICleaner.exe --self-test` reports `SELF_TEST_RESULT=PASS` and 0 `[FAIL]`)
- [ ] Verified CLI JSON contract (`--list-zones --json`, `--dry-run --json --zone claude-code`)
- [ ] Confirmed no personal paths, usernames, or private secrets are included in the diff
- [ ] Confirmed any new cleanable target respects NTFS junction protection, process locks, and redline boundaries
