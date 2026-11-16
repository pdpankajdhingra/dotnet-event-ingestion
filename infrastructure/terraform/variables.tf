variable "subscription_id" {
  type        = string
  description = "Azure subscription ID for the environment."
}

variable "deployment_principal_id" {
  type        = string
  description = "Object ID of the separate Azure DevOps application deployment service principal."
}

variable "location" {
  type        = string
  description = "Azure region. Verify availability and cost before deployment."
}

variable "monthly_budget_amount" {
  type        = number
  description = "Monthly alert threshold in the subscription billing currency."
  validation {
    condition     = var.monthly_budget_amount > 0
    error_message = "The monthly budget must be positive."
  }
}

variable "budget_start_date" {
  type        = string
  description = "First day of the budget's start month, e.g. 2026-11-01T00:00:00Z; keep stable between runs."
}

variable "budget_alert_email" {
  type        = string
  description = "Email address for subscription budget notifications."
}

variable "name_prefix" {
  type        = string
  description = "Globally unique lowercase alphanumeric prefix (3-12 characters)."
  validation {
    condition     = can(regex("^[a-z][a-z0-9]{2,11}$", var.name_prefix))
    error_message = "Use 3-12 lowercase letters or digits, starting with a letter."
  }
}

variable "node_vm_size" {
  type        = string
  default     = "Standard_B2s"
  description = "A small AKS-supported VM; availability and quota vary by region."
}

variable "kubernetes_namespace" {
  type        = string
  default     = "event-ingestion"
  description = "Namespace of the Kubernetes service account."
}

variable "service_account_name" {
  type        = string
  default     = "event-ingestion"
  description = "Service account federated with the application identity."
}

variable "tags" {
  type        = map(string)
  default     = { purpose = "event-ingestion-test" }
  description = "Tags applied to Azure resources."
}
