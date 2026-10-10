# Keys

`NightClubModKit.keystore` + `keystore.json` are generated **once** on the first patch and reused
for every patched build, so new modded builds install over old ones without uninstalling.

**Back this folder up.** If it is lost, the next patched build has a different signature and
Android will require uninstalling the modded game (accessible data is backed up first).
It is git-ignored on purpose: never publish it.
