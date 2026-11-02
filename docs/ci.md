# CI

`.github/workflows/ci.yml` runs for pushes to `main` and on demand from the Actions tab (`CI` → `Run workflow`). A newer run for the same ref cancels the older one. Two independent jobs run on `windows-2022` and `windows-2025`, each with a 25-minute timeout:

1. Install .NET SDK 10.0.x (the app and test projects target `net10.0-windows10.0.19041.0`) and restore packages from the NuGet cache.
2. Build the solution in Release with warnings treated as errors.
3. Run the xUnit suite with a 180-second hang dump/timeout. Hardware opt-in tests remain skipped because CI does not set `LYCORIS_HW_TESTS`.
4. Launch the built app's UI self-test in Russian and English. It explores pages and checks UI bindings, crashes, security inputs, concurrency, and the fake update flow. Self-test locks system-changing operations and runs without elevation; page loading can still inspect host capabilities, so a failure should be checked against its report before attributing it to a code regression.
5. On job failure, upload any generated self-test reports for seven days.

## Cost and failures

Every qualifying push consumes two Windows runner jobs; the matrix intentionally checks both Windows images. GitHub-hosted Windows minutes count at twice the Linux rate, so budget roughly `2 × (build + tests + two UI self-tests)` Windows minutes per run, multiplied by the account's current Windows-minute price. Actual usage varies with cache hits and queue/runtime; the Actions run's billing details are authoritative. Concurrency cancels superseded runs on the same ref.

Open the failed job, expand the first red step, and inspect its output. A test hang includes a blame dump; a self-test failure has an artifact named for its Windows image containing `report.md`, `report.json`, and page screenshots when those were produced. Compare the failing stage and page across both Windows jobs to distinguish a platform-specific issue from a shared regression.

## Run locally

From the repository root in PowerShell, the equivalent checks are:

```powershell
dotnet build Lycoris.slnx -c Release --warnaserror
dotnet test tests/Lycoris.Tests/Lycoris.Tests.csproj -c Release --no-build --blame-hang --blame-hang-timeout 180s
$exe = 'src/Lycoris/bin/Release/net10.0-windows10.0.19041.0/win-x64/Lycoris.exe'
& $exe --selftest (Join-Path $env:TEMP 'lycoris-selftest-ru') --no-elevate
if ($LASTEXITCODE -ne 0) { throw "Russian self-test failed: $LASTEXITCODE" }
& $exe --selftest (Join-Path $env:TEMP 'lycoris-selftest-en') --lang en --no-elevate
if ($LASTEXITCODE -ne 0) { throw "English self-test failed: $LASTEXITCODE" }
```

Run the manual workflow in GitHub from **Actions → CI → Run workflow**, selecting the branch to check.
