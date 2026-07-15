# Hetzner Docker Deployment

This deploys Strim to one Hetzner Cloud VM running Docker:

- `strim` container on the internal Docker network
- `caddy` container on ports `80` and `443`
- SQLite persisted on the host at `/opt/strim/data/strim.db`
- HTTPS via Caddy and Let's Encrypt

Use an x86 server type such as `cx22`; the published Strim image is currently `linux/amd64`.

## Prerequisites

1. Hetzner Cloud API token.
2. SSH public key.
3. A DNS hostname for Strim, for example `strim.example.com`.
4. Terraform 1.5+.

## Deploy

```bash
cd infra/hetzner
cp terraform.tfvars.example terraform.tfvars
```

Edit `terraform.tfvars`, then run:

```bash
terraform init
terraform apply
```

After Terraform prints `server_ipv4`, point your DNS `A` record for `domain_name` to that IP.
If you use IPv6, point an `AAAA` record to `server_ipv6`.

Caddy will keep retrying certificate issuance while DNS propagates.

## Restore A SQLite Backup

The database path on the host is:

```bash
/opt/strim/data/strim.db
```

To replace it with a backup:

```bash
scp strim.db root@SERVER_IP:/opt/strim/data/strim.db
ssh root@SERVER_IP 'cd /opt/strim && docker compose restart strim'
```

## Operations

SSH into the server:

```bash
ssh root@SERVER_IP
```

View services:

```bash
cd /opt/strim
docker compose ps
docker compose logs -f strim
docker compose logs -f caddy
```

Upgrade image:

```bash
cd /opt/strim
docker compose pull
docker compose up -d
```

## Notes

- Login requires HTTPS in production. Do not run this publicly over plain HTTP.
- The default firewall allows SSH from anywhere. Restrict `ssh_allowed_ips` in `terraform.tfvars` before long-term use.
- If the IPTV provider blocks Hetzner IPs too, this deployment will still return provider-side `403`.
