import argparse
import os
import re
import sys
import uuid
from pathlib import Path
from string import Template

ROOT = Path(__file__).resolve().parent.parent
FILES = ("service-account.yaml", "configmap.yaml", "deployment.yaml", "service.yaml")
PATTERNS = {
	"K8S_NAMESPACE": r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?",
	"SERVICE_ACCOUNT": r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?",
	"DNS_LABEL": r"[a-z][a-z0-9-]{1,61}[a-z0-9]",
	"PUBLIC_HOST": r"[a-z0-9.-]+\.cloudapp\.azure\.com",
	"SERVICE_BUS_NAMESPACE": r"[a-z0-9-]+\.servicebus\.windows\.net",
	"SERVICE_BUS_TOPIC": r"[a-zA-Z0-9][a-zA-Z0-9._-]{0,49}",
	"IMAGE": r"[a-z0-9][a-z0-9._/-]*:[a-zA-Z0-9_][a-zA-Z0-9._-]{0,127}",
	"ACME_EMAIL": r"[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9.-]+",
}
GUIDS = ("ENTRA_TENANT_ID", "ENTRA_API_CLIENT_ID", "WORKLOAD_IDENTITY_CLIENT_ID")


def main():
	parser = argparse.ArgumentParser()
	parser.add_argument("--secret-provider-class", action="store_true")
	args = parser.parse_args()
	if args.secret_provider_class:
		patterns = {
			"K8S_NAMESPACE": PATTERNS["K8S_NAMESPACE"],
			"KEY_VAULT_NAME": r"[a-zA-Z0-9][a-zA-Z0-9-]{1,22}[a-zA-Z0-9]",
			"KEY_VAULT_SECRET_NAME": r"[a-zA-Z0-9-]{1,127}",
		}
		guids = ("ENTRA_TENANT_ID", "WORKLOAD_IDENTITY_CLIENT_ID")
		files = ("secret-provider-class.yaml",)
	else:
		patterns, guids, files = PATTERNS, GUIDS, FILES
	values = {}
	for key, pattern in patterns.items():
		value = os.environ.get(key, "")
		if not re.fullmatch(pattern, value):
			raise ValueError(f"{key} is missing or invalid")
		values[key] = value
	for key in guids:
		try:
			values[key] = str(uuid.UUID(os.environ.get(key, "")))
		except ValueError as error:
			raise ValueError(f"{key} must be a GUID") from error
	if not args.secret_provider_class and not values["PUBLIC_HOST"].startswith(values["DNS_LABEL"] + "."):
		raise ValueError("PUBLIC_HOST must start with the configured DNS_LABEL")
	for index, name in enumerate(files):
		if index:
			print("---")
		print(Template((ROOT / "k8s" / name).read_text(encoding="utf-8")).substitute(values))


if __name__ == "__main__":
	try:
		main()
	except (ValueError, KeyError) as error:
		print(f"Cannot render Kubernetes manifests: {error}", file=sys.stderr)
		sys.exit(1)
