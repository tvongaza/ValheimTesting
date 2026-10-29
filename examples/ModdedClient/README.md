# Dedicated server and modded client

Use the existing foreground `valheim-test server-load` owner to check a consumer mod on a dedicated server and one joining client. No toolkit test assembly belongs in either production plugin set. The [two-role command example](../../docs/packages/Valheim.Testing.NativeSmoke.md#dedicated-server-and-modded-client) shows distinct builds of the same plugin GUID, separate config/companion bytes and a shared non-secret environment value.

Choose one read-only command implemented by your mod, with independently fixed role/version/config expectations. Supply the server command/prefix and client command/prefix separately. Confirm `SERVER_JOIN_PASS` and inspect each actor's strict pins and retained logs. As a negative control, use a fresh output and an impossible client prefix: the run must fail its client-labelled step and still stop only its owned actors. Server success must never substitute for client success.

Run the documented preflight before an authorized native check and `valheim-test env status` afterward. No native result is included in this example. A prepared plan proves no actor joined; a passing observation proves only what that observation asserts. The default without explicit client selectors remains a CLI-only client.
