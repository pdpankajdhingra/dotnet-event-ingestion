import os
import subprocess
import sys
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parent.parent / "scripts" / "RenderKubernetes.py"
VALUES = {
	"K8S_NAMESPACE": "event-ingestion",
	"SERVICE_ACCOUNT": "event-ingestion",
	"DNS_LABEL": "eventtest",
	"PUBLIC_HOST": "eventtest.eastus.cloudapp.azure.com",
	"SERVICE_BUS_NAMESPACE": "eventtest-bus.servicebus.windows.net",
	"SERVICE_BUS_TOPIC": "events",
	"IMAGE": "eventtestacr.azurecr.io/event-ingestion:123",
	"ACME_EMAIL": "test@example.com",
	"ENTRA_TENANT_ID": "11111111-1111-1111-1111-111111111111",
	"ENTRA_API_CLIENT_ID": "22222222-2222-2222-2222-222222222222",
	"WORKLOAD_IDENTITY_CLIENT_ID": "33333333-3333-3333-3333-333333333333",
}


class RenderKubernetesTests(unittest.TestCase):
	def run_renderer(self, **overrides):
		env = os.environ.copy()
		env.update(VALUES)
		env.update(overrides)
		return subprocess.run([sys.executable, str(SCRIPT)], env=env, text=True, capture_output=True)

	def test_renders_identity_image_and_public_host(self):
		result = self.run_renderer()
		self.assertEqual(0, result.returncode, result.stderr)
		self.assertIn("azure.workload.identity/client-id: " + VALUES["WORKLOAD_IDENTITY_CLIENT_ID"], result.stdout)
		self.assertIn("image: " + VALUES["IMAGE"], result.stdout)
		self.assertIn(VALUES["PUBLIC_HOST"] + " {", result.stdout)
		self.assertNotIn("${", result.stdout)

	def test_rejects_mismatched_host(self):
		result = self.run_renderer(PUBLIC_HOST="other.eastus.cloudapp.azure.com")
		self.assertNotEqual(0, result.returncode)
		self.assertIn("PUBLIC_HOST must start", result.stderr)

	def test_renders_optional_secret_provider(self):
		env = os.environ.copy()
		env.update(VALUES)
		env.update(KEY_VAULT_NAME="eventtest-kv", KEY_VAULT_SECRET_NAME="example-secret")
		result = subprocess.run([sys.executable, str(SCRIPT), "--secret-provider-class"], env=env, text=True, capture_output=True)
		self.assertEqual(0, result.returncode, result.stderr)
		self.assertIn("keyvaultName: eventtest-kv", result.stdout)
		self.assertIn("objectName: example-secret", result.stdout)
		self.assertNotIn("${", result.stdout)

	def test_rejects_invalid_values(self):
		for name, value in (("IMAGE", "invalid\nkind: Secret"), ("ENTRA_TENANT_ID", "not-a-guid"), ("DNS_LABEL", "")):
			with self.subTest(name=name):
				result = self.run_renderer(**{name: value})
				self.assertNotEqual(0, result.returncode)
				self.assertIn(name, result.stderr)


if __name__ == "__main__":
	unittest.main()
