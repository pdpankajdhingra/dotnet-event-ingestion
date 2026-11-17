BEGIN;
CREATE TABLE IF NOT EXISTS event_ingestion_outbox (
	tenant_id text NOT NULL,
	message_id text NOT NULL,
	content jsonb NOT NULL,
	envelope jsonb NOT NULL,
	created_at timestamptz NOT NULL DEFAULT now(),
	published_at timestamptz,
	next_attempt_at timestamptz NOT NULL DEFAULT now(),
	attempts integer NOT NULL DEFAULT 0,
	PRIMARY KEY (tenant_id, message_id)
);
CREATE INDEX IF NOT EXISTS ix_event_ingestion_outbox_pending
	ON event_ingestion_outbox (next_attempt_at, created_at)
	WHERE published_at IS NULL;
COMMIT;
