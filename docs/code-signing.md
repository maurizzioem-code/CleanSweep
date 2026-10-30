# Code signing

## Code signing policy

CleanSweep's Windows program (`CleanSweep.exe`) is built only by this repository's GitHub Actions workflow
(`.github/workflows/csharp.yml`) on GitHub-hosted Windows machines, from the source code in this repository.
A build is only published after every self-test step has passed.

- **Signing:** builds are signed with Azure Artifact Signing once it is set up (see below). Until then builds are unsigned.
- **Who can change the code and release it:** the repository owner, [maurizzioem-code](https://github.com/maurizzioem-code).
- **Privacy:** CleanSweep does not send any information to other networked systems unless you ask it to.
  The only network use is the daily update check (it reads `update/csharp.json` from this repository and
  downloads an update only when you click Install update) and the Network page's tests (they ping 1.1.1.1 and
  look up a few websites only when you click Test).
- **Updates check the signature:** once CleanSweep is signed, it refuses an update that is unsigned, has a broken
  signature or is signed by a different publisher, in addition to checking the SHA-256 fingerprint, product name and build.

## What signing does and doesn't do

- Windows shows the verified publisher name in the administrator prompt instead of "Unknown publisher".
- SmartScreen ("Windows protected your PC") is based on reputation. A signed app can still be warned about at
  first; the warnings fade as more people download builds signed by the same publisher. No certificate
  (not even EV) skips this any more.
- Updates installed by CleanSweep itself never show SmartScreen, because they aren't downloaded by a browser.

## Setting up Azure Artifact Signing (about US$9.99 a month)

Available to individual developers in the USA and Canada. The publisher name on the certificate is your
legal name, and your city, province and country are shown in it too.

1. **Azure account:** sign up at portal.azure.com with a **pay-as-you-go** subscription. The billing account type
   must be **Individual**, with your legal name and address exactly as on your photo ID (check it under
   Cost Management + Billing > Properties before step 3; the name and city go on the certificate).
2. **Run the setup script** in Azure Cloud Shell (the `>_` button at the top of the portal, choose Bash):

   ```
   curl -sL https://raw.githubusercontent.com/maurizzioem-code/CleanSweep/main/docs/azure-signing-setup.sh | bash
   ```

   It registers the `Microsoft.CodeSigning` resource provider, creates the resource group `cleansweep-signing` and the
   Basic Artifact Signing account `cleansweep` in East US (endpoint `https://eus.codesigning.azure.net/`), gives you the
   **Artifact Signing Identity Verifier** role, and creates the app `CleanSweep GitHub signing` with a federated
   credential for this repository's `main` branch and the **Artifact Signing Certificate Profile Signer** role.
   It prints six lines (IDs, not passwords) for the GitHub settings in step 5.
   If the name `cleansweep` is taken, edit `ACCOUNT=` at the top of the script and run it again.
3. **Identity validation** (portal only): the `cleansweep` account > Identity validations > choose **Individual** >
   New identity > **Public** > check the details > Create. When the status is **Action Required**, click your name and
   the verification link, sign in with the same email, and do the ID check on your phone (AU10TIX, then add the
   Verified ID to the Microsoft Authenticator app and share it). Microsoft says public identity validation can take
   1-20 business days.
4. **Certificate profile:** when the validation says **Completed**: Certificate profiles > Create > **Public Trust**,
   name `CleanSweepPublic`, Program Type None, Verified CN and O = your validation. Leave street address and postal code off.
5. **GitHub settings** (repository > Settings > Secrets and variables > Actions):
   - Secrets: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`.
   - Variables: `AZURE_SIGNING_ENDPOINT`, `AZURE_SIGNING_ACCOUNT`, `AZURE_SIGNING_PROFILE`.
   Set these last: as soon as `AZURE_SIGNING_ACCOUNT` exists, every build must be validly signed or it fails.

The next build signs both test builds, checks the signatures and runs every test on the signed program.
No password is stored: GitHub signs in to Azure for each build with a short-lived token (OpenID Connect).

## Free alternative: SignPath Foundation

SignPath Foundation signs qualifying open-source projects for free. Requirements include an OSI-approved open-source
license for the whole repository, a code signing policy page, and a manual approval of every release. The
publisher shown by Windows is "SignPath Foundation", not your name.
