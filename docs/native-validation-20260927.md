# Native validation — 27 September 2026

Assistant-written evidence summary. Valheim 1.0.16 on a Windows dedicated server and instrumented client, using disposable world copies. ValheimCLI core and all four packs were built from the reviewed source. Game preview.8 supplied the external actors. Plugin/world pins were explicit; per-command strict preflights and the persistent game-side guard stayed enabled.

| Boundary | Result and scope |
| --- | --- |
| Strict reload driver | A → B → absent; cancelled old read-only command, removed-command refusal, fresh owner, resource cleanup, unchanged core/connection. |
| Wrong environment | Deliberately wrong core hash refused a logout before execution; the client remained in the same world. |
| Capture/replay | Four generator samples and four loaded-ground samples; public save/load exact replay round trips passed. An unloaded ground grid was refused without generator fallback. The loaded ground remained identical after verified server save/restart/rejoin. |
| Session controls | Wrong password refused; correct password then joined despite stale password-error state. Explicit world repinning, protection, leave and menu repinning passed. Both structured save and `cli_save` confirmed advanced save numbers. |
| Mod readiness | Native objects/socket existed while MWL held its load; both save paths refused then. They succeeded after MWL reported registration ready and world loaded. Native readiness is not mod readiness. |
| Retired mutating owner | A controlled 12-second effect registered a quiescence predicate. ScriptEngine replacement cancelled its iterator early but retained its mutation gate and owner until settlement. A queued mutation waited, read-only state answered, cleanup ran, and the replacement then registered. This tests native scheduling and ownership with a controlled effect, not cancellation of every native save/join implementation. |
| Roads paint | The saved paved fixture passed again. A new width-8 dirt fixture matched 20 server height/collider and 16 paint samples. The client, with Roads/MWL absent, matched dirt core/fade/verge RGBA before and after save/restart/rejoin. Unchanged-input negative expectations failed 12 of 16 samples (eight core, four fade); four untouched verge samples passed. Maximum positive residual was 0.000753 RGBA. Alpha and untouched verge were preserved. |
| MWL ports | Full-mode 5.1.4: fee charged exactly once, delivery opened, different real character denied, same shipment accepted for the original owner after rejoin. Adapter fixes and exact limits live in MWL’s guide. No production MWL change. |
| MWL menu boundary | Strict adapter install, aliases, no-world refusal, missing-MWL observation, removal and stable core passed. Full-mode port evidence is tracked in MWL's own adapter guide. |

Roads' dirt fixture fixes the target profile and disables noise, batter and fill spread. Its independent fade expectation is 0.352 at the sampled texel; it does not derive expectations from the writer. These results do not establish arbitrary terrain generation, visual blending or walking usability. The dirt client's floating/support observations were not counted as a walking/support pass. Paved stationary support remains separately established.

The port campaign exposed adapter fixture/observer defects, handled in MWL: detached item data needed an explicit prefab reference, and the open-delivery flag moved from a field to saved ZDO state. The shared library does not absorb those domain rules.

Remaining: human appearance/walking review, ordinary noisy earthwork appearance, and public binary/NuGet release packaging. Synthetic worlds still model declared inputs; they are not native Unity or networking emulators. Private logs, credentials, accounts, worlds and captured inputs are not published.


## Artifact and log scope

ValheimCLI core MD5 `058d033fb155d7ac5381250063bf613d` was unchanged through the native checks. The Roads production fixture used MD5 `bf79a8aae7909b7920172987eb60070e`; full MWL used `c73a65fbfcb89febd57834e659706d9f`. Updated test adapters were Roads `c796b83df250625448293336ee2143fc` and MWL `8d4425964e8aa429795212122fe529e9`. These results validate the toolkit/adapters against those artifacts, not an untested production release.

Logs were retained and reviewed by message. Each client boot had BepInEx's Unity-log-writer startup error, before plugin execution. Warnings included BepInEx target versions, audio/lifecycle messages, missing-location warnings on the MWL-absent client, Jötunn ambiguities/mock failures and full-mode asset/shader issues. Expected refused saves and pin transitions were recorded too. Full-mode missing `MWL_StoneOutlook1` configuration and `8_PuzzleStand` definitions were not repaired or declared harmless by these tests. No clean-log or visual-quality certification is claimed.

Restoration verified 422 original client-file hashes and 15 source-fixture-file hashes. The owned client/server processes were stopped and the test machine released. No production server was changed.
