output "app_url" {
  description = "HTTPS endpoint for the deployed app."
  value       = "https://${azurerm_linux_web_app.strim.default_host_name}"
}

output "resource_group" {
  description = "Resource group for the deployment."
  value       = azurerm_resource_group.strim.name
}

output "storage_share" {
  description = "Azure Files share used for SQLite persistence."
  value = {
    account_name = azurerm_storage_account.sqlite.name
    share_name   = azurerm_storage_share.sqlite.name
    mount_path   = local.sqlite_mount_path
  }
}
