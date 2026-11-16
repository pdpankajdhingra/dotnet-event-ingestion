output "resource_group_name" {
  value = azurerm_resource_group.main.name
}

output "cluster_name" {
  value = azurerm_kubernetes_cluster.main.name
}

output "registry_name" {
  value = azurerm_container_registry.main.name
}

output "registry_login_server" {
  value = azurerm_container_registry.main.login_server
}

output "service_bus_namespace" {
  value = "${azurerm_servicebus_namespace.main.name}.servicebus.windows.net"
}

output "service_bus_topic" {
  value = azurerm_servicebus_topic.events.name
}

output "key_vault_name" {
  value = azurerm_key_vault.main.name
}

output "workload_identity_client_id" {
  value = azurerm_user_assigned_identity.app.client_id
}

output "tenant_id" {
  value = data.azurerm_client_config.current.tenant_id
}

output "kubernetes_namespace" {
  value = var.kubernetes_namespace
}

output "service_account_name" {
  value = var.service_account_name
}
