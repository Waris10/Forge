-- Forge initial schema. Milestone 1.
--
-- Design notes:
--   * id is UUID, not BIGSERIAL. The API generates the id, inserts the row, and
--     returns the id in one round trip. Predictable ids also make idempotency
--     implementable without an extra lookup roundtrip.
--   * payload is JSONB, not TEXT. JSONB is binary, indexable, and can be queried
--     with operators like @>. We won't query into it in Milestone 1, but leaving
--     it as JSONB costs nothing and opens options later.
--   * priority is SMALLINT with 1 = highest. Gives us 32k priorities — overkill,
--     but SMALLINT is smaller than INT and aligns with how most queues think.
--   * status is TEXT with a CHECK constraint, not an ENUM. Postgres ENUMs are
--     rigid to modify; TEXT + CHECK is easier to evolve.

CREATE TABLE jobs (
    id              UUID        PRIMARY KEY,
    job_type        TEXT        NOT NULL,
    payload         JSONB       NOT NULL,
    queue           TEXT        NOT NULL DEFAULT 'default',
    priority        SMALLINT    NOT NULL DEFAULT 5,
    status          TEXT        NOT NULL
                    CHECK (status IN ('queued','running','succeeded','failed','dead')),
    attempts        INT         NOT NULL DEFAULT 0,
    max_attempts    INT         NOT NULL DEFAULT 5,
    last_error      TEXT        NULL,
    idempotency_key TEXT        NULL,
    scheduled_for   TIMESTAMPTZ NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    started_at      TIMESTAMPTZ NULL,
    completed_at    TIMESTAMPTZ NULL,
    duration_ms     INT         NULL
);

-- Index rationale:
--
-- (status, created_at DESC): the dashboard's core query is "show me recent jobs
-- by status." This is the workhorse index.
CREATE INDEX idx_jobs_status_created ON jobs (status, created_at DESC);

-- (job_type, status): "all failed SendEmail jobs" type queries.
CREATE INDEX idx_jobs_type_status ON jobs (job_type, status);

-- Partial unique index on idempotency_key.
-- Partial = only rows where idempotency_key IS NOT NULL.
-- Null values don't compete for uniqueness (hundreds of nulls are fine);
-- non-null values are globally unique. This is the idiomatic Postgres way
-- to express "unique if present."
CREATE UNIQUE INDEX idx_jobs_idempotency
    ON jobs (idempotency_key)
    WHERE idempotency_key IS NOT NULL;