variable "project_name" {
  description = "Prefix for Azure resource names. Letters and numbers only."
  type        = string
  default     = "strim"
}

variable "location" {
  description = "Azure region for all resources."
  type        = string
  default     = "switzerlandnorth"
}

variable "resource_group_name" {
  description = "Optional name for the resource group. Defaults to <project_name>-rg."
  type        = string
  default     = null
}

variable "app_service_plan_name" {
  description = "Optional name for the App Service plan. Defaults to <project_name>-plan."
  type        = string
  default     = null
}

variable "app_service_plan_sku" {
  description = "App Service plan SKU (e.g. B1, P1v3)."
  type        = string
  default     = "B1"
}

variable "app_service_name" {
  description = "Optional name for the web app. Defaults to <project_name>-app."
  type        = string
  default     = null
}

variable "container_image" {
  description = "Container image repository (e.g. ghcr.io/<owner>/strim)."
  type        = string
  default     = ""

  validation {
    condition     = length(trim(var.container_image)) > 0
    error_message = "Provide the container_image, e.g. ghcr.io/<owner>/strim."
  }
}

variable "container_image_tag" {
  description = "Image tag to deploy."
  type        = string
  default     = "main"
}

variable "docker_registry_url" {
  description = "Registry URL for the container image."
  type        = string
  default     = "https://ghcr.io"
}

variable "docker_registry_username" {
  description = "Registry username. Required for private GHCR images."
  type        = string
  default     = ""
}

variable "docker_registry_password" {
  description = "Registry password/PAT. Required for private GHCR images."
  type        = string
  default     = ""
  sensitive   = true
}

variable "container_port" {
  description = "Port exposed by the container image."
  type        = number
  default     = 8080
}

variable "sqlite_mount_path" {
  description = "Path inside the container to mount Azure Files for SQLite persistence."
  type        = string
  default     = "/var/strim/data"
}

variable "sqlite_filename" {
  description = "SQLite database filename inside the mounted directory."
  type        = string
  default     = "strim.db"
}

variable "file_share_name" {
  description = "Azure Files share name for SQLite storage."
  type        = string
  default     = "strimdata"
}

variable "file_share_quota_gb" {
  description = "Azure Files share size quota in GB."
  type        = number
  default     = 10
}

variable "tags" {
  description = "Common tags applied to all resources."
  type        = map(string)
  default     = {}
}
