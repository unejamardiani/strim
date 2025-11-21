# Azure App Service Terraform deploy

This Terraform stack deploys the GHCR `strim` image to an Azure Linux App Service (custom container) and mounts an Azure Files share so the SQLite database persists across restarts.

## What it creates
- Resource group, App Service plan (Linux), and Linux Web App configured to run the container on port `8080`
- Storage account + Azure Files share mounted into the container at `/var/strim/data`
- App settings for SQLite (`DB_PROVIDER=sqlite`, `SQLITE_PATH=/var/strim/data/strim.db`) and `WEBSITES_PORT` for the container

## Prereqs
- Terraform >= 1.5
- Azure credentials exported (e.g. `az login` plus `ARM_SUBSCRIPTION_ID`, `ARM_TENANT_ID`, `ARM_CLIENT_ID`, `ARM_CLIENT_SECRET` if using a service principal)
- The GHCR image pushed by `.github/workflows/publish-image.yml` (default tag is `main` or the Git SHA). Set the registry username/password when the image is private.

## Usage
1) From `infra/terraform`, create a `terraform.tfvars` with your image reference:
```hcl
container_image      = "ghcr.io/<owner>/strim"
container_image_tag  = "main"          # or a release tag like v1.2.3
docker_registry_username = "<github-username>"      # only needed for private images
docker_registry_password = "<ghcr-read-pat>"        # scopes: read:packages

# Optional overrides
location            = "eastus"
project_name        = "strim"
app_service_plan_sku = "B1"
sqlite_mount_path   = "/var/strim/data"
sqlite_filename     = "strim.db"
file_share_quota_gb = 10
```

2) Initialize and apply:
```bash
terraform init
terraform apply
```

3) The output `app_url` points to the deployed site. The Azure Files share is exposed in the `storage_share` output and is mounted to the container path set in `SQLITE_PATH`.

### Notes
- The container listens on `8080`; `WEBSITES_PORT` is set accordingly.
- Azure Files is mounted read/write at `/var/strim/data` by default. Change `sqlite_mount_path`/`sqlite_filename` if you want a different location; `SQLITE_PATH` will update automatically.
- If you ever switch to Postgres, set `DB_PROVIDER=postgres` and `POSTGRES_CONNECTION` in `app_settings` (edit `local.app_settings` in `main.tf`).
