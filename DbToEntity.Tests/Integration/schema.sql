-- Schema for the integration tests: one example of every shape db2entity handles.
-- Loaded once per run into a throwaway postgres:17 container (PostgresFixture).

CREATE EXTENSION citext;
CREATE EXTENSION hstore;
CREATE SCHEMA sales;

-- Enums, in two schemas; a label that isn't a valid identifier
CREATE TYPE public.mood AS ENUM ('happy', 'very sad', 'ok');
CREATE TYPE sales.order_status AS ENUM ('pending', 'shipped');

CREATE TABLE roles (
    id   serial PRIMARY KEY,
    name varchar(50) NOT NULL UNIQUE
);
COMMENT ON TABLE roles IS 'User roles';
COMMENT ON COLUMN roles.name IS 'Display name & <key>';

CREATE TABLE users (
    id         int GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email      citext NOT NULL,
    nickname   text,
    mood       mood NOT NULL DEFAULT 'happy',
    moods      mood[],
    tags       text[],
    created_at timestamp NOT NULL DEFAULT now(),
    seen_at    timestamptz,
    payload    jsonb,
    role_id    int NOT NULL REFERENCES roles (id) ON DELETE RESTRICT,
    manager_id int REFERENCES users (id) ON DELETE SET NULL
);
CREATE UNIQUE INDEX ux_users_email ON users (email);
CREATE INDEX ix_users_nick_created ON users (nickname, created_at);             -- column order matters
CREATE UNIQUE INDEX ux_users_partial ON users (manager_id) WHERE nickname IS NOT NULL; -- partial: not a 1:1 guarantee
CREATE UNIQUE INDEX ux_users_expr ON users (role_id, lower(nickname));          -- expression part

-- One-to-one: shared primary key, and unique FK column
CREATE TABLE user_profiles (
    user_id int PRIMARY KEY REFERENCES users (id) ON DELETE CASCADE,
    bio     text
);
CREATE TABLE user_settings (
    id      serial PRIMARY KEY,
    user_id int NOT NULL UNIQUE REFERENCES users (id) ON DELETE CASCADE,
    theme   text
);

-- FK to a unique non-PK column (alternate key), default NO ACTION
CREATE TABLE countries (
    id       serial PRIMARY KEY,
    iso_code char(2) NOT NULL UNIQUE
);
CREATE TABLE cities (
    id           serial PRIMARY KEY,
    country_code char(2) NOT NULL REFERENCES countries (iso_code),
    name         text NOT NULL
);

-- Other schema, enum from that schema, ON DELETE SET DEFAULT
CREATE TABLE sales.orders (
    id        bigserial PRIMARY KEY,
    user_id   int DEFAULT NULL REFERENCES public.users (id) ON DELETE SET DEFAULT,
    status    sales.order_status NOT NULL,
    total     numeric(10, 2) NOT NULL,
    placed_on date NOT NULL
);

-- Declarative partitioning: only the parent should be generated; its index must be read
CREATE TABLE events (
    id          bigint NOT NULL,
    happened_at timestamptz NOT NULL,
    kind        text NOT NULL,
    PRIMARY KEY (id, happened_at)
) PARTITION BY RANGE (happened_at);
CREATE TABLE events_2026 PARTITION OF events FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
CREATE INDEX ix_events_kind ON events (kind);

-- Classic inheritance: the child is a separate table and is kept
CREATE TABLE vehicles (id serial PRIMARY KEY, name text NOT NULL);
CREATE TABLE trucks (payload_tons int) INHERITS (vehicles);

CREATE VIEW active_users AS SELECT id, email FROM users WHERE nickname IS NOT NULL;
COMMENT ON VIEW active_users IS 'Users with a nickname';
CREATE MATERIALIZED VIEW role_counts AS SELECT role_id, count(*) AS n FROM users GROUP BY role_id;

-- Domain: resolves to its base type; NOT NULL and length come from the domain
CREATE DOMAIN email_address AS varchar(120) NOT NULL;
CREATE TABLE contacts (id serial PRIMARY KEY, email email_address, alt_email email_address NULL);

-- EF Core's own table: must never be generated
CREATE TABLE "__EFMigrationsHistory" ("MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL);

-- A table name that would break naive SQL string building
CREATE TABLE "it's" (id int PRIMARY KEY);

-- One column per mapped type (names c_<type>), read through EF in the type test
CREATE TABLE all_types (
    id          int PRIMARY KEY,
    c_int2      int2, c_int4 int4, c_int8 int8, c_float4 float4, c_float8 float8,
    c_numeric   numeric, c_money money, c_bool bool,
    c_text      text, c_varchar varchar(50), c_bpchar char(3), c_name name, c_citext citext,
    c_json      json, c_jsonb jsonb, c_xml xml,
    c_uuid      uuid, c_bytea bytea,
    c_date      date, c_time time, c_timetz timetz, c_timestamp timestamp, c_timestamptz timestamptz, c_interval interval,
    c_inet      inet, c_cidr cidr, c_macaddr macaddr,
    c_bit       bit(8), c_varbit varbit,
    c_tsvector  tsvector, c_tsquery tsquery, c_hstore hstore,
    c_point     point, c_line line, c_lseg lseg, c_box box, c_path path, c_polygon polygon, c_circle circle,
    c_int4range int4range, c_int8range int8range, c_numrange numrange, c_daterange daterange,
    c_tsrange   tsrange, c_tstzrange tstzrange,
    c_oid       oid, c_xid xid,
    c_int4_arr  int4[], c_text_arr text[], c_uuid_arr uuid[], c_timestamp_arr timestamp[], c_jsonb_arr jsonb[]
);
INSERT INTO all_types VALUES (
    1,
    1, 2, 3, 1.5, 2.5,
    12.34, 9.99, true,
    'text', 'varchar', 'abc', 'name', 'CiText',
    '{"a":1}', '{"b":2}', '<x>1</x>',
    'a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11', '\xdeadbeef',
    '2026-11-18', '10:30', '10:30+05:30', '2026-11-18 10:30:00', '2026-11-18 05:00:00+00', '1 day 2 hours',
    '10.0.0.1', '10.0.0.0/8', '08:00:2b:01:02:03',
    B'10101010', B'101',
    'a fat cat', 'fat & cat', 'k => v',
    '(1,2)', '{1,-1,0}', '[(0,0),(1,1)]', '((0,0),(1,1))', '[(0,0),(1,1),(2,0)]', '((0,0),(1,1),(1,0))', '<(0,0),1>',
    '[1,10)', '[1,10)', '[1.5,2.5)', '[2026-01-01,2026-12-31)',
    '[2026-01-01 00:00,2026-01-02 00:00)', '[2026-01-01 00:00+00,2026-01-02 00:00+00)',
    12345, '42',
    '{1,2,3}', '{a,b}', '{a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11}', '{"2026-11-18 10:30:00"}', '{"{\"c\":3}"}'
);
