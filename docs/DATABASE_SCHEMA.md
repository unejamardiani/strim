# Database Schema Management

## Current Approach

This project currently uses `EnsureCreated()` with manual schema patching instead of EF Core migrations.

### How It Works

1. **Initial Schema**: `db.Database.EnsureCreated()` creates the database and tables if they don't exist
2. **Schema Patching**: Custom code adds columns and indexes that may be missing from older databases
3. **Provider-Specific Logic**: Separate patching logic for PostgreSQL and SQLite

### Files Involved

- `api/Configuration/Database.cs`: `EnsureStrimSchema()`, `TryPatchPostgresSchema()`, `TryAddSqliteColumn()`, etc.
- `api/Data/AppDbContext.cs`: Entity configuration and model definition

### Film and series marks (`titlemarks`)

The one extra table the owner approved (18 Nov 2026) holds a user's marks on films and series: `id`, `userid`,
`kind` (`film` or `series`), `titlekey`, `year` (nullable), `displaytitle`, `seen`, `rating` (-1, 0 or 1),
`filmdbid` (nullable, unused for now), `createdutc`, `updatedutc`. `userid` references `AspNetUsers (Id)` with
`ON DELETE CASCADE`, so the marks go with the account. `IX_titlemarks_userid` indexes the user;
`IX_titlemarks_userid_kind_titlekey_year` is unique on `(userid, kind, titlekey, COALESCE(year, 0))`, so a null
year counts as its own value. EF cannot model that expression index: `Database.EnsureTitleMarksSchema` creates the
table and both indexes with `IF NOT EXISTS` on every start, for SQLite and PostgreSQL.

## Limitations of Current Approach

### 1. No Migration History
- Cannot track what schema changes have been applied
- No rollback capability
- Difficult to sync schema across environments

### 2. Manual Column Management
- Each new column requires manual SQL for both PostgreSQL and SQLite
- Easy to miss a column or have inconsistent types across providers
- Index creation must be handled separately

### 3. EnsureCreated() Constraints
- Does not update existing tables when model changes
- Only creates tables from scratch on empty databases
- Incompatible with EF Core migrations (cannot use both)

### 4. Production Concerns
- No atomic migrations with rollback
- Schema changes require careful manual coordination
- No built-in tools for diff/compare

## Recommended: EF Core Migrations

For production deployments, consider switching to EF Core migrations:

### Benefits
- **Version Control**: Each migration is a code file in source control
- **Rollback**: Can revert to previous schema versions
- **Tooling**: `dotnet ef` CLI for generating and applying migrations
- **Multi-Provider**: Migrations work across database providers
- **Atomic**: Changes are applied transactionally

### Migration Steps

1. **Remove EnsureCreated()**: Delete or comment out `db.Database.EnsureCreated()` and manual patching
2. **Generate Initial Migration**:
   ```bash
   cd api
   dotnet ef migrations add InitialCreate
   ```
3. **Apply Migrations**:
   ```bash
   dotnet ef database update
   ```
4. **Future Changes**: Generate new migrations when model changes:
   ```bash
   dotnet ef migrations add AddNewFeature
   ```

### Startup Code Change

Replace:
```csharp
void EnsureSchema(AppDbContext db, ILogger logger)
{
  db.Database.EnsureCreated();
  // ... manual patching
}
```

With:
```csharp
void EnsureSchema(AppDbContext db, ILogger logger)
{
  db.Database.Migrate();  // Applies pending migrations
}
```

## When to Keep Current Approach

The current approach is acceptable for:
- Development/prototyping
- Simple deployments with infrequent schema changes
- Projects where migration infrastructure is overkill

## Index Configuration

Indexes are defined in `AppDbContext.OnModelCreating()`:

```csharp
entity.HasIndex(p => new { p.OwnerId, p.UpdatedAt });
entity.HasIndex(p => p.ShareCode).HasFilter("\"ShareCode\" IS NOT NULL");
```

With migrations, these would be automatically included. With the current approach, indexes may need manual `CREATE INDEX IF NOT EXISTS` statements in the patching code.
