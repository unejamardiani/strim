output "server_ipv4" {
  description = "Public IPv4 address for DNS A record."
  value       = hcloud_server.strim.ipv4_address
}

output "server_ipv6" {
  description = "Public IPv6 address for DNS AAAA record."
  value       = hcloud_server.strim.ipv6_address
}

output "app_url" {
  description = "Public Strim URL."
  value       = "https://${var.domain_name}"
}

output "ssh_command" {
  description = "SSH command for server access."
  value       = "ssh root@${hcloud_server.strim.ipv4_address}"
}

output "sqlite_path" {
  description = "SQLite DB location on the Hetzner host."
  value       = "/opt/strim/data/strim.db"
}
