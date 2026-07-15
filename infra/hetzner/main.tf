terraform {
  required_version = ">= 1.5.0"

  required_providers {
    hcloud = {
      source  = "hetznercloud/hcloud"
      version = "~> 1.49"
    }
  }
}

provider "hcloud" {
  token = var.hcloud_token
}

locals {
  app_origin = "https://${var.domain_name}"
  labels = merge(var.labels, {
    app     = "strim"
    managed = "terraform"
  })
}

resource "hcloud_ssh_key" "strim" {
  name       = "${var.server_name}-ssh-key"
  public_key = var.ssh_public_key
  labels     = local.labels
}

resource "hcloud_firewall" "strim" {
  name   = "${var.server_name}-firewall"
  labels = local.labels

  rule {
    direction  = "in"
    protocol   = "tcp"
    port       = "22"
    source_ips = var.ssh_allowed_ips
  }

  rule {
    direction  = "in"
    protocol   = "tcp"
    port       = "80"
    source_ips = ["0.0.0.0/0", "::/0"]
  }

  rule {
    direction  = "in"
    protocol   = "tcp"
    port       = "443"
    source_ips = ["0.0.0.0/0", "::/0"]
  }
}

resource "hcloud_server" "strim" {
  name        = var.server_name
  image       = var.server_image
  server_type = var.server_type
  location    = var.location
  ssh_keys    = [hcloud_ssh_key.strim.id]
  firewall_ids = [
    hcloud_firewall.strim.id
  ]
  labels = local.labels

  user_data = templatefile("${path.module}/templates/cloud-init.yml.tftpl", {
    admin_email     = var.admin_email
    allowed_origins = local.app_origin
    domain_name     = var.domain_name
    strim_image     = var.strim_image
  })
}
