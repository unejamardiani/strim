terraform {
  required_version = ">= 1.5.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.107"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

provider "azurerm" {
  features {}
}

resource "random_string" "storage_suffix" {
  length  = 6
  upper   = false
  special = false
}

locals {
  base_name            = length(var.project_name) > 0 ? lower(replace(var.project_name, "/[^a-z0-9]/", "")) : "strim"
  storage_account_name = substr("${local.base_name}${random_string.storage_suffix.result}", 0, 24)
  sqlite_mount_path    = var.sqlite_mount_path != "" ? var.sqlite_mount_path : "/var/strim/data"
  sqlite_path          = "${local.sqlite_mount_path}/${var.sqlite_filename}"
  image_name           = "${var.container_image}:${var.container_image_tag}"

  app_settings = merge({
    DB_PROVIDER                        = "sqlite"
    SQLITE_PATH                        = local.sqlite_path
    WEBSITES_ENABLE_APP_SERVICE_STORAGE = "true"
    WEBSITES_PORT                      = tostring(var.container_port)
  }, var.docker_registry_username != "" && var.docker_registry_password != "" ? {
    DOCKER_REGISTRY_SERVER_URL      = var.docker_registry_url
    DOCKER_REGISTRY_SERVER_USERNAME = var.docker_registry_username
    DOCKER_REGISTRY_SERVER_PASSWORD = var.docker_registry_password
  } : {})
}

resource "azurerm_resource_group" "strim" {
  name     = coalesce(var.resource_group_name, "${local.base_name}-rg")
  location = var.location
  tags     = var.tags
}

resource "azurerm_service_plan" "strim" {
  name                = coalesce(var.app_service_plan_name, "${local.base_name}-plan")
  location            = azurerm_resource_group.strim.location
  resource_group_name = azurerm_resource_group.strim.name
  os_type             = "Linux"
  sku_name            = var.app_service_plan_sku
  tags                = var.tags
}

resource "azurerm_storage_account" "sqlite" {
  name                     = local.storage_account_name
  resource_group_name      = azurerm_resource_group.strim.name
  location                 = azurerm_resource_group.strim.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  account_kind             = "StorageV2"
  min_tls_version          = "TLS1_2"
  allow_nested_items_to_be_public = false
  tags                     = var.tags
}

resource "azurerm_storage_share" "sqlite" {
  name                 = var.file_share_name
  storage_account_name = azurerm_storage_account.sqlite.name
  quota                = var.file_share_quota_gb
}

resource "azurerm_linux_web_app" "strim" {
  name                = coalesce(var.app_service_name, "${local.base_name}-app")
  resource_group_name = azurerm_resource_group.strim.name
  location            = azurerm_resource_group.strim.location
  service_plan_id     = azurerm_service_plan.strim.id
  https_only          = true
  tags                = var.tags

  identity {
    type = "SystemAssigned"
  }

  site_config {
    always_on = true
    ftps_state = "Disabled"

    application_stack {
      docker_image_name   = local.image_name
      docker_registry_url = var.docker_registry_url
    }
  }

  app_settings = local.app_settings

  storage_account {
    name         = "sqlitedata"
    type         = "AzureFiles"
    account_name = azurerm_storage_account.sqlite.name
    share_name   = azurerm_storage_share.sqlite.name
    access_key   = azurerm_storage_account.sqlite.primary_access_key
    mount_path   = local.sqlite_mount_path
  }
}
