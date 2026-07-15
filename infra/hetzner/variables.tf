variable "hcloud_token" {
  description = "Hetzner Cloud API token with read/write access."
  type        = string
  sensitive   = true
}

variable "server_name" {
  description = "Hetzner server name."
  type        = string
  default     = "strim-hetzner"
}

variable "server_type" {
  description = "Hetzner x86 server type. Use x86 because the published Strim image is linux/amd64."
  type        = string
  default     = "cx22"
}

variable "server_image" {
  description = "Hetzner base image."
  type        = string
  default     = "ubuntu-24.04"
}

variable "location" {
  description = "Hetzner location, for example fsn1, nbg1, or hel1."
  type        = string
  default     = "fsn1"
}

variable "ssh_public_key" {
  description = "SSH public key allowed to access the server."
  type        = string
}

variable "ssh_allowed_ips" {
  description = "CIDR ranges allowed to SSH to the server."
  type        = list(string)
  default     = ["0.0.0.0/0", "::/0"]
}

variable "domain_name" {
  description = "Public hostname for Strim. Point its A/AAAA records at the server IP so Caddy can issue TLS."
  type        = string

  validation {
    condition     = length(trimspace(var.domain_name)) > 0
    error_message = "domain_name is required because Strim auth cookies require HTTPS in production."
  }
}

variable "admin_email" {
  description = "Email address used by Caddy/Let's Encrypt for certificate notices."
  type        = string
}

variable "strim_image" {
  description = "Docker image to run."
  type        = string
  default     = "index.docker.io/unejamardiani/strim:sha-38d4246"
}

variable "labels" {
  description = "Additional Hetzner labels."
  type        = map(string)
  default     = {}
}
