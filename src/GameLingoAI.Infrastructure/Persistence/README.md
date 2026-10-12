# SQLite persistence

`DatabaseMigrations` defines trusted, ordered schema changes and compatibility checks.
`SqliteDatabase` provides bounded background execution, transactional migration, safe
failure classification and explicit read-only recovery copying. Secrets remain outside
SQLite. See root `DATABASE.md` for lifecycle, backup, cancellation and integration status.
