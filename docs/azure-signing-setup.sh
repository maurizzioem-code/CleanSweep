#!/usr/bin/env bash
# CleanSweep - Azure Artifact Signing setup, part 1.
# Run it in Azure Cloud Shell (portal.azure.com > the >_ button at the top > Bash):
#   curl -sL https://raw.githubusercontent.com/maurizzioem-code/CleanSweep/main/docs/azure-signing-setup.sh | bash
# It only creates CleanSweep's own things (a resource group, a signing account, an app GitHub signs in as)
# and gives you and that app the two roles they need. Safe to run again: existing items are reused.
# Afterwards: do the identity validation and create the certificate profile in the portal (docs/code-signing.md).
set -euo pipefail

RG="cleansweep-signing"            # resource group
ACCOUNT="cleansweep"               # Artifact Signing account (must be unique in Azure - change it if taken)
REGION="eastus"
ENDPOINT="https://eus.codesigning.azure.net/"
PROFILE="CleanSweepPublic"         # the certificate profile name you'll create in the portal later
APP="CleanSweep GitHub signing"
REPO="maurizzioem-code/CleanSweep"

say() { printf '\n== %s\n' "$*"; }

SUB=$(az account show --query id -o tsv); TENANT=$(az account show --query tenantId -o tsv)
say "Subscription: $(az account show --query name -o tsv) ($SUB)"

say "Registering the Microsoft.CodeSigning resource provider (can take a minute)"
az provider register --namespace Microsoft.CodeSigning --wait

say "Adding the artifact-signing extension to the Azure CLI"
az extension add --name artifact-signing --upgrade --only-show-errors

say "Resource group $RG"
az group create -n "$RG" -l "$REGION" -o none

say "Artifact Signing account $ACCOUNT (Basic, about US\$9.99/month)"
if ! az artifact-signing show -n "$ACCOUNT" -g "$RG" -o none 2>/dev/null; then
  az artifact-signing create -n "$ACCOUNT" -l "$REGION" -g "$RG" --sku Basic -o none
fi
SCOPE=$(az artifact-signing show -n "$ACCOUNT" -g "$RG" --query id -o tsv)

say "Giving you the Artifact Signing Identity Verifier role (needed for the ID check)"
ME=$(az ad signed-in-user show --query id -o tsv)
az role assignment create --assignee-object-id "$ME" --assignee-principal-type User \
  --role "Artifact Signing Identity Verifier" --scope "$SCOPE" -o none 2>/dev/null || echo "(already assigned)"

say "App registration \"$APP\" that GitHub signs in as (no password - GitHub gets a short-lived token per build)"
CLIENT=$(az ad app list --display-name "$APP" --query "[0].appId" -o tsv)
[ -n "$CLIENT" ] || CLIENT=$(az ad app create --display-name "$APP" --query appId -o tsv)
az ad sp show --id "$CLIENT" -o none 2>/dev/null || az ad sp create --id "$CLIENT" -o none
SP=$(az ad sp show --id "$CLIENT" --query id -o tsv)
if [ -z "$(az ad app federated-credential list --id "$CLIENT" --query "[?name=='github-main'].name" -o tsv)" ]; then
  az ad app federated-credential create --id "$CLIENT" -o none --parameters "{
    \"name\": \"github-main\",
    \"issuer\": \"https://token.actions.githubusercontent.com\",
    \"subject\": \"repo:$REPO:ref:refs/heads/main\",
    \"audiences\": [\"api://AzureADTokenExchange\"],
    \"description\": \"CleanSweep builds on the main branch\"}"
fi

say "Giving the app the Artifact Signing Certificate Profile Signer role"
for i in 1 2 3 4 5; do   # a brand-new app can take a few seconds to appear
  az role assignment create --assignee-object-id "$SP" --assignee-principal-type ServicePrincipal \
    --role "Artifact Signing Certificate Profile Signer" --scope "$SCOPE" -o none 2>/dev/null && break
  [ "$(az role assignment list --assignee "$SP" --scope "$SCOPE" --query "length(@)" -o tsv)" != "0" ] && break
  sleep 10
done

cat <<EOF

== Done. Send these lines back (they are IDs, not passwords):

AZURE_TENANT_ID=$TENANT
AZURE_SUBSCRIPTION_ID=$SUB
AZURE_CLIENT_ID=$CLIENT
AZURE_SIGNING_ENDPOINT=$ENDPOINT
AZURE_SIGNING_ACCOUNT=$ACCOUNT
AZURE_SIGNING_PROFILE=$PROFILE

Next, in the portal: open the "$ACCOUNT" Artifact Signing account > Identity validations > Individual >
New identity > Public, and finish the ID check. When it says Completed, create the certificate profile:
Certificate profiles > Create > Public Trust, name: $PROFILE.
EOF
