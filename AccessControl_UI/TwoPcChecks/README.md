# Two-PC transport integration checks

These checks target Linux with .NET 10. They exercise production TLS transports, remote API, media agent, recording/clip contracts, transfer code and server coordinator. Database methods and Python clip execution are test doubles in Stubs.cs; real PostgreSQL SQL was checked separately and Windows codec/camera/DPAPI are not exercised here.

Use a temporary server data root so test clip/cache files never mix with application data:

```bash
access_test_data=$(mktemp -d)
XDG_DATA_HOME="$access_test_data" dotnet run --project TwoPcChecks/TwoPcChecks.csproj
rm -rf -- "$access_test_data"
```

The agent child process uses another temporary data root. Both communicate through actual TLS sockets on loopback; this is not a test of school LAN routing. Stop any other test instance before running. The command removes only the freshly created test directory after completion.

12.2.3 extends the checks with recovered terminal clip failures, durable FAILED reasons and DB completion, interrupted encoder output replacement, and same-PC client-temp/server-copy separation. SQL checks in `DbChecks/recording_upsert.mjs` run the production UPSERT in an isolated PGlite PostgreSQL WASM engine (not the school DB).

12.3 adds `--gallery-only` (catalog/cache/snapshot and observation policy checks) and `--gallery-bridge` (production C# AiClient and Python worker with a synthetic camera/model fixture). The bridge test requires ACCESS_GALLERY_BRIDGE_TEST=1, ACCESS_FACEAI_ROOT pointing to FaceAI, ACCESS_TEST_PYTHON pointing to a Python executable, and PYTHONPATH containing FaceAI plus NumPy. No real camera or model inference is used. `DbChecks/gallery_catalog.mjs` executes the actual production catalog query in isolated PGlite PostgreSQL WASM.
